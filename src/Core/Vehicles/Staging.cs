using System.Collections.Generic;
using System.Linq;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    /// <summary>
    /// Staging sequence helpers. Stage 0 is activated first. Each stageable part (engine, decoupler, parachute,
    /// fairing) belongs to at most one stage.
    /// </summary>
    public static class Staging
    {
        public static bool IsStageable(PartInstance p) => p.Def != null && p.Def.IsStageable;

        /// <summary>Parts grouped by stage index (index = list position). Empty stages are kept so indices stay meaningful.</summary>
        public static List<List<PartInstance>> Stages(IEnumerable<PartInstance> parts)
        {
            var list = parts.Where(p => p.Stage >= 0 && IsStageable(p)).ToList();
            int count = list.Count == 0 ? 0 : list.Max(p => p.Stage) + 1;
            var stages = new List<List<PartInstance>>();
            for (int i = 0; i < count; i++) stages.Add(new List<PartInstance>());
            foreach (PartInstance p in list) stages[p.Stage].Add(p);
            return stages;
        }

        /// <summary>Removes gaps so stage indices are contiguous from 0.</summary>
        public static void Compact(VesselDesign design)
        {
            var used = design.Parts.Where(p => p.Stage >= 0).Select(p => p.Stage).Distinct().OrderBy(s => s).ToList();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < used.Count; i++) map[used[i]] = i;
            foreach (PartInstance p in design.Parts)
            {
                if (!IsStageable(p)) p.Stage = -1;
                else if (p.Stage >= 0) p.Stage = map[p.Stage];
            }
        }

        private static void Depths(VesselDesign design, PartInstance part, out int stackDepth, out int radialDepth)
        {
            stackDepth = 0;
            radialDepth = 0;
            for (PartInstance p = part; p != null; p = design.Get(p.Parent))
            {
                DecouplerModuleDef d = p.Def?.GetModule<DecouplerModuleDef>();
                if (d == null || p.Parent < 0) continue;
                if (d.IsRadial || p.IsSurfaceAttached) radialDepth++;
                else stackDepth++;
            }
        }

        /// <summary>
        /// Assigns a sensible default staging order:
        ///   for each stack level L (number of stack decouplers between the part and the root), from the bottom up:
        ///     [stack decouplers separating level L+1] + [ignite engines of level L]
        ///     [radial decouplers of level L, outermost first, one stage each]
        ///     [fairings / jettisons of level L]
        ///   parachutes last.
        /// </summary>
        public static void AutoStage(VesselDesign design)
        {
            var info = design.Parts.Where(IsStageable).Select(p =>
            {
                Depths(design, p, out int s, out int r);
                return (p, s, r);
            }).ToList();
            foreach (PartInstance p in design.Parts) p.Stage = -1;
            if (info.Count == 0) return;

            int maxLevel = info.Max(x => x.s);
            var stages = new List<List<PartInstance>>();
            for (int level = maxLevel; level >= 0; level--)
            {
                var ignite = new List<PartInstance>();
                // Stack decouplers that drop level+1 sit at depth level+1 (they count themselves).
                ignite.AddRange(info.Where(x => x.p.Def.HasModule<DecouplerModuleDef>() && !IsRadialDecoupler(x.p) && x.s == level + 1).Select(x => x.p));
                ignite.AddRange(info.Where(x => x.p.Def.HasModule<EngineModuleDef>() && x.s == level).Select(x => x.p));
                stages.Add(ignite);

                var radials = info.Where(x => IsRadialDecoupler(x.p) && x.s == level).ToList();
                foreach (int r in radials.Select(x => x.r).Distinct().OrderByDescending(r => r))
                    stages.Add(radials.Where(x => x.r == r).Select(x => x.p).ToList());

                var jettison = info.Where(x => x.p.Def.HasModule<JettisonModuleDef>() && !x.p.Def.HasModule<EngineModuleDef>() && x.s == level).Select(x => x.p).ToList();
                if (jettison.Count > 0) stages.Add(jettison);
            }
            stages.Add(info.Where(x => x.p.Def.HasModule<ParachuteModuleDef>()).Select(x => x.p).ToList());

            int index = 0;
            foreach (List<PartInstance> stage in stages)
            {
                if (stage.Count == 0) continue;
                foreach (PartInstance p in stage) p.Stage = index;
                index++;
            }
            // Anything stageable left unassigned (e.g. docking ports) goes last.
            foreach (var x in info.Where(x => x.p.Stage < 0)) x.p.Stage = index;
            Compact(design);
        }

        private static bool IsRadialDecoupler(PartInstance p)
        {
            DecouplerModuleDef d = p.Def.GetModule<DecouplerModuleDef>();
            return d != null && (d.IsRadial || p.IsSurfaceAttached);
        }

        /// <summary>Moves a part (and its symmetry siblings) to another stage, creating a new stage if needed.</summary>
        public static void MoveToStage(VesselDesign design, PartInstance part, int stage)
        {
            if (!IsStageable(part)) return;
            stage = System.Math.Max(0, stage);
            IEnumerable<PartInstance> group = part.SymmetryGroup != 0
                ? design.Parts.Where(p => p.SymmetryGroup == part.SymmetryGroup)
                : new[] { part };
            foreach (PartInstance p in group) p.Stage = stage;
            Compact(design);
        }

        /// <summary>Inserts an empty stage at index, shifting later stages up.</summary>
        public static void InsertStage(VesselDesign design, int index)
        {
            foreach (PartInstance p in design.Parts)
                if (p.Stage >= index) p.Stage++;
        }
    }
}
