using System.Collections.Generic;
using System.Linq;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    /// <summary>
    /// Resource flow rules for a connected set of parts (see docs/PHYSICS.md, "Resources"):
    ///  * StackCrossfeed resources (fuel, oxidiser) flow freely between connected parts, except across the joint
    ///    between a decoupler and its parent. Parts separated this way form different "crossfeed groups".
    ///  * A fuel line (surface-mounted on a source part and pointing at a target part) lets consumers in the
    ///    target's group draw from the source's group first. Chains of fuel lines drain outermost groups first
    ///    (asparagus staging).
    ///  * AllVessel resources (electric charge, monopropellant) are shared by the whole vessel.
    ///  * NoFlow resources (solid propellant) never leave their part.
    /// Within a set of tanks, draws are proportional to each tank's current contents.
    /// Rebuild the network whenever the part tree changes (staging, docking, breakage).
    /// </summary>
    public sealed class ResourceNetwork
    {
        private readonly List<PartInstance> _parts;
        private readonly Dictionary<int, int> _indexById = new();
        private readonly int[] _group;
        private readonly List<int>[] _groupMembers;
        /// <summary>For each group, the source groups that feed it via fuel lines.</summary>
        private readonly List<int>[] _feeders;
        private readonly Dictionary<int, int[]> _drawOrderCache = new();

        public int GroupCount => _groupMembers.Length;

        public ResourceNetwork(IEnumerable<PartInstance> parts)
        {
            _parts = parts.ToList();
            for (int i = 0; i < _parts.Count; i++) _indexById[_parts[i].Id] = i;

            // Union-find over tree edges that permit crossfeed.
            int n = _parts.Count;
            int[] uf = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            for (int i = 0; i < n; i++)
            {
                PartInstance p = _parts[i];
                if (p.Parent < 0 || !_indexById.TryGetValue(p.Parent, out int pi)) continue;
                bool blocks = p.Def != null && p.Def.HasModule<DecouplerModuleDef>();
                if (!blocks) uf[Find(i)] = Find(pi);
            }
            var groupIds = new Dictionary<int, int>();
            _group = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groupIds.TryGetValue(r, out int g)) groupIds[r] = g = groupIds.Count;
                _group[i] = g;
            }
            _groupMembers = new List<int>[groupIds.Count];
            _feeders = new List<int>[groupIds.Count];
            for (int g = 0; g < _groupMembers.Length; g++)
            {
                _groupMembers[g] = new List<int>();
                _feeders[g] = new List<int>();
            }
            for (int i = 0; i < n; i++) _groupMembers[_group[i]].Add(i);

            // Fuel lines: source = the line's group (it sits on the source tank), target = its target part's group.
            for (int i = 0; i < n; i++)
            {
                PartInstance p = _parts[i];
                if (p.Def == null || !p.Def.HasModule<FuelLineModuleDef>()) continue;
                if (p.FuelLineTarget < 0 || !_indexById.TryGetValue(p.FuelLineTarget, out int ti)) continue;
                int src = _group[i], dst = _group[ti];
                if (src != dst && !_feeders[dst].Contains(src)) _feeders[dst].Add(src);
            }
        }

        public int GroupOf(PartInstance part) => _indexById.TryGetValue(part.Id, out int i) ? _group[i] : -1;

        /// <summary>Groups to drain for a consumer in group g, in priority order (outermost feeders first, own group last).</summary>
        private int[] DrawOrder(int g)
        {
            if (_drawOrderCache.TryGetValue(g, out int[] cached)) return cached;
            var order = new List<int>();
            var visiting = new HashSet<int>();
            void Visit(int x)
            {
                if (!visiting.Add(x)) return;
                foreach (int f in _feeders[x]) Visit(f);
                if (!order.Contains(x)) order.Add(x);
            }
            Visit(g);
            int[] arr = order.ToArray();
            _drawOrderCache[g] = arr;
            return arr;
        }

        private IEnumerable<IEnumerable<int>> TankSetsFor(int consumerIndex, ResourceDefinition res)
        {
            switch (res.Flow)
            {
                case ResourceFlowMode.NoFlow:
                    yield return new[] { consumerIndex };
                    break;
                case ResourceFlowMode.AllVessel:
                    yield return Enumerable.Range(0, _parts.Count);
                    break;
                default:
                    foreach (int g in DrawOrder(_group[consumerIndex])) yield return _groupMembers[g];
                    break;
            }
        }

        /// <summary>Amount of a resource the consumer part can access.</summary>
        public double Available(PartInstance consumer, string resource)
        {
            ResourceDefinition res = ResourceLibrary.Get(resource);
            if (res == null || !_indexById.TryGetValue(consumer.Id, out int ci)) return 0;
            double sum = 0;
            foreach (IEnumerable<int> set in TankSetsFor(ci, res))
                foreach (int i in set) sum += _parts[i].GetResource(resource);
            return sum;
        }

        /// <summary>Removes up to <paramref name="amount"/> of a resource for a consumer. Returns the amount drawn.</summary>
        public double Draw(PartInstance consumer, string resource, double amount)
        {
            if (amount <= 0) return 0;
            ResourceDefinition res = ResourceLibrary.Get(resource);
            if (res == null || !_indexById.TryGetValue(consumer.Id, out int ci)) return 0;
            double remaining = amount;
            foreach (IEnumerable<int> setEnum in TankSetsFor(ci, res))
            {
                if (remaining <= 0) break;
                remaining -= DrawFromSet(setEnum, resource, remaining);
            }
            return amount - remaining;
        }

        private double DrawFromSet(IEnumerable<int> set, string resource, double amount)
        {
            double total = 0;
            foreach (int i in set) total += _parts[i].GetResource(resource);
            if (total <= 1e-12) return 0;
            double take = System.Math.Min(amount, total);
            double fraction = take / total;
            foreach (int i in set)
            {
                PartInstance p = _parts[i];
                double have = p.GetResource(resource);
                if (have <= 0) continue;
                p.SetResource(resource, System.Math.Max(0, have - have * fraction));
            }
            return take;
        }

        /// <summary>Adds a resource (e.g. generated electricity) to containers with free capacity. Returns the amount stored.</summary>
        public double Add(string resource, double amount)
        {
            if (amount <= 0) return 0;
            double free = 0;
            foreach (PartInstance p in _parts) free += System.Math.Max(0, p.GetResourceMax(resource) - p.GetResource(resource));
            if (free <= 1e-12) return 0;
            double put = System.Math.Min(amount, free);
            foreach (PartInstance p in _parts)
            {
                double space = System.Math.Max(0, p.GetResourceMax(resource) - p.GetResource(resource));
                if (space > 0) p.SetResource(resource, p.GetResource(resource) + put * space / free);
            }
            return put;
        }

        public double Total(string resource) => _parts.Sum(p => p.GetResource(resource));
        public double Capacity(string resource) => _parts.Sum(p => p.GetResourceMax(resource));

        /// <summary>
        /// Transfers fuel between two parts in the same vessel (manual fuel transfer). Only StackCrossfeed resources
        /// may be moved, and only between parts in the same crossfeed group. Returns the amount moved.
        /// </summary>
        public double Transfer(PartInstance from, PartInstance to, string resource, double amount)
        {
            ResourceDefinition res = ResourceLibrary.Get(resource);
            if (res == null || res.Flow == ResourceFlowMode.NoFlow) return 0;
            if (res.Flow == ResourceFlowMode.StackCrossfeed && GroupOf(from) != GroupOf(to)) return 0;
            double space = to.GetResourceMax(resource) - to.GetResource(resource);
            double moved = System.Math.Min(System.Math.Min(amount, from.GetResource(resource)), System.Math.Max(0, space));
            if (moved <= 0) return 0;
            from.SetResource(resource, from.GetResource(resource) - moved);
            to.SetResource(resource, to.GetResource(resource) + moved);
            return moved;
        }
    }
}
