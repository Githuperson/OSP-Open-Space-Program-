using OSP.Core.Math;

namespace OSP.Core.Flight
{
    public enum SasMode
    {
        Stability,
        Prograde,
        Retrograde,
        Normal,
        Antinormal,
        RadialOut,
        RadialIn,
        Maneuver,
        Target,
        AntiTarget,
    }

    /// <summary>Which velocity frame the navball and prograde/retrograde SAS modes use.</summary>
    public enum SpeedReference
    {
        Orbit,
        Surface,
        Target,
    }

    /// <summary>Pilot (or autopilot) control inputs for a vessel. Rotation axes are in the vessel frame.</summary>
    public sealed class VesselControl
    {
        /// <summary>Main throttle 0..1.</summary>
        public double Throttle { get; set; }
        /// <summary>Pitch input -1..1 (rotation about vessel X).</summary>
        public double Pitch { get; set; }
        /// <summary>Yaw input -1..1 (rotation about vessel Z).</summary>
        public double Yaw { get; set; }
        /// <summary>Roll input -1..1 (rotation about vessel Y).</summary>
        public double Roll { get; set; }
        /// <summary>RCS translation input in the vessel frame (-1..1 per axis).</summary>
        public Vector3d Translation { get; set; }
        public bool SasEnabled { get; set; }
        public SasMode SasMode { get; set; } = SasMode.Stability;
        public bool RcsEnabled { get; set; }
        /// <summary>Fine control halves rotation authority for precise manoeuvres.</summary>
        public bool PrecisionMode { get; set; }
        public SpeedReference SpeedReference { get; set; } = SpeedReference.Surface;
        /// <summary>Automatically pick surface/orbit reference by altitude.</summary>
        public bool AutoSpeedReference { get; set; } = true;

        /// <summary>Rotation command (pitch, roll, yaw) as a vector about vessel (X, Y, Z).</summary>
        public Vector3d RotationVector => new(Pitch, Roll, Yaw);

        public bool HasPilotRotationInput => System.Math.Abs(Pitch) > 1e-3 || System.Math.Abs(Yaw) > 1e-3 || System.Math.Abs(Roll) > 1e-3;
    }
}
