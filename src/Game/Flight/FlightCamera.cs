using Godot;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Game.Rendering;

namespace OSP.Game.Flight
{
    public enum CameraMode
    {
        /// <summary>Orbit the vessel; "up" follows the local horizon (radial direction).</summary>
        Auto,
        /// <summary>Orbit the vessel; frame rotates with the vessel.</summary>
        Chase,
        /// <summary>Orbit the vessel in a fixed inertial frame.</summary>
        Free,
    }

    /// <summary>
    /// Orbit camera around the active vessel. The camera's position is tracked in double precision relative to the
    /// vessel's CoM (which is the floating origin), so it never jitters even far from the planet.
    /// Controls: right-drag to rotate, wheel to zoom, V to change mode, Backspace to reset.
    /// </summary>
    public partial class FlightCamera : Camera3D
    {
        public CameraMode Mode { get; set; } = CameraMode.Auto;
        public double Distance { get; private set; } = 25;
        public double Yaw { get; private set; } = 0.6;
        public double Pitch { get; private set; } = 0.15;
        /// <summary>Camera position relative to the vessel CoM (m), inertial axes.</summary>
        public Vector3d Offset { get; private set; }
        public Basis WorldBasis { get; private set; } = Basis.Identity;

        private double _targetDistance = 25;
        private bool _dragging;
        private QuaternionD _frame = QuaternionD.Identity;
        private QuaternionD _smoothedFrame = QuaternionD.Identity;
        private bool _hasFrame;
        public float Sensitivity { get; set; } = 1f;

        public override void _Ready()
        {
            Fov = 60;
            Near = 0.05f;
            Far = 3.0e4f;
            Current = true;
        }

        public void ResetView(double vesselSize)
        {
            _targetDistance = System.Math.Clamp(vesselSize * 2.2 + 6, 6, 400);
            Yaw = 0.6;
            Pitch = 0.15;
        }

        public void HandleInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Right) _dragging = mb.Pressed;
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) _targetDistance = System.Math.Max(2.0, _targetDistance * 0.88);
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) _targetDistance = System.Math.Min(80000, _targetDistance * 1.14);
            }
            else if (ev is InputEventMouseMotion mm && _dragging)
            {
                Yaw -= mm.Relative.X * 0.005 * Sensitivity;
                Pitch = MathUtil.Clamp(Pitch + mm.Relative.Y * 0.005 * Sensitivity, -1.5, 1.5);
            }
        }

        public void CycleMode()
        {
            Mode = (CameraMode)(((int)Mode + 1) % 3);
            _hasFrame = false;
        }

        /// <summary>Recomputes the camera for the vessel. Call once per frame after the simulation step.</summary>
        public void UpdateCamera(Vessel v, double delta)
        {
            Distance += (_targetDistance - Distance) * (1 - System.Math.Exp(-delta * 8));
            Vector3d up = v.Position.Normalized();
            QuaternionD frame;
            switch (Mode)
            {
                case CameraMode.Chase:
                    frame = v.Rotation;
                    break;
                case CameraMode.Free:
                    frame = QuaternionD.Identity;
                    break;
                default:
                {
                    // Horizon frame: +Y = radial up, -Z = north (projected pole), like standing on the surface.
                    Vector3d north = Vector3d.UnitY.ProjectOnPlane(up);
                    if (north.LengthSquared < 1e-6) north = Vector3d.UnitZ.ProjectOnPlane(up);
                    frame = QuaternionD.LookRotation(north.Normalized(), up);
                    break;
                }
            }
            if (!_hasFrame)
            {
                _smoothedFrame = frame;
                _hasFrame = true;
            }
            else
            {
                _smoothedFrame = QuaternionD.Slerp(_smoothedFrame, frame, 1 - System.Math.Exp(-delta * 6));
            }
            _frame = _smoothedFrame;

            // Spherical offset in the frame: yaw about +Y, pitch towards +Y.
            Vector3d local = new(System.Math.Sin(Yaw) * System.Math.Cos(Pitch), System.Math.Sin(Pitch), System.Math.Cos(Yaw) * System.Math.Cos(Pitch));
            Offset = _frame.Rotate(local * Distance);
            Vector3d camUp = _frame.Rotate(Vector3d.UnitY);
            QuaternionD look = QuaternionD.LookRotation(-Offset.Normalized(), camUp);
            WorldBasis = new Basis(look.ToGodot());
            Transform = new Transform3D(WorldBasis, Offset.ToGodot());
            // Keep the near plane tight for close-ups and relaxed when zoomed out. Godot needs far/near ≲ 1e6 to build
            // frustums reliably, so the far plane follows the near plane (scaled space draws anything farther).
            Near = (float)System.Math.Clamp(Distance * 0.004, 0.05, 20);
            Far = Near * 6.0e5f;
        }
    }
}
