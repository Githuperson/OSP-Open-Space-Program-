using OSP.Core.Mathx;

namespace OSP.Core.Flight;

public enum SasMode
{
    StabilityAssist = 0, Prograde, Retrograde, Normal, Antinormal, RadialOut, RadialIn, Maneuver,
}

public enum SpeedReference { Auto = 0, Surface, Orbit }

/// <summary>Pilot / autopilot inputs for one craft. Axis conventions are documented in docs/PHYSICS.md:
/// Pitch +1 = nose toward craft +Z (heading), Yaw +1 = nose toward right, Roll +1 = clockwise seen from behind.</summary>
public sealed class ControlState
{
    public double Throttle { get; set; }
    public double Pitch { get; set; }
    public double Yaw { get; set; }
    public double Roll { get; set; }
    /// <summary>RCS translation request as a force direction in craft axes (X = right-hand side, Y = along the nose, Z = heading), each -1..1.</summary>
    public Vec3d Translation { get; set; }
    public bool RcsEnabled { get; set; }
    public bool SasEnabled { get; set; } = true;
    public SasMode Sas { get; set; } = SasMode.StabilityAssist;
    public SpeedReference Reference { get; set; } = SpeedReference.Auto;
    /// <summary>Orientation (craft -> inertial) held by stability assist.</summary>
    public QuatD HoldOrientation { get; set; } = QuatD.Identity;
    public bool HoldValid { get; set; }
    /// <summary>Burn direction (inertial, unit) used when SAS mode is Maneuver.</summary>
    public Vec3d ManeuverDirection { get; set; }

    public bool HasManualRotation => Math.Abs(Pitch) + Math.Abs(Yaw) + Math.Abs(Roll) > 1e-6;
}
