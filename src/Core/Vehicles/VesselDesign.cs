using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OSP.Core.Math;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    /// <summary>
    /// One part placed in a vessel design. Transform is in the vessel frame (root part at the origin, +Y is the
    /// vessel's "up"/nose direction).
    /// </summary>
    public sealed class PartInstance
    {
        public const string SurfaceNode = "surface";

        public int Id { get; set; }
        public string Part { get; set; }
        /// <summary>Parent part id, or -1 for the root part.</summary>
        public int Parent { get; set; } = -1;
        /// <summary>Attach node on the parent this part is connected to, or "surface".</summary>
        public string ParentNode { get; set; }
        /// <summary>Attach node on this part used for the connection, or "surface".</summary>
        public string SelfNode { get; set; }
        public Vector3d Position { get; set; }
        public QuaternionD Rotation { get; set; } = QuaternionD.Identity;
        /// <summary>Staging index (0 fires first), or -1 if the part is not staged.</summary>
        public int Stage { get; set; } = -1;
        /// <summary>Parts placed together in symmetry share a non-zero group id.</summary>
        public int SymmetryGroup { get; set; }
        /// <summary>Current resource amounts (by resource id). Missing entries mean "full".</summary>
        public Dictionary<string, double> Resources { get; set; } = new();
        /// <summary>For fuel lines: the part fuel flows into (-1 = none).</summary>
        public int FuelLineTarget { get; set; } = -1;
        /// <summary>Engine thrust limiter (0..1).</summary>
        public double ThrustLimit { get; set; } = 1.0;
        /// <summary>Runtime state of the part's modules during a flight. Null in a fresh design.</summary>
        public PartFlightState State { get; set; }

        [JsonIgnore] public PartDefinition Def { get; set; }

        public bool IsSurfaceAttached => ParentNode == SurfaceNode;

        public PartInstance Clone()
        {
            var c = (PartInstance)MemberwiseClone();
            c.Resources = new Dictionary<string, double>(Resources);
            c.State = State?.Clone();
            return c;
        }

        public double GetResource(string id)
        {
            if (Resources.TryGetValue(id, out double v)) return v;
            ResourceAmountDef def = Def?.Resources.FirstOrDefault(r => r.Resource == id);
            return def?.Amount ?? 0;
        }

        public double GetResourceMax(string id) => Def?.Resources.FirstOrDefault(r => r.Resource == id)?.Max ?? 0;

        public void SetResource(string id, double amount) => Resources[id] = amount;

        /// <summary>Fills every resource container to its default starting amount.</summary>
        public void InitialiseResources()
        {
            Resources.Clear();
            if (Def == null) return;
            foreach (ResourceAmountDef r in Def.Resources) Resources[r.Resource] = r.Amount;
        }

        /// <summary>Dry mass, accounting for jettisoned shells (kg).</summary>
        public double DryMass
        {
            get
            {
                if (Def == null) return 0;
                double m = Def.Mass;
                if (State != null && State.Jettisoned)
                {
                    JettisonModuleDef j = Def.GetModule<JettisonModuleDef>();
                    if (j != null) m -= j.JettisonMass;
                }
                return System.Math.Max(m, 1e-3);
            }
        }

        public double ResourceMass
        {
            get
            {
                if (Def == null) return 0;
                double m = 0;
                foreach (ResourceAmountDef r in Def.Resources)
                    m += GetResource(r.Resource) * (ResourceLibrary.Get(r.Resource)?.Density ?? 0);
                return m;
            }
        }

        public double TotalMass => DryMass + ResourceMass;

        /// <summary>Transforms a point from part-local to vessel coordinates.</summary>
        public Vector3d LocalToVessel(Vector3d local) => Position + Rotation.Rotate(local);
        public Vector3d DirectionToVessel(Vector3d local) => Rotation.Rotate(local);
        public Vector3d VesselToLocal(Vector3d vessel) => Rotation.InverseRotate(vessel - Position);

        public override string ToString() => $"#{Id} {Part}";
    }

    /// <summary>
    /// A vessel blueprint: a tree of parts. The first part is the root (usually a command pod). Saved as JSON;
    /// see <see cref="Persistence.BlueprintSerializer"/>.
    /// </summary>
    public sealed class VesselDesign
    {
        public string Name { get; set; } = "Untitled Rocket";
        public string Description { get; set; } = "";
        public List<PartInstance> Parts { get; set; } = new();
        public int NextId { get; set; } = 1;
        public int NextSymmetryGroup { get; set; } = 1;

        [JsonIgnore] public PartInstance Root => Parts.FirstOrDefault(p => p.Parent < 0);

        public PartInstance Get(int id) => Parts.FirstOrDefault(p => p.Id == id);

        public IEnumerable<PartInstance> Children(int id) => Parts.Where(p => p.Parent == id);

        /// <summary>The part and all its descendants (depth-first, part first).</summary>
        public List<PartInstance> Subtree(int id)
        {
            var result = new List<PartInstance>();
            var stack = new Stack<int>();
            stack.Push(id);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                PartInstance p = Get(cur);
                if (p == null) continue;
                result.Add(p);
                foreach (PartInstance c in Children(cur)) stack.Push(c.Id);
            }
            return result;
        }

        public bool IsDescendant(int id, int ancestorId)
        {
            for (PartInstance p = Get(id); p != null; p = Get(p.Parent))
                if (p.Id == ancestorId) return true;
            return false;
        }

        public PartInstance AddPart(PartDefinition def, int parentId, string parentNode, string selfNode, Vector3d position, QuaternionD rotation)
        {
            var p = new PartInstance
            {
                Id = NextId++,
                Part = def.Id,
                Def = def,
                Parent = parentId,
                ParentNode = parentNode,
                SelfNode = selfNode,
                Position = position,
                Rotation = rotation,
            };
            Parts.Add(p);
            return p;
        }

        public void RemoveSubtree(int id)
        {
            var ids = new HashSet<int>(Subtree(id).Select(p => p.Id));
            Parts.RemoveAll(p => ids.Contains(p.Id));
            foreach (PartInstance p in Parts)
                if (ids.Contains(p.FuelLineTarget)) p.FuelLineTarget = -1;
        }

        /// <summary>Re-links <see cref="PartInstance.Def"/> after deserialisation. Returns ids of parts whose definition is missing.</summary>
        public List<string> Resolve(PartCatalog catalog)
        {
            var missing = new List<string>();
            foreach (PartInstance p in Parts)
            {
                p.Def = catalog.Get(p.Part);
                if (p.Def == null) missing.Add(p.Part);
            }
            return missing;
        }

        public VesselDesign Clone()
        {
            return new VesselDesign
            {
                Name = Name,
                Description = Description,
                Parts = Parts.Select(p => p.Clone()).ToList(),
                NextId = NextId,
                NextSymmetryGroup = NextSymmetryGroup,
            };
        }

        /// <summary>Parts that are connected to the given part's node (children attached to it, or the parent if attached through it).</summary>
        public PartInstance PartAtNode(PartInstance part, string nodeId)
        {
            if (part.SelfNode == nodeId && part.Parent >= 0) return Get(part.Parent);
            return Parts.FirstOrDefault(c => c.Parent == part.Id && c.ParentNode == nodeId);
        }

        public bool IsNodeFree(PartInstance part, string nodeId) => PartAtNode(part, nodeId) == null;

        /// <summary>All parts that are adjacent to this part in the tree.</summary>
        public IEnumerable<PartInstance> Neighbours(PartInstance part)
        {
            if (part.Parent >= 0)
            {
                PartInstance parent = Get(part.Parent);
                if (parent != null) yield return parent;
            }
            foreach (PartInstance c in Children(part.Id)) yield return c;
        }

        public int StageCount => Parts.Count == 0 ? 0 : System.Math.Max(0, Parts.Max(p => p.Stage) + 1);

        public double TotalCost() => Parts.Sum(p => p.Def?.Cost ?? 0);

        /// <summary>Makes every subtree rooted at <paramref name="newRootId"/> the new root by reversing parent links along the path.</summary>
        public void Reroot(int newRootId)
        {
            PartInstance node = Get(newRootId);
            if (node == null || node.Parent < 0) return;
            var path = new List<PartInstance>();
            for (PartInstance p = node; p != null; p = Get(p.Parent)) path.Add(p);
            // path[0] = new root ... path[^1] = old root. Reverse edges.
            for (int i = path.Count - 1; i > 0; i--)
            {
                PartInstance parent = path[i];
                PartInstance child = path[i - 1];
                parent.Parent = child.Id;
                (parent.ParentNode, parent.SelfNode) = (child.SelfNode, child.ParentNode);
            }
            node.Parent = -1;
            node.ParentNode = null;
            node.SelfNode = null;
            // Keep the root first in the list for readability.
            Parts.Remove(node);
            Parts.Insert(0, node);
        }
    }
}
