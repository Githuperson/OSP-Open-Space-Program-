using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Science;
using OSP.Core.Vehicles;
using OSP.Game.Editor;
using OSP.Game.Flight;

namespace OSP.Game.App
{
    /// <summary>
    /// Automated end-to-end check of the running game, started with:
    ///   godot --path . -- --smoke-test [--screenshots=DIR] [--steps-per-frame=N]
    /// It opens the menu and the editor, loads the stock "Pathfinder Orbiter", launches it, flies a gravity turn to orbit
    /// using only the normal pilot controls (throttle, steering, staging), opens the map, saves and reloads the game,
    /// and exits with code 0 on success or 1 on failure. Screenshots are written when rendering is available.
    /// </summary>
    public partial class SmokeTest : Node
    {
        private enum Phase { Menu, Editor, Pad, Ascent, Coast, Circularize, Orbit, Map, SaveLoad, Done }

        private Phase _phase = Phase.Menu;
        private int _frames;
        private string _shots;
        private int _stepsPerFrame = 25;
        private readonly List<string> _log = new();
        private double _maxAlt;
        private bool _failed;
        private int _phaseFrames;

        public void Begin(string[] args)
        {
            foreach (string a in args)
            {
                if (a.StartsWith("--screenshots=")) _shots = a["--screenshots=".Length..];
                if (a.StartsWith("--steps-per-frame=")) _stepsPerFrame = int.Parse(a["--steps-per-frame=".Length..]);
            }
            if (_shots != null) DirAccess.MakeDirRecursiveAbsolute(_shots);
            Log($"Smoke test started (renderer: {RenderingServer.GetVideoAdapterName()}, screenshots: {_shots ?? "off"})");
            GameRoot.Instance.GoToMenu();
        }

        private void Log(string s)
        {
            _log.Add(s);
            GD.Print("[SMOKE] " + s);
        }

        private void Fail(string why)
        {
            Log("FAIL: " + why);
            _failed = true;
            Finish();
        }

        private void Finish()
        {
            Log(_failed ? "RESULT: FAILED" : "RESULT: PASSED");
            GetTree().Quit(_failed ? 1 : 0);
            _phase = Phase.Done;
        }

        private void Shot(string name)
        {
            if (_shots == null || DisplayServer.GetName() == "headless") return;
            Image img = GetViewport().GetTexture().GetImage();
            string path = $"{_shots}/{name}.png";
            img.SavePng(path);
            Log($"screenshot {path}");
        }

        private void Next(Phase p)
        {
            _phase = p;
            _phaseFrames = 0;
        }

