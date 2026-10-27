using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight.Systems;

/// <summary>
/// Per-part aerodynamic model (documented in docs/PHYSICS.md).
///  * Dynamic pressure q = 1/2 rho |v|^2 with v the part velocity relative to the co-rotating air.
///  * Exposed end faces use a Newtonian impact law: F = -q Cd A (v̂·n̂)^2 n̂ (windward) or a small base-drag suction (leeward).
///    Faces covered by stack neighbours are removed (occlusion), so tall stacks do not accumulate fictitious drag.
///  * Cross-flow on the side area: F = -q CdSide A_side sin^2(alpha) n̂_perp.
///  * Lifting surfaces: saturating flat-plate normal force CN = CNmax tanh(2 pi sin(alpha)/CNmax) plus skin friction.
///  * Parachutes: F = -q (Cd A)(t) v̂.
///  * Transonic drag rise: multiplier 1 + 0.8 exp(-((M-1.1)/0.35)^2), decaying supersonic.
/// Drag at each part's centre produces the weathercock torque naturally.
/// </summary>
public static class AeroSystem
{
    public const double CnMax = 1.3;

    public static double MachMultiplier(double mach) =>
        1.0 + 0.8 * Math.Exp(-Math.Pow((mach - 1.1) / 0.35, 2)) - (mach > 2 ? 0.15 * Math.Min(1, (mach - 2) / 2) : 0);

    public static void Update(Craft craft, in FlightEnvironment env, ForceAccumulator acc, Vec3d controlC, double dt)
    {
        craft.LastDrag = 0; craft.LastDynamicPressure = 0;
        if (env.Density <= 0) return;
        var q = craft.Orientation;
        var vBody = q.InverseRotate(env.SurfaceVelocity);
        var w = craft.AngularVelocity;
        var com = craft.Mass.CenterOfMass;
        double speed0 = vBody.Length;
        double mach = speed0 / Math.Max(1.0, env.SpeedOfSound);
        double mm = MachMultiplier(mach);
        craft.LastDynamicPressure = 0.5 * env.Density * speed0 * speed0;
        Vec3d dragSum = Vec3d.Zero;

        foreach (var p in craft.Parts)
        {
            var rel = p.Position - com;
            var v = vBody + Vec3d.Cross(w, rel);
            double speed = v.Length;
            if (speed < 0.05) continue;
            var vh = v / speed;
            double qd = 0.5 * env.Density * speed * speed;
            var axis = p.Rotation.Rotate(Vec3d.UnitY);
            double c = Vec3d.Dot(vh, axis);
            var def = p.Def;
            Vec3d F = Vec3d.Zero;

            if (def.AeroSurface is { } fin)
            {
                F += FinForce(p, fin, vh, qd, controlC, rel, craft);
            }
            else
            {
                var a = def.Aero;
                double c2 = c * c;
                // axial faces
                if (c > 0)
                {
                    F += axis * (-qd * a.CdTop * p.ExposedTopArea * c2);
                    F += axis * (-qd * a.CdBase * p.ExposedBottomArea * c2);      // lee-side suction on the rear face
                }
                else
                {
                    F += axis * (qd * a.CdBottom * p.ExposedBottomArea * c2);
                    F += axis * (qd * a.CdBase * p.ExposedTopArea * c2);
                }
                // cross flow
                double s2 = 1 - c2;
                if (s2 > 1e-9)
                {
                    var perp = (vh - axis * c);
                    double pl = perp.Length;
                    if (pl > 1e-9)
                        F += perp / pl * (-qd * a.CdSide * def.Shape.SideArea * s2);
                }
                F = F * mm;
            }

            if (def.Parachute != null && (p.Chute == ChuteState.Semi || p.Chute == ChuteState.Deployed))
            {
                var pc = def.Parachute;
                double area = p.Chute == ChuteState.Semi
                    ? pc.SemiDragArea * p.ChuteProgress
                    : MathUtil.Lerp(pc.SemiDragArea, pc.FullDragArea, p.ChuteProgress);
                F += vh * (-qd * area);
            }

            if (F.LengthSquared > 0)
            {
                acc.Add(p, F, p.Position);
                dragSum += F;
            }
        }
        craft.LastDrag = dragSum.Length;
    }

    private static Vec3d FinForce(CraftPart p, AeroSurfaceModule fin, Vec3d vh, double qd, Vec3d c, Vec3d rel, Craft craft)
    {
        var n0 = p.Rotation.Rotate(fin.NormalV);
        var span = p.Rotation.Rotate(Vec3d.UnitX);          // hinge axis: fins extend along local X
        double defl = 0;
        if (fin.Control && qd > 1.0)
        {
            double maxD = fin.MaxDeflectionDeg * MathUtil.Deg2Rad;
            // Probe: torque direction produced by +max deflection under the current flow.
            var nPlus = Rodrigues(n0, span, maxD);
            var tProbe = Vec3d.Cross(rel, PlateForce(nPlus, vh, qd, fin.Area, fin.Cd0));
            double tl = tProbe.Length;
            if (tl > 1e-9)
            {
                double s = Math.Clamp(Vec3d.Dot(c, tProbe / tl) * 2.0, -1, 1);
                defl = s * maxD;
            }
        }
        p.ControlSurfaceDeflection = defl;
        var n = defl != 0 ? Rodrigues(n0, span, defl) : n0;
        return PlateForce(n, vh, qd, fin.Area, fin.Cd0);
    }

    private static Vec3d PlateForce(Vec3d n, Vec3d vh, double qd, double area, double cd0)
    {
        double vn = Vec3d.Dot(vh, n);
        double sinA = Math.Abs(vn);
        double cn = CnMax * Math.Tanh(2 * Math.PI * sinA / CnMax);
        var normalForce = n * (-Math.Sign(vn) * qd * area * cn);
        var skin = vh * (-qd * area * cd0 * 2);
        return normalForce + skin;
    }

    private static Vec3d Rodrigues(Vec3d v, Vec3d axis, double ang)
    {
        var k = axis.Normalized;
        return v * Math.Cos(ang) + Vec3d.Cross(k, v) * Math.Sin(ang) + k * (Vec3d.Dot(k, v) * (1 - Math.Cos(ang)));
    }
}
