using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Game.UI;

namespace OSP.Game.Flight;

/// <summary>
/// Map view: a scaled 3D view of the whole system (1 unit = 1 km, positions made camera-relative in double precision) showing bodies,
/// their orbits, vessels, the predicted patched-conic trajectory, apsides, sphere-of-influence transitions and manoeuvre nodes.
/// </summary>
public sealed partial class FlightScene
{
    private bool _mapMode;
    private Node3D _mapRoot = null!;
    private Camera3D _mapCam = null!;
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _lineMesh = new();
    private readonly Dictionary<string, MeshInstance3D> _mapBodies = new();
    private readonly Dictionary<string, Label3D> _mapBodyLabels = new();
    private readonly Dictionary<int, (MeshInstance3D icon, Label3D label)> _mapCrafts = new();
    private readonly List<(MeshInstance3D m, Label3D l)> _markerPool = new();
    private double _mYaw = 0.6, _mPitch = 0.7, _mDist = 3000;       // km
    private bool _mOrbiting;
    private object? _mapFocus;       // CelestialBody or Craft; null = active craft
    private object? _mapSelected;
    private ManeuverNode? _selNode;
    private bool _placingNode;
    private Control _mapPanel = null!, _nodePanel = null!;
    private Label _mapInfo = null!, _nodeInfo = null!, _mapHint = null!;
    private readonly List<Vector2> _pickPoints = new();
    private readonly List<(double t, Vec3d posRoot)> _orbitPick = new();

    private static readonly Color BodyOrbitColor = new(0.45f, 0.55f, 0.7f, 0.7f);

    private void BuildMapUi()
    {
        _mapRoot = new Node3D { Name = "Map", Visible = false };
        AddChild(_mapRoot);
        _mapCam = new Camera3D { Fov = 55 };
        _mapRoot.AddChild(_mapCam);
        _lines = new MeshInstance3D { Mesh = _lineMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f };
        _lines.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = false };
        _mapRoot.AddChild(_lines);

