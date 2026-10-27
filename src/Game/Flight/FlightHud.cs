using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;
using OSP.Game.App;
using OSP.Game.UI;

namespace OSP.Game.Flight
{
    /// <summary>
    /// Flight instruments. Every number shown here is read from the simulation (vessel telemetry, orbit derived from
    /// the state vector, resource network, delta-v estimator). Nothing is hard-coded.
    /// </summary>
    public partial class FlightHud : Control
    {
        private FlightScene _scene;
        private FlightWorld World => _scene.World;

        // Top bar
        private Label _clock, _met, _warpLabel, _vesselLabel;
        // Surface/atmosphere panel
        private Label _alt, _radar, _vs, _hs, _mach, _q, _pressure, _g, _latlon;
        // Orbit panel
        private Label _orbitBody, _ap, _pe, _tap, _tpe, _period, _inc, _ecc, _event;
        // Navball area
        private Navball _navball;
        private Label _speed, _hdg;
        private Button _speedMode;
        private ProgressBar _throttle;
        private readonly Dictionary<SasMode, Button> _sasButtons = new();
        private Button _sasToggle, _rcsToggle, _gearToggle, _panelToggle, _precisionToggle;
        // Staging
        private VBoxContainer _stageList;
        private int _stageListVersion = -1, _stageListStage = -1;
        // Resources
        private readonly Dictionary<string, (ProgressBar bar, Label label)> _resources = new();
        private Label _mass, _thrust, _twr, _stageDv, _totalDv, _power;
        private double _dvTimer;
        // Messages
        private VBoxContainer _messages;
        // Science / recovery
        private VBoxContainer _sciencePanel;
        private Button _recoverButton;
        private double _scienceTimer;
        // Manoeuvre panel
        private PanelContainer _nodePanel;
        private Label _nodeInfo;
        private double _nodeStep = 10;
        // Overlays
        private Control _pauseMenu, _help, _dialog;
        private Label _status;

        public static FlightHud Create(FlightScene scene)
        {
            var h = new FlightHud { _scene = scene, Name = "FlightHud", Theme = Ui.Theme };
            Ui.FullRect(h);
            h.MouseFilter = MouseFilterEnum.Ignore;
            h.Build();
            return h;
        }

