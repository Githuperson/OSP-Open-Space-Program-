using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OSP.Core.Math;
using OSP.Core.Orbits;

namespace OSP.Core.Celestial
{
    /// <summary>The set of celestial bodies in a game. Loaded from data/system.json.</summary>
    public sealed class CelestialSystem
    {
        private readonly Dictionary<string, CelestialBody> _byId = new(StringComparer.OrdinalIgnoreCase);

        public CelestialBody Root { get; private set; }
        public IReadOnlyList<CelestialBody> Bodies => _bodies;
        private readonly List<CelestialBody> _bodies = new();

        /// <summary>The body a new game launches from.</summary>
        public CelestialBody HomeBody { get; private set; }
        public FlatSite LaunchSite { get; private set; }

        public CelestialBody Get(string id) => _byId.TryGetValue(id ?? "", out var b) ? b : null;

        public void Add(CelestialBody body, string parentId, Orbit orbit)
        {
            if (_byId.ContainsKey(body.Id)) throw new InvalidOperationException($"Duplicate body id '{body.Id}'.");
            if (parentId == null)
            {
                if (Root != null) throw new InvalidOperationException("Only one root body is allowed.");
                Root = body;
            }
            else
            {
                CelestialBody parent = Get(parentId) ?? throw new InvalidOperationException($"Body '{body.Id}' references unknown parent '{parentId}'.");
                body.SetParent(parent, orbit);
            }
            _byId[body.Id] = body;
            _bodies.Add(body);
        }

        public void SetHome(string bodyId, string siteName)
        {
            HomeBody = Get(bodyId) ?? throw new InvalidOperationException($"Unknown home body '{bodyId}'.");
            LaunchSite = HomeBody.Terrain?.Settings.FlatSites.FirstOrDefault(s => s.Name == siteName)
                         ?? HomeBody.Terrain?.Settings.FlatSites.FirstOrDefault()
                         ?? new FlatSite { Name = "Default", Latitude = 0, Longitude = 0, Height = 0 };
        }

        /// <summary>Finds the body whose sphere of influence contains an absolute (root-relative) position.</summary>
        public CelestialBody FindSoiBody(Vector3d absolutePosition, double time)
        {
            CelestialBody current = Root;
            bool descended = true;
            while (descended)
            {
                descended = false;
                foreach (CelestialBody child in current.Children)
                {
                    Vector3d rel = absolutePosition - child.AbsolutePosition(time);
                    if (rel.Length < child.SphereOfInfluence)
                    {
                        current = child;
                        descended = true;
                        break;
                    }
                }
            }
            return current;
        }

        // ----- Loading -------------------------------------------------------------------------------

        private sealed class SystemDto
        {
            public int Version { get; set; }
            public string Home { get; set; }
            public string LaunchSite { get; set; }
            public List<BodyDto> Bodies { get; set; } = new();
        }

        private sealed class OrbitDto
        {
            public double SemiMajorAxis { get; set; }
            public double Eccentricity { get; set; }
            public double Inclination { get; set; }
            public double LongitudeOfAscendingNode { get; set; }
            public double ArgumentOfPeriapsis { get; set; }
            public double MeanAnomalyAtEpoch { get; set; }
        }

        private sealed class AtmosphereDto
        {
            public double SeaLevelPressure { get; set; }
            public double SeaLevelDensity { get; set; }
            public double ScaleHeight { get; set; }
            public double Height { get; set; }
            public double Temperature { get; set; } = 288;
            public double MolarMass { get; set; } = 0.0289644;
            public double[] SkyColor { get; set; }
        }

