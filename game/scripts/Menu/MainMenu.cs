using System;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Util;
using OSP.Game.Data;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Menu;

/// <summary>Start menu over a live rendering of the home planet.</summary>
public partial class MainMenu : Node3D
{
    private PlanetView _planet = null!;
    private SpaceEnvironment _env = null!;
    private Camera3D _cam = null!;
    private CanvasLayer _ui = null!;
    private double _t;
    private Label _status = null!;

    public override void _Ready()
    {
        GameData.Load();
        var gs = GameState.Instance;
        gs.ApplyGraphicsSettings();

        _env = new SpaceEnvironment(); AddChild(_env);
        _env.SetShadows(false, 50);
        _cam = new Camera3D { Current = true, Fov = 45, Near = 0.3f, Far = 4.0e6f }; AddChild(_cam);
        var sys = GameData.NewSystem();
        _planet = new PlanetView(sys.Home) { SplitFactor = 1.6, MaxLevelCap = 6 };
        _planet.SetAtmosphereQuality(Quality.AtmosphereSteps(gs.Settings.GraphicsQuality));
        AddChild(_planet);
        PlaceCamera(0);
        _planet.Prewarm(0, -_camPos, _camPos, _sun);

        BuildUi();
    }

    private Vec3d _camPos; private Vec3d _sun = new Vec3d(0.8, 0.25, 0.55).Normalized;

    private void PlaceCamera(double t)
    {
        double R = _planet.Body.Radius;
        double ang = t * 0.015;
        _camPos = new Vec3d(Math.Cos(ang) * 2.7 * R, 0.55 * R, Math.Sin(ang) * 2.7 * R);
        var f = (-_camPos).Normalized;
        _cam.Position = Vector3.Zero;
        _cam.LookAt(new Vector3((float)f.X, (float)f.Y, (float)f.Z) + new Vector3(0.0f, 0, 0), Vector3.Up);
        // shift the planet to the right of the screen so the menu panel has room
        _cam.RotateObjectLocal(Vector3.Up, Mathf.DegToRad(11));
        _planet.CameraForwardWorld = f;
        double dens = 0;
        _env.Update(_sun, 1.0, dens);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        PlaceCamera(_t);
        _planet.UpdateView(_t * 400, -_camPos, _camPos, _sun);
    }

    // ------------------------------------------------------------------------------------------- UI
    private void BuildUi()
    {
        var gs = GameState.Instance;
        _ui = new CanvasLayer(); AddChild(_ui);
        var root = new Control { Theme = Ui.Theme }; root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(root);

        var col = Ui.VBox(10);
        col.CustomMinimumSize = new Vector2(360, 0);
        col.AddChild(Ui.Label("OSP", 64, Colors.White));
        col.AddChild(Ui.Label("OPEN SPACE PROGRAM", 22, Ui.Accent));
        col.AddChild(Ui.Label("An original 3D spaceflight sandbox", 14, Ui.Dim));
        col.AddChild(Ui.Spacer(14));
        col.AddChild(Ui.Button("Vehicle Assembly", () => gs.GoTo("Editor"), "Build a rocket from 3D parts"));
        var launch = Ui.Button("Launch Current Vehicle", () => LaunchCurrent(), "Fly the vehicle currently in the assembly building");
        launch.Disabled = gs.CurrentDesign.IsEmpty;
        col.AddChild(launch);
        col.AddChild(Ui.Button("Example Vehicles", ShowExamples, "Start from a ready-made rocket"));
        col.AddChild(Ui.Button("Load Saved Flight", ShowSaves));
        col.AddChild(Ui.Spacer(8));
        col.AddChild(Ui.Button("Settings", ShowSettings));
        col.AddChild(Ui.Button("Controls", () => Modal.Show(_ui, "Controls reference", SettingsPanels.BuildControlsReference(), new Vector2(820, 0))));
        col.AddChild(Ui.Button("Feature status", ShowFeatures));
        col.AddChild(Ui.Button("Quit", () => GetTree().Quit()));

        var margin = new MarginContainer(); margin.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        margin.AddThemeConstantOverride("margin_left", 48); margin.AddThemeConstantOverride("margin_top", 60);
        margin.AddChild(col);
        root.AddChild(margin);

        _status = Ui.Label("", 13, Ui.Dim);
        var foot = new MarginContainer(); foot.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        foot.AddThemeConstantOverride("margin_left", 48); foot.AddThemeConstantOverride("margin_bottom", 18);
        var f = Ui.VBox(2);
        f.AddChild(Ui.Label($"Build: Godot {Engine.GetVersionInfo()["string"]} · Vesper system · {GameData.Catalog.All.Count} parts · {GameData.System.Bodies.Count} bodies", 12, Ui.Dim));
        f.AddChild(Ui.Label($"Saves & designs: {OS.GetUserDataDir()}", 12, Ui.Dim));
        f.AddChild(_status);
        foot.AddChild(f);
        root.AddChild(foot);
    }

