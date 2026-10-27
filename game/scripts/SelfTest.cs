using System;
using System.Linq;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Game.Data;
using OSP.Game.Rendering;

namespace OSP.Game;

/// <summary>Headless-friendly visual checks driven from the command line: godot --path game -- --selftest=planet</summary>
public partial class SelfTest : Node
{
    public string Name_ = "";
    private PlanetView? _view; private SpaceEnvironment? _env; private Camera3D? _cam;
    private int _frame; private int _shot;

    public override void _Ready()
    {
        GameData.Load();
        if (Name_ == "editor") { StartEditorTest(); return; }
        if (Name_ == "flight") { StartFlightTest(); return; }
        _env = new SpaceEnvironment(); AddChild(_env);
        _cam = new Camera3D { Current = true, Fov = 60, Near = 0.3f, Far = 4e6f }; AddChild(_cam);
        var sys = GameData.NewSystem();
        var bodyId = OS.GetEnvironment("OSP_BODY"); if (bodyId == "") bodyId = "aldara";
        var al = sys.ById(bodyId);
        _view = new PlanetView(al); AddChild(_view);
        var inten = OS.GetEnvironment("OSP_INTENSITY");
        if (inten != "") _view.SetAtmosphereParam("intensity", float.Parse(inten));
        GD.Print("[selftest] planet scene built");
    }

    private void Place(Vec3d camPosBodyInertial, Vec3d lookDirWorld, Vec3d up)
    {
        // render origin = camera; camera sits at the world origin
        var rel = -camPosBodyInertial;
        var sunDir = new Vec3d(0.6, 0.45, 0.65).Normalized;
        _cam!.Position = Vector3.Zero;
        var ld = new Vector3((float)lookDirWorld.X, (float)lookDirWorld.Y, (float)lookDirWorld.Z);
        var u = new Vector3((float)up.X, (float)up.Y, (float)up.Z);
        _cam.LookAt(_cam.Position + ld, u);
        _view!.CameraForwardWorld = lookDirWorld.Normalized;
        double dens = 0;
        var atm = _view!.Body.Atmosphere;
        double alt = camPosBodyInertial.Length - _view.Body.Radius;
        if (atm != null) dens = Math.Exp(-alt / atm.ScaleHeight);
        _env!.Update(sunDir, 1.0, dens);
        if (_frame == 0) _view.Prewarm(0, rel, camPosBodyInertial, sunDir);
        else _view.UpdateView(0, rel, camPosBodyInertial, sunDir);
    }

    public override void _Process(double delta)
    {
        if (Name_ == "editor") { EditorTestStep(); return; }
        if (Name_ == "flight") { FlightTestStep(); return; }
        var al = _view!.Body;
        double R = al.Radius;
        var dir = CelestialBody.DirectionFromLatLon(0.3, 0.5);
        var up = dir;
        var east = Vec3d.Cross(Vec3d.UnitY, up).Normalized;
        var north = Vec3d.Cross(up, east).Normalized;
        double ground = R + al.SurfaceHeightAt(dir);
        switch (_shot)
        {
            case 0: Place(dir * (ground + 40), east * 0.97 + up * 0.05 + north * 0.1, up); break;               // standing on the pad looking east
            case 1: Place(dir * (R + 12000) , -up * 0.6 + east * 0.8, east); break;                                  // 12 km up, looking out
            case 2: Place(dir * (R * 3.2) + east * R * 0.3, -dir, Vec3d.UnitY); break;                          // from far away
        }
        _frame++;
        if (_frame == 6)
        {
            GetViewport().GetTexture().GetImage().SavePng($"/tmp/claude-0/shot_planet{_shot}.png");
            GD.Print($"[selftest] shot {_shot}: chunks drawn {_view!.ActiveChunks} cached {_view.CachedChunks} levels [{string.Join(",", _view.DesiredByLevel)}]");
            _shot++; _frame = 1;
            if (_shot == 3) GetTree().Quit();
        }
    }
}

