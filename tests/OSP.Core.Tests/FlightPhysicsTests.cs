using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Core.Tests;

public class FlightPhysicsTests
{
    private static readonly PartCatalog Cat = TestEnv.Catalog;

    /// <summary>A craft floating in deep space (Lumen frame, far from the surface) for clean propulsion tests.</summary>
    private static (FlightSimulation sim, Craft craft) SpaceCraft(VehicleDesign d, Vec3d velocity)
    {
        var sys = TestEnv.NewSystem();
        var sim = new FlightSimulation(sys, Cat);
        var craft = Craft.FromDesign(d, Cat);
        craft.Body = sys.ById("lumen");
        craft.Position = new Vec3d(0, 1.5e6, 0);   // 1500 km out, inside Lumen's SOI
        craft.Velocity = velocity;
        craft.Mode = CraftMode.Physics;
        craft.Control.SasEnabled = false;
        craft.UpdateMassPropertiesFresh();
        sim.AddCraft(craft, true);
        craft.HasLaunched = true;
        return (sim, craft);
    }

    [Fact]
    public void EngineConsumesPropellantAtMassFlowAndMassDrops()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s4").Below("eng_swift").Design;
        var (sim, craft) = SpaceCraft(d, new Vec3d(100, 0, 0));
        double m0 = craft.TotalMass, prop0 = craft.ResourceTotal("Propellant");
        sim.ActivateStage();
        craft.Control.Throttle = 1;
        for (int i = 0; i < 500; i++) sim.StepFixed(0.02);       // 10 s
        var e = Cat.Get("eng_swift").Engine!;
        double expected = e.ThrustVac / (e.IspVac * MathUtil.G0) * 10.0;     // mdot = F / (Isp g0)
        Assert.Equal(expected, prop0 - craft.ResourceTotal("Propellant"), 3);
        Assert.Equal(expected, m0 - craft.TotalMass, 3);
    }

    [Fact]
    public void HalfThrottleHalvesFuelFlow()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s4").Below("eng_swift").Design;
        var (sim, craft) = SpaceCraft(d, Vec3d.Zero);
        sim.ActivateStage(); craft.Control.Throttle = 0.5;
        double p0 = craft.ResourceTotal("Propellant");
        for (int i = 0; i < 250; i++) sim.StepFixed(0.02);
        var e = Cat.Get("eng_swift").Engine!;
        Assert.Equal(0.5 * e.MaxMassFlow * 5.0, p0 - craft.ResourceTotal("Propellant"), 3);
    }

    [Fact]
    public void ThrustAccelerationFollowsNoseDirectionAndRocketEquation()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s2").Below("eng_whisper").Design;
        var (sim, craft) = SpaceCraft(d, Vec3d.Zero);
        // Point the nose along a skewed direction.
        var dir = new Vec3d(1, 2, -0.5).Normalized;
        craft.Orientation = QuatD.FromTo(Vec3d.UnitY, dir);
        sim.ActivateStage(); craft.Control.Throttle = 1;
        craft.Control.SasEnabled = true; craft.Control.HoldOrientation = craft.Orientation; craft.Control.HoldValid = true;
        double m0 = craft.TotalMass; var v0 = craft.Velocity;
        double T = 10;
        var mu = craft.Body.Mu;
        // accumulate gravity impulse using the same positions the craft passes through
        var gravDv = Vec3d.Zero;
        for (int i = 0; i < (int)(T / 0.02); i++) { gravDv += OrbitMath.GravityAcceleration(mu, craft.Position) * 0.02; sim.StepFixed(0.02); }
        var dv = craft.Velocity - v0 - gravDv;
        double m1 = craft.TotalMass;
        var e = Cat.Get("eng_whisper").Engine!;
        double expected = e.IspVac * MathUtil.G0 * Math.Log(m0 / m1);
        Assert.Equal(expected, dv.Length, 0);
        Assert.True(Vec3d.Dot(dv.Normalized, dir) > 0.999, $"thrust direction cosine {Vec3d.Dot(dv.Normalized, dir)}");
    }

    [Fact]
    public void FlameoutWhenPropellantRunsOut()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s1").Below("eng_ember").Design;
        var (sim, craft) = SpaceCraft(d, Vec3d.Zero);
        sim.ActivateStage(); craft.Control.Throttle = 1;
        for (int i = 0; i < 100 * 50; i++) sim.StepFixed(0.02);
        Assert.True(craft.Parts.First(p => p.Def.Engine != null).FlamedOut);
        Assert.True(craft.ResourceTotal("Propellant") < 1e-6);
        Assert.Equal(0, craft.LastTotalThrust);
    }

    [Fact]
    public void ReactionWheelsRotateTheCraftAndSasStopsTheRotation()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s2").Design;
        var (sim, craft) = SpaceCraft(d, Vec3d.Zero);
        craft.Control.SasEnabled = true; craft.Control.HoldOrientation = craft.Orientation; craft.Control.HoldValid = true;
        craft.Control.Pitch = 1;
        for (int i = 0; i < 100; i++) sim.StepFixed(0.02);
        Assert.True(craft.AngularVelocity.X > 0.01, "pitch input produces rotation about +X");
        craft.Control.Pitch = 0;
        for (int i = 0; i < 100 * 40; i++) sim.StepFixed(0.02);
        Assert.True(craft.AngularVelocity.Length < 0.02, $"SAS should arrest the rotation, w={craft.AngularVelocity.Length}");
    }

    [Fact]
    public void SasProgradeAlignsNoseWithVelocity()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s2").Below("rw_unit").Design;
        var (sim, craft) = SpaceCraft(d, new Vec3d(300, 0, 400));
        craft.Control.SasEnabled = true; craft.Control.Sas = SasMode.Prograde;
        for (int i = 0; i < 100 * 40; i++) sim.StepFixed(0.02);
        double cos = Vec3d.Dot(craft.Orientation.Rotate(Vec3d.UnitY), craft.Velocity.Normalized);
        Assert.True(cos > 0.999, $"cos={cos}");
    }

    [Fact]
    public void RcsTranslatesAlongRequestedAxis()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_mono").Design;
        var ed = new VehicleEditor(Cat);
        ed.Attach(d, "rcs_block", ed.RadialCandidate(d, d.Parts[1].Id, "rcs_block", 0, 0)!, 4);
        var (sim, craft) = SpaceCraft(d, Vec3d.Zero);
        craft.Control.RcsEnabled = true; craft.Control.SasEnabled = true;
        craft.Control.HoldOrientation = craft.Orientation; craft.Control.HoldValid = true;
        craft.Control.Translation = new Vec3d(0, 0, 1);      // +Z (heading)
        double mono0 = craft.ResourceTotal("Monopropellant");
        for (int i = 0; i < 100; i++) sim.StepFixed(0.02);
        Assert.True(craft.Velocity.Z > 0.2, $"vz={craft.Velocity.Z}");
        Assert.True(Math.Abs(craft.Velocity.X) < Math.Abs(craft.Velocity.Z));
        Assert.True(craft.ResourceTotal("Monopropellant") < mono0);
    }

    [Fact]
    public void StructuralFailureSeparatesOverloadedJoint()
    {
        // A copy of the Ember engine with an absurdly weak joint breaks away under thrust.
        var files = Directory.GetFiles(Path.Combine(TestEnv.DataDir, "parts"), "*.json").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        var cat = PartCatalog.FromJson(files);
        var weak = Json.Deserialize<PartDefinition>(Json.Serialize(cat.Get("eng_ember")), "clone");
        weak.Id = "eng_weak"; weak.JointStrength = 5_000;
        cat.Add(weak);
        var ed = new VehicleEditor(cat);
        var d = new VehicleDesign(); ed.SetRoot(d, "pod_aster");
        var t = ed.FindNodeCandidates(d, "tank_s2").First(); ed.Attach(d, "tank_s2", t);
        var e = ed.FindNodeCandidates(d, "eng_weak").First(c => c.ParentId == d.Parts[1].Id && c.ParentNode == "bottom"); ed.Attach(d, "eng_weak", e);

        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, cat);
        var craft = Craft.FromDesign(d, cat);
        craft.Body = sys.ById("lumen"); craft.Position = new Vec3d(0, 1.5e6, 0); craft.Mode = CraftMode.Physics;
        craft.UpdateMassPropertiesFresh(); craft.HasLaunched = true; sim.AddCraft(craft, true);
        sim.ActivateStage(); craft.Control.Throttle = 1;
        for (int i = 0; i < 100; i++) sim.StepFixed(0.02);
        Assert.Contains(sim.EventLog, ev => ev.Kind == SimEventKind.JointFailed);
        Assert.DoesNotContain(craft.Parts, p => p.Def.Id == "eng_weak");
        Assert.True(sim.Crafts.Count >= 2);
    }

    [Fact]
    public void NormalRocketDoesNotBreakApartDuringAscent()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        var ap = new Autopilot(sim, craft);
        sim.ActivateStage();
        for (int i = 0; i < 100 * 80; i++) { ap.Update(0.02); sim.StepFixed(0.02); }
        Assert.DoesNotContain(sim.EventLog, e => e.Kind == SimEventKind.JointFailed || e.Kind == SimEventKind.PartDestroyed);
    }

    // ------------------------------------------------------------------ surface
    [Fact]
    public void RocketSitsOnPadWithoutDriftingAndLocksToSurface()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        double alt0 = craft.Body.AltitudeAboveTerrain(craft.Position, sim.Time);
        for (int i = 0; i < 100 * 20; i++) sim.StepFixed(0.02);
        Assert.Equal(CraftMode.Landed, craft.Mode);
        double alt1 = craft.Body.AltitudeAboveTerrain(craft.Position, sim.Time);
        Assert.Equal(alt0, alt1, 3);
        // co-rotating with the surface: inertial speed = omega * r
        double expected = sys.Home.AngularSpeed * craft.Position.Length;
        Assert.Equal(expected, craft.Velocity.Length, 3);
    }

    private static Craft Lander(FlightSimulation sim, bool legs, double dropHeight, double downSpeed)
    {
        var sys = sim.System;
        var b = new RocketBuilder().Root("pod_aster").Below("tank_s1").Below("eng_wren");
        if (legs) b.Radial("leg_strut", b.Design.Parts[1].Id, 3, 0, -0.5);
        var craft = sim.Launch(b.Design, sys.Home, sys.Home.LaunchSites[0]);
        sim.Time += 0;
        craft.Mode = CraftMode.Physics;
        var up = craft.Position.Normalized;
        craft.Position += up * dropHeight;
        craft.Velocity = sys.Home.SurfaceVelocityAt(craft.Position) - up * downSpeed;
        craft.Control.SasEnabled = false;
        craft.HasLaunched = true;
        if (legs) { sim.SetGear(true); foreach (var p in craft.Parts) p.LegDeploy = 1; }
        return craft;
    }

    [Fact]
    public void SoftLandingOnLegsComesToRest()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = Lander(sim, true, 0.3, 2.0);
        for (int i = 0; i < 100 * 30; i++) sim.StepFixed(0.02);
        Assert.DoesNotContain(sim.EventLog, e => e.Kind == SimEventKind.PartDestroyed);
        Assert.Equal(CraftMode.Landed, craft.Mode);
        Assert.Contains(sim.EventLog, e => e.Kind == SimEventKind.Touchdown);
        // upright
        Assert.True(Vec3d.Dot(craft.Orientation.Rotate(Vec3d.UnitY), craft.Position.Normalized) > 0.95);
    }

    [Fact]
    public void HighSpeedImpactDestroysParts()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = Lander(sim, false, 30.0, 60.0);
        for (int i = 0; i < 100 * 5; i++) sim.StepFixed(0.02);
        Assert.Contains(sim.EventLog, e => e.Kind == SimEventKind.PartDestroyed);
    }

    [Fact]
    public void ParachuteLimitsDescentToAnalyticTerminalVelocity()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var d = new RocketBuilder().Root("pod_aster").Above("chute_mk1", null, "bottom").Design;
        var craft = sim.Launch(d, sys.Home, sys.Home.LaunchSites[0]);
        craft.Mode = CraftMode.Physics; craft.Control.SasEnabled = false;
        var up = craft.Position.Normalized;
        craft.Position += up * 4000;      // plenty of room; atmosphere density ~0.6 kg/m3 -> check against local density below
        craft.Velocity = sys.Home.SurfaceVelocityAt(craft.Position);
        craft.HasLaunched = true;
        var chute = craft.Parts.First(p => p.Def.Parachute != null);
        chute.Chute = ChuteState.Deployed; chute.ChuteProgress = 1;
        for (int i = 0; i < 100 * 15 && craft.InContact == false; i++) sim.StepFixed(0.02);
        // terminal velocity where net force ~0 : v = sqrt(2 m g / (rho CdA))
        var env = FlightEnvironment.Compute(sys, craft.Body, sim.Time, craft.Position, craft.Velocity);
        double m = craft.TotalMass, g = craft.Body.Mu / craft.Position.LengthSquared;
        double cda = Cat.Get("chute_mk1").Parachute!.FullDragArea + 0.2;     // plus small body drag
        double vTerm = Math.Sqrt(2 * m * g / (env.Density * cda));
        double vNow = env.SurfaceVelocity.Length;
        Assert.True(Math.Abs(vNow - vTerm) / vTerm < 0.12, $"v={vNow:F2} expected~{vTerm:F2}");
    }

    [Fact]
    public void AtmosphereFollowsExponentialModel()
    {
        var atm = TestEnv.Aldara.Atmosphere!;
        Assert.Equal(101325, atm.Pressure(0), 6);
        Assert.Equal(101325 * Math.Exp(-1), atm.Pressure(atm.ScaleHeight), 3);
        Assert.Equal(0, atm.Density(atm.Height + 1));
        Assert.True(atm.Density(10000) < atm.Density(5000));
    }

    [Fact]
    public void AerodynamicTorqueRestoresNoseToFlowWithFins()
    {
        // In dense air a finned rocket flying sideways to its velocity is rotated back toward the flow.
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s4").Below("eng_ember").Design;
        int tank = d.Parts[1].Id;
        new RocketBuilder();
        var ed = new VehicleEditor(Cat);
        ed.Attach(d, "fin_delta", ed.RadialCandidate(d, tank, "fin_delta", 0, -1.4)!, 4);
        var craft = sim.Launch(d, sys.Home, sys.Home.LaunchSites[0]);
        craft.Mode = CraftMode.Physics; craft.Control.SasEnabled = false;
        var up = craft.Position.Normalized;
        craft.Position += up * 2000;
        var east = Vec3d.Cross(Vec3d.UnitY, up).Normalized;
        craft.Velocity = sys.Home.SurfaceVelocityAt(craft.Position) + east * 250;
        craft.Orientation = QuatD.FromTo(Vec3d.UnitY, (east * Math.Sin(0.3) + up * Math.Cos(0.3)));   // tilted up out of the flow
        craft.HasLaunched = true;
        double Angle() => Vec3d.Angle(craft.Orientation.Rotate(Vec3d.UnitY), craft.Velocity - sys.Home.SurfaceVelocityAt(craft.Position));
        double a0 = Angle();
        double aMin = a0;
        for (int i = 0; i < 100; i++) { sim.StepFixed(0.02); aMin = Math.Min(aMin, Angle()); }
        Assert.True(Math.Abs(a0 - 1.27) < 0.05);       // 90 - 17.2 degrees
        Assert.True(aMin < a0 - 0.05, $"angle of attack should shrink: {a0} -> min {aMin}");
    }

    [Fact]
    public void SolarPanelsChargeBatteriesOnlyWhenDeployedAndSunlit()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var d = new RocketBuilder().Root("probe_wisp").Below("battery_l").Design;
        var ed = new VehicleEditor(Cat);
        ed.Attach(d, "solar_flat", ed.RadialCandidate(d, d.Parts[1].Id, "solar_flat", 0, 0)!, 2);
        var (s2, craft) = SpaceCraft(d, Vec3d.Zero);
        // find a sunlit position: Lumen frame, on the sun side of Lumen
        var (lumenRoot, _) = s2.System.StateInRoot(craft.Body, 0);
        var sunDir = (-lumenRoot).Normalized;
        craft.Position = sunDir * 1.0e6;
        foreach (var p in craft.Parts) p.Resources["ElectricCharge"] = 0;
        for (int i = 0; i < 100; i++) s2.StepFixed(0.02);
        Assert.Equal(0, craft.ResourceTotal("ElectricCharge"), 6);       // not deployed -> only avionics drain
        s2.SetPanels(true);
        for (int i = 0; i < 500; i++) s2.StepFixed(0.02);
        Assert.True(craft.ResourceTotal("ElectricCharge") > 5, $"charge={craft.ResourceTotal("ElectricCharge")}");
        // behind the moon: no charge
        craft.Position = -sunDir * 1.0e6 * 0.5 + new Vec3d(0, 0, 0);
        craft.Position = -sunDir * 250_000;
        double c0 = craft.ResourceTotal("ElectricCharge");
        for (int i = 0; i < 100; i++) s2.StepFixed(0.02);
        Assert.True(craft.ResourceTotal("ElectricCharge") <= c0 + 1e-9);
    }

    // ------------------------------------------------------------------ SoI and warp
    [Fact]
    public void SphereOfInfluenceTransitionPreservesInertialState()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var al = sys.Home; var lu = sys.ById("lumen");
        var d = new RocketBuilder().Root("probe_wisp").Design;
        var craft = Craft.FromDesign(d, Cat); craft.Body = al; craft.Mode = CraftMode.Rails; craft.UpdateMassPropertiesFresh();
        var (mr, mv) = sys.StateRelativeToParent(lu, 0);
        craft.Position = mr + new Vec3d(lu.SphereOfInfluence * 0.9, 0, 0); craft.Velocity = mv + new Vec3d(0, 0, 100);
        sim.AddCraft(craft, true);
        var (rootBefore, vBefore) = sys.ConvertFrame(craft.Position, craft.Velocity, al, sys.Root, 0);
        sim.StepFixed(0.02);
        Assert.Same(lu, craft.Body);
        var (rootAfter, vAfter) = sys.ConvertFrame(craft.Position, craft.Velocity, lu, sys.Root, sim.Time);
        var expected = rootBefore + vBefore * sim.Time;      // free motion over the elapsed step
        Assert.True((rootAfter - expected).Length < 5, $"{(rootAfter - expected).Length}");
        Assert.True((vAfter - vBefore).Length < 1);
        // and back out again
        craft.Position = new Vec3d(lu.SphereOfInfluence * 1.01, 0, 0);
        sim.StepFixed(0.02);
        Assert.Same(al, craft.Body);
    }

    [Fact]
    public void TimeWarpKeepsOrbitStableAtEveryRailsLevel()
    {
        var sys = TestEnv.NewSystem(); var al = sys.Home;
        for (int level = 4; level < FlightSimulation.WarpLevels.Length; level++)
        {
            var sim = new FlightSimulation(sys, Cat);
            var d = new RocketBuilder().Root("probe_wisp").Design;
            var craft = Craft.FromDesign(d, Cat); craft.Body = al; craft.Mode = CraftMode.Physics; craft.UpdateMassPropertiesFresh();
            double r = al.Radius + 120_000;
            craft.Position = new Vec3d(r, 0, 0); craft.Velocity = new Vec3d(0, 0, -OrbitMath.CircularSpeed(al.Mu, r) * 1.05);
            sim.AddCraft(craft, true);
            craft.HasLaunched = true;
            var o0 = Orbit.FromState(craft.Position, craft.Velocity, al.Mu, 0);
            sim.SetWarp(level);
            Assert.Equal(level, sim.WarpIndex);
            double t0 = sim.Time;
            for (int f = 0; f < 600; f++) sim.Advance(1.0 / 60.0);
            var o1 = Orbit.FromState(craft.Position, craft.Velocity, al.Mu, sim.Time);
            Assert.False(craft.IsDestroyed);
            Assert.Equal(o0.SemiMajorAxis, o1.SemiMajorAxis, o0.SemiMajorAxis * 1e-8);
            Assert.True(Math.Abs(o0.Eccentricity - o1.Eccentricity) < 1e-9);
            double expectedElapsed = 10.0 * FlightSimulation.WarpLevels[level].Factor;
            Assert.True(Math.Abs((sim.Time - t0) - expectedElapsed) / expectedElapsed < 0.01, $"warp {level}: {(sim.Time - t0)} vs {expectedElapsed}");
        }
    }

    [Fact]
    public void PhysicsWarpKeepsEnergyDriftSmall()
    {
        var sys = TestEnv.NewSystem(); var al = sys.Home;
        var sim = new FlightSimulation(sys, Cat);
        var craft = Craft.FromDesign(new RocketBuilder().Root("probe_wisp").Design, Cat);
        craft.Body = al; craft.Mode = CraftMode.Physics; craft.UpdateMassPropertiesFresh();
        double r = al.Radius + 100_000;
        craft.Position = new Vec3d(r, 0, 0); craft.Velocity = new Vec3d(0, 0, -OrbitMath.CircularSpeed(al.Mu, r));
        sim.AddCraft(craft, true); craft.HasLaunched = true; craft.Control.SasEnabled = false;
        double E(Craft c) => c.Velocity.LengthSquared / 2 - al.Mu / c.Position.Length;
        double e0 = E(craft);
        sim.SetWarp(3);
        // Atmosphere top is 70 km; 100 km orbit stays outside, so we can integrate a full revolution in physics mode.
        for (int f = 0; f < 60 * 400; f++) sim.Advance(1.0 / 60.0);       // 400 s real x 4 = 1600 s ~ 0.84 orbits ...
        Assert.True(sim.Time > 1500);
        Assert.True(Math.Abs((E(craft) - e0) / e0) < 1e-5, $"energy drift {(E(craft) - e0) / e0}");
    }

    [Fact]
    public void WarpIsRefusedWhileEnginesBurnOrInAtmosphere()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        // landed: warp is allowed
        Assert.True(sim.SetWarp(7));
        sim.StopWarp();
        sim.ActivateStage(); craft.Control.Throttle = 1;
        for (int i = 0; i < 100; i++) sim.StepFixed(0.02);
        Assert.False(sim.SetWarp(7));
        Assert.True(sim.WarpIndex <= 3);
        Assert.NotNull(sim.WarpLimitReason);
    }

    [Fact]
    public void RailsWarpStopsBeforeHitchingTheAtmosphere()
    {
        var sys = TestEnv.NewSystem(); var al = sys.Home;
        var sim = new FlightSimulation(sys, Cat);
        var craft = Craft.FromDesign(new RocketBuilder().Root("probe_wisp").Design, Cat);
        craft.Body = al; craft.Mode = CraftMode.Physics; craft.UpdateMassPropertiesFresh();
        double r = al.Radius + 300_000;
        var v = OrbitMath.VisViva(al.Mu, r, (r + al.Radius + 20_000) / 2);          // periapsis 20 km: deep in the atmosphere
        craft.Position = new Vec3d(r, 0, 0); craft.Velocity = new Vec3d(0, 0, -v);
        sim.AddCraft(craft, true); craft.HasLaunched = true;
        sim.SetWarp(10);
        for (int f = 0; f < 60 * 120 && sim.WarpIndex > 0; f++) sim.Advance(1.0 / 60.0);
        Assert.Equal(0, sim.WarpIndex);
        double alt = craft.Position.Length - al.Radius;
        Assert.True(alt > al.Atmosphere!.Height, $"warp must stop outside the atmosphere, alt={alt}");
    }

    // ------------------------------------------------------------------ terrain
    [Fact]
    public void TerrainIsDeterministicBoundedAndFlatAtLaunchSite()
    {
        var sys = TestEnv.NewSystem(); var al = sys.Home; var site = al.LaunchSites[0];
        var dir = CelestialBody.DirectionFromLatLon(site.LatitudeDeg * MathUtil.Deg2Rad, site.LongitudeDeg * MathUtil.Deg2Rad);
        Assert.Equal(site.Height, al.SurfaceHeightAt(dir), 6);
        var sys2 = TestEnv.NewSystem();
        var rng = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vec3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized;
            double h = al.SurfaceHeightAt(p);
            Assert.Equal(h, sys2.Home.SurfaceHeightAt(p), 9);
            Assert.InRange(h, 0, al.Terrain!.Amplitude * 1.6);      // ocean clamps to 0
        }
        // pad is level: heights within 1500 m of the site vary by < 1 m
        for (int i = 0; i < 20; i++)
        {
            double ang = i * 0.3; double dist = 1000.0 * (i % 5) / 5.0;
            var local = (dir + Vec3d.Cross(dir, Vec3d.UnitY).Normalized * Math.Cos(ang) * dist / al.Radius + Vec3d.UnitY * Math.Sin(ang) * dist / al.Radius).Normalized;
            Assert.InRange(al.SurfaceHeightAt(local), site.Height - 1, site.Height + 1);
        }
    }
}
