using System;
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
        _env = new SpaceEnvironment(); AddChild(_env);
        _cam = new Camera3D { Current = true, Fov = 60, Near = 0.3f, Far = 4e6f }; AddChild(_cam);
        var sys = GameData.NewSystem();
        var al = sys.ById("aldara");
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
        var al = _view!.Body;
        double R = al.Radius;
        var site = al.LaunchSites[0];
        var dir = CelestialBody.DirectionFromLatLon(0, 0);
        var up = dir;
        var east = Vec3d.Cross(Vec3d.UnitY, up).Normalized;
        var north = Vec3d.Cross(up, east).Normalized;
        double ground = R + al.SurfaceHeightAt(dir);
        switch (_shot)
        {
            case 0: Place(dir * (ground + 6), east * 0.97 + up * 0.05 + north * 0.1, up); break;               // standing on the pad looking east
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