        foreach (var b in System.Bodies)
        {
            var c = new Color((float)b.Color[0], (float)b.Color[1], (float)b.Color[2]);
            var mi = new MeshInstance3D { Mesh = Rendering.MeshFactory.Sphere(1.0, 32), MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _mapRoot.AddChild(mi); _mapBodies[b.Id] = mi;
            var lbl = MakeLabel(b.Name, new Color(0.85f, 0.92f, 1f));
            _mapRoot.AddChild(lbl); _mapBodyLabels[b.Id] = lbl;
        }

        // ---- UI: info panel (left), node editor, hint ----
        var info = Ui.VBox(6);
        info.AddChild(Ui.Label("MAP VIEW", 15, Ui.Accent));
        _mapInfo = Ui.Label("", 13); info.AddChild(_mapInfo);
        var focusRow = Ui.HBox(4);
        focusRow.AddChild(Ui.Button("Focus", () => { _mapFocus = _mapSelected; }, "Center the map on the selected object", 70));
        focusRow.AddChild(Ui.Button("Target", SetTargetFromSelection, "Make the selected body/vessel the navigation target", 70));
        focusRow.AddChild(Ui.Button("Vessel", () => { _mapFocus = null; _mapSelected = Sim.Active; }, "Back to the active vessel", 70));
        info.AddChild(focusRow);
        info.AddChild(Ui.Label("Maneuver nodes", 14, Ui.Accent));
        var addRow = Ui.HBox(4);
        addRow.AddChild(Ui.Button("+ at Ap", () => AddNodeAt(1), "Add a node at the next apoapsis", 70));
        addRow.AddChild(Ui.Button("+ at Pe", () => AddNodeAt(2), "Add a node at the next periapsis", 70));
        addRow.AddChild(Ui.Button("+ click", () => { _placingNode = true; _mapHint.Text = "Click on the orbit line to place the node"; }, "Click on the orbit to place a node", 70));
        info.AddChild(addRow);
        _mapPanel = Ui.Panel(info, 6);
        _mapPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _mapPanel.GrowVertical = Control.GrowDirection.Begin;
        _mapPanel.OffsetLeft = 10; _mapPanel.OffsetBottom = -44; _mapPanel.Visible = false;
        _hudRoot.AddChild(_mapPanel);

        var nodeV = Ui.VBox(4);
        _nodeInfo = Ui.Label("", 13); nodeV.AddChild(_nodeInfo);
        void Row(string label, Func<ManeuverNode, double> get, Action<ManeuverNode, double> set)
        {
            var r = Ui.HBox(2);
            var l = Ui.Label(label, 13, Ui.Dim); l.CustomMinimumSize = new Vector2(70, 0); r.AddChild(l);
            foreach (var d in new[] { -100.0, -10.0, -1.0, 1.0, 10.0, 100.0 })
            {
                var dd = d;
                r.AddChild(Ui.Button(dd > 0 ? $"+{dd:F0}" : $"{dd:F0}", () => { if (_selNode != null) { set(_selNode, get(_selNode) + dd); NodeChanged(); } }, null, 38));
            }
            nodeV.AddChild(r);
        }
        Row("Prograde", n => n.Prograde, (n, v) => n.Prograde = v);
        Row("Normal", n => n.Normal, (n, v) => n.Normal = v);
        Row("Radial", n => n.Radial, (n, v) => n.Radial = v);
        var tr = Ui.HBox(2);
        var tl = Ui.Label("Time", 13, Ui.Dim); tl.CustomMinimumSize = new Vector2(70, 0); tr.AddChild(tl);
        foreach (var (lab, dtv) in new[] { ("−10m", -600.0), ("−1m", -60.0), ("−10s", -10.0), ("+10s", 10.0), ("+1m", 60.0), ("+10m", 600.0) })
        { var dd = dtv; tr.AddChild(Ui.Button(lab, () => { if (_selNode != null) { _selNode.Time = Math.Max(Sim.Time + 1, _selNode.Time + dd); NodeChanged(); } }, null, 44)); }
        nodeV.AddChild(tr);
        var br = Ui.HBox(4);
        br.AddChild(Ui.Button("Zero", () => { if (_selNode != null) { _selNode.Prograde = _selNode.Normal = _selNode.Radial = 0; NodeChanged(); } }));
        br.AddChild(Ui.Button("Delete node", () => { if (_selNode != null && Sim.Active != null) { Sim.RemoveNode(Sim.Active, _selNode); _selNode = Sim.NodesFor(Sim.Active).FirstOrDefault(); NodeChanged(); } }));
        br.AddChild(Ui.Button("Burn here (MAN)", () => SetSasMode(SasMode.Maneuver), "Point SAS at the node", 0));
        nodeV.AddChild(br);
        _nodePanel = Ui.Panel(nodeV, 6);
        _nodePanel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        _nodePanel.GrowHorizontal = Control.GrowDirection.Begin;
        _nodePanel.OffsetRight = -10; _nodePanel.OffsetTop = 120; _nodePanel.Visible = false;
        _hudRoot.AddChild(_nodePanel);

        _mapHint = Ui.Label("", 14, Ui.Warn);
        var hp = Ui.Panel(_mapHint, 4);
        hp.SetAnchorsPreset(Control.LayoutPreset.CenterTop); hp.GrowHorizontal = Control.GrowDirection.Both; hp.OffsetTop = 56; hp.Name = "MapHint";
        hp.Visible = false;
        _hudRoot.AddChild(hp);
    }

    private static Label3D MakeLabel(string text, Color c) =>
        new() { Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0011f, FontSize = 30, OutlineSize = 6, Modulate = c, NoDepthTest = true, OutlineModulate = new Color(0, 0, 0, 0.9f) };

    private void ToggleMap()
    {
        _mapMode = !_mapMode;
        _mapRoot.Visible = _mapMode;
        _world.Visible = !_mapMode;
        _mapCam.Current = _mapMode;
        _cam.Current = !_mapMode;
        _mapPanel.Visible = _mapMode;
        _hudRoot.GetNode<Control>("MapHint").Visible = _mapMode;
        if (_mapMode)
        {
            _mapFocus = null; _mapSelected = Sim.Active;
            _mDist = Math.Max(500, (Sim.Active?.Body.Radius ?? 600000) / 1000.0 * 4);
            var envPitch = OS.GetEnvironment("OSP_MAPPITCH"); if (envPitch != "") _mPitch = double.Parse(envPitch);
            _selNode = Sim.Active != null ? Sim.NodesFor(Sim.Active).FirstOrDefault() : null;
            _patchAge = 99;
            _mapHint.Text = "Right-drag orbit · wheel zoom · click a body/vessel to select · Focus centres the view";
        }
        else { _nodePanel.Visible = false; }
    }

