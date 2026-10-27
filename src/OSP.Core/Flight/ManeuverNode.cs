using OSP.Core.Mathx;
using OSP.Core.Orbits;

namespace OSP.Core.Flight;

/// <summary>
/// An impulsive manoeuvre planned at universal time <see cref="Time"/>. The delta-v is expressed in the orbital
/// frame at that instant: Prograde (along velocity), Normal (orbit normal r x v) and Radial-out.
/// </summary>
public sealed class ManeuverNode
{
    public int Id { get; set; }
    public double Time { get; set; }
    public double Prograde { get; set; }
    public double Normal { get; set; }
    public double Radial { get; set; }
    public double DeltaVMagnitude => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);

    /// <summary>Inertial delta-v vector given the craft state (relative to the SoI body) at the node time.</summary>
    public Vec3d DeltaVVector(Vec3d r, Vec3d v)
    {
        var pro = v.Normalized;
        var nrm = Vec3d.Cross(r, v).Normalized;
        var rad = Vec3d.Cross(pro, nrm).Normalized;
        return pro * Prograde + nrm * Normal + rad * Radial;
    }
}

public static class ManeuverPlanner
{
    /// <summary>
    /// Burn time for a given delta-v with the craft's currently-active stage. Uses the rocket equation with the combined
    /// vacuum thrust/Isp of the ignited engines (or all engines of the next stage if none are lit) and ignores later staging.
    /// </summary>
    public static (double burnTime, bool ok) EstimateBurnTime(Craft craft, double dv)
    {
        double thrust = 0, mdot = 0;
        int stage = Math.Max(craft.CurrentStage, -1);
        var engines = craft.Parts.Where(p => p.Def.Engine != null && !p.FlamedOut && (p.Ignited || p.Stage == StagingController.NextStage(craft))).ToList();
        if (engines.Count == 0) return (double.PositiveInfinity, false);
        foreach (var e in engines)
        {
            var m = e.Def.Engine!;
            thrust += m.ThrustVac; mdot += m.MaxMassFlow;
        }
        if (mdot <= 0) return (double.PositiveInfinity, false);
        double isp = thrust / (mdot * MathUtil.G0);
        double m0 = craft.TotalMass;
        double m1 = m0 * Math.Exp(-dv / (isp * MathUtil.G0));
        double t = (m0 - m1) / mdot;
        return (t, true);
    }
}