        private sealed class BodyDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; } = "";
            public string Parent { get; set; }
            public double Radius { get; set; }
            public double Mu { get; set; }
            public double SurfaceGravity { get; set; }
            public double RotationPeriod { get; set; }
            public bool TidallyLocked { get; set; }
            public double InitialRotation { get; set; }
            public bool Star { get; set; }
            public double[] Color { get; set; }
            public double[] WarpAltitudeLimits { get; set; }
            public OrbitDto Orbit { get; set; }
            public AtmosphereDto Atmosphere { get; set; }
            public TerrainSettings Terrain { get; set; }
        }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Parses a system definition. Throws <see cref="FormatException"/> with a clear message on invalid data.</summary>
        public static CelestialSystem LoadFromJson(string json)
        {
            SystemDto dto;
            try
            {
                dto = JsonSerializer.Deserialize<SystemDto>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Invalid celestial system JSON: " + ex.Message, ex);
            }
            if (dto == null || dto.Bodies.Count == 0) throw new FormatException("Celestial system contains no bodies.");

            var system = new CelestialSystem();
            // Parents must be defined before children; sort topologically.
            var pending = new List<BodyDto>(dto.Bodies);
            int guard = 0;
            while (pending.Count > 0)
            {
                if (guard++ > 1000) throw new FormatException("Celestial system has a parent cycle or missing parent.");
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    BodyDto b = pending[i];
                    if (b.Parent != null && system.Get(b.Parent) == null) continue;
                    pending.RemoveAt(i);
                    system.Add(CreateBody(b, system), b.Parent, b.Parent == null ? null : CreateOrbit(b, system.Get(b.Parent)));
                }
            }

            system.SetHome(dto.Home ?? system.Bodies.First(b => b.Parent != null).Id, dto.LaunchSite);
            return system;
        }

        private static CelestialBody CreateBody(BodyDto b, CelestialSystem system)
        {
            if (string.IsNullOrWhiteSpace(b.Id)) throw new FormatException("Body without id.");
            if (b.Radius <= 0) throw new FormatException($"Body '{b.Id}' must have a positive radius.");
            double mu = b.Mu > 0 ? b.Mu : b.SurfaceGravity * b.Radius * b.Radius;
            if (mu <= 0) throw new FormatException($"Body '{b.Id}' needs 'mu' or 'surfaceGravity'.");

            double rotation = b.RotationPeriod;
            if (b.TidallyLocked && b.Parent != null && b.Orbit != null)
            {
                CelestialBody parent = system.Get(b.Parent);
                rotation = Orbits.Orbit.PeriodForSemiMajorAxis(parent.Mu, b.Orbit.SemiMajorAxis);
            }

            Atmosphere atmo = null;
            if (b.Atmosphere != null)
            {
                atmo = new Atmosphere
                {
                    SeaLevelPressure = b.Atmosphere.SeaLevelPressure,
                    SeaLevelDensity = b.Atmosphere.SeaLevelDensity,
                    ScaleHeight = b.Atmosphere.ScaleHeight,
                    Height = b.Atmosphere.Height,
                    Temperature = b.Atmosphere.Temperature,
                    MolarMass = b.Atmosphere.MolarMass,
                    SkyColor = b.Atmosphere.SkyColor ?? new[] { 0.35, 0.55, 0.95 },
                };
                if (atmo.ScaleHeight <= 0 || atmo.Height <= 0) throw new FormatException($"Body '{b.Id}' atmosphere needs positive scaleHeight and height.");
            }

            double[] warp = b.WarpAltitudeLimits;
            if (warp == null)
            {
                double[] factors = { 0.0, 0.0, 0.05, 0.1, 0.2, 0.3, 0.5 }; // × radius, for 5×..100000×
                warp = factors.Select(f => System.Math.Max(atmo?.Height ?? 0, b.Radius * f)).ToArray();
            }

            var body = new CelestialBody(b.Id, b.Name ?? b.Id, b.Radius, mu, rotation)
            {
                Description = b.Description,
                InitialRotation = b.InitialRotation * MathUtil.Deg2Rad,
                Atmosphere = atmo,
                IsStar = b.Star,
                Color = b.Color ?? new[] { 0.6, 0.6, 0.6 },
                WarpAltitudeLimits = warp,
            };
            if (!b.Star) body.SetTerrain(b.Terrain ?? new TerrainSettings());
            return body;
        }

        private static Orbit CreateOrbit(BodyDto b, CelestialBody parent)
        {
            OrbitDto o = b.Orbit ?? throw new FormatException($"Body '{b.Id}' has a parent but no orbit.");
            if (o.SemiMajorAxis <= 0 || o.Eccentricity < 0 || o.Eccentricity >= 1)
                throw new FormatException($"Body '{b.Id}' must have a closed orbit (a > 0, 0 <= e < 1).");
            return Orbits.Orbit.FromElements(parent.Mu, o.SemiMajorAxis, o.Eccentricity,
                o.Inclination * MathUtil.Deg2Rad, o.LongitudeOfAscendingNode * MathUtil.Deg2Rad,
                o.ArgumentOfPeriapsis * MathUtil.Deg2Rad, o.MeanAnomalyAtEpoch * MathUtil.Deg2Rad, 0);
        }
    }
}
