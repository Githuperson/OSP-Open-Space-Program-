using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Math;

namespace OSP.Core.Science
{
    /// <summary>Science situations, derived from the vessel's actual state.</summary>
    public enum ScienceSituation
    {
        Landed,
        Splashed,
        FlyingLow,
        FlyingHigh,
        SpaceLow,
        SpaceHigh,
    }

    public sealed class ExperimentDefinition
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public double BaseValue { get; init; }
        public ScienceSituation[] Situations { get; init; }
        /// <summary>Situations in which results depend on the biome below the vessel.</summary>
        public ScienceSituation[] BiomeSituations { get; init; } = Array.Empty<ScienceSituation>();
        public bool RequiresAtmosphere { get; init; }
        public bool RequiresCrew { get; init; }
        /// <summary>Data size; transmission costs electricity per unit.</summary>
        public double DataSize { get; init; } = 10;
        /// <summary>Fraction of value obtained by transmitting instead of recovering.</summary>
        public double TransmitFraction { get; init; } = 0.5;
    }

    /// <summary>A result that was collected but not yet credited (stored on the vessel).</summary>
    public sealed class ScienceResult
    {
        public string ExperimentId;
        public string BodyId;
        public ScienceSituation Situation;
        public string Biome;
        public string Key => ScienceRules.Key(ExperimentId, BodyId, Situation, Biome);
    }

    public static class ScienceRules
    {
        public static readonly IReadOnlyList<ExperimentDefinition> Experiments = new List<ExperimentDefinition>
        {
            new() { Id = "crewReport", Name = "Crew Report", Description = "The crew records their observations.", BaseValue = 5, RequiresCrew = true,
                    Situations = Enum.GetValues<ScienceSituation>(), BiomeSituations = new[] { ScienceSituation.Landed, ScienceSituation.Splashed, ScienceSituation.FlyingLow }, DataSize = 5, TransmitFraction = 1.0 },
            new() { Id = "thermometer", Name = "Temperature Scan", Description = "Measures local temperature.", BaseValue = 8,
                    Situations = Enum.GetValues<ScienceSituation>(), BiomeSituations = new[] { ScienceSituation.Landed, ScienceSituation.Splashed, ScienceSituation.FlyingLow, ScienceSituation.SpaceLow } },
            new() { Id = "barometer", Name = "Pressure Scan", Description = "Measures atmospheric pressure.", BaseValue = 12, RequiresAtmosphere = true,
                    Situations = new[] { ScienceSituation.Landed, ScienceSituation.Splashed, ScienceSituation.FlyingLow, ScienceSituation.FlyingHigh }, BiomeSituations = new[] { ScienceSituation.Landed, ScienceSituation.FlyingLow } },
            new() { Id = "seismometer", Name = "Seismic Scan", Description = "Listens to the ground. Must be landed.", BaseValue = 20,
                    Situations = new[] { ScienceSituation.Landed }, BiomeSituations = new[] { ScienceSituation.Landed } },
            new() { Id = "gravimeter", Name = "Gravity Scan", Description = "Maps the gravity field.", BaseValue = 20,
                    Situations = new[] { ScienceSituation.Landed, ScienceSituation.SpaceLow, ScienceSituation.SpaceHigh }, BiomeSituations = new[] { ScienceSituation.Landed, ScienceSituation.SpaceLow } },
            new() { Id = "sampleBay", Name = "Material Exposure", Description = "Exposes material samples to the environment.", BaseValue = 25,
                    Situations = Enum.GetValues<ScienceSituation>(), BiomeSituations = new[] { ScienceSituation.Landed }, DataSize = 40, TransmitFraction = 0.25 },
        };

        public static ExperimentDefinition Get(string id) => Experiments.FirstOrDefault(e => e.Id == id);

        public static string Key(string experiment, string body, ScienceSituation situation, string biome) =>
            $"{experiment}@{body}:{situation}{(string.IsNullOrEmpty(biome) ? "" : "/" + biome)}";

        /// <summary>Altitude below which a flying vessel is "low" (m).</summary>
        public static double FlyingHighThreshold(CelestialBody b) => b.HasAtmosphere ? b.AtmosphereHeight * 0.25 : 0;
        /// <summary>Altitude above which space is "high" (m).</summary>
        public static double SpaceHighThreshold(CelestialBody b) => System.Math.Max(b.Radius * 0.5, b.AtmosphereHeight * 1.5);

        public static double BodyMultiplier(CelestialBody b) => b.Id switch
        {
            "aster" => 1.0,
            "tessar" => 4.0,
            "vesk" => 5.0,
            "cindra" => 8.0,
            "ferrus" => 7.0,
            "ket" => 8.0,
            _ => b.IsStar ? 10.0 : 3.0,
        };

        public static double SituationMultiplier(ScienceSituation s) => s switch
        {
            ScienceSituation.Landed => 1.5,
            ScienceSituation.Splashed => 1.2,
            ScienceSituation.FlyingLow => 0.8,
            ScienceSituation.FlyingHigh => 0.9,
            ScienceSituation.SpaceLow => 1.0,
            _ => 1.2,
        };

        public static ScienceSituation? Classify(Vessel v)
        {
            CelestialBody b = v.Body;
            switch (v.Situation)
            {
                case Situation.Landed:
                case Situation.Prelaunch:
                    return ScienceSituation.Landed;
                case Situation.Splashed:
                    return ScienceSituation.Splashed;
                case Situation.Destroyed:
                    return null;
            }
            double alt = v.Altitude;
            if (b.HasAtmosphere && alt < b.AtmosphereHeight)
                return alt < FlyingHighThreshold(b) ? ScienceSituation.FlyingLow : ScienceSituation.FlyingHigh;
            return alt < SpaceHighThreshold(b) ? ScienceSituation.SpaceLow : ScienceSituation.SpaceHigh;
        }

        /// <summary>
        /// Biome from latitude and the actual terrain under the vessel: Poles, Oceans/Shores, Highlands, Midlands,
        /// Lowlands. Bodies share the classification scheme so new bodies get biomes automatically.
        /// </summary>
        public static string Biome(CelestialBody b, Vector3d bodyFixedDirection)
        {
            if (b.Terrain == null) return "Surface";
            CelestialBody.LatLonFromDirection(bodyFixedDirection, out double lat, out _);
            if (System.Math.Abs(lat) > 65) return lat > 0 ? "North Pole" : "South Pole";
            double h = b.Terrain.RawHeight(bodyFixedDirection.Normalized());
            if (b.Terrain.Settings.HasOcean && h < 0) return h < -400 ? "Deep Ocean" : "Shores";
            double peak = b.Terrain.Settings.MountainAmplitude + b.Terrain.Settings.HillAmplitude;
            if (h > peak * 0.45) return "Highlands";
            if (h > peak * 0.15) return "Midlands";
            return "Lowlands";
        }

        /// <summary>Full value of an experiment result (science points before diminishing returns).</summary>
        public static double Value(ExperimentDefinition e, CelestialBody b, ScienceSituation s) =>
            System.Math.Round(e.BaseValue * BodyMultiplier(b) * SituationMultiplier(s), 1);

        /// <summary>Checks whether an experiment can run on a vessel now. Returns a result, or null with a reason.</summary>
        public static ScienceResult TryRun(Vessel v, ExperimentDefinition e, double time, out string reason)
        {
            reason = null;
            ScienceSituation? sit = Classify(v);
            if (sit == null) { reason = "No valid situation."; return null; }
            if (!e.Situations.Contains(sit.Value)) { reason = $"{e.Name} cannot be used while {Describe(sit.Value)}."; return null; }
            if (e.RequiresAtmosphere && !(v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereHeight)) { reason = $"{e.Name} needs an atmosphere."; return null; }
            string biome = e.BiomeSituations.Contains(sit.Value) ? Biome(v.Body, v.Body.InertialToBodyFixed(v.Position, time)) : null;
            return new ScienceResult { ExperimentId = e.Id, BodyId = v.Body.Id, Situation = sit.Value, Biome = biome };
        }

        public static string Describe(ScienceSituation s) => s switch
        {
            ScienceSituation.Landed => "landed",
            ScienceSituation.Splashed => "splashed down",
            ScienceSituation.FlyingLow => "flying low",
            ScienceSituation.FlyingHigh => "flying high",
            ScienceSituation.SpaceLow => "in space near the body",
            _ => "in high space",
        };
    }
}
