using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Persistence;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Tests
{
    /// <summary>
    /// Stock vessel blueprints shipped in data/vessels. They are built with the same placement code the editor uses.
    /// Regenerate the files with:  OSP_WRITE_STOCK=1 dotnet test tests/OSP.Tests --filter StockCraft
    /// The tests also check that every stock craft is valid and has sensible performance.
    /// </summary>
    public class StockCraft
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();

        public static Dictionary<string, VesselDesign> Build(PartCatalog catalog)
        {
            var result = new Dictionary<string, VesselDesign>();

            // Pathfinder: two-stage orbital rocket with a recoverable capsule.
            {
                var b = new DesignBuilder(catalog, "pod.aquila");
                PartInstance pod = b.Last;
                b.Above("chute.petal", pod);
                b.Below("heatshield.h1", pod);
                b.Below("decoupler.td1");
                b.Below("tank.t400");
                b.Below("engine.ember");
                b.Below("decoupler.td1");
                b.Below("tank.t800");
                PartInstance lower = b.Below("tank.t400");
                b.Below("engine.vector");
                b.Radial("fin.delta", lower, 4, -0.55);
                VesselDesign d = b.Finish();
                d.Name = "Pathfinder Orbiter";
                d.Description = "Two-stage rocket that reaches a low orbit around Aster and returns its capsule.";
                result["Pathfinder_Orbiter"] = d;
            }
            // Skylark: single-stage sounding rocket.
            {
                var b = new DesignBuilder(catalog, "pod.aquila");
                PartInstance pod = b.Last;
                b.Above("chute.petal", pod);
                b.Below("heatshield.h1", pod);
                b.Below("decoupler.td1");
                PartInstance tank = b.Below("tank.t400");
                b.Below("engine.swift");
                b.Radial("fin.delta", tank, 3, -0.6);
                b.Radial("sci.thermo", b.Design.Parts.First(p => p.Part == "pod.aquila"), 1, -0.1);
                VesselDesign d = b.Finish();
                d.Name = "Skylark Hopper";
                d.Description = "Suborbital hop with a thermometer. Separate the capsule for the parachute descent.";
                result["Skylark_Hopper"] = d;
            }
            // Thunderbird: radial solid boosters around a liquid core.
            {
                var b = new DesignBuilder(catalog, "pod.aquila");
                PartInstance pod = b.Last;
                b.Above("chute.petal", pod);
                b.Below("heatshield.h1", pod);
                b.Below("decoupler.td1");
                b.Below("tank.t400");
                b.Below("engine.ember");
                b.Below("decoupler.td1");
                b.Below("tank.t800");
                PartInstance core = b.Below("tank.t400");
                b.Below("engine.vector");
                List<PartInstance> decs = b.Radial("decoupler.radial", b.Design.Parts.First(p => p.Part == "tank.t800" ), 2, -0.2);
                PartDefinition srb = catalog.Get("srb.hammer");
                int group = b.Design.NextSymmetryGroup++;
                foreach (PartInstance rd in decs)
                {
                    PartRayHit hit = Assembly.RaycastPart(rd, rd.LocalToVessel(new Vector3d(0, 0, 3)), rd.DirectionToVessel(new Vector3d(0, 0, -1))).Value;
                    Placement pl = Assembly.SurfacePlacement(srb, hit.Point, hit.Normal, 0);
                    PartInstance s = b.Design.AddPart(srb, rd.Id, PartInstance.SurfaceNode, PartInstance.SurfaceNode, pl.Position, pl.Rotation);
                    s.SymmetryGroup = group;
                }
                b.Radial("fin.delta", core, 4, -0.55);
                VesselDesign d = b.Finish();
                d.Name = "Thunderbird Heavy";
                d.Description = "Orbital rocket with two strap-on solid boosters that separate after burnout.";
                result["Thunderbird_Heavy"] = d;
            }
            // Tessar Lander: probe-controlled lander with legs, solar power and a gravimeter-free science kit.
            {
                var b = new DesignBuilder(catalog, "probe.orb");
                PartInstance upper = b.Below("tank.t200");
                PartInstance tank = b.Below("tank.t200");
                b.Below("engine.vector");
                b.Radial("leg.lt1", tank, 4, -0.1);
                b.Radial("solar.fixed", upper, 3, 0.0);
                b.Radial("sci.thermo", b.Design.Parts[0], 1, 0.0);
                VesselDesign d = b.Finish();
                d.Name = "Tessar Lander";
                d.Description = "A probe lander: deploy legs with G. Put it in orbit with a launcher to visit Tessar.";
                result["Tessar_Lander"] = d;
            }
            return result;
        }

        [Fact]
        public void StockCraft_AreValidAndCapable()
        {
            Dictionary<string, VesselDesign> craft = Build(_catalog);
            double g = TestData.LoadSystem().HomeBody.SurfaceGravity;
            foreach (var (name, d) in craft)
            {
                string json = BlueprintSerializer.Serialize(d);
                VesselDesign r = BlueprintSerializer.Deserialize(json, _catalog, out List<string> warnings);
                Assert.Empty(warnings);
                Assert.Equal(d.Parts.Count, r.Parts.Count);
                // No deep overlaps between non-adjacent parts.
                foreach (PartInstance p in d.Parts)
                {
                    var ignore = new HashSet<int> { p.Id, p.Parent };
                    foreach (PartInstance c in d.Children(p.Id)) ignore.Add(c.Id);
                    if (p.Def.HasModule<JettisonModuleDef>()) continue;
                    AttachResult ok = Assembly.ValidateOverlap(d, p.Def, new Placement(p.Position, p.Rotation), ignore);
                    Assert.True(ok.Ok, $"{name}: {p.Part} {ok.Reason}");
                }
            }
            var stats = DeltaVCalculator.Compute(craft["Pathfinder_Orbiter"], g);
            Assert.True(DeltaVCalculator.TotalVacuum(stats) > 4000, "the orbiter needs enough Δv for orbit");
            Assert.True(stats[0].TwrSeaLevel > 1.15, $"launch TWR {stats[0].TwrSeaLevel}");
            var heavy = DeltaVCalculator.Compute(craft["Thunderbird_Heavy"], g);
            Assert.True(heavy[0].TwrSeaLevel > 1.3);

            if (Environment.GetEnvironmentVariable("OSP_WRITE_STOCK") == "1")
            {
                string dir = Path.Combine(TestData.ProjectRoot, "data", "vessels");
                Directory.CreateDirectory(dir);
                foreach (var (name, d) in craft)
                    File.WriteAllText(Path.Combine(dir, name + ".osp-vessel.json"), BlueprintSerializer.Serialize(d));
            }
        }
    }
}
