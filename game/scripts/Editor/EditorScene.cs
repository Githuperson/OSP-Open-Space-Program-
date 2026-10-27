using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using OSP.Game.Data;
using OSP.Game.Rendering;
using OSP.Game.UI;
using Json = OSP.Core.Util.Json;

namespace OSP.Game.Editor;

/// <summary>
/// The Vehicle Assembly Building: a 3D construction workspace. Parts are placed by clicking: stack nodes snap by screen
/// proximity, radial parts snap to the surface of the cylinder under the cursor. All connection rules, symmetry, staging
/// and statistics come from OSP.Core (the same code the flight simulation uses).
/// </summary>
public sealed partial class EditorScene : Node3D
{
    // ----- data -----
    private PartCatalog _cat = null!;
    private VehicleEditor _ed = null!;
    private VehicleDesign _design = new();
    private readonly List<string> _undo = new(), _redo = new();

    // ----- held part -----
    private VehicleDesign? _held;
    private string? _heldLabel;
    private double _heldRoll;
    private int _flip;
    private int _symmetry = 1;
    private AttachCandidate? _cand;
    private List<(Vec3d pos, QuatD rot)> _candPoses = new();
    private bool _candValid;
    private int _selected = -1;
    private bool _heldFromMove;

    // ----- 3D nodes -----
    private Camera3D _cam = null!;
    private Node3D _vehicleRoot = null!;
    private Node3D _ghostRoot = null!;
    private readonly Dictionary<int, PartModel> _views = new();
    private readonly List<List<PartModel>> _ghostSets = new();
    private readonly List<MeshInstance3D> _markers = new();
    private StandardMaterial3D _ghostOk = null!, _ghostBad = null!, _selOverlay = null!;
    private double _yaw = 0.7, _pitch = 0.25, _dist = 12;
    private Vector3 _target = new(0f, 2.5f, 0f);
    private bool _orbiting, _panning;
    private Vector2 _rmbStart;
    private double _vehicleLift;

