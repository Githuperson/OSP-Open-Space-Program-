using System;
using System.Collections.Generic;
using Godot;
using OSP.Game.Data;

namespace OSP.Game;

/// <summary>
/// Entry scene: loads data, then routes to the main menu. Developer/CI options (after "--"):
///   --selftest=planet            render planet test shots
///   --osp-scene=Editor|Flight    jump straight into a scene
///   --osp-example=Pathfinder-1   load an example vehicle first
///   --osp-shot=/path.png@120     save a screenshot after 120 frames, then quit
/// </summary>
public partial class Boot : Node
{
    public override void _Ready()
    {
        var opts = new Dictionary<string, string>();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (!a.StartsWith("--")) continue;
            var kv = a.Substring(2).Split('=', 2);
            opts[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        if (opts.TryGetValue("selftest", out var name))
        {
            GD.Print("[OSP] running self test: " + name);
            AddChild(new SelfTest { Name_ = name });
            return;
        }
        GameData.Load();
        var gs = GameState.Instance;
        if (opts.TryGetValue("osp-example", out var ex)) gs.CurrentDesign = gs.LoadDesign(ex, true);
        if (opts.TryGetValue("osp-shot", out var shot)) gs.ScheduleShot(shot);
        gs.DevAutopilot = opts.ContainsKey("osp-autopilot");
        if (opts.TryGetValue("osp-save", out var sv)) gs.PendingFlightJson = gs.ReadExampleSave(sv);
        gs.DevMap = opts.ContainsKey("osp-map");
        if (opts.TryGetValue("osp-warp", out var wp)) gs.DevWarp = int.Parse(wp);
        string scene = opts.TryGetValue("osp-scene", out var sc) ? sc : "MainMenu";
        if (scene == "Flight" && !gs.CurrentDesign.IsEmpty && gs.PendingFlightJson == null) gs.PendingLaunch = gs.CurrentDesign.Clone();
        gs.GoTo(scene);
    }
}
