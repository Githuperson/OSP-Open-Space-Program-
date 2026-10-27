using OSP.Core.Mathx;

namespace OSP.Core.Flight.Systems;

/// <summary>
/// Joint-stress analysis. For every joint (a part and its parent) the force and bending torque the joint must transmit
/// to give the subtree beyond it its observed acceleration are computed:
///   F_joint = sum_i (m_i a_i - F_ext,i),   M_joint = sum_i (x_i - x_j) x (m_i a_i) - sum_i (p_i - x_j) x F_ext,i
/// with a_i = a_cm + alpha x r_i + omega x (omega x r_i) (gravity cancels because it is uniform).
/// A joint fails if overloaded for 0.1 s, or instantly beyond 3x its rating.
/// </summary>
public static class StructureSystem
{
    public const double FailTime = 0.1;
    public const double InstantFactor = 3.0;

    public static List<CraftPart> Analyze(Craft craft, ForceAccumulator acc, Vec3d angularAccel, double dt)
    {
        var failed = new List<CraftPart>();
        double m = craft.Mass.Mass;
        var com = craft.Mass.CenterOfMass;
        var aCm = acc.Force / m;
        var w = craft.AngularVelocity;

        // Per-part external force sums.
        var fExt = new Dictionary<int, Vec3d>(); var tExt = new Dictionary<int, Vec3d>();
        foreach (var it in acc.Items)
        {
            fExt[it.Part.Id] = fExt.GetValueOrDefault(it.Part.Id) + it.Force;
            tExt[it.Part.Id] = tExt.GetValueOrDefault(it.Part.Id) + Vec3d.Cross(it.Point, it.Force);   // about craft origin
        }

        // Post-order accumulation: sumF, sumTorqueAboutOrigin of (m a - F_ext).
        var sumF = new Dictionary<int, Vec3d>(); var sumT = new Dictionary<int, Vec3d>();
        var order = new List<CraftPart>(craft.Parts.Count);
        var root = craft.RootPart;
        if (root == null) return failed;
        var stack = new Stack<CraftPart>(); stack.Push(root);
        while (stack.Count > 0) { var c = stack.Pop(); order.Add(c); foreach (var k in craft.ChildrenOf(c.Id)) stack.Push(k); }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var p = order[i];
            var r = p.Position - com;
            var a = aCm + Vec3d.Cross(angularAccel, r) + Vec3d.Cross(w, Vec3d.Cross(w, r));
            double pm = p.Mass;
            var f = a * pm - fExt.GetValueOrDefault(p.Id);
            var t = Vec3d.Cross(p.Position, a * pm) - tExt.GetValueOrDefault(p.Id);
            foreach (var k in craft.ChildrenOf(p.Id)) { f += sumF[k.Id]; t += sumT[k.Id]; }
            sumF[p.Id] = f; sumT[p.Id] = t;
        }

        foreach (var p in order)
        {
            if (p.ParentId < 0) continue;
            var f = sumF[p.Id];
            var mj = sumT[p.Id] - Vec3d.Cross(p.Position, f);
            double fr = f.Length / p.Def.JointStrength;
            double tr = mj.Length / p.Def.JointTorque;
            double ratio = Math.Max(fr, tr);
            p.LastJointForce = f.Length; p.LastJointTorque = mj.Length;
            if (ratio > 1.0) p.OverloadTime += dt; else p.OverloadTime = Math.Max(0, p.OverloadTime - dt);
            if (p.OverloadTime >= FailTime || ratio > InstantFactor) failed.Add(p);
        }
        return failed;
    }
}
