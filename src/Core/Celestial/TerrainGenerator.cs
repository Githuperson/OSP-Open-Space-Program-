using System.Collections.Generic;
using OSP.Core.Math;

namespace OSP.Core.Celestial
{
    /// <summary>Data describing a body's procedural terrain. Distances in metres.</summary>
    public sealed class TerrainSettings
    {
        public int Seed { get; init; } = 1;
        /// <summary>Peak-to-mean amplitude of rolling hills (m).</summary>
        public double HillAmplitude { get; init; } = 600;
        /// <summary>Horizontal size of hill features (m).</summary>
        public double HillScale { get; init; } = 40000;
        /// <summary>Amplitude of ridged mountains (m).</summary>
        public double MountainAmplitude { get; init; } = 2500;
        public double MountainScale { get; init; } = 120000;
        /// <summary>Amplitude of continent-scale variation (m). With an ocean, negative values are sea floor.</summary>
        public double ContinentAmplitude { get; init; } = 1500;
        public double ContinentScale { get; init; } = 400000;
        /// <summary>Constant offset added to the height (m).</summary>
        public double BaseOffset { get; init; } = 200;
        /// <summary>Crater density for airless bodies (0 = none).</summary>
        public double CraterDepth { get; init; } = 0;
        public double CraterScale { get; init; } = 30000;
        public int Octaves { get; init; } = 6;
        /// <summary>When true, heights below zero are covered by liquid at height 0.</summary>
        public bool HasOcean { get; init; }
        /// <summary>Linear RGB colours for the renderer.</summary>
        public double[] LowColor { get; init; } = { 0.18, 0.35, 0.12 };
        public double[] HighColor { get; init; } = { 0.45, 0.40, 0.33 };
        public double[] PeakColor { get; init; } = { 0.92, 0.93, 0.95 };
        public double[] ShoreColor { get; init; } = { 0.76, 0.70, 0.50 };
        public double[] OceanColor { get; init; } = { 0.03, 0.12, 0.30 };
        public double SnowHeight { get; init; } = 3500;
        public List<FlatSite> FlatSites { get; init; } = new();
    }

    /// <summary>A flattened region (e.g. a launch site). Lat/lon in degrees.</summary>
    public sealed class FlatSite
    {
        public string Name { get; init; } = "Site";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        /// <summary>Radius of the perfectly flat area (m).</summary>
        public double Radius { get; init; } = 1500;
        /// <summary>Width of the blend ring around the flat area (m).</summary>
        public double Blend { get; init; } = 3000;
        public double Height { get; init; } = 80;
    }

    /// <summary>
    /// Evaluates terrain height as a function of body-fixed direction. Height is in metres above the body's
    /// datum radius. Deterministic and thread-safe (read-only after construction).
    /// </summary>
    public sealed class TerrainGenerator
    {
        private readonly Noise _noise;
        private readonly double _radius;
        private readonly (Vector3d dir, FlatSite site)[] _sites;

        public TerrainSettings Settings { get; }

        public TerrainGenerator(TerrainSettings settings, double bodyRadius)
        {
            Settings = settings;
            _radius = bodyRadius;
            _noise = new Noise(settings.Seed);
            _sites = new (Vector3d, FlatSite)[settings.FlatSites.Count];
            for (int i = 0; i < _sites.Length; i++)
            {
                FlatSite s = settings.FlatSites[i];
                _sites[i] = (CelestialBody.DirectionFromLatLon(s.Latitude, s.Longitude), s);
            }
        }

