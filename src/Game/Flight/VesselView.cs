using System.Collections.Generic;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Vehicles;
using OSP.Game.Rendering;

namespace OSP.Game.Flight
{
    /// <summary>
    /// Renders one simulated vessel. The node's origin is the vessel's centre of mass; parts are placed at
    /// (part position − CoM) in the vessel frame. Rebuilt when the part tree changes.
    /// </summary>
    public partial class VesselView : Node3D
    {
        public Vessel Vessel { get; private set; }
        private readonly Dictionary<int, PartVisual> _parts = new();
        private readonly Dictionary<int, EngineFx> _engines = new();
        private readonly List<(PartInstance line, MeshInstance3D mesh)> _fuelLines = new();
        private int _topologyVersion = -1;

        public static VesselView Create(Vessel v) => new() { Vessel = v, Name = "Vessel_" + v.Id };

        private void Rebuild()
        {
            foreach (Node c in GetChildren()) c.QueueFree();
            _parts.Clear();
            _engines.Clear();
            _fuelLines.Clear();
            foreach (PartInstance p in Vessel.Parts)
            {
                PartVisual pv = PartVisual.Create(p.Def, p.Id);
                AddChild(pv);
                _parts[p.Id] = pv;
                EngineModuleDef e = p.Def.GetModule<EngineModuleDef>();
                if (e != null)
                {
                    EngineFx fx = EngineFx.Create(e);
                    fx.Position = e.ThrustPoint.ToGodot();
                    Vector3 exhaust = e.ExhaustDirection.ToGodot().Normalized();
                    fx.Basis = new Basis(new Quaternion(Vector3.Down, exhaust));
                    pv.AddChild(fx);
                    _engines[p.Id] = fx;
                }
                if (p.Def.HasModule<FuelLineModuleDef>() && p.FuelLineTarget >= 0)
                {
                    var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 1, RadialSegments = 8 }, MaterialOverride = MaterialLibrary.Get("darkgray") };
                    AddChild(mi);
                    _fuelLines.Add((p, mi));
                }
            }
            _topologyVersion = Vessel.TopologyVersion;
        }

        /// <summary>Updates transforms and effects. <paramref name="offset"/> = vessel CoM − floating origin (m).</summary>
        public void Sync(Vector3d offset, float delta, double pressureAtm)
        {
            if (_topologyVersion != Vessel.TopologyVersion) Rebuild();
            Transform = new Transform3D(new Basis(Vessel.Rotation.ToGodot()), offset.ToGodot());
            Vector3d com = Vessel.CenterOfMassLocal;
            Vector3d rotCmd = Vessel.LastRotationCommand;
            foreach (PartInstance p in Vessel.Parts)
            {
                if (!_parts.TryGetValue(p.Id, out PartVisual pv)) continue;
                pv.Transform = new Transform3D(new Basis(p.Rotation.ToGodot()), (p.Position - com).ToGodot());
                Vector3 tilt = Vector3.Zero;
                EngineModuleDef e = p.Def.GetModule<EngineModuleDef>();
                if (e != null && e.GimbalRange > 0 && p.State.Activated)
                {
                    // Visual gimbal: tilt about the axis that produces the commanded torque.
                    Vector3d lever = p.LocalToVessel(e.ThrustPoint) - com;
                    Vector3d axisV = Vector3d.Cross(rotCmd, lever);
                    Vector3d axisLocal = p.Rotation.InverseRotate(Vector3d.Cross(-Vector3d.UnitY, axisV));
                    double mag = System.Math.Min(1, rotCmd.Length) * e.GimbalRange * MathUtil.Deg2Rad;
                    if (axisLocal.LengthSquared > 1e-12) tilt = (axisLocal.Normalized() * mag).ToGodot();
                }
                pv.ApplyState(p.State, tilt);
                if (_engines.TryGetValue(p.Id, out EngineFx fx))
                {
                    float throttle = p.State.Activated && !p.State.Shutdown ? (float)p.State.EngineThrottle : 0;
                    if (Vessel.Telemetry.FlameOut && throttle < 0.01f) throttle = 0;
                    fx.UpdateFx(throttle, (float)pressureAtm, delta);
                }
            }
            foreach (var (line, mesh) in _fuelLines)
            {
                PartInstance target = Vessel.GetPart(line.FuelLineTarget);
                if (target == null) { mesh.Visible = false; continue; }
                Vector3 a = (line.Position - com).ToGodot();
                Vector3 b = (target.Position - com).ToGodot();
                Vector3 dir = b - a;
                // Stop the duct at the target's surface.
                float len = Mathf.Max(0.05f, dir.Length() - (float)target.Def.Bounds.Radius);
                Vector3 d = dir.Normalized();
                mesh.Visible = true;
                mesh.Transform = new Transform3D(new Basis(new Quaternion(Vector3.Up, d)).Scaled(new Vector3(1, len, 1)), a + d * len / 2);
            }
        }

        public void SetShadows(bool on)
        {
            foreach (PartVisual pv in _parts.Values) pv.SetCastShadows(on);
        }
    }
}