    // ------------------------------------------------------------------------------------------------------------
    private Vec3d RootPos(Craft c, double t)
    {
        var (b, _) = System.StateInRoot(c.Body, t);
        return b + c.Position;
    }

    private Vec3d FocusPos(double t)
    {
        switch (_mapFocus)
        {
            case CelestialBody b: return System.StateInRoot(b, t).r;
            case Craft c when !c.IsDestroyed: return RootPos(c, t);
        }
        return Sim.Active != null ? RootPos(Sim.Active, t) : Vec3d.Zero;
    }

    private static Vector3 V3(Vec3d v) => new((float)(v.X / 1000.0), (float)(v.Y / 1000.0), (float)(v.Z / 1000.0));

    private void UpdateMap(double dt)
    {
        var a = Sim.Active; if (a == null) return;
        double t = Sim.Time;
        var focus = FocusPos(t);

        _mDist = Math.Clamp(_mDist, 0.05, 3.0e8);
        _mPitch = Math.Clamp(_mPitch, -1.5, 1.5);
        var off = new Vector3((float)(Math.Cos(_mPitch) * Math.Sin(_mYaw)), (float)Math.Sin(_mPitch), (float)(Math.Cos(_mPitch) * Math.Cos(_mYaw))) * (float)_mDist;
        _mapCam.Position = off;
        _mapCam.LookAt(Vector3.Zero, Vector3.Up);
        _mapCam.Near = (float)Math.Max(0.001, _mDist * 0.002);
        _mapCam.Far = (float)(_mDist * 4000 + 1e8);

        // bodies
        foreach (var b in System.Bodies)
        {
            var (r, _) = System.StateInRoot(b, t);
            var p = V3(r - focus);
            double minR = _mDist * 0.0035;
            float rad = (float)Math.Max(b.Radius / 1000.0, minR);
            var mi = _mapBodies[b.Id];
            mi.Position = p; mi.Scale = new Vector3(rad, rad, rad);
            var lbl = _mapBodyLabels[b.Id];
            lbl.Position = p + new Vector3(0, rad * 1.2f, 0);
            lbl.Modulate = _mapSelected == b ? Ui.Accent : (_targetBody == b ? new Color(1f, 0.35f, 0.85f) : new Color(0.85f, 0.92f, 1f));
        }

        // vessels
        var seen = new HashSet<int>();
        foreach (var c in Sim.Crafts)
        {
            seen.Add(c.Id);
            if (!_mapCrafts.TryGetValue(c.Id, out var pair))
            {
                var mi = new MeshInstance3D { Mesh = Rendering.MeshFactory.Box(Vector3.One), MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.7f, 0.2f), NoDepthTest = true }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                var lbl = MakeLabel(c.Name, new Color(1f, 0.85f, 0.4f));
                _mapRoot.AddChild(mi); _mapRoot.AddChild(lbl);
                _mapCrafts[c.Id] = pair = (mi, lbl);
            }
            var p = V3(RootPos(c, t) - focus);
            float s = (float)(_mDist * 0.006);
            pair.icon.Position = p; pair.icon.Scale = new Vector3(s, s, s);
            pair.icon.Visible = c.HasCommand || c == a || _mDist < 20000;
            pair.label.Position = p + new Vector3(0, s * 1.6f, 0);
            pair.label.Text = c.Name; pair.label.Visible = pair.icon.Visible && (c.HasCommand || c == a);
            ((StandardMaterial3D)pair.icon.MaterialOverride!).AlbedoColor = c == a ? new Color(1f, 0.9f, 0.2f) : (c == _targetCraft ? new Color(1f, 0.35f, 0.85f) : new Color(0.95f, 0.55f, 0.15f));
        }
        foreach (var id in _mapCrafts.Keys.ToList())
            if (!seen.Contains(id)) { _mapCrafts[id].icon.QueueFree(); _mapCrafts[id].label.QueueFree(); _mapCrafts.Remove(id); }