        public override void _Process(double delta)
        {
            if (_phase == Phase.Done) return;
            _frames++;
            _phaseFrames++;
            GameRoot game = GameRoot.Instance;
            try
            {
                switch (_phase)
                {
                    case Phase.Menu:
                        if (_phaseFrames == 20) Shot("01_menu");
                        if (_phaseFrames == 25)
                        {
                            game.NewGame(GameMode.Sandbox);
                            Next(Phase.Editor);
                        }
                        break;
                    case Phase.Editor:
                        if (_phaseFrames == 3)
                        {
                            if (game.CurrentScene is not EditorScene editor) { Fail("editor scene did not open"); return; }
                            string file = game.ListVesselFiles().FirstOrDefault(f => f.StartsWith("Pathfinder"));
                            if (file == null) { Fail("stock vessel Pathfinder not installed"); return; }
                            VesselDesign d = game.LoadDesign(file, out var warnings);
                            if (warnings.Count > 0) Log("design warnings: " + string.Join("; ", warnings));
                            editor.SetDesign(d);
                            // Exercise editor operations: duplicate-free undo/redo round trip.
                            int parts = editor.Design.Parts.Count;
                            editor.DeletePart(editor.Design.Parts.Last(p => p.Def.HasModule<LiftingSurfaceModuleDef>()));
                            if (editor.Design.Parts.Count != parts - 4) { Fail($"symmetric delete removed {parts - editor.Design.Parts.Count} parts"); return; }
                            editor.Undo();
                            if (editor.Design.Parts.Count != parts) { Fail("undo did not restore parts"); return; }
                            Log($"editor: loaded '{d.Name}' with {parts} parts, delete/undo OK");
                        }
                        if (_phaseFrames == 40) Shot("02_editor");
                        if (_phaseFrames == 45)
                        {
                            ((EditorScene)game.CurrentScene).Launch();
                            Next(Phase.Pad);
                        }
                        break;
                    case Phase.Pad:
                    {
                        if (game.CurrentScene is not FlightScene fs) { Fail("flight scene did not open"); return; }
                        fs.ExternalStepping = true;
                        Vessel v = fs.World.ActiveVessel;
                        if (_phaseFrames == 1) Log($"on pad: alt {v.Altitude:F1} m, AGL {v.Telemetry.AltitudeTerrain:F2} m, mass {v.Mass:F0} kg, situation {v.Situation}");
                        if (_phaseFrames == 60) Shot("03_pad");
                        if (_phaseFrames == 61)
                        {
                            if (v.Situation != Situation.Prelaunch) { Fail("vessel not on pad"); return; }
                            v.Control.Throttle = 1;
                            v.Control.SasEnabled = false;
                            fs.Stage();
                            Next(Phase.Ascent);
                        }
                        break;
                    }
                    case Phase.Ascent:
                    case Phase.Coast:
                    case Phase.Circularize:
                        Fly((FlightScene)game.CurrentScene);
                        break;
                    case Phase.Orbit:
                    {
                        var fs = (FlightScene)game.CurrentScene;
                        Vessel v = fs.World.ActiveVessel;
                        for (int i = 0; i < 5; i++) fs.World.StepPhysics(FlightWorld.PhysicsStep);
                        if (_phaseFrames == 30) Shot("06_orbit");
                        if (_phaseFrames == 31)
                        {
                            Orbit o = v.CurrentOrbit(fs.World.Time);
                            Log($"ORBIT: Ap {(o.ApoapsisRadius - v.Body.Radius) / 1000:F1} km, Pe {(o.PeriapsisRadius - v.Body.Radius) / 1000:F1} km, e {o.Eccentricity:F4}, period {o.Period:F0} s, situation {v.Situation}");
                            if (v.Situation != Situation.Orbiting) { Fail("vessel is not in a stable orbit"); return; }
                            // Time warp a full orbit on rails and check the orbit is unchanged.
                            double a0 = o.SemiMajorAxis;
                            fs.World.IncreaseWarp(); fs.World.IncreaseWarp(); fs.World.IncreaseWarp();
                            Log($"warp: {fs.World.Warp}");
                            for (int i = 0; i < 40; i++) fs.World.Update(0.1);
                            fs.World.StopWarp();
                            Orbit o2 = v.CurrentOrbit(fs.World.Time);
                            Log($"after warp: Ap {(o2.ApoapsisRadius - v.Body.Radius) / 1000:F1} km, Pe {(o2.PeriapsisRadius - v.Body.Radius) / 1000:F1} km, SMA change {o2.SemiMajorAxis - a0:F3} m");
                            if (System.Math.Abs(o2.SemiMajorAxis - a0) > 10) { Fail("orbit changed during time warp"); return; }
                            fs.ToggleMap();
                            Next(Phase.Map);
                        }
                        break;
                    }
                    case Phase.Map:
                    {
                        var fs = (FlightScene)game.CurrentScene;
                        if (_phaseFrames == 10)
                        {
                            // Plan a manoeuvre and check the predicted trajectory reacts.
                            Vessel v = fs.World.ActiveVessel;
                            Orbit o = v.CurrentOrbit(fs.World.Time);
                            v.Nodes.Add(new ManeuverNode { Time = fs.World.Time + o.TimeToPeriapsis(fs.World.Time), Prograde = 860, BurnBaseline = v.AccumulatedThrustDeltaV });
                            fs.Map.SelectedNode = v.Nodes[0];
                            fs.Map.Refresh();
                        }
                        if (_phaseFrames == 30)
                        {
                            var patches = fs.Map.Patches;
                            Log($"map: {patches.Count} predicted patches: " + string.Join(" → ", patches.Select(p => $"{p.Body.Name}/{p.End}")));
                            if (!patches.Any(p => p.IsPlanned)) { Fail("manoeuvre node did not produce a planned trajectory"); return; }
                            Shot("07_map");
                        }
                        if (_phaseFrames == 31)
                        {
                            fs.ToggleMap();
                            Next(Phase.SaveLoad);
                        }
                        break;
                    }
                    case Phase.SaveLoad:
                    {
                        var fs = (FlightScene)game.CurrentScene;
                        if (_phaseFrames == 2)
                        {
                            Vessel v = fs.World.ActiveVessel;
                            Vector3d pos = v.Position;
                            if (!game.SaveGame("smoke-test")) { Fail("save failed"); return; }
                            var warnings = game.LoadGame("smoke-test");
                            if (warnings.Count > 0) Log("load warnings: " + string.Join("; ", warnings));
                            Vessel r = game.World.ActiveVessel;
                            double err = (r.Position - pos).Length;
                            Log($"save/load: {game.World.Vessels.Count} vessels restored, active '{r.Name}', position error {err:E2} m");
                            if (err > 1e-3) { Fail("save/load changed the vessel position"); return; }
                            game.ResumeFlight();
                        }
                        if (_phaseFrames == 20)
                        {
                            if (game.CurrentScene is not FlightScene) { Fail("flight did not resume after load"); return; }
                            Shot("08_reloaded");
                            Log($"max altitude {_maxAlt / 1000:F1} km, frames {_frames}, {((FlightScene)game.CurrentScene).Diagnostics()}");
                            Finish();
                        }
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Fail(ex.ToString());
            }
        }

        /// <summary>Ascent guidance using only pilot inputs: a gravity turn to the east, coast, then circularise.</summary>
        private void Fly(FlightScene fs)
        {
            FlightWorld w = fs.World;
            Vessel v = w.ActiveVessel;
            for (int i = 0; i < _stepsPerFrame && _phase is Phase.Ascent or Phase.Coast or Phase.Circularize; i++)
            {
                w.StepPhysics(FlightWorld.PhysicsStep);
                v = w.ActiveVessel;
                if (v == null) { Fail("active vessel lost"); return; }
                Steer(w, v);
            }
            _maxAlt = System.Math.Max(_maxAlt, v.Altitude);
            if (_phaseFrames % 40 == 0)
            {
                Orbit o = v.CurrentOrbit(w.Time);
                Log($"{_phase} t+{w.Time - v.LaunchTime:F0}s alt {v.Altitude / 1000:F1} km, v {v.Velocity.Length:F0} m/s, Ap {(o.ApoapsisRadius - v.Body.Radius) / 1000:F1} km, Pe {(o.PeriapsisRadius - v.Body.Radius) / 1000:F1} km, stage {v.CurrentStage}, mass {v.Mass:F0}");
            }
            if (_phase == Phase.Ascent && _phaseFrames == 12) Shot("04_liftoff");
            if (_phase == Phase.Ascent && v.Altitude > 18000 && _shotAscent == false) { _shotAscent = true; Shot("05_ascent"); }
            if (w.Time - v.LaunchTime > 1500) Fail($"no orbit after 1500 s (alt {v.Altitude:F0} m)");
        }

        private bool _shotAscent;

        private void Steer(FlightWorld w, Vessel v)
        {
            Orbit o = v.CurrentOrbit(w.Time);
            Vector3d up = v.Position.Normalized();
            Vector3d east = Vector3d.Cross(Vector3d.UnitY, up).Normalized();
            Vector3d target;
            double apAlt = o.ApoapsisRadius - v.Body.Radius;
            const double targetAp = 82000;

            // Stage when the current stage is out of propellant.
            bool burning = v.Parts.Any(p => p.Def.GetModule<EngineModuleDef>() is { } e && p.State.Activated && !p.State.Shutdown && Propulsion.HasPropellant(v.Network, p, e));
            bool hasCapsuleOnly = !v.Parts.Any(p => p.Def.HasModule<EngineModuleDef>());
            if (!burning && v.Control.Throttle > 0 && !hasCapsuleOnly && v.CurrentStage < 2) w.ActivateNextStage(v);

            switch (_phase)
            {
                case Phase.Ascent:
                {
                    double alt = v.Altitude;
                    double tilt = alt < 900 ? 0 : System.Math.Min(80, 90 * System.Math.Pow((alt - 900) / 55000.0, 0.55));
                    double th = tilt * MathUtil.Deg2Rad;
                    target = (up * System.Math.Cos(th) + east * System.Math.Sin(th)).Normalized();
                    // Follow surface prograde once well into the turn to limit aerodynamic loads.
                    if (alt > 12000)
                    {
                        Vector3d pro = v.SurfaceVelocity.Normalized();
                        target = (target + pro).Normalized();
                    }
                    v.Control.Throttle = 1;
                    if (apAlt > targetAp)
                    {
                        v.Control.Throttle = 0;
                        Next(Phase.Coast);
                        Log($"MECO: Ap {apAlt / 1000:F1} km at alt {alt / 1000:F1} km");
                    }
                    break;
                }
                case Phase.Coast:
                {
                    target = v.Velocity.Normalized();
                    v.Control.Throttle = 0;
                    // Keep the apoapsis above the atmosphere while coasting through it.
                    if (apAlt < targetAp - 2000 && v.Altitude < 70000) v.Control.Throttle = 0.3;
                    double burn = FlightWorld.EstimateBurnTime(v, Orbit.CircularVelocity(v.Body.Mu, o.ApoapsisRadius) - Orbit.VisViva(v.Body.Mu, o.ApoapsisRadius, o.SemiMajorAxis));
                    if (double.IsNaN(burn) && v.CurrentStage < 2) { w.ActivateNextStage(v); burn = 60; }
                    if (v.Altitude > 70000 && o.TimeToApoapsis(w.Time) < burn / 2 + 3)
                    {
                        Next(Phase.Circularize);
                        Log($"circularisation burn ≈ {burn:F0} s");
                    }
                    break;
                }
                default:
                {
                    // Prograde, pitched slightly to hold the apoapsis in front.
                    double tta = o.TimeToApoapsis(w.Time);
                    double pitch = tta > o.Period / 2 ? 0.25 : 0; // past apoapsis: raise
                    target = (v.Velocity.Normalized() + up * pitch).Normalized();
                    v.Control.Throttle = 1;
                    double peAlt = o.PeriapsisRadius - v.Body.Radius;
                    if (peAlt > 72000)
                    {
                        v.Control.Throttle = 0;
                        w.UpdateSituation(v);
                        Log($"orbit achieved: Pe {peAlt / 1000:F1} km, Ap {apAlt / 1000:F1} km, propellant left {v.Network.Total(ResourceLibrary.Fuel):F0} kg");
                        v.Control.SasEnabled = true;
                        v.Control.SasMode = SasMode.Prograde;
                        Next(Phase.Orbit);
                    }
                    break;
                }
            }

            // Attitude: feed the SAS rate controller with our own target, as pilot input.
            Vector3d tgtLocal = v.Rotation.InverseRotate(target);
            Vector3d axis = Vector3d.Cross(Vector3d.UnitY, tgtLocal);
            double ang = System.Math.Atan2(axis.Length, Vector3d.Dot(Vector3d.UnitY, tgtLocal));
            Vector3d err = axis.LengthSquared > 1e-18 ? axis.Normalized() * ang : Vector3d.Zero;
            Vector3d omega = v.Rotation.InverseRotate(v.AngularVelocity - v.Body.AngularVelocityVector * 0);
            Vector3d authority = VesselPhysics.ControlAuthority(v, v.Telemetry.StaticPressure / 101325.0);
            Vector3d cmd = Autopilot.RateControl(err, omega, authority);
            v.Control.Pitch = cmd.X;
            v.Control.Roll = cmd.Y;
            v.Control.Yaw = cmd.Z;
        }
    }
}
