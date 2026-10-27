using OSP.Core.Math;
using OSP.Core.Orbits;

namespace OSP.Core.Flight
{
    /// <summary>
    /// Stability Assist (SAS). A rate-limited attitude controller: the attitude error is turned into a desired
    /// angular velocity using the maximum safe rate for the available angular acceleration (ω = √(2·α·θ)), and
    /// the rate error is converted to a normalised torque command for each vessel axis.
    /// Outputs are in the vessel frame: X = pitch, Y = roll, Z = yaw, each in [-1, 1].
    /// </summary>
    public sealed class Autopilot
    {
        private QuaternionD? _holdAttitude;
        private bool _wasPiloting;

        public const double MaxRate = 0.6;      // rad/s
        public const double RateGain = 4.0;     // 1/s : response of the rate loop
        public const double Deadband = 0.0015;  // rad

        public void Reset()
        {
            _holdAttitude = null;
        }

        /// <summary>Target nose direction for the current mode, or null for stability hold.</summary>
        public static Vector3d? TargetDirection(Vessel v, SasMode mode, Vector3d referenceVelocity, Vector3d? targetPosition, double time)
        {
            Vector3d r = v.Position;
            Vector3d vel = referenceVelocity;
            if (vel.LengthSquared < 1e-4 && mode is SasMode.Prograde or SasMode.Retrograde) return null;
            ManeuverNode.OrbitalFrame(r, v.Velocity, out _, out Vector3d normal, out _);
            Vector3d radialOut = r.Normalized();
            switch (mode)
            {
                case SasMode.Prograde: return vel.Normalized();
                case SasMode.Retrograde: return -vel.Normalized();
                case SasMode.Normal: return normal;
                case SasMode.Antinormal: return -normal;
                case SasMode.RadialOut: return radialOut;
                case SasMode.RadialIn: return -radialOut;
                case SasMode.Maneuver:
                {
                    if (v.Nodes.Count == 0) return null;
                    Vector3d remaining = ManeuverRemaining(v, time);
                    return remaining.LengthSquared > 1e-6 ? remaining.Normalized() : (Vector3d?)null;
                }
                case SasMode.Target:
                    return targetPosition.HasValue ? (targetPosition.Value - r).Normalized() : (Vector3d?)null;
                case SasMode.AntiTarget:
                    return targetPosition.HasValue ? (r - targetPosition.Value).Normalized() : (Vector3d?)null;
                default:
                    return null;
            }
        }

        /// <summary>Remaining Δv vector (inertial) for the first manoeuvre node.</summary>
        public static Vector3d ManeuverRemaining(Vessel v, double time)
        {
            if (v.Nodes.Count == 0) return Vector3d.Zero;
            ManeuverNode node = v.Nodes[0];
            Orbit orbit = v.CurrentOrbit(time);
            if (orbit == null) return Vector3d.Zero;
            orbit.GetStateAt(System.Math.Max(node.Time, time), out Vector3d p, out Vector3d vel);
            Vector3d planned = node.WorldDeltaV(p, vel);
            Vector3d done = v.AccumulatedThrustDeltaV - node.BurnBaseline;
            return planned - done;
        }

        /// <summary>
        /// Computes the rotation command. <paramref name="authority"/> is the maximum angular acceleration about
        /// each vessel axis (rad/s²). Returns zero when SAS is off.
        /// </summary>
        public Vector3d Update(Vessel v, Vector3d authority, Vector3d referenceVelocity, Vector3d? targetPosition, double time)
        {
            VesselControl c = v.Control;
            if (!c.SasEnabled)
            {
                _holdAttitude = null;
                return Vector3d.Zero;
            }

            Vector3d omegaLocal = v.Rotation.InverseRotate(v.AngularVelocity);
            Vector3d errorLocal;

            Vector3d? dir = c.SasMode == SasMode.Stability ? null : TargetDirection(v, c.SasMode, referenceVelocity, targetPosition, time);
            if (dir.HasValue)
            {
                _holdAttitude = null;
                Vector3d tgt = v.Rotation.InverseRotate(dir.Value);
                Vector3d axis = Vector3d.Cross(Vector3d.UnitY, tgt);
                double angle = System.Math.Atan2(axis.Length, Vector3d.Dot(Vector3d.UnitY, tgt));
                errorLocal = axis.LengthSquared > 1e-18 ? axis.Normalized() * angle : Vector3d.Zero;
                if (angle > System.Math.PI - 1e-3) errorLocal = Vector3d.UnitX * angle; // exactly opposite: pick pitch
            }
            else
            {
                bool piloting = c.HasPilotRotationInput;
                if (piloting || _holdAttitude == null || (_wasPiloting && omegaLocal.Length > 0.02))
                {
                    // Re-capture the hold attitude once the pilot lets go and rotation has settled.
                    _holdAttitude = v.Rotation;
                }
                _wasPiloting = piloting || (_wasPiloting && omegaLocal.Length > 0.02);
                QuaternionD err = (_holdAttitude.Value * v.Rotation.Inverse()).Normalized();
                if (err.W < 0) err = new QuaternionD(-err.X, -err.Y, -err.Z, -err.W);
                double s = System.Math.Sqrt(err.X * err.X + err.Y * err.Y + err.Z * err.Z);
                double angle = 2 * System.Math.Atan2(s, err.W);
                Vector3d axisWorld = s > 1e-12 ? new Vector3d(err.X, err.Y, err.Z) / s : Vector3d.Zero;
                errorLocal = v.Rotation.InverseRotate(axisWorld) * angle;
            }

            return RateControl(errorLocal, omegaLocal, authority);
        }

        /// <summary>Per-axis rate controller. Public for testing.</summary>
        public static Vector3d RateControl(Vector3d errorLocal, Vector3d omegaLocal, Vector3d authority)
        {
            double Axis(double err, double omega, double alpha)
            {
                if (alpha <= 1e-9) return 0;
                double mag = System.Math.Abs(err);
                double desired = mag < Deadband ? 0 : System.Math.Sign(err) * System.Math.Min(MaxRate, System.Math.Min(System.Math.Sqrt(2 * 0.6 * alpha * mag), 2.5 * mag));
                double u = (desired - omega) * RateGain / alpha;
                return MathUtil.Clamp(u, -1, 1);
            }
            return new Vector3d(
                Axis(errorLocal.X, omegaLocal.X, authority.X),
                Axis(errorLocal.Y, omegaLocal.Y, authority.Y),
                Axis(errorLocal.Z, omegaLocal.Z, authority.Z));
        }
    }
}
