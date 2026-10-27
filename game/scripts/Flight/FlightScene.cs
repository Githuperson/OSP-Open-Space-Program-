using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using OSP.Game.Data;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Flight;

/// <summary>
/// The flight scene: owns the <see cref="FlightSimulation"/>, renders it with a floating origin (everything is placed
/// relative to the active vessel in double precision), reads the controls and drives the HUD and map.
/// </summary>
public sealed partial class FlightScene : Node3D
{
    public FlightSimulation Sim { get; private set; } = null!;
    public StarSystem System { get; private set; } = null!;
    private PartCatalog _cat = null!;

    private SpaceEnvironment _env = null!;
    private Camera3D _cam = null!;
    private Node3D _world = null!;
    private readonly Dictionary<string, PlanetView> _planets = new();
    private readonly Dictionary<int, CraftView> _craftViews = new();
    private PadView? _pad;
    private double _yaw = 0.6, _pitch = 0.2, _dist = 25;
    private bool _orbiting;
    private bool _camVesselLocked;
    private bool _paused;
    private bool _loaded;
    private double _throttleKeyRate = 0.6;
    private string _loadError = "";
    private double _debugTime;

    public override void _Ready()
    {
        GameData.Load();
        var gs = GameState.Instance;
        gs.ApplyGraphicsSettings();
        _cat = GameData.Catalog;
        System = GameData.NewSystem();

        try
        {
            if (gs.PendingFlightJson != null)
            {
                Sim = SaveSerializer.Restore(gs.PendingFlightJson, System, _cat, out _);
                gs.PendingFlightJson = null;
            }
            else if (gs.PendingLaunch != null)
            {
                Sim = new FlightSimulation(System, _cat);
                var site = System.Home.LaunchSites[0];
                Sim.Time = FindMorningTime(System.Home, site);
                gs.LastLaunchedDesignJson = SaveSerializer.SerializeDesign(gs.PendingLaunch);
                Sim.Launch(gs.PendingLaunch, System.Home, site);
                gs.PendingLaunch = null;
            }
            else { _loadError = "No vehicle to launch."; }
        }
        catch (DataException ex) { _loadError = ex.Message; GD.PushError("[OSP] flight load failed: " + ex.Message); }

        BuildWorld();
        BuildHud();
        if (_loadError != "") { ShowFatal(_loadError); return; }
        Sim.EventRaised += OnSimEvent;
        _loaded = true;
        DevAutopilotEnabled = gs.DevAutopilot;
        if (gs.DevWarp > 0) Sim.SetWarp(gs.DevWarp);
        if (gs.DevDeploy) { Sim.SetGear(true); Sim.SetPanels(true); foreach (var p in Sim.Active!.Parts) { p.LegDeploy = p.LegsDeployedTarget ? 1 : 0; p.SolarDeploy = p.SolarDeployedTarget ? 1 : 0; } _dist = 14; _yaw = 0.9; _pitch = 0.1; }
        if (gs.DevMap) { ToggleMap(); if (Sim.Active != null && Sim.Active.Mode != CraftMode.Landed) { var o0 = Orbit.FromState(Sim.Active.Position, Sim.Active.Velocity, Sim.Active.Body.Mu, Sim.Time); _selNode = Sim.AddNode(Sim.Active, Sim.Time + o0.TimeToApoapsis(Sim.Time), 650, 0, 0); } }
        CenterCameraOnVessel(true);
        UpdateRender(0);
        foreach (var p in _planets.Values.Where(p => p.Body.Terrain != null && p.Body == Sim.Active?.Body)) PrewarmPlanet(p);
        Log($"Launch site: {System.Home.LaunchSites[0].Name}, {System.Home.Name}. Press {Bindings.KeyName(gs.Keys.Get("stage"))} to ignite, {Bindings.KeyName(gs.Keys.Get("throttle_max"))} for full throttle.");
    }

