using System.Text.Json.Serialization;
using OSP.Core.Mathx;
using OSP.Core.Orbits;

namespace OSP.Core.Bodies;

public sealed class OrbitDef
{
    public double SemiMajorAxis { get; set; }
    public double Eccentricity { get; set; }
    public double InclinationDeg { get; set; }
    public double LanDeg { get; set; }
    public double ArgPeriapsisDeg { get; set; }
    public double MeanAnomalyAtEpochDeg { get; set; }
}

public sealed class LaunchSiteDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double LatitudeDeg { get; set; }
    public double LongitudeDeg { get; set; }
    /// <summary>Radius (m) of the guaranteed-flat area; terrain blends back to natural over 3x this distance.</summary>
    public double FlatRadius { get; set; } = 1500;
    /// <summary>Height above the reference radius of the flat pad (m).</summary>
    public double Height { get; set; } = 8;
}

public sealed class TerrainDef
{
    public int Seed { get; set; } = 1;
    /// <summary>Peak-to-sea-level relief scale in metres.</summary>
    public double Amplitude { get; set; } = 6000;
    public double ContinentFrequency { get; set; } = 1.4;
    public double MountainFrequency { get; set; } = 6.0;
    public double DetailFrequency { get; set; } = 90.0;
    /// <summary>If true terrain below the reference radius is covered by a flat ocean surface.</summary>
    public bool HasOcean { get; set; }
    /// <summary>Shift applied to raw continent noise: positive = more land.</summary>
    public double LandBias { get; set; } = 0.0;
    /// <summary>Cratered (airless) style: adds pits and rims.</summary>
    public double CraterStrength { get; set; } = 0.0;
    public double[] ColorLow { get; set; } = { 0.25, 0.4, 0.2 };
    public double[] ColorMid { get; set; } = { 0.4, 0.35, 0.25 };
    public double[] ColorHigh { get; set; } = { 0.75, 0.75, 0.75 };
    public double[] ColorPolar { get; set; } = { 0.95, 0.97, 1.0 };
    public double[] ColorShore { get; set; } = { 0.8, 0.75, 0.5 };
    public double[] ColorOcean { get; set; } = { 0.04, 0.2, 0.45 };
    /// <summary>Latitude (deg) above which polar caps start; 90 disables.</summary>
    public double PolarLatitudeDeg { get; set; } = 90;
}

/// <summary>Data definition and runtime state accessors for a star, planet or moon.</summary>
public sealed class CelestialBody
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string Kind { get; set; } = "planet";               // star | planet | moon
    public string Description { get; set; } = "";

    /// <summary>Gravitational parameter GM (m^3/s^2).</summary>
    public double Mu { get; set; }
    /// <summary>Mean (sea-level) radius in metres.</summary>
    public double Radius { get; set; }
    /// <summary>Sidereal rotation period in seconds (0 or negative = non-rotating).</summary>
    public double RotationPeriod { get; set; }
    public double RotationPhaseDeg { get; set; }
    public OrbitDef? OrbitDef { get; set; }
    public AtmosphereDef? Atmosphere { get; set; }
    public TerrainDef? Terrain { get; set; }
    public List<LaunchSiteDef> LaunchSites { get; set; } = new();
    /// <summary>Linear RGB base colour for distant/scaled rendering.</summary>
    public double[] Color { get; set; } = { 0.6, 0.6, 0.6 };
    /// <summary>Star luminosity at 1 Astronomical-Unit-equivalent (W/m^2 at <see cref="StarSystem.ReferenceDistance"/>).</summary>
    public double SolarFluxAtReference { get; set; }

    // ---- runtime links filled in by StarSystem.Link() ---------------------------------------
    [JsonIgnore] public CelestialBody? Parent { get; internal set; }
    [JsonIgnore] public List<CelestialBody> Children { get; } = new();
    [JsonIgnore] public Orbit? Orbit { get; internal set; }
    [JsonIgnore] public double SphereOfInfluence { get; internal set; } = double.PositiveInfinity;
    [JsonIgnore] public TerrainModel? TerrainModel { get; internal set; }

    [JsonIgnore] public bool HasAtmosphere => Atmosphere != null;
    [JsonIgnore] public double SurfaceGravity => Mu / (Radius * Radius);
    [JsonIgnore] public double AngularSpeed => RotationPeriod > 0 ? MathUtil.Tau / RotationPeriod : 0.0;
    [JsonIgnore] public Vec3d AngularVelocity => new(0, AngularSpeed, 0);
    [JsonIgnore] public double EscapeVelocityAtSurface => OrbitMath.EscapeSpeed(Mu, Radius);

    /// <summary>Rotation angle (rad) of the body-fixed frame about +Y at universal time t.</summary>
    public double RotationAngleAt(double t) =>
        RotationPhaseDeg * MathUtil.Deg2Rad + AngularSpeed * t;

    public QuatD RotationAt(double t) => QuatD.FromAxisAngle(Vec3d.UnitY, RotationAngleAt(t));

    public Vec3d LocalToInertial(Vec3d local, double t) => RotationAt(t).Rotate(local);
    public Vec3d InertialToLocal(Vec3d inertial, double t) => RotationAt(t).InverseRotate(inertial);

    /// <summary>Velocity of the (co-rotating) surface/atmosphere at an inertial position: omega x r.</summary>
    public Vec3d SurfaceVelocityAt(Vec3d inertialPos) => Vec3d.Cross(AngularVelocity, inertialPos);

    /// <summary>Unit direction in the body-fixed frame for a latitude/longitude (east = direction of rotation).</summary>
    public static Vec3d DirectionFromLatLon(double latRad, double lonRad) =>
        new(Math.Cos(latRad) * Math.Cos(lonRad), Math.Sin(latRad), -Math.Cos(latRad) * Math.Sin(lonRad));

    public static (double latRad, double lonRad) LatLonFromDirection(Vec3d dirLocal)
    {
        var d = dirLocal.Normalized;
        return (Math.Asin(Math.Clamp(d.Y, -1, 1)), Math.Atan2(-d.Z, d.X));
    }

    /// <summary>Terrain/ocean surface height above the reference radius along a body-fixed direction (collision surface).</summary>
    public double SurfaceHeightAt(Vec3d dirLocal) => TerrainModel?.SurfaceHeight(dirLocal) ?? 0.0;

    /// <summary>Altitude above the reference radius ("sea level") of an inertial position.</summary>
    public double AltitudeAboveSeaLevel(Vec3d posInertial) => posInertial.Length - Radius;

    /// <summary>Altitude above the terrain/ocean surface directly below an inertial position at time t.</summary>
    public double AltitudeAboveTerrain(Vec3d posInertial, double t)
    {
        var local = InertialToLocal(posInertial, t);
        return local.Length - (Radius + SurfaceHeightAt(local));
    }
}
