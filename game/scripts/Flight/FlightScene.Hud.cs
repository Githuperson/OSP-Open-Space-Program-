using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Flight;

public sealed partial class FlightScene
{
    private CanvasLayer _uiLayer = null!;
    private Control _hudRoot = null!;
    private Label _telemetry = null!, _orbitInfo = null!, _topInfo = null!, _logLabel = null!, _stageList = null!, _attitude = null!, _targetInfo = null!;
    private ProgressBar _throttleBar = null!;
    private readonly Dictionary<string, ProgressBar> _resBars = new();
    private readonly Dictionary<string, Label> _resLabels = new();
    private NavBall _navball = null!;
    private Button _sasBtn = null!, _rcsBtn = null!, _warpLabelBtn = null!;
    private readonly Dictionary<SasMode, Button> _sasModeBtns = new();
    private Modal? _pauseModal;
    private readonly List<(string text, double until)> _log = new();
    private double _hudAccum;
    private List<PredictedPatch> _patches = new();
    private double _patchAge = 99;
    private CelestialBody? _targetBody;
    private Craft? _targetCraft;

    private void BuildHud()
    {
        _uiLayer = new CanvasLayer { Layer = 5 }; AddChild(_uiLayer);
        _hudRoot = new Control { Theme = Ui.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _uiLayer.AddChild(_hudRoot);

        // ----- top left: telemetry -----
        var tel = Ui.VBox(4);
        _telemetry = Ui.Label("", 14); tel.AddChild(_telemetry);
        foreach (var (res, label) in new[] { ("Propellant", "Propellant"), ("SolidFuel", "Solid fuel"), ("Monopropellant", "Monoprop"), ("ElectricCharge", "Electric") })
        {
            var row = Ui.HBox(6);
            var l = Ui.Label(label, 12, Ui.Dim); l.CustomMinimumSize = new Vector2(72, 0);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(120, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            var val = Ui.Label("", 12, Ui.Text);
            row.AddChild(l); row.AddChild(bar); row.AddChild(val);
            tel.AddChild(row);
            _resBars[res] = bar; _resLabels[res] = val;
            row.Visible = false;
            row.Name = "res_" + res;
        }
        _attitude = Ui.Label("", 13, Ui.Dim); tel.AddChild(_attitude);
        var telPanel = Ui.Panel(tel, 6);
        telPanel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        telPanel.OffsetLeft = 10; telPanel.OffsetTop = 10;
        _hudRoot.AddChild(telPanel);

        // ----- top right: orbit info + staging -----
        var right = Ui.VBox(6);
        _orbitInfo = Ui.Label("", 14); right.AddChild(_orbitInfo);
        _targetInfo = Ui.Label("", 13, Ui.Accent); right.AddChild(_targetInfo);
        right.AddChild(Ui.Sep());
        right.AddChild(Ui.Label("STAGING", 13, Ui.Accent));
        _stageList = Ui.Label("", 13); right.AddChild(_stageList);
        var rightPanel = Ui.Panel(right, 6);
        rightPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        rightPanel.GrowHorizontal = Control.GrowDirection.Begin;
        rightPanel.OffsetRight = -10; rightPanel.OffsetTop = 10;
        _hudRoot.AddChild(rightPanel);

        // ----- top centre: time -----
        _topInfo = Ui.Label("", 15, Ui.Text);
        _topInfo.HorizontalAlignment = HorizontalAlignment.Center;
        var topPanel = Ui.Panel(_topInfo, 4);
        topPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        topPanel.GrowHorizontal = Control.GrowDirection.Both;
        topPanel.OffsetTop = 10;
        _hudRoot.AddChild(topPanel);

        // ----- bottom centre: throttle, navball, buttons -----
        var bottom = Ui.HBox(10);
        // throttle bar
        var thr = Ui.VBox(2);
        _throttleBar = new ProgressBar { MinValue = 0, MaxValue = 1, FillMode = (int)ProgressBar.FillModeEnum.BottomToTop, ShowPercentage = false, CustomMinimumSize = new Vector2(22, 160), TooltipText = "Throttle" };
        thr.AddChild(Ui.Label("THR", 11, Ui.Dim)); thr.AddChild(_throttleBar);
        bottom.AddChild(thr);

        var ballCol = Ui.VBox(2);
        _navball = new NavBall();
        ballCol.AddChild(_navball);
        bottom.AddChild(ballCol);

        var btns = Ui.VBox(4);
        var sasRow1 = Ui.HBox(3); var sasRow2 = Ui.HBox(3);
        int i = 0;
        foreach (var (mode, label, tip) in new[] {
            (SasMode.StabilityAssist, "STB", "Hold attitude"), (SasMode.Prograde, "PRO", "Point along velocity"), (SasMode.Retrograde, "RET", "Point against velocity"), (SasMode.Normal, "NRM", "Orbit normal"),
            (SasMode.Antinormal, "ANT", "Anti-normal"), (SasMode.RadialOut, "R+", "Radial out"), (SasMode.RadialIn, "R−", "Radial in"), (SasMode.Maneuver, "MAN", "Maneuver node burn direction") })
        {
            var m = mode;
            var b = Ui.Button(label, () => SetSasMode(m), tip, 44);
            _sasModeBtns[mode] = b;
            (i++ < 4 ? sasRow1 : sasRow2).AddChild(b);
        }
        btns.AddChild(sasRow1); btns.AddChild(sasRow2);
        var tog = Ui.HBox(3);
        _sasBtn = Ui.Button("SAS", () => { var a = Sim.Active; if (a != null) { a.Control.SasEnabled = !a.Control.SasEnabled; a.Control.HoldOrientation = a.Orientation; a.Control.HoldValid = true; } }, "Toggle stability assist (T)", 58);
        _rcsBtn = Ui.Button("RCS", () => { var a = Sim.Active; if (a != null) a.Control.RcsEnabled = !a.Control.RcsEnabled; }, "Toggle RCS (R)", 58);
        tog.AddChild(_sasBtn); tog.AddChild(_rcsBtn);
        tog.AddChild(Ui.Button("GEAR", () => { bool d = !(Sim.Active?.Parts.Any(p => p.LegsDeployedTarget) ?? false); Sim.SetGear(d); }, "Landing gear (G)", 58));
        btns.AddChild(tog);
        var tog2 = Ui.HBox(3);
        tog2.AddChild(Ui.Button("SOLAR", () => { bool o = !(Sim.Active?.Parts.Any(p => p.SolarDeployedTarget) ?? false); Sim.SetPanels(o); }, "Solar panels (U)", 58));
        tog2.AddChild(Ui.Button("CHUTE", () => Sim.ArmParachutes(), "Arm parachutes (P)", 58));
        tog2.AddChild(Ui.Button("MAP", ToggleMap, "Map view (M)", 58));
        btns.AddChild(tog2);
        var stageBtn = Ui.Button("STAGE  ▶", () => { Sim.ActivateStage(); }, "Activate the next stage (Space)", 0);
        stageBtn.CustomMinimumSize = new Vector2(190, 38);
        btns.AddChild(stageBtn);
        var warpRow = Ui.HBox(3);
        warpRow.AddChild(Ui.Button("◀", () => Sim.SetWarp(Sim.WarpIndex - 1), "Slower (,)", 40));
        _warpLabelBtn = Ui.Button("Warp 1x", () => Sim.StopWarp(), "Stop warp (/)", 100);
        warpRow.AddChild(_warpLabelBtn);
        warpRow.AddChild(Ui.Button("▶", () => Sim.SetWarp(Sim.WarpIndex + 1), "Faster (.)", 40));
        btns.AddChild(warpRow);
        bottom.AddChild(btns);
        var botPanel = Ui.Panel(bottom, 6);
        botPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        botPanel.GrowHorizontal = Control.GrowDirection.Both; botPanel.GrowVertical = Control.GrowDirection.Begin;
        botPanel.OffsetBottom = -8;
        _hudRoot.AddChild(botPanel);

        // ----- bottom left: message log -----
        _logLabel = Ui.Label("", 14, Ui.Text);
        _logLabel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _logLabel.GrowVertical = Control.GrowDirection.Begin;
        _logLabel.OffsetLeft = 14; _logLabel.OffsetBottom = -14;
        _logLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        _logLabel.AddThemeConstantOverride("shadow_offset_x", 1); _logLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        _hudRoot.AddChild(_logLabel);

        BuildMapUi();
    }

    public void Log(string text)
    {
        _log.Add((text, _debugTime + 9));
        if (_log.Count > 12) _log.RemoveAt(0);
        GD.Print("[flight] " + text);
    }

    private void ShowFatal(string msg)
    {
        var v = Ui.VBox(10);
        v.AddChild(Ui.Label("The flight could not be started:", 15, Ui.Text, true));
        v.AddChild(Ui.Label(msg, 14, Ui.Bad, true));
        v.AddChild(Ui.Button("Back to menu", () => GameState.Instance.GoTo("MainMenu")));
        Modal.Show(_uiLayer, "Flight error", v, new Vector2(560, 0), false);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  HUD update
    // ------------------------------------------------------------------------------------------------------------
    private void UpdateHud(double dt)
    {
        _hudAccum += dt;
        // message log (cheap)
        _log.RemoveAll(m => m.until < _debugTime);
        _logLabel.Text = string.Join("\n", _log.TakeLast(8).Select(m => m.text));

        var a = Sim.Active;
        if (a == null) { _telemetry.Text = "NO ACTIVE VESSEL"; return; }

        // navball is cheap enough to update every frame; text at 10 Hz
        var envN = FlightEnvironment.Compute(System, a.Body, Sim.Time, a.Position, a.Velocity, false);
        _navball.SetAttitude(a.Orientation, a.Position, NavMarkersFor(a, envN));
        _throttleBar.Value = a.Control.Throttle;
        _sasBtn.Modulate = a.Control.SasEnabled ? new Color(0.5f, 1f, 0.6f) : new Color(1, 1, 1, 0.8f);
        _rcsBtn.Modulate = a.Control.RcsEnabled ? new Color(0.5f, 1f, 0.6f) : new Color(1, 1, 1, 0.8f);
        foreach (var (m, b) in _sasModeBtns) b.Modulate = a.Control.SasEnabled && a.Control.Sas == m ? new Color(0.5f, 1f, 0.6f) : new Color(1, 1, 1, 0.8f);
        _warpLabelBtn.Text = "Warp " + Sim.Warp.Label;

        if (_hudAccum < 0.1) return;
        _hudAccum = 0;

        var env = FlightEnvironment.Compute(System, a.Body, Sim.Time, a.Position, a.Velocity, true);
        var up = env.Up;
        double vVert = Vec3d.Dot(env.SurfaceVelocity, up);
        double surfSpeed = env.SurfaceVelocity.Length;
        double vHoriz = Math.Sqrt(Math.Max(0, surfSpeed * surfSpeed - vVert * vVert));
        double thrust = a.LastTotalThrust;
        double mass = a.TotalMass;
        double gLocal = a.Body.Mu / Math.Max(1, a.Position.LengthSquared);
        var sb = new StringBuilder();
        sb.AppendLine($"{a.Name}  ·  {a.Body.Name}");
        sb.AppendLine($"Situation: {Situation(a, env)}");
        sb.AppendLine($"Altitude (sea): {Ui.FormatDistance(env.AltitudeSeaLevel)}");
        sb.AppendLine($"Altitude (ground): {Ui.FormatDistance(env.AltitudeTerrain)}");
        sb.AppendLine($"Vertical speed: {vVert:F1} m/s");
        sb.AppendLine($"Horizontal speed: {vHoriz:F1} m/s");
        sb.AppendLine($"Surface speed: {Ui.FormatSpeed(surfSpeed)}");
        sb.AppendLine($"Orbital speed: {Ui.FormatSpeed(a.Velocity.Length)}");
        if (a.Body.Atmosphere != null && env.PressurePa > 0.5)
            sb.AppendLine($"Pressure: {env.PressurePa / 1000:F2} kPa · q {a.LastDynamicPressure / 1000:F2} kPa · Mach {surfSpeed / Math.Max(1, env.SpeedOfSound):F2}");
        sb.AppendLine($"Mass: {Ui.FormatMass(mass)}   G: {a.LastGForce:F2}");
        sb.AppendLine($"Thrust: {thrust / 1000:F0} kN   TWR: {(mass > 0 ? thrust / (mass * gLocal) : 0):F2}");
        sb.AppendLine($"Throttle: {a.Control.Throttle * 100:F0}%   Stage: {Math.Max(0, a.CurrentStage + 1)}");
        int active = a.Parts.Count(p => p.Ignited && p.Def.Engine != null), dead = a.Parts.Count(p => p.FlamedOut);
        int engines = a.Parts.Count(p => p.Def.Engine != null);
        if (engines > 0) sb.AppendLine($"Engines: {active} burning, {engines - active - dead} ready, {dead} spent");
        if (!a.HasCommand) sb.AppendLine("⚠ No command module: uncontrolled");
        sb.Append($"SAS: {(a.Control.SasEnabled ? FlightScene.SasName(a.Control.Sas) : "off")} · RCS: {(a.Control.RcsEnabled ? "on" : "off")}");
        _telemetry.Text = sb.ToString();
        _attitude.Text = $"Pitch {_navball.Pitch:F0}°  Hdg {_navball.Heading:F0}°  Roll {_navball.Roll:F0}°";

        foreach (var (res, bar) in _resBars)
        {
            double cap = a.ResourceCapacityTotal(res), amt = a.ResourceTotal(res);
            var row = _telemetry.GetParent().GetNodeOrNull<HBoxContainer>("res_" + res);
            if (row != null) row.Visible = cap > 0;
            if (cap > 0) { bar.Value = amt / cap; _resLabels[res].Text = res == "ElectricCharge" ? $"{amt:F0}/{cap:F0}" : $"{amt:F0} kg"; }
        }

        UpdateOrbitInfo(a, env);
        UpdateStageList(a);
        _topInfo.Text = $"UT {Ui.FormatTime(Sim.Time)}   ·   MET {Ui.FormatTime(Sim.Time - a.MissionStartTime)}   ·   Warp {Sim.Warp.Label}" + (Sim.Paused || _paused ? "   ·   PAUSED" : "") + (Sim.WarpLimitReason != null && Sim.WarpIndex <= 3 && Sim.Warp.Factor > 1 ? "" : "");
    }

    private static string Situation(Craft a, FlightEnvironment env)
    {
        if (a.Mode == CraftMode.Landed) return "Landed";
        if (a.InContact) return "Touching down";
        var o = Orbit.FromState(a.Position, a.Velocity, a.Body.Mu, env.Time);
        double atm = a.Body.Atmosphere?.Height ?? 0;
        if (env.AltitudeSeaLevel < atm) return "Flying";
        if (o.Eccentricity >= 1) return "Escaping";
        if (o.PeriapsisRadius < a.Body.Radius + atm * 0.99) return "Sub-orbital";
        return "Orbiting";
    }

    private void UpdateOrbitInfo(Craft a, FlightEnvironment env)
    {
        var o = Orbit.FromState(a.Position, a.Velocity, a.Body.Mu, Sim.Time);
        var sb = new StringBuilder();
        double R = a.Body.Radius;
        sb.AppendLine("ORBIT  (" + a.Body.Name + ")");
        if (o.IsElliptic)
        {
            sb.AppendLine($"Apoapsis: {Ui.FormatDistance(o.ApoapsisRadius - R)}   in {Ui.FormatTime(o.TimeToApoapsis(Sim.Time))}");
            sb.AppendLine($"Periapsis: {Ui.FormatDistance(o.PeriapsisRadius - R)}   in {Ui.FormatTime(o.TimeToPeriapsis(Sim.Time))}");
            sb.AppendLine($"Period: {Ui.FormatTime(o.Period)}");
        }
        else
        {
            sb.AppendLine("Apoapsis: —   (escape trajectory)");
            sb.AppendLine($"Periapsis: {Ui.FormatDistance(o.PeriapsisRadius - R)}   in {Ui.FormatTime(o.TimeToPeriapsis(Sim.Time))}");
            sb.AppendLine($"Excess speed: {Math.Sqrt(Math.Max(0, -a.Body.Mu / o.SemiMajorAxis)):F0} m/s");
        }
        sb.AppendLine($"Eccentricity: {o.Eccentricity:F4}   Inclination: {o.Inclination * MathUtil.Rad2Deg:F2}°");
        sb.Append($"Escape speed here: {Ui.FormatSpeed(OrbitMath.EscapeSpeed(a.Body.Mu, a.Position.Length))}");

        _patchAge += 0.1;
        if (_patchAge > 0.4 && a.Mode != CraftMode.Landed) { _patches = Sim.PredictFor(a, 5, 2.0e7); _patchAge = 0; }
        if (a.Mode == CraftMode.Landed) _patches = new();
        var change = _patches.FirstOrDefault(p => p.End == PatchEnd.SoiEntry || p.End == PatchEnd.SoiExit);
        if (change != null)
            sb.Append($"\nNext SoI change: {(change.End == PatchEnd.SoiEntry ? "→ " + change.NextBody!.Name : "→ " + change.Body.Parent!.Name)} in {Ui.FormatTime(change.EndTime - Sim.Time)}");
        var impact = _patches.FirstOrDefault(p => p.End == PatchEnd.Impact);
        if (impact != null && a.Mode != CraftMode.Landed && a.LastTotalThrust <= 0) sb.Append($"\n⚠ Predicted impact in {Ui.FormatTime(impact.EndTime - Sim.Time)}");
        var nodes = Sim.NodesFor(a).Where(n => n.Time >= Sim.Time - 1).OrderBy(n => n.Time).FirstOrDefault();
        if (nodes != null)
        {
            var (bt, ok) = ManeuverPlanner.EstimateBurnTime(a, nodes.DeltaVMagnitude);
            sb.Append($"\nManeuver in {Ui.FormatTime(nodes.Time - Sim.Time)}: Δv {nodes.DeltaVMagnitude:F1} m/s" + (ok ? $", burn ≈ {bt:F0} s" : ""));
        }
        _orbitInfo.Text = sb.ToString();

        if (_targetBody != null || _targetCraft != null)
        {
            Vec3d tp, tv; string name;
            if (_targetCraft != null && !_targetCraft.IsDestroyed) { (tp, tv) = System.ConvertFrame(_targetCraft.Position, _targetCraft.Velocity, _targetCraft.Body, a.Body, Sim.Time); name = _targetCraft.Name; }
            else if (_targetBody != null && _targetBody != a.Body)
            {
                var (r, v) = System.StateInRoot(_targetBody, Sim.Time); var (br, bv) = System.StateInRoot(a.Body, Sim.Time);
                tp = r - br; tv = v - bv; name = _targetBody.Name;
            }
            else { _targetInfo.Text = ""; return; }
            _targetInfo.Text = $"TARGET {name}: {Ui.FormatDistance((tp - a.Position).Length)}  ·  rel. speed {Ui.FormatSpeed((tv - a.Velocity).Length)}";
        }
        else _targetInfo.Text = "";
    }

    private void UpdateStageList(Craft a)
    {
        var sb = new StringBuilder();
        var groups = a.Parts.Where(p => p.Stage >= 0 && (p.Def.Engine != null || p.Def.Decoupler != null)).GroupBy(p => p.Stage).OrderByDescending(g => g.Key).ToList();
        if (groups.Count == 0) sb.Append("(no stages)");
        foreach (var g in groups)
        {
            bool next = g.Key == StagingController.NextStage(a);
            var names = g.GroupBy(p => p.Def.Name + (p.Def.Engine != null && p.Ignited ? " ●" : p.FlamedOut ? " ✗" : "")).Select(x => x.Count() > 1 ? $"{x.Key} ×{x.Count()}" : x.Key);
            sb.AppendLine($"{(next ? "▶ " : "   ")}{g.Key + 1}: {string.Join(", ", names)}");
        }
        _stageList.Text = sb.ToString().TrimEnd();
    }

    private List<(Vec3d, string)> NavMarkersFor(Craft a, FlightEnvironment env)
    {
        var list = new List<(Vec3d, string)>();
        var ctl = a.Control;
        void Add(SasMode m, string kind) { var d = Systems_SasDirection(a, env, m); if (d.HasValue) list.Add((d.Value, kind)); }
        Add(SasMode.Prograde, "pro"); Add(SasMode.Retrograde, "retro"); Add(SasMode.Normal, "normal"); Add(SasMode.Antinormal, "anti");
        Add(SasMode.RadialOut, "rad_out"); Add(SasMode.RadialIn, "rad_in");
        var nd = Sim.NextNodeDirection(a, out var node);
        if (nd.HasValue && node != null && node.DeltaVMagnitude > 0.01) list.Add((nd.Value, "node"));
        return list;
    }

    private static Vec3d? Systems_SasDirection(Craft a, FlightEnvironment env, SasMode mode)
    {
        var tmp = new ControlState { Sas = mode, Reference = a.Control.Reference };
        return OSP.Core.Flight.Systems.ControlSystem.SasDirection(a, env, tmp);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Pause menu & dialogs
    // ------------------------------------------------------------------------------------------------------------
    private void ShowPauseMenu()
    {
        _paused = true; Sim.Paused = true;
        var gs = GameState.Instance;
        var v = Ui.VBox(8);
        v.CustomMinimumSize = new Vector2(380, 0);
        v.AddChild(Ui.Button("Resume", () => _pauseModal?.Close()));
        v.AddChild(Ui.Button("Quicksave (F5)", () => { QuickSave(); _pauseModal?.Close(); }));
        v.AddChild(Ui.Button("Save as…", ShowSaveAs));
        v.AddChild(Ui.Button("Load saved flight…", ShowLoadFlight));
        v.AddChild(Ui.Button("Settings", () => { var t = new TabContainer { CustomMinimumSize = new Vector2(660, 440) }; var s = SettingsPanels.BuildSettings(ApplyQualityLive); s.Name = "Graphics & input"; var k = SettingsPanels.BuildKeyBindings(); k.Name = "Key bindings"; t.AddChild(s); t.AddChild(k); Modal.Show(_uiLayer, "Settings", t, new Vector2(700, 0)); }));
        v.AddChild(Ui.Button("Controls reference", ShowControls));
        v.AddChild(Ui.Sep());
        if (Sim.Active != null && Sim.Active.Mode == CraftMode.Landed) v.AddChild(Ui.Button("Recover vessel", ShowRecovery, "End the flight and return to the assembly building"));
        v.AddChild(Ui.Button("Revert to launch (restart flight)", () => { if (gs.LastLaunchedDesignJson != null) { gs.PendingLaunch = SaveSerializer.DeserializeDesign(gs.LastLaunchedDesignJson, _cat); GetTree().ReloadCurrentScene(); } }));
        v.AddChild(Ui.Button("Return to Vehicle Assembly", () => gs.GoTo("Editor")));
        v.AddChild(Ui.Button("Quit to main menu", () => gs.GoTo("MainMenu")));
        _pauseModal = Modal.Show(_uiLayer, "Paused", v, new Vector2(420, 0), false);
        _pauseModal.Closed = () => { _paused = false; Sim.Paused = false; _pauseModal = null; };
    }

    private void ApplyQualityLive()
    {
        int q = GameState.Instance.Settings.GraphicsQuality;
        foreach (var pv in _planets.Values) { pv.SplitFactor = Quality.TerrainSplit(q); pv.SetAtmosphereQuality(Quality.AtmosphereSteps(q)); }
        _env.SetShadows(Quality.Shadows(q), Quality.ShadowDistance(q));
        _cam.Fov = (float)GameState.Instance.Settings.FieldOfView;
    }

    private void ShowSaveAs()
    {
        var v = Ui.VBox(8);
        var edit = new LineEdit { Text = "flight-" + Time.GetTimeStringFromSystem().Replace(':', '-'), CustomMinimumSize = new Vector2(380, 0) };
        v.AddChild(edit);
        var msg = Ui.Label("", 13, Ui.Warn);
        Modal? m = null;
        v.AddChild(Ui.Button("Save", () =>
        {
            try { GameState.Instance.WriteSave(edit.Text, SaveSerializer.Capture(Sim, edit.Text)); Log($"Saved '{edit.Text}'."); m?.Close(); }
            catch (Exception ex) { msg.Text = ex.Message; }
        }));
        v.AddChild(msg);
        m = Modal.Show(_uiLayer, "Save flight", v, new Vector2(440, 0));
    }

    private void ShowLoadFlight()
    {
        var gs = GameState.Instance;
        var v = Ui.VBox(8);
        var list = new ItemList { CustomMinimumSize = new Vector2(480, 240) };
        var saves = gs.ListSaves();
        foreach (var s in saves) list.AddItem(s.label);
        v.AddChild(list);
        var msg = Ui.Label("", 13, Ui.Warn, true);
        v.AddChild(msg);
        v.AddChild(Ui.Button("Load", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0) return;
            try { gs.PendingFlightJson = gs.ReadSave(saves[sel[0]].file); GetTree().ReloadCurrentScene(); }
            catch (Exception ex) { msg.Text = "Load failed: " + ex.Message; }
        }));
        Modal.Show(_uiLayer, "Load flight", v);
    }

    private void ShowControls() => Modal.Show(_uiLayer, "Controls reference", SettingsPanels.BuildControlsReference(), new Vector2(820, 0));

    private void ShowRecovery()
    {
        var a = Sim.Active; if (a == null) return;
        var home = System.Home;
        var local = a.Body.InertialToLocal(a.Position, Sim.Time);
        var site = home.LaunchSites[0];
        var siteDir = CelestialBody.DirectionFromLatLon(site.LatitudeDeg * MathUtil.Deg2Rad, site.LongitudeDeg * MathUtil.Deg2Rad);
        double dist = a.Body == home ? Math.Acos(Math.Clamp(Vec3d.Dot(local.Normalized, siteDir), -1, 1)) * home.Radius : double.NaN;
        double value = a.Parts.Sum(p => p.Def.Cost);
        double frac = double.IsNaN(dist) ? 0.0 : Math.Clamp(1.0 - dist / 200_000.0, 0.2, 1.0) * 0.9;
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label($"Vessel: {a.Name}", 16));
        v.AddChild(Ui.Label($"Mission time: {Ui.FormatTime(Sim.Time - a.MissionStartTime)}", 14));
        v.AddChild(Ui.Label($"Landed on {a.Body.Name}" + (double.IsNaN(dist) ? "" : $", {Ui.FormatDistance(dist)} from the launch site"), 14));
        v.AddChild(Ui.Label($"Recovered parts: {a.Parts.Count}  ·  value {value:F0}  ·  recovery {frac:P0}  ·  refund {value * frac:F0}", 14));
        v.AddChild(Ui.Label("Career funds are not implemented yet; this summary is informational.", 12, Ui.Dim, true));
        v.AddChild(Ui.Button("Return to Vehicle Assembly", () => GameState.Instance.GoTo("Editor")));
        Modal.Show(_uiLayer, "Recovery summary", v, new Vector2(520, 0));
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Developer autopilot (for headless verification: --osp-autopilot)
    // ------------------------------------------------------------------------------------------------------------
    private bool _devAutoActive;
    private int _devPhase;
    private double _devKickAlt = 900;
    private bool _devKicked;

    public bool DevAutopilotEnabled { get; set; }

    private void DevAutopilot(double dt)
    {
        if (!DevAutopilotEnabled) { return; }
        var a = Sim.Active; if (a == null) return;
        _devAutoActive = true;
        var c = a.Control;
        var env = FlightEnvironment.Compute(System, a.Body, Sim.Time, a.Position, a.Velocity, false);
        var o = Orbit.FromState(a.Position, a.Velocity, a.Body.Mu, Sim.Time);
        double ap = o.ApoapsisRadius - a.Body.Radius, pe = o.PeriapsisRadius - a.Body.Radius;
        if (_devPhase == 0) { Sim.ActivateStage(); c.Throttle = 1; _devPhase = 1; }
        bool burning = a.Parts.Any(p => p.Ignited && p.Def.Engine != null);
        if (!burning && c.Throttle > 0 && StagingController.NextStage(a) >= 0) Sim.ActivateStage();
        switch (_devPhase)
        {
            case 1:
                c.Throttle = 1;
                if (env.AltitudeSeaLevel > _devKickAlt && !_devKicked)
                {
                    var upv = env.Up; var east = Vec3d.Cross(Vec3d.UnitY, upv).Normalized;
                    var tilt = (upv * Math.Cos(4 * MathUtil.Deg2Rad) + east * Math.Sin(4 * MathUtil.Deg2Rad)).Normalized;
                    c.HoldOrientation = QuatD.FromTo(a.Orientation.Rotate(Vec3d.UnitY), tilt) * a.Orientation; c.HoldValid = true; _devKicked = true;
                }
                if (_devKicked && env.AltitudeSeaLevel > _devKickAlt + 800) { c.Sas = SasMode.Prograde; _devPhase = 2; }
                break;
            case 2:
                c.Sas = SasMode.Prograde; c.Throttle = 1;
                if (ap >= 90_000) { c.Throttle = 0; _devPhase = 3; }
                break;
            case 3:
                c.Sas = SasMode.Prograde; c.Throttle = env.AltitudeSeaLevel < 70_000 && ap < 90_000 ? 0.5 : 0;
                double dv = OrbitMath.CircularSpeed(a.Body.Mu, o.ApoapsisRadius) - OrbitMath.VisViva(a.Body.Mu, o.ApoapsisRadius, o.SemiMajorAxis);
                var (bt, ok) = ManeuverPlanner.EstimateBurnTime(a, dv);
                if (env.AltitudeSeaLevel > 70_000 && ok && o.TimeToApoapsis(Sim.Time) < bt / 2 + 1) _devPhase = 4;
                break;
            case 4:
                c.Sas = SasMode.Prograde; c.Throttle = 1;
                if (pe > 75_000) { c.Throttle = 0; _devPhase = 5; Log("[autopilot] orbit achieved"); }
                break;
            case 5: c.Throttle = 0; break;
        }
    }
}
