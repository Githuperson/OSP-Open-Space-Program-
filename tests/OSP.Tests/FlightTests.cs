using System;
using System.Linq;
using OSP.Core;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Tests
{
    /// <summary>Integration tests running the real flight simulation.</summary>
    public class FlightTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();
        private readonly CelestialSystem _system = TestData.LoadSystem();

        private VesselDesign SimpleRocket()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Below("tank.t400");
            b.Below("tank.t400");
            b.Below("engine.vector");
            return b.Finish();
        }

        private static void Run(FlightWorld w, double seconds)
        {
            int steps = (int)System.Math.Round(seconds / FlightWorld.PhysicsStep);
            for (int i = 0; i < steps; i++) w.StepPhysics(FlightWorld.PhysicsStep);
        }

        [Fact]
        public void Launch_PlacesVesselRestingOnThePad()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.Launch(SimpleRocket());
            Assert.True(v.Pinned);
            Assert.Equal(Situation.Prelaunch, v.Situation);
            Run(w, 5);
            Assert.True(v.Telemetry.AltitudeTerrain < 10, $"vessel should be on the ground, AGL {v.Telemetry.AltitudeTerrain}");
            // At rest relative to the rotating surface.
            Assert.True(v.SurfaceVelocity.Length < 0.01);
            Assert.Single(w.Vessels);
        }

        /// <summary>
        /// The required integration test: build a valid rocket, ignite its engine, and verify the trajectory responds to
        /// thrust (climbs, consumes fuel at ṁ = F/(Isp·g0)) and to gravity (decelerates at ≈ g after cut-off).
        /// </summary>
        [Fact]
        public void IgnitedRocket_ClimbsBurnsFuelAndFallsBackUnderGravity()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.Launch(SimpleRocket());
            double m0 = v.Mass;
            double startAlt = v.Altitude;
            v.Control.Throttle = 1.0;
            w.ActivateNextStage(v);
            Assert.False(v.Pinned);
            PartInstance engine = v.Parts.First(p => p.Def.HasModule<EngineModuleDef>());
            Assert.True(engine.State.Activated);

            Run(w, 10);
            Assert.Equal(Situation.Flying, v.Situation);
            double climbed = v.Altitude - startAlt;
            Assert.True(climbed > 100, $"climbed only {climbed} m");
            Assert.True(v.Telemetry.VerticalSpeed > 20);
            EngineModuleDef e = engine.Def.GetModule<EngineModuleDef>();
            double expectedBurn = e.MaxMassFlow * 10;
            Assert.True(System.Math.Abs((m0 - v.Mass) - expectedBurn) < expectedBurn * 0.03, $"mass dropped {m0 - v.Mass}, expected ≈{expectedBurn}");

            // Cut the engine: proper acceleration ≈ drag only, vertical speed falls at ≈ g.
            v.Control.Throttle = 0;
            Run(w, 0.5);
            double vs0 = v.Telemetry.VerticalSpeed;
            Run(w, 2);
            double decel = (vs0 - v.Telemetry.VerticalSpeed) / 2;
            double g = v.Body.Mu / System.Math.Pow(v.Position.Length, 2);
            Assert.True(decel > g * 0.95 && decel < g * 1.4, $"deceleration {decel} m/s², g = {g}");
        }

        [Fact]
        public void ThrustDirection_FollowsVesselOrientation()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            w.PlaceInOrbit(v, _system.HomeBody, 200000);
            w.AddVessel(v);
            w.SetActive(v);
            // Point the nose along +X (inertial), radial at this point of the orbit.
            v.Rotation = QuaternionD.FromToRotation(Vector3d.UnitY, Vector3d.UnitX);
            v.AngularVelocity = Vector3d.Zero;
            Vector3d v0 = v.Velocity;
            v.Control.Throttle = 1;
            w.ActivateNextStage(v);
            Run(w, 2);
            Vector3d dv = v.Velocity - v0;
            // Remove the gravity contribution (analytic, ~ -μ/r² along r̂ = +X... gravity is along -X here).
            Vector3d gravityDv = v.Body.GravityAt(v.Position) * 2;
            Vector3d thrustDv = dv - gravityDv;
            Assert.True(Vector3d.Dot(thrustDv.Normalized(), Vector3d.UnitX) > 0.995, $"thrust Δv direction {thrustDv.Normalized()}");
        }

        [Fact]
        public void PhysicsIntegrator_KeepsCircularOrbitStable()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            w.PlaceInOrbit(v, _system.HomeBody, 100000);
            w.AddVessel(v);
            w.SetActive(v);
            Orbit o0 = v.CurrentOrbit(w.Time);
            // One full orbit (~33 min) of 50 Hz physics.
            Run(w, o0.Period);
            Orbit o1 = v.CurrentOrbit(w.Time);
            Assert.True(System.Math.Abs(o1.SemiMajorAxis - o0.SemiMajorAxis) < 1.0, $"SMA drift {o1.SemiMajorAxis - o0.SemiMajorAxis} m");
            Assert.True(System.Math.Abs(o1.ApoapsisRadius - o0.ApoapsisRadius) < 5 && System.Math.Abs(o1.PeriapsisRadius - o0.PeriapsisRadius) < 5);
            Assert.Equal(Situation.Orbiting, v.Situation);
        }

        [Fact]
        public void RailsTimeWarp_PreservesOrbitExactly()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            w.PlaceInOrbit(v, _system.HomeBody, 400000);
            w.AddVessel(v);
            w.SetActive(v);
            Run(w, 0.1);
            Orbit o0 = v.CurrentOrbit(w.Time);
            for (int i = 0; i < 7; i++) w.IncreaseWarp();
            Assert.True(w.Warp.IsRails);
            Assert.Equal(100000, w.Warp.Rate);
            // 200 frames at 100000× ≈ 2 million seconds (> 800 orbits).
            for (int i = 0; i < 200; i++) w.Update(0.1);
            Assert.True(w.Time > 1e6);
            Orbit o1 = v.CurrentOrbit(w.Time);
            Assert.True(System.Math.Abs(o1.SemiMajorAxis - o0.SemiMajorAxis) < 1e-3 * o0.SemiMajorAxis * 1e-3, $"SMA changed by {o1.SemiMajorAxis - o0.SemiMajorAxis} m");
            Assert.True(System.Math.Abs(o1.Eccentricity - o0.Eccentricity) < 1e-6);
            Assert.Contains(v, w.Vessels);
            w.StopWarp();
            Assert.False(v.OnRails);
            Run(w, 1);
            Assert.Equal(Situation.Orbiting, v.Situation);
        }

        [Fact]
        public void RailsWarp_IsRefusedInAtmosphereAndUnderThrust()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            w.PlaceInOrbit(v, _system.HomeBody, 30000); // inside the 70 km atmosphere
            w.AddVessel(v);
            w.SetActive(v);
            Assert.False(w.CanRailsWarp(1, out string reason));
            Assert.Contains("atmosphere", reason);
            w.IncreaseWarp();
            Assert.False(w.Warp.IsRails);
            Assert.True(w.Warp.IsPhysicsWarp, "physics warp is still allowed");
        }

        [Fact]
        public void RailsWarp_DropsOutBeforeEnteringTheAtmosphere()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            CelestialBody aster = _system.HomeBody;
            // Elliptical orbit dipping to 40 km.
            double rp = aster.Radius + 40000, ra = aster.Radius + 400000;
            double a = (rp + ra) / 2;
            v.Body = aster;
            v.Position = new Vector3d(ra, 0, 0);
            v.Velocity = new Vector3d(0, 0, -Orbit.VisViva(aster.Mu, ra, a));
            v.Rotation = QuaternionD.Identity;
            v.Pinned = false;
            w.AddVessel(v);
            w.SetActive(v);
            Run(w, 0.1);
            for (int i = 0; i < 4; i++) w.IncreaseWarp();
            Assert.True(w.Warp.IsRails);
            for (int i = 0; i < 400 && w.Warp.IsRails; i++) w.Update(0.1);
            Assert.False(w.Warp.IsRails);
            Assert.True(v.Altitude >= aster.AtmosphereHeight - 2000, $"warp should stop near the atmosphere, alt {v.Altitude}");
            Assert.Contains(v, w.Vessels);
        }

        [Fact]
        public void Staging_SeparatesStagesIntoIndependentVessels()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Below("tank.t200");
            b.Below("engine.swift");
            PartInstance dec = b.Below("decoupler.td1");
            b.Below("tank.t400");
            b.Below("engine.swift");
            VesselDesign d = b.Finish();
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(d);
            w.PlaceInOrbit(v, _system.HomeBody, 200000);
            w.AddVessel(v);
            w.SetActive(v);
            int partCount = v.Parts.Count;
            double totalMass = v.Mass;
            Vector3d momentum = v.Velocity * v.Mass;
            w.ActivateNextStage(v); // lower engine
            Assert.Single(w.Vessels);
            w.ActivateNextStage(v); // decouple + upper engine
            Assert.Equal(2, w.Vessels.Count);
            Vessel lower = w.Vessels.First(x => x != v);
            Assert.Equal(partCount, v.Parts.Count + lower.Parts.Count);
            Assert.Contains(lower.Parts, p => p.Id == dec.Id); // decoupler goes with the lower stage
            Assert.Equal(VesselType.Debris, lower.Type);
            Assert.Equal(VesselType.Ship, v.Type);
            Assert.Equal(totalMass, v.Mass + lower.Mass, 6);
            // Equal and opposite separation impulse: momentum conserved, stages move apart.
            Vector3d after = v.Velocity * v.Mass + lower.Velocity * lower.Mass;
            Assert.True((after - momentum).Length < 1e-6 * momentum.Length);
            Vector3d rel = v.Velocity - lower.Velocity;
            Vector3d sep = v.Position - lower.Position;
            Assert.True(Vector3d.Dot(rel, sep) > 0, "stages must separate");
            Assert.True(v.Parts.First(p => p.Def.HasModule<EngineModuleDef>()).State.Activated);
            Run(w, 5);
            Assert.True((v.Position - lower.Position).Length > 2);
        }

        [Fact]
        public void HardImpact_DestroysParts_SoftLandingSettles()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Below("heatshield.h1");
            VesselDesign d = b.Finish();

            foreach (var (speed, survive) in new[] { (3.0, true), (60.0, false) })
            {
                var w = new FlightWorld(_system, _catalog);
                Vessel v = w.Launch(d);
                w.ActivateNextStage(v);
                v.Pinned = false;
                v.Situation = Situation.Flying;
                Vector3d up = v.Position.Normalized();
                v.Position += up * 3;
                v.Velocity = v.Body.SurfaceVelocity(v.Position) - up * speed;
                Run(w, 6);
                bool intact = w.Vessels.Any(x => x.Parts.Count == 2);
                Assert.Equal(survive, intact);
                if (survive)
                {
                    Assert.True(v.Situation is Situation.Landed or Situation.Splashed);
                    Assert.True(v.SurfaceVelocity.Length < 0.2);
                }
            }
        }

        [Fact]
        public void Parachute_ReachesTerminalVelocity()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Above("chute.petal", b.Last);
            b.Below("heatshield.h1", b.Design.Parts[0]);
            VesselDesign d = b.Finish();
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(d);
            CelestialBody aster = _system.HomeBody;
            Vector3d up = CelestialBody.DirectionFromLatLon(0, 0);
            v.Body = aster;
            v.Position = up * (aster.Radius + 3000);
            v.Velocity = aster.SurfaceVelocity(v.Position) - up * 60;
            v.Rotation = QuaternionD.FromToRotation(Vector3d.UnitY, up);
            v.Pinned = false;
            v.Situation = Situation.Flying;
            w.AddVessel(v);
            w.SetActive(v);
            w.ActivateNextStage(v); // arm parachute
            Run(w, 95);
            PartInstance chute = v.Parts.First(p => p.Def.HasModule<ParachuteModuleDef>());
            Assert.True(chute.State.Parachute == ParachuteState.Deployed, $"chute {chute.State.Parachute}, AGL {v.Telemetry.AltitudeTerrain:F0} m, vs {v.Telemetry.VerticalSpeed:F1}, stage {v.CurrentStage}, parts {v.Parts.Count}, vessels {w.Vessels.Count}, sit {v.Situation}");
            double alt = v.Altitude;
            double rho = aster.Atmosphere.Density(alt);
            double g = aster.Mu / System.Math.Pow(v.Position.Length, 2);
            double cdA = chute.Def.GetModule<ParachuteModuleDef>().DeployedDragArea;
            double terminal = System.Math.Sqrt(2 * v.Mass * g / (rho * cdA));
            double descent = -v.Telemetry.VerticalSpeed;
            Assert.True(descent < terminal * 1.3 && descent > terminal * 0.7, $"descent {descent} m/s vs analytic terminal {terminal} m/s");
        }

        [Fact]
        public void Sas_TurnsVesselToPrograde()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            w.PlaceInOrbit(v, _system.HomeBody, 200000);
            w.AddVessel(v);
            w.SetActive(v);
            v.Rotation = QuaternionD.FromToRotation(Vector3d.UnitY, v.Position.Normalized()); // radial out
            v.Control.SasEnabled = true;
            v.Control.SasMode = SasMode.Prograde;
            v.Control.SpeedReference = SpeedReference.Orbit;
            Run(w, 60);
            double err = Vector3d.Angle(v.Forward, v.Velocity.Normalized()) * MathUtil.Rad2Deg;
            Assert.True(err < 3, $"pointing error {err}°");
            Assert.True(v.AngularVelocity.Length < 0.05);
        }

        [Fact]
        public void SoiTransition_IsPredictedAndExecuted()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.CreateFromDesign(SimpleRocket());
            CelestialBody aster = _system.HomeBody, tessar = _system.Get("tessar");
            // Hohmann-like transfer from 100 km to Tessar's orbit, timed so Tessar is at apoapsis.
            double r1 = aster.Radius + 100000, r2 = tessar.Orbit.SemiMajorAxis;
            double a = (r1 + r2) / 2;
            double transfer = System.Math.PI * System.Math.Sqrt(a * a * a / aster.Mu);
            Vector3d tessarAtArrival = tessar.PositionRelativeToParent(transfer);
            Vector3d dir = -tessarAtArrival.Normalized(); // depart opposite to where Tessar will be
            Vector3d normal = Vector3d.UnitY;
            Vector3d velDir = Vector3d.Cross(normal, dir).Normalized(); // prograde for +Y angular momentum
            v.Body = aster;
            v.Position = dir * r1;
            v.Velocity = velDir * Orbit.VisViva(aster.Mu, r1, a);
            v.Pinned = false;
            w.AddVessel(v);
            w.SetActive(v);

            var patches = w.PredictTrajectory(v);
            Assert.True(patches.Count >= 2, "an encounter should be predicted");
            Assert.Equal(PatchEnd.Encounter, patches[0].End);
            Assert.Equal(tessar, patches[0].NextBody);
            double tEnc = patches[0].EndTime;

            // Absolute position must be continuous across the SOI change.
            for (int i = 0; i < 7; i++) w.IncreaseWarp();
            Vector3d lastAbs = v.Position + v.Body.AbsolutePosition(w.Time);
            while (w.Time < tEnc + 600 && v.Body == aster) w.Update(0.05);
            Assert.Equal(tessar, v.Body);
            Assert.True(System.Math.Abs(w.Time - tEnc) < 700, $"transition at {w.Time}, predicted {tEnc}");
            Vector3d abs = v.Position + v.Body.AbsolutePosition(w.Time);
            Assert.True((abs - (v.Body.AbsolutePosition(w.Time) + v.Position)).Length < 1e-6);
            Assert.True(v.Position.Length <= tessar.SphereOfInfluence * 1.01);
            _ = lastAbs;
        }

        [Fact]
        public void Docking_MergesVesselsAndConservesMomentum()
        {
            // Vessel A: probe core with a docking port on top (port's "top" face free, pointing +Y).
            var b1 = new DesignBuilder(_catalog, "probe.sprite");
            b1.Above("dock.port", b1.Last);
            VesselDesign d1 = b1.Finish();
            // Vessel B: a docking port with a tank below it.
            var b2 = new DesignBuilder(_catalog, "dock.port");
            b2.Below("tank.t200");
            VesselDesign d2 = b2.Finish();

            var w = new FlightWorld(_system, _catalog);
            Vessel a = w.CreateFromDesign(d1);
            w.PlaceInOrbit(a, _system.HomeBody, 200000);
            a.Rotation = QuaternionD.Identity;
            w.AddVessel(a);
            w.SetActive(a);
            Vessel c = w.CreateFromDesign(d2);
            PartInstance pa = a.Parts.First(p => p.Def.HasModule<DockingPortModuleDef>());
            PartInstance pc = c.Parts.First(p => p.Def.HasModule<DockingPortModuleDef>());
            // Flip B so its port faces A's port, 0.4 m above it, closing at 0.3 m/s.
            c.Body = a.Body;
            c.Rotation = QuaternionD.FromAxisAngle(Vector3d.UnitX, System.Math.PI);
            Vector3d faceA = a.VesselToWorld(pa.LocalToVessel(pa.Def.GetNode("top").Position));
            Vector3d target = faceA + Vector3d.UnitY * 0.4;
            c.Position = target - c.Rotation.Rotate(pc.LocalToVessel(pc.Def.GetNode("top").Position) - c.CenterOfMassLocal);
            c.Velocity = a.Velocity - Vector3d.UnitY * 0.3;
            c.Pinned = false;
            w.AddVessel(c);
            int totalParts = a.Parts.Count + c.Parts.Count;
            double totalMass = a.Mass + c.Mass;

            // Direct merge conserves linear momentum exactly.
            Vector3d p0 = a.Velocity * a.Mass + c.Velocity * c.Mass;
            var w2 = new FlightWorld(_system, _catalog);
            Vessel a2 = w2.CreateFromDesign(d1), c2 = w2.CreateFromDesign(d2);
            foreach (var (src, dst) in new[] { (a, a2), (c, c2) })
            {
                dst.Body = src.Body; dst.Position = src.Position; dst.Velocity = src.Velocity; dst.Rotation = src.Rotation; dst.Pinned = false;
                w2.AddVessel(dst);
            }
            w2.SetActive(a2);
            w2.Dock(a2, a2.Parts.First(p => p.Def.HasModule<DockingPortModuleDef>()), c2, c2.Parts.First(p => p.Def.HasModule<DockingPortModuleDef>()));
            Assert.Single(w2.Vessels);
            Assert.True((a2.Velocity * a2.Mass - p0).Length < 1e-6 * p0.Length);

            // Approaching vessels dock automatically once within capture range.
            for (int i = 0; i < 200 && w.Vessels.Count > 1; i++) w.StepPhysics(FlightWorld.PhysicsStep);
            Assert.Single(w.Vessels);
            Assert.Equal(totalParts, a.Parts.Count);
            Assert.Equal(totalMass, a.Mass, 3);
            Assert.Contains(a.Parts, p => p.Part == "tank.t200");

            // Undocking splits them again.
            Vessel undocked = w.Undock(a, a.Parts.First(p => p.Id == pa.Id));
            Assert.NotNull(undocked);
            Assert.Equal(2, w.Vessels.Count);
        }
    }
}
