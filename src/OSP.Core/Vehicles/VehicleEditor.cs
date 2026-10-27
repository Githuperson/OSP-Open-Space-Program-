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

    /// <summary>Attach a new single part (and symmetry copies for radial mounts). Returns the created root instances.</summary>
    public List<PartInstance> Attach(VehicleDesign d, string partId, AttachCandidate c, int symmetry = 1, double rollDeg = 0)
    {
        var held = new VehicleDesign();
        held.Parts.Add(new PartInstance { Id = 1, PartId = partId, Mode = AttachMode.Root });
        held.NextPartId = 2;
        return AttachDesign(d, held, c, symmetry, rollDeg);
    }

    /// <summary>Copy of the subtree rooted at <paramref name="partId"/> as a standalone design (ids renumbered from 1, root first).</summary>
    public VehicleDesign ExtractSubtree(VehicleDesign d, int partId)
    {
        var ids = d.Subtree(partId);
        var map = new Dictionary<int, int>();
        int next = 1;
        foreach (var id in ids) map[id] = next++;
        var sub = new VehicleDesign { Name = d.Name };
        foreach (var id in ids)
        {
            var p = d.Get(id).Clone();
            p.Id = map[id];
            p.ParentId = id == partId ? -1 : map[p.ParentId];
            if (id == partId) { p.Mode = AttachMode.Root; p.ParentNode = ""; }
            p.Stage = -1;
            sub.Parts.Add(p);
        }
        sub.NextPartId = next;
        sub.NextSymmetryGroup = d.NextSymmetryGroup;
        sub.Recompute(_catalog);
        return sub;
    }

    /// <summary>All free connection points where the held design's root could mate with the target design.</summary>
    public List<AttachCandidate> FindNodeCandidates(VehicleDesign target, VehicleDesign held)
    {
        var result = new List<AttachCandidate>();
        var root = held.Root ?? throw new Util.DataException("Held design is empty");
        var newDef = _catalog.Get(root.PartId);
        foreach (var parent in target.Parts)
        {
            var pdef = _catalog.Get(parent.PartId);
            foreach (var pn in pdef.Nodes)
            {
                if (pn.IsSurface || !IsNodeFree(target, parent.Id, pn.Id)) continue;
                foreach (var cn in newDef.Nodes)
                {
                    if (!AttachGeometry.CanMate(pn, cn) || !IsNodeFree(held, root.Id, cn.Id)) continue;
                    var (pos, rot) = AttachGeometry.StackTransform(parent.Position, parent.Rotation, pn, cn, 0);
                    result.Add(new AttachCandidate
                    {
                        ParentId = parent.Id, Mode = AttachMode.Stack, ParentNode = pn.Id, ChildNode = cn.Id, Position = pos, Rotation = rot,
                    });
                }
            }
        }
        return result;
    }

    /// <summary>Root pose of a held part for a candidate with an extra roll about the attachment axis.</summary>
    public (Vec3d pos, QuatD rot) PoseFor(VehicleDesign target, AttachCandidate c, string heldRootPartId, double rollDeg)
    {
        var parent = target.Get(c.ParentId);
        var pdef = _catalog.Get(parent.PartId);
        var cdef = _catalog.Get(heldRootPartId);
        var cnode = cdef.Node(c.ChildNode)!;
        if (c.Mode == AttachMode.Radial)
            return AttachGeometry.RadialTransform(parent.Position, parent.Rotation, pdef, cnode, c.AngleDeg, c.OffsetY, rollDeg);
        return AttachGeometry.StackTransform(parent.Position, parent.Rotation, pdef.Node(c.ParentNode)!, cnode, rollDeg);
    }

    /// <summary>
    /// Attach a held design (one part or a whole subtree) at a candidate. Radial candidates are replicated
    /// <paramref name="symmetry"/> times around the parent; stack candidates replicate across the parent's symmetry group.
    /// </summary>
    public List<PartInstance> AttachDesign(VehicleDesign d, VehicleDesign held, AttachCandidate c, int symmetry = 1, double rollDeg = 0)
    {
        var heldRoot = held.Root ?? throw new Util.DataException("Held design is empty");
        var created = new List<PartInstance>();
        var parent = d.Get(c.ParentId);

        // Decide the list of (parentId, angle) copies.
        var copies = new List<(int parentId, double angle)>();
        if (c.Mode == AttachMode.Radial)
        {
            int n = Math.Max(1, symmetry);
            for (int i = 0; i < n; i++) copies.Add((c.ParentId, c.AngleDeg + 360.0 * i / n));
        }
        else
        {
            copies.Add((c.ParentId, 0));
            if (parent.SymmetryGroup != 0)
                foreach (var sib in d.Parts.Where(p => p.SymmetryGroup == parent.SymmetryGroup && p.Id != parent.Id).ToList())
                    if (IsNodeFree(d, sib.Id, c.ParentNode)) copies.Add((sib.Id, 0));
        }
        int rootGroup = copies.Count > 1 ? d.AllocateSymmetryGroup() : 0;

        foreach (var (parentId, angle) in copies)
        {
            var map = new Dictionary<int, int>();
            var groupMap = new Dictionary<int, int>();
            foreach (var hp in held.Parts) map[hp.Id] = d.AllocateId();
            foreach (var hp in held.Parts)
            {
                var np = hp.Clone();
                np.Id = map[hp.Id];
                if (hp.Id == heldRoot.Id)
                {
                    np.ParentId = parentId; np.Mode = c.Mode; np.ParentNode = c.ParentNode; np.ChildNode = c.ChildNode;
                    np.AngleDeg = angle; np.OffsetY = c.OffsetY; np.RollDeg = rollDeg; np.SymmetryGroup = rootGroup;
                    created.Add(np);
                }
                else
                {
                    np.ParentId = map[hp.ParentId];
                    if (hp.SymmetryGroup != 0)
                    {
                        if (!groupMap.TryGetValue(hp.SymmetryGroup, out var g)) groupMap[hp.SymmetryGroup] = g = d.AllocateSymmetryGroup();
                        np.SymmetryGroup = g;
                    }
                }
                d.Parts.Add(np);
            }
        }
        d.Recompute(_catalog);
        StagingPlanner.AutoAssign(d, _catalog);
        return created;
    }

    /// <summary>Remove a subtree without symmetry handling (used when picking a part up to move it).</summary>
    public void RemoveSubtree(VehicleDesign d, int partId)
    {
        d.RemoveSubtree(partId);
        if (!d.IsEmpty) d.Recompute(_catalog);
        StagingPlanner.AutoAssign(d, _catalog);
    }

    /// <summary>Detach a part and its subtree (keeps it out of the design).</summary>
    public void Delete(VehicleDesign d, int partId)
    {
        d.RemoveWithSymmetry(partId);
        if (!d.IsEmpty) d.Recompute(_catalog);
        StagingPlanner.AutoAssign(d, _catalog);
    }
}
