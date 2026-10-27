using System;
using System.IO;
using OSP.Core.Celestial;
using OSP.Core.Parts;

namespace OSP.Tests
{
    /// <summary>Loads the real game data files (data/*.json) shipped with the project.</summary>
    public static class TestData
    {
        private static readonly Lazy<string> Root = new(() =>
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "project.godot"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the project root (project.godot).");
        });

        public static string ProjectRoot => Root.Value;

        public static PartCatalog LoadCatalog()
        {
            var catalog = new PartCatalog();
            foreach (string file in Directory.GetFiles(Path.Combine(ProjectRoot, "data", "parts"), "*.json"))
                catalog.LoadJson(File.ReadAllText(file), Path.GetFileName(file));
            return catalog;
        }

        public static CelestialSystem LoadSystem() =>
            CelestialSystem.LoadFromJson(File.ReadAllText(Path.Combine(ProjectRoot, "data", "system.json")));
    }
}
