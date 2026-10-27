using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OSP.Core.Tests;

public class FlightIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public FlightIntegrationTests(ITestOutputHelper o) { _out = o; }

    public static VehicleDesign TwoStageRocket()
    {
        var b = new RocketBuilder().Root("pod_aster");
        b.Below("rw_unit").Below("tank_s2").Below("eng_whisper");
        b.Below("dec_s").Below("tank_s4").Below("tank_s4").Below("eng_swift");
        // three stabiliser fins on the lower tank
        var d = b.Design;
        int lowerTank = d.Parts.Last(p => p.PartId == "tank_s4").Id;
        b.Radial("fin_delta", lowerTank, 3, 90, -1.0);
        b.Design.Name = "Pathfinder-1";
        return b.Design;
    }

    [Fact]
    public void TwoStageRocketStatsAreSane()
    {
        var d = TwoStageRocket();
        var st = VehicleAnalysis.Analyze(d, TestEnv.Catalog);
        _out.WriteLine($"mass {st.WetMass:F0} kg, TWR {st.LaunchTwr:F2}, dv vac {st.DeltaVVacuum:F0}, dv atm {st.DeltaVAtmosphere:F0}");
        foreach (var s in st.StagesVacuum) _out.WriteLine($"stage {s.Stage}: m0={s.StartMass:F0} dv={s.DeltaV:F0} t={s.BurnTime:F0}s twr={s.Twr:F2}");
        foreach (var p in d.Parts) _out.WriteLine($"{p.Id} {p.PartId} stage={p.Stage}");
        Assert.Equal(2, st.StagesVacuum.Count);
        Assert.True(st.DeltaVVacuum > 4500);
        Assert.True(st.LaunchTwr > 1.2);
    }

    [Fact]
    public void RocketReachesStableOrbit()
    {
        var sys = TestEnv.NewSystem();
        var sim = new FlightSimulation(sys, TestEnv.Catalog);
        var site = sys.Home.LaunchSites[0];
        var craft = sim.Launch(TwoStageRocket(), sys.Home, site);
        var ap = new Autopilot(sim, craft);
        sim.ActivateStage();     // ignite first stage
        double dt = 0.02; double maxAlt = 0; double maxQ = 0;
        int steps = 0;
        while (sim.Time < 2400 && ap.Current != Autopilot.Phase.Done)
        {
            ap.Update(dt);
            sim.StepFixed(dt);
            if (craft.IsDestroyed) break;
            var env = FlightEnvironment.Compute(sys, craft.Body, sim.Time, craft.Position, craft.Velocity, false);
            maxAlt = Math.Max(maxAlt, env.AltitudeSeaLevel); maxQ = Math.Max(maxQ, craft.LastDynamicPressure);
            if (steps++ % 1500 == 0)
            {
                var o = Orbit.FromState(craft.Position, craft.Velocity, craft.Body.Mu, sim.Time);
                _out.WriteLine($"t={sim.Time:F0} alt={env.AltitudeSeaLevel:F0} v={craft.Velocity.Length:F0} ap={o.ApoapsisRadius - craft.Body.Radius:F0} pe={o.PeriapsisRadius - craft.Body.Radius:F0} phase={ap.Current} m={craft.TotalMass:F0} q={craft.LastDynamicPressure:F0} g={craft.LastGForce:F2}");
            }
        }
        foreach (var l in ap.Log) _out.WriteLine(l);
        foreach (var e in sim.EventLog) _out.WriteLine(e.ToString());
        var fo = Orbit.FromState(craft.Position, craft.Velocity, craft.Body.Mu, sim.Time);
        _out.WriteLine($"FINAL phase={ap.Current} t={sim.Time:F0} ap={fo.ApoapsisRadius - craft.Body.Radius:F0} pe={fo.PeriapsisRadius - craft.Body.Radius:F0} maxQ={maxQ:F0} parts={craft.Parts.Count}");
        Assert.Equal(Autopilot.Phase.Done, ap.Current);
        Assert.True(fo.PeriapsisRadius - craft.Body.Radius > 70_000);
        Assert.True(fo.Eccentricity < 0.05);
    }
}