        // lines
        _lineMesh.ClearSurfaces();
        foreach (var b in System.Bodies.Where(b => b.Parent != null))
            DrawBodyOrbit(b, focus, t);
        DrawSoi(a.Body, focus, t);
        int mk = 0;
        _orbitPick.Clear();
        _patchAge += dt;
        if (_patchAge > 0.25 && a.Mode != CraftMode.Landed) { _patches = Sim.PredictFor(a, 6, 3.0e7); _patchAge = 0; }
        for (int i = 0; i < _patches.Count; i++)
        {
            var patch = _patches[i];
            var color = i == 0 ? new Color(0.4f, 1f, 0.5f, 0.95f) : (patch.End == PatchEnd.Impact ? new Color(1f, 0.35f, 0.3f, 0.95f) : new Color(1f, 0.65f, 0.2f, 0.95f));
            if (patch.StartTime > t + 1 && patch.Body != _patches[0].Body && i > 0 && _patches[i - 1].EndManeuver != null) color = new Color(0.4f, 0.8f, 1f, 0.95f);
            // Patches in the vessel's current SoI are drawn about the body's present position; later bodies about where they will be at the patch start.
            var (bRoot, _) = System.StateInRoot(patch.Body, patch.Body == _patches[0].Body ? t : patch.StartTime);
            var pts = patch.Sample(360);
            var basePos = bRoot - focus;
            _lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
            for (int k = 0; k < pts.Count; k++)
            {
                _lineMesh.SurfaceSetColor(color);
                _lineMesh.SurfaceAddVertex(V3(basePos + pts[k]));
            }
            _lineMesh.SurfaceEnd();
            // picking data (first patch only: node placement)
            if (i == 0)
            {
                var o = patch.Orbit;
                for (int k = 0; k < pts.Count; k++)
                {
                    double tk = patch.StartTime + (patch.EndTime - patch.StartTime) * k / (pts.Count - 1);
                    if (o.IsElliptic && patch.EndTime - patch.StartTime > o.Period) tk = patch.StartTime + o.Period * k / (pts.Count - 1);
                    _orbitPick.Add((tk, basePos + pts[k]));
                }
                if (o.IsElliptic)
                {
                    var tp = t + o.TimeToPeriapsis(t); var ta = t + o.TimeToApoapsis(t);
                    Marker(ref mk, V3(basePos + o.StateAt(tp).r), "Pe", new Color(0.5f, 1f, 0.6f));
                    Marker(ref mk, V3(basePos + o.StateAt(ta).r), "Ap", new Color(0.5f, 1f, 0.6f));
                }
            }
            if (patch.End == PatchEnd.SoiEntry || patch.End == PatchEnd.SoiExit)
                Marker(ref mk, V3(basePos + patch.EndPosition), patch.End == PatchEnd.SoiEntry ? "→ " + patch.NextBody!.Name : "→ " + patch.Body.Parent!.Name, new Color(1f, 1f, 0.5f));
            if (patch.End == PatchEnd.Impact) Marker(ref mk, V3(basePos + patch.EndPosition), "Impact", new Color(1f, 0.35f, 0.3f));
            if (patch.End == PatchEnd.Maneuver && patch.EndManeuver != null)
                Marker(ref mk, V3(basePos + patch.EndPosition), $"Δv {patch.EndManeuver.DeltaVMagnitude:F0} m/s", new Color(0.4f, 0.6f, 1f));
        }
        for (; mk < _markerPool.Count; mk++) { _markerPool[mk].m.Visible = false; _markerPool[mk].l.Visible = false; }

