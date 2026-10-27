namespace OSP.Core.Celestial
{
    /// <summary>
    /// Isothermal exponential atmosphere: p(h) = p0·exp(-h/H), ρ(h) = ρ0·exp(-h/H), truncated to zero at
    /// <see cref="Height"/>. Simple, smooth, and fast; documented in docs/PHYSICS.md.
    /// </summary>
    public sealed class Atmosphere
    {
        /// <summary>Sea-level (datum) pressure in pascals.</summary>
        public double SeaLevelPressure { get; init; }
        /// <summary>Sea-level density in kg/m³.</summary>
        public double SeaLevelDensity { get; init; }
        /// <summary>Scale height in metres.</summary>
        public double ScaleHeight { get; init; }
        /// <summary>Altitude of the top of the atmosphere in metres (pressure is defined as zero above).</summary>
        public double Height { get; init; }
        /// <summary>Mean temperature in kelvin (used for speed of sound).</summary>
        public double Temperature { get; init; } = 288;
        /// <summary>Ratio of specific heats.</summary>
        public double Gamma { get; init; } = 1.4;
        /// <summary>Molar mass of the gas (kg/mol).</summary>
        public double MolarMass { get; init; } = 0.0289644;
        /// <summary>Sky colour used by the renderer (linear RGB).</summary>
        public double[] SkyColor { get; init; } = { 0.35, 0.55, 0.95 };

        public bool Contains(double altitude) => altitude < Height;

        public double Pressure(double altitude)
        {
            if (altitude >= Height) return 0;
            if (altitude < 0) altitude = 0;
            double p = SeaLevelPressure * System.Math.Exp(-altitude / ScaleHeight);
            // Fade smoothly to zero over the top 2% to avoid a discontinuity.
            double fadeStart = Height * 0.98;
            if (altitude > fadeStart) p *= (Height - altitude) / (Height - fadeStart);
            return p;
        }

        public double Density(double altitude)
        {
            if (altitude >= Height) return 0;
            if (altitude < 0) altitude = 0;
            double rho = SeaLevelDensity * System.Math.Exp(-altitude / ScaleHeight);
            double fadeStart = Height * 0.98;
            if (altitude > fadeStart) rho *= (Height - altitude) / (Height - fadeStart);
            return rho;
        }

        /// <summary>Pressure in standard atmospheres (used for engine Isp interpolation).</summary>
        public double PressureAtm(double altitude) => Pressure(altitude) / PhysicsConstants.StandardAtmosphere;

        public double SpeedOfSound => System.Math.Sqrt(Gamma * 8.314462618 * Temperature / MolarMass);
    }
}