        private void Build()
        {
            // ---- Top bar -------------------------------------------------------------------------------
            var top = Ui.Panel();
            Ui.Anchor(top, 0.5f, 0, new Vector2(0, 6), GrowDirection.Both, GrowDirection.End);
            var tb = Ui.HBox(10);
            top.AddChild(tb);
            _vesselLabel = Ui.Label("", 15, Ui.Accent);
            _vesselLabel.CustomMinimumSize = new Vector2(150, 0);
            tb.AddChild(_vesselLabel);
            _clock = Ui.Label("", 14);
            tb.AddChild(_clock);
            _met = Ui.Label("", 13, Ui.TextDim);
            tb.AddChild(_met);
            tb.AddChild(Ui.Button("◀◀", () => World.DecreaseWarp(), "Decrease time warp (,)"));
            _warpLabel = Ui.Label("1×", 15, Ui.Warn);
            _warpLabel.CustomMinimumSize = new Vector2(70, 0);
            _warpLabel.HorizontalAlignment = HorizontalAlignment.Center;
            tb.AddChild(_warpLabel);
            tb.AddChild(Ui.Button("▶▶", () => World.IncreaseWarp(), "Increase time warp (.)"));
            tb.AddChild(Ui.Button("■", () => World.StopWarp(), "Stop warp (/)"));
            AddChild(top);

            // ---- Left: flight data -------------------------------------------------------------------
            var left = Ui.VBox(3);
            left.AddChild(Ui.Heading("FLIGHT"));
            _alt = Ui.Row(left, "Altitude");
            _radar = Ui.Row(left, "Above terrain");
            _vs = Ui.Row(left, "Vertical speed");
            _hs = Ui.Row(left, "Horiz. speed");
            _mach = Ui.Row(left, "Mach");
            _q = Ui.Row(left, "Dynamic press.");
            _pressure = Ui.Row(left, "Static press.");
            _g = Ui.Row(left, "Acceleration");
            _latlon = Ui.Row(left, "Lat / Lon");
            var leftPanel = Ui.Panel(left);
            Ui.Anchor(leftPanel, 0, 0, new Vector2(8, 50), GrowDirection.End, GrowDirection.End);
            leftPanel.CustomMinimumSize = new Vector2(255, 0);
            AddChild(leftPanel);

            // ---- Right: orbit data -------------------------------------------------------------------
            var right = Ui.VBox(3);
            right.AddChild(Ui.Heading("ORBIT"));
            _orbitBody = Ui.Row(right, "Body");
            _ap = Ui.Row(right, "Apoapsis");
            _tap = Ui.Row(right, "Time to Ap");
            _pe = Ui.Row(right, "Periapsis");
            _tpe = Ui.Row(right, "Time to Pe");
            _period = Ui.Row(right, "Period");
            _inc = Ui.Row(right, "Inclination");
            _ecc = Ui.Row(right, "Eccentricity");
            _event = Ui.Row(right, "Next event");
            var rightPanel = Ui.Panel(right);
            rightPanel.CustomMinimumSize = new Vector2(270, 0);
            Ui.Anchor(rightPanel, 1, 0, new Vector2(-8, 50), GrowDirection.Begin, GrowDirection.End);
            AddChild(rightPanel);

            // ---- Bottom centre: navball, throttle, SAS ---------------------------------------------------
            var bottom = Ui.VBox(4);
            var speedRow = Ui.HBox(8);
            _speedMode = Ui.Button("SURFACE", () => _scene.CycleSpeedReference(), "Click to switch the navball between surface, orbit and target speed");
            speedRow.AddChild(_speedMode);
            _speed = Ui.Label("0 m/s", 18, Ui.Good);
            speedRow.AddChild(_speed);
            bottom.AddChild(speedRow);
            var navRow = Ui.HBox(6);
            var thrBox = Ui.VBox(2);
            _throttle = new ProgressBar { MinValue = 0, MaxValue = 1, FillMode = (int)ProgressBar.FillModeEnum.BottomToTop, ShowPercentage = false, CustomMinimumSize = new Vector2(18, 170) };
            var fill = new StyleBoxFlat { BgColor = new Color(0.95f, 0.6f, 0.15f) };
            _throttle.AddThemeStyleboxOverride("fill", fill);
            thrBox.AddChild(Ui.Label("THR", 11, Ui.TextDim));
            thrBox.AddChild(_throttle);
            navRow.AddChild(thrBox);
            _navball = new Navball { BallSize = 190, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            navRow.AddChild(_navball);
            var sasCol = Ui.VBox(2);
            void Sas(SasMode m, string text, string tip)
            {
                Button b = Ui.Button(text, () => _scene.SetSasMode(m), tip, true);
                b.CustomMinimumSize = new Vector2(92, 0);
                _sasButtons[m] = b;
                sasCol.AddChild(b);
            }
            Sas(SasMode.Stability, "Hold", "Hold current attitude");
            Sas(SasMode.Prograde, "Prograde", "Point along velocity");
            Sas(SasMode.Retrograde, "Retrograde", "Point against velocity");
            Sas(SasMode.Normal, "Normal", "Orbit normal");
            Sas(SasMode.Antinormal, "Anti-normal", "Orbit anti-normal");
            Sas(SasMode.RadialOut, "Radial out", "Away from the body");
            Sas(SasMode.RadialIn, "Radial in", "Towards the body");
            Sas(SasMode.Maneuver, "Manoeuvre", "Point at the next manoeuvre node");
            Sas(SasMode.Target, "Target", "Point at the target");
            navRow.AddChild(sasCol);
            bottom.AddChild(navRow);
            _hdg = Ui.Label("", 13, Ui.TextDim, HorizontalAlignment.Center);
            bottom.AddChild(_hdg);
            var toggles = Ui.HBox(4);
            _sasToggle = Ui.Button("SAS", () => _scene.ToggleSas(), "Stability assist (T)", true);
            _rcsToggle = Ui.Button("RCS", () => _scene.ToggleRcs(), "Reaction control (R)", true);
            _gearToggle = Ui.Button("Legs", () => _scene.ToggleGear(), "Landing legs (G)", true);
            _panelToggle = Ui.Button("Panels", () => _scene.TogglePanels(), "Solar panels (U)", true);
            _precisionToggle = Ui.Button("Fine", () => _scene.TogglePrecision(), "Precision control (Caps Lock)", true);
            foreach (Button b in new[] { _sasToggle, _rcsToggle, _gearToggle, _panelToggle, _precisionToggle }) toggles.AddChild(b);
            toggles.AddChild(Ui.Button("Map", () => _scene.ToggleMap(), "Map view (M)"));
            bottom.AddChild(toggles);
            var bottomPanel = Ui.Panel(bottom);
            Ui.Anchor(bottomPanel, 0.5f, 1, new Vector2(0, -6), GrowDirection.Both, GrowDirection.Begin);
            AddChild(bottomPanel);

            // ---- Bottom left: staging -----------------------------------------------------------------
            var stageBox = Ui.VBox(3);
            var stageHead = Ui.HBox(6);
            stageHead.AddChild(Ui.Heading("STAGES"));
            stageHead.AddChild(Ui.Button("Stage ▶", () => _scene.Stage(), "Activate next stage (Space)"));
            stageBox.AddChild(stageHead);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(230, 260), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _stageList = Ui.VBox(4);
            _stageList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(_stageList);
            stageBox.AddChild(scroll);
            var stagePanel = Ui.Panel(stageBox);
            Ui.Anchor(stagePanel, 0, 1, new Vector2(8, -8), GrowDirection.End, GrowDirection.Begin);
            AddChild(stagePanel);

            // ---- Bottom right: resources -----------------------------------------------------------------
            var res = Ui.VBox(3);
            res.AddChild(Ui.Heading("VESSEL"));
            foreach (var (id, name, color) in new[]
                     {
                         (ResourceLibrary.Fuel, "Fuel", new Color(0.95f, 0.55f, 0.2f)),
                         (ResourceLibrary.Oxidizer, "Oxidizer", new Color(0.4f, 0.7f, 1.0f)),
                         (ResourceLibrary.SolidFuel, "Solid fuel", new Color(0.85f, 0.85f, 0.4f)),
                         (ResourceLibrary.Monopropellant, "Monoprop", new Color(0.9f, 0.9f, 0.9f)),
                         (ResourceLibrary.ElectricCharge, "Electric", new Color(0.4f, 0.95f, 0.5f)),
                     })
            {
                HBoxContainer row = Ui.HBox(6);
                Label n = Ui.Label(name, 13, Ui.TextDim);
                n.CustomMinimumSize = new Vector2(72, 0);
                ProgressBar bar = Ui.Bar(color);
                bar.CustomMinimumSize = new Vector2(80, 12);
                Label v = Ui.Label("", 12);
                row.AddChild(n);
                row.AddChild(bar);
                row.AddChild(v);
                res.AddChild(row);
                _resources[id] = (bar, v);
            }
            _mass = Ui.Row(res, "Mass", 90);
            _thrust = Ui.Row(res, "Thrust", 90);
            _twr = Ui.Row(res, "TWR", 90);
            _stageDv = Ui.Row(res, "Stage Δv", 90);
            _totalDv = Ui.Row(res, "Total Δv", 90);
            _power = Ui.Row(res, "Power", 90);
            var resPanel = Ui.Panel(res);
            resPanel.CustomMinimumSize = new Vector2(290, 0);
            Ui.Anchor(resPanel, 1, 1, new Vector2(-8, -8), GrowDirection.Begin, GrowDirection.Begin);
            AddChild(resPanel);

            // ---- Science / recovery panel (right, under orbit) --------------------------------------------
            var sciBox = Ui.VBox(3);
            sciBox.AddChild(Ui.Heading("SCIENCE"));
            _sciencePanel = Ui.VBox(3);
            sciBox.AddChild(_sciencePanel);
            _recoverButton = Ui.Button("Recover vessel", () => _scene.RecoverVessel(), "Recover the vessel, crew and data (landed on Aster)");
            _recoverButton.Visible = false;
            sciBox.AddChild(_recoverButton);
            var sciPanel = Ui.Panel(sciBox);
            sciPanel.CustomMinimumSize = new Vector2(270, 0);
            Ui.Anchor(sciPanel, 0, 0, new Vector2(8, 300), GrowDirection.End, GrowDirection.End);
            AddChild(sciPanel);

            // ---- Messages ---------------------------------------------------------------------------------
            _messages = Ui.VBox(2);
            _messages.CustomMinimumSize = new Vector2(600, 0);
            Ui.Anchor(_messages, 0.5f, 0, new Vector2(0, 90), GrowDirection.Both, GrowDirection.End);
            _messages.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_messages);

            _status = Ui.Label("", 13, Ui.Warn, HorizontalAlignment.Center);
            _status.CustomMinimumSize = new Vector2(600, 0);
            Ui.Anchor(_status, 0.5f, 0, new Vector2(0, 56), GrowDirection.Both, GrowDirection.End);
            AddChild(_status);

            BuildNodePanel();
        }

