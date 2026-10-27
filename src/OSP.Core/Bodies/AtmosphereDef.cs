using System.Text.Json.Serialization;

namespace OSP.Core.Bodies;

/// <summary>
/// Isothermal-exponential atmosphere: p(h) = p0 exp(-h/H), rho(h) = rho0 exp(-h/H),
/// truncated to zero above <see cref="Height"/>. Simple, monotonic and cheap; documented in docs/PHYSICS.md.
/// </summary>
public sealed class AtmosphereDef
{
    public double SeaLevelPressure { get; set; } = 101325.0;          // Pa
    public double SeaLevelDensity { get; set; } = 1.225;              // kg/m^3
    public double ScaleHeight { get; set; } = 7000.0;                 // m
    public double Height { get; set; } = 70000.0;                     // m, top of the atmosphere (density treated as 0 above)
    public double SeaLevelTemperature { get; set; } = 288.0;          // K
    public double TemperatureLapse { get; set; } = 0.0065;            // K/m (floor 'MinTemperature')
    public double MinTemperature { get; set; } = 180.0;               // K
    public double SpecificHeatRatio { get; set; } = 1.4;
    public double GasConstant { get; set; } = 287.0;                  // J/(kg K) specific gas constant
    /// <summary>Linear RGB of the scattered sky colour (visual only).</summary>
    public double[] ScatterColor { get; set; } = { 0.25, 0.5, 1.0 };

    [JsonIgnore]
    public double SeaLevelPressureAtm => SeaLevelPressure / 101325.0;

    public double Pressure(double altitude)
    {
        if (altitude >= Height) return 0.0;
        if (altitude < 0) altitude = 0;
        return SeaLevelPressure * Math.Exp(-altitude / ScaleHeight);
    }

    public double Density(double altitude)
    {
        if (altitude >= Height) return 0.0;
        if (altitude < 0) altitude = 0;
        return SeaLevelDensity * Math.Exp(-altitude / ScaleHeight);
    }

    public double Temperature(double altitude) =>
        Math.Max(MinTemperature, SeaLevelTemperature - TemperatureLapse * Math.Max(0, altitude));

    public double SpeedOfSound(double altitude) =>
        Math.Sqrt(SpecificHeatRatio * GasConstant * Temperature(altitude));
}