    /// <summary>First universal time (within one day) at which the sun is about 35 degrees up and rising at the pad.</summary>
    public static double FindMorningTime(CelestialBody home, LaunchSiteDef site)
    {
        var dirLocal = CelestialBody.DirectionFromLatLon(site.LatitudeDeg * MathUtil.Deg2Rad, site.LongitudeDeg * MathUtil.Deg2Rad);
        double bestT = 0, bestErr = 1e9;
        for (double t = 0; t < home.RotationPeriod; t += 120)
        {
            var up = home.LocalToInertial(dirLocal, t);
            var (r, _) = home.Orbit!.StateAt(t);
            var sun = (-r).Normalized;   // star direction from Aldara in the star-centred frame (parent orbit position negated)
            double elev = Math.Asin(Math.Clamp(Vec3d.Dot(up, sun), -1, 1)) * MathUtil.Rad2Deg;
            var up2 = home.LocalToInertial(dirLocal, t + 120);
            double elev2 = Math.Asin(Math.Clamp(Vec3d.Dot(up2, (-home.Orbit.StateAt(t + 120).r).Normalized), -1, 1)) * MathUtil.Rad2Deg;
            if (elev2 <= elev) continue;
            double err = Math.Abs(elev - 35);
            if (err < bestErr) { bestErr = err; bestT = t; }
        }
        return bestT;
    }

    // ------------------------------------------------------------------------------------------------------------
    private void BuildWorld()
    {
        var gs = GameState.Instance; int q = gs.Settings.GraphicsQuality;
        _env = new SpaceEnvironment(); AddChild(_env);
        _env.SetShadows(Quality.Shadows(q), Quality.ShadowDistance(q));
        _world = new Node3D { Name = "World" }; AddChild(_world);
        _cam = new Camera3D { Current = true, Fov = (float)gs.Settings.FieldOfView, Near = 0.3f, Far = 4.0e6f };
        _world.AddChild(_cam);

        foreach (var b in System.Bodies)
        {
            var pv = new PlanetView(b) { SplitFactor = Quality.TerrainSplit(q) };
            pv.SetAtmosphereQuality(Quality.AtmosphereSteps(q));
            _world.AddChild(pv);
            _planets[b.Id] = pv;
        }
        var site = System.Home.LaunchSites.FirstOrDefault();
        if (site != null) { _pad = new PadView(System.Home, site); _world.AddChild(_pad); }
    }

    private void PrewarmPlanet(PlanetView pv)
    {
        if (Sim.Active == null) return;
        var (rel, camRel, sun) = BodyViewVectors(pv.Body, Sim.Active, Vec3d.Zero);
        pv.CameraForwardWorld = (-camRel).Normalized;
        pv.Prewarm(Sim.Time, rel, camRel, sun);
    }