public partial class SelfTest
{
    private OSP.Game.Editor.EditorScene? _editor;
    private int _tf, _step;
    private int _failures;
    private int _tankId = -1;

    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "[PASS] " : "[FAIL] ") + what);
        if (!ok) _failures++;
    }

    private void StartEditorTest()
    {
        GameState.Instance.CurrentDesign = new OSP.Core.Vehicles.VehicleDesign();
        _editor = new OSP.Game.Editor.EditorScene();
        AddChild(_editor);
    }

    private void Mouse(Vector2 pos)
    {
        GetViewport().WarpMouse(pos);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos, Relative = Vector2.Zero });
    }

    private void Click(Vector2 pos)
    {
        Mouse(pos);
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true });
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false });
    }

    private void EditorTestStep()
    {
        var ed = _editor!;
        _tf++;
        if (_tf < 10) return;
        // one action every 6 frames so hover/ghost logic runs between steps
        if ((_tf % 6) != 0) return;
        var d = ed.DebugDesign;
        var centre = new Vector2(800, 450);
        switch (_step++)
        {
            case 0: ed.DebugPick("pod_aster"); break;
            case 1: Mouse(centre); break;
            case 2: Click(centre); break;
            case 3: Check(d.Parts.Count == 1 && d.Parts[0].PartId == "pod_aster", "first click places the pod as root"); ed.DebugPick("tank_s2"); break;
            case 4: Mouse(ed.DebugNodeScreen(d.Parts[0].Id, "bottom") + new Vector2(6, 4)); break;
            case 5: break;
            case 6: Click(ed.DebugNodeScreen(d.Parts[0].Id, "bottom") + new Vector2(6, 4)); break;
            case 7:
                Check(d.Parts.Count == 2, "tank attached");
                if (d.Parts.Count == 2) { Check(d.Parts[1].ParentId == d.Parts[0].Id && d.Parts[1].ParentNode == "bottom" && d.Parts[1].ChildNode == "top", "tank snapped bottom->top"); _tankId = d.Parts[1].Id; }
                ed.DebugSymmetry = 3; ed.DebugPick("fin_delta");
                break;
            case 8: if (_tankId >= 0) Mouse(ed.DebugPartScreen(_tankId, 0.6, -0.5)); break;
            case 9: break;
            case 10: if (_tankId >= 0) Click(ed.DebugPartScreen(_tankId, 0.6, -0.5)); break;
            case 11:
                Check(d.Parts.Count(p => p.PartId == "fin_delta") == 3, $"3 fins placed radially with symmetry (found {d.Parts.Count(p => p.PartId == "fin_delta")})");
                Check(d.Parts.Where(p => p.PartId == "fin_delta").Select(p => p.SymmetryGroup).Distinct().Count() == 1, "fins share a symmetry group");
                ed.DebugUndo(); break;
            case 12: Check(d.Parts.Count == 0 || ed.DebugDesign.Parts.Count == 2, "undo removes the fins"); ed.DebugRedo(); break;
            case 13: Check(ed.DebugDesign.Parts.Count == 5, "redo restores the fins"); ed.DebugSymmetry = 1; ed.DebugPick("eng_swift"); break;
            case 14: Mouse(ed.DebugNodeScreen(_tankId, "bottom") + new Vector2(2, 2)); break;
            case 15: break;
            case 16: Click(ed.DebugNodeScreen(_tankId, "bottom") + new Vector2(2, 2)); break;
            case 17:
                Check(ed.DebugDesign.Parts.Any(p => p.PartId == "eng_swift"), "engine attached to the tank bottom");
                var errs = OSP.Core.Vehicles.VehicleValidator.Validate(ed.DebugDesign, GameData.Catalog).Where(i => i.Severity == OSP.Core.Vehicles.IssueSeverity.Error).ToList();
                Check(errs.Count == 0, "resulting vehicle validates");
                // save + reload through GameState
                var gs = GameState.Instance; ed.DebugDesign.Name = "UI Test Rocket"; var file = gs.SaveDesign(ed.DebugDesign);
                var back = gs.LoadDesign(file);
                Check(back.Parts.Count == ed.DebugDesign.Parts.Count, "saved design reloads with the same part count");
                gs.DeleteDesign(file);
                break;
            case 18:
                GetViewport().GetTexture().GetImage().SavePng("/tmp/claude-0/shot_editor_built.png");
                GD.Print(_failures == 0 ? "[selftest] editor: ALL PASSED" : $"[selftest] editor: {_failures} FAILED");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                break;
        }
    }
}

