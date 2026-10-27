using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Core.Tests;

public class VehicleTests
{
    private static readonly PartCatalog Cat = TestEnv.Catalog;

    [Fact]
    public void CatalogLoadsAllCategoriesWithDistinctParts()
    {
        Assert.True(Cat.All.Count >= 30);
        foreach (var c in new[] { "Command", "Fuel", "Engines", "Boosters", "Separation", "Structural", "Aerodynamics", "Landing", "Recovery", "Power", "Control" })
            Assert.Contains(c, Cat.Categories);
        // engines differ meaningfully
        var engines = Cat.All.Where(p => p.Engine != null).Select(p => (p.Engine!.ThrustVac, p.Engine.IspVac, p.DryMass)).Distinct().Count();
        Assert.Equal(Cat.All.Count(p => p.Engine != null), engines);
    }

    [Fact]
    public void StackAttachmentPlacesPartsEndToEnd()
    {
        var b = new RocketBuilder().Root("pod_aster").Below("tank_s2");
        var d = b.Design;
        var pod = d.Parts[0]; var tank = d.Parts[1];
        // pod bottom node is at y=-0.75; tank top node at +1.0 -> tank centre at y = -0.75 - 1.0
        Assert.Equal(-1.75, tank.Position.Y, 9);
        Assert.Equal(0, tank.Position.X, 9);
        Assert.Equal(1.0, tank.Rotation.Rotate(Vec3d.UnitY).Y, 9);
    }

    [Fact]
    public void NodeSizeMismatchIsRejected()
    {
        var ed = new VehicleEditor(Cat);
        var d = new VehicleDesign(); ed.SetRoot(d, "tank_l2");
        // a 1.25 m tank cannot attach to a 2.5 m node
        var cands = ed.FindNodeCandidates(d, "tank_s1");
        Assert.Empty(cands);
        // but the adapter can
        Assert.NotEmpty(ed.FindNodeCandidates(d, "adapter_21"));
    }

    [Fact]
    public void OccupiedNodeIsNotOfferedAgain()
    {
        var b = new RocketBuilder().Root("tank_s2").Below("tank_s1");
        var ed = new VehicleEditor(Cat);
        var c = ed.FindNodeCandidates(b.Design, "tank_s1").Where(x => x.ParentId == b.Design.Parts[0].Id && x.ParentNode == "bottom");
        Assert.Empty(c);
    }

    [Fact]
    public void RadialPartsRequireAcceptingParentAndSurfaceNode()
    {
        var ed = new VehicleEditor(Cat);
        var d = new VehicleDesign(); var root = ed.SetRoot(d, "eng_ember");
        Assert.Null(ed.RadialCandidate(d, root.Id, "fin_delta", 0, 0));          // engines do not accept radial parts
        var d2 = new VehicleDesign(); var tank = ed.SetRoot(d2, "tank_s2");
        Assert.Null(ed.RadialCandidate(d2, tank.Id, "tank_s1", 0, 0));           // tank_s1 has no surface node
        Assert.NotNull(ed.RadialCandidate(d2, tank.Id, "fin_delta", 0, 0));
    }

    [Fact]
    public void RadialSymmetryPlacesEvenlySpacedCopies()
    {
        var b = new RocketBuilder().Root("tank_s2");
        b.Radial("fin_delta", b.Design.Parts[0].Id, 4, 10, 0);
        var fins = b.Design.Parts.Where(p => p.PartId == "fin_delta").ToList();
        Assert.Equal(4, fins.Count);
        Assert.Equal(new[] { 10.0, 100.0, 190.0, 280.0 }, fins.Select(f => f.AngleDeg).OrderBy(x => x).ToArray());
        Assert.Single(fins.Select(f => f.SymmetryGroup).Distinct());
        // all fins are the same distance from the axis, and their outward axes (+X) point radially outward
        foreach (var f in fins)
        {
            double r = Math.Sqrt(f.Position.X * f.Position.X + f.Position.Z * f.Position.Z);
            Assert.Equal(fins[0].Position.X * fins[0].Position.X + fins[0].Position.Z * fins[0].Position.Z, r * r, 9);
            var outward = f.Rotation.Rotate(Vec3d.UnitX);
            var radial = new Vec3d(f.Position.X, 0, f.Position.Z).Normalized;
            Assert.True(Vec3d.Dot(outward, radial) > 0.999);
        }
    }

    [Fact]
    public void DeletingRemovesSubtreeAndSymmetryPartners()
    {
        var b = new RocketBuilder().Root("pod_aster").Below("tank_s2").Below("tank_s1").Below("eng_ember");
        var ed = new VehicleEditor(Cat);
        var d = b.Design;
        ed.Delete(d, d.Parts[1].Id);       // remove the first tank: everything below goes too
        Assert.Single(d.Parts);
    }