        // ---- Manoeuvre node editor ------------------------------------------------------------------------

        private void BuildNodePanel()
        {
            var box = Ui.VBox(4);
            box.AddChild(Ui.Heading("MANOEUVRE NODE"));
            _nodeInfo = Ui.Label("", 13);
            box.AddChild(_nodeInfo);
            var stepRow = Ui.HBox(4);
            stepRow.AddChild(Ui.Label("Step", 12, Ui.TextDim));
            foreach (double st in new[] { 0.1, 1, 10, 100 })
            {
                double s = st;
                stepRow.AddChild(Ui.Button($"{s:0.#}", () => _nodeStep = s, $"Change Δv by {s} m/s per click"));
            }
            box.AddChild(stepRow);
            void DvRow(string name, System.Action<double> apply)
            {
                HBoxContainer r = Ui.HBox(4);
                Label l = Ui.Label(name, 13, Ui.TextDim);
                l.CustomMinimumSize = new Vector2(80, 0);
                r.AddChild(l);
                r.AddChild(Ui.Button(" − ", () => { apply(-_nodeStep); _scene.Map.Refresh(); }));
                r.AddChild(Ui.Button(" + ", () => { apply(_nodeStep); _scene.Map.Refresh(); }));
                box.AddChild(r);
            }
            DvRow("Prograde", d => { if (_scene.Map.SelectedNode is { } n) n.Prograde += d; });
            DvRow("Normal", d => { if (_scene.Map.SelectedNode is { } n) n.Normal += d; });
            DvRow("Radial", d => { if (_scene.Map.SelectedNode is { } n) n.Radial += d; });
            var timeRow = Ui.HBox(3);
            timeRow.AddChild(Ui.Label("Time", 13, Ui.TextDim));
            foreach (var (label, dt) in new[] { ("−10m", -600.0), ("−1m", -60.0), ("−10s", -10.0), ("+10s", 10.0), ("+1m", 60.0), ("+10m", 600.0) })
            {
                double d = dt;
                timeRow.AddChild(Ui.Button(label, () =>
                {
                    if (_scene.Map.SelectedNode is { } n) n.Time = System.Math.Max(World.Time + 1, n.Time + d);
                    _scene.Map.Refresh();
                }));
            }
            box.AddChild(timeRow);
            var actions = Ui.HBox(4);
            actions.AddChild(Ui.Button("Next orbit", () =>
            {
                if (_scene.Map.SelectedNode is not { } n) return;
                Orbit o = World.ActiveVessel.CurrentOrbit(World.Time);
                if (o != null && o.IsElliptic) n.Time += o.Period;
                _scene.Map.Refresh();
            }));
            actions.AddChild(Ui.Button("Warp to", () => _scene.WarpToNode(), "Time-warp to 1 minute before the burn starts"));
            actions.AddChild(Ui.Button("Delete", () =>
            {
                if (_scene.Map.SelectedNode is { } n) World.ActiveVessel.Nodes.Remove(n);
                _scene.Map.SelectedNode = null;
                _scene.Map.Refresh();
            }));
            box.AddChild(actions);
            box.AddChild(Ui.Label("Click the orbit line in map view to create a node.", 12, Ui.TextDim));
            _nodePanel = Ui.Panel(box);
            Ui.Anchor(_nodePanel, 0, 0, new Vector2(290, 50), GrowDirection.End, GrowDirection.End);
            _nodePanel.Visible = false;
            AddChild(_nodePanel);
        }

