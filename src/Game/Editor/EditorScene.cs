using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Persistence;
using OSP.Core.Vehicles;
using OSP.Game.App;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Editor
{
    /// <summary>
    /// Vehicle assembly building (scenes/Editor.tscn). Owns the design being edited and implements part placement:
    /// stack-node snapping, surface attachment with radial symmetry and angle snap, picking up and moving
    /// sub-assemblies, duplication, deletion, undo/redo and automatic staging. All attachment rules and geometry
    /// come from <see cref="Assembly"/> in the simulation core.
    /// </summary>
    public partial class EditorScene : Node3D
    {
        public VesselDesign Design { get; private set; }
        public int Symmetry { get; set; } = 1;
        public bool AngleSnap { get; set; } = true;
        public bool ShowCenters { get; set; } = true;
        public int SelectedPartId { get; set; } = -1;
        public bool IsHolding => _held != null;
        public string HintText { get; private set; } = "";
        public event System.Action DesignChanged;

        private static readonly int[] SymmetrySteps = { 1, 2, 3, 4, 6, 8 };

        private EditorCamera _camera;
        private Node3D _vesselRoot;
        private readonly Dictionary<int, PartVisual> _visuals = new();
        private readonly List<string> _undo = new();
        private readonly List<string> _redo = new();
        private bool _manualStaging;
        private EditorUi _ui;
        private MeshInstance3D _comMarker, _cotMarker;
        private float _lift = 1;

        // Held sub-assembly (parts in the held root's local frame; root first).
        private sealed class Held
        {
            public List<PartInstance> Parts = new();
            public bool IsNew;
        }

        private sealed class Candidate
        {
            public bool Valid;
            public string Reason;
            public bool Surface;
            public bool AsRoot;
            public List<(int parentId, string parentNode, string selfNode, Placement placement)> Copies = new();
        }

        private Held _held;
        private Candidate _candidate;
        private readonly List<Node3D> _ghosts = new();
        private double _spin;
        private int _hoverId = -1;
        private Vector2 _mouse;

        public override void _Ready()
        {
            GameRoot game = GameRoot.Instance;
            Design = game.CurrentDesign ?? new VesselDesign();
            game.CurrentDesign = Design;
            Design.Resolve(game.Catalog);

            BuildHangar();
            _camera = new EditorCamera { Name = "EditorCamera", Sensitivity = (float)game.Settings.MouseSensitivity };
            AddChild(_camera);
            _vesselRoot = new Node3D { Name = "Vessel" };
            AddChild(_vesselRoot);

            _comMarker = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f }, MaterialOverride = MaterialLibrary.Unshaded(new Color(1, 0.85f, 0.1f), noDepth: true) };
            _cotMarker = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0, BottomRadius = 0.16f, Height = 0.6f }, MaterialOverride = MaterialLibrary.Unshaded(new Color(0.9f, 0.3f, 1f), noDepth: true) };
            AddChild(_comMarker);
            AddChild(_cotMarker);

            var layer = new CanvasLayer { Layer = 1 };
            AddChild(layer);
            _ui = EditorUi.Create(this);
            layer.AddChild(_ui);

            if (Design.Parts.Count > 0 && !Design.Parts.Any(p => p.Stage >= 0)) Staging.AutoStage(Design);
            RebuildVisuals();
            FrameVessel();
        }

        private void BuildHangar()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial { SkyTopColor = new Color(0.16f, 0.2f, 0.28f), SkyHorizonColor = new Color(0.35f, 0.38f, 0.42f), GroundBottomColor = new Color(0.08f, 0.08f, 0.09f), GroundHorizonColor = new Color(0.25f, 0.26f, 0.28f) } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.7f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                SsaoEnabled = GameRoot.Instance.Settings.Quality == GraphicsQuality.High,
                GlowEnabled = true,
                GlowIntensity = 0.3f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { Basis = Basis.LookingAt(new Vector3(-0.5f, -1, -0.35f).Normalized(), Vector3.Up), LightEnergy = 1.3f, ShadowEnabled = GameRoot.Instance.Settings.Shadows, DirectionalShadowMaxDistance = 120 });
            AddChild(new DirectionalLight3D { Basis = Basis.LookingAt(new Vector3(0.6f, -0.3f, 0.8f).Normalized(), Vector3.Up), LightEnergy = 0.35f, LightColor = new Color(0.7f, 0.8f, 1f) });

            var floorMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grid.gdshader") };
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(120, 120) }, MaterialOverride = floorMat });
            // Assembly building: walls, gantry columns and a raised door frame.
            var wall = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.3f, 0.33f), Roughness = 0.85f };
            var steel = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.47f, 0.5f), Metallic = 0.6f, Roughness = 0.4f };
            var stripe = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.7f, 0.1f), Roughness = 0.5f };
            void B(Vector3 size, Vector3 pos, Material m) => AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = pos, MaterialOverride = m });
            B(new Vector3(60, 45, 1), new Vector3(0, 22.5f, -30), wall);
            B(new Vector3(1, 45, 60), new Vector3(-30, 22.5f, 0), wall);
            B(new Vector3(1, 45, 60), new Vector3(30, 22.5f, 0), wall);
            for (int i = -2; i <= 2; i++)
            {
                B(new Vector3(0.8f, 45, 0.8f), new Vector3(i * 12, 22.5f, -29), steel);
                B(new Vector3(0.8f, 45, 0.8f), new Vector3(-29, 22.5f, i * 12), steel);
                B(new Vector3(0.8f, 45, 0.8f), new Vector3(29, 22.5f, i * 12), steel);
            }
            for (int y = 8; y < 45; y += 9) B(new Vector3(60, 0.5f, 0.6f), new Vector3(0, y, -29.2f), steel);
            B(new Vector3(10, 0.05f, 10), new Vector3(0, 0.02f, 0), stripe);
            B(new Vector3(9.4f, 0.06f, 9.4f), new Vector3(0, 0.03f, 0), new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.23f), Roughness = 0.7f });
            for (int i = 0; i < 4; i++)
            {
                var light = new OmniLight3D { Position = new Vector3((i % 2 == 0 ? -1 : 1) * 14, 30, (i < 2 ? -1 : 1) * 14), OmniRange = 60, LightEnergy = 1.2f, LightColor = new Color(1, 0.95f, 0.85f) };
                AddChild(light);
            }
        }

        // ---- Undo / redo --------------------------------------------------------------------------------

        private void Snapshot()
        {
            _undo.Add(BlueprintSerializer.Serialize(Design));
            if (_undo.Count > 64) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public void Undo()
        {
            CancelHold(discard: true, record: false);
            if (_undo.Count == 0) return;
            _redo.Add(BlueprintSerializer.Serialize(Design));
            Restore(_undo[^1]);
            _undo.RemoveAt(_undo.Count - 1);
        }

        public void Redo()
        {
            CancelHold(discard: true, record: false);
            if (_redo.Count == 0) return;
            _undo.Add(BlueprintSerializer.Serialize(Design));
            Restore(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
        }

        private void Restore(string json)
        {
            VesselDesign d = BlueprintSerializer.Deserialize(json, GameRoot.Instance.Catalog, out _);
            SetDesign(d, keepUndo: true);
        }

        public void SetDesign(VesselDesign d, bool keepUndo = false)
        {
            CancelHold(discard: true, record: false);
            Design = d;
            GameRoot.Instance.CurrentDesign = d;
            Design.Resolve(GameRoot.Instance.Catalog);
            if (!keepUndo)
            {
                _undo.Clear();
                _redo.Clear();
                _manualStaging = Design.Parts.Any(p => p.Stage >= 0);
            }
            SelectedPartId = -1;
            OnChanged(autoStage: false);
            FrameVessel();
        }

        public void NewDesign()
        {
            Snapshot();
            SetDesign(new VesselDesign(), keepUndo: true);
            _manualStaging = false;
        }

        // ---- Changes ----------------------------------------------------------------------------------------

        private void OnChanged(bool autoStage = true)
        {
            if (autoStage && !_manualStaging) Staging.AutoStage(Design);
            else Staging.Compact(Design);
            RebuildVisuals();
            DesignChanged?.Invoke();
        }

        public void MarkManualStaging() => _manualStaging = true;
        public void RunAutoStage()
        {
            Snapshot();
            _manualStaging = false;
            OnChanged();
        }

        public void ChangeStage(PartInstance part, int delta)
        {
            Snapshot();
            _manualStaging = true;
            int target = part.Stage + delta;
            if (target < 0)
            {
                Staging.InsertStage(Design, 0);
                target = 0;
            }
            Staging.MoveToStage(Design, part, target);
            OnChanged(autoStage: false);
        }

        public void Rename(string name)
        {
            Design.Name = string.IsNullOrWhiteSpace(name) ? "Untitled Rocket" : name.Trim();
            DesignChanged?.Invoke();
        }

        private void RebuildVisuals()
        {
            foreach (PartVisual v in _visuals.Values) v.QueueFree();
            _visuals.Clear();
            foreach (PartInstance p in Design.Parts)
            {
                if (p.Def == null) continue;
                PartVisual pv = PartVisual.Create(p.Def, p.Id);
                pv.Transform = new Transform3D(new Basis(p.Rotation.ToGodot()), p.Position.ToGodot());
                pv.ApplyEditorPose();
                _vesselRoot.AddChild(pv);
                _visuals[p.Id] = pv;
            }
            // Keep the vessel standing just above the floor.
            double minY = Design.Parts.Count == 0 ? 0 : Design.Parts.Min(p => VesselMinY(p));
            _lift = (float)(0.6 - minY);
            _vesselRoot.Position = new Vector3(0, _lift, 0);
            UpdateCenters();
        }

        private static double VesselMinY(PartInstance p)
        {
            double m = double.MaxValue;
            PartBounds b = p.Def.Bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3d((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                m = System.Math.Min(m, p.LocalToVessel(c).Y);
            }
            return m;
        }

        private void UpdateCenters()
        {
            bool any = Design.Parts.Count > 0 && ShowCenters;
            _comMarker.Visible = any;
            _cotMarker.Visible = false;
            if (!any) return;
            MassProperties mp = MassProperties.Compute(Design.Parts);
            _comMarker.Position = mp.CenterOfMass.ToGodot() + new Vector3(0, _lift, 0);
            Vector3d sumPos = Vector3d.Zero, sumDir = Vector3d.Zero;
            double sumF = 0;
            foreach (PartInstance p in Design.Parts)
            {
                EngineModuleDef e = p.Def.GetModule<EngineModuleDef>();
                if (e == null) continue;
                double f = e.ThrustAt(1);
                sumPos += p.LocalToVessel(e.ThrustPoint) * f;
                sumDir += -p.DirectionToVessel(e.ExhaustDirection) * f;
                sumF += f;
            }
            if (sumF > 0)
            {
                _cotMarker.Visible = true;
                Vector3 pos = (sumPos / sumF).ToGodot() + new Vector3(0, _lift, 0);
                Vector3 dir = sumDir.Normalized().ToGodot();
                _cotMarker.Transform = new Transform3D(new Basis(new Quaternion(Vector3.Up, dir.Length() > 0.5f ? dir : Vector3.Up)), pos - dir * 0.4f);
            }
        }

        public void FrameVessel()
        {
            if (Design.Parts.Count == 0)
            {
                _camera.Frame(new Aabb(new Vector3(-2, 0, -2), new Vector3(4, 5, 4)));
                return;
            }
            Aabb box = new(Design.Parts[0].Position.ToGodot() + new Vector3(0, _lift, 0), Vector3.Zero);
            foreach (PartInstance p in Design.Parts)
                box = box.Expand(p.Position.ToGodot() + new Vector3(0, _lift, 0)).Merge(new Aabb(p.Position.ToGodot() + new Vector3(0, _lift, 0) + p.Def.Bounds.Min.ToGodot(), p.Def.Bounds.Size.ToGodot()));
            _camera.Frame(box);
        }

        // ---- Holding parts ------------------------------------------------------------------------------------

        public void StartPlacing(PartDefinition def)
        {
            CancelHold(discard: true, record: false);
            var p = new PartInstance { Id = -1, Part = def.Id, Def = def, Parent = -1, Position = Vector3d.Zero, Rotation = QuaternionD.Identity };
            p.InitialiseResources();
            _held = new Held { Parts = new List<PartInstance> { p }, IsNew = true };
            _spin = 0;
            BuildGhosts();
        }

        private void PickUp(PartInstance part)
        {
            if (part.Parent < 0 && Design.Parts.Count > 1)
            {
                _ui.ShowHint("The root part cannot be detached. Pick up the parts attached to it instead.");
                return;
            }
            Snapshot();
            var held = new Held { IsNew = false };
            List<PartInstance> subtree = Design.Subtree(part.Id);
            QuaternionD inv = part.Rotation.Inverse();
            foreach (PartInstance q in subtree)
            {
                PartInstance c = q.Clone();
                c.Def = q.Def;
                c.Position = inv.Rotate(q.Position - part.Position);
                c.Rotation = (inv * q.Rotation).Normalized();
                held.Parts.Add(c);
            }
            // Symmetry counterparts are removed too and re-created on placement.
            if (part.SymmetryGroup != 0)
            {
                var counterparts = Design.Parts.Where(p => p.SymmetryGroup == part.SymmetryGroup && p.Id != part.Id).ToList();
                Symmetry = counterparts.Count + 1;
                foreach (PartInstance cp in counterparts) Design.RemoveSubtree(cp.Id);
            }
            else Symmetry = 1;
            Design.RemoveSubtree(part.Id);
            _held = held;
            _spin = 0;
            OnChanged();
            BuildGhosts();
        }

        public void DuplicateHovered()
        {
            PartInstance part = Design.Get(_hoverId) ?? Design.Get(SelectedPartId);
            if (part == null) return;
            CancelHold(discard: true, record: false);
            var held = new Held { IsNew = true };
            QuaternionD inv = part.Rotation.Inverse();
            foreach (PartInstance q in Design.Subtree(part.Id))
            {
                PartInstance c = q.Clone();
                c.Def = q.Def;
                c.Position = inv.Rotate(q.Position - part.Position);
                c.Rotation = (inv * q.Rotation).Normalized();
                held.Parts.Add(c);
            }
            _held = held;
            _spin = 0;
            BuildGhosts();
        }

        public void DeletePart(PartInstance part)
        {
            if (part == null) return;
            Snapshot();
            if (part.SymmetryGroup != 0)
                foreach (PartInstance cp in Design.Parts.Where(p => p.SymmetryGroup == part.SymmetryGroup && p.Id != part.Id).ToList())
                    Design.RemoveSubtree(cp.Id);
            Design.RemoveSubtree(part.Id);
            if (SelectedPartId == part.Id) SelectedPartId = -1;
            OnChanged();
        }

        public void CancelHold(bool discard, bool record = true)
        {
            if (_held == null) return;
            // Parts that were picked up and then dropped nowhere are deleted (undo restores them).
            _held = null;
            _candidate = null;
            ClearGhosts();
            if (record) DesignChanged?.Invoke();
        }

        private void ClearGhosts()
        {
            foreach (Node3D g in _ghosts) g.QueueFree();
            _ghosts.Clear();
        }

        private void BuildGhosts()
        {
            ClearGhosts();
            if (_held == null) return;
            int copies = System.Math.Max(1, Symmetry);
            for (int k = 0; k < 8; k++)
            {
                var root = new Node3D { Visible = k < copies };
                foreach (PartInstance p in _held.Parts)
                {
                    PartVisual pv = PartVisual.Create(p.Def, p.Id);
                    pv.Transform = new Transform3D(new Basis(p.Rotation.ToGodot()), p.Position.ToGodot());
                    pv.ApplyEditorPose();
                    pv.SetOverlay(MaterialLibrary.Ghost);
                    pv.SetCastShadows(false);
                    root.AddChild(pv);
                }
                AddChild(root);
                _ghosts.Add(root);
            }
        }

        public void CycleSymmetry(int dir = 1)
        {
            int i = System.Array.IndexOf(SymmetrySteps, Symmetry);
            if (i < 0) i = 0;
            Symmetry = SymmetrySteps[(i + dir + SymmetrySteps.Length) % SymmetrySteps.Length];
            DesignChanged?.Invoke();
        }

        // ---- Input ----------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_camera.HandleInput(ev)) return;
            if (ev is InputEventMouseMotion mm) _mouse = mm.Position;
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
            {
                _mouse = click.Position;
                if (_held != null) TryPlace();
                else
                {
                    PartInstance hit = HoveredPart();
                    if (hit != null && click.AltPressed) DuplicateHovered();
                    else if (hit != null) PickUp(hit);
                }
                GetViewport().SetInputAsHandled();
            }
            if (ev is InputEventKey { Pressed: true } key)
            {
                bool ctrl = key.CtrlPressed || key.MetaPressed;
                switch (key.Keycode)
                {
                    case Key.Escape when _held != null: CancelHold(true); break;
                    case Key.Z when ctrl && key.ShiftPressed: Redo(); break;
                    case Key.Z when ctrl: Undo(); break;
                    case Key.Y when ctrl: Redo(); break;
                    case Key.D when ctrl: DuplicateHovered(); break;
                    case Key.S when ctrl: _ui.SaveDesign(); break;
                    case Key.X: CycleSymmetry(key.ShiftPressed ? -1 : 1); BuildGhosts(); break;
                    case Key.C: AngleSnap = !AngleSnap; DesignChanged?.Invoke(); break;
                    case Key.R: _spin += (key.ShiftPressed ? 5 : 15) * MathUtil.Deg2Rad; break;
                    case Key.F: _spin -= (key.ShiftPressed ? 5 : 15) * MathUtil.Deg2Rad; break;
                    case Key.Delete:
                    case Key.Backspace:
                        if (_held != null) CancelHold(true);
                        else DeletePart(HoveredPart() ?? Design.Get(SelectedPartId));
                        break;
                    case Key.F1: _ui.ToggleHelp(); break;
                    default: return;
                }
                GetViewport().SetInputAsHandled();
            }
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && _held != null && !Input.IsMouseButtonPressed(MouseButton.Left))
            {
                // Right-click while holding rotates instead of cancelling, so camera orbit still works.
            }
        }

        private (Vector3d origin, Vector3d dir) MouseRay()
        {
            Vector3 o = _camera.ProjectRayOrigin(_mouse);
            Vector3 d = _camera.ProjectRayNormal(_mouse);
            return ((o - _vesselRoot.Position).ToCore(), d.ToCore());
        }

        private PartInstance HoveredPart()
        {
            var (o, d) = MouseRay();
            PartRayHit? hit = Assembly.Raycast(Design, o, d);
            return hit?.Part;
        }

        public override void _Process(double delta)
        {
            _mouse = GetViewport().GetMousePosition();
            bool overUi = _ui.IsMouseOverUi(_mouse);
            // Hover highlight.
            int hover = overUi || _held != null ? -1 : (HoveredPart()?.Id ?? -1);
            if (hover != _hoverId)
            {
                if (_visuals.TryGetValue(_hoverId, out PartVisual old)) old.SetOverlay(_hoverId == SelectedPartId ? MaterialLibrary.Highlight : null);
                _hoverId = hover;
            }
            foreach (var (id, pv) in _visuals)
                pv.SetOverlay(id == _hoverId || id == SelectedPartId ? MaterialLibrary.Highlight : null);

            if (_held != null) UpdateHeld(overUi);
            HintText = _held != null
                ? (_candidate == null ? "Move the part onto a node or surface. R/F rotate · X symmetry · C angle snap · Esc cancel"
                    : (_candidate.Valid ? (_candidate.AsRoot ? "Click to place the first (root) part." : "Click to attach.") : "Cannot attach: " + _candidate.Reason))
                : (hover >= 0 ? $"{Design.Get(hover)?.Def?.Name} — click to pick up · Alt+click duplicate · Del delete" : "Choose a part from the catalogue to start building.");
        }

        private void UpdateHeld(bool overUi)
        {
            _candidate = overUi ? null : FindCandidate();
            int copies = _candidate?.Copies.Count ?? 1;
            var (o, d) = MouseRay();
            for (int k = 0; k < _ghosts.Count; k++)
            {
                Node3D g = _ghosts[k];
                if (_candidate != null && k < _candidate.Copies.Count)
                {
                    Placement pl = _candidate.Copies[k].placement;
                    g.Visible = true;
                    g.Transform = new Transform3D(new Basis(pl.Rotation.ToGodot()), pl.Position.ToGodot() + _vesselRoot.Position);
                }
                else if (k == 0 && _candidate == null)
                {
                    // Free-floating in front of the camera.
                    Vector3d p = o + d * System.Math.Max(4, _camera.Distance * 0.6);
                    g.Visible = !overUi;
                    QuaternionD spin = QuaternionD.FromAxisAngle(Vector3d.UnitY, _spin);
                    g.Transform = new Transform3D(new Basis(spin.ToGodot()), p.ToGodot() + _vesselRoot.Position);
                }
                else g.Visible = false;
                Material overlay = _candidate == null ? MaterialLibrary.Ghost : (_candidate.Valid ? MaterialLibrary.Ghost : MaterialLibrary.Invalid);
                foreach (Node n in g.GetChildren()) if (n is PartVisual pv) pv.SetOverlay(overlay);
            }
            _ = copies;
        }

        private Candidate FindCandidate()
        {
            PartInstance heldRoot = _held.Parts[0];
            PartDefinition def = heldRoot.Def;
            var c = new Candidate();
            if (Design.Parts.Count == 0)
            {
                c.AsRoot = true;
                c.Valid = true;
                c.Copies.Add((-1, null, null, new Placement(Vector3d.Zero, QuaternionD.FromAxisAngle(Vector3d.UnitY, _spin))));
                return c;
            }

            // 1. Stack nodes near the cursor (screen space).
            var (o, d) = MouseRay();
            double bestScreen = 40;
            (PartInstance parent, AttachNodeDef pn, AttachNodeDef cn)? best = null;
            foreach (PartInstance p in Design.Parts)
            {
                foreach (AttachNodeDef pn in p.Def.AttachNodes)
                {
                    if (!Design.IsNodeFree(p, pn.Id)) continue;
                    Vector3 world = p.LocalToVessel(pn.Position).ToGodot() + _vesselRoot.Position;
                    if (_camera.IsPositionBehind(world)) continue;
                    float dist = _camera.UnprojectPosition(world).DistanceTo(_mouse);
                    if (dist >= bestScreen) continue;
                    Vector3d dirV = p.DirectionToVessel(pn.Direction);
                    AttachNodeDef cn = def.AttachNodes.OrderBy(n => Vector3d.Dot(n.Direction, dirV)).FirstOrDefault();
                    if (cn == null) continue;
                    bestScreen = dist;
                    best = (p, pn, cn);
                }
            }
            if (best.HasValue)
            {
                var (parent, pn, cn) = best.Value;
                AttachResult r = Assembly.ValidateStack(Design, parent, pn.Id, def, cn.Id);
                c.Valid = r.Ok;
                c.Reason = r.Reason;
                // Mirror onto symmetry counterparts of the parent.
                var parents = new List<PartInstance> { parent };
                if (parent.SymmetryGroup != 0) parents.AddRange(Design.Parts.Where(p => p.SymmetryGroup == parent.SymmetryGroup && p.Id != parent.Id));
                foreach (PartInstance par in parents)
                {
                    AttachNodeDef node = par.Def.GetNode(pn.Id);
                    if (node == null || !Design.IsNodeFree(par, node.Id)) continue;
                    Placement pl = Assembly.StackPlacement(par, node, def, cn, _spin);
                    c.Copies.Add((par.Id, pn.Id, cn.Id, pl));
                }
                ValidateOverlaps(c);
                return c;
            }

            // 2. Surface attachment under the cursor.
            if (def.SurfaceAttach == null) return null;
            PartRayHit? hit = Assembly.Raycast(Design, o, d);
            if (!hit.HasValue) return null;
            PartInstance target = hit.Value.Part;
            Vector3d point = hit.Value.Point, normal = hit.Value.Normal;
            if (AngleSnap && target.Def.Bounds.IsRound && System.Math.Abs(Vector3d.Dot(target.Rotation.InverseRotate(normal), Vector3d.UnitY)) < 0.5)
            {
                // Snap the azimuth around the parent's axis to 15° steps.
                Vector3d local = target.VesselToLocal(point);
                double az = System.Math.Atan2(local.Z, local.X);
                double step = 15 * MathUtil.Deg2Rad;
                az = System.Math.Round(az / step) * step;
                Vector3d dirLocal = new(System.Math.Cos(az), 0, System.Math.Sin(az));
                Vector3d from = target.LocalToVessel(new Vector3d(0, local.Y, 0) + dirLocal * (target.Def.Bounds.Radius + 5));
                PartRayHit? snapped = Assembly.RaycastPart(target, from, -target.DirectionToVessel(dirLocal));
                if (snapped.HasValue)
                {
                    point = snapped.Value.Point;
                    normal = snapped.Value.Normal;
                }
            }
            AttachResult sr = Assembly.ValidateSurface(target, def);
            c.Surface = true;
            c.Valid = sr.Ok;
            c.Reason = sr.Reason;
            Placement basePl = Assembly.SurfacePlacement(def, point, normal, _spin);
            foreach (Placement pl in Assembly.SymmetryPlacements(target, basePl, Symmetry))
                c.Copies.Add((target.Id, PartInstance.SurfaceNode, PartInstance.SurfaceNode, pl));
            ValidateOverlaps(c);
            return c;
        }

        private void ValidateOverlaps(Candidate c)
        {
            if (!c.Valid) return;
            PartDefinition def = _held.Parts[0].Def;
            bool skip = def.HasModule<JettisonModuleDef>();
            foreach (var (parentId, _, _, pl) in c.Copies)
            {
                var ignore = new HashSet<int> { parentId };
                PartInstance parent = Design.Get(parentId);
                // Parts sharing the parent's symmetry ring are allowed to touch.
                foreach (PartInstance p in Design.Parts.Where(p => p.Def.HasModule<JettisonModuleDef>())) ignore.Add(p.Id);
                if (parent != null && parent.Parent >= 0) ignore.Add(parent.Parent);
                if (skip) continue;
                AttachResult r = Assembly.ValidateOverlap(Design, def, pl, ignore);
                if (!r.Ok)
                {
                    c.Valid = false;
                    c.Reason = r.Reason;
                    return;
                }
            }
        }

        private void TryPlace()
        {
            if (_candidate == null) return;
            if (!_candidate.Valid)
            {
                _ui.ShowHint("Cannot attach: " + _candidate.Reason);
                return;
            }
            if (_held.IsNew || !_undo.Any()) Snapshot();
            int copies = _candidate.Copies.Count;
            // One symmetry group per held part (so counterparts of every sub-part stay linked).
            int[] groups = new int[_held.Parts.Count];
            if (copies > 1)
                for (int i = 0; i < groups.Length; i++) groups[i] = Design.NextSymmetryGroup++;
            foreach (var (parentId, parentNode, selfNode, pl) in _candidate.Copies)
            {
                var idMap = new Dictionary<int, int>();
                for (int i = 0; i < _held.Parts.Count; i++)
                {
                    PartInstance src = _held.Parts[i];
                    PartInstance p = src.Clone();
                    p.Def = src.Def;
                    p.Id = Design.NextId++;
                    idMap[src.Id] = p.Id;
                    p.Position = pl.Position + pl.Rotation.Rotate(src.Position);
                    p.Rotation = (pl.Rotation * src.Rotation).Normalized();
                    p.SymmetryGroup = copies > 1 ? groups[i] : (i == 0 ? 0 : src.SymmetryGroup);
                    p.State = null;
                    if (i == 0)
                    {
                        p.Parent = parentId;
                        p.ParentNode = parentNode;
                        p.SelfNode = selfNode;
                    }
                    else
                    {
                        p.Parent = idMap.TryGetValue(src.Parent, out int np) ? np : -1;
                    }
                    if (p.Resources.Count == 0) p.InitialiseResources();
                    if (_manualStaging && Staging.IsStageable(p)) p.Stage = Design.StageCount;
                    Design.Parts.Add(p);
                }
                // Fix fuel-line targets inside the copied assembly and aim new ducts.
                foreach (PartInstance p in Design.Parts.Where(x => idMap.ContainsValue(x.Id)))
                {
                    if (p.FuelLineTarget >= 0 && idMap.TryGetValue(p.FuelLineTarget, out int nt)) p.FuelLineTarget = nt;
                    if (p.Def.GetModule<FuelLineModuleDef>() is { } fl) AimFuelLine(p, fl);
                }
            }
            if (_candidate.AsRoot) Design.Parts.Sort((a, b) => (a.Parent < 0 ? 0 : 1).CompareTo(b.Parent < 0 ? 0 : 1));
            bool keepHolding = Input.IsKeyPressed(Key.Shift) && _held.Parts.Count == 1 && _held.IsNew;
            PartDefinition again = _held.Parts[0].Def;
            _held = null;
            _candidate = null;
            ClearGhosts();
            OnChanged();
            if (keepHolding) StartPlacing(again);
        }

        /// <summary>A fuel duct feeds the first part found along its outward axis (+Z), within reach.</summary>
        private void AimFuelLine(PartInstance line, FuelLineModuleDef fl)
        {
            Vector3d origin = line.LocalToVessel(new Vector3d(0, 0, 0.15));
            Vector3d dir = line.DirectionToVessel(Vector3d.UnitZ);
            var ignore = new HashSet<int> { line.Id, line.Parent };
            PartRayHit? hit = Assembly.Raycast(Design, origin, dir, ignore);
            line.FuelLineTarget = hit.HasValue && hit.Value.Distance <= fl.Reach ? hit.Value.Part.Id : -1;
            if (line.FuelLineTarget < 0) _ui?.ShowHint("Fuel duct has no target: point it at a neighbouring tank.");
        }

        public void Launch()
        {
            if (Design.Parts.Count == 0)
            {
                _ui.ShowHint("Build something first!");
                return;
            }
            if (!Design.Parts.Any(p => p.Def.HasModule<CommandModuleDef>()))
                _ui.ShowHint("Warning: no command pod or probe core — the vessel will be uncontrollable.");
            CancelHold(true, false);
            GameRoot.Instance.CurrentDesign = Design;
            GameRoot.Instance.Launch(Design);
        }
    }
}