public partial class SelfTest
{
    private OSP.Game.Flight.FlightScene? _flight;
    private double _altAtStep;

    private void StartFlightTest()
    {
        var gs = GameState.Instance;
        gs.PendingLaunch = gs.LoadDesign("Boosted_Swift", true);
        _flight = new OSP.Game.Flight.FlightScene();
        AddChild(_flight);
    }

    private void Key(Key k, bool pressed = true)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = pressed });
    }

    private void Tap(Key k) { Key(k, true); Key(k, false); }

    private void FlightTestStep()
    {
        var f = _flight!; var sim = f.DebugSim; var a = sim.Active;
        _tf++;
        if (_tf < 8 || a == null) { if (a == null && _tf > 30) { Check(false, "active vessel exists"); GetTree().Quit(1); } return; }
        if (_tf % 5 != 0) return;
        int step = _step++;
        switch (step)
        {
            case 0:
                Check(a.Mode == OSP.Core.Flight.CraftMode.Landed, "vessel starts landed on the pad");
                Tap(Godot.Key.Space); break;
            case 1:
                Check(a.Parts.Any(p => p.Ignited && p.Def.Engine != null), "Space ignites the first stage");
                Key(Godot.Key.Z); break;
            case 2:
                Key(Godot.Key.Z, false);
                Check(a.Control.Throttle >= 0.99, "Z sets full throttle");
                break;
            case 5: _altAtStep = a.Position.Length - a.Body.Radius; break;
            case 50:
                Check(a.Position.Length - a.Body.Radius > _altAtStep + 20, "vessel climbs under thrust");
                Check(a.Parts.Sum(p => p.Amount("Propellant") + p.Amount("SolidFuel")) < a.Parts.Sum(p => p.Capacity("Propellant") + p.Capacity("SolidFuel")), "propellant is consumed");
                GameState.Instance.DeleteSave("quicksave");
                Tap(Godot.Key.F5);
                break;
            case 51:
                Check(GameState.Instance.ListSaves().Any(s => s.file == "quicksave"), "F5 writes a quicksave");
                GameState.Instance.DeleteSave("quicksave");
                Tap(Godot.Key.Escape);
                break;
            case 52:
                Check(f.DebugPaused, "Esc opens the pause menu and pauses the simulation");
                Tap(Godot.Key.Escape);
                break;
            case 53:
                Check(!f.DebugPaused, "Esc again resumes");
                Tap(Godot.Key.M);
                break;
            case 54:
                Check(f.DebugMapMode, "M toggles the map view");
                GetViewport().GetTexture().GetImage().SavePng("/tmp/claude-0/shot_flight_map_srb.png");
                GD.Print(_failures == 0 ? "[selftest] flight: ALL PASSED" : $"[selftest] flight: {_failures} FAILED");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                break;
            default:
                if (step < 50 && step % 10 == 0) GD.Print($"[flight-test] t={sim.Time:F1} alt={a.Position.Length - a.Body.Radius:F1} v={a.Velocity.Length:F1} thrust={a.LastTotalThrust / 1000:F0}kN mode={a.Mode}");
                break;
        }
    }
}