    private void LaunchCurrent()
    {
        var gs = GameState.Instance;
        gs.PendingLaunch = gs.CurrentDesign.Clone();
        gs.GoTo("Flight");
    }

    private void ShowExamples()
    {
        var gs = GameState.Instance;
        var v = Ui.VBox(8);
        v.AddChild(Ui.Label("Ready-made vehicles. Opening one loads it into the Vehicle Assembly Building.", 14, Ui.Dim, true));
        var notes = new System.Collections.Generic.Dictionary<string, string>
        {
            ["Pathfinder-1"] = "Two-stage orbiter. Reaches a 75×140 km orbit with the autopilot-free gravity turn described in the README.",
            ["Boosted_Swift"] = "Strap-on SRB sounding rocket: great for learning staging and the first suborbital hop.",
            ["Hopper_Lander"] = "Capsule + vacuum engine + legs + chute + solar: a lander that needs an orbital stage to carry it.",
            ["Heavy_Lifter"] = "2.5 m two-stage heavy lifter with ~5.6 km/s of vacuum delta-v, enough to reach Lumen with a good ascent.",
        };
        var modal = (Modal?)null;
        foreach (var name in gs.ListExampleDesigns())
        {
            var n = name;
            var row = Ui.VBox(1);
            row.AddChild(Ui.Button(n.Replace('_', ' '), () => { try { gs.CurrentDesign = gs.LoadDesign(n, true); gs.GoTo("Editor"); } catch (DataException ex) { _status.Text = ex.Message; } }, null, 300));
            row.AddChild(Ui.Label(notes.TryGetValue(n, out var d) ? d : "", 12, Ui.Dim, true));
            v.AddChild(row);
        }
        modal = Modal.Show(_ui, "Example vehicles", v, new Vector2(600, 0));
    }

    private void ShowSaves()
    {
        var gs = GameState.Instance;
        var v = Ui.VBox(8);
        var list = new ItemList { CustomMinimumSize = new Vector2(540, 260) };
        var saves = gs.ListSaves();
        foreach (var s in saves) list.AddItem(s.label);
        var examples = gs.ListExampleSaves();
        foreach (var ex in examples) list.AddItem($"{ex.Replace('_', ' ')}   (example flight)");
        v.AddChild(list);
        if (saves.Count == 0 && examples.Count == 0) v.AddChild(Ui.Label("No saved flights yet. Press F5 during a flight to quicksave.", 14, Ui.Dim, true));
        var row = Ui.HBox();
        Modal? modal = null;
        row.AddChild(Ui.Button("Load", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0) return;
            try { gs.PendingFlightJson = sel[0] < saves.Count ? gs.ReadSave(saves[sel[0]].file) : gs.ReadExampleSave(examples[sel[0] - saves.Count]); gs.GoTo("Flight"); }
            catch (Exception ex) { _status.Text = "Load failed: " + ex.Message; modal?.Close(); }
        }));
        row.AddChild(Ui.Button("Delete", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0 || sel[0] >= saves.Count) return;
            gs.DeleteSave(saves[sel[0]].file); modal?.Close(); ShowSaves();
        }));
        v.AddChild(row);
        modal = Modal.Show(_ui, "Saved flights", v);
    }

    private void ShowSettings()
    {
        var tabs = new TabContainer { CustomMinimumSize = new Vector2(660, 460) };
        var settings = SettingsPanels.BuildSettings(() => _planet.SetAtmosphereQuality(Quality.AtmosphereSteps(GameState.Instance.Settings.GraphicsQuality)));
        settings.Name = "Graphics & input";
        var keys = SettingsPanels.BuildKeyBindings(); keys.Name = "Key bindings";
        tabs.AddChild(settings); tabs.AddChild(keys);
        Modal.Show(_ui, "Settings", tabs, new Vector2(700, 0));
    }

    private void ShowFeatures()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(840, 440) };
        var v = Ui.VBox(4); scroll.AddChild(v);
        string area = "";
        foreach (var it in FeatureStatus.Items)
        {
            if (it.Area != area) { v.AddChild(Ui.Spacer(4)); v.AddChild(Ui.Label(it.Area, 17, Ui.Accent)); area = it.Area; }
            var row = Ui.HBox(10);
            var color = it.State == "Working" ? Ui.Good : it.State == "Partial" ? Ui.Warn : Ui.Dim;
            var st = Ui.Label(it.State, 14, color); st.CustomMinimumSize = new Vector2(80, 0);
            row.AddChild(st);
            row.AddChild(Ui.Label(it.Feature + (it.Note != "" ? $"  ({it.Note})" : ""), 14));
            v.AddChild(row);
        }
        Modal.Show(_ui, "Feature status (nothing here is faked)", scroll, new Vector2(900, 0));
    }
}