    private (Vec3d rel, Vec3d camRelBody, Vec3d sun) BodyViewVectors(CelestialBody b, Craft active, Vec3d camOffset)
    {
        double t = Sim.Time;
        var (bRoot, _) = System.StateInRoot(b, t);
        var (soiRoot, _) = System.StateInRoot(active.Body, t);
        var posRelSoi = bRoot - soiRoot;
        var rel = posRelSoi - active.Position;
        var sun = bRoot.LengthSquared > 1 ? (-bRoot).Normalized : Vec3d.UnitX;
        return (rel, -rel + camOffset, sun);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Frame update
    // ------------------------------------------------------------------------------------------------------------
    public override void _Process(double delta)
    {
        if (!_loaded) return;
        _debugTime += delta;
        if (!_paused) { PollControls(delta); }
        if (!_paused && !_gameOver) Sim.Advance(delta);
        _gameOver = Sim.Active == null;
        UpdateRender(delta);
        UpdateHud(delta);
        if (_mapMode) UpdateMap(delta);
        DevAutopilot(delta);
    }

    private bool _gameOver;

    private Vec3d CameraOffset()
    {
        double cp = Math.Cos(_pitch);
        return new Vec3d(cp * Math.Sin(_yaw), Math.Sin(_pitch), cp * Math.Cos(_yaw)) * _dist;
    }

    private void UpdateRender(double delta)
    {
        var a = Sim.Active;
        if (a == null || _mapMode) { SyncCraftViews(null); return; }
        var body = a.Body;
        double t = Sim.Time;
        var camOff = CameraOffset();
        if (_camVesselLocked) camOff = a.Orientation.Rotate(camOff);
        _pitch = Math.Clamp(_pitch, -1.5, 1.5);
        _cam.Position = new Vector3((float)camOff.X, (float)camOff.Y, (float)camOff.Z);
        var camPos = _cam.Position;
        // Orbit camera around the CoM at the origin. Up vector: use the craft's up (local vertical) in the low atmosphere for a steady horizon.
        var upRef = (a.Position).Normalized;
        var upV = new Vector3((float)upRef.X, (float)upRef.Y, (float)upRef.Z);
        if (_camVesselLocked) { var cu = a.Orientation.Rotate(new Vec3d(0, 0, -1)); upV = new Vector3((float)cu.X, (float)cu.Y, (float)cu.Z); }
        if (Mathf.Abs(camPos.Normalized().Dot(upV)) > 0.995f) upV = Vector3.Right;
        _cam.LookAt(Vector3.Zero, upV);
        _cam.Near = (float)Math.Clamp(_dist * 0.02, 0.3, 4.0);
        var fwd = -_cam.GlobalTransform.Basis.Z;
        var fwdD = new Vec3d(fwd.X, fwd.Y, fwd.Z);

        // Planets
        foreach (var (id, pv) in _planets)
        {
            var (rel, camRel, sun) = BodyViewVectors(pv.Body, a, camOff);
            pv.CameraForwardWorld = fwdD;
            pv.UpdateView(t, rel, camRel, sun);
            if (pv.Body == System.Home && _pad != null) _pad.UpdateView(t, rel, pv.RenderScale);
        }
        if (body != System.Home && _pad != null) _pad.Visible = false;

        // Sun light + sky
        var (bRoot, _) = System.StateInRoot(body, t);
        var starDir = (-(bRoot + a.Position)).Normalized;
        double flux = System.SolarFluxAtRootPosition(bRoot + a.Position) / Math.Max(1e-9, System.Root.SolarFluxAtReference);
        double dens = 0;
        if (body.Atmosphere != null) dens = Math.Exp(-Math.Max(0, a.Position.Length - body.Radius) / (body.Atmosphere.ScaleHeight * 1.5)) * Math.Min(1.0, body.Atmosphere.SeaLevelDensity / 0.6);
        _env.Update(starDir, Math.Sqrt(Math.Max(0.2, flux)), dens);

        SyncCraftViews(a);
    }

    private void SyncCraftViews(Craft? active)
    {
        var alive = new HashSet<int>(Sim.Crafts.Select(c => c.Id));
        foreach (var id in _craftViews.Keys.ToList())
            if (!alive.Contains(id)) { _craftViews[id].QueueFree(); _craftViews.Remove(id); }
        if (active == null) { foreach (var v in _craftViews.Values) v.Visible = false; return; }
        double t = Sim.Time;
        foreach (var c in Sim.Crafts)
        {
            if (!_craftViews.TryGetValue(c.Id, out var v)) { v = new CraftView(c); _world.AddChild(v); _craftViews[c.Id] = v; }
            Vec3d pos = c.Position;
            if (c.Body != active.Body) (pos, _) = System.ConvertFrame(c.Position, c.Velocity, c.Body, active.Body, t);
            var rel = pos - active.Position;
            v.Visible = rel.Length < 120_000;
            if (!v.Visible) continue;
            var q = c.Orientation;
            v.Transform = new Transform3D(new Basis(new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W)), new Vector3((float)rel.X, (float)rel.Y, (float)rel.Z));
            double pAtm = 0;
            if (c.Body.Atmosphere != null) pAtm = c.Body.Atmosphere.Pressure(Math.Max(0, c.Position.Length - c.Body.Radius)) / 101325.0;
            v.Refresh(pAtm);
        }
    }

