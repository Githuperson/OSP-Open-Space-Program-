using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Flight;
using Xunit;

namespace OSP.Core.Tests;

public class OrbitTests
{
    private static readonly double Mu = TestEnv.Aldara.Mu;
    private static readonly double R = TestEnv.Aldara.Radius;

    [Fact]
    public void GravityMatchesNewton()
    {
        // g = GM / r^2 at the surface (3.5316e12 / 6e5^2 = 9.81 m/s^2)
        var g = OrbitMath.GravityAcceleration(Mu, new Vec3d(R, 0, 0));
        Assert.Equal(-Mu / (R * R), g.X, 9);
        Assert.Equal(9.81, Mu / (R * R), 3);
        // inverse-square: doubling r quarters g
        var g2 = OrbitMath.GravityAcceleration(Mu, new Vec3d(0, 2 * R, 0));
        Assert.Equal(g.Length / 4, g2.Length, 9);
    }

    [Fact]
    public void EscapeVelocityIsSqrt2TimesCircular()
    {
        double r = R + 100_000;
        double vc = OrbitMath.CircularSpeed(Mu, r), ve = OrbitMath.EscapeSpeed(Mu, r);
        Assert.Equal(Math.Sqrt(2) * vc, ve, 6);
        // Analytic value: sqrt(2 * 3.5316e12 / 700000)
        Assert.Equal(Math.Sqrt(2 * 3.5316e12 / 700000.0), ve, 4);
    }

    [Fact]
    public void CircularOrbitSpeedAndPeriod()
    {
        double r = R + 100_000;
        double v = OrbitMath.CircularSpeed(Mu, r);
        var o = Orbit.FromState(new Vec3d(r, 0, 0), new Vec3d(0, 0, -v), Mu, 0);   // some prograde tangent
        Assert.True(o.Eccentricity < 1e-9);
        Assert.Equal(r, o.SemiMajorAxis, 3);
        double expectedPeriod = 2 * Math.PI * Math.Sqrt(Math.Pow(r, 3) / Mu);
        Assert.Equal(expectedPeriod, o.Period, 6);
        // speed constant around the orbit
        for (double t = 0; t < o.Period; t += o.Period / 7)
            Assert.Equal(v, o.StateAt(t).v.Length, 3);
    }

    [Fact]
    public void ApoapsisPeriapsisAndEccentricityFromState()
    {
        double rp = R + 80_000, ra = R + 600_000;
        double a = (rp + ra) / 2;
        double vp = OrbitMath.VisViva(Mu, rp, a);
        var o = Orbit.FromState(new Vec3d(rp, 0, 0), new Vec3d(0, 0, -vp), Mu, 0);
        Assert.Equal(rp, o.PeriapsisRadius, 2);
        Assert.Equal(ra, o.ApoapsisRadius, 2);
        Assert.Equal((ra - rp) / (ra + rp), o.Eccentricity, 9);
        Assert.Equal(0.0, o.TimeToPeriapsis(0) % o.Period, 3);
        Assert.Equal(o.Period / 2, o.TimeToApoapsis(0), 3);
    }

    [Fact]
    public void InclinationOfPolarOrbitIs90Degrees()
    {
        double r = R + 100_000; double v = OrbitMath.CircularSpeed(Mu, r);
        var o = Orbit.FromState(new Vec3d(r, 0, 0), new Vec3d(0, v, 0), Mu, 0);   // velocity along north axis
        Assert.Equal(Math.PI / 2, o.Inclination, 6);
        var eq = Orbit.FromState(new Vec3d(r, 0, 0), new Vec3d(0, 0, -v), Mu, 0);
        Assert.Equal(0.0, eq.Inclination, 6);
    }

    [Fact]
    public void KeplerPropagationMatchesElementEvaluation()
    {
        double rp = R + 80_000, ra = R + 2_500_000, a = (rp + ra) / 2;
        var o = Orbit.FromState(new Vec3d(0.6 * rp, 0.3 * rp, -0.74 * rp).Normalized * rp, new Vec3d(1, 0, 0.2).Normalized * OrbitMath.VisViva(Mu, rp, a), Mu, 0);
        var (r0, v0) = o.StateAt(0);
        foreach (double dt in new[] { 10.0, 600.0, 5000.0, 123456.0, 1.0e7 })
        {
            var (rp1, vp1) = KeplerPropagator.Propagate(r0, v0, Mu, dt);
            var (re, ve) = o.StateAt(dt);
            Assert.True((rp1 - re).Length < 1e-3 * Math.Max(1, 1e-3 * dt) + 1.0, $"dt={dt} position error {(rp1 - re).Length}");
            Assert.True((vp1 - ve).Length < 1e-3, $"dt={dt} velocity error {(vp1 - ve).Length}");
        }
    }