        // ---- Update -------------------------------------------------------------------------------------------

        public void UpdateHud(double delta)
        {
            Vessel v = World.ActiveVessel;
            if (v == null) return;
            FlightTelemetry tm = v.Telemetry;
            double t = World.Time;

            _vesselLabel.Text = $"{v.Name}";
            _clock.Text = Ui.Date(t);
            _met.Text = v.Situation == Situation.Prelaunch ? "PRELAUNCH" : $"MET {Ui.Duration(t - v.LaunchTime)}";
            _warpLabel.Text = World.Paused ? "PAUSED" : World.Warp.ToString();

            _alt.Text = Ui.Distance(tm.AltitudeSeaLevel);
            _radar.Text = tm.AltitudeTerrain < 50000 ? Ui.Distance(tm.AltitudeTerrain) : "—";
            _vs.Text = Ui.Speed(tm.VerticalSpeed);
            _vs.AddThemeColorOverride("font_color", tm.VerticalSpeed < -5 && tm.AltitudeTerrain < 2000 ? Ui.Bad : Ui.Text);
            _hs.Text = Ui.Speed(tm.HorizontalSurfaceSpeed);
            _mach.Text = tm.Density > 0 ? $"{tm.Mach:0.00}" : "—";
            _q.Text = Ui.Pressure(tm.DynamicPressure);
            _pressure.Text = tm.StaticPressure > 0 ? $"{Ui.Pressure(tm.StaticPressure)} ({tm.StaticPressure / 101325:0.000} atm)" : "vacuum";
            _g.Text = $"{tm.GForce:0.00} g";
            _latlon.Text = $"{tm.Latitude:0.00}°, {tm.Longitude:0.00}°";

            // Orbit (derived from the actual state vector).
            Orbit o = v.CurrentOrbit(t);
            CelestialBody b = v.Body;
            _orbitBody.Text = $"{b.Name} · {SituationText(v.Situation)}";
            if (o != null && !v.Pinned)
            {
                _ap.Text = o.IsElliptic ? Ui.Distance(o.ApoapsisRadius - b.Radius) : "escape";
                _pe.Text = Ui.Distance(o.PeriapsisRadius - b.Radius);
                _pe.AddThemeColorOverride("font_color", o.PeriapsisRadius < b.Radius + b.AtmosphereHeight ? Ui.Warn : Ui.Text);
                _tap.Text = o.IsElliptic ? Ui.Duration(o.TimeToApoapsis(t)) : "—";
                _tpe.Text = Ui.Duration(o.TimeToPeriapsis(t));
                _period.Text = o.IsElliptic ? Ui.Duration(o.Period) : "—";
                _inc.Text = $"{o.Inclination * 180 / System.Math.PI:0.00}°";
                _ecc.Text = $"{o.Eccentricity:0.0000}";
            }
            else
            {
                foreach (Label l in new[] { _ap, _pe, _tap, _tpe, _period, _inc, _ecc }) l.Text = "—";
            }
            _event.Text = NextEventText(v);

            // Navball.
            Vector3d refVel = VesselPhysics.ReferenceVelocity(v, World.Environment(v));
            Vector3d? man = v.Nodes.Count > 0 ? Autopilot.ManeuverRemaining(v, t) : null;
            Vector3d? tgt = null;
            if (World.TryGetTargetState(v.Body, out Vector3d tp, out _)) tgt = (tp - v.Position).Normalized();
            _navball.UpdateFromVessel(v, refVel, man, tgt);
            _speed.Text = Ui.Speed(refVel.Length);
            _speedMode.Text = v.Control.SpeedReference.ToString().ToUpperInvariant();
            _hdg.Text = $"HDG {_navball.Heading:000}°   PITCH {_navball.PitchDeg:+00;-00}°   ROLL {_navball.RollDeg:+000;-000}°";
            _throttle.Value = v.Control.Throttle;
            foreach (var (mode, btn) in _sasButtons)
            {
                btn.SetPressedNoSignal(v.Control.SasEnabled && v.Control.SasMode == mode);
                btn.Disabled = !v.Control.SasEnabled;
            }
            _sasToggle.SetPressedNoSignal(v.Control.SasEnabled);
            _rcsToggle.SetPressedNoSignal(v.Control.RcsEnabled);
            _precisionToggle.SetPressedNoSignal(v.Control.PrecisionMode);
            _gearToggle.SetPressedNoSignal(v.Parts.Any(p => p.Def.HasModule<LandingLegModuleDef>() && p.State.DeployTarget));
            _panelToggle.SetPressedNoSignal(v.Parts.Any(p => p.Def.GetModule<SolarPanelModuleDef>() is { Deployable: true } && p.State.DeployTarget));

            // Resources.
            foreach (var (id, (bar, label)) in _resources)
            {
                double have = v.Network.Total(id), cap = v.Network.Capacity(id);
                bar.Value = cap > 0 ? have / cap : 0;
                label.Text = cap > 0 ? $"{have:N0} / {cap:N0}" : "—";
                bar.GetParent<Control>().Visible = cap > 0;
            }
            _mass.Text = Ui.Mass(v.Mass);
            _thrust.Text = $"{Ui.Force(tm.Thrust)} / {Ui.Force(tm.MaxThrust)}";
            _twr.Text = $"{tm.Twr:0.00}";
            _power.Text = $"+{tm.ElectricGeneration:0.00} / −{tm.ElectricConsumption:0.00} EC/s";
            _dvTimer -= delta;
            if (_dvTimer <= 0)
            {
                _dvTimer = 0.5;
                UpdateDeltaV(v);
            }

            UpdateStaging(v);
            _scienceTimer -= delta;
            if (_scienceTimer <= 0)
            {
                _scienceTimer = 0.5;
                UpdateScience(v);
            }
            UpdateNodePanel(v);
            UpdateMessages(delta);

            var status = new List<string>();
            if (!tm.HasControl) status.Add("NO CONTROL: no crew and no powered probe core");
            if (World.Warp.IsRails) status.Add($"TIME WARP {World.Warp.Rate:0}× (on rails)");
            if (tm.FlameOut && v.Control.Throttle > 0) status.Add("FLAME-OUT: engines out of propellant");
            if (World.ActiveVesselLost) status.Add("VESSEL DESTROYED — open the menu (Esc) to revert");
            _status.Text = string.Join("   ·   ", status);
        }

