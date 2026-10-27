using System;
using OSP.Core.Celestial;
using OSP.Core.Math;
using OSP.Core.Orbits;
using Xunit;

namespace OSP.Tests
{
    /// <summary>Orbital mechanics compared against closed-form two-body results.</summary>
    public class OrbitalMechanicsTests
    {
        // Aster-like body: μ = g R², R = 600 km, g = 9.81 m/s².
        private const double R = 600000;
        private const double Mu = 9.81 * R * R;

        private static double Rel(double a, double b) => System.Math.Abs(a - b) / System.Math.Max(System.Math.Abs(b), 1e-300);

        [Fact]
        public void GravitationalAcceleration_MatchesInverseSquareLaw()
        {
            var body = new CelestialBody("b", "B", R, Mu, 0);
            foreach (double r in new[] { R, 2 * R, 10 * R, 123456789.0 })
            {
                Vector3d pos = new Vector3d(1, 2, -3).Normalized() * r;
                Vector3d g = body.GravityAt(pos);
                Assert.True(Rel(g.Length, Mu / (r * r)) < 1e-12);
                Assert.True(Vector3d.Dot(g.Normalized(), -pos.Normalized()) > 1 - 1e-12, "gravity must point at the centre");
            }
            Assert.True(Rel(body.SurfaceGravity, 9.81) < 1e-12);
        }

        [Fact]
        public void EscapeVelocity_IsSqrtTwoMuOverR_AndGivesParabolicOrbit()
        {
            double r = R + 100000;
            double vEsc = Orbit.EscapeVelocity(Mu, r);
            Assert.True(Rel(vEsc, System.Math.Sqrt(2 * Mu / r)) < 1e-14);
            var o = new Orbit(Mu, new Vector3d(r, 0, 0), new Vector3d(0, 0, -vEsc), 0);
            Assert.True(System.Math.Abs(o.SpecificEnergy) < 1e-6 * Mu / r);
            Assert.True(System.Math.Abs(o.Eccentricity - 1) < 1e-9);
            Assert.False(o.IsElliptic);
        }

        [Fact]
        public void CircularOrbit_HasZeroEccentricity_AndKeplerPeriod()
        {
            double r = R + 80000;
            double v = Orbit.CircularVelocity(Mu, r);
            Assert.True(Rel(v, System.Math.Sqrt(Mu / r)) < 1e-14);
            var o = new Orbit(Mu, new Vector3d(r, 0, 0), new Vector3d(0, 0, -v), 0);
            Assert.True(o.Eccentricity < 1e-12);
            double expectedPeriod = 2 * System.Math.PI * System.Math.Sqrt(r * r * r / Mu);
            Assert.True(Rel(o.Period, expectedPeriod) < 1e-12);
            Assert.True(Rel(o.ApoapsisRadius, r) < 1e-9 && Rel(o.PeriapsisRadius, r) < 1e-9);
            Assert.True(o.Inclination < 1e-12, "prograde equatorial orbit has zero inclination");
        }

        [Fact]
        public void ApoapsisAndPeriapsis_FollowVisViva()
        {
            double rp = R + 75000, ra = R + 2_000_000;
            double a = (rp + ra) / 2;
            double vp = Orbit.VisViva(Mu, rp, a);
            var o = new Orbit(Mu, new Vector3d(0, 0, rp), new Vector3d(vp, 0, 0), 0);
            Assert.True(Rel(o.PeriapsisRadius, rp) < 1e-9);
            Assert.True(Rel(o.ApoapsisRadius, ra) < 1e-9);
            Assert.True(Rel(o.SemiMajorAxis, a) < 1e-9);
            Assert.True(Rel(o.Eccentricity, (ra - rp) / (ra + rp)) < 1e-9);
            // Time from periapsis to apoapsis is half a period.
            Assert.True(Rel(o.TimeToApoapsis(0), o.Period / 2) < 1e-9);
            // Speed at apoapsis from vis-viva matches propagation.
            o.GetStateAt(o.Period / 2, out Vector3d pa, out Vector3d va);
            Assert.True(Rel(pa.Length, ra) < 1e-8);
            Assert.True(Rel(va.Length, Orbit.VisViva(Mu, ra, a)) < 1e-8);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.95)]
        public void EllipticPropagation_ReturnsToStartAfterOnePeriod(double e)
        {
            double a = R * 3;
            var o = Orbit.FromElements(Mu, a, e, 0.4, 1.0, 2.0, 0.5, 0);
            o.GetStateAt(o.Period, out Vector3d p, out Vector3d v);
            Assert.True((p - o.EpochPosition).Length < 1e-6 * a, $"position error {(p - o.EpochPosition).Length}");
            Assert.True((v - o.EpochVelocity).Length < 1e-6 * o.EpochVelocity.Length);
            // Thousands of periods later (time warp) the orbit is unchanged.
            o.GetStateAt(o.Period * 5000.25, out p, out v);
            var later = new Orbit(Mu, p, v, o.Period * 5000.25);
            Assert.True(Rel(later.SemiMajorAxis, a) < 1e-8);
            Assert.True(System.Math.Abs(later.Eccentricity - e) < 1e-8);
        }

