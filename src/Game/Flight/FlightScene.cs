using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Science;
using OSP.Core.Vehicles;
using OSP.Game.App;
using OSP.Game.Map;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Flight
{
    /// <summary>
    /// The flight scene (scenes/Flight.tscn). Each frame it reads input into the active vessel's controls, advances
    /// the <see cref="FlightWorld"/> simulation, re-centres the floating origin on the active vessel and updates the
    /// near world (vessels, terrain, launch site), the scaled-space background, lighting and the HUD.
    /// </summary>
    public partial class FlightScene : Node3D
    {
        public FlightWorld World { get; private set; }
        public MapView Map { get; private set; }
        public bool MapOpen { get; private set; }
        public FlightHud Hud => _hud;
        /// <summary>When set (smoke test), the caller advances the simulation instead of real time.</summary>
        public bool ExternalStepping { get; set; }

        private GameRoot _game;
        private ScaledSpace _scaled;
        private TextureRect _background;
        private Godot.Environment _env;
        private DirectionalLight3D _sun;
        private FlightCamera _camera;
        private Node3D _near;
        private TerrainRenderer _terrain;
        private LaunchSite _site;
        private FlightHud _hud;
        private readonly Dictionary<Vessel, VesselView> _views = new();
        private double _contractTimer;
        private bool _uiHidden;
        private Vessel _cameraVessel;

        public override void _Ready()
        {
            _game = GameRoot.Instance;
            World = _game.World;
            World.Logged += OnLog;

            // Scaled space background, shown behind the near world through a canvas-background environment.
            _scaled = ScaledSpace.Create(World.SolarSystem);
            AddChild(_scaled);
            var bgLayer = new CanvasLayer { Layer = -10, Name = "Background" };
            _background = new TextureRect { Texture = _scaled.Viewport.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            Ui.FullRect(_background);
            bgLayer.AddChild(_background);
            AddChild(bgLayer);

            _env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Canvas,
                BackgroundCanvasMaxLayer = -1,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.3f, 0.35f, 0.45f),
                AmbientLightEnergy = 0.4f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 1.0f,
                GlowEnabled = _game.Settings.Quality != GraphicsQuality.Low,
                GlowIntensity = 0.7f,
                GlowBloom = 0.02f,
                GlowHdrThreshold = 1.2f,
                FogEnabled = true,
                FogDensity = 0.00002f,
                FogSkyAffect = 0.0f,
            };
            AddChild(new WorldEnvironment { Environment = _env });
            _sun = new DirectionalLight3D
            {
                Name = "Sun",
                LightEnergy = 1.6f,
                ShadowEnabled = _game.Settings.Shadows,
                DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
                DirectionalShadowMaxDistance = 400,
                ShadowBias = 0.05f,
                LightAngularDistance = 0.5f,
            };
            AddChild(_sun);

            _near = new Node3D { Name = "NearWorld" };
            AddChild(_near);
            _camera = new FlightCamera { Name = "FlightCamera", Sensitivity = (float)_game.Settings.MouseSensitivity };
            AddChild(_camera);
            _site = LaunchSite.Create(World.SolarSystem);
            _near.AddChild(_site);

            var uiLayer = new CanvasLayer { Layer = 1, Name = "UI" };
            AddChild(uiLayer);
            Map = MapView.Create(World, _scaled);
            uiLayer.AddChild(Map);
            _hud = FlightHud.Create(this);
            uiLayer.AddChild(_hud);

            Vessel active = World.ActiveVessel;
            _camera.ResetView(VesselSize(active));
            foreach (FlightLogEntry e in World.Log.TakeLast(3)) _hud.ShowMessage(e.Message, false);
            _hud.ShowMessage("Space: stage · Shift/Ctrl: throttle · WASD/QE: steer · T: SAS · M: map · F1: controls", false);
        }

        public override void _ExitTree()
        {
            if (World != null) World.Logged -= OnLog;
        }

        private void OnLog(FlightLogEntry e) => _hud?.ShowMessage(e.Message, e.Important);

        private static double VesselSize(Vessel v)
        {
            double r = 1;
            foreach (PartInstance p in v.Parts) r = System.Math.Max(r, (p.Position - v.CenterOfMassLocal).Length + p.Def.Bounds.Size.Length / 2);
            return r;
        }

        // ---- Input -------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (!MapOpen) _camera.HandleInput(ev);
            if (ev is InputEventKey { Echo: true }) return;
            Vessel v = World.ActiveVessel;
            if (ev.IsActionPressed("pause")) { _hud.TogglePauseMenu(); GetViewport().SetInputAsHandled(); return; }
            if (_hud.OverlayOpen) return;
            if (ev.IsActionPressed("toggle_help")) _hud.ToggleHelp();
            if (ev.IsActionPressed("toggle_ui")) { _uiHidden = !_uiHidden; _hud.Visible = !_uiHidden; }
            if (v == null) return;
            if (ev.IsActionPressed("stage")) Stage();
            if (ev.IsActionPressed("toggle_sas")) ToggleSas();
            if (ev.IsActionPressed("toggle_rcs")) ToggleRcs();
            if (ev.IsActionPressed("precision")) TogglePrecision();
            if (ev.IsActionPressed("toggle_gear")) ToggleGear();
            if (ev.IsActionPressed("toggle_panels")) TogglePanels();
            if (ev.IsActionPressed("throttle_full")) v.Control.Throttle = 1;
            if (ev.IsActionPressed("throttle_cut")) v.Control.Throttle = 0;
            if (ev.IsActionPressed("map_view")) ToggleMap();
            if (ev.IsActionPressed("camera_mode")) { _camera.CycleMode(); World.Message($"Camera: {_camera.Mode}"); }
            if (ev.IsActionPressed("camera_reset")) _camera.ResetView(VesselSize(v));
            if (ev.IsActionPressed("warp_up")) World.IncreaseWarp();
            if (ev.IsActionPressed("warp_down")) World.DecreaseWarp();
            if (ev.IsActionPressed("warp_stop")) { World.CancelWarpTo(); World.StopWarp(); }
            if (ev.IsActionPressed("vessel_next")) World.SwitchVessel(1);
            if (ev.IsActionPressed("vessel_prev")) World.SwitchVessel(-1);
            if (ev.IsActionPressed("quicksave"))
            {
                bool ok = _game.SaveGame("quicksave");
                _hud.ShowMessage(ok ? "Quicksaved." : "Quicksave failed.", !ok);
            }
            if (ev.IsActionPressed("quickload")) LoadSave("quicksave");
            if (MapOpen && ev is InputEventKey { Pressed: true, Keycode: Key.Tab }) Map.CycleFocus();
        }

        private void ReadContinuousInput(double delta)
        {
            Vessel v = World.ActiveVessel;
            if (v == null) return;
            VesselControl c = v.Control;
            bool typing = GetViewport().GuiGetFocusOwner() is LineEdit;
            if (typing || _hud.OverlayOpen)
            {
                c.Pitch = c.Yaw = c.Roll = 0;
                c.Translation = Vector3d.Zero;
                return;
            }
            // Vessel frame: nose +Y, right +X, dorsal ("top") +Z.
            //   pitch: + rotates the nose towards the dorsal side (S = pitch up)
            //   yaw:   + rotates the nose to the left (about +Z), so D (yaw right) is negative
            //   roll:  + rolls right (dorsal side moves towards +X)
            float Axis(string neg, string pos) => (Input.IsActionPressed(pos) ? 1f : 0f) - (Input.IsActionPressed(neg) ? 1f : 0f);
            double pitch = Axis("pitch_down", "pitch_up");
            if (_game.Settings.InvertPitch) pitch = -pitch;
            c.Pitch = pitch;
            c.Yaw = -Axis("yaw_left", "yaw_right");
            c.Roll = Axis("roll_left", "roll_right");
            double thr = Axis("throttle_down", "throttle_up");
            if (thr != 0) c.Throttle = MathUtil.Clamp01(c.Throttle + thr * delta * 0.6);
            // RCS translation: right = +X, forward = +Y (along the nose), up = +Z (dorsal).
            c.Translation = new Vector3d(Axis("translate_left", "translate_right"), Axis("translate_back", "translate_forward"), Axis("translate_down", "translate_up"));
        }

        // ---- Actions (also used by the HUD) ----------------------------------------------------------------------

        public void Stage()
        {
            if (World.ActiveVessel == null) return;
            World.ActivateNextStage(World.ActiveVessel);
        }

        public void ToggleSas()
        {
            VesselControl c = World.ActiveVessel.Control;
            c.SasEnabled = !c.SasEnabled;
            World.ActiveVessel.Autopilot.Reset();
        }

        public void SetSasMode(SasMode m)
        {
            VesselControl c = World.ActiveVessel.Control;
            c.SasEnabled = true;
            c.SasMode = m;
            World.ActiveVessel.Autopilot.Reset();
            if (m == SasMode.Maneuver && World.ActiveVessel.Nodes.Count == 0) World.Message("No manoeuvre node: create one in map view.");
            if (m == SasMode.Target && World.TargetBody == null && World.TargetVessel == null) World.Message("No target selected.");
        }

        public void ToggleRcs()
        {
            VesselControl c = World.ActiveVessel.Control;
            c.RcsEnabled = !c.RcsEnabled;
            if (c.RcsEnabled && !World.ActiveVessel.Parts.Any(p => p.Def.HasModule<RcsModuleDef>())) World.Message("RCS enabled, but this vessel has no RCS thrusters.");
        }

        public void TogglePrecision() => World.ActiveVessel.Control.PrecisionMode = !World.ActiveVessel.Control.PrecisionMode;

        public void ToggleGear()
        {
            var legs = World.ActiveVessel.Parts.Where(p => p.Def.HasModule<LandingLegModuleDef>()).ToList();
            if (legs.Count == 0) { World.Message("No landing legs."); return; }
            bool target = !legs.Any(p => p.State.DeployTarget);
            foreach (PartInstance p in legs) p.State.DeployTarget = target;
            World.Message(target ? "Landing legs deploying." : "Landing legs retracting.");
        }

        public void TogglePanels()
        {
            var panels = World.ActiveVessel.Parts.Where(p => p.Def.GetModule<SolarPanelModuleDef>() is { Deployable: true } && !p.State.Broken).ToList();
            if (panels.Count == 0) { World.Message("No deployable solar panels."); return; }
            bool target = !panels.Any(p => p.State.DeployTarget);
            foreach (PartInstance p in panels) p.State.DeployTarget = target;
            World.Message(target ? "Solar panels extending." : "Solar panels retracting.");
        }

        public void CycleSpeedReference()
        {
            VesselControl c = World.ActiveVessel.Control;
            c.AutoSpeedReference = false;
            c.SpeedReference = c.SpeedReference switch
            {
                SpeedReference.Surface => SpeedReference.Orbit,
                SpeedReference.Orbit => (World.TargetBody != null || World.TargetVessel != null) ? SpeedReference.Target : SpeedReference.Surface,
                _ => SpeedReference.Surface,
            };
        }

        public void ToggleMap()
        {
            MapOpen = !MapOpen;
            if (MapOpen)
            {
                Map.Open();
                _near.Visible = false;
            }
            else
            {
                Map.Close();
                _near.Visible = true;
            }
        }

        public void WarpToNode()
        {
            Vessel v = World.ActiveVessel;
            if (v.Nodes.Count == 0) return;
            ManeuverNode n = v.Nodes[0];
            double burn = FlightWorld.EstimateBurnTime(v, n.DeltaVMagnitude);
            double target = n.Time - (double.IsNaN(burn) ? 0 : burn / 2) - 60;
            if (target <= World.Time) { World.Message("The node is too close to warp to."); return; }
            World.WarpTo(target);
        }

        public void LoadSave(string name)
        {
            try
            {
                List<string> warnings = _game.LoadGame(name);
                foreach (string w in warnings) GD.PushWarning(w);
                _game.ResumeFlight();
            }
            catch (System.Exception ex) when (ex is System.FormatException or System.InvalidOperationException)
            {
                _hud.ShowInfo("Could not load the save", ex.Message);
            }
        }

        // ---- Science & recovery ---------------------------------------------------------------------------------

        public void RunExperiment(PartInstance part)
        {
            Vessel v = World.ActiveVessel;
            ExperimentModuleDef exp = part.Def.GetModule<ExperimentModuleDef>();
            ExperimentDefinition def = ScienceRules.Get(exp?.ExperimentId);
            if (def == null) return;
            if (def.RequiresCrew && !v.Parts.Any(p => p.State.Crew.Count > 0)) { World.Message("A crew report needs crew on board."); return; }
            ScienceResult r = ScienceRules.TryRun(v, def, World.Time, out string reason);
            if (r == null) { World.Message(reason); return; }
            part.State.StoredScience.Clear();
            part.State.StoredScience.Add($"{r.ExperimentId}|{r.BodyId}|{r.Situation}|{r.Biome}");
            double value = _game.Progress.RemainingValue(r, World.SolarSystem);
            World.Message($"{def.Name}: {ScienceRules.Describe(r.Situation)} at {v.Body.Name}{(r.Biome != null ? " (" + r.Biome + ")" : "")} — worth {value:0.0} science if recovered.", true);
            if (!_game.Progress.Flags.Contains("visited:" + v.Body.Id)) _game.Progress.Flags.Add("visited:" + v.Body.Id);
        }

        private static ScienceResult ParseResult(string s)
        {
            string[] f = s.Split('|');
            if (f.Length < 4 || !System.Enum.TryParse(f[2], out ScienceSituation sit)) return null;
            return new ScienceResult { ExperimentId = f[0], BodyId = f[1], Situation = sit, Biome = string.IsNullOrEmpty(f[3]) ? null : f[3] };
        }

        public void TransmitScience(PartInstance part)
        {
            Vessel v = World.ActiveVessel;
            AntennaModuleDef antenna = v.Parts.Select(p => p.Def.GetModule<AntennaModuleDef>()).Where(a => a != null).OrderByDescending(a => a.Range).FirstOrDefault();
            if (antenna == null) { World.Message("No antenna on board: recover the vessel to return the data."); return; }
            CelestialBody home = World.SolarSystem.HomeBody;
            double dist = (v.Body.AbsolutePosition(World.Time) + v.Position - home.AbsolutePosition(World.Time)).Length;
            if (dist > antenna.Range + home.Radius) { World.Message($"Out of antenna range ({Ui.Distance(antenna.Range)})."); return; }
            foreach (string s in part.State.StoredScience.ToList())
            {
                ScienceResult r = ParseResult(s);
                ExperimentDefinition def = ScienceRules.Get(r?.ExperimentId);
                if (def == null) continue;
                double ec = def.DataSize * antenna.ElectricPerData;
                if (v.Network.Total(ResourceLibrary.ElectricCharge) < ec) { World.Message($"Not enough electric charge to transmit ({ec:0} EC needed)."); return; }
                v.Network.Draw(v.Root, ResourceLibrary.ElectricCharge, ec);
                double gained = _game.Progress.Credit(r, World.SolarSystem, def.TransmitFraction);
                World.Message($"Transmitted {def.Name}: +{gained:0.0} science.", true);
            }
            part.State.StoredScience.Clear();
        }

        public bool CanRecover(Vessel v) =>
            v.Body == World.SolarSystem.HomeBody && (v.Situation is Situation.Landed or Situation.Splashed || (v.Situation == Situation.Prelaunch)) && v.SurfaceVelocity.Length < 1.0;

        public void RecoverVessel()
        {
            Vessel v = World.ActiveVessel;
            if (!CanRecover(v)) return;
            ProgressState prog = _game.Progress;
            CelestialBody home = World.SolarSystem.HomeBody;
            Vector3d site = CelestialBody.DirectionFromLatLon(World.SolarSystem.LaunchSite.Latitude, World.SolarSystem.LaunchSite.Longitude);
            double dist = Vector3d.Angle(home.InertialToBodyFixed(v.Position, World.Time), site) * home.Radius;
            double factor = CareerRules.RecoveryFactor(dist);
            double partValue = v.Parts.Sum(p => p.Def.Cost) * factor;
            double science = 0;
            foreach (PartInstance p in v.Parts)
            {
                foreach (string s in p.State.StoredScience)
                {
                    ScienceResult r = ParseResult(s);
                    if (r != null) science += prog.Credit(r, World.SolarSystem, 1.0);
                }
                p.State.StoredScience.Clear();
            }
            int crew = v.Parts.Sum(p => p.State.Crew.Count);
            if (prog.Mode == GameMode.Career) prog.Funds += partValue;
            var completed = new List<string>();
            foreach (string id in prog.AcceptedContracts.ToList())
            {
                ContractDefinition c = _game.Contracts.Contracts.FirstOrDefault(x => x.Id == id);
                if (c != null && ContractEvaluator.IsMet(c, v, World.SolarSystem, prog, recovered: true) && prog.Complete(c)) completed.Add(c.Title);
            }
            string summary = $"Recovered {v.Name} {Ui.Distance(dist)} from the launch site.\n" +
                             $"Mission time: {Ui.Duration(World.Time - v.LaunchTime)}\n" +
                             $"Crew returned: {crew}\n" +
                             $"Science recovered: {science:0.0}\n" +
                             (prog.Mode == GameMode.Career ? $"Funds refunded: {partValue:N0} ({factor:P0} of part value)\n" : "") +
                             (completed.Count > 0 ? "Contracts completed: " + string.Join(", ", completed) + "\n" : "");
            World.RemoveVessel(v);
            _hud.ShowInfo("Vessel recovered", summary, () =>
            {
                Vessel next = World.Vessels.FirstOrDefault(x => x.Type != VesselType.Debris);
                if (next != null && next.Loaded) World.SetActive(next);
                _game.LeaveFlight();
            });
            World.Paused = true;
        }

        private void CheckContracts(double delta)
        {
            _contractTimer -= delta;
            if (_contractTimer > 0) return;
            _contractTimer = 0.5;
            Vessel v = World.ActiveVessel;
            ProgressState prog = _game.Progress;
            string visited = "visited:" + v.Body.Id;
            if (v.Body != World.SolarSystem.HomeBody && !prog.Flags.Contains(visited)) prog.Flags.Add(visited);
            if (prog.Mode == GameMode.Sandbox) return;
            foreach (string id in prog.AcceptedContracts.ToList())
            {
                ContractDefinition c = _game.Contracts.Contracts.FirstOrDefault(x => x.Id == id);
                if (c == null || c.Objective == ObjectiveType.ReturnHome) continue;
                if (ContractEvaluator.IsMet(c, v, World.SolarSystem, prog) && prog.Complete(c))
                    World.Message($"Contract complete: {c.Title}! +{c.RewardFunds:N0} funds, +{c.RewardScience:0} science", true);
            }
        }

        // ---- Frame update ----------------------------------------------------------------------------------------

        public override void _Process(double delta)
        {
            if (World.ActiveVessel == null)
            {
                _hud.UpdateHud(delta);
                return;
            }
            if (!ExternalStepping)
            {
                ReadContinuousInput(delta);
                World.Update(delta);
            }
            World.RearmPorts();
            Vessel v = World.ActiveVessel;
            if (v == null) return;
            if (v.Control.AutoSpeedReference)
            {
                double switchAlt = v.Body.HasAtmosphere ? v.Body.AtmosphereHeight * 0.5 : v.Body.Radius * 0.05;
                v.Control.SpeedReference = v.Altitude < switchAlt ? SpeedReference.Surface : SpeedReference.Orbit;
            }
            CheckContracts(delta);
            _game.UniversalTime = World.Time;
            if (_cameraVessel != v)
            {
                _cameraVessel = v;
                _camera.ResetView(VesselSize(v));
            }

            UpdateNearWorld(delta);
            if (MapOpen) Map.UpdateMap(delta);
            _hud.UpdateHud(delta);
        }

        private void UpdateNearWorld(double delta)
        {
            Vessel v = World.ActiveVessel;
            double t = World.Time;
            CelestialBody body = v.Body;
            Vector3d originAbs = body.AbsolutePosition(t) + v.Position;
            Vector3d bodyOffset = -v.Position; // body centre relative to the origin

            _camera.UpdateCamera(v, delta);

            // Vessels.
            foreach (Vessel other in World.Vessels)
            {
                bool show = other.Loaded && other.Body == body && (other.Position - v.Position).Length < FlightWorld.LoadDistance * 1.2;
                if (show && !_views.ContainsKey(other))
                {
                    VesselView view = VesselView.Create(other);
                    _near.AddChild(view);
                    _views[other] = view;
                }
            }
            foreach (Vessel gone in _views.Keys.Where(x => !World.Vessels.Contains(x) || !x.Loaded || x.Body != body).ToList())
            {
                _views[gone].QueueFree();
                _views.Remove(gone);
            }
            double pressureAtm = body.Atmosphere?.PressureAtm(v.Altitude) ?? 0;
            foreach (var (vessel, view) in _views)
                view.Sync(vessel.Position - v.Position, (float)delta, pressureAtm);

            // Terrain for the current body.
            if (_terrain == null || _terrain.Body != body)
            {
                _terrain?.QueueFree();
                _terrain = body.IsStar ? null : TerrainRenderer.Create(body);
                if (_terrain != null) _near.AddChild(_terrain);
            }
            QuaternionD bodyRot = body.RotationAt(t);
            if (_terrain != null)
            {
                Vector3d camBodyFixed = bodyRot.InverseRotate(v.Position + _camera.Offset);
                _terrain.Visible = v.Altitude < body.Radius * 6;
                if (_terrain.Visible) _terrain.UpdateTerrain(camBodyFixed, bodyOffset, bodyRot, _camera.Far);
            }
            _site.Visible = body == _site.Body && v.Altitude < 200000;
            if (_site.Visible) _site.Sync(bodyOffset, bodyRot);

            // Lighting: the star's direction, eclipsed by the current body.
            Vector3d starRel = World.SolarSystem.Root.AbsolutePosition(t) - originAbs;
            Vector3d sunDir = starRel.Normalized();
            bool shadow = VesselPhysics.InShadow(v.Position, sunDir, body.Radius * 0.995);
            _sun.Basis = Basis.LookingAt(-sunDir.ToGodot(), System.Math.Abs(sunDir.Y) > 0.99 ? Vector3.Right : Vector3.Up);
            _sun.LightEnergy = shadow ? 0f : 1.6f;
            _sun.DirectionalShadowMaxDistance = (float)System.Math.Clamp(_camera.Distance * 6, 60, 600);

            // Atmosphere: ambient sky light, fog and star visibility.
            double sunElevation = Vector3d.Dot(v.Position.Normalized(), sunDir);
            float day = (float)MathUtil.Clamp01((sunElevation + 0.12) / 0.3);
            if (body.Atmosphere != null && v.Altitude < body.AtmosphereHeight)
            {
                double frac = body.Atmosphere.Pressure(v.Altitude) / body.Atmosphere.SeaLevelPressure;
                Color sky = body.Atmosphere.SkyColor.ToColor();
                _env.AmbientLightColor = sky.Lerp(new Color(0.25f, 0.27f, 0.32f), 0.4f);
                _env.AmbientLightEnergy = 0.25f + 0.55f * day * (float)System.Math.Sqrt(frac);
                _env.FogEnabled = true;
                _env.FogLightColor = sky.Lerp(Colors.White, 0.35f) * (0.2f + 0.8f * day);
                _env.FogDensity = (float)(0.000035 * frac);
                _scaled.SetSkyBrightness((float)(System.Math.Pow(frac, 0.25) * day));
            }
            else
            {
                _env.AmbientLightColor = new Color(0.32f, 0.34f, 0.4f);
                _env.AmbientLightEnergy = 0.12f;
                _env.FogEnabled = false;
                _scaled.SetSkyBrightness(0);
            }

            if (!MapOpen)
            {
                _scaled.Sync(t, originAbs, _camera.Offset, _camera.WorldBasis, _camera.Fov);
            }
            Vector2I size = (Vector2I)GetViewport().GetVisibleRect().Size;
            Vector2I px = DisplayServer.WindowGetSize();
            _scaled.Resize(new Vector2I(System.Math.Max(64, (int)(px.X * _game.Settings.RenderScale)), System.Math.Max(64, (int)(px.Y * _game.Settings.RenderScale))));
            _ = size;
        }

        /// <summary>Diagnostic counters for the debug overlay and smoke test.</summary>
        public string Diagnostics() => $"terrain chunks {_terrain?.ActiveChunks ?? 0} (pending {_terrain?.PendingChunks ?? 0}), vessels {World.Vessels.Count}, views {_views.Count}";
    }
}