        // info text
        UpdateMapInfo(a);
        UpdateNodePanel(a);
    }

    private void Marker(ref int idx, Vector3 pos, string text, Color c)
    {
        while (_markerPool.Count <= idx)
        {
            var m = new MeshInstance3D { Mesh = Rendering.MeshFactory.Sphere(1.0, 10), MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var l = MakeLabel("", Colors.White);
            _mapRoot.AddChild(m); _mapRoot.AddChild(l);
            _markerPool.Add((m, l));
        }
        var (mi, lb) = _markerPool[idx++];
        float s = (float)(_mDist * 0.0045);
        mi.Visible = true; mi.Position = pos; mi.Scale = new Vector3(s, s, s);
        ((StandardMaterial3D)mi.MaterialOverride!).AlbedoColor = c;
        lb.Visible = true; lb.Position = pos + new Vector3(0, s * 1.8f, 0); lb.Text = text; lb.Modulate = c;
    }

    private void DrawBodyOrbit(CelestialBody b, Vec3d focus, double t)
    {
        var o = b.Orbit!;
        var (pr, _) = System.StateInRoot(b.Parent!, t);
        var basePos = pr - focus;
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        const int N = 180;
        for (int i = 0; i <= N; i++)
        {
            var p = o.StateAtTrueAnomaly(MathUtil.Tau * i / N).r;
            _lineMesh.SurfaceSetColor(_targetBody == b ? new Color(1f, 0.35f, 0.85f, 0.9f) : BodyOrbitColor);
            _lineMesh.SurfaceAddVertex(V3(basePos + p));
        }
        _lineMesh.SurfaceEnd();
    }

    private void DrawSoi(CelestialBody b, Vec3d focus, double t)
    {
        if (b.Parent == null) return;
        var (r, _) = System.StateInRoot(b, t);
        var c = r - focus;
        double R = b.SphereOfInfluence;
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        for (int i = 0; i <= 96; i++)
        {
            double a = MathUtil.Tau * i / 96;
            _lineMesh.SurfaceSetColor(new Color(0.5f, 0.5f, 0.2f, 0.45f));
            _lineMesh.SurfaceAddVertex(V3(c + new Vec3d(Math.Cos(a) * R, 0, Math.Sin(a) * R)));
        }
        _lineMesh.SurfaceEnd();
    }

    private void UpdateMapInfo(Craft a)
    {
        var sb = new StringBuilder();
        switch (_mapSelected)
        {
            case CelestialBody b:
                sb.AppendLine(b.Name + $" ({b.Kind})");
                sb.AppendLine($"Radius {Ui.FormatDistance(b.Radius)}   GM {b.Mu:E3}");
                sb.AppendLine($"Surface gravity {b.SurfaceGravity:F2} m/s²   Escape {Ui.FormatSpeed(b.EscapeVelocityAtSurface)}");
                if (b.Parent != null) sb.AppendLine($"SoI {Ui.FormatDistance(b.SphereOfInfluence)}   Orbit: a={Ui.FormatDistance(b.Orbit!.SemiMajorAxis)} e={b.Orbit.Eccentricity:F3} i={b.Orbit.Inclination * MathUtil.Rad2Deg:F1}° T={Ui.FormatTime(b.Orbit.Period)}");
                if (b.Atmosphere != null) sb.AppendLine($"Atmosphere to {Ui.FormatDistance(b.Atmosphere.Height)}, {b.Atmosphere.SeaLevelPressure / 1000:F1} kPa at surface");
                else sb.AppendLine("No atmosphere");
                if (_targetBody == b) sb.AppendLine("★ current target");
                break;
            case Craft c:
                var o = Orbit.FromState(c.Position, c.Velocity, c.Body.Mu, Sim.Time);
                sb.AppendLine(c.Name + (c == a ? " (active)" : ""));
                sb.AppendLine($"Around {c.Body.Name}: " + (o.IsElliptic ? $"Ap {Ui.FormatDistance(o.ApoapsisRadius - c.Body.Radius)}  Pe {Ui.FormatDistance(o.PeriapsisRadius - c.Body.Radius)}" : $"escape, Pe {Ui.FormatDistance(o.PeriapsisRadius - c.Body.Radius)}"));
                sb.AppendLine($"Mass {Ui.FormatMass(c.TotalMass)} · {c.Parts.Count} parts");
                break;
            default: sb.AppendLine("Click a body or vessel."); break;
        }
        _mapInfo.Text = sb.ToString().TrimEnd();
    }

    private void UpdateNodePanel(Craft a)
    {
        var nodes = Sim.NodesFor(a);
        if (_selNode != null && !nodes.Contains(_selNode)) _selNode = nodes.FirstOrDefault();
        _nodePanel.Visible = _selNode != null;
        if (_selNode == null) return;
        var n = _selNode;
        var (bt, ok) = ManeuverPlanner.EstimateBurnTime(a, n.DeltaVMagnitude);
        var after = _patches.FirstOrDefault(p => p.StartTime > n.Time - 1) ?? _patches.LastOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine($"MANEUVER NODE   in {Ui.FormatTime(n.Time - Sim.Time)}  (UT {Ui.FormatTime(n.Time)})");
        sb.AppendLine($"Δv {n.DeltaVMagnitude:F1} m/s  (pro {n.Prograde:F0} · norm {n.Normal:F0} · rad {n.Radial:F0})");
        sb.Append(ok ? $"Burn time ≈ {bt:F1} s with the current stage" : "No engine ready: burn time unknown");
        if (after != null && after.Orbit.IsElliptic) sb.Append($"\nResulting orbit: Ap {Ui.FormatDistance(after.Orbit.ApoapsisRadius - after.Body.Radius)}  Pe {Ui.FormatDistance(after.Orbit.PeriapsisRadius - after.Body.Radius)}  ({after.Body.Name})");
        _nodeInfo.Text = sb.ToString();
    }

    private void NodeChanged() { _patchAge = 99; }

    private void AddNodeAt(int where)
    {
        var a = Sim.Active; if (a == null || a.Mode == CraftMode.Landed) return;
        var o = Orbit.FromState(a.Position, a.Velocity, a.Body.Mu, Sim.Time);
        double t = where == 1 ? (o.IsElliptic ? o.TimeToApoapsis(Sim.Time) : 120) : o.TimeToPeriapsis(Sim.Time);
        if (double.IsNaN(t) || t < 5) t = 60;
        _selNode = Sim.AddNode(a, Sim.Time + t);
        NodeChanged();
    }

    private void SetTargetFromSelection()
    {
        switch (_mapSelected)
        {
            case CelestialBody b: _targetBody = b; _targetCraft = null; Log("Target: " + b.Name); break;
            case Craft c when c != Sim.Active: _targetCraft = c; _targetBody = null; Log("Target: " + c.Name); break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    private void MapMouse(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right) _mOrbiting = mb.Pressed;
        else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _mDist *= 0.88;
        else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _mDist *= 1.14;
        else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            if (GetViewport().GuiGetHoveredControl() != null) return;
            if (_placingNode) { PlaceNodeAtClick(mb.Position); return; }
            PickMapObject(mb.Position);
        }
    }

    private void MapMotion(InputEventMouseMotion mm)
    {
        if (_mOrbiting) { var s = GameState.Instance.Settings.MouseSensitivity; _mYaw -= mm.Relative.X * 0.006 * s; _mPitch += mm.Relative.Y * 0.006 * s; }
    }

    private void PickMapObject(Vector2 mouse)
    {
        object? best = null; double bestD = 28;
        foreach (var b in System.Bodies)
        {
            var w = _mapBodies[b.Id].GlobalPosition;
            if (_mapCam.IsPositionBehind(w)) continue;
            double d = _mapCam.UnprojectPosition(w).DistanceTo(mouse);
            if (d < bestD) { bestD = d; best = b; }
        }
        foreach (var c in Sim.Crafts)
        {
            if (!_mapCrafts.TryGetValue(c.Id, out var pair) || !pair.icon.Visible) continue;
            var w = pair.icon.GlobalPosition;
            if (_mapCam.IsPositionBehind(w)) continue;
            double d = _mapCam.UnprojectPosition(w).DistanceTo(mouse);
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best != null) _mapSelected = best;
    }

    private void PlaceNodeAtClick(Vector2 mouse)
    {
        var a = Sim.Active; if (a == null) return;
        double bestD = 40; double bestT = -1;
        foreach (var (tk, pos) in _orbitPick)
        {
            var w = V3(pos);
            if (_mapCam.IsPositionBehind(w)) continue;
            double d = _mapCam.UnprojectPosition(w).DistanceTo(mouse);
            if (d < bestD) { bestD = d; bestT = tk; }
        }
        _placingNode = false;
        if (bestT < 0) { _mapHint.Text = "No orbit line under the cursor."; return; }
        _selNode = Sim.AddNode(a, Math.Max(Sim.Time + 5, bestT));
        NodeChanged();
        _mapHint.Text = "Node placed: adjust it with the panel on the right.";
    }
}