    [Fact]
    public void ValidatorFlagsMissingCommandAndUnstagedEngine()
    {
        var d = new RocketBuilder().Root("tank_s2").Below("eng_ember").Design;
        var issues = VehicleValidator.Validate(d, Cat);
        Assert.Contains(issues, i => i.Message.Contains("command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        // Corrupt: reference a missing parent
        d.Parts[1].ParentId = 999;
        Assert.Contains(VehicleValidator.Validate(d, Cat), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void MassAndCenterOfMassAreMassWeighted()
    {
        var d = new RocketBuilder().Root("tank_s2").Below("tank_s2").Design;
        var craft = Craft.FromDesign(d, Cat);
        double each = Cat.Get("tank_s2").DryMass + Cat.Get("tank_s2").ResourceCapacity("Propellant");
        Assert.Equal(2 * each, craft.TotalMass, 6);
        Assert.Equal(-1.0, craft.Mass.CenterOfMass.Y, 9);     // midpoint of the two tanks (0 and -2)
    }

    [Fact]
    public void InertiaMatchesSolidCylinder()
    {
        var d = new VehicleDesign(); new VehicleEditor(Cat).SetRoot(d, "tank_s4");
        var craft = Craft.FromDesign(d, Cat);
        double m = craft.TotalMass, r = 0.625, L = 4.0;
        Assert.Equal(0.5 * m * r * r, craft.Mass.Inertia.M11, 6);
        Assert.Equal(m * (3 * r * r + L * L) / 12, craft.Mass.Inertia.M00, 6);
    }

    [Fact]
    public void DeltaVMatchesTsiolkovsky()
    {
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s2").Below("eng_wren").Design;
        var craft = Craft.FromDesign(d, Cat);
        double m0 = craft.TotalMass; double prop = 1800;
        double expected = 305 * MathUtil.G0 * Math.Log(m0 / (m0 - prop));
        var st = VehicleAnalysis.Analyze(d, Cat);
        Assert.Single(st.StagesVacuum);
        Assert.True(Math.Abs(st.DeltaVVacuum - expected) / expected < 0.002);
        // sea-level Isp is lower so atmospheric delta-v is lower
        Assert.True(st.DeltaVAtmosphere < st.DeltaVVacuum);
        double expAtm = Math.Log(m0 / (m0 - prop)) * 240 * MathUtil.G0;
        Assert.True(Math.Abs(st.DeltaVAtmosphere - expAtm) / expAtm < 0.002);
    }

    [Fact]
    public void AutoStagingNumbersMatchDocumentedRules()
    {
        // serial: pod / tank / whisper / decoupler / tank / swift  -> swift fires at launch (1); decoupler + whisper at 0
        var d = new RocketBuilder().Root("pod_aster").Below("tank_s2").Below("eng_whisper").Below("dec_s").Below("tank_s4").Below("eng_swift").Design;
        int Stage(string id) => d.Parts.First(p => p.PartId == id).Stage;
        Assert.Equal(1, Stage("eng_swift"));
        Assert.Equal(0, Stage("dec_s"));
        Assert.Equal(0, Stage("eng_whisper"));

        // radial boosters: core engine and boosters at launch (1), radial decouplers at 0
        var b = new RocketBuilder().Root("pod_aster").Below("tank_s4").Below("eng_swift");
        int core = b.Design.Parts[1].Id;
        b.Radial("dec_radial", core, 2, 0, 0);
        var decs = b.Design.Parts.Where(p => p.PartId == "dec_radial").ToList();
        b.OnSocket("srb_hammer", decs[0].Id);          // symmetry replicates onto the partner decoupler
        Assert.Equal(2, b.Design.Parts.Count(p => p.PartId == "srb_hammer"));
        var d2 = b.Design;
        Assert.All(d2.Parts.Where(p => p.PartId == "srb_hammer"), p => Assert.Equal(1, p.Stage));
        Assert.All(d2.Parts.Where(p => p.PartId == "dec_radial"), p => Assert.Equal(0, p.Stage));
        Assert.Equal(1, d2.Parts.First(p => p.PartId == "eng_swift").Stage);
    }

    [Fact]
    public void StagingFiresInOrderAndSeparatesWithConservedMomentum()
    {
        var sys = TestEnv.NewSystem();
        var sim = new FlightSimulation(sys, Cat);
        var d = FlightIntegrationTests.TwoStageRocket();
        var craft = sim.Launch(d, sys.Home, sys.Home.LaunchSites[0]);
        craft.Mode = CraftMode.Physics;
        // Put it in vacuum coasting so only the separation impulse matters.
        craft.Body = sys.ById("lumen");
        craft.Position = new Vec3d(0, 600_000, 0); craft.Velocity = new Vec3d(1000, 0, 0);
        craft.Orientation = QuatD.Identity;
        var first = sim.ActivateStage();                    // stage 1: ignite the swift only
        Assert.Empty(first!.NewCrafts);
        Assert.Equal(1, first.StageFired);
        Assert.True(craft.Parts.First(p => p.Def.Id == "eng_swift").Ignited);
        Assert.False(craft.Parts.First(p => p.Def.Id == "eng_whisper").Ignited);

        double massBefore = craft.TotalMass;
        var pBefore = craft.Velocity * massBefore;
        var second = sim.ActivateStage();                   // stage 0: decoupler + whisper
        Assert.Equal(0, second!.StageFired);
        Assert.Single(second.NewCrafts);
        var spent = second.NewCrafts[0];
        Assert.Equal(massBefore, craft.TotalMass + spent.TotalMass, 6);
        var pAfter = craft.Velocity * craft.TotalMass + spent.Velocity * spent.TotalMass;
        Assert.True((pAfter - pBefore).Length < 1e-6 * pBefore.Length, "momentum must be conserved by separation");
        // relative separation speed = J (1/m1 + 1/m2) along the axis
        double J = Cat.Get("dec_s").Decoupler!.SeparationImpulse;
        double expected = J * (1 / craft.TotalMass + 1 / spent.TotalMass);
        Assert.Equal(expected, (craft.Velocity - spent.Velocity).Length, 6);
        // the upper stage keeps the whisper and pod; spent stage holds the swift
        Assert.Contains(craft.Parts, p => p.Def.Id == "pod_aster");
        Assert.Contains(spent.Parts, p => p.Def.Id == "eng_swift");
        Assert.True(craft.Parts.First(p => p.Def.Id == "eng_whisper").Ignited);
        Assert.Equal(-1, StagingController.NextStage(craft));
    }
}
