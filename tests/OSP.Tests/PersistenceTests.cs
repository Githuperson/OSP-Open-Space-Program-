using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Persistence;
using OSP.Core.Science;
using OSP.Core.Vehicles;
using Xunit;

namespace OSP.Tests
{
    public class PersistenceTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();
        private readonly CelestialSystem _system = TestData.LoadSystem();

        private VesselDesign Rocket()
        {
            var b = new DesignBuilder(_catalog, "pod.aquila");
            b.Above("chute.petal", b.Last);
            b.Below("tank.t200", b.Design.Parts[0]);
            b.Below("engine.swift");
            b.Below("decoupler.td1");
            PartInstance core = b.Below("tank.t400");
            b.Below("engine.vector");
            b.Radial("fin.delta", core, 3, -0.6);
            VesselDesign d = b.Finish();
            d.Name = "Round Trip Rocket";
            d.Parts[2].Resources["Fuel"] = 123.5;
            return d;
        }

        [Fact]
        public void Blueprint_RoundTripPreservesEverything()
        {
            VesselDesign d = Rocket();
            string json = BlueprintSerializer.Serialize(d);
            VesselDesign r = BlueprintSerializer.Deserialize(json, _catalog, out List<string> warnings);
            Assert.Empty(warnings);
            Assert.Equal(d.Name, r.Name);
            Assert.Equal(d.Parts.Count, r.Parts.Count);
            for (int i = 0; i < d.Parts.Count; i++)
            {
                PartInstance a = d.Parts[i], b = r.Parts[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Part, b.Part);
                Assert.Equal(a.Parent, b.Parent);
                Assert.Equal(a.ParentNode, b.ParentNode);
                Assert.Equal(a.Stage, b.Stage);
                Assert.Equal(a.SymmetryGroup, b.SymmetryGroup);
                Assert.True((a.Position - b.Position).Length < 1e-12);
                Assert.NotNull(b.Def);
            }
            Assert.Equal(123.5, r.Parts[2].Resources["Fuel"]);
            Assert.Equal(MassProperties.Compute(d.Parts).Mass, MassProperties.Compute(r.Parts).Mass, 9);
            Assert.Equal(d.StageCount, r.StageCount);
        }