        private static string SituationText(Situation s) => s switch
        {
            Situation.Prelaunch => "on the pad",
            Situation.Landed => "landed",
            Situation.Splashed => "splashed down",
            Situation.Flying => "flying",
            Situation.SubOrbital => "sub-orbital",
            Situation.Orbiting => "orbiting",
            Situation.Escaping => "escaping",
            _ => "destroyed",
        };

        private string NextEventText(Vessel v)
        {
            if (v.Pinned) return "—";
            if (v.Nodes.Count > 0)
            {
                ManeuverNode n = v.Nodes[0];
                double dv = Autopilot.ManeuverRemaining(v, World.Time).Length;
                double burn = FlightWorld.EstimateBurnTime(v, dv);
                return $"Node in {Ui.Duration(n.Time - World.Time)} · {dv:0.0} m/s · burn {Ui.Duration(burn)}";
            }
            Orbit o = v.CurrentOrbit(World.Time);
            if (o == null) return "—";
            var tr = PatchedConics.FindTransition(o, v.Body, World.Time, World.Time + System.Math.Min(o.IsElliptic ? o.Period : 3e6, 3e7));
            if (tr.HasValue) return $"{(tr.Value.Kind == PatchEnd.Escape ? "Escape to" : "Encounter")} {tr.Value.NewBody.Name} in {Ui.Duration(tr.Value.Time - World.Time)}";
            if (o.PeriapsisRadius < v.Body.Radius)
            {
                double ti = o.NextTimeAtRadius(v.Body.Radius, World.Time, false);
                if (!double.IsNaN(ti)) return $"Surface in ≈{Ui.Duration(ti - World.Time)} (no drag)";
            }
            return "—";
        }

