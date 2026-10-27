using System.Collections.Generic;
using OSP.Core.Math;
using OSP.Core.Orbits;

namespace OSP.Core.Celestial
{
    /// <summary>
    /// A star, planet or moon. Bodies move on fixed Keplerian orbits around their parent ("on rails") and
    /// rotate about their +Y axis. Positions returned by this class are in the parent-centred, non-rotating
    /// simulation frame described in <see cref="Orbit"/>.
    /// </summary>
    public sealed class CelestialBody
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; init; } = "";
        /// <summary>Mean (datum) radius, m.</summary>
        public double Radius { get; }
        /// <summary>Gravitational parameter μ = GM, m³/s².</summary>
        public double Mu { get; }
        /// <summary>Sidereal rotation period, s. Positive rotates eastward (about +Y).</summary>
        public double RotationPeriod { get; }
        /// <summary>Rotation angle at t = 0, rad.</summary>
        public double InitialRotation { get; init; }
        public CelestialBody Parent { get; private set; }
        public Orbit Orbit { get; private set; }
        public Atmosphere Atmosphere { get; init; }
        public TerrainGenerator Terrain { get; private set; }
        public bool IsStar { get; init; }
        /// <summary>Map/scaled-space colour (linear RGB).</summary>
        public double[] Color { get; init; } = { 0.6, 0.6, 0.6 };
        /// <summary>Altitude limits (m) per rails time-warp level; see <see cref="Flight.TimeWarp"/>.</summary>
        public double[] WarpAltitudeLimits { get; init; }
        public List<CelestialBody> Children { get; } = new();

        /// <summary>Sphere-of-influence radius, m (Laplace: a·(m/M)^(2/5)). Infinite for the root body.</summary>
        public double SphereOfInfluence { get; private set; } = double.PositiveInfinity;

        public double Mass => Mu / PhysicsConstants.G;
        public double SurfaceGravity => Mu / (Radius * Radius);
        public double AngularVelocity => RotationPeriod == 0 ? 0 : MathUtil.TwoPi / RotationPeriod;
        public Vector3d AngularVelocityVector => new(0, AngularVelocity, 0);
        public bool HasAtmosphere => Atmosphere != null;
        public double AtmosphereHeight => Atmosphere?.Height ?? 0;

        public CelestialBody(string id, string name, double radius, double mu, double rotationPeriod)
        {
            Id = id;
            Name = name;
            Radius = radius;
            Mu = mu;
            RotationPeriod = rotationPeriod;
        }

        public void SetTerrain(TerrainSettings settings)
        {
            Terrain = settings == null ? null : new TerrainGenerator(settings, Radius);
        }

        public void SetParent(CelestialBody parent, Orbit orbit)
        {
            Parent = parent;
            Orbit = orbit;
            parent.Children.Add(this);
            double a = orbit.SemiMajorAxis;
            SphereOfInfluence = a * System.Math.Pow(Mu / parent.Mu, 0.4);
        }

        // ----- Kinematics ---------------------------------------------------------------------------

        public Vector3d PositionRelativeToParent(double time) => Orbit == null ? Vector3d.Zero : Orbit.PositionAt(time);
        public Vector3d VelocityRelativeToParent(double time) => Orbit == null ? Vector3d.Zero : Orbit.VelocityAt(time);

        /// <summary>Position relative to the root body (the star).</summary>
        public Vector3d AbsolutePosition(double time)
        {
            Vector3d p = Vector3d.Zero;
            for (CelestialBody b = this; b.Parent != null; b = b.Parent) p += b.PositionRelativeToParent(time);
            return p;
        }

        public Vector3d AbsoluteVelocity(double time)
        {
            Vector3d v = Vector3d.Zero;
            for (CelestialBody b = this; b.Parent != null; b = b.Parent) v += b.VelocityRelativeToParent(time);
            return v;
        }

        /// <summary>Position of this body relative to <paramref name="other"/>'s centre.</summary>
        public Vector3d PositionRelativeTo(CelestialBody other, double time) => AbsolutePosition(time) - other.AbsolutePosition(time);
        public Vector3d VelocityRelativeTo(CelestialBody other, double time) => AbsoluteVelocity(time) - other.AbsoluteVelocity(time);

        public double RotationAngle(double time) => InitialRotation + AngularVelocity * time;

        /// <summary>Rotation from body-fixed coordinates to the inertial (non-rotating) frame.</summary>
        public QuaternionD RotationAt(double time) => QuaternionD.FromAxisAngle(Vector3d.UnitY, RotationAngle(time));

        public Vector3d BodyFixedToInertial(Vector3d bodyFixed, double time) => RotationAt(time).Rotate(bodyFixed);
        public Vector3d InertialToBodyFixed(Vector3d inertial, double time) => RotationAt(time).InverseRotate(inertial);

        /// <summary>Velocity of the co-rotating surface/atmosphere at an inertial position.</summary>
        public Vector3d SurfaceVelocity(Vector3d inertialPosition) => Vector3d.Cross(AngularVelocityVector, inertialPosition);

        public double GravitationalAcceleration(double radius) => Mu / (radius * radius);

        public Vector3d GravityAt(Vector3d position)
        {
            double r2 = position.LengthSquared;
            double r = System.Math.Sqrt(r2);
            return position * (-Mu / (r2 * r));
        }

        // ----- Surface helpers ----------------------------------------------------------------------

        /// <summary>Body-fixed unit direction for latitude/longitude in degrees (east-positive longitude).</summary>
        public static Vector3d DirectionFromLatLon(double latDeg, double lonDeg)
        {
            double lat = latDeg * MathUtil.Deg2Rad, lon = lonDeg * MathUtil.Deg2Rad;
            // Longitude increases eastward, i.e. in the direction of rotation (+X towards -Z).
            return new Vector3d(System.Math.Cos(lat) * System.Math.Cos(lon), System.Math.Sin(lat), -System.Math.Cos(lat) * System.Math.Sin(lon));
        }

        public static void LatLonFromDirection(Vector3d bodyFixed, out double latDeg, out double lonDeg)
        {
            Vector3d d = bodyFixed.Normalized();
            latDeg = System.Math.Asin(MathUtil.Clamp(d.Y, -1, 1)) * MathUtil.Rad2Deg;
            lonDeg = System.Math.Atan2(-d.Z, d.X) * MathUtil.Rad2Deg;
        }

        /// <summary>Terrain/ocean surface height (m above datum) under a body-fixed direction.</summary>
        public double SurfaceHeight(Vector3d bodyFixedDirection) => Terrain?.SurfaceHeight(bodyFixedDirection.Normalized()) ?? 0;

        /// <summary>Height of the terrain below an inertial position at a given time (m above datum).</summary>
        public double SurfaceHeightBelow(Vector3d inertialPosition, double time) =>
            SurfaceHeight(InertialToBodyFixed(inertialPosition, time));

        public double AltitudeAboveDatum(Vector3d inertialPosition) => inertialPosition.Length - Radius;

        public double AltitudeAboveTerrain(Vector3d inertialPosition, double time) =>
            inertialPosition.Length - Radius - SurfaceHeightBelow(inertialPosition, time);

        public override string ToString() => Name;
    }
}