        [Fact]
        public void Blueprint_InvalidDataIsRejectedOrRepaired()
        {
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize("{ this is not json", _catalog, out _));
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize("{\"format\":\"something-else\",\"version\":1,\"vessel\":{}}", _catalog, out _));
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize("{\"format\":\"osp-vessel\",\"version\":99,\"vessel\":{\"parts\":[]}}", _catalog, out _));
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize("{\"format\":\"osp-vessel\",\"version\":1}", _catalog, out _));

            // Duplicate ids.
            string dup = "{\"format\":\"osp-vessel\",\"version\":1,\"vessel\":{\"parts\":[{\"id\":1,\"part\":\"pod.aquila\",\"parent\":-1},{\"id\":1,\"part\":\"tank.t200\",\"parent\":1}]}}";
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize(dup, _catalog, out _));
            // Cycle: no root.
            string cycle = "{\"format\":\"osp-vessel\",\"version\":1,\"vessel\":{\"parts\":[{\"id\":1,\"part\":\"pod.aquila\",\"parent\":2},{\"id\":2,\"part\":\"tank.t200\",\"parent\":1}]}}";
            Assert.Throws<FormatException>(() => BlueprintSerializer.Deserialize(cycle, _catalog, out _));

            // Unknown part with children: removed with a warning; bad values repaired.
            string repair = @"{""format"":""osp-vessel"",""version"":1,""vessel"":{""name"":""Fixme"",""parts"":[
                {""id"":1,""part"":""pod.aquila"",""parent"":-1,""rotation"":[0,0,0,1]},
                {""id"":2,""part"":""tank.t200"",""parent"":1,""resources"":{""Fuel"":99999,""Oxidizer"":-5,""Unobtainium"":3},""stage"":4},
                {""id"":3,""part"":""mystery.part"",""parent"":2},
                {""id"":4,""part"":""engine.swift"",""parent"":3}]}}";
            VesselDesign r = BlueprintSerializer.Deserialize(repair, _catalog, out List<string> warnings);
            Assert.Equal(2, r.Parts.Count);
            Assert.Contains(warnings, w => w.Contains("mystery.part"));
            Assert.Equal(450, r.Parts[1].Resources["Fuel"]);
            Assert.Equal(0, r.Parts[1].Resources["Oxidizer"]);
            Assert.False(r.Parts[1].Resources.ContainsKey("Unobtainium"));
            Assert.Equal(-1, r.Parts[1].Stage); // tanks are not stageable
        }

        [Fact]
        public void SaveGame_RoundTripRestoresFlightState()
        {
            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.Launch(Rocket());
            v.Control.Throttle = 1;
            w.ActivateNextStage(v);
            for (int i = 0; i < 300; i++) w.StepPhysics(FlightWorld.PhysicsStep);
            v.Nodes.Add(new ManeuverNode { Time = w.Time + 600, Prograde = 120, Normal = -5, Radial = 3 });
            w.TargetBody = _system.Get("tessar");
            var progress = new ProgressState { Mode = GameMode.Career, Funds = 12345, Science = 42 };
            progress.UnlockedTech.Add("basicRocketry");

            SaveGame save = SaveGame.Capture(w, progress, Rocket(), "test");
            string json = save.ToJson();
            SaveGame loaded = SaveGame.FromJson(json, _catalog, _system, out List<string> warnings);
            Assert.Empty(warnings);
            FlightWorld w2 = loaded.Restore(_system, _catalog);

            Assert.Equal(w.Time, w2.Time, 9);
            Assert.Equal(w.Vessels.Count, w2.Vessels.Count);
            Vessel v2 = w2.ActiveVessel;
            Assert.Equal(v.Id, v2.Id);
            Assert.True((v.Position - v2.Position).Length < 1e-6);
            Assert.True((v.Velocity - v2.Velocity).Length < 1e-9);
            Assert.True((v.AngularVelocity - v2.AngularVelocity).Length < 1e-12);
            Assert.Equal(v.Mass, v2.Mass, 6);
            Assert.Equal(v.CurrentStage, v2.CurrentStage);
            Assert.Equal(v.Control.Throttle, v2.Control.Throttle);
            Assert.Single(v2.Nodes);
            Assert.Equal(120, v2.Nodes[0].Prograde);
            Assert.Equal("tessar", w2.TargetBody.Id);
            Assert.Equal(GameMode.Career, loaded.Progress.Mode);
            Assert.Equal(12345, loaded.Progress.Funds);
            Assert.Contains("basicRocketry", loaded.Progress.UnlockedTech);
            Assert.NotNull(loaded.LaunchDesign);
            Assert.True(v2.Parts.First(p => p.Def.HasModule<EngineModuleDef>() && p.Stage == 0).State.Activated);

            // Both worlds continue identically (deterministic simulation).
            for (int i = 0; i < 100; i++)
            {
                w.StepPhysics(FlightWorld.PhysicsStep);
                w2.StepPhysics(FlightWorld.PhysicsStep);
            }
            Assert.True((w.ActiveVessel.Position - w2.ActiveVessel.Position).Length < 1e-3);
        }

        [Fact]
        public void SaveGame_InvalidDataIsHandledSafely()
        {
            Assert.Throws<FormatException>(() => SaveGame.FromJson("[]", _catalog, _system, out _));
            Assert.Throws<FormatException>(() => SaveGame.FromJson("{\"format\":\"osp-save\",\"version\":2}", _catalog, _system, out _));
            Assert.Throws<FormatException>(() => SaveGame.FromJson("{\"format\":\"osp-save\",\"version\":1,\"universalTime\":-5}", _catalog, _system, out _));

            var w = new FlightWorld(_system, _catalog);
            w.Launch(Rocket());
            SaveGame save = SaveGame.Capture(w, new ProgressState(), null, "x");
            save.Vessels.Add(new VesselSave { Id = 999, Name = "Ghost", Body = "nowhere", Parts = save.Vessels[0].Parts });
            save.Vessels.Add(new VesselSave { Id = 998, Name = "Broken", Body = "aster", Position = new Vector3d(double.NaN, 0, 0), Parts = save.Vessels[0].Parts });
            string json = save.ToJson();
            SaveGame loaded = SaveGame.FromJson(json, _catalog, _system, out List<string> warnings);
            Assert.Single(loaded.Vessels);
            Assert.Contains(warnings, x => x.Contains("Ghost"));
            Assert.Contains(warnings, x => x.Contains("Broken"));
            // Missing optional sections fall back to defaults.
            SaveGame minimal = SaveGame.FromJson("{\"format\":\"osp-save\",\"version\":1,\"universalTime\":10}", _catalog, _system, out _);
            Assert.Empty(minimal.Vessels);
            Assert.NotNull(minimal.Progress);
            Assert.Contains("start", minimal.Progress.UnlockedTech);
        }
    }

    public class ScienceTests
    {
        private readonly PartCatalog _catalog = TestData.LoadCatalog();
        private readonly CelestialSystem _system = TestData.LoadSystem();

        [Fact]
        public void Situation_IsDerivedFromVesselState()
        {
            var w = new FlightWorld(_system, _catalog);
            var b = new DesignBuilder(_catalog, "pod.aquila");
            Vessel v = w.Launch(b.Finish());
            Assert.Equal(ScienceSituation.Landed, ScienceRules.Classify(v));
            v.Pinned = false;
            w.PlaceInOrbit(v, _system.HomeBody, 100000);
            w.UpdateSituation(v);
            Assert.Equal(ScienceSituation.SpaceLow, ScienceRules.Classify(v));
            w.PlaceInOrbit(v, _system.HomeBody, 5000);
            w.UpdateSituation(v);
            Assert.Equal(ScienceSituation.FlyingLow, ScienceRules.Classify(v));

            ExperimentDefinition seismo = ScienceRules.Get("seismometer");
            Assert.Null(ScienceRules.TryRun(v, seismo, w.Time, out string reason));
            Assert.Contains("cannot", reason);
            Assert.NotNull(ScienceRules.TryRun(v, ScienceRules.Get("barometer"), w.Time, out _));
        }

        [Fact]
        public void Science_HasDiminishingReturns_AndUnlocksResearch()
        {
            var progress = new ProgressState { Mode = GameMode.Science };
            var result = new ScienceResult { ExperimentId = "thermometer", BodyId = "tessar", Situation = ScienceSituation.Landed, Biome = "Lowlands" };
            double full = ScienceRules.Value(ScienceRules.Get("thermometer"), _system.Get("tessar"), ScienceSituation.Landed);
            double first = progress.Credit(result, _system, 1.0);
            Assert.Equal(full, first, 6);
            Assert.Equal(0, progress.Credit(result, _system, 1.0), 6);
            var transmitted = new ScienceResult { ExperimentId = "thermometer", BodyId = "aster", Situation = ScienceSituation.SpaceLow };
            double half = progress.Credit(transmitted, _system, 0.5);
            double again = progress.Credit(transmitted, _system, 0.5);
            Assert.Equal(half / 2, again, 2);

            TechTree tree = TechTree.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(TestData.ProjectRoot, "data", "techtree.json")));
            Assert.True(progress.Science >= 5);
            Assert.True(progress.Research(tree, "basicRocketry"));
            Assert.False(progress.Research(tree, "heavyRocketry")); // prerequisites missing
            Assert.True(progress.IsPartAvailable(_catalog.Get("engine.vector")));
            Assert.False(progress.IsPartAvailable(_catalog.Get("engine.titan")));
            // Every part's tech exists in the tree.
            Assert.All(_catalog.All, p => Assert.NotNull(tree.Get(p.Tech)));
        }

        [Fact]
        public void Contracts_CompleteFromSimulatedState()
        {
            ContractList contracts = ContractList.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(TestData.ProjectRoot, "data", "contracts.json")));
            var progress = new ProgressState { Mode = GameMode.Career, Funds = 0 };
            ContractDefinition first = progress.OfferedContracts(contracts).First(c => c.Id == "firstLaunch");
            Assert.DoesNotContain(progress.OfferedContracts(contracts), c => c.Id == "orbit"); // locked behind prerequisites
            progress.Accept(first);
            Assert.Equal(first.AdvanceFunds, progress.Funds);

            var w = new FlightWorld(_system, _catalog);
            Vessel v = w.Launch(new DesignBuilder(_catalog, "pod.aquila").Finish());
            Assert.False(ContractEvaluator.IsMet(first, v, _system, progress));
            v.Pinned = false;
            w.PlaceInOrbit(v, _system.HomeBody, 1500);
            Assert.True(ContractEvaluator.IsMet(first, v, _system, progress));
            Assert.True(progress.Complete(first));
            Assert.Equal(first.AdvanceFunds + first.RewardFunds, progress.Funds);
            Assert.Contains(progress.OfferedContracts(contracts), c => c.Id == "highAltitude");
        }
    }
}
