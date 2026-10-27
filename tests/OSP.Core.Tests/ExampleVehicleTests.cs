using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Core.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OSP.Core.Tests;

/// <summary>
/// The example vehicles shipped in game/data/vehicles. Set OSP_WRITE_EXAMPLES=1 to regenerate the files from the
/// definitions below (they are normal game data; players can edit/replace them).
/// </summary>
public class ExampleVehicleTests
{
    private readonly ITestOutputHelper _out;
    public ExampleVehicleTests(ITestOutputHelper o) { _out = o; }

    public static IEnumerable<VehicleDesign> Examples()
    {
        yield return FlightIntegrationTests.TwoStageRocket();

        // Strap-on boosters
        var b = new RocketBuilder().Root("pod_aster").Below("rw_unit").Below("tank_s4").Below("tank_s4").Below("eng_swift");
        int core = b.Design.Parts[3].Id;
        b.Radial("fin_delta", core, 3, 90, -1.4);
        var sym = b.Radial("dec_radial", core, 2, 0, 0.3);
        int dec = b.Design.Parts.First(p => p.PartId == "dec_radial").Id;
        b.OnSocket("srb_hammer", dec);
        var srbs = b.Design.Parts.Where(p => p.PartId == "srb_hammer").ToList();
        foreach (var s in srbs)
        {
            var ed = new VehicleEditor(TestEnv.Catalog);
            var c = ed.FindNodeCandidates(b.Design, "nose_radial").FirstOrDefault(x => x.ParentId == s.Id && x.ParentNode == "top");
            if (c != null) { ed.Attach(b.Design, "nose_radial", c); break; }
        }
        b.Design.Name = "Boosted Swift";
        yield return b.Design;

        // Lander
        var l = new RocketBuilder().Root("pod_aster");
        var ed2 = new VehicleEditor(TestEnv.Catalog);
        var chute = ed2.FindNodeCandidates(l.Design, "chute_mk1").First(c => c.ParentNode == "top");
        ed2.Attach(l.Design, "chute_mk1", chute);
        l.Below("tank_s2").Below("eng_whisper");
        int tank = l.Design.Parts.First(p => p.PartId == "tank_s2").Id;
        l.Radial("leg_strut", tank, 3, 0, -0.5);
        l.Radial("solar_flat", tank, 2, 90, 0.4);
        l.Design.Name = "Hopper Lander";
        yield return l.Design;

        // Heavy 2.5 m lifter
        var h = new RocketBuilder().Root("pod_trio").Below("tank_l2").Below("eng_aeon").Below("dec_l").Below("tank_l8").Below("tank_l4").Below("eng_comet");
        h.Radial("fin_delta", h.Design.Parts.Last(p => p.PartId == "tank_l4").Id, 4, 45, -1.0);
        h.Design.Name = "Heavy Lifter";
        yield return h.Design;
    }

    [Fact]
    public void ExamplesAreValidAndStatsPrint()
    {
        string dir = Path.Combine(TestEnv.DataDir, "vehicles");
        bool write = Environment.GetEnvironmentVariable("OSP_WRITE_EXAMPLES") == "1";
        if (write) Directory.CreateDirectory(dir);
        foreach (var d in Examples())
        {
            var issues = VehicleValidator.Validate(d, TestEnv.Catalog);
            Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
            var st = VehicleAnalysis.Analyze(d, TestEnv.Catalog);
            _out.WriteLine($"{d.Name}: parts={d.Parts.Count} mass={st.WetMass:F0} TWR={st.LaunchTwr:F2} dv(vac)={st.DeltaVVacuum:F0} dv(atm)={st.DeltaVAtmosphere:F0} warnings={string.Join("|", issues.Select(i => i.Message))}");
            if (write) File.WriteAllText(Path.Combine(dir, d.Name.Replace(' ', '_') + ".osp-vehicle.json"), SaveSerializer.SerializeDesign(d));
        }
    }

    [Fact]
    public void ShippedExampleFilesLoad()
    {
        string dir = Path.Combine(TestEnv.DataDir, "vehicles");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.osp-vehicle.json"))
        {
            var d = SaveSerializer.DeserializeDesign(File.ReadAllText(f), TestEnv.Catalog);
            Assert.NotEmpty(d.Parts);
        }
    }
}

public class ExampleFlightTests
{
    /// <summary>Flies Pathfinder-1 to orbit with the scripted autopilot and (optionally) writes the state as an example save.</summary>
    [Fact]
    public void OrbitExampleSaveIsValid()
    {
        var sys = TestEnv.NewSystem(); var sim = new OSP.Core.Flight.FlightSimulation(sys, TestEnv.Catalog);
        var craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        var ap = new Autopilot(sim, craft);
        sim.ActivateStage();
        while (sim.Time < 2400 && ap.Current != Autopilot.Phase.Done) { ap.Update(0.02); sim.StepFixed(0.02); }
        Assert.Equal(Autopilot.Phase.Done, ap.Current);
        craft.Control.Sas = OSP.Core.Flight.SasMode.Prograde; craft.Control.SasEnabled = true; craft.Control.Throttle = 0;
        craft.Name = "Pathfinder-1";
        var save = SaveSerializer.Capture(sim, "Pathfinder-1 in orbit");
        var json = SaveSerializer.Serialize(save);
        var back = SaveSerializer.Restore(json, TestEnv.NewSystem(), TestEnv.Catalog, out _);
        Assert.Equal(sim.Crafts.Count, back.Crafts.Count);
        if (Environment.GetEnvironmentVariable("OSP_WRITE_EXAMPLES") == "1")
        {
            var dir = Path.Combine(TestEnv.DataDir, "saves"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Pathfinder-1_in_orbit.osp-save.json"), json);
        }
    }

    [Fact]
    public void ShippedExampleSavesRestore()
    {
        var dir = Path.Combine(TestEnv.DataDir, "saves");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.osp-save.json"))
        {
            var sim = SaveSerializer.Restore(File.ReadAllText(f), TestEnv.NewSystem(), TestEnv.Catalog, out _);
            Assert.NotNull(sim.Active);
        }
    }
}
