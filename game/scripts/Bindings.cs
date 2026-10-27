using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace OSP.Game;

public sealed record BindingDef(string Id, string Label, Key Default, string Group);

/// <summary>Configurable flight key bindings (persisted in settings.json).</summary>
public sealed class Bindings
{
    public static readonly BindingDef[] Defs =
    {
        new("pitch_down", "Pitch down", Key.W, "Attitude"), new("pitch_up", "Pitch up", Key.S, "Attitude"),
        new("yaw_left", "Yaw left", Key.A, "Attitude"), new("yaw_right", "Yaw right", Key.D, "Attitude"),
        new("roll_left", "Roll left", Key.Q, "Attitude"), new("roll_right", "Roll right", Key.E, "Attitude"),
        new("throttle_up", "Throttle up", Key.Shift, "Engines"), new("throttle_down", "Throttle down", Key.Ctrl, "Engines"),
        new("throttle_max", "Full throttle", Key.Z, "Engines"), new("throttle_cut", "Cut throttle", Key.X, "Engines"),
        new("stage", "Activate next stage", Key.Space, "Engines"),
        new("sas_toggle", "Toggle SAS", Key.T, "Autopilot"), new("rcs_toggle", "Toggle RCS", Key.R, "Autopilot"),
        new("sas_stability", "SAS: stability assist", Key.Key1, "Autopilot"), new("sas_prograde", "SAS: prograde", Key.Key2, "Autopilot"),
        new("sas_retrograde", "SAS: retrograde", Key.Key3, "Autopilot"), new("sas_normal", "SAS: normal", Key.Key4, "Autopilot"),
        new("sas_antinormal", "SAS: anti-normal", Key.Key5, "Autopilot"), new("sas_radial_out", "SAS: radial out", Key.Key6, "Autopilot"),
        new("sas_radial_in", "SAS: radial in", Key.Key7, "Autopilot"), new("sas_maneuver", "SAS: maneuver node", Key.Key8, "Autopilot"),
        new("rcs_forward", "RCS translate forward (nose)", Key.H, "RCS"), new("rcs_back", "RCS translate back", Key.N, "RCS"),
        new("rcs_up", "RCS translate toward heading", Key.I, "RCS"), new("rcs_down", "RCS translate away from heading", Key.K, "RCS"),
        new("rcs_left", "RCS translate left", Key.J, "RCS"), new("rcs_right", "RCS translate right", Key.L, "RCS"),
        new("gear", "Landing gear", Key.G, "Systems"), new("solar", "Solar panels", Key.U, "Systems"), new("chutes", "Arm parachutes", Key.P, "Systems"),
        new("map", "Toggle map view", Key.M, "View"), new("camera_reset", "Reset camera", Key.V, "View"), new("camera_mode", "Camera: world-locked / vessel-locked", Key.C, "View"),
        new("hide_ui", "Hide / show interface", Key.F2, "View"), new("controls_help", "Controls reference", Key.F1, "View"),
        new("warp_up", "Time warp faster", Key.Period, "Time"), new("warp_down", "Time warp slower", Key.Comma, "Time"), new("warp_stop", "Stop time warp", Key.Slash, "Time"),
        new("next_craft", "Next vessel", Key.Bracketright, "Vessels"), new("prev_craft", "Previous vessel", Key.Bracketleft, "Vessels"),
        new("quicksave", "Quicksave", Key.F5, "Game"), new("quickload", "Quickload", Key.F9, "Game"), new("screenshot", "Screenshot", Key.F12, "Game"),
    };

    private readonly Dictionary<string, Key> _map = new();

    public Bindings(Dictionary<string, string>? saved = null)
    {
        foreach (var d in Defs) _map[d.Id] = d.Default;
        if (saved == null) return;
        foreach (var (id, name) in saved)
            if (_map.ContainsKey(id) && Enum.TryParse<Key>(name, out var k)) _map[id] = k;
    }

    public Key Get(string id) => _map[id];
    public void Set(string id, Key k) => _map[id] = k;
    public bool IsDown(string id) => Input.IsKeyPressed(_map[id]);
    public bool Matches(InputEventKey e, string id) => e.Pressed && !e.Echo && e.Keycode == _map[id];
    public void ResetAll() { foreach (var d in Defs) _map[d.Id] = d.Default; }
    public Dictionary<string, string> ToDictionary() => _map.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    public static string KeyName(Key k) => OS.GetKeycodeString(k);
}
