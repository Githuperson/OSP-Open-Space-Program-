using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Map
{
    /// <summary>
    /// Interactive map: orbits of all bodies, the active vessel's predicted patched-conic trajectory (including
    /// planned manoeuvres and SOI changes), other vessels, and markers for apsides, encounters and nodes.
    /// Uses the scaled-space world with its own camera; the map's floating origin is the focused object.
    /// Controls: right-drag rotate, wheel zoom, click a label to focus, Tab to cycle, click the orbit to add a node.
    /// </summary>
    public partial class MapView : Control
    {
        private FlightWorld _world;
        private ScaledSpace _scaled;
        private ImmediateMesh _lines;
        private MeshInstance3D _lineInstance;
        private StandardMaterial3D _lineMaterial;

        private CelestialBody _focusBody;
        private Vessel _focusVessel;
        private double _distance = 3000; // scaled units (km)
        private double _yaw = 0.4, _pitch = 0.6;
        private bool _dragging;
        private List<OrbitPatch> _patches = new();
        private double _predictTimer;
        private readonly List<(Vector2 screen, string label, Color color, object target)> _labels = new();
        private readonly List<(Vector2 screen, double time)> _orbitSamples = new();

        public ManeuverNode SelectedNode { get; set; }
        public IReadOnlyList<OrbitPatch> Patches => _patches;
        public event System.Action<ManeuverNode> NodeCreated;

        public static MapView Create(FlightWorld world, ScaledSpace scaled)
        {
            var m = new MapView { _world = world, _scaled = scaled, Name = "MapView", Visible = false };
            Ui.FullRect(m);
            m.MouseFilter = MouseFilterEnum.Pass;
            m._lines = new ImmediateMesh();
            m._lineMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = false,
            };
            m._lineInstance = new MeshInstance3D { Mesh = m._lines, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            scaled.Root.AddChild(m._lineInstance);
            return m;
        }

        public void Open()
        {
            Visible = true;
            _lineInstance.Visible = true;
            _focusVessel = _world.ActiveVessel;
            _focusBody = null;
            double r = _world.ActiveVessel.Body.Radius / ScaledSpace.UnitMeters;
            _distance = System.Math.Max(r * 4, 50);
            _predictTimer = 0;
        }

        public void Close()
        {
            Visible = false;
            _lineInstance.Visible = false;
        }

        public void SetFocus(CelestialBody b)
        {
            _focusBody = b;
            _focusVessel = null;
            _distance = System.Math.Max(b.Radius / ScaledSpace.UnitMeters * 4, 40);
        }

        public void SetFocus(Vessel v)
        {
            _focusVessel = v;
            _focusBody = null;
        }

        public void CycleFocus()
        {
            var list = new List<object> { _world.ActiveVessel };
            list.AddRange(_world.SolarSystem.Bodies);
            object cur = (object)_focusVessel ?? _focusBody;
            int i = list.IndexOf(cur);
            object next = list[(i + 1) % list.Count];
            if (next is Vessel v) SetFocus(v); else SetFocus((CelestialBody)next);
        }

        private Vector3d FocusAbsolute()
        {
            double t = _world.Time;
            if (_focusVessel != null && _world.Vessels.Contains(_focusVessel)) return _focusVessel.Body.AbsolutePosition(t) + _focusVessel.Position;
            if (_focusBody != null) return _focusBody.AbsolutePosition(t);
            Vessel a = _world.ActiveVessel;
            return a.Body.AbsolutePosition(t) + a.Position;
        }

        public override void _GuiInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Right) { _dragging = mb.Pressed; AcceptEvent(); }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { _distance = System.Math.Max(0.05, _distance * 0.85); AcceptEvent(); }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { _distance = System.Math.Min(5e7, _distance * 1.18); AcceptEvent(); }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    if (HandleClick(mb.Position)) AcceptEvent();
                }
            }
            else if (ev is InputEventMouseMotion mm && _dragging)
            {
                _yaw -= mm.Relative.X * 0.006;
                _pitch = MathUtil.Clamp(_pitch + mm.Relative.Y * 0.006, -1.55, 1.55);
                AcceptEvent();
            }
        }

        private bool HandleClick(Vector2 pos)
        {
            foreach (var (screen, label, _, target) in _labels)
            {
                if (screen.DistanceTo(pos) > 14) continue;
                if (target is CelestialBody b) { SetFocus(b); return true; }
                if (target is Vessel v) { SetFocus(v); return true; }
                if (target is ManeuverNode n) { SelectedNode = n; return true; }
            }
            // Click near the current orbit: add a manoeuvre node there.
            double bestD = 12, bestT = double.NaN;
            foreach (var (screen, time) in _orbitSamples)
            {
                float d = screen.DistanceTo(pos);
                if (d < bestD) { bestD = d; bestT = time; }
            }
            if (!double.IsNaN(bestT) && bestT > _world.Time + 1)
            {
                Vessel a = _world.ActiveVessel;
                var node = new ManeuverNode { Time = bestT, BurnBaseline = a.AccumulatedThrustDeltaV };
                a.Nodes.Add(node);
                a.Nodes.Sort((x, y) => x.Time.CompareTo(y.Time));
                SelectedNode = node;
                _predictTimer = 0;
                NodeCreated?.Invoke(node);
                return true;
            }
            return false;
        }

        public void Refresh() => _predictTimer = 0;

        /// <summary>Per-frame update while the map is open.</summary>
        public void UpdateMap(double delta)
        {
            if (!Visible) return;
            Vessel active = _world.ActiveVessel;
            if (active == null) return;
            _predictTimer -= delta;
            if (_predictTimer <= 0)
            {
                _patches = _world.PredictTrajectory(active, 6);
                _predictTimer = 0.25;
            }
            if (SelectedNode != null && !active.Nodes.Contains(SelectedNode)) SelectedNode = null;

            Vector3d origin = FocusAbsolute();
            // Camera orbiting the focus in the ecliptic frame.
            Vector3 offset = new Vector3((float)(System.Math.Sin(_yaw) * System.Math.Cos(_pitch)), (float)System.Math.Sin(_pitch), (float)(System.Math.Cos(_yaw) * System.Math.Cos(_pitch))) * (float)_distance;
            Camera3D cam = _scaled.Camera;
            cam.Transform = new Transform3D(Basis.LookingAt(-offset.Normalized(), Vector3.Up), offset);
            cam.Fov = 55;
            // Bounded depth range around the zoom distance; anything farther is compressed by scaled space.
            cam.Near = (float)System.Math.Max(0.002, _distance * 2e-4);
            cam.Far = (float)(_distance * 150 + 10);
            _scaled.Compress = cam.Far * 0.6;
            _scaled.SyncBodies(_world.Time, origin, offset);

            DrawLines(origin);
            QueueRedraw();
        }

        private Vector3 S(Vector3d absolute, Vector3d origin) => ((absolute - origin) / ScaledSpace.UnitMeters).ToGodot();

        private void DrawLines(Vector3d origin)
        {
            double t = _world.Time;
            _lines.ClearSurfaces();
            _labels.Clear();
            _orbitSamples.Clear();
            _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, _lineMaterial);
            bool any = false;

            void Segment(Vector3 a, Vector3 b, Color c)
            {
                _lines.SurfaceSetColor(c);
                _lines.SurfaceAddVertex(a);
                _lines.SurfaceSetColor(c);
                _lines.SurfaceAddVertex(b);
                any = true;
            }

            // Celestial body orbits (relative to their parent's current position).
            foreach (CelestialBody b in _world.SolarSystem.Bodies)
            {
                Vector3d bodyAbs = b.AbsolutePosition(t);
                AddLabel(S(bodyAbs, origin), b.Name, b.Color.ToColor().Lightened(0.3f), b);
                if (b.Parent == null) continue;
                Vector3d parentAbs = b.Parent.AbsolutePosition(t);
                Orbit o = b.Orbit.Rebased(t);
                Color col = b.Color.ToColor().Lightened(0.2f);
                col.A = 0.55f;
                Vector3 prev = default;
                int n = 160;
                for (int i = 0; i <= n; i++)
                {
                    Vector3 p = S(parentAbs + o.PositionAtTrueAnomaly(i * Mathf.Tau / n), origin);
                    if (i > 0) Segment(prev, p, col);
                    prev = p;
                }
                // SOI sphere outline (equatorial circle) for moons/planets when close.
                if (b.SphereOfInfluence < 1e12)
                {
                    Color soi = col;
                    soi.A = 0.18f;
                    for (int i = 0; i < 64; i++)
                    {
                        double a0 = i * MathUtil.TwoPi / 64, a1 = (i + 1) * MathUtil.TwoPi / 64;
                        Vector3d d0 = new(System.Math.Cos(a0), 0, System.Math.Sin(a0));
                        Vector3d d1 = new(System.Math.Cos(a1), 0, System.Math.Sin(a1));
                        Segment(S(bodyAbs + d0 * b.SphereOfInfluence, origin), S(bodyAbs + d1 * b.SphereOfInfluence, origin), soi);
                    }
                }
            }

            // Other vessels.
            foreach (Vessel v in _world.Vessels)
            {
                Vector3d abs = v.Body.AbsolutePosition(t) + v.Position;
                bool active = v == _world.ActiveVessel;
                AddLabel(S(abs, origin), v.Name, active ? Ui.Accent : (v.Type == VesselType.Debris ? Ui.TextDim * 0.8f : Ui.TextDim), v);
                if (active || v.Pinned) continue;
                Orbit o = v.CurrentOrbit(t);
                if (o == null || !o.IsElliptic) continue;
                Vector3d bodyAbs = v.Body.AbsolutePosition(t);
                Color col = new(0.6f, 0.6f, 0.65f, 0.35f);
                Vector3 prev = default;
                for (int i = 0; i <= 96; i++)
                {
                    Vector3 p = S(bodyAbs + o.PositionAtTrueAnomaly(i * Mathf.Tau / 96), origin);
                    if (i > 0) Segment(prev, p, col);
                    prev = p;
                }
            }

            // Active vessel trajectory.
            Vessel a = _world.ActiveVessel;
            Color[] palette = { new(0.3f, 0.85f, 1.0f), new(0.55f, 1.0f, 0.5f), new(1.0f, 0.9f, 0.35f), new(1.0f, 0.6f, 0.9f), new(0.8f, 0.8f, 1.0f), new(1.0f, 1.0f, 1.0f) };
            for (int pi = 0; pi < _patches.Count; pi++)
            {
                OrbitPatch patch = _patches[pi];
                Vector3d bodyAbs = patch.Body.AbsolutePosition(t);
                Color col = patch.IsPlanned ? new Color(1.0f, 0.55f, 0.15f) : palette[System.Math.Min(pi, palette.Length - 1)];
                Orbit o = patch.Orbit;
                bool fullEllipse = o.IsElliptic && patch.End == PatchEnd.None && patch.EndTime - patch.StartTime >= o.Period * 0.999;
                int n = 220;
                Vector3 prev = default;
                for (int i = 0; i <= n; i++)
                {
                    double time = patch.StartTime + (patch.EndTime - patch.StartTime) * i / n;
                    Vector3d rel = fullEllipse ? o.PositionAtTrueAnomaly(o.TrueAnomalyAt(patch.StartTime) + i * MathUtil.TwoPi / n) : o.PositionAt(time);
                    Vector3 p = S(bodyAbs + rel, origin);
                    if (i > 0) Segment(prev, p, col);
                    prev = p;
                    if (pi == 0 && !patch.IsPlanned)
                    {
                        double sampleTime = fullEllipse ? o.NextTimeAtTrueAnomaly(MathUtil.WrapTwoPi(o.TrueAnomalyAt(patch.StartTime) + i * MathUtil.TwoPi / n), t) : time;
                        Vector2 sp = ToScreen(p);
                        if (sp.X > -1e5f) _orbitSamples.Add((sp, sampleTime));
                    }
                }
                // Apsides for closed patches.
                if (o.IsElliptic && patch.Body.Radius > 0)
                {
                    double tPe = o.NextTimeAtTrueAnomaly(0, patch.StartTime);
                    double tAp = o.NextTimeAtTrueAnomaly(System.Math.PI, patch.StartTime);
                    if (tAp <= patch.EndTime + 1)
                        AddLabel(S(bodyAbs + o.PositionAtTrueAnomaly(System.Math.PI), origin), $"Ap {Ui.Distance(o.ApoapsisRadius - patch.Body.Radius)}", col, null);
                    if (tPe <= patch.EndTime + 1 || fullEllipse)
                        AddLabel(S(bodyAbs + o.PositionAtTrueAnomaly(0), origin), $"Pe {Ui.Distance(o.PeriapsisRadius - patch.Body.Radius)}", col, null);
                }
                else if (!o.IsElliptic && o.PeriapsisRadius > 0 && pi > 0)
                {
                    if (o.NextTimeAtTrueAnomaly(0, patch.StartTime) is var tp && !double.IsNaN(tp) && tp < patch.EndTime)
                        AddLabel(S(bodyAbs + o.PositionAtTrueAnomaly(0), origin), $"Pe {Ui.Distance(o.PeriapsisRadius - patch.Body.Radius)}", col, null);
                }
                string endLabel = patch.End switch
                {
                    PatchEnd.Encounter => $"{patch.NextBody?.Name} encounter",
                    PatchEnd.Escape => $"Escape {patch.Body.Name}",
                    PatchEnd.Impact => "Impact",
                    _ => null,
                };
                if (endLabel != null)
                    AddLabel(S(bodyAbs + o.PositionAt(patch.EndTime), origin), endLabel + " · " + Ui.Duration(patch.EndTime - t), col, null);
                if (!double.IsNaN(patch.AtmosphereEntryTime) && patch.AtmosphereEntryTime < patch.EndTime)
                    AddLabel(S(bodyAbs + o.PositionAt(patch.AtmosphereEntryTime), origin), "Atmosphere", new Color(0.5f, 0.7f, 1f), null);
            }

            // Manoeuvre nodes.
            foreach (ManeuverNode node in a.Nodes)
            {
                OrbitPatch patch = _patches.FirstOrDefault(p => node.Time >= p.StartTime - 1e-6 && node.Time <= p.EndTime + 1e-6);
                if (patch == null) continue;
                Vector3d abs = patch.Body.AbsolutePosition(t) + patch.Orbit.PositionAt(node.Time);
                AddLabel(S(abs, origin), node == SelectedNode ? "◆ Node (selected)" : "◆ Node", new Color(0.35f, 0.6f, 1f), node);
            }

            if (!any) Segment(Vector3.Zero, Vector3.Zero, Colors.Transparent);
            _lines.SurfaceEnd();
        }

        private Vector2 ToScreen(Vector3 scaledPos)
        {
            Camera3D cam = _scaled.Camera;
            if (cam.IsPositionBehind(scaledPos)) return new Vector2(-1e6f, -1e6f);
            Vector2 p = cam.UnprojectPosition(scaledPos);
            Vector2 vpSize = _scaled.Viewport.Size;
            return p * (Size / vpSize);
        }

        private void AddLabel(Vector3 scaledPos, string text, Color color, object target)
        {
            Vector2 s = ToScreen(scaledPos);
            if (s.X < -1e5f) return;
            _labels.Add((s, text, color, target));
        }

        public override void _Draw()
        {
            Font font = GetThemeDefaultFont();
            foreach (var (screen, label, color, target) in _labels)
            {
                if (screen.X < -50 || screen.Y < -50 || screen.X > Size.X + 50 || screen.Y > Size.Y + 50) continue;
                float r = target is CelestialBody ? 5 : 4;
                if (target is Vessel) DrawRect(new Rect2(screen - new Vector2(4, 4), new Vector2(8, 8)), color, false, 2);
                else if (target is ManeuverNode) DrawCircle(screen, 6, color);
                else if (target is CelestialBody) DrawCircle(screen, r, color);
                else DrawArc(screen, 4, 0, Mathf.Tau, 12, color, 2);
                DrawString(font, screen + new Vector2(9, 5), label, HorizontalAlignment.Left, -1, 13, color);
            }
            string focus = _focusVessel?.Name ?? _focusBody?.Name ?? "";
            DrawString(font, new Vector2(Size.X / 2 - 200, Size.Y - 18), $"MAP · focus: {focus} · right-drag rotate · wheel zoom · Tab cycle focus · click orbit to add node · M to exit",
                HorizontalAlignment.Center, 400 * 2, 13, Ui.TextDim);
        }
    }
}
