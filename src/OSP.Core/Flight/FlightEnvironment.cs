using OSP.Core.Bodies;
using OSP.Core.Mathx;

namespace OSP.Core.Flight;

/// <summary>Everything the physics needs to know about the surroundings of a craft at one instant.</summary>
public struct FlightEnvironment
{
    public double Time;
    public CelestialBody Body;
    public Vec3d Position;              // craft CoM, body-centred inertial
    public Vec3d Velocity;
    public double Radius;               // |Position|
    public double AltitudeSeaLevel;     // above reference radius
    public double AltitudeTerrain;      // above terrain / ocean surface directly below
    public Vec3d Up;
    public Vec3d AirVelocity;           // velocity of the (co-rotating) air at the craft position
    public Vec3d SurfaceVelocity;       // craft velocity relative to the surface / air
    public double PressurePa;
    public double PressureAtm;
    public double Density;
    public double SpeedOfSound;
    public Vec3d SunDirection;          // inertial unit vector from craft to star
    public double SolarFluxRatio;       // flux / reference flux at this distance
    public bool InShadow;

    public static FlightEnvironment Compute(StarSystem system, CelestialBody body, double time, Vec3d pos, Vec3d vel, bool needTerrain = true)
    {
        var e = new FlightEnvironment { Time = time, Body = body, Position = pos, Velocity = vel };
        e.Radius = pos.Length;
        e.Up = e.Radius > 0 ? pos / e.Radius : Vec3d.UnitY;
        e.AltitudeSeaLevel = e.Radius - body.Radius;
        e.AltitudeTerrain = needTerrain ? body.AltitudeAboveTerrain(pos, time) : e.AltitudeSeaLevel;
        e.AirVelocity = body.SurfaceVelocityAt(pos);
        e.SurfaceVelocity = vel - e.AirVelocity;
        var atm = body.Atmosphere;
        if (atm != null)
        {
            // Atmospheric properties depend on altitude above the reference radius.
            double h = Math.Max(0, e.AltitudeSeaLevel);
            e.PressurePa = atm.Pressure(h);
            e.PressureAtm = e.PressurePa / 101325.0;
            e.Density = atm.Density(h);
            e.SpeedOfSound = atm.SpeedOfSound(h);
        }
        else { e.SpeedOfSound = 300; }

        // Solar illumination: direction to the star in the body-centred inertial frame.
        var (bodyRoot, _) = system.StateInRoot(body, time);
        var craftRoot = bodyRoot + pos;
        double dStar = craftRoot.Length;
        e.SunDirection = dStar > 0 ? -craftRoot / dStar : Vec3d.UnitX;
        e.SolarFluxRatio = system.SolarFluxAtRootPosition(craftRoot) / Math.Max(1e-9, system.Root.SolarFluxAtReference);
        e.InShadow = IsOccluded(system, body, time, pos, e.SunDirection);
        return e;
    }

    private static bool IsOccluded(StarSystem system, CelestialBody body, double time, Vec3d pos, Vec3d sunDir)
    {
        if (body.Parent == null) return false;
        if (RayHitsSphere(pos, sunDir, Vec3d.Zero, body.Radius)) return true;
        // Parent planet as an occluder when orbiting its moon.
        if (body.Parent.Parent != null)
        {
            var (rRel, _) = system.StateRelativeToParent(body, time);
            if (RayHitsSphere(pos, sunDir, -rRel, body.Parent.Radius)) return true;
        }
        return false;
    }

    private static bool RayHitsSphere(Vec3d origin, Vec3d dir, Vec3d center, double radius)
    {
        var oc = origin - center;
        double b = Vec3d.Dot(oc, dir);
        double c = oc.LengthSquared - radius * radius;
        if (c <= 0) return true;               // inside the sphere
        if (b > 0) return false;                // sphere is behind
        return b * b - c >= 0;
    }
}
