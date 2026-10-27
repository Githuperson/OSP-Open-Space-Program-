using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Persistence
{
    /// <summary>
    /// Vessel blueprint files (*.osp-vessel.json). Format (version 1):
    /// { "format": "osp-vessel", "version": 1, "vessel": { "name", "description", "parts": [ PartInstance... ], ... } }
    /// See docs/SAVE_FORMAT.md. Loading validates the structure and repairs what it safely can, reporting warnings.
    /// </summary>
    public static class BlueprintSerializer
    {
        public const string Format = "osp-vessel";
        public const int CurrentVersion = 1;

        private sealed class BlueprintFile
        {
            public string Format { get; set; } = BlueprintSerializer.Format;
            public int Version { get; set; } = CurrentVersion;
            public VesselDesign Vessel { get; set; }
        }

        public static string Serialize(VesselDesign design)
        {
            var copy = design.Clone();
            foreach (PartInstance p in copy.Parts) p.State = null; // blueprints never carry flight state
            return JsonSerializer.Serialize(new BlueprintFile { Vessel = copy }, JsonUtil.Options);
        }

        /// <summary>Parses and validates a blueprint. Throws <see cref="FormatException"/> if it cannot be used at all.</summary>
        public static VesselDesign Deserialize(string json, PartCatalog catalog, out List<string> warnings)
        {
            warnings = new List<string>();
            BlueprintFile file;
            try
            {
                file = JsonSerializer.Deserialize<BlueprintFile>(json, JsonUtil.Options);
            }
            catch (JsonException ex)
            {
                throw new FormatException("The vessel file is not valid JSON: " + ex.Message, ex);
            }
            if (file == null || file.Vessel == null) throw new FormatException("The vessel file has no 'vessel' section.");
            if (file.Format != Format) throw new FormatException($"Not a vessel file (format '{file.Format}').");
            if (file.Version > CurrentVersion) throw new FormatException($"The vessel was saved by a newer version of the game (format v{file.Version}).");
            if (file.Version < 1) throw new FormatException($"Unsupported vessel format version {file.Version}.");

            VesselDesign d = file.Vessel;
            d.Name = string.IsNullOrWhiteSpace(d.Name) ? "Untitled Rocket" : d.Name.Trim();
            d.Parts ??= new List<PartInstance>();
            ValidateDesign(d, catalog, warnings);
            return d;
        }

        /// <summary>Validates and repairs a part tree in place. Shared by blueprints and save games.</summary>
        public static void ValidateDesign(VesselDesign d, PartCatalog catalog, List<string> warnings)
        {
            if (d.Parts.Count == 0) return;

            // Duplicate ids are fatal: connections would be ambiguous.
            var ids = new HashSet<int>();
            foreach (PartInstance p in d.Parts)
                if (!ids.Add(p.Id)) throw new FormatException($"Duplicate part id {p.Id}.");

            // Exactly one root.
            var roots = d.Parts.Where(p => p.Parent < 0 || !ids.Contains(p.Parent)).ToList();
            if (roots.Count == 0) throw new FormatException("The vessel has no root part (parent cycle).");
            PartInstance root = roots.FirstOrDefault(p => p.Parent < 0) ?? roots[0];
            foreach (PartInstance orphan in roots.Where(r => r != root))
            {
                warnings.Add($"Part {orphan.Id} ({orphan.Part}) was not connected to the vessel and was removed.");
            }
            foreach (PartInstance orphan in roots.Where(r => r != root).ToList()) d.RemoveSubtree(orphan.Id);
            root.Parent = -1;

            // Cycle check: every part must reach the root.
            foreach (PartInstance p in d.Parts)
            {
                int guard = 0;
                for (PartInstance q = p; q != null && q.Parent >= 0; q = d.Get(q.Parent))
                    if (++guard > d.Parts.Count) throw new FormatException("The part tree contains a cycle.");
            }

            // Unknown parts: drop them and anything attached below them.
            foreach (PartInstance p in d.Parts.ToList())
            {
                if (d.Get(p.Id) == null) continue;
                if (catalog.Get(p.Part) == null)
                {
                    if (p == root) throw new FormatException($"The root part '{p.Part}' is not installed.");
                    warnings.Add($"Unknown part '{p.Part}' and the parts attached to it were removed.");
                    d.RemoveSubtree(p.Id);
                }
            }
            d.Resolve(catalog);

            foreach (PartInstance p in d.Parts)
            {
                if (!p.Position.IsFinite) { p.Position = Math.Vector3d.Zero; warnings.Add($"Part {p.Id} had an invalid position."); }
                if (!p.Rotation.IsFinite) { p.Rotation = Math.QuaternionD.Identity; warnings.Add($"Part {p.Id} had an invalid rotation."); }
                p.Rotation = p.Rotation.Normalized();
                if (p.Stage < -1) p.Stage = -1;
                if (!Staging.IsStageable(p)) p.Stage = -1;
                if (!(p.ThrustLimit >= 0 && p.ThrustLimit <= 1)) p.ThrustLimit = 1;
                p.Resources ??= new Dictionary<string, double>();
                foreach (string key in p.Resources.Keys.ToList())
                {
                    double max = p.GetResourceMax(key);
                    if (max <= 0) { p.Resources.Remove(key); continue; }
                    double v = p.Resources[key];
                    if (!double.IsFinite(v) || v < 0) p.Resources[key] = 0;
                    else if (v > max) p.Resources[key] = max;
                }
                if (p.FuelLineTarget >= 0 && d.Get(p.FuelLineTarget) == null) p.FuelLineTarget = -1;
            }
            int maxId = d.Parts.Max(p => p.Id);
            if (d.NextId <= maxId) d.NextId = maxId + 1;
            int maxSym = d.Parts.Select(p => p.SymmetryGroup).DefaultIfEmpty(0).Max();
            if (d.NextSymmetryGroup <= maxSym) d.NextSymmetryGroup = maxSym + 1;
            Staging.Compact(d);
            // Root first.
            if (d.Parts[0] != root)
            {
                d.Parts.Remove(root);
                d.Parts.Insert(0, root);
            }
        }

        /// <summary>Makes a safe file name from a vessel name.</summary>
        public static string FileNameFor(string vesselName)
        {
            var chars = (vesselName ?? "vessel").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : (c == ' ' ? '_' : '\0')).Where(c => c != '\0').ToArray();
            string name = new string(chars);
            if (name.Length == 0) name = "vessel";
            if (name.Length > 60) name = name[..60];
            return name + ".osp-vessel.json";
        }
    }
}
