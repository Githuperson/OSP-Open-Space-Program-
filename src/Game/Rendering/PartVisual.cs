using System.Collections.Generic;
using Godot;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// The 3D representation of one part: a static mesh plus pivot nodes for animated groups (landing legs, solar
    /// arrays, parachute canopies, gimballing nozzles, control-surface flaps and jettisonable shells).
    /// </summary>
    public partial class PartVisual : Node3D
    {
        public PartDefinition Def { get; private set; }
        public int PartId { get; private set; }

        private readonly List<MeshInstance3D> _meshes = new();
        private readonly Dictionary<string, Node3D> _groups = new();
        private Material _overlay;

        public static PartVisual Create(PartDefinition def, int partId)
        {
            var v = new PartVisual { Def = def, PartId = partId, Name = $"Part{partId}_{def.Id}" };
            PartMeshSet set = PartMeshFactory.Get(def);
            if (set.Static != null)
            {
                var mi = new MeshInstance3D { Mesh = set.Static, Name = "Static" };
                v.AddChild(mi);
                v._meshes.Add(mi);
            }
            foreach (var (name, (mesh, pivot)) in set.Groups)
            {
                var pivotNode = new Node3D { Name = "Group_" + name, Position = pivot };
                var mi = new MeshInstance3D { Mesh = mesh, Name = "Mesh" };
                pivotNode.AddChild(mi);
                v.AddChild(pivotNode);
                v._groups[name] = pivotNode;
                v._meshes.Add(mi);
            }
            return v;
        }

        public Node3D Group(string name) => _groups.TryGetValue(name, out Node3D n) ? n : null;

        public void SetOverlay(Material overlay)
        {
            if (_overlay == overlay) return;
            _overlay = overlay;
            foreach (MeshInstance3D m in _meshes) m.MaterialOverlay = overlay;
        }

        public void SetCastShadows(bool on)
        {
            foreach (MeshInstance3D m in _meshes)
                m.CastShadow = on ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }

        /// <summary>Applies module state (deploy progress, gimbal, jettison) to the animated groups.</summary>
        public void ApplyState(PartFlightState state, Vector3 gimbalTilt = default)
        {
            if (state == null) return;
            foreach (PartModuleDef m in Def.Modules)
            {
                switch (m)
                {
                    case LandingLegModuleDef leg when leg.Animation != null:
                        Rotate(leg.Animation, state.Deploy);
                        break;
                    case SolarPanelModuleDef sp when sp.Animation != null:
                        if (Group(sp.Animation.Group) is { } panel) panel.Visible = !state.Broken;
                        ScaleGroup(sp.Animation, sp.Deployable ? state.Deploy : 1);
                        break;
                    case ParachuteModuleDef chute when chute.Animation != null:
                    {
                        Node3D g = Group(chute.Animation.Group);
                        if (g == null) break;
                        bool open = state.Parachute is ParachuteState.SemiDeployed or ParachuteState.Deployed;
                        g.Visible = open;
                        if (open)
                        {
                            float d = (float)System.Math.Max(state.Deploy, chute.SemiDeployedFraction);
                            // A streamer while semi-deployed: narrow but long.
                            float lateral = Mathf.Max(0.02f, d);
                            float vertical = Mathf.Max(0.05f, Mathf.Sqrt(d));
                            g.Scale = new Vector3(lateral, vertical, lateral);
                        }
                        break;
                    }
                    case JettisonModuleDef jet when jet.Group != null:
                        if (Group(jet.Group) is { } shell) shell.Visible = !state.Jettisoned;
                        break;
                    case EngineModuleDef eng when eng.GimbalGroup != null:
                        if (Group(eng.GimbalGroup) is { } gim)
                            gim.Basis = gimbalTilt.LengthSquared() > 1e-8f ? new Basis(gimbalTilt.Normalized(), gimbalTilt.Length()) : Basis.Identity;
                        break;
                    case LiftingSurfaceModuleDef wing when wing.IsControlSurface && wing.DeflectionGroup != null:
                        if (Group(wing.DeflectionGroup) is { } flap)
                        {
                            float angle = (float)(MathUtil.Clamp(state.Deploy, -1, 1) * wing.MaxDeflection * MathUtil.Deg2Rad);
                            flap.Basis = new Basis(wing.HingeAxis.ToGodot().Normalized(), angle);
                        }
                        break;
                }
            }
        }

        /// <summary>Shows a part in its fully deployed configuration (editor preview of legs etc.).</summary>
        public void ApplyEditorPose()
        {
            foreach (PartModuleDef m in Def.Modules)
            {
                switch (m)
                {
                    case LandingLegModuleDef leg when leg.Animation != null:
                        Rotate(leg.Animation, leg.StartDeployed ? 1 : 0);
                        break;
                    case SolarPanelModuleDef sp when sp.Animation != null:
                        ScaleGroup(sp.Animation, sp.Deployable ? 0 : 1);
                        break;
                    case ParachuteModuleDef chute when chute.Animation != null:
                        if (Group(chute.Animation.Group) is { } g) g.Visible = false;
                        break;
                }
            }
        }

        private void Rotate(AnimationDef a, double t)
        {
            Node3D g = Group(a.Group);
            if (g == null) return;
            double angle = MathUtil.Lerp(a.AngleStowed, a.AngleDeployed, MathUtil.Clamp01(t)) * MathUtil.Deg2Rad;
            g.Basis = new Basis(a.Axis.ToGodot().Normalized(), (float)angle);
        }

        private void ScaleGroup(AnimationDef a, double t)
        {
            Node3D g = Group(a.Group);
            if (g == null) return;
            Vector3d s = Vector3d.Lerp(a.ScaleStowed, a.ScaleDeployed, MathUtil.Clamp01(t));
            g.Scale = new Vector3((float)System.Math.Max(s.X, 0.01), (float)System.Math.Max(s.Y, 0.01), (float)System.Math.Max(s.Z, 0.01));
        }
    }
}