        /// <summary>Raw terrain height (may be below zero under an ocean).</summary>
        public double RawHeight(Vector3d dir)
        {
            TerrainSettings s = Settings;
            double r = _radius;
            double h = s.BaseOffset;

            if (s.ContinentAmplitude != 0)
            {
                double k = r / s.ContinentScale;
                h += s.ContinentAmplitude * _noise.Fbm(dir.X * k + 11.3, dir.Y * k - 7.1, dir.Z * k + 3.7, 3) * 1.6;
            }
            if (s.HillAmplitude != 0)
            {
                double k = r / s.HillScale;
                h += s.HillAmplitude * _noise.Fbm(dir.X * k, dir.Y * k, dir.Z * k, s.Octaves);
            }
            if (s.MountainAmplitude != 0)
            {
                double k = r / s.MountainScale;
                double mask = MathUtil.Clamp01(_noise.Fbm(dir.X * k * 0.5 - 40, dir.Y * k * 0.5 + 9, dir.Z * k * 0.5 + 21, 2) * 2.0 + 0.2);
                h += s.MountainAmplitude * mask * _noise.Ridged(dir.X * k + 101, dir.Y * k + 55, dir.Z * k - 12, 5);
            }
            if (s.CraterDepth > 0)
            {
                h += Craters(dir);
            }

            // Flatten launch/landing sites.
            foreach (var (siteDir, site) in _sites)
            {
                double ang = Vector3d.Angle(dir, siteDir);
                double dist = ang * r;
                if (dist < site.Radius + site.Blend)
                {
                    double t = dist <= site.Radius ? 1.0 : 1.0 - (dist - site.Radius) / site.Blend;
                    t = t * t * (3 - 2 * t);
                    h = MathUtil.Lerp(h, site.Height, t);
                }
            }
            return h;
        }

        private double Craters(Vector3d dir)
        {
            // Cellular crater field: distance to jittered cell centres shaped into bowls with rims.
            TerrainSettings s = Settings;
            double k = _radius / s.CraterScale;
            double px = dir.X * k, py = dir.Y * k, pz = dir.Z * k;
            double fx = System.Math.Floor(px), fy = System.Math.Floor(py), fz = System.Math.Floor(pz);
            double result = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                double cx = fx + dx, cy = fy + dy, cz = fz + dz;
                double jx = Hash(cx, cy, cz, 1), jy = Hash(cx, cy, cz, 2), jz = Hash(cx, cy, cz, 3);
                double size = 0.15 + 0.35 * Hash(cx, cy, cz, 4);
                if (Hash(cx, cy, cz, 5) < 0.45) continue;
                double ox = cx + jx - px, oy = cy + jy - py, oz = cz + jz - pz;
                double d = System.Math.Sqrt(ox * ox + oy * oy + oz * oz) / size;
                if (d < 1.3)
                {
                    double bowl = d < 1.0 ? (d * d - 1.0) : 0.0;
                    double rim = System.Math.Exp(-(d - 1.0) * (d - 1.0) * 30.0) * 0.25;
                    result += (bowl + rim) * s.CraterDepth * size * 2.0;
                }
            }
            return result;
        }

        private static double Hash(double x, double y, double z, int salt)
        {
            unchecked
            {
                long h = (long)x * 73856093L ^ (long)y * 19349663L ^ (long)z * 83492791L ^ salt * 2654435761L;
                h = (h ^ (h >> 13)) * 1274126177L;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0xFFFFFF;
            }
        }

        /// <summary>Height of the solid-or-liquid surface used for collision (oceans are flat at 0).</summary>
        public double SurfaceHeight(Vector3d dir)
        {
            double h = RawHeight(dir);
            return Settings.HasOcean && h < 0 ? 0 : h;
        }

        public bool IsOcean(Vector3d dir) => Settings.HasOcean && RawHeight(dir) < 0;

        /// <summary>Approximate surface normal (body-fixed) via central differences over ~<paramref name="spacing"/> metres.</summary>
        public Vector3d SurfaceNormal(Vector3d dir, double spacing = 2.0)
        {
            Vector3d up = dir.Normalized();
            Vector3d t1 = Vector3d.Cross(up, System.Math.Abs(up.Y) < 0.9 ? Vector3d.UnitY : Vector3d.UnitX).Normalized();
            Vector3d t2 = Vector3d.Cross(up, t1);
            double eps = spacing / _radius;
            Vector3d pA = PointOnSurface((up + t1 * eps).Normalized());
            Vector3d pB = PointOnSurface((up - t1 * eps).Normalized());
            Vector3d pC = PointOnSurface((up + t2 * eps).Normalized());
            Vector3d pD = PointOnSurface((up - t2 * eps).Normalized());
            Vector3d n = Vector3d.Cross(pC - pD, pA - pB).Normalized();
            if (Vector3d.Dot(n, up) < 0) n = -n;
            return n;
        }

        private Vector3d PointOnSurface(Vector3d dir) => dir * (_radius + SurfaceHeight(dir));
    }
}
