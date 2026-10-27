using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Save;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using OSP.Game.Data;
using FileAccess = Godot.FileAccess;
using Json = OSP.Core.Util.Json;

namespace OSP.Game;

/// <summary>
/// Autoload singleton: scene transitions, player settings, key bindings and persistent storage
/// (vehicle designs and saved flights live under user:// so the game never needs write access elsewhere).
/// </summary>
public partial class GameState : Node
{
    public static GameState Instance { get; private set; } = null!;
    public const string DesignDir = "user://designs";
    public const string SaveDir = "user://saves";
    public const string SettingsPath = "user://settings.json";

    public PlayerSettings Settings { get; private set; } = new();
    public Bindings Keys { get; private set; } = new();
    public VehicleDesign CurrentDesign { get; set; } = new();
    /// <summary>Flight to restore when entering the Flight scene (set by "load flight").</summary>
    public string? PendingFlightJson { get; set; }
    /// <summary>Design to launch when entering the Flight scene.</summary>
    public VehicleDesign? PendingLaunch { get; set; }
    /// <summary>Snapshot of the design at launch time so "Revert to VAB" restores it.</summary>
    public string? LastLaunchedDesignJson { get; set; }
    public string? LastMessage { get; set; }
    /// <summary>Developer flag (--osp-autopilot): fly a scripted ascent for headless verification.</summary>
    public bool DevAutopilot { get; set; }
    public int DevWarp { get; set; }
    public bool DevMap { get; set; }

    // ---- developer screenshot hook (--osp-shot=path@frames) ----
    private string? _shotPath; private int _shotFrames = -1, _frame;
    public void ScheduleShot(string spec)
    {
        var parts = spec.Split('@');
        _shotPath = parts[0]; _shotFrames = parts.Length > 1 ? int.Parse(parts[1]) : 60;
    }

    public override void _Process(double delta)
    {
        if (_shotPath == null) return;
        if (++_frame == _shotFrames)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shotPath);
            GD.Print($"[OSP] screenshot saved: {_shotPath}");
            GetTree().Quit();
        }
    }

    public override void _Ready()
    {
        Instance = this;
        DirAccess.MakeDirRecursiveAbsolute(DesignDir);
        DirAccess.MakeDirRecursiveAbsolute(SaveDir);
        LoadSettings();
        GD.Print($"[OSP] user data directory: {OS.GetUserDataDir()}");
    }

    // ----------------------------------------------------------------------------- scenes
    public void GoTo(string scene) => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, $"res://scenes/{scene}.tscn");

    // ----------------------------------------------------------------------------- settings
    public void LoadSettings()
    {
        try
        {
            if (FileAccess.FileExists(SettingsPath))
                Settings = Json.Deserialize<PlayerSettings>(GameData.ReadText(SettingsPath), "settings");
        }
        catch (DataException ex)
        {
            GD.PushWarning($"[OSP] settings.json unreadable ({ex.Message}); using defaults");
            Settings = new PlayerSettings();
        }
        Keys = new Bindings(Settings.KeyBindings);
    }

    public void SaveSettings()
    {
        Settings.KeyBindings = Keys.ToDictionary();
        WriteText(SettingsPath, Json.Serialize(Settings));
    }

    /// <summary>Apply window-level graphics settings (vsync, fullscreen, 3D render scale, MSAA).</summary>
    public void ApplyGraphicsSettings(Viewport? vp = null)
    {
        DisplayServer.WindowSetVsyncMode(Settings.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        DisplayServer.WindowSetMode(Settings.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        vp ??= GetViewport();
        vp.Scaling3DScale = (float)Math.Clamp(Settings.RenderScale, 0.5, 1.0);
        vp.Msaa3D = Settings.GraphicsQuality switch { 0 => Viewport.Msaa.Disabled, 1 => Viewport.Msaa.Msaa2X, _ => Viewport.Msaa.Msaa4X };
    }

    // ----------------------------------------------------------------------------- files
    public static void WriteText(string path, string text)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) throw new DataException($"Cannot write {path}: {FileAccess.GetOpenError()}");
        f.StoreString(text);
    }

    public static string SafeName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' ? c : '_').ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "Untitled" : s;
    }

    // ----------------------------------------------------------------------------- designs
    public List<string> ListDesigns()
    {
        var names = new List<string>();
        foreach (var f in DirAccess.GetFilesAt(DesignDir))
            if (f.EndsWith(".osp-vehicle.json")) names.Add(f.Substring(0, f.Length - ".osp-vehicle.json".Length));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public List<string> ListExampleDesigns()
    {
        var names = new List<string>();
        foreach (var f in DirAccess.GetFilesAt("res://data/vehicles"))
            if (f.EndsWith(".osp-vehicle.json")) names.Add(f.Substring(0, f.Length - ".osp-vehicle.json".Length));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public string SaveDesign(VehicleDesign d)
    {
        var file = SafeName(d.Name);
        WriteText($"{DesignDir}/{file}.osp-vehicle.json", SaveSerializer.SerializeDesign(d));
        return file;
    }

    public VehicleDesign LoadDesign(string fileName, bool example = false)
    {
        var path = example ? $"res://data/vehicles/{fileName}.osp-vehicle.json" : $"{DesignDir}/{fileName}.osp-vehicle.json";
        return SaveSerializer.DeserializeDesign(GameData.ReadText(path), GameData.Catalog);
    }

    public void DeleteDesign(string fileName) => DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath($"{DesignDir}/{fileName}.osp-vehicle.json"));

    // ----------------------------------------------------------------------------- saved flights
    public List<(string file, string label)> ListSaves()
    {
        var list = new List<(string, string)>();
        foreach (var f in DirAccess.GetFilesAt(SaveDir))
        {
            if (!f.EndsWith(".osp-save.json")) continue;
            var name = f.Substring(0, f.Length - ".osp-save.json".Length);
            var mod = FileAccess.GetModifiedTime($"{SaveDir}/{f}");
            list.Add((name, $"{name}   ({Time.GetDatetimeStringFromUnixTime((long)mod)})"));
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Item1, a.Item1));
        return list;
    }

    public List<string> ListExampleSaves()
    {
        var names = new List<string>();
        foreach (var f in DirAccess.GetFilesAt("res://data/saves"))
            if (f.EndsWith(".osp-save.json")) names.Add(f.Substring(0, f.Length - ".osp-save.json".Length));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public string ReadExampleSave(string name) => GameData.ReadText($"res://data/saves/{name}.osp-save.json");

    public void WriteSave(string name, SaveGame save) => WriteText($"{SaveDir}/{SafeName(name)}.osp-save.json", SaveSerializer.Serialize(save));
    public string ReadSave(string name) => GameData.ReadText($"{SaveDir}/{SafeName(name)}.osp-save.json");
    public void DeleteSave(string name) => DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath($"{SaveDir}/{SafeName(name)}.osp-save.json"));
}
