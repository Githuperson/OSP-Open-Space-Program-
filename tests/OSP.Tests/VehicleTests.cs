using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Tests
{
    public class PartCatalogTests
    {
        [Fact]
        public void ShippedParts_AllLoadWithoutErrors()
        {
            PartCatalog catalog = TestData.LoadCatalog();
            Assert.Empty(catalog.LoadErrors);
            Assert.True(catalog.All.Count >= 35, $"expected a substantial catalogue, got {catalog.All.Count}");
            foreach (PartCategory c in new[] { PartCategory.Command, PartCategory.FuelTank, PartCategory.Engine, PartCategory.Coupling,
                         PartCategory.Aerodynamic, PartCategory.Ground, PartCategory.Electrical, PartCategory.Control, PartCategory.Science, PartCategory.Communication })
                Assert.Contains(catalog.All, p => p.Category == c);
        }

        [Fact]
        public void Parts_HaveDistinctGeometry()
        {
            PartCatalog catalog = TestData.LoadCatalog();
            // No two parts share an identical mesh description.
            var signatures = catalog.All.Select(p => string.Join("|", p.Mesh.Select(m => $"{m.Type}:{m.Radius}:{m.Height}:{m.Size}:{m.Offset}:{m.Material}"))).ToList();
            Assert.Equal(signatures.Count, signatures.Distinct().Count());
            foreach (PartDefinition p in catalog.All)
            {
                Assert.True(p.Bounds.Size.X > 0.01 && p.Bounds.Size.Y > 0.01 && p.Bounds.Size.Z > 0.01, $"{p.Id} has degenerate bounds");
                Assert.True(p.AttachNodes.Count > 0 || p.SurfaceAttach != null, $"{p.Id} cannot attach to anything");
            }
        }

        [Fact]
        public void Engines_HaveMeaningfullyDifferentPerformance()
        {
            PartCatalog catalog = TestData.LoadCatalog();
            var engines = catalog.All.Select(p => p.GetModule<EngineModuleDef>()).Where(e => e != null).ToList();
            Assert.True(engines.Count >= 5);
            Assert.True(engines.Select(e => e.ThrustVacuum).Distinct().Count() == engines.Count, "every engine should have its own thrust");
            Assert.True(engines.Max(e => e.IspVacuum) - engines.Min(e => e.IspVacuum) > 100);
            Assert.Contains(engines, e => e.IsSolid);
            Assert.Contains(engines, e => e.GimbalRange > 0);
        }

        [Fact]
        public void InvalidPartDefinitions_AreRejectedWithMessages()
        {
            var catalog = new PartCatalog();
            string json = @"{ ""parts"": [
                { ""id"": ""bad.nomass"", ""name"": ""No Mass"", ""category"": ""structural"", ""mass"": 0, ""mesh"": [ { ""type"": ""box"" } ] },
                { ""id"": ""bad.module"", ""name"": ""Bad Module"", ""category"": ""structural"", ""mass"": 10, ""mesh"": [ { ""type"": ""box"" } ], ""modules"": [ { ""type"": ""WarpDrive"" } ] },
                { ""id"": ""bad.engine"", ""name"": ""Bad Engine"", ""category"": ""engine"", ""mass"": 10, ""mesh"": [ { ""type"": ""box"" } ], ""modules"": [ { ""type"": ""Engine"", ""thrustVacuum"": 1000, ""ispVacuum"": 300, ""propellants"": [ { ""resource"": ""Fuel"", ""ratio"": 0.3 } ] } ] },
                { ""id"": ""good"", ""name"": ""Good"", ""category"": ""structural"", ""mass"": 10, ""mesh"": [ { ""type"": ""box"" } ], ""attachNodes"": [ { ""id"": ""top"", ""position"": [0, 0.5, 0], ""direction"": [0, 1, 0] } ] }
            ] }";
            int loaded = catalog.LoadJson(json, "test.json");
            Assert.Equal(1, loaded);
            Assert.Equal(3, catalog.LoadErrors.Count);
            Assert.Contains(catalog.LoadErrors, e => e.Contains("dry mass"));
            Assert.Contains(catalog.LoadErrors, e => e.Contains("WarpDrive"));
            Assert.Contains(catalog.LoadErrors, e => e.Contains("sum to 1"));
            Assert.Equal(0, new PartCatalog().LoadJson("{ not json", "broken.json"));
        }

        [Fact]
        public void Search_FindsByNameAndCategory()
        {
            PartCatalog catalog = TestData.LoadCatalog();
            Assert.Contains(catalog.Search("swift"), p => p.Id == "engine.swift");
            Assert.All(catalog.Search(null, PartCategory.FuelTank), p => Assert.Equal(PartCategory.FuelTank, p.Category));
            Assert.Contains(catalog.Search("parachute"), p => p.HasModule<ParachuteModuleDef>());
        }
    }

    public class AssemblyTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();

        [Fact]
        public void StackPlacement_JoinsNodesFaceToFace()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            PartInstance pod = b.Last;
            PartInstance tank = b.Below("tank.t400");
            Vector3d podNode = pod.LocalToVessel(pod.Def.GetNode("bottom").Position);
            Vector3d tankNode = tank.LocalToVessel(tank.Def.GetNode("top").Position);
            Assert.True((podNode - tankNode).Length < 1e-9);
            Vector3d d1 = pod.DirectionToVessel(pod.Def.GetNode("bottom").Direction);
            Vector3d d2 = tank.DirectionToVessel(tank.Def.GetNode("top").Direction);
            Assert.True(Vector3d.Dot(d1, d2) < -0.999999);
            // The tank sits directly below the pod.
            Assert.True(tank.Position.Y < pod.Position.Y);
            Assert.True(System.Math.Abs(tank.Position.X) < 1e-9 && System.Math.Abs(tank.Position.Z) < 1e-9);
        }

        [Fact]
        public void OccupiedNodes_AndSizeMismatch_AreRejected()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            PartInstance pod = b.Last;
            b.Below("tank.t200");
            AttachResult occupied = Assembly.ValidateStack(b.Design, pod, "bottom", _catalog.Get("tank.t100"), "top");
            Assert.False(occupied.Ok);
            Assert.Contains("occupied", occupied.Reason);

            var b2 = new DesignBuilder(_catalog, "probe.sprite"); // size 0 bottom node
            AttachResult mismatch = Assembly.ValidateStack(b2.Design, b2.Last, "bottom", _catalog.Get("tank.x200"), "top"); // size 2
            Assert.False(mismatch.Ok);
            Assert.Contains("incompatible", mismatch.Reason);

            AttachResult unknownNode = Assembly.ValidateStack(b2.Design, b2.Last, "side", _catalog.Get("tank.oscar"), "top");
            Assert.False(unknownNode.Ok);
        }

        [Fact]
        public void SurfaceAttach_RequiresSurfaceAttachPoint()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            Assert.False(Assembly.ValidateSurface(b.Last, _catalog.Get("tank.t400")).Ok);
            Assert.True(Assembly.ValidateSurface(b.Last, _catalog.Get("fin.delta")).Ok);
            // Fins don't accept parts on their surface.
            PartInstance fin = b.Radial("fin.delta", b.Last, 1, -0.5)[0];
            Assert.False(Assembly.ValidateSurface(fin, _catalog.Get("sci.thermo")).Ok);
        }

        [Fact]
        public void Symmetry_PlacesEvenlySpacedCopies()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            List<PartInstance> fins = b.Radial("fin.delta", b.Last, 4, -0.6);
            Assert.Equal(4, fins.Count);
            Assert.All(fins, f => Assert.Equal(fins[0].SymmetryGroup, f.SymmetryGroup));
            Assert.NotEqual(0, fins[0].SymmetryGroup);
            for (int i = 0; i < 4; i++)
            {
                Vector3d a = new Vector3d(fins[i].Position.X, 0, fins[i].Position.Z);
                Vector3d c = new Vector3d(fins[(i + 1) % 4].Position.X, 0, fins[(i + 1) % 4].Position.Z);
                Assert.True(System.Math.Abs(Vector3d.Angle(a, c) - System.Math.PI / 2) < 1e-9);
                Assert.True(System.Math.Abs(a.Length - 0.625) < 0.05, "fin root should sit on the tank surface");
            }
        }

        [Fact]
        public void Overlap_IsDetected()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            PartDefinition t = _catalog.Get("tank.t200");
            AttachResult inside = Assembly.ValidateOverlap(b.Design, t, new Placement(Vector3d.Zero, QuaternionD.Identity), null);
            Assert.False(inside.Ok);
            AttachResult far = Assembly.ValidateOverlap(b.Design, t, new Placement(new Vector3d(5, 0, 0), QuaternionD.Identity), null);
            Assert.True(far.Ok);
        }

        [Fact]
        public void Raycast_HitsCylinderSideAndCap()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            PartRayHit? side = Assembly.Raycast(b.Design, new Vector3d(10, 0.2, 0), new Vector3d(-1, 0, 0));
            Assert.True(side.HasValue);
            Assert.True(System.Math.Abs(side.Value.Point.X - 0.625) < 0.03); // bounds include the stringers
            Assert.True(side.Value.Normal.X > 0.99);
            PartRayHit? cap = Assembly.Raycast(b.Design, new Vector3d(0.1, 10, 0), new Vector3d(0, -1, 0));
            Assert.True(cap.HasValue);
            Assert.True(cap.Value.Normal.Y > 0.99);
            Assert.False(Assembly.Raycast(b.Design, new Vector3d(10, 0, 0), new Vector3d(1, 0, 0)).HasValue);
        }

        [Fact]
        public void Reroot_ReversesParentChain()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            PartInstance tank = b.Below("tank.t200");
            PartInstance engine = b.Below("engine.swift");
            b.Design.Reroot(engine.Id);
            Assert.Equal(-1, engine.Parent);
            Assert.Equal(engine.Id, tank.Parent);
            Assert.Equal(tank.Id, b.Design.Parts.First(p => p.Part == "pod.aquila").Parent);
            Assert.Equal(engine, b.Design.Root);
        }
    }

    public class MassAndPropulsionTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();

        [Fact]
        public void Mass_IsSumOfDryMassAndResources()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Below("tank.t400");
            b.Below("engine.swift");
            VesselDesign d = b.Finish();
            MassProperties mp = MassProperties.Compute(d.Parts);
            double expected = 840 + 10 + 250 + 2000 + 1250; // pod dry + pod monoprop (EC is massless) + tank dry + propellant + engine
            Assert.Equal(expected, mp.Mass, 6);
        }

        [Fact]
        public void CenterOfMass_AndInertia_MatchAnalyticCases()
        {
            // Two identical tanks at ±y: CoM is midway.
            PartDefinition t = _catalog.Get("tank.t400");
            var a = new PartInstance { Id = 1, Def = t, Part = t.Id, Position = new Vector3d(0, 2, 0) };
            var c = new PartInstance { Id = 2, Def = t, Part = t.Id, Position = new Vector3d(0, -2, 0), Parent = 1 };
            MassProperties mp = MassProperties.Compute(new[] { a, c });
            Assert.True(mp.CenterOfMass.Length < 1e-9);
            // Axial inertia of a solid cylinder: ½ m r² (per tank), no parallel-axis term along Y.
            double m = t.WetMass, r = t.Bounds.Radius;
            Assert.True(System.Math.Abs(mp.Inertia.M11 - 2 * 0.5 * m * r * r) < 1e-6 * m);
            // Transverse: 2·(m(3r²+h²)/12 + m·2²).
            double h = t.Bounds.Height;
            double expected = 2 * (m * (3 * r * r + h * h) / 12 + m * 4);
            Assert.True(System.Math.Abs(mp.Inertia.M00 - expected) < 1e-6 * expected);
        }

        [Fact]
        public void FuelConsumption_MatchesMassFlowEquation()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            PartInstance engine = b.Below("engine.swift");
            VesselDesign d = b.Finish();
            var net = new ResourceNetwork(d.Parts);
            EngineModuleDef e = engine.Def.GetModule<EngineModuleDef>();
            double mdot = e.ThrustVacuum / (e.IspVacuum * PhysicsConstants.G0);
            Assert.Equal(mdot, e.MaxMassFlow, 9);
            double before = d.Parts.Sum(p => p.TotalMass);
            for (int i = 0; i < 100; i++) Propulsion.ConsumePropellant(net, engine, e, 1.0, 0.1); // 10 s
            double after = d.Parts.Sum(p => p.TotalMass);
            Assert.Equal(mdot * 10, before - after, 6);
            // Propellants drawn in the engine's mass ratio.
            PartInstance tank = d.Parts[0];
            Assert.Equal(900 - mdot * 10 * 0.45, tank.GetResource("Fuel"), 6);
            Assert.Equal(1100 - mdot * 10 * 0.55, tank.GetResource("Oxidizer"), 6);
        }

        [Fact]
        public void Thrust_VariesWithPressureAtConstantMassFlow()
        {
            EngineModuleDef e = _catalog.Get("engine.swift").GetModule<EngineModuleDef>();
            Assert.Equal(e.ThrustVacuum, e.ThrustAt(0), 6);
            Assert.Equal(e.MaxMassFlow * e.IspSeaLevel * PhysicsConstants.G0, e.ThrustAt(1), 6);
            Assert.True(e.ThrustAt(0.5) < e.ThrustAt(0) && e.ThrustAt(0.5) > e.ThrustAt(1));
        }

        [Fact]
        public void Engine_FlamesOutWhenTanksAreEmpty()
        {
            var b = new DesignBuilder(_catalog, "tank.oscar");
            PartInstance engine = b.Below("engine.spark");
            VesselDesign d = b.Finish();
            var net = new ResourceNetwork(d.Parts);
            EngineModuleDef e = engine.Def.GetModule<EngineModuleDef>();
            double burnTime = 80 / e.MaxMassFlow;
            double t = 0;
            while (Propulsion.ConsumePropellant(net, engine, e, 1, 0.5) > 0 && t < 10000) t += 0.5;
            Assert.True(System.Math.Abs(t - burnTime) < 1.0, $"burned {t}s, expected {burnTime}s");
            Assert.False(Propulsion.HasPropellant(net, engine, e));
        }

        [Fact]
        public void Decoupler_BlocksCrossfeed_AndFuelLineFeedsSourceFirst()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            PartInstance upperTank = b.Last;
            PartInstance upperEngine = b.Below("engine.swift");
            PartInstance dec = b.Below("decoupler.td1");
            PartInstance lowerTank = b.Below("tank.t400");
            PartInstance lowerEngine = b.Below("engine.swift");
            VesselDesign d = b.Finish();
            var net = new ResourceNetwork(d.Parts);
            Assert.NotEqual(net.GroupOf(upperTank), net.GroupOf(lowerTank));
            Assert.Equal(900, net.Available(upperEngine, "Fuel"), 6);
            Assert.Equal(900, net.Available(lowerEngine, "Fuel"), 6);

            // A fuel line from the lower tank into the upper stack: upper engine drains the lower tank first.
            PartDefinition lineDef = _catalog.Get("fuelline.fl1");
            PartInstance line = d.AddPart(lineDef, lowerTank.Id, PartInstance.SurfaceNode, PartInstance.SurfaceNode, lowerTank.Position + new Vector3d(0.65, 0, 0), QuaternionD.Identity);
            line.FuelLineTarget = upperTank.Id;
            net = new ResourceNetwork(d.Parts);
            Assert.Equal(1800, net.Available(upperEngine, "Fuel"), 6);
            net.Draw(upperEngine, "Fuel", 100);
            Assert.Equal(800, lowerTank.GetResource("Fuel"), 6);
            Assert.Equal(900, upperTank.GetResource("Fuel"), 6);
            _ = dec;
        }

        [Fact]
        public void SolidFuel_NeverLeavesItsPart()
        {
            var b = new DesignBuilder(_catalog, "srb.flea");
            PartInstance srb = b.Last;
            PartInstance spark = b.Below("decoupler.td1");
            VesselDesign d = b.Finish();
            var net = new ResourceNetwork(d.Parts);
            Assert.Equal(1200, net.Available(srb, "SolidFuel"), 6);
            Assert.Equal(0, net.Available(spark, "SolidFuel"), 6);
        }

        [Fact]
        public void DeltaV_MatchesTsiolkovskyForSingleStage()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            PartInstance engine = b.Below("engine.swift");
            VesselDesign d = b.Finish();
            List<StageStats> stats = DeltaVCalculator.Compute(d, 9.81);
            Assert.Single(stats);
            double m0 = 250 + 2000 + 1250, m1 = 250 + 1250;
            EngineModuleDef e = engine.Def.GetModule<EngineModuleDef>();
            double expectedVac = e.IspVacuum * PhysicsConstants.G0 * System.Math.Log(m0 / m1);
            double expectedSl = e.IspSeaLevel * PhysicsConstants.G0 * System.Math.Log(m0 / m1);
            Assert.True(System.Math.Abs(stats[0].DeltaVVacuum - expectedVac) < 1.0, $"{stats[0].DeltaVVacuum} vs {expectedVac}");
            Assert.True(System.Math.Abs(stats[0].DeltaVSeaLevel - expectedSl) < 1.0);
            Assert.Equal(m0, stats[0].StartMass, 6);
            Assert.True(System.Math.Abs(stats[0].BurnTime - 2000 / e.MaxMassFlow) < 0.5);
            Assert.Equal(e.ThrustAt(1) / (m0 * 9.81), stats[0].TwrSeaLevel, 6);
        }

        [Fact]
        public void DeltaV_TwoStageRocket_SumsStages()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Below("tank.t200");
            b.Below("engine.swift");
            b.Below("decoupler.td1");
            b.Below("tank.t400");
            b.Below("engine.swift");
            VesselDesign d = b.Finish();
            List<StageStats> stats = DeltaVCalculator.Compute(d, 9.81);
            Assert.Equal(2, stats.Count);
            Assert.True(stats.All(s => s.DeltaVVacuum > 100));
            // Lower stage: carries the whole upper stack as payload.
            double upper = 840 + 10 + 125 + 1000 + 1250;
            double lowerFull = 50 + 250 + 2000 + 1250;
            double expectedLower = 310 * PhysicsConstants.G0 * System.Math.Log((upper + lowerFull) / (upper + lowerFull - 2000));
            Assert.True(System.Math.Abs(stats[0].DeltaVVacuum - expectedLower) < 2);
            double expectedUpper = 310 * PhysicsConstants.G0 * System.Math.Log(upper / (upper - 1000));
            Assert.True(System.Math.Abs(stats[1].DeltaVVacuum - expectedUpper) < 2);
        }
    }

    public class StagingTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();

        [Fact]
        public void AutoStage_OrdersBoostersCoreAndUpperStage()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            PartInstance chute = b.Above("chute.petal", b.Last);
            b.Below("tank.t200", b.Design.Parts[0]);
            PartInstance upperEngine = b.Below("engine.swift");
            PartInstance stackDec = b.Below("decoupler.td1");
            PartInstance core = b.Below("tank.t800");
            PartInstance coreEngine = b.Below("engine.swift");
            List<PartInstance> radialDecs = b.Radial("decoupler.radial", core, 2, -0.5);
            var boosters = new List<PartInstance>();
            foreach (PartInstance rd in radialDecs)
            {
                PartRayHit hit = Assembly.RaycastPart(rd, rd.LocalToVessel(new Vector3d(0, 0, 3)), rd.DirectionToVessel(new Vector3d(0, 0, -1))).Value;
                Placement pl = Assembly.SurfacePlacement(_catalog.Get("srb.hammer"), hit.Point, hit.Normal, 0);
                boosters.Add(b.Design.AddPart(_catalog.Get("srb.hammer"), rd.Id, PartInstance.SurfaceNode, PartInstance.SurfaceNode, pl.Position, pl.Rotation));
            }
            VesselDesign d = b.Finish();

            // Stage 0: boosters + core engine ignite together.
            Assert.Equal(0, coreEngine.Stage);
            Assert.All(boosters, x => Assert.Equal(0, x.Stage));
            // Stage 1: drop the boosters.
            Assert.All(radialDecs, x => Assert.Equal(1, x.Stage));
            // Stage 2: separate the core and light the upper stage.
            Assert.Equal(2, stackDec.Stage);
            Assert.Equal(2, upperEngine.Stage);
            // Stage 3: parachute.
            Assert.Equal(3, chute.Stage);
            Assert.Equal(4, d.StageCount);
        }

        [Fact]
        public void MoveToStage_MovesSymmetryGroupAndCompacts()
        {
            var b = new DesignBuilder(_catalog, "tank.t400");
            b.Below("engine.swift");
            List<PartInstance> decs = b.Radial("decoupler.radial", b.Design.Parts[0], 3, 0);
            VesselDesign d = b.Finish();
            Staging.MoveToStage(d, decs[0], 0);
            Assert.All(decs, x => Assert.Equal(0, x.Stage));
            Assert.Equal(1, d.StageCount);
        }
    }
}
