using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Orbits;

namespace OSP.Core.Flight;

public enum PatchEnd { None, SoiExit, SoiEntry, Impact, Maneuver, Horizon }

/// <summary>One conic segment of a patched-conic trajectory, valid inside a single sphere of influence.</summary>
public sealed class PredictedPatch
{
    public CelestialBody Body { get; init; } = null!;
    public Orbit Orbit { get; init; } = null!;
    public double StartTime { get; init; }
    public double EndTime { get; set; }
    public PatchEnd End { get; set; }
    public CelestialBody? NextBody { get; set; }
    public Vec3d StartPosition { get; init; }
    public Vec3d StartVelocity { get; init; }
    public Vec3d EndPosition { get; set; }
    public Vec3d EndVelocity { get; set; }
    public ManeuverNode? EndManeuver { get; set; }

    /// <summary>Points along the patch in the body-centred frame. Elliptic arcs are sampled uniformly in time-of-flight fractions
    /// of true anomaly so periapsis stays smooth.</summary>
    public List<Vec3d> Sample(int count)
    {
        var pts = new List<Vec3d>(count + 1);
        double t0 = StartTime, t1 = EndTime;
        if (Orbit.IsElliptic && t1 - t0 > Orbit.Period) t1 = t0 + Orbit.Period;
        double nu0 = Orbit.TrueAnomalyAt(t0);
        double nu1 = Orbit.TrueAnomalyAt(t1);
        if (Orbit.IsElliptic)
        {
            double span = MathUtil.WrapTwoPi(nu1 - nu0);
            if (t1 - t0 >= Orbit.Period * 0.9999) span = MathUtil.Tau;
            for (int i = 0; i <= count; i++) pts.Add(Orbit.StateAtTrueAnomaly(nu0 + span * i / count).r);
        }
        else
        {
            // hyperbolic: true anomaly is monotonic in (-pi, pi)
            for (int i = 0; i <= count; i++) pts.Add(Orbit.StateAtTrueAnomaly(nu0 + (nu1 - nu0) * i / count).r);
        }
        return pts;
    }
}

/// <summary>Patched-conic trajectory prediction (see docs/PHYSICS.md).</summary>
public static class TrajectoryPredictor
{
    public static List<PredictedPatch> Predict(StarSystem system, CelestialBody body, Vec3d r0, Vec3d v0, double t0,
        IReadOnlyList<ManeuverNode>? maneuvers = null, int maxPatches = 6, double maxHorizon = 3.0e7, int samplesPerPatch = 720)
    {
        var patches = new List<PredictedPatch>();
        var nodes = (maneuvers ?? Array.Empty<ManeuverNode>()).Where(n => n.Time >= t0).OrderBy(n => n.Time).ToList();
        int nodeIdx = 0;
        var cb = body; var r = r0; var v = v0; double t = t0;
        double tLimit = t0 + maxHorizon;

        for (int pi = 0; pi < maxPatches && t < tLimit; pi++)
        {
            var orbit = Orbit.FromState(r, v, cb.Mu, t);
            var patch = new PredictedPatch { Body = cb, Orbit = orbit, StartTime = t, StartPosition = r, StartVelocity = v };

            double horizon = Math.Min(tLimit, orbit.IsElliptic ? t + orbit.Period * 1.0001 : tLimit);
            double tEnd = horizon; var end = PatchEnd.Horizon; CelestialBody? next = null; ManeuverNode? mnode = null;

            // Next manoeuvre node inside this patch.
            while (nodeIdx < nodes.Count && nodes[nodeIdx].Time <= t + 1e-6) nodeIdx++;
            if (nodeIdx < nodes.Count && nodes[nodeIdx].Time < tEnd) { tEnd = nodes[nodeIdx].Time; end = PatchEnd.Maneuver; mnode = nodes[nodeIdx]; }

            // SoI exit (closed form on the conic).
            if (cb.Parent != null)
            {
                double? te = TimeAtRadius(orbit, cb.SphereOfInfluence, t, outbound: true);
                if (te.HasValue && te.Value < tEnd) { tEnd = te.Value; end = PatchEnd.SoiExit; mnode = null; }
            }
            // Surface impact (descending crossing of the body radius).
            double? ti = TimeAtRadius(orbit, cb.Radius, t, outbound: false);
            if (ti.HasValue && ti.Value < tEnd) { tEnd = ti.Value; end = PatchEnd.Impact; mnode = null; }

            // Child sphere entries by sampling relative distance, refined by bisection.
            foreach (var child in cb.Children)
            {
                double? tc = FindChildEntry(system, orbit, child, t, tEnd, samplesPerPatch);
                if (tc.HasValue && tc.Value < tEnd) { tEnd = tc.Value; end = PatchEnd.SoiEntry; next = child; mnode = null; }
            }

            patch.EndTime = tEnd; patch.End = end; patch.NextBody = next; patch.EndManeuver = mnode;
            var (re, ve) = orbit.StateAt(tEnd);
            patch.EndPosition = re; patch.EndVelocity = ve;
            patches.Add(patch);

            if (end == PatchEnd.Impact || end == PatchEnd.Horizon) break;
            t = tEnd;
            switch (end)
            {
                case PatchEnd.SoiExit:
                    (r, v) = system.ConvertFrame(re, ve, cb, cb.Parent!, t);
                    cb = cb.Parent!;
                    break;
                case PatchEnd.SoiEntry:
                    (r, v) = system.ConvertFrame(re, ve, cb, next!, t);
                    cb = next!;
                    break;
                case PatchEnd.Maneuver:
                    r = re; v = ve + mnode!.DeltaVVector(re, ve);
                    nodeIdx++;
                    break;
            }
        }
        return patches;
    }

    /// <summary>First time after <paramref name="t"/> that the conic reaches radius rt moving outward/inward, closed form.</summary>
    public static double? TimeAtRadius(Orbit o, double rt, double t, bool outbound)
    {
        double e = o.Eccentricity, p = o.SemiLatusRectum;
        if (e < 1e-9) return null;
        double rp = o.PeriapsisRadius;
        if (rt < rp) return null;
        if (o.IsElliptic && rt > o.ApoapsisRadius) return null;
        double cosNu = (p / rt - 1.0) / e;
        if (cosNu < -1 || cosNu > 1) return null;
        double nu = Math.Acos(cosNu);              // inbound is -nu (2pi - nu), outbound is +nu
        double target = outbound ? nu : MathUtil.Tau - nu;
        double dt = o.TimeToTrueAnomaly(t, target);
        if (double.IsNaN(dt)) return null;
        if (dt < 1e-6) dt = o.IsElliptic ? dt + o.Period : double.NaN;
        return double.IsNaN(dt) ? null : t + dt;
    }

    private static double? FindChildEntry(StarSystem system, Orbit o, CelestialBody child, double t0, double t1, int samples)
    {
        if (t1 <= t0) return null;
        double soi = child.SphereOfInfluence;
        double F(double t)
        {
            var (r, _) = o.StateAt(t);
            var (cr, _) = system.StateRelativeToParent(child, t);
            return (r - cr).Length - soi;
        }
        double prevT = t0, prevF = F(t0);
        if (prevF < 0) return null;       // already inside (handled by SoI switch earlier)
        double step = (t1 - t0) / samples;
        for (int i = 1; i <= samples; i++)
        {
            double tt = t0 + step * i, ff = F(tt);
            if (ff < 0)
            {
                double lo = prevT, hi = tt;
                for (int k = 0; k < 50; k++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (F(mid) < 0) hi = mid; else lo = mid;
                }
                return hi;
            }
            prevT = tt; prevF = ff;
        }
        return null;
    }
}