        [Fact]
        public void HyperbolicPropagation_MatchesNumericalIntegration()
        {
            Vector3d r0 = new(R + 200000, 0, 0);
            Vector3d v0 = new(300, 50, -1.3 * Orbit.EscapeVelocity(Mu, r0.Length));
            var o = new Orbit(Mu, r0, v0, 0);
            Assert.True(o.IsHyperbolic);
            double T = 3000;
            o.GetStateAt(T, out Vector3d pk, out Vector3d vk);

            // Reference: fine RK4 integration of r'' = -μ r/|r|³.
            Vector3d x = r0, v = v0;
            double dt = 0.05;
            Vector3d Acc(Vector3d p) => p * (-Mu / System.Math.Pow(p.Length, 3));
            for (double t = 0; t < T - 1e-9; t += dt)
            {
                Vector3d k1v = Acc(x), k1x = v;
                Vector3d k2v = Acc(x + k1x * dt / 2), k2x = v + k1v * dt / 2;
                Vector3d k3v = Acc(x + k2x * dt / 2), k3x = v + k2v * dt / 2;
                Vector3d k4v = Acc(x + k3x * dt), k4x = v + k3v * dt;
                x += (k1x + 2 * k2x + 2 * k3x + k4x) * dt / 6;
                v += (k1v + 2 * k2v + 2 * k3v + k4v) * dt / 6;
            }
            Assert.True((pk - x).Length < 1.0, $"position mismatch {(pk - x).Length} m");
            Assert.True((vk - v).Length < 1e-3);
            // Energy and angular momentum conserved.
            var o2 = new Orbit(Mu, pk, vk, T);
            Assert.True(Rel(o2.SpecificEnergy, o.SpecificEnergy) < 1e-9);
            Assert.True((o2.AngularMomentum - o.AngularMomentum).Length < 1e-6 * o.AngularMomentum.Length);
        }

        [Fact]
        public void ElementsRoundTrip()
        {
            double a = 7.5e6, e = 0.25, i = 0.6, lan = 2.1, w = 0.9, m0 = 1.3;
            var o = Orbit.FromElements(Mu, a, e, i, lan, w, m0, 100);
            Assert.True(Rel(o.SemiMajorAxis, a) < 1e-10);
            Assert.True(System.Math.Abs(o.Eccentricity - e) < 1e-10);
            Assert.True(System.Math.Abs(o.Inclination - i) < 1e-10);
            Assert.True(System.Math.Abs(o.LongitudeOfAscendingNode - lan) < 1e-10);
            Assert.True(System.Math.Abs(o.ArgumentOfPeriapsis - w) < 1e-10);
        }

        [Fact]
        public void PolarOrbit_HasNinetyDegreeInclination()
        {
            double r = R + 100000;
            var o = new Orbit(Mu, new Vector3d(r, 0, 0), new Vector3d(0, Orbit.CircularVelocity(Mu, r), 0), 0);
            Assert.True(System.Math.Abs(o.Inclination - System.Math.PI / 2) < 1e-12);
        }

        [Fact]
        public void NextTimeAtRadius_FindsCrossing()
        {
            double rp = R + 75000, ra = R + 500000;
            double a = (rp + ra) / 2;
            var o = new Orbit(Mu, new Vector3d(rp, 0, 0), new Vector3d(0, 0, -Orbit.VisViva(Mu, rp, a)), 0);
            double target = R + 300000;
            double tOut = o.NextTimeAtRadius(target, 0, true);
            double tIn = o.NextTimeAtRadius(target, 0, false);
            Assert.True(tOut > 0 && tOut < o.Period / 2);
            Assert.True(tIn > o.Period / 2 && tIn < o.Period);
            Assert.True(System.Math.Abs(o.PositionAt(tOut).Length - target) < 1e-3);
            Assert.True(System.Math.Abs(o.PositionAt(tIn).Length - target) < 1e-3);
            Assert.True(double.IsNaN(o.NextTimeAtRadius(R + 10_000_000, 0, true)));
        }

        [Fact]
        public void Atmosphere_PressureFollowsExponentialProfile()
        {
            var atmo = new Atmosphere { SeaLevelPressure = 101325, SeaLevelDensity = 1.225, ScaleHeight = 5600, Height = 70000 };
            Assert.Equal(101325, atmo.Pressure(0), 6);
            Assert.True(Rel(atmo.Pressure(5600), 101325 / System.Math.E) < 1e-12);
            Assert.True(Rel(atmo.Density(11200), 1.225 / (System.Math.E * System.Math.E)) < 1e-12);
            Assert.Equal(0, atmo.Pressure(70000));
            Assert.True(atmo.Pressure(69000) < atmo.Pressure(68000));
        }

        [Fact]
        public void SphereOfInfluence_MatchesLaplaceFormula()
        {
            CelestialSystem sys = TestData.LoadSystem();
            CelestialBody aster = sys.Get("aster");
            CelestialBody tessar = sys.Get("tessar");
            double expected = tessar.Orbit.SemiMajorAxis * System.Math.Pow(tessar.Mu / aster.Mu, 0.4);
            Assert.True(Rel(tessar.SphereOfInfluence, expected) < 1e-12);
            Assert.True(tessar.SphereOfInfluence > tessar.Radius * 5);
            Assert.True(double.IsPositiveInfinity(sys.Root.SphereOfInfluence));
        }

        [Fact]
        public void TidallyLockedMoon_RotatesOncePerOrbit()
        {
            CelestialSystem sys = TestData.LoadSystem();
            CelestialBody tessar = sys.Get("tessar");
            Assert.True(Rel(tessar.RotationPeriod, tessar.Orbit.Period) < 1e-12);
        }
    }
}