    private void CenterCameraOnVessel(bool snapDistance)
    {
        var a = Sim.Active; if (a == null) return;
        if (a.MassDirty) a.UpdateMassProperties();
        if (snapDistance) _dist = Math.Clamp(a.BoundingRadius * 3.2, 10, 120);
        _yaw = 0.6; _pitch = 0.2;
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Controls
    // ------------------------------------------------------------------------------------------------------------
    private void PollControls(double dt)
    {
        var a = Sim.Active; if (a == null) return;
        var k = GameState.Instance.Keys;
        var c = a.Control;
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
        double pitch = (k.IsDown("pitch_down") ? 1 : 0) - (k.IsDown("pitch_up") ? 1 : 0);
        double yaw = (k.IsDown("yaw_right") ? 1 : 0) - (k.IsDown("yaw_left") ? 1 : 0);
        double roll = (k.IsDown("roll_right") ? 1 : 0) - (k.IsDown("roll_left") ? 1 : 0);
        if (!_devAutoActive) { c.Pitch = pitch; c.Yaw = yaw; c.Roll = roll; }
        if (!_devAutoActive)
        {
            if (k.IsDown("throttle_up")) c.Throttle = Math.Min(1, c.Throttle + _throttleKeyRate * dt);
            if (k.IsDown("throttle_down")) c.Throttle = Math.Max(0, c.Throttle - _throttleKeyRate * dt);
            if (k.IsDown("throttle_max")) c.Throttle = 1;
            if (k.IsDown("throttle_cut")) c.Throttle = 0;
        }
        c.Translation = new Vec3d(
            (k.IsDown("rcs_left") ? 1 : 0) - (k.IsDown("rcs_right") ? 1 : 0),
            (k.IsDown("rcs_forward") ? 1 : 0) - (k.IsDown("rcs_back") ? 1 : 0),
            (k.IsDown("rcs_up") ? 1 : 0) - (k.IsDown("rcs_down") ? 1 : 0));
        if (c.Sas == SasMode.Maneuver)
        {
            var d = Sim.NextNodeDirection(a, out _);
            if (d.HasValue) c.ManeuverDirection = d.Value;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_loaded) return;
        var gs = GameState.Instance; var k = gs.Keys;
        if (e is InputEventKey ke && ke.Pressed && !ke.Echo)
        {
            if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
            if (ke.Keycode == Key.Escape) { TogglePause(); return; }
            if (_paused) return;
            var a = Sim.Active;
            if (k.Matches(ke, "stage")) { if (a != null) { var r = Sim.ActivateStage(); if (r != null && r.NothingToDo) Log("No more stages."); } }
            else if (k.Matches(ke, "sas_toggle") && a != null) { a.Control.SasEnabled = !a.Control.SasEnabled; a.Control.HoldOrientation = a.Orientation; a.Control.HoldValid = true; Log("SAS " + (a.Control.SasEnabled ? "on" : "off")); }
            else if (k.Matches(ke, "rcs_toggle") && a != null) { a.Control.RcsEnabled = !a.Control.RcsEnabled; Log("RCS " + (a.Control.RcsEnabled ? "on" : "off")); }
            else if (k.Matches(ke, "gear") && a != null) { bool down = !a.Parts.Any(p => p.LegsDeployedTarget); Sim.SetGear(down); Log(down ? "Landing gear deploying" : "Landing gear retracting"); }
            else if (k.Matches(ke, "solar") && a != null) { bool out_ = !a.Parts.Any(p => p.SolarDeployedTarget); Sim.SetPanels(out_); Log(out_ ? "Solar panels extending" : "Solar panels retracting"); }
            else if (k.Matches(ke, "chutes")) { Sim.ArmParachutes(); }
            else if (k.Matches(ke, "map")) ToggleMap();
            else if (k.Matches(ke, "camera_reset")) CenterCameraOnVessel(true);
            else if (k.Matches(ke, "camera_mode")) { _camVesselLocked = !_camVesselLocked; Log("Camera: " + (_camVesselLocked ? "locked to the vessel" : "locked to the world")); }
            else if (k.Matches(ke, "hide_ui")) _hudRoot.Visible = !_hudRoot.Visible;
            else if (k.Matches(ke, "controls_help")) ShowControls();
            else if (k.Matches(ke, "warp_up")) { Sim.SetWarp(Sim.WarpIndex + 1); }
            else if (k.Matches(ke, "warp_down")) { Sim.SetWarp(Sim.WarpIndex - 1); }
            else if (k.Matches(ke, "warp_stop")) { Sim.StopWarp(); }
            else if (k.Matches(ke, "next_craft")) CycleCraft(1);
            else if (k.Matches(ke, "prev_craft")) CycleCraft(-1);
            else if (k.Matches(ke, "quicksave")) QuickSave();
            else if (k.Matches(ke, "quickload")) QuickLoad();
            else if (k.Matches(ke, "screenshot")) Screenshot();
            else if (a != null)
            {
                foreach (var (id, mode) in new[] { ("sas_stability", SasMode.StabilityAssist), ("sas_prograde", SasMode.Prograde), ("sas_retrograde", SasMode.Retrograde), ("sas_normal", SasMode.Normal), ("sas_antinormal", SasMode.Antinormal), ("sas_radial_out", SasMode.RadialOut), ("sas_radial_in", SasMode.RadialIn), ("sas_maneuver", SasMode.Maneuver) })
                    if (k.Matches(ke, id)) SetSasMode(mode);
            }
        }
        else if (e is InputEventMouseButton mb)
        {
            if (_mapMode) { MapMouse(mb); return; }
            if (mb.ButtonIndex == MouseButton.Right) _orbiting = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _dist = Math.Max(3, _dist * 0.9);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _dist = Math.Min(3000, _dist * 1.1);
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_mapMode) { MapMotion(mm); return; }
            if (_orbiting) { var s = gs.Settings.MouseSensitivity; _yaw -= mm.Relative.X * 0.005 * s; _pitch += mm.Relative.Y * 0.005 * s; }
        }
    }