        private void UpdateDeltaV(Vessel v)
        {
            var active = new HashSet<int>(v.Parts.Where(p => p.Def.HasModule<EngineModuleDef>() && p.State.Activated && !p.State.Shutdown).Select(p => p.Id));
            List<StageStats> stats = DeltaVCalculator.Compute(v.Parts, v.CurrentStage, v.Body.SurfaceGravity, active);
            double pressure = v.Telemetry.StaticPressure / 101325.0;
            double StageDv(StageStats s) => s.DeltaVVacuum + (s.DeltaVSeaLevel - s.DeltaVVacuum) * System.Math.Clamp(pressure, 0, 1);
            StageStats cur = stats.FirstOrDefault();
            _stageDv.Text = cur != null ? $"{StageDv(cur):N0} m/s" : "—";
            _totalDv.Text = $"{stats.Sum(StageDv):N0} m/s";
        }

        private void UpdateStaging(Vessel v)
        {
            if (_stageListVersion == v.TopologyVersion && _stageListStage == v.CurrentStage) return;
            _stageListVersion = v.TopologyVersion;
            _stageListStage = v.CurrentStage;
            foreach (Node c in _stageList.GetChildren()) c.QueueFree();
            var stages = Staging.Stages(v.Parts);
            for (int s = stages.Count - 1; s >= 0; s--)
            {
                if (stages[s].Count == 0) continue;
                bool done = s < v.CurrentStage;
                bool next = s == v.CurrentStage;
                var box = Ui.VBox(1);
                Label head = Ui.Label($"Stage {s + 1}{(next ? "  ◀ next" : (done ? "  ✓" : ""))}", 13, next ? Ui.Accent : (done ? Ui.TextDim * 0.8f : Ui.Text));
                box.AddChild(head);
                foreach (var grp in stages[s].GroupBy(p => p.Def.Name))
                {
                    PartDefinition def = grp.First().Def;
                    Color c = def.HasModule<EngineModuleDef>() ? new Color(1.0f, 0.5f, 0.3f) : def.HasModule<DecouplerModuleDef>() ? new Color(1.0f, 0.85f, 0.3f) : def.HasModule<ParachuteModuleDef>() ? new Color(0.5f, 0.9f, 1.0f) : Ui.TextDim;
                    if (done) c *= 0.6f;
                    box.AddChild(Ui.Label($"   {grp.Count()}× {def.Name}", 12, c));
                }
                _stageList.AddChild(box);
            }
            if (stages.Count == 0 || v.CurrentStage >= stages.Count) _stageList.AddChild(Ui.Label("No stages remaining", 12, Ui.TextDim));
        }

