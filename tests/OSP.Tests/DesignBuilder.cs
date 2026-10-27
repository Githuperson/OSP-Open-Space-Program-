using System.Collections.Generic;
using System.Linq;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Tests
{
    /// <summary>Builds vessel designs in tests using the same placement code as the editor.</summary>
    public sealed class DesignBuilder
    {
        private readonly PartCatalog _catalog;
        public VesselDesign Design { get; } = new() { Name = "Test Rocket" };

        public DesignBuilder(PartCatalog catalog, string rootPart)
        {
            _catalog = catalog;
            PartDefinition def = catalog.Get(rootPart);
            Design.AddPart(def, -1, null, null, Vector3d.Zero, QuaternionD.Identity);
        }

        public PartInstance Last => Design.Parts[^1];

        /// <summary>Attaches a part's "top" node to the "bottom" node of <paramref name="parent"/> (default: last part).</summary>
        public PartInstance Below(string partId, PartInstance parent = null)
        {
            parent ??= Last;
            PartDefinition def = _catalog.Get(partId);
            AttachNodeDef pn = parent.Def.GetNode("bottom");
            AttachNodeDef cn = def.GetNode("top");
            AttachResult ok = Assembly.ValidateStack(Design, parent, "bottom", def, "top");
            if (!ok.Ok) throw new System.InvalidOperationException(ok.Reason);
            Placement pl = Assembly.StackPlacement(parent, pn, def, cn, 0);
            return Design.AddPart(def, parent.Id, "bottom", "top", pl.Position, pl.Rotation);
        }

        /// <summary>Attaches a part's "bottom" node to the "top" node of the parent.</summary>
        public PartInstance Above(string partId, PartInstance parent)
        {
            PartDefinition def = _catalog.Get(partId);
            Placement pl = Assembly.StackPlacement(parent, parent.Def.GetNode("top"), def, def.GetNode("bottom"), 0);
            return Design.AddPart(def, parent.Id, "top", "bottom", pl.Position, pl.Rotation);
        }

        /// <summary>Surface-attaches <paramref name="count"/> copies of a part around the parent at height <paramref name="localY"/>.</summary>
        public List<PartInstance> Radial(string partId, PartInstance parent, int count, double localY)
        {
            PartDefinition def = _catalog.Get(partId);
            PartBounds b = parent.Def.Bounds;
            Vector3d origin = parent.LocalToVessel(new Vector3d(5, localY, 0));
            Vector3d dir = parent.DirectionToVessel(new Vector3d(-1, 0, 0));
            PartRayHit hit = Assembly.RaycastPart(parent, origin, dir) ?? throw new System.InvalidOperationException("Radial ray missed the parent.");
            Placement pl = Assembly.SurfacePlacement(def, hit.Point, hit.Normal, 0);
            int group = count > 1 ? Design.NextSymmetryGroup++ : 0;
            var result = new List<PartInstance>();
            foreach (Placement p in Assembly.SymmetryPlacements(parent, pl, count))
            {
                PartInstance inst = Design.AddPart(def, parent.Id, PartInstance.SurfaceNode, PartInstance.SurfaceNode, p.Position, p.Rotation);
                inst.SymmetryGroup = group;
                result.Add(inst);
            }
            _ = b;
            return result;
        }

        /// <summary>Stacks a part on the bottom node of each given parent (e.g. engines below radial boosters).</summary>
        public List<PartInstance> BelowEach(string partId, IEnumerable<PartInstance> parents) => parents.Select(p => Below(partId, p)).ToList();

        public VesselDesign Finish(bool autoStage = true)
        {
            Design.Resolve(_catalog);
            if (autoStage) Staging.AutoStage(Design);
            return Design;
        }
    }
}
