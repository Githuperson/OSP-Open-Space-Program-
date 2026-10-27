using OSP.Core.Flight;
using OSP.Core.Save;
using Xunit;
using Xunit.Abstractions;

namespace OSP.Core.Tests;

public class BoostedLaunchTests
{
    private readonly ITestOutputHelper _o; public BoostedLaunchTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void BoostedSwiftLaunchesWithoutBreakingUp()
    {
        var path = Path.Combine(TestEnv.DataDir, "vehicles", "Boosted_Swift.osp-vehicle.json");
        var d = SaveSerializer.DeserializeDesign(File.ReadAllText(path), TestEnv.Catalog);
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, TestEnv.Catalog);
        var craft = sim.Launch(d, sys.Home, sys.Home.LaunchSites[0]);
        sim.ActivateStage(); craft.Control.Throttle = 1;
        for (int i = 0; i < 100 * 40; i++)
        {
            sim.StepFixed(0.02);
            if (i < 60 && i % 4 == 0 && sim.Active != null) _o.WriteLine($"  i={i} alt={sim.Active.Position.Length - sys.Home.Radius:F2} tilt={OSP.Core.Mathx.Vec3d.Angle(sim.Active.Orientation.Rotate(OSP.Core.Mathx.Vec3d.UnitY), sim.Active.Position):F4} w={sim.Active.AngularVelocity.Length:F4} contact={sim.Active.InContact} mode={sim.Active.Mode} launched={sim.Active.HasLaunched} v={sim.Active.Velocity.Length:F1}");
            if (i % 100 == 0 && sim.Active != null) _o.WriteLine($"t={sim.Time:F0} alt={sim.Active.Position.Length - sys.Home.Radius:F0} v={sim.Active.Velocity.Length:F0} thrust={sim.Active.LastTotalThrust / 1000:F0} parts={sim.Active.Parts.Count} crafts={sim.Crafts.Count}");
        }
        foreach (var e in sim.EventLog) _o.WriteLine(e.ToString());
        Assert.DoesNotContain(sim.EventLog, e => e.Kind == SimEventKind.JointFailed || e.Kind == SimEventKind.PartDestroyed);
        Assert.NotNull(sim.Active);
    }

    [Fact]
    public void BoostersBurnOutAndSeparateInStagingOrder()
    {
        var path = Path.Combine(TestEnv.DataDir, "vehicles", "Boosted_Swift.osp-vehicle.json");
        var d = SaveSerializer.DeserializeDesign(File.ReadAllText(path), TestEnv.Catalog);
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, TestEnv.Catalog);
        var craft = sim.Launch(d, sys.Home, sys.Home.LaunchSites[0]);
        sim.ActivateStage(); craft.Control.Throttle = 1;
        // run until both boosters have burned out
        int guard = 0;
        while (craft.Parts.Any(p => p.Def.Id == "srb_hammer" && !p.FlamedOut) && guard++ < 100 * 80) sim.StepFixed(0.02);
        Assert.True(craft.Parts.Where(p => p.Def.Id == "srb_hammer").All(p => p.FlamedOut), "boosters burn out");
        Assert.Single(sim.Crafts);
        double massBefore = craft.TotalMass;
        var result = sim.ActivateStage();
        Assert.Equal(0, result!.StageFired);
        Assert.Equal(2, result.NewCrafts.Count);
        Assert.DoesNotContain(craft.Parts, p => p.Def.Id == "srb_hammer" || p.Def.Id == "dec_radial");
        Assert.True(craft.TotalMass < massBefore);
        // the core keeps flying under Swift thrust and the boosters drift away
        for (int i = 0; i < 100 * 5; i++) sim.StepFixed(0.02);
        Assert.True(craft.LastTotalThrust > 100_000);
        foreach (var nc in result.NewCrafts) Assert.True((nc.Position - craft.Position).Length > 3, "boosters separated from the core");
        Assert.Equal(craft, sim.Active);
    }
}