    [Fact]
    public void PropagationConservesEnergyAndAngularMomentumAtHugeSteps()
    {
        double rp = R + 100_000;
        var r = new Vec3d(rp, 0, 0); var v = new Vec3d(0, 0, -OrbitMath.CircularSpeed(Mu, rp) * 1.15);
        double e0 = v.LengthSquared / 2 - Mu / r.Length; var h0 = Vec3d.Cross(r, v);
        for (int i = 0; i < 2000; i++) (r, v) = KeplerPropagator.Propagate(r, v, Mu, 1667.0);   // 100,000x warp at 60 fps
        double e1 = v.LengthSquared / 2 - Mu / r.Length; var h1 = Vec3d.Cross(r, v);
        Assert.True(Math.Abs(e1 - e0) / Math.Abs(e0) < 1e-9, $"energy drift {(e1 - e0) / e0}");
        Assert.True((h1 - h0).Length / h0.Length < 1e-9);
    }

    [Fact]
    public void FullPeriodReturnsToStart()
    {
        double r0 = R + 150_000; double v0 = OrbitMath.CircularSpeed(Mu, r0) * 1.1;
        var r = new Vec3d(r0, 0, 0); var v = new Vec3d(0, 0, -v0);
        var o = Orbit.FromState(r, v, Mu, 0);
        var (r1, v1) = KeplerPropagator.Propagate(r, v, Mu, o.Period);
        Assert.True((r1 - r).Length < 1e-3, $"{(r1 - r).Length}");
        Assert.True((v1 - v).Length < 1e-6);
    }

    [Fact]
    public void HyperbolicEscapeKeepsExcessSpeed()
    {
        double r0 = R + 100_000; double v0 = OrbitMath.EscapeSpeed(Mu, r0) * 1.3;
        var r = new Vec3d(r0, 0, 0); var v = new Vec3d(0, 0, -v0);
        double vInf2 = v0 * v0 - 2 * Mu / r0;                     // analytic v_inf^2
        var (r1, v1) = KeplerPropagator.Propagate(r, v, Mu, 5.0e6);
        double vInf2Later = v1.LengthSquared - 2 * Mu / r1.Length;
        Assert.True(Math.Abs(vInf2 - vInf2Later) / vInf2 < 1e-9);
        Assert.True(r1.Length > 1e9);
    }

    [Fact]
    public void SphereOfInfluenceFollowsLaplaceFormula()
    {
        var lumen = TestEnv.System.ById("lumen");
        double expected = lumen.OrbitDef!.SemiMajorAxis * Math.Pow(lumen.Mu / TestEnv.Aldara.Mu, 0.4);
        Assert.Equal(expected, lumen.SphereOfInfluence, 3);
        Assert.True(lumen.SphereOfInfluence > lumen.Radius * 5);
    }

    [Fact]
    public void BodiesStayOnTheirOrbits()
    {
        var sys = TestEnv.System;
        var aldara = sys.ById("aldara");
        var (r0, _) = sys.StateInRoot(aldara, 0);
        double T = aldara.Orbit!.Period;
        var (r1, _) = sys.StateInRoot(aldara, T);
        Assert.True((r1 - r0).Length < 1.0);
        var (rm, vm) = sys.StateRelativeToParent(sys.ById("lumen"), 12345);
        Assert.Equal(sys.ById("lumen").OrbitDef!.SemiMajorAxis, rm.Length, 3);
    }

    [Fact]
    public void PatchedConicPredictsLumenEncounter()
    {
        // Hohmann-like transfer from 100 km orbit to Lumen's orbit radius, launched at the correct phase.
        var sys = TestEnv.System; var al = sys.ById("aldara"); var lu = sys.ById("lumen");
        double r1 = al.Radius + 100_000, r2 = lu.OrbitDef!.SemiMajorAxis;
        var (dv1, _) = OrbitMath.HohmannDeltaV(al.Mu, r1, r2);
        double vc = OrbitMath.CircularSpeed(al.Mu, r1) + dv1;
        double aT = (r1 + r2) / 2; double tof = Math.PI * Math.Sqrt(aT * aT * aT / al.Mu);
        // Moon angular position at departure must lead the craft by (pi - n_moon * tof) at arrival.
        double nMoon = lu.Orbit!.MeanMotion;
        double phase0 = Math.PI - nMoon * tof;   // moon ahead of craft by this angle at departure
        // Craft starts at angle 0 on its orbit; place t0 so the moon is at that angle.
        double t0 = 0; // search t0 where moon true longitude (angle in XZ plane, Z->X positive) equals phase0
        double Lon(double t) { var (p, _) = sys.StateRelativeToParent(lu, t); return Math.Atan2(p.X, p.Z); }
        // Craft angle convention: position (0,0,r1) i.e. angle 0 => Z axis; velocity +X (counter-clockwise from +Y).
        double best = 0, bestErr = 1e9;
        for (double t = 0; t < lu.Orbit.Period; t += 200)
        {
            double err = Math.Abs(MathUtil.WrapPi(Lon(t) - phase0));
            if (err < bestErr) { bestErr = err; best = t; }
        }
        t0 = best;
        var patches = TrajectoryPredictor.Predict(sys, al, new Vec3d(0, 0, r1), new Vec3d(vc, 0, 0), t0, null, 6, 1.0e7);
        Assert.Contains(patches, p => p.Body == lu);
        var inLumen = patches.First(p => p.Body == lu);
        Assert.True(inLumen.Orbit.PeriapsisRadius < 3_000_000, "periapsis at Lumen should be modest");
    }
}
