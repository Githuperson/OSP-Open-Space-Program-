using System.Text.Json.Serialization;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Util;

namespace OSP.Core.Vehicles;

/// <summary>
/// A vehicle blueprint: a tree of <see cref="PartInstance"/>s. Vehicle frame: origin at the root part's origin,
/// +Y = nose/up, +Z = forward/heading, +X = right-hand side when facing forward.
/// </summary>
public sealed class VehicleDesign
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "Untitled Vehicle";
    public string Description { get; set; } = "";
    public List<PartInstance> Parts { get; set; } = new();
    public int NextPartId { get; set; } = 1;
    public int NextSymmetryGroup { get; set; } = 1;

    [JsonIgnore] public PartInstance? Root => Parts.FirstOrDefault(p => p.ParentId < 0);
    [JsonIgnore] public bool IsEmpty => Parts.Count == 0;

    public PartInstance? Find(int id) => Parts.FirstOrDefault(p => p.Id == id);
    public PartInstance Get(int id) => Find(id) ?? throw new DataException($"Vehicle has no part {id}");
    public IEnumerable<PartInstance> ChildrenOf(int id) => Parts.Where(p => p.ParentId == id);

    public int AllocateId() => NextPartId++;
    public int AllocateSymmetryGroup() => NextSymmetryGroup++;

    public VehicleDesign Clone()
    {
        var c = new VehicleDesign
        {
            FormatVersion = FormatVersion, Name = Name, Description = Description,
            NextPartId = NextPartId, NextSymmetryGroup = NextSymmetryGroup,
        };
        foreach (var p in Parts) c.Parts.Add(p.Clone());
        return c;
    }

    /// <summary>All ids in the subtree rooted at <paramref name="id"/> (including itself), parents before children.</summary>
    public List<int> Subtree(int id)
    {
        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            result.Add(cur);
            foreach (var c in ChildrenOf(cur)) stack.Push(c.Id);
        }
        return result;
    }

    public void RemoveSubtree(int id)
    {
        var ids = new HashSet<int>(Subtree(id));
        Parts.RemoveAll(p => ids.Contains(p.Id));
    }

    /// <summary>Remove a part together with its symmetry partners (and their subtrees).</summary>
    public void RemoveWithSymmetry(int id)
    {
        var p = Find(id);
        if (p == null) return;
        if (p.SymmetryGroup != 0)
        {
            foreach (var other in Parts.Where(q => q.SymmetryGroup == p.SymmetryGroup).Select(q => q.Id).ToList())
                RemoveSubtree(other);
        }
        else RemoveSubtree(id);
    }

    /// <summary>
    /// Recompute every part's world pose from its attachment parameters. Throws <see cref="DataException"/>
    /// if the tree is malformed (missing parent, cycle, unknown nodes).
    /// </summary>
    public void Recompute(PartCatalog catalog)
    {
        if (Parts.Count == 0) return;
        var roots = Parts.Where(p => p.ParentId < 0).ToList();
        if (roots.Count != 1) throw new DataException($"Vehicle must have exactly one root part (found {roots.Count})");
        var byId = new Dictionary<int, PartInstance>();
        foreach (var p in Parts)
            if (!byId.TryAdd(p.Id, p)) throw new DataException($"Duplicate part id {p.Id}");

        var visited = new HashSet<int>();
        var queue = new Queue<PartInstance>();
        var root = roots[0];
        root.Mode = AttachMode.Root;
        root.Position = Vec3d.Zero; root.Rotation = QuatD.Identity;
        queue.Enqueue(root); visited.Add(root.Id);
        var childrenOf = Parts.Where(p => p.ParentId >= 0).GroupBy(p => p.ParentId).ToDictionary(g => g.Key, g => g.ToList());

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var pdef = catalog.Get(parent.PartId);
            if (!childrenOf.TryGetValue(parent.Id, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!visited.Add(kid.Id)) throw new DataException($"Cycle detected at part {kid.Id}");
                var cdef = catalog.Get(kid.PartId);
                var cnode = cdef.Node(kid.ChildNode) ?? throw new DataException($"Part {kid.Id} ({kid.PartId}): unknown node '{kid.ChildNode}'");
                if (kid.Mode == AttachMode.Radial)
                {
                    (kid.Position, kid.Rotation) = AttachGeometry.RadialTransform(
                        parent.Position, parent.Rotation, pdef, cnode, kid.AngleDeg, kid.OffsetY, kid.RollDeg);
                }
                else
                {
                    var pnode = pdef.Node(kid.ParentNode) ?? throw new DataException($"Part {kid.Id} ({kid.PartId}): parent node '{kid.ParentNode}' not on {pdef.Id}");
                    (kid.Position, kid.Rotation) = AttachGeometry.StackTransform(
                        parent.Position, parent.Rotation, pnode, cnode, kid.RollDeg);
                }
                queue.Enqueue(kid);
            }
        }
        if (visited.Count != Parts.Count) throw new DataException("Vehicle contains parts not connected to the root");
    }

    /// <summary>Lowest point of the design along -Y (bounding estimate), for placing it on the pad.</summary>
    public double LowestPoint(PartCatalog catalog)
    {
        double low = 0;
        foreach (var p in Parts)
        {
            var def = catalog.Get(p.PartId);
            var down = p.Rotation.Rotate(Vec3d.UnitY);
            double half = def.Shape.HalfLength, r = def.Shape.MaxRadius;
            double ext = Math.Abs(down.Y) * half + Math.Sqrt(Math.Max(0, 1 - down.Y * down.Y)) * r;
            if (def.Leg != null) ext = Math.Max(ext, Math.Abs(p.Rotation.Rotate(def.Leg.FootV).Y));
            low = Math.Min(low, p.Position.Y - ext);
        }
        return low;
    }
}