    public override void _Ready()
    {
        GameData.Load();
        var gs = GameState.Instance;
        gs.ApplyGraphicsSettings();
        _cat = GameData.Catalog; _ed = new VehicleEditor(_cat);
        _design = gs.CurrentDesign.Clone();
        if (!_design.IsEmpty) { try { _design.Recompute(_cat); } catch (DataException ex) { GD.PushWarning(ex.Message); _design = new VehicleDesign(); } }

        BuildWorld();
        BuildUi();
        RefreshAll(true);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  World
    // ------------------------------------------------------------------------------------------------------------
    private void BuildWorld()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.05f, 0.07f, 0.11f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.62f, 0.75f), AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic, GlowEnabled = true, GlowIntensity = 0.3f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var key = new DirectionalLight3D { RotationDegrees = new Vector3(-40, 35, 0), LightEnergy = 1.25f, ShadowEnabled = true, DirectionalShadowMaxDistance = 60 };
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-20, -140, 0), LightEnergy = 0.45f, LightColor = new Color(0.7f, 0.8f, 1f) };
        AddChild(key); AddChild(fill);

        // floor with a metric grid
        var floorShader = new Shader { Code = @"
shader_type spatial;
varying vec3 wp;
void vertex() { wp = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz; }
void fragment() {
    vec2 g = abs(fract(wp.xz - 0.5) - 0.5) / fwidth(wp.xz);
    float minor = 1.0 - min(min(g.x, g.y), 1.0);
    vec2 g5 = abs(fract(wp.xz / 5.0 - 0.5) - 0.5) / (fwidth(wp.xz) / 5.0);
    float major = 1.0 - min(min(g5.x, g5.y), 1.0);
    float r = length(wp.xz);
    float fade = 1.0 - smoothstep(10.0, 28.0, r);
    vec3 base = vec3(0.07, 0.09, 0.12);
    ALBEDO = base + vec3(0.10, 0.2, 0.28) * minor * 0.5 * fade + vec3(0.2, 0.45, 0.6) * major * fade;
    ROUGHNESS = 0.7; METALLIC = 0.0;
}" };
        var floor = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 30, BottomRadius = 30, Height = 0.2f, RadialSegments = 96 }, MaterialOverride = new ShaderMaterial { Shader = floorShader }, Position = new Vector3(0, -0.1f, 0) };
        AddChild(floor);

        // backdrop gantry (decor): two towers and a cross beam
        var steel = Materials.Get("steel");
        void Box(Vector3 size, Vector3 pos) => AddChild(new MeshInstance3D { Mesh = MeshFactory.Box(size), MaterialOverride = steel, Position = pos, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
        Box(new Vector3(0.6f, 24, 0.6f), new Vector3(-9, 12, -9)); Box(new Vector3(0.6f, 24, 0.6f), new Vector3(9, 12, -9));
        Box(new Vector3(18.6f, 0.5f, 0.5f), new Vector3(0, 23.8f, -9));
        Box(new Vector3(18, 0.3f, 0.3f), new Vector3(0, 12, -9));

        _vehicleRoot = new Node3D { Name = "Vehicle" }; AddChild(_vehicleRoot);
        _ghostRoot = new Node3D { Name = "Ghosts" }; AddChild(_ghostRoot);

        _cam = new Camera3D { Current = true, Fov = 55, Near = 0.05f, Far = 400 }; AddChild(_cam);
        UpdateCamera();

        _ghostOk = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.95f, 0.55f, 0.5f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _ghostBad = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.3f, 0.25f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _selOverlay = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        var markerMesh = MeshFactory.Sphere(1.0, 12);
        for (int i = 0; i < 40; i++)
        {
            var m = new MeshInstance3D { Mesh = markerMesh, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.4f, 1f, 0.5f, 0.9f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true } };
            AddChild(m); _markers.Add(m);
        }
    }

    private void UpdateCamera()
    {
        _pitch = Math.Clamp(_pitch, -1.45, 1.45);
        _dist = Math.Clamp(_dist, 2.0, 80.0);
        var off = new Vector3((float)(Math.Cos(_pitch) * Math.Sin(_yaw)), (float)Math.Sin(_pitch), (float)(Math.Cos(_pitch) * Math.Cos(_yaw))) * (float)_dist;
        _cam.Position = _target + off;
        _cam.LookAt(_target, Vector3.Up);
    }

    private void FrameVehicle()
    {
        if (_design.IsEmpty) { _target = new Vector3(0, 2.5f, 0); _dist = 12; UpdateCamera(); return; }
        double minY = double.MaxValue, maxY = double.MinValue, maxR = 0;
        foreach (var p in _design.Parts)
        {
            var def = _cat.Get(p.PartId);
            minY = Math.Min(minY, p.Position.Y - def.Shape.HalfLength); maxY = Math.Max(maxY, p.Position.Y + def.Shape.HalfLength);
            maxR = Math.Max(maxR, Math.Sqrt(p.Position.X * p.Position.X + p.Position.Z * p.Position.Z) + def.Shape.MaxRadius);
        }
        double h = maxY - minY;
        _target = new Vector3(0, (float)(_vehicleLift + (minY + maxY) / 2), 0);
        _dist = Math.Clamp(Math.Max(h * 1.25, maxR * 3.5) + 4, 6, 70);
        UpdateCamera();
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Design <-> view
    // ------------------------------------------------------------------------------------------------------------
    private void RefreshAll(bool frame = false)
    {
        try { if (!_design.IsEmpty) _design.Recompute(_cat); }
        catch (DataException ex) { SetHint("Design error: " + ex.Message); }
        _vehicleLift = _design.IsEmpty ? 0 : -_design.LowestPoint(_cat);
        _vehicleRoot.Position = new Vector3(0, (float)_vehicleLift, 0);

        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear();
        foreach (var inst in _design.Parts)
        {
            var def = _cat.Get(inst.PartId);
            var model = PartModel.Build(def);
            model.Position = ToV(inst.Position); model.Quaternion = ToQ(inst.Rotation);
            if (def.Leg != null) model.Animate(0, 0, false, 1);
            if (def.Solar != null) model.Animate(0, 1, false, 1);   // show panels deployed in the editor for readability
            var body = new StaticBody3D { Name = "Body" };
            body.SetMeta("part_id", inst.Id);
            var shape = new CollisionShape3D();
            var sh = def.Shape;
            if (sh.IsBox) shape.Shape = new BoxShape3D { Size = new Vector3((float)sh.Size[0], (float)sh.Size[1], (float)sh.Size[2]) };
            else shape.Shape = new CylinderShape3D { Radius = (float)sh.MaxRadius, Height = (float)sh.Length };
            body.AddChild(shape);
            model.AddChild(body);
            _vehicleRoot.AddChild(model);
            _views[inst.Id] = model;
        }
        ApplySelectionHighlight();
        if (frame) FrameVehicle();
        RefreshUi();
    }

    private static Vector3 ToV(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    private static Quaternion ToQ(QuatD q) => new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);

    private void ApplySelectionHighlight()
    {
        foreach (var (id, view) in _views)
            foreach (var mi in AllMeshes(view)) mi.MaterialOverlay = id == _selected ? _selOverlay : null;
    }

    private static IEnumerable<MeshInstance3D> AllMeshes(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is MeshInstance3D mi) yield return mi;
            foreach (var d in AllMeshes(c)) yield return d;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Undo / redo
    // ------------------------------------------------------------------------------------------------------------
    private string Snapshot() => Json.Serialize(_design);

    private void PushUndo()
    {
        _undo.Add(Snapshot()); if (_undo.Count > 120) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Restore(string json)
    {
        _design = Json.Deserialize<VehicleDesign>(json, "undo snapshot");
        if (!_design.IsEmpty) _design.Recompute(_cat);
        _selected = _design.Find(_selected) != null ? _selected : -1;
        RefreshAll();
    }

    private void DoUndo() { if (_undo.Count == 0) return; _redo.Add(Snapshot()); var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); CancelHeld(false); Restore(s); }
    private void DoRedo() { if (_redo.Count == 0) return; _undo.Add(Snapshot()); var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); CancelHeld(false); Restore(s); }

    // ------------------------------------------------------------------------------------------------------------
    //  Holding / placing
    // ------------------------------------------------------------------------------------------------------------
    private void PickCatalogPart(PartDefinition def)
    {
        CancelHeld(true);
        var h = new VehicleDesign { Name = def.Name };
        h.Parts.Add(new PartInstance { Id = 1, PartId = def.Id, Mode = AttachMode.Root });
        h.NextPartId = 2;
        BeginHold(h, def.Name, false);
    }

    private void BeginHold(VehicleDesign held, string label, bool fromMove)
    {
        _held = held; _heldLabel = label; _heldRoll = 0; _flip = 0; _heldFromMove = fromMove; _cand = null;
        held.Recompute(_cat);
        ClearGhosts();
        SetHint($"Placing {label}: click to attach · Q/E rotate · F flip node · X symmetry · Esc cancel");
    }

    private void CancelHeld(bool silent)
    {
        if (_held == null) return;
        bool wasMove = _heldFromMove;
        _held = null; _cand = null; _heldFromMove = false;
        ClearGhosts(); HideMarkers();
        if (wasMove && !silent) DoUndoAfterCancelMove();
        SetHint("");
    }

    private void DoUndoAfterCancelMove()
    {
        // The pick-up pushed an undo state; revert it so cancelling a move restores the vehicle.
        if (_undo.Count == 0) return;
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
    }

    private void ClearGhosts()
    {
        foreach (var set in _ghostSets) foreach (var m in set) m.QueueFree();
        _ghostSets.Clear();
    }

    private List<PartModel> BuildGhostSet()
    {
        var set = new List<PartModel>();
        foreach (var hp in _held!.Parts)
        {
            var m = PartModel.Build(_cat.Get(hp.PartId));
            foreach (var mi in AllMeshes(m)) { mi.MaterialOverride = _ghostOk; mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; }
            _ghostRoot.AddChild(m);
            set.Add(m);
        }
        return set;
    }

    private void TryPlace()
    {
        if (_held == null) return;
        var root = _held.Root!;
        if (_design.IsEmpty)
        {
            PushUndo();
            _design = new VehicleDesign { Name = _design.Name == "" ? "Untitled Vehicle" : _design.Name, NextSymmetryGroup = 1 };
            var map = new Dictionary<int, int>();
            foreach (var hp in _held.Parts) map[hp.Id] = _design.AllocateId();
            foreach (var hp in _held.Parts)
            {
                var np = hp.Clone(); np.Id = map[hp.Id]; np.ParentId = hp.ParentId < 0 ? -1 : map[hp.ParentId]; _design.Parts.Add(np);
            }
            _design.Recompute(_cat); StagingPlanner.AutoAssign(_design, _cat);
        }
        else
        {
            if (_cand == null || !_candValid) { SetHint("Nothing to attach to there: move near a highlighted node or onto a tank's side (for radial parts)."); return; }
            PushUndo();
            int sym = _cand.Mode == AttachMode.Radial ? _symmetry : 1;
            try { _ed.AttachDesign(_design, _held, _cand, sym, _heldRoll); }
            catch (DataException ex) { SetHint(ex.Message); return; }
        }
        bool keep = Input.IsKeyPressed(Key.Shift) && !_heldFromMove;
        var again = _held;
        _heldFromMove = false;
        if (keep) { ClearGhosts(); _cand = null; } else { _held = null; _cand = null; ClearGhosts(); HideMarkers(); SetHint(""); }
        _selected = -1;
        RefreshAll(_design.Parts.Count <= 2);
    }

    private void SelectPart(int id)
    {
        _selected = id; ApplySelectionHighlight(); RefreshSelectionUi();
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _design.Find(_selected) == null) return;
        PushUndo();
        _ed.Delete(_design, _selected);
        _selected = -1;
        RefreshAll();
    }

    private void DuplicateSelected(bool move)
    {
        if (_selected < 0 || _design.Find(_selected) == null) return;
        var sub = _ed.ExtractSubtree(_design, _selected);
        var name = _cat.Get(_design.Get(_selected).PartId).Name;
        if (move)
        {
            if (_design.Get(_selected).ParentId < 0) { SetHint("The root part cannot be moved; delete it or build around it."); return; }
            PushUndo();
            _ed.RemoveSubtree(_design, _selected);
            _selected = -1;
            RefreshAll();
            BeginHold(sub, name, true);
        }
        else BeginHold(sub, name + " (copy)", false);
    }

    private void CycleSymmetry() { int[] order = { 1, 2, 3, 4, 6, 8 }; _symmetry = order[(Array.IndexOf(order, _symmetry) + 1) % order.Length]; RefreshTopBar(); }

    // ------------------------------------------------------------------------------------------------------------
    //  Per-frame: candidate selection, ghosts and markers
    // ------------------------------------------------------------------------------------------------------------
    public override void _Process(double delta)
    {
        if (_held == null) { HideMarkers(); return; }
        var mouse = GetViewport().GetMousePosition();
        bool overUi = GetViewport().GuiGetHoveredControl() != null;
        var root = _held.Root!;
        var rootDef = _cat.Get(root.PartId);

        if (_design.IsEmpty)
        {
            _cand = null; _candPoses = new() { (Vec3d.Zero, QuatD.Identity) }; _candValid = true;
            ShowGhosts(1, valid: true, lift: 0);
            HideMarkers();
            return;
        }

        var stackCands = _ed.FindNodeCandidates(_design, _held);
        // marker per unique anchor
        int mi = 0;
        var anchors = new List<(AttachCandidate c, Vector3 world, Vector2 screen)>();
        foreach (var c in stackCands)
        {
            var parent = _design.Get(c.ParentId);
            var pn = _cat.Get(parent.PartId).Node(c.ParentNode)!;
            var (ap, _) = AttachGeometry.NodeWorld(parent, pn);
            var w = ToV(ap) + _vehicleRoot.Position;
            bool behind = _cam.IsPositionBehind(w);
            anchors.Add((c, w, behind ? new Vector2(-9999, -9999) : _cam.UnprojectPosition(w)));
        }

        AttachCandidate? best = null; double bestD = 70;
        var groups = anchors.GroupBy(a => (a.c.ParentId, a.c.ParentNode)).ToList();
        foreach (var g in groups)
        {
            var first = g.First();
            double d = first.screen.DistanceTo(mouse);
            if (d < bestD)
            {
                bestD = d;
                var options = g.Select(x => x.c).ToList();
                // prefer the identity orientation (child node opposite to parent axis) first; F cycles through the rest
                options = options.OrderBy(o => Math.Abs(QuatAngle(o.Rotation))).ToList();
                best = options[((_flip % options.Count) + options.Count) % options.Count];
            }
        }
        var chosen = best;
        AttachCandidate? radial = null;
        bool wantsRadial = rootDef.Nodes.Any(n => n.IsSurface);
        if (wantsRadial && !overUi)
        {
            var hit = Raycast(mouse);
            if (hit.HasValue && _design.Find(hit.Value.partId) is { } hp && _cat.Get(hp.PartId).AcceptsRadial && !_cat.Get(hp.PartId).Shape.IsBox)
            {
                var local = hp.Rotation.InverseRotate(FromV(hit.Value.pos - _vehicleRoot.Position) - hp.Position);
                double ang = Math.Atan2(local.Z, local.X) * MathUtil.Rad2Deg;
                double snapA = Input.IsKeyPressed(Key.Alt) ? 1.0 : 15.0;
                ang = Math.Round(ang / snapA) * snapA;
                double oy = Math.Round(local.Y / 0.1) * 0.1;
                radial = _ed.RadialCandidate(_design, hp.Id, root.PartId, ang, oy);
            }
        }
        // stack snap wins when very close; otherwise a radial surface hit; otherwise a looser stack snap
        if (best != null && bestD < 38) chosen = best;
        else if (radial != null) chosen = radial;
        else chosen = best;
        _cand = chosen;

        // markers
        foreach (var g in groups)
        {
            if (mi >= _markers.Count) break;
            var first = g.First();
            bool isChosen = chosen != null && chosen.Mode == AttachMode.Stack && chosen.ParentId == first.c.ParentId && chosen.ParentNode == first.c.ParentNode;
            var m = _markers[mi++];
            m.Visible = true; m.Position = first.world;
            float s = (float)(_dist * (isChosen ? 0.016 : 0.010));
            m.Scale = new Vector3(s, s, s);
            ((StandardMaterial3D)m.MaterialOverride!).AlbedoColor = isChosen ? new Color(1f, 0.9f, 0.2f, 0.95f) : new Color(0.4f, 1f, 0.5f, 0.75f);
        }
        for (; mi < _markers.Count; mi++) _markers[mi].Visible = false;

        _candValid = chosen != null;
        if (chosen == null)
        {
            // float the ghost in front of the camera so the player sees what they hold
            var ray = _cam.ProjectRayNormal(mouse);
            var p = _cam.Position + ray * (float)(_dist * 0.8) - _vehicleRoot.Position;
            _candPoses = new() { (FromV(p), QuatD.Identity) };
            ShowGhosts(1, false, _vehicleLift);
            return;
        }
        _candPoses = ComputeCopyPoses(chosen, root.PartId);
        ShowGhosts(_candPoses.Count, true, _vehicleLift);
    }

    private static double QuatAngle(QuatD q) => 2 * Math.Acos(Math.Clamp(Math.Abs(q.W), 0, 1));

    private List<(Vec3d, QuatD)> ComputeCopyPoses(AttachCandidate c, string rootPartId)
    {
        var poses = new List<(Vec3d, QuatD)>();
        if (c.Mode == AttachMode.Radial)
        {
            int n = _symmetry;
            for (int i = 0; i < n; i++)
            {
                var cc = new AttachCandidate { ParentId = c.ParentId, Mode = c.Mode, ChildNode = c.ChildNode, OffsetY = c.OffsetY, AngleDeg = c.AngleDeg + 360.0 * i / n };
                poses.Add(_ed.PoseFor(_design, cc, rootPartId, _heldRoll));
            }
        }
        else
        {
            poses.Add(_ed.PoseFor(_design, c, rootPartId, _heldRoll));
            var parent = _design.Get(c.ParentId);
            if (parent.SymmetryGroup != 0)
                foreach (var sib in _design.Parts.Where(p => p.SymmetryGroup == parent.SymmetryGroup && p.Id != parent.Id))
                {
                    if (!_ed.IsNodeFree(_design, sib.Id, c.ParentNode)) continue;
                    var cc = new AttachCandidate { ParentId = sib.Id, Mode = AttachMode.Stack, ParentNode = c.ParentNode, ChildNode = c.ChildNode };
                    poses.Add(_ed.PoseFor(_design, cc, rootPartId, _heldRoll));
                }
        }
        return poses;
    }

    private void ShowGhosts(int count, bool valid, double lift)
    {
        count = Math.Min(count, 8);
        while (_ghostSets.Count < count) _ghostSets.Add(BuildGhostSet());
        var mat = valid ? _ghostOk : _ghostBad;
        for (int k = 0; k < _ghostSets.Count; k++)
        {
            bool vis = k < count;
            for (int i = 0; i < _ghostSets[k].Count; i++)
            {
                var m = _ghostSets[k][i]; m.Visible = vis;
                if (!vis) continue;
                var (pp, pr) = _candPoses[Math.Min(k, _candPoses.Count - 1)];
                var hp = _held!.Parts[i];
                var wp = pp + pr.Rotate(hp.Position);
                var wr = pr * hp.Rotation;
                m.Position = ToV(wp) + new Vector3(0, (float)lift, 0); m.Quaternion = ToQ(wr);
                foreach (var mi in AllMeshes(m)) mi.MaterialOverride = mat;
            }
        }
    }

    private void HideMarkers() { foreach (var m in _markers) m.Visible = false; }

    private static Vec3d FromV(Vector3 v) => new(v.X, v.Y, v.Z);

    private (int partId, Vector3 pos)? Raycast(Vector2 mouse)
    {
        var from = _cam.ProjectRayOrigin(mouse);
        var to = from + _cam.ProjectRayNormal(mouse) * 200f;
        var q = PhysicsRayQueryParameters3D.Create(from, to);
        var res = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (res.Count == 0) return null;
        var col = res["collider"].AsGodotObject() as Node;
        if (col == null || !col.HasMeta("part_id")) return null;
        return ((int)col.GetMeta("part_id"), res["position"].AsVector3());
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Input
    // ------------------------------------------------------------------------------------------------------------
    public override void _UnhandledInput(InputEvent e)
    {
        var s = GameState.Instance.Settings.MouseSensitivity;
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right) { _orbiting = mb.Pressed; _rmbStart = mb.Position; }
            else if (mb.ButtonIndex == MouseButton.Middle) _panning = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) { _dist *= 0.9; UpdateCamera(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) { _dist *= 1.1; UpdateCamera(); }
            else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (_held != null) TryPlace();
                else
                {
                    var hit = Raycast(mb.Position);
                    if (hit.HasValue) SelectPart(hit.Value.partId); else SelectPart(-1);
                }
            }
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_orbiting) { _yaw -= mm.Relative.X * 0.006 * s; _pitch += mm.Relative.Y * 0.006 * s; UpdateCamera(); }
            else if (_panning)
            {
                var right = _cam.GlobalTransform.Basis.X; var up = _cam.GlobalTransform.Basis.Y;
                _target += (-right * mm.Relative.X + up * mm.Relative.Y) * (float)(_dist * 0.0012 * s);
                UpdateCamera();
            }
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            // don't steal typing from line edits
            if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
            switch (k.Keycode)
            {
                case Key.Escape: if (_held != null) CancelHeld(false); else SelectPart(-1); break;
                case Key.Delete: case Key.Backspace: DeleteSelected(); break;
                case Key.Z when k.CtrlPressed || k.MetaPressed: DoUndo(); break;
                case Key.Y when k.CtrlPressed || k.MetaPressed: DoRedo(); break;
                case Key.D when k.CtrlPressed || k.MetaPressed: DuplicateSelected(false); break;
                case Key.M: DuplicateSelected(true); break;
                case Key.Q: _heldRoll -= 15; break;
                case Key.E: _heldRoll += 15; break;
                case Key.F: _flip++; break;
                case Key.X: CycleSymmetry(); break;
                case Key.Home: FrameVehicle(); break;
            }
        }
    }
}
