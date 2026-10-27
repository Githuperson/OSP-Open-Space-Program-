using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Util;

namespace OSP.Core.Vehicles;

/// <summary>A candidate attachment found by <see cref="VehicleEditor"/>; used for snapping and ghost preview.</summary>
public sealed class AttachCandidate
{
    public int ParentId { get; init; }
    public AttachMode Mode { get; init; }
    public string ParentNode { get; init; } = "";
    public string ChildNode { get; init; } = "";
    public double AngleDeg { get; init; }
    public double OffsetY { get; init; }
    public Vec3d Position { get; init; }
    public QuatD Rotation { get; init; } = QuatD.Identity;
}

/// <summary>Editing operations on a <see cref="VehicleDesign"/> (attach, symmetry, validation of connection rules).</summary>
public sealed class VehicleEditor
{
    private readonly PartCatalog _catalog;
    public VehicleEditor(PartCatalog catalog) { _catalog = catalog; }

    /// <summary>Start a new vehicle with the given part as root.</summary>
    public PartInstance SetRoot(VehicleDesign d, string partId)
    {
        _catalog.Get(partId);
        d.Parts.Clear();
        var inst = new PartInstance { Id = d.AllocateId(), PartId = partId, Mode = AttachMode.Root };
        d.Parts.Add(inst);
        d.Recompute(_catalog);
        return inst;
    }

    /// <summary>True if the parent's stack node is free (no other part is mated to it).</summary>
    public bool IsNodeFree(VehicleDesign d, int partId, string nodeId) =>
        !d.Parts.Any(p => (p.ParentId == partId && p.Mode == AttachMode.Stack && p.ParentNode == nodeId)) &&
        !(d.Find(partId) is { ParentId: >= 0, Mode: AttachMode.Stack } me && me.ChildNode == nodeId);

    /// <summary>
    /// Every free stack/socket connection that could accept the part's nodes. Candidate poses are
    /// full world transforms so an editor can rank them by screen/world distance.
    /// </summary>
    public List<AttachCandidate> FindNodeCandidates(VehicleDesign d, string newPartId)
    {
        var result = new List<AttachCandidate>();
        var newDef = _catalog.Get(newPartId);
        foreach (var parent in d.Parts)
        {
            var pdef = _catalog.Get(parent.PartId);
            foreach (var pn in pdef.Nodes)
            {
                if (pn.IsSurface || !IsNodeFree(d, parent.Id, pn.Id)) continue;
                foreach (var cn in newDef.Nodes)
                {
                    if (!AttachGeometry.CanMate(pn, cn)) continue;
                    var (pos, rot) = AttachGeometry.StackTransform(parent.Position, parent.Rotation, pn, cn, 0);
                    result.Add(new AttachCandidate
                    {
                        ParentId = parent.Id, Mode = AttachMode.Stack, ParentNode = pn.Id, ChildNode = cn.Id,
                        Position = pos, Rotation = rot,
                    });
                }
            }
        }
        return result;
    }

    /// <summary>Radial candidate at an explicit angle/height on a cylindrical parent.</summary>
    public AttachCandidate? RadialCandidate(VehicleDesign d, int parentId, string newPartId, double angleDeg, double offsetY)
    {
        var parent = d.Find(parentId);
        if (parent == null) return null;
        var pdef = _catalog.Get(parent.PartId);
        var newDef = _catalog.Get(newPartId);
        var srf = newDef.Nodes.FirstOrDefault(n => n.IsSurface);
        if (srf == null || !pdef.AcceptsRadial || pdef.Shape.IsBox) return null;
        double half = pdef.Shape.HalfLength;
        offsetY = Math.Clamp(offsetY, -half, half);
        var (pos, rot) = AttachGeometry.RadialTransform(parent.Position, parent.Rotation, pdef, srf, angleDeg, offsetY, 0);
        return new AttachCandidate
        {
            ParentId = parentId, Mode = AttachMode.Radial, ChildNode = srf.Id,
            AngleDeg = angleDeg, OffsetY = offsetY, Position = pos, Rotation = rot,
        };
    }

    /// <summary>Attach a new part (and symmetry copies for radial mounts). Returns the created instances.</summary>
    public List<PartInstance> Attach(VehicleDesign d, string partId, AttachCandidate c, int symmetry = 1, double rollDeg = 0)
    {
        var def = _catalog.Get(partId);
        var created = new List<PartInstance>();
        var parent = d.Get(c.ParentId);
        int group = symmetry > 1 ? d.AllocateSymmetryGroup() : 0;
        int count = Math.Max(1, symmetry);

        if (c.Mode == AttachMode.Radial)
        {
            for (int i = 0; i < count; i++)
            {
                var inst = new PartInstance
                {
                    Id = d.AllocateId(), PartId = partId, ParentId = c.ParentId, Mode = AttachMode.Radial,
                    ChildNode = c.ChildNode, AngleDeg = c.AngleDeg + 360.0 * i / count, OffsetY = c.OffsetY,
                    RollDeg = rollDeg, SymmetryGroup = group,
                };
                d.Parts.Add(inst); created.Add(inst);
            }
        }
        else
        {
            // Stack attach: symmetry applies only when the parent node belongs to a radially-symmetric set
            // (handled by the editor placing each copy explicitly); a single stack part has no copies.
            var inst = new PartInstance
            {
                Id = d.AllocateId(), PartId = partId, ParentId = c.ParentId, Mode = AttachMode.Stack,
                ParentNode = c.ParentNode, ChildNode = c.ChildNode, RollDeg = rollDeg, SymmetryGroup = 0,
            };
            d.Parts.Add(inst); created.Add(inst);

            // Replicate across the parent's own symmetry group (e.g. nose cones on N boosters).
            if (parent.SymmetryGroup != 0)
            {
                foreach (var sibling in d.Parts.Where(p => p.SymmetryGroup == parent.SymmetryGroup && p.Id != parent.Id).ToList())
                {
                    if (!IsNodeFree(d, sibling.Id, c.ParentNode)) continue;
                    var copy = new PartInstance
                    {
                        Id = d.AllocateId(), PartId = partId, ParentId = sibling.Id, Mode = AttachMode.Stack,
                        ParentNode = c.ParentNode, ChildNode = c.ChildNode, RollDeg = rollDeg, SymmetryGroup = 0,
                    };
                    d.Parts.Add(copy); created.Add(copy);
                }
                if (created.Count > 1)
                {
                    int g = d.AllocateSymmetryGroup();
                    foreach (var cr in created) cr.SymmetryGroup = g;
                }
            }
        }
        d.Recompute(_catalog);
        StagingPlanner.AutoAssign(d, _catalog);
        return created;
    }

    /// <summary>Detach a part and its subtree (keeps it out of the design).</summary>
    public void Delete(VehicleDesign d, int partId)
    {
        d.RemoveWithSymmetry(partId);
        if (!d.IsEmpty) d.Recompute(_catalog);
        StagingPlanner.AutoAssign(d, _catalog);
    }
}
