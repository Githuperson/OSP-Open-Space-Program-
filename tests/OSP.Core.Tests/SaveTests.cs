using System.Text.Json;
using System.Text.Json.Nodes;
using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Core.Tests;

public class SaveTests
{
    private static readonly PartCatalog Cat = TestEnv.Catalog;

    [Fact]
    public void DesignRoundTripPreservesEverything()
    {
        var d = FlightIntegrationTests.TwoStageRocket();
        d.Description = "two stage test";
        var json = SaveSerializer.SerializeDesign(d);
        var back = SaveSerializer.DeserializeDesign(json, Cat);
        Assert.Equal(d.Name, back.Name);
        Assert.Equal(d.Parts.Count, back.Parts.Count);
        for (int i = 0; i < d.Parts.Count; i++)
        {
            var a = d.Parts[i]; var b = back.Parts[i];
            Assert.Equal(a.PartId, b.PartId); Assert.Equal(a.ParentId, b.ParentId); Assert.Equal(a.Stage, b.Stage);
            Assert.Equal(a.ParentNode, b.ParentNode); Assert.Equal(a.ChildNode, b.ChildNode); Assert.Equal(a.AngleDeg, b.AngleDeg);
            Assert.True((a.Position - b.Position).Length < 1e-9);
        }
        // Serialising twice gives byte-identical output (deterministic format).
        Assert.Equal(json, SaveSerializer.SerializeDesign(back));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"kindTag\":\"something-else\",\"formatVersion\":1}")]
    [InlineData("{\"kindTag\":\"osp-vehicle\",\"formatVersion\":99,\"design\":{\"parts\":[]}}")]
    [InlineData("{\"kindTag\":\"osp-vehicle\",\"formatVersion\":0,\"design\":{\"parts\":[]}}")]
    [InlineData("{\"kindTag\":\"osp-vehicle\",\"formatVersion\":1,\"design\":{\"parts\":[]}}")]
    [InlineData("{\"kindTag\":\"osp-vehicle\",\"formatVersion\":1,\"design\":{\"parts\":[{\"id\":1,\"partId\":\"does_not_exist\",\"parentId\":-1}]}}")]
    public void InvalidDesignFilesAreRejectedCleanly(string json)
    {
        Assert.Throws<DataException>(() => SaveSerializer.DeserializeDesign(json, Cat));
    }

    [Fact]
    public void DesignWithDanglingParentOrWrongNodeIsRejected()
    {
        var d = FlightIntegrationTests.TwoStageRocket();
        var root = JsonNode.Parse(SaveSerializer.SerializeDesign(d))!;
        root["design"]!["parts"]![2]!["parentId"] = 777;
        Assert.Throws<DataException>(() => SaveSerializer.DeserializeDesign(root.ToJsonString(), Cat));

        root = JsonNode.Parse(SaveSerializer.SerializeDesign(d))!;
        root["design"]!["parts"]![2]!["childNode"] = "nope";
        Assert.Throws<DataException>(() => SaveSerializer.DeserializeDesign(root.ToJsonString(), Cat));
    }

    private static FlightSimulation FlyForAWhile(out Craft craft)
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        var ap = new Autopilot(sim, craft);
        sim.ActivateStage();
        for (int i = 0; i < 100 * 40; i++) { ap.Update(0.02); sim.StepFixed(0.02); }
        sim.AddNode(craft, sim.Time + 1000, 120, 10, -5);
        craft.Parts.First(p => p.Def.Id == "pod_aster").Resources["ElectricCharge"] = 33.3;
        return sim;
    }

    [Fact]
    public void FlightStateRoundTripAndContinuesIdentically()
    {
        var sim = FlyForAWhile(out var craft);
        var save = SaveSerializer.Capture(sim, "unit test");
        var json = SaveSerializer.Serialize(save);
        var sim2 = SaveSerializer.Restore(json, TestEnv.NewSystem(), Cat, out var loaded);
        var c2 = sim2.Active!;
        Assert.Equal(sim.Time, sim2.Time);
        Assert.Equal(craft.Id, c2.Id);
        Assert.Equal(craft.Parts.Count, c2.Parts.Count);
        Assert.Equal(craft.Body.Id, c2.Body.Id);
        Assert.True((craft.Position - c2.Position).Length < 1e-9);
        Assert.True((craft.Velocity - c2.Velocity).Length < 1e-9);
        Assert.Equal(craft.TotalMass, c2.TotalMass, 6);
        Assert.Equal(33.3, c2.Parts.First(p => p.Def.Id == "pod_aster").Amount("ElectricCharge"), 9);
        Assert.Equal(craft.ResourceTotal("Propellant"), c2.ResourceTotal("Propellant"), 6);
        Assert.Equal(craft.CurrentStage, c2.CurrentStage);
        var n = sim2.NodesFor(c2); Assert.Single(n); Assert.Equal(120, n[0].Prograde, 9);
        Assert.Equal(craft.Parts.First(p => p.Def.Engine != null && p.Ignited).Id, c2.Parts.First(p => p.Def.Engine != null && p.Ignited).Id);

        // both simulations continue to agree (determinism after reload)
        craft.Control.Throttle = 1; c2.Control.Throttle = 1;
        for (int i = 0; i < 100 * 5; i++) { sim.StepFixed(0.02); sim2.StepFixed(0.02); }
        Assert.True((craft.Position - c2.Position).Length < 1.0, $"{(craft.Position - c2.Position).Length}");
    }

    [Fact]
    public void LandedCraftRoundTrips()
    {
        var sys = TestEnv.NewSystem(); var sim = new FlightSimulation(sys, Cat);
        var craft = sim.Launch(FlightIntegrationTests.TwoStageRocket(), sys.Home, sys.Home.LaunchSites[0]);
        sim.Time = 12345;
        var json = SaveSerializer.Serialize(SaveSerializer.Capture(sim, "pad"));
        var sim2 = SaveSerializer.Restore(json, TestEnv.NewSystem(), Cat, out _);
        Assert.Equal(CraftMode.Landed, sim2.Active!.Mode);
        sim2.Active.SyncFromSurface(sim2.Time);
        double alt = sim2.Active.Body.AltitudeAboveTerrain(sim2.Active.Position, sim2.Time);
        Assert.InRange(alt, 0, 40);
    }

    [Fact]
    public void CorruptedSavesAreRejectedWithoutPartialState()
    {
        var sim = FlyForAWhile(out _);
        var good = SaveSerializer.Serialize(SaveSerializer.Capture(sim, "x"));
        void Expect(Action<JsonNode> mutate)
        {
            var node = JsonNode.Parse(good)!;
            mutate(node);
            Assert.Throws<DataException>(() => SaveSerializer.Restore(node.ToJsonString(), TestEnv.NewSystem(), Cat, out _));
        }
        Expect(n => n["kindTag"] = "bogus");
        Expect(n => n["formatVersion"] = 42);
        Expect(n => n["universalTime"] = -5);
        Expect(n => n["crafts"]![0]!["bodyId"] = "planet_x");
        Expect(n => n["crafts"]![0]!["position"] = new JsonArray(1, 2));
        Expect(n => n["crafts"]![0]!["mode"] = 9);
        Expect(n => n["crafts"]![0]!["parts"]![0]!["partId"] = "ghost_part");
        Expect(n => n["crafts"]![0]!["parts"]![1]!["resources"] = new JsonObject { ["Unobtainium"] = 5 });
        Expect(n => n["crafts"]![0]!["parts"]![1]!["parentId"] = 31337);
        Expect(n => n["crafts"]![0]!["parts"] = new JsonArray());
        Expect(n => n["activeCraftId"] = 987654);
        Assert.Throws<DataException>(() => SaveSerializer.Restore("<<<garbage>>>", TestEnv.NewSystem(), Cat, out _));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new PlayerSettings { GraphicsQuality = 2, RenderScale = 0.75, KeyBindings = { ["stage"] = "Space" } };
        var back = Json.Deserialize<PlayerSettings>(Json.Serialize(s), "settings");
        Assert.Equal(2, back.GraphicsQuality); Assert.Equal(0.75, back.RenderScale); Assert.Equal("Space", back.KeyBindings["stage"]);
    }
}
