using System;
using System.Collections.Generic;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Parts;
using OSP.Core.Util;

namespace OSP.Game.Data;

/// <summary>Loads the data-driven game content (planetary system, part catalogue) from res://data.</summary>
public static class GameData
{
    public static StarSystem System { get; private set; } = null!;
    public static PartCatalog Catalog { get; private set; } = null!;
    public static bool Loaded => System != null && Catalog != null;

    public static void Load()
    {
        if (Loaded) return;
        System = StarSystem.FromJson(ReadText("res://data/system.json"));
        var files = new List<(string, string)>();
        foreach (var f in DirAccess.GetFilesAt("res://data/parts"))
        {
            if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add((f, ReadText("res://data/parts/" + f)));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        if (files.Count == 0) throw new DataException("No part files found in res://data/parts");
        Catalog = PartCatalog.FromJson(files);
        GD.Print($"[OSP] Loaded {System.Bodies.Count} bodies and {Catalog.All.Count} parts.");
    }

    /// <summary>Returns a fresh StarSystem (e.g. one per flight, so body state is never shared between games).</summary>
    public static StarSystem NewSystem() => StarSystem.FromJson(ReadText("res://data/system.json"));

    public static string ReadText(string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) throw new DataException($"Cannot open {path}: {FileAccess.GetOpenError()}");
        return f.GetAsText();
    }
}
