using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OSP.Core.Persistence;

namespace OSP.Core.Parts
{
    /// <summary>
    /// All known part definitions. Part files are JSON objects of the form { "parts": [ ... ] } (or a single part
    /// object). The catalogue is filled from strings so the Godot layer (res:// files) and the test suite
    /// (plain files) share the same parser.
    /// </summary>
    public sealed class PartCatalog
    {
        private readonly Dictionary<string, PartDefinition> _parts = new(StringComparer.Ordinal);
        private readonly List<string> _loadErrors = new();

        public IReadOnlyCollection<PartDefinition> All => _parts.Values;
        public IReadOnlyList<string> LoadErrors => _loadErrors;

        public PartDefinition Get(string id) => id != null && _parts.TryGetValue(id, out var p) ? p : null;
        public bool Contains(string id) => id != null && _parts.ContainsKey(id);

        public void Add(PartDefinition def)
        {
            if (_parts.ContainsKey(def.Id)) throw new FormatException($"Duplicate part id '{def.Id}'.");
            _parts[def.Id] = def;
        }

        /// <summary>
        /// Loads all parts in a JSON document. Invalid parts are skipped and reported in <see cref="LoadErrors"/>
        /// so one broken file cannot stop the game from starting. Returns the number of parts loaded.
        /// </summary>
        public int LoadJson(string json, string sourceName)
        {
            int count = 0;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                _loadErrors.Add($"{sourceName}: invalid JSON ({ex.Message})");
                return 0;
            }
            using (doc)
            {
                IEnumerable<JsonElement> elements;
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("parts", out JsonElement arr))
                    elements = arr.EnumerateArray();
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    elements = doc.RootElement.EnumerateArray();
                else
                    elements = new[] { doc.RootElement };

                foreach (JsonElement el in elements)
                {
                    try
                    {
                        PartDefinition def = ParsePart(el);
                        Add(def);
                        count++;
                    }
                    catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
                    {
                        string id = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out var idEl) ? idEl.GetString() : "?";
                        _loadErrors.Add($"{sourceName}: part '{id}' rejected: {ex.Message}");
                    }
                }
            }
            return count;
        }

        public static PartDefinition ParsePart(JsonElement el)
        {
            PartDefinition def = el.Deserialize<PartDefinition>(JsonUtil.Options)
                                 ?? throw new FormatException("Empty part definition.");
            if (el.TryGetProperty("modules", out JsonElement modules))
            {
                foreach (JsonElement m in modules.EnumerateArray())
                {
                    if (!m.TryGetProperty("type", out JsonElement typeEl))
                        throw new FormatException($"{def.Id}: module without 'type'.");
                    string type = typeEl.GetString();
                    if (!ModuleRegistry.Types.TryGetValue(type ?? "", out Type t))
                        throw new FormatException($"{def.Id}: unknown module type '{type}'.");
                    var module = (PartModuleDef)m.Deserialize(t, JsonUtil.Options);
                    def.Modules.Add(module);
                }
            }
            def.FinishLoading();
            return def;
        }

        public IEnumerable<PartCategory> Categories => All.Select(p => p.Category).Distinct().OrderBy(c => c);

        /// <summary>Case-insensitive search over name, id, description, manufacturer and category.</summary>
        public IEnumerable<PartDefinition> Search(string query, PartCategory? category = null, Func<PartDefinition, bool> available = null)
        {
            IEnumerable<PartDefinition> q = All;
            if (category.HasValue) q = q.Where(p => p.Category == category.Value);
            if (available != null) q = q.Where(available);
            if (!string.IsNullOrWhiteSpace(query))
            {
                string s = query.Trim();
                q = q.Where(p => Contains(p.Name, s) || Contains(p.Id, s) || Contains(p.Description, s)
                                 || Contains(p.Manufacturer, s) || Contains(p.Category.ToString(), s));
            }
            return q.OrderBy(p => p.Category).ThenBy(p => p.Cost).ThenBy(p => p.Name);
        }

        private static bool Contains(string text, string s) => text != null && text.Contains(s, StringComparison.OrdinalIgnoreCase);
    }
}