        private void UpdateScience(Vessel v)
        {
            foreach (Node c in _sciencePanel.GetChildren()) c.QueueFree();
            foreach (PartInstance p in v.Parts)
            {
                ExperimentModuleDef exp = p.Def.GetModule<ExperimentModuleDef>();
                if (exp == null) continue;
                var def = Core.Science.ScienceRules.Get(exp.ExperimentId);
                if (def == null) continue;
                HBoxContainer row = Ui.HBox(4);
                bool stored = p.State.StoredScience.Count > 0;
                Label l = Ui.Label(def.Name + (stored ? " (data)" : ""), 12, stored ? Ui.Good : Ui.Text);
                l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(l);
                PartInstance part = p;
                if (!stored) row.AddChild(Ui.Button("Run", () => _scene.RunExperiment(part), "Run the experiment in the current situation"));
                else
                {
                    row.AddChild(Ui.Button("Send", () => _scene.TransmitScience(part), "Transmit for partial value (needs an antenna and power)"));
                    row.AddChild(Ui.Button("Discard", () => { part.State.StoredScience.Clear(); _scienceTimer = 0; }));
                }
                _sciencePanel.AddChild(row);
            }
            if (_sciencePanel.GetChildCount() == 0) _sciencePanel.AddChild(Ui.Label("No instruments on board", 12, Ui.TextDim));
            var mode = GameRoot.Instance.Progress;
            _sciencePanel.AddChild(Ui.Label(mode.Mode == Core.Science.GameMode.Sandbox ? "Sandbox: science is recorded but not spent" : $"Science: {mode.Science:0.0}   Funds: {mode.Funds:N0}", 12, Ui.TextDim));
            _recoverButton.Visible = _scene.CanRecover(v);
        }

        private void UpdateNodePanel(Vessel v)
        {
            bool show = _scene.MapOpen && _scene.Map.SelectedNode != null;
            _nodePanel.Visible = show;
            if (!show) return;
            ManeuverNode n = _scene.Map.SelectedNode;
            double burn = FlightWorld.EstimateBurnTime(v, n.DeltaVMagnitude);
            string result = "";
            var patches = _scene.Map.Patches;
            OrbitPatch after = patches.FirstOrDefault(p => p.IsPlanned);
            if (after != null)
            {
                Orbit o = after.Orbit;
                result = $"\nAfter: Ap {(o.IsElliptic ? Ui.Distance(o.ApoapsisRadius - after.Body.Radius) : "escape")} · Pe {Ui.Distance(o.PeriapsisRadius - after.Body.Radius)} ({after.Body.Name})";
                OrbitPatch enc = patches.FirstOrDefault(p => p.IsPlanned && p.End == PatchEnd.Encounter);
                if (enc != null) result += $"\nEncounter: {enc.NextBody.Name}";
            }
            _nodeInfo.Text = $"Prograde {n.Prograde:0.0} · Normal {n.Normal:0.0} · Radial {n.Radial:0.0} m/s\nΔv {n.DeltaVMagnitude:0.0} m/s · burn ≈ {Ui.Duration(burn)} · in {Ui.Duration(n.Time - World.Time)}{result}\nStep: {_nodeStep:0.#} m/s";
        }

        // ---- Messages --------------------------------------------------------------------------------------

        private readonly List<(Label label, double ttl)> _msgs = new();

        public void ShowMessage(string text, bool important)
        {
            Label l = Ui.Label(text, important ? 15 : 13, important ? Ui.Warn : Ui.Text, HorizontalAlignment.Center);
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            l.AddThemeConstantOverride("outline_size", 4);
            _messages.AddChild(l);
            _msgs.Add((l, important ? 7 : 4.5));
            while (_msgs.Count > 6)
            {
                _msgs[0].label.QueueFree();
                _msgs.RemoveAt(0);
            }
        }

        private void UpdateMessages(double delta)
        {
            for (int i = _msgs.Count - 1; i >= 0; i--)
            {
                var (l, ttl) = _msgs[i];
                ttl -= delta;
                if (ttl <= 0)
                {
                    l.QueueFree();
                    _msgs.RemoveAt(i);
                    continue;
                }
                l.Modulate = new Color(1, 1, 1, (float)System.Math.Min(1, ttl));
                _msgs[i] = (l, ttl);
            }
        }

        // ---- Overlays --------------------------------------------------------------------------------------

        public bool OverlayOpen => (_pauseMenu != null && _pauseMenu.Visible) || (_dialog != null && IsInstanceValid(_dialog));

        public void TogglePauseMenu()
        {
            if (_dialog != null && IsInstanceValid(_dialog))
            {
                _dialog.QueueFree();
                _dialog = null;
                return;
            }
            if (_pauseMenu == null) BuildPauseMenu();
            _pauseMenu.Visible = !_pauseMenu.Visible;
            World.Paused = _pauseMenu.Visible;
        }

