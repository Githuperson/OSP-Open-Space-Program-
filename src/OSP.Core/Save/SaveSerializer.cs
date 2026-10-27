using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Util;
using OSP.Core.Vehicles;

namespace OSP.Core.Save;

/// <summary>
/// Versioned JSON persistence for vehicle designs and complete flight states. Loading validates everything it reads and
/// throws <see cref="DataException"/> with a human-readable reason instead of producing a half-built object.
/// </summary>
public static class SaveSerializer
{
    // ---------------- vehicle designs ----------------
    public static string SerializeDesign(VehicleDesign d) =>
        Json.Serialize(new VehicleFile { FormatVersion = VehicleDesign.CurrentFormatVersion, Design = d });

    public static VehicleDesign DeserializeDesign(string json, PartCatalog catalog)
    {
        var f = Json.Deserialize<VehicleFile>(json, "vehicle file");
        if (f.KindTag != VehicleFile.Kind) throw new DataException("Not an OSP vehicle file");
        if (f.FormatVersion > VehicleDesign.CurrentFormatVersion)
            throw new DataException($"Vehicle was saved by a newer version (format {f.FormatVersion}, this build supports {VehicleDesign.CurrentFormatVersion})");
        if (f.FormatVersion < 1) throw new DataException($"Unsupported vehicle format version {f.FormatVersion}");
        var d = f.Design ?? throw new DataException("Vehicle file has no design");
        d.Name = string.IsNullOrWhiteSpace(d.Name) ? "Untitled Vehicle" : d.Name;
        var issues = VehicleValidator.Validate(d, catalog).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (issues.Count > 0) throw new DataException("Vehicle failed validation: " + string.Join("; ", issues.Select(i => i.Message)));
        // Re-derive consistent ids for future placement.
        d.NextPartId = Math.Max(d.NextPartId, d.Parts.Max(p => p.Id) + 1);
        d.NextSymmetryGroup = Math.Max(d.NextSymmetryGroup, d.Parts.Max(p => p.SymmetryGroup) + 1);
        return d;
    }

    // ---------------- flight state ----------------
    public static SaveGame Capture(FlightSimulation sim, string name, ProgressSave? progress = null)
    {
        var s = new SaveGame
        {
            Name = name, SavedAtUtc = DateTime.UtcNow.ToString("o"), SystemName = sim.System.Name,
            UniversalTime = sim.Time, ActiveCraftId = sim.Active?.Id ?? -1, NextCraftId = sim.Crafts.Count == 0 ? 1 : sim.Crafts.Max(c => c.Id) + 1,
            Progress = progress ?? new ProgressSave(),
        };
        foreach (var c in sim.Crafts)
        {
            var cs = new CraftSave
            {
                Id = c.Id, Name = c.Name, BodyId = c.Body.Id, Mode = (int)c.Mode,
                Position = V(c.Position), Velocity = V(c.Velocity), Orientation = Q(c.Orientation), AngularVelocity = V(c.AngularVelocity),
                LandedLocalPosition = V(c.LandedLocalPosition), LandedLocalOrientation = Q(c.LandedLocalOrientation),
                CurrentStage = c.CurrentStage, HasLaunched = c.HasLaunched, MissionStartTime = c.MissionStartTime,
                Throttle = c.Control.Throttle, SasEnabled = c.Control.SasEnabled, SasMode = (int)c.Control.Sas, RcsEnabled = c.Control.RcsEnabled,
            };
            foreach (var p in c.Parts)
                cs.Parts.Add(new PartStateSave
                {
                    Id = p.Id, PartId = p.Def.Id, ParentId = p.ParentId, Mode = (int)p.Mode, ParentNode = p.ParentNodeId, ChildNode = p.ChildNodeId,
                    Stage = p.Stage, SymmetryGroup = p.SymmetryGroup, Position = V(p.Position), Rotation = Q(p.Rotation),
                    Resources = new Dictionary<string, double>(p.Resources), Ignited = p.Ignited, FlamedOut = p.FlamedOut,
                    LegsDown = p.LegsDeployedTarget, LegDeploy = p.LegDeploy, PanelsOut = p.SolarDeployedTarget, SolarDeploy = p.SolarDeploy,
                    Chute = (int)p.Chute, ChuteProgress = p.ChuteProgress,
                });
            foreach (var n in sim.NodesFor(c))
                cs.Maneuvers.Add(new ManeuverSave { Id = n.Id, Time = n.Time, Prograde = n.Prograde, Normal = n.Normal, Radial = n.Radial });
            s.Crafts.Add(cs);
        }
        return s;
    }

    public static string Serialize(SaveGame s) => Json.Serialize(s);

