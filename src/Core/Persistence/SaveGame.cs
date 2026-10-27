using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Science;
using OSP.Core.Vehicles;

namespace OSP.Core.Persistence
{
    public sealed class VesselSave
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public VesselType Type { get; set; }
        public string Body { get; set; }
        public Vector3d Position { get; set; }
        public Vector3d Velocity { get; set; }
        public QuaternionD Rotation { get; set; } = QuaternionD.Identity;
        public Vector3d AngularVelocity { get; set; }
        public bool Pinned { get; set; }
        public Vector3d PinnedPosition { get; set; }
        public QuaternionD PinnedRotation { get; set; } = QuaternionD.Identity;
        public Situation Situation { get; set; }
        public int CurrentStage { get; set; }
        public double LaunchTime { get; set; }
        public double Throttle { get; set; }
        public bool SasEnabled { get; set; }
        public SasMode SasMode { get; set; }
        public bool RcsEnabled { get; set; }
        public List<ManeuverNode> Nodes { get; set; } = new();
        public List<PartInstance> Parts { get; set; } = new();
    }

    /// <summary>
    /// A persistent game (*.osp-save.json): universal time, every vessel's full state, and progression.
    /// Celestial bodies are on fixed rails, so their state is fully determined by the universal time and is not
    /// stored. See docs/SAVE_FORMAT.md.
    /// </summary>
    public sealed class SaveGame
    {
        public const string FormatName = "osp-save";
        public const int CurrentVersion = 1;

        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = "Save";
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        public double UniversalTime { get; set; }
        public int ActiveVesselId { get; set; } = -1;
        public string TargetBody { get; set; }
        public int TargetVesselId { get; set; } = -1;
        public List<VesselSave> Vessels { get; set; } = new();
        public ProgressState Progress { get; set; } = new();
        /// <summary>Blueprint of the active vessel at launch, so "revert to launch" survives a reload.</summary>
        public VesselDesign LaunchDesign { get; set; }

        public static SaveGame Capture(FlightWorld world, ProgressState progress, VesselDesign launchDesign, string name)
        {
            var save = new SaveGame
            {
                Name = name,
                UniversalTime = world.Time,
                ActiveVesselId = world.ActiveVessel?.Id ?? -1,
                TargetBody = world.TargetBody?.Id,
                TargetVesselId = world.TargetVessel?.Id ?? -1,
                Progress = progress ?? new ProgressState(),
                LaunchDesign = launchDesign?.Clone(),
            };
            foreach (Vessel v in world.Vessels)
            {
                Vector3d pos = v.Position, vel = v.Velocity;
                if (v.OnRails && v.RailsOrbit != null) v.RailsOrbit.GetStateAt(world.Time, out pos, out vel);
                save.Vessels.Add(new VesselSave
                {
                    Id = v.Id,
                    Name = v.Name,
                    Type = v.Type,
                    Body = v.Body.Id,
                    Position = pos,
                    Velocity = vel,
                    Rotation = v.Rotation,
                    AngularVelocity = v.AngularVelocity,
                    Pinned = v.Pinned,
                    PinnedPosition = v.PinnedBodyFixedPosition,
                    PinnedRotation = v.PinnedBodyFixedRotation,
                    Situation = v.Situation,
                    CurrentStage = v.CurrentStage,
                    LaunchTime = v.LaunchTime,
                    Throttle = v.Control.Throttle,
                    SasEnabled = v.Control.SasEnabled,
                    SasMode = v.Control.SasMode,
                    RcsEnabled = v.Control.RcsEnabled,
                    Nodes = v.Nodes.Select(n => n.Clone()).ToList(),
                    Parts = v.Parts.Select(p => p.Clone()).ToList(),
                });
            }
            if (save.LaunchDesign != null)
                foreach (PartInstance p in save.LaunchDesign.Parts) p.State = null;
            return save;
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonUtil.Options);

        /// <summary>Parses and validates a save file. Throws <see cref="FormatException"/> if unusable.</summary>
        public static SaveGame FromJson(string json, PartCatalog catalog, CelestialSystem system, out List<string> warnings)
        {
            warnings = new List<string>();
            SaveGame s;
            try
            {
                s = JsonSerializer.Deserialize<SaveGame>(json, JsonUtil.Options);
            }
            catch (JsonException ex)
            {
                throw new FormatException("The save file is not valid JSON: " + ex.Message, ex);
            }
            if (s == null) throw new FormatException("The save file is empty.");
            if (s.Format != FormatName) throw new FormatException($"Not a save file (format '{s.Format}').");
            if (s.Version > CurrentVersion) throw new FormatException($"The save was written by a newer version of the game (v{s.Version}).");
            if (s.Version < 1) throw new FormatException($"Unsupported save version {s.Version}.");
            if (!double.IsFinite(s.UniversalTime) || s.UniversalTime < 0) throw new FormatException("The save has an invalid universal time.");
            s.Vessels ??= new List<VesselSave>();
            s.Progress ??= new ProgressState();
            s.Progress.Validate(catalog);

            foreach (VesselSave vs in s.Vessels.ToList())
            {
                string problem = ValidateVessel(vs, catalog, system, warnings);
                if (problem != null)
                {
                    warnings.Add($"Vessel '{vs.Name}' was removed: {problem}");
                    s.Vessels.Remove(vs);
                }
            }
            if (s.Vessels.All(v => v.Id != s.ActiveVesselId)) s.ActiveVesselId = s.Vessels.FirstOrDefault()?.Id ?? -1;
            if (s.LaunchDesign != null)
            {
                try
                {
                    BlueprintSerializer.ValidateDesign(s.LaunchDesign, catalog, warnings);
                }
                catch (FormatException ex)
                {
                    warnings.Add("Launch blueprint discarded: " + ex.Message);
                    s.LaunchDesign = null;
                }
            }
            return s;
        }

        private static string ValidateVessel(VesselSave vs, PartCatalog catalog, CelestialSystem system, List<string> warnings)
        {
            if (system.Get(vs.Body) == null) return $"unknown celestial body '{vs.Body}'";
            if (vs.Parts == null || vs.Parts.Count == 0) return "it has no parts";
            if (!vs.Position.IsFinite || !vs.Velocity.IsFinite) return "invalid position or velocity";
            if (!vs.Rotation.IsFinite) vs.Rotation = QuaternionD.Identity;
            if (!vs.AngularVelocity.IsFinite) vs.AngularVelocity = Vector3d.Zero;
            if (!vs.PinnedRotation.IsFinite) vs.PinnedRotation = QuaternionD.Identity;
            if (vs.Pinned && !vs.PinnedPosition.IsFinite) vs.Pinned = false;
            vs.Nodes ??= new List<ManeuverNode>();
            vs.Nodes.RemoveAll(n => !double.IsFinite(n.Time) || !double.IsFinite(n.Prograde) || !double.IsFinite(n.Normal) || !double.IsFinite(n.Radial));
            vs.Throttle = MathUtil.Clamp01(double.IsFinite(vs.Throttle) ? vs.Throttle : 0);
            var tmp = new VesselDesign { Name = vs.Name, Parts = vs.Parts };
            try
            {
                BlueprintSerializer.ValidateDesign(tmp, catalog, warnings);
            }
            catch (FormatException ex)
            {
                return ex.Message;
            }
            vs.Parts = tmp.Parts;
            if (vs.Parts.Count == 0) return "all of its parts are missing";
            foreach (PartInstance p in vs.Parts) p.State ??= new PartFlightState();
            return null;
        }

        /// <summary>Rebuilds a flight world from this save.</summary>
        public FlightWorld Restore(CelestialSystem system, PartCatalog catalog)
        {
            var world = new FlightWorld(system, catalog) { Time = UniversalTime };
            foreach (VesselSave vs in Vessels)
            {
                var v = new Vessel
                {
                    Id = vs.Id,
                    Name = vs.Name,
                    Type = vs.Type,
                    Body = system.Get(vs.Body),
                    Position = vs.Position,
                    Velocity = vs.Velocity,
                    Rotation = vs.Rotation.Normalized(),
                    AngularVelocity = vs.AngularVelocity,
                    Pinned = vs.Pinned,
                    PinnedBodyFixedPosition = vs.PinnedPosition,
                    PinnedBodyFixedRotation = vs.PinnedRotation.Normalized(),
                    Situation = vs.Situation,
                    CurrentStage = System.Math.Max(0, vs.CurrentStage),
                    LaunchTime = vs.LaunchTime,
                };
                Vessel.EnsureIdAbove(vs.Id);
                v.Control.Throttle = vs.Throttle;
                v.Control.SasEnabled = vs.SasEnabled;
                v.Control.SasMode = vs.SasMode;
                v.Control.RcsEnabled = vs.RcsEnabled;
                v.Nodes.AddRange(vs.Nodes);
                foreach (PartInstance p in vs.Parts) v.Parts.Add(p);
                v.RebuildTopology();
                if (v.Pinned) VesselPhysics.ApplyPinnedState(v, UniversalTime);
                world.AddVessel(v);
            }
            world.TargetBody = system.Get(TargetBody);
            world.TargetVessel = world.Vessels.FirstOrDefault(v => v.Id == TargetVesselId);
            Vessel active = world.Vessels.FirstOrDefault(v => v.Id == ActiveVesselId);
            if (active != null) world.SetActive(active);
            foreach (Vessel v in world.Vessels) world.UpdateSituation(v);
            return world;
        }
    }
}
