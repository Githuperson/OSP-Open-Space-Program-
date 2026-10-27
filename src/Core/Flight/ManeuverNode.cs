using OSP.Core.Math;

namespace OSP.Core.Flight
{
    /// <summary>
    /// A planned impulsive burn. Delta-v components are in the orbital frame at the node time:
    /// prograde (along velocity), normal (along orbital angular momentum), radial (prograde × normal, outward).
    /// </summary>
    public sealed class ManeuverNode
    {
        public double Time { get; set; }
        public double Prograde { get; set; }
        public double Normal { get; set; }
        public double Radial { get; set; }

        /// <summary>Thrust Δv accumulated on the vessel when the node was created (for remaining-Δv tracking).</summary>
        public Vector3d BurnBaseline { get; set; }

        public double DeltaVMagnitude => new Vector3d(Prograde, Normal, Radial).Length;

        /// <summary>Converts the node's components to an inertial vector given the orbital state at the node.</summary>
        public Vector3d WorldDeltaV(Vector3d position, Vector3d velocity)
        {
            OrbitalFrame(position, velocity, out Vector3d pro, out Vector3d nor, out Vector3d rad);
            return pro * Prograde + nor * Normal + rad * Radial;
        }

        public static void OrbitalFrame(Vector3d position, Vector3d velocity, out Vector3d prograde, out Vector3d normal, out Vector3d radial)
        {
            prograde = velocity.Normalized();
            normal = Vector3d.Cross(position, velocity).Normalized();
            if (normal.LengthSquared < 0.5) normal = Vector3d.UnitY;
            radial = Vector3d.Cross(prograde, normal).Normalized(); // points away from the body for circular orbits
        }

        public ManeuverNode Clone() => (ManeuverNode)MemberwiseClone();
    }
}