    private void SetSasMode(SasMode mode)
    {
        var a = Sim.Active; if (a == null) return;
        a.Control.Sas = mode; a.Control.SasEnabled = true;
        if (mode == SasMode.StabilityAssist) { a.Control.HoldOrientation = a.Orientation; a.Control.HoldValid = true; }
        Log("SAS mode: " + SasName(mode));
    }

    public static string SasName(SasMode m) => m switch
    {
        SasMode.StabilityAssist => "Stability assist", SasMode.Prograde => "Prograde", SasMode.Retrograde => "Retrograde",
        SasMode.Normal => "Normal", SasMode.Antinormal => "Anti-normal", SasMode.RadialOut => "Radial out", SasMode.RadialIn => "Radial in",
        SasMode.Maneuver => "Maneuver node", _ => m.ToString(),
    };

    private void CycleCraft(int dir)
    {
        var list = Sim.Crafts.Where(c => c.HasCommand || c == Sim.Active).ToList();
        if (list.Count < 2 || Sim.Active == null) return;
        int i = list.IndexOf(Sim.Active);
        Sim.SetActive(list[(i + dir + list.Count) % list.Count]);
        CenterCameraOnVessel(true);
        Log("Switched to " + Sim.Active!.Name);
    }

    private void TogglePause()
    {
        if (_pauseModal != null) { _pauseModal.Close(); return; }
        ShowPauseMenu();
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Persistence
    // ------------------------------------------------------------------------------------------------------------
    private void QuickSave()
    {
        try
        {
            var save = SaveSerializer.Capture(Sim, "quicksave");
            GameState.Instance.WriteSave("quicksave", save);
            Log("Quicksaved.");
        }
        catch (Exception ex) { Log("Quicksave failed: " + ex.Message); }
    }

    private void QuickLoad()
    {
        try
        {
            GameState.Instance.PendingFlightJson = GameState.Instance.ReadSave("quicksave");
            GetTree().ReloadCurrentScene();
        }
        catch (Exception ex) { Log("Quickload failed: " + ex.Message); }
    }

    private void Screenshot()
    {
        var dir = ProjectSettings.GlobalizePath("user://screenshots");
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var path = $"{dir}/osp_{Time.GetDatetimeStringFromSystem().Replace(':', '-')}.png";
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log("Screenshot saved: " + path);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Sim events -> messages / effects
    // ------------------------------------------------------------------------------------------------------------
    private void OnSimEvent(SimEvent e)
    {
        switch (e.Kind)
        {
            case SimEventKind.Info: case SimEventKind.StageActivated: case SimEventKind.EngineIgnition:
            case SimEventKind.ParachuteOpened: case SimEventKind.Touchdown: case SimEventKind.SoiChange: case SimEventKind.Separation:
                Log(e.Message); break;
            case SimEventKind.Warning: case SimEventKind.WarpLimited: case SimEventKind.EngineFlameout:
                Log("⚠ " + e.Message); break;
            case SimEventKind.JointFailed: case SimEventKind.PartDestroyed: case SimEventKind.ParachuteCut: case SimEventKind.CraftDestroyed: case SimEventKind.Crashed:
                Log("✖ " + e.Message); SpawnExplosion(e); break;
        }
    }

    private void SpawnExplosion(SimEvent e)
    {
        var a = Sim.Active; if (a == null) return;
        var craft = Sim.FindCraft(e.CraftId);
        if (craft == null || !_craftViews.TryGetValue(craft.Id, out var view)) return;
        var local = e.LocalPosition ?? Vec3d.Zero;
        var com = craft.Mass.CenterOfMass;
        var off = local - com;
        var pos = view.ToGlobal(new Vector3((float)off.X, (float)off.Y, (float)off.Z));
        Effects.Explosion(_world, pos, 1.5f);
    }
}
