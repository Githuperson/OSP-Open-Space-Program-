using System.Collections.Generic;
using System.Linq;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    public sealed class StageStats
    {
        public int Stage { get; init; }
        public double StartMass { get; set; }
        public double EndMass { get; set; }
        public double DeltaVVacuum { get; set; }
        public double DeltaVSeaLevel { get; set; }
        public double BurnTime { get; set; }
        public double ThrustVacuum { get; set; }
        public double ThrustSeaLevel { get; set; }
        /// <summary>Thrust-to-weight ratio at sea level on the reference body at stage start.</summary>
        public double TwrSeaLevel { get; set; }
        public double TwrVacuum { get; set; }
        public int ActiveEngines { get; set; }
    }

    /// <summary>
    /// Estimates per-stage delta-v by simulating the staging sequence with the same resource network and engine
    /// model used in flight (so crossfeed, fuel lines and solid boosters are handled consistently).
    /// Each burn step uses the exact rocket equation Δv = v_e·ln(m0/m1), so the result is independent of step size.
    /// A stage burns until the engines that the next stage would jettison are dry (or all engines are dry if the
    /// next stage drops none).
    /// </summary>
    public static class DeltaVCalculator
    {
        public const double Step = 0.25;
        public const int MaxStepsPerStage = 400000;

        public static List<StageStats> Compute(VesselDesign design, double referenceGravity) =>
            Compute(design.Parts, 0, referenceGravity, null);

        /// <summary>
        /// Delta-v for the remaining stages of a vessel in flight: stages before <paramref name="nextStage"/> have
        /// already fired and <paramref name="activeEngines"/> are burning. The first entry is the current stage
        /// (index nextStage − 1) when engines are already active.
        /// </summary>
        public static List<StageStats> Compute(IReadOnlyList<PartInstance> sourceParts, int nextStage, double referenceGravity, ISet<int> activeEngines)
        {
            var result = new List<StageStats>();
            if (sourceParts.Count == 0) return result;

            var parts = new Dictionary<int, PartInstance>();
            foreach (PartInstance src in sourceParts)
            {
                PartInstance c = src.Clone();
                c.Def = src.Def;
                parts[c.Id] = c;
            }
            var current = new HashSet<int>(parts.Keys);
            var active = new HashSet<int>(activeEngines ?? (IEnumerable<int>)System.Array.Empty<int>());
            int stageCount = parts.Values.Where(p => p.Stage >= 0).Select(p => p.Stage + 1).DefaultIfEmpty(0).Max();

            int first = active.Count > 0 && nextStage > 0 ? nextStage - 1 : nextStage;
            for (int s = first; s < stageCount; s++)
            {
                bool activate = s >= nextStage;
                if (activate)
                {
                    foreach (PartInstance p in parts.Values.Where(p => p.Stage == s && current.Contains(p.Id)).ToList())
                    {
                        if (p.Def.HasModule<EngineModuleDef>()) active.Add(p.Id);
                        if (p.Def.HasModule<DecouplerModuleDef>() && p.Parent >= 0 && current.Contains(p.Parent))
                            foreach (int id in SubtreeIds(parts, p.Id)) { current.Remove(id); active.Remove(id); }
                        if (p.Def.HasModule<JettisonModuleDef>())
                        {
                            p.State ??= new PartFlightState();
                            p.State.Jettisoned = true;
                        }
                    }
                }

                // Engines the next stage would drop.
                var dropNext = new HashSet<int>();
                foreach (PartInstance p in parts.Values.Where(p => p.Stage == s + 1 && current.Contains(p.Id) && p.Def.HasModule<DecouplerModuleDef>() && p.Parent >= 0))
                    foreach (int id in SubtreeIds(parts, p.Id)) dropNext.Add(id);

                var stagePartList = current.Select(id => parts[id]).ToList();
                var network = new ResourceNetwork(stagePartList);
                var engines = active.Where(current.Contains).Select(id => (part: parts[id], def: parts[id].Def.GetModule<EngineModuleDef>())).ToList();
                var watched = engines.Where(e => dropNext.Contains(e.part.Id)).ToList();
                if (watched.Count == 0) watched = engines;

                double mass = stagePartList.Sum(p => p.TotalMass);
                var stats = new StageStats { Stage = s, StartMass = mass, ActiveEngines = engines.Count(e => Propulsion.HasPropellant(network, e.part, e.def)) };
                foreach (var (part, def) in engines)
                {
                    if (!Propulsion.HasPropellant(network, part, def)) continue;
                    stats.ThrustVacuum += def.ThrustAt(0) * part.ThrustLimit;
                    stats.ThrustSeaLevel += def.ThrustAt(1) * part.ThrustLimit;
                }
                if (referenceGravity > 0 && mass > 0)
                {
                    stats.TwrSeaLevel = stats.ThrustSeaLevel / (mass * referenceGravity);
                    stats.TwrVacuum = stats.ThrustVacuum / (mass * referenceGravity);
                }

                for (int step = 0; step < MaxStepsPerStage; step++)
                {
                    if (!watched.Any(e => Propulsion.HasPropellant(network, e.part, e.def))) break;
                    double before = mass;
                    double fVac = 0, fSl = 0, mdot = 0;
                    foreach (var (part, def) in engines)
                    {
                        double throttle = part.ThrustLimit;
                        double frac = Propulsion.ConsumePropellant(network, part, def, throttle, Step);
                        if (frac <= 0) continue;
                        double flow = def.MaxMassFlow * throttle * frac;
                        mdot += flow;
                        fVac += flow * def.IspAt(0) * PhysicsConstants.G0;
                        fSl += flow * def.IspAt(1) * PhysicsConstants.G0;
                    }
                    if (mdot <= 0) break;
                    mass = stagePartList.Sum(p => p.TotalMass);
                    if (mass >= before) break;
                    double ln = System.Math.Log(before / mass);
                    stats.DeltaVVacuum += fVac / mdot * ln;
                    stats.DeltaVSeaLevel += fSl / mdot * ln;
                    stats.BurnTime += Step;
                }
                stats.EndMass = mass;
                result.Add(stats);
            }
            return result;
        }

        private static IEnumerable<int> SubtreeIds(Dictionary<int, PartInstance> parts, int root)
        {
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                yield return id;
                foreach (PartInstance c in parts.Values)
                    if (c.Parent == id) stack.Push(c.Id);
            }
        }

        public static double TotalVacuum(IEnumerable<StageStats> stats) => stats.Sum(s => s.DeltaVVacuum);
        public static double TotalSeaLevel(IEnumerable<StageStats> stats) => stats.Sum(s => s.DeltaVSeaLevel);
    }
}
