using System.Text.Json.Serialization;
using OSP.Core.Mathx;

namespace OSP.Core.Parts;

public sealed class EngineModule
{
    /// <summary>Vacuum thrust at full throttle (N).</summary>
    public double ThrustVac { get; set; }
    public double IspVac { get; set; }
    /// <summary>Specific impulse at 1 atm (s). Interpolated linearly in pressure between vacuum and 1 atm.</summary>
    public double IspAtm { get; set; }
    public string Propellant { get; set; } = "Propellant";
    /// <summary>Solid motors cannot be throttled or shut down once lit.</summary>
    public bool Solid { get; set; }
    public double MinThrottle { get; set; }
    /// <summary>Maximum thrust-vector deflection in degrees (0 = fixed nozzle).</summary>
    public double GimbalDeg { get; set; }
    public double[] NozzlePosition { get; set; } = { 0, -0.5, 0 };
    /// <summary>Direction of the thrust force on the vehicle in part-local space.</summary>
    public double[] ThrustDirection { get; set; } = { 0, 1, 0 };
    public double PlumeLength { get; set; } = 4.0;
    public double PlumeRadius { get; set; } = 0.4;

    [JsonIgnore] public Vec3d NozzleV => new(NozzlePosition[0], NozzlePosition[1], NozzlePosition[2]);
    [JsonIgnore] public Vec3d ThrustDirV => new Vec3d(ThrustDirection[0], ThrustDirection[1], ThrustDirection[2]).Normalized;

    /// <summary>Mass flow at full throttle: mdot = F_vac / (Isp_vac g0).</summary>
    [JsonIgnore] public double MaxMassFlow => ThrustVac / (IspVac * MathUtil.G0);

    public double Isp(double pressureAtm)
    {
        double p = Math.Clamp(pressureAtm, 0, 1.0);
        return IspVac + (IspAtm - IspVac) * p;
    }

    /// <summary>Thrust at throttle and ambient pressure: F = mdot Isp(p) g0.</summary>
    public double Thrust(double throttle, double pressureAtm) =>
        throttle * MaxMassFlow * Isp(pressureAtm) * MathUtil.G0;
}

public sealed class DecouplerModule
{
    /// <summary>Total separation impulse (N s), applied equally and oppositely to the two pieces.</summary>
    public double SeparationImpulse { get; set; } = 6000;
    /// <summary>Id of the node whose connection is severed when the decoupler fires.</summary>
    public string CutNode { get; set; } = "bottom";
}

public sealed class CommandModule
{
    public int CrewCapacity { get; set; }
    /// <summary>True for uncrewed probe cores.</summary>
    public bool Probe { get; set; }
    /// <summary>Electric charge consumed per second by the avionics.</summary>
    public double ElectricRate { get; set; } = 0.0;
}

public sealed class ReactionWheelModule
{
    /// <summary>Maximum torque about each craft axis (N m): x = pitch, y = roll(axial), z = yaw.</summary>
    public double Torque { get; set; } = 5000;
    public double ElectricRate { get; set; } = 0.3;
}

public sealed class RcsNozzleDef
{
    public double[] Position { get; set; } = { 0, 0, 0 };
    /// <summary>Direction of the force on the vehicle (local).</summary>
    public double[] Direction { get; set; } = { 1, 0, 0 };
    public double Thrust { get; set; } = 1000;
}

public sealed class RcsModule
{
    public List<RcsNozzleDef> Nozzles { get; set; } = new();
    public double Isp { get; set; } = 240;
    public string Propellant { get; set; } = "Monopropellant";
}

public sealed class SolarModule
{
    /// <summary>Electric charge per second at the stellar reference flux when facing the sun.</summary>
    public double ChargeRate { get; set; } = 1.4;
    /// <summary>Sun-tracking panels ignore their orientation; fixed ones follow the cosine law about <see cref="Normal"/>.</summary>
    public bool Tracking { get; set; } = true;
    public double[] Normal { get; set; } = { 0, 0, 1 };
    public double DeployTime { get; set; } = 3.0;
    [JsonIgnore] public Vec3d NormalV => new Vec3d(Normal[0], Normal[1], Normal[2]).Normalized;
}

public sealed class ParachuteModule
{
    /// <summary>Drag area Cd*A (m^2) when fully open.</summary>
    public double FullDragArea { get; set; } = 150;
    /// <summary>Drag area when semi-deployed (reefed).</summary>
    public double SemiDragArea { get; set; } = 12;
    /// <summary>Altitude above terrain (m) at which an armed chute fully opens.</summary>
    public double FullDeployAltitude { get; set; } = 700;
    /// <summary>Minimum ambient pressure (Pa) required to deploy at all.</summary>
    public double MinPressure { get; set; } = 3000;
    /// <summary>Dynamic pressure (Pa) above which a semi-deployed canopy tears away.</summary>
    public double MaxDynamicPressure { get; set; } = 6000;
    public double SemiDeployTime { get; set; } = 1.5;
    public double FullDeployTime { get; set; } = 5.0;
}

public sealed class LegModule
{
    public double DeployTime { get; set; } = 1.5;
    /// <summary>Foot contact point in part-local coordinates when fully extended.</summary>
    public double[] FootPosition { get; set; } = { 0.9, -0.9, 0 };
    public double Travel { get; set; } = 0.25;
    public double Friction { get; set; } = 1.1;
    [JsonIgnore] public Vec3d FootV => new(FootPosition[0], FootPosition[1], FootPosition[2]);
}

public sealed class AeroSurfaceModule
{
    /// <summary>Planform area (m^2).</summary>
    public double Area { get; set; } = 0.5;
    /// <summary>Plate normal in part-local space (lift acts along +/- this vector).</summary>
    public double[] Normal { get; set; } = { 0, 0, 1 };
    public double Cd0 { get; set; } = 0.02;
    /// <summary>True for actively-deflecting control surfaces.</summary>
    public bool Control { get; set; }
    public double MaxDeflectionDeg { get; set; } = 25;
    [JsonIgnore] public Vec3d NormalV => new Vec3d(Normal[0], Normal[1], Normal[2]).Normalized;
}
