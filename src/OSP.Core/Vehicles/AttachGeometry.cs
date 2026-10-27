using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles;

/// <summary>Pure geometry + rules for attaching one part to another.</summary>
public static class AttachGeometry
{
    /// <summary>Can <paramref name="childNode"/> of the child connect to <paramref name="parentNode"/> of the parent?</summary>
    public static bool CanMate(AttachNodeDef parentNode, AttachNodeDef childNode)
    {
        if (parentNode.IsStack && childNode.IsStack) return parentNode.Size == childNode.Size;
        if (parentNode.IsSocket && childNode.IsSurface) return true;
        return false;
    }

    /// <summary>Child pose when its <paramref name="child"/> node is mated to the parent's node (faces opposed).</summary>
    public static (Vec3d pos, QuatD rot) StackTransform(
        Vec3d parentPos, QuatD parentRot, AttachNodeDef parentNode, AttachNodeDef childNode, double rollDeg)
    {
        var anchor = parentPos + parentRot.Rotate(parentNode.PositionV);
        var axis = parentRot.Rotate(parentNode.DirectionV);
        var q0 = QuatD.FromTo(childNode.DirectionV, -axis);
        var q = QuatD.FromAxisAngle(axis, rollDeg * MathUtil.Deg2Rad) * q0;
        var pos = anchor - q.Rotate(childNode.PositionV);
        return (pos, q.Normalized);
    }

    /// <summary>Child pose when surface-mounted on a cylindrical parent at angle/height.</summary>
    public static (Vec3d pos, QuatD rot) RadialTransform(
        Vec3d parentPos, QuatD parentRot, PartDefinition parent, AttachNodeDef childSurfaceNode,
        double angleDeg, double offsetY, double rollDeg)
    {
        double phi = angleDeg * MathUtil.Deg2Rad;
        double radius = parent.Shape.RadiusAt(offsetY);
        var nLocal = new Vec3d(Math.Cos(phi), 0, Math.Sin(phi));
        var pLocal = new Vec3d(radius * nLocal.X, offsetY, radius * nLocal.Z);
        var anchor = parentPos + parentRot.Rotate(pLocal);
        var nWorld = parentRot.Rotate(nLocal);
        // Child +X (outward) must point along the surface normal: rotate about parent Y by -phi.
        var q = parentRot * QuatD.FromAxisAngle(Vec3d.UnitY, -phi);
        if (rollDeg != 0) q = QuatD.FromAxisAngle(nWorld, rollDeg * MathUtil.Deg2Rad) * q;
        q = q.Normalized;
        var pos = anchor - q.Rotate(childSurfaceNode.PositionV);
        return (pos, q);
    }

    /// <summary>World-space position and direction of a node for a placed part.</summary>
    public static (Vec3d pos, Vec3d dir) NodeWorld(PartInstance inst, AttachNodeDef node) =>
        (inst.Position + inst.Rotation.Rotate(node.PositionV), inst.Rotation.Rotate(node.DirectionV));
}