    public static FlightSimulation Restore(string json, StarSystem system, PartCatalog catalog, out SaveGame save)
    {
        save = Json.Deserialize<SaveGame>(json, "save game");
        if (save.KindTag != SaveGame.Kind) throw new DataException("Not an OSP save file");
        if (save.FormatVersion > SaveGame.CurrentVersion)
            throw new DataException($"Save was written by a newer version (format {save.FormatVersion}; this build reads up to {SaveGame.CurrentVersion})");
        if (save.FormatVersion < 1) throw new DataException($"Unsupported save format {save.FormatVersion}");
        if (!double.IsFinite(save.UniversalTime) || save.UniversalTime < 0) throw new DataException("Invalid universal time");

        var sim = new FlightSimulation(system, catalog) { Time = save.UniversalTime };
        foreach (var cs in save.Crafts)
        {
            if (!system.TryGet(cs.BodyId, out var body)) throw new DataException($"Craft '{cs.Name}': unknown body '{cs.BodyId}'");
            Need(cs.Position, 3, "position"); Need(cs.Velocity, 3, "velocity"); Need(cs.Orientation, 4, "orientation");
            Need(cs.AngularVelocity, 3, "angularVelocity"); Need(cs.LandedLocalPosition, 3, "landedLocalPosition"); Need(cs.LandedLocalOrientation, 4, "landedLocalOrientation");
            if (cs.Mode < 0 || cs.Mode > 2) throw new DataException($"Craft '{cs.Name}': invalid mode {cs.Mode}");
            if (cs.Parts.Count == 0) throw new DataException($"Craft '{cs.Name}' has no parts");

            var craft = new Craft(catalog)
            {
                Id = cs.Id, Name = cs.Name, Body = body, Mode = (CraftMode)cs.Mode,
                Position = Vd(cs.Position), Velocity = Vd(cs.Velocity), Orientation = Qd(cs.Orientation),
                AngularVelocity = Vd(cs.AngularVelocity), LandedLocalPosition = Vd(cs.LandedLocalPosition),
                LandedLocalOrientation = Qd(cs.LandedLocalOrientation), CurrentStage = cs.CurrentStage, HasLaunched = cs.HasLaunched,
                MissionStartTime = cs.MissionStartTime,
            };
            if (!craft.Position.IsFinite || !craft.Velocity.IsFinite) throw new DataException($"Craft '{cs.Name}': non-finite state");
            craft.Control.Throttle = Math.Clamp(cs.Throttle, 0, 1);
            craft.Control.SasEnabled = cs.SasEnabled;
            craft.Control.Sas = Enum.IsDefined(typeof(SasMode), cs.SasMode) ? (SasMode)cs.SasMode : SasMode.StabilityAssist;
            craft.Control.RcsEnabled = cs.RcsEnabled;
            craft.Control.HoldOrientation = craft.Orientation; craft.Control.HoldValid = true;

            foreach (var ps in cs.Parts)
            {
                if (!catalog.TryGet(ps.PartId, out var def)) throw new DataException($"Craft '{cs.Name}': unknown part '{ps.PartId}'");
                Need(ps.Position, 3, "part position"); Need(ps.Rotation, 4, "part rotation");
                var cp = new CraftPart(ps.Id, def, ps.ParentId, Vd(ps.Position), Qd(ps.Rotation), ps.Stage)
                {
                    SymmetryGroup = ps.SymmetryGroup, Mode = (AttachMode)ps.Mode, ParentNodeId = ps.ParentNode, ChildNodeId = ps.ChildNode,
                    Ignited = ps.Ignited, FlamedOut = ps.FlamedOut, LegsDeployedTarget = ps.LegsDown, LegDeploy = ps.LegDeploy,
                    SolarDeployedTarget = ps.PanelsOut, SolarDeploy = ps.SolarDeploy,
                    Chute = Enum.IsDefined(typeof(ChuteState), ps.Chute) ? (ChuteState)ps.Chute : ChuteState.Stowed, ChuteProgress = ps.ChuteProgress,
                };
                foreach (var (res, amt) in ps.Resources)
                {
                    if (!PartCatalog.Resources.ContainsKey(res)) throw new DataException($"Part {ps.Id}: unknown resource '{res}'");
                    if (!double.IsFinite(amt) || amt < 0) throw new DataException($"Part {ps.Id}: invalid amount for '{res}'");
                    cp.Resources[res] = Math.Min(amt, def.ResourceCapacity(res));
                }
                craft.Parts.Add(cp);
            }
            var ids = craft.Parts.Select(p => p.Id).ToHashSet();
            if (ids.Count != craft.Parts.Count) throw new DataException($"Craft '{cs.Name}': duplicate part ids");
            foreach (var p in craft.Parts)
                if (p.ParentId >= 0 && !ids.Contains(p.ParentId)) throw new DataException($"Craft '{cs.Name}': part {p.Id} has missing parent {p.ParentId}");
            if (craft.Parts.Count(p => p.ParentId < 0) != 1) throw new DataException($"Craft '{cs.Name}': must have exactly one root part");
            craft.RebuildStructure();
            craft.UpdateMassPropertiesFresh();
            sim.AddCraft(craft, craft.Id == save.ActiveCraftId);
            foreach (var n in cs.Maneuvers)
                sim.NodesFor(craft).Add(new ManeuverNode { Id = n.Id, Time = n.Time, Prograde = n.Prograde, Normal = n.Normal, Radial = n.Radial });
        }
        if (save.ActiveCraftId >= 0 && sim.Active?.Id != save.ActiveCraftId && save.Crafts.Count > 0)
            throw new DataException("Active craft id not found in save");
        if (sim.Active != null && sim.Active.Mode == CraftMode.Landed) sim.Active.SyncFromSurface(sim.Time);
        return sim;
    }

    private static void Need(double[] a, int n, string what)
    {
        if (a == null || a.Length != n || a.Any(v => !double.IsFinite(v))) throw new DataException($"Invalid {what}: expected {n} finite numbers");
    }
    private static double[] V(Vec3d v) => new[] { v.X, v.Y, v.Z };
    private static double[] Q(QuatD q) => new[] { q.W, q.X, q.Y, q.Z };
    private static Vec3d Vd(double[] a) => new(a[0], a[1], a[2]);
    private static QuatD Qd(double[] a) => new QuatD(a[0], a[1], a[2], a[3]).Normalized;
}