        private void BuildPauseMenu()
        {
            var overlay = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
            Ui.FullRect(overlay);
            var box = Ui.VBox(8);
            box.AddChild(Ui.Label("PAUSED", 22, Ui.Accent, HorizontalAlignment.Center));
            box.AddChild(Ui.Button("Resume", TogglePauseMenu));
            box.AddChild(Ui.Button("Save game…", () => ShowSaveDialog()));
            box.AddChild(Ui.Button("Load game…", () => ShowLoadDialog()));
            box.AddChild(Ui.Button("Revert to launch", () => { World.Paused = false; GameRoot.Instance.RevertToLaunch(); }, "Restart this flight from the launch pad"));
            box.AddChild(Ui.Button("Return to vehicle assembly", () => { World.Paused = false; GameRoot.Instance.LeaveFlight(); }, "Vessels in flight keep their state in the persistent world"));
            box.AddChild(Ui.Button("Controls", () => ToggleHelp()));
            box.AddChild(Ui.Button("Settings…", () => ShowDialog(Menu.SettingsPanel.Create(() => { _dialog?.QueueFree(); _dialog = null; }))));
            box.AddChild(Ui.Button("Main menu", () => { World.Paused = false; GameRoot.Instance.GoToMenu(); }));
            var panel = Ui.Panel(box);
            panel.CustomMinimumSize = new Vector2(320, 0);
            var center = new CenterContainer();
            Ui.FullRect(center);
            center.AddChild(panel);
            overlay.AddChild(center);
            _pauseMenu = overlay;
            _pauseMenu.Visible = false;
            AddChild(_pauseMenu);
        }

        public void ShowDialog(Control content)
        {
            _dialog?.QueueFree();
            var overlay = new ColorRect { Color = new Color(0, 0, 0, 0.4f), MouseFilter = MouseFilterEnum.Stop };
            Ui.FullRect(overlay);
            var center = new CenterContainer();
            Ui.FullRect(center);
            center.AddChild(content);
            overlay.AddChild(center);
            _dialog = overlay;
            AddChild(overlay);
        }

        public void CloseDialog()
        {
            _dialog?.QueueFree();
            _dialog = null;
        }

        public void ShowInfo(string title, string body, System.Action onClose = null)
        {
            var box = Ui.VBox(8);
            box.AddChild(Ui.Label(title, 18, Ui.Accent));
            var text = new RichTextLabel { Text = body, FitContent = true, CustomMinimumSize = new Vector2(460, 0), ScrollActive = false };
            box.AddChild(text);
            box.AddChild(Ui.Button("OK", () => { CloseDialog(); onClose?.Invoke(); }));
            ShowDialog(Ui.Panel(box));
        }

        private void ShowSaveDialog()
        {
            var box = Ui.VBox(8);
            box.AddChild(Ui.Label("Save game", 18, Ui.Accent));
            var name = new LineEdit { Text = GameRoot.Instance.CurrentSaveName, CustomMinimumSize = new Vector2(320, 0) };
            box.AddChild(name);
            var row = Ui.HBox(6);
            row.AddChild(Ui.Button("Save", () =>
            {
                string n = string.Concat(name.Text.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
                if (n.Length == 0) n = "save";
                bool ok = GameRoot.Instance.SaveGame(n);
                CloseDialog();
                ShowMessage(ok ? $"Game saved as '{n}'." : "Saving failed — see the log.", !ok);
            }));
            row.AddChild(Ui.Button("Cancel", CloseDialog));
            box.AddChild(row);
            ShowDialog(Ui.Panel(box));
        }

        private void ShowLoadDialog()
        {
            var box = Ui.VBox(6);
            box.AddChild(Ui.Label("Load game", 18, Ui.Accent));
            var saves = GameRoot.Instance.ListSaves().ToList();
            if (saves.Count == 0) box.AddChild(Ui.Label("No saved games yet.", 13, Ui.TextDim));
            foreach (string s in saves)
            {
                string save = s;
                box.AddChild(Ui.Button(save, () => _scene.LoadSave(save)));
            }
            box.AddChild(Ui.Button("Cancel", CloseDialog));
            ShowDialog(Ui.Panel(box));
        }

        public void ToggleHelp()
        {
            if (_help != null)
            {
                _help.QueueFree();
                _help = null;
                return;
            }
            _help = Menu.ControlsReference.Create(() => { _help?.QueueFree(); _help = null; });
            AddChild(_help);
        }
    }
}
