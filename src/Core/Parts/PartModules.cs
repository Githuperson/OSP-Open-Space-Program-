using System;
using System.Collections.Generic;
using OSP.Core.Math;

namespace OSP.Core.Parts
{
    /// <summary>
    /// Base class for part behaviour definitions. Modules are listed in a part's JSON under "modules", each with
    /// a "type" discriminator registered in <see cref="ModuleRegistry"/>. Adding a new behaviour means adding a
    /// definition class here, registering it, and handling it in the flight simulation.
    /// </summary>
    public abstract class PartModuleDef
    {
        /// <summary>True if this module is triggered by the staging sequence.</summary>
        public virtual bool IsStageable => false;
        public virtual void Validate(PartDefinition part) { }
    }

    /// <summary>A rigid rotation or scale applied to a named mesh group as a module deploys (0 = stowed, 1 = deployed).</summary>
    public sealed class AnimationDef
    {
        public string Group { get; init; }
        public Vector3d Pivot { get; init; }
        public Vector3d Axis { get; init; } = Vector3d.UnitX;
        public double AngleStowed { get; init; }
        public double AngleDeployed { get; init; }
        public Vector3d ScaleStowed { get; init; } = Vector3d.One;
        public Vector3d ScaleDeployed { get; init; } = Vector3d.One;
        /// <summary>Seconds to go from stowed to deployed.</summary>
        public double Duration { get; init; } = 1.5;
    }

    public sealed class CommandModuleDef : PartModuleDef
    {
        public int CrewCapacity { get; init; }
        /// <summary>Probe cores need no crew to control the vessel but need electricity.</summary>
        public bool IsProbe { get; init; }
        /// <summary>Electric charge consumed per second while controlling (EC/s).</summary>
        public double ElectricUsage { get; init; }
        public bool HasSas { get; init; } = true;
    }

    public sealed class PropellantDef
    {
        public string Resource { get; init; }
        /// <summary>Mass fraction of the total propellant flow (ratios are normalised on load).</summary>
        public double Ratio { get; init; }
    }

    public sealed class PlumeDef
    {
        public double Length { get; init; } = 3;
        public double Radius { get; init; } = 0.4;
        public double[] Color { get; init; } = { 1.0, 0.65, 0.3 };
        /// <summary>Solid motors produce a dense, smoky plume.</summary>
        public bool Smoky { get; init; }
    }

    public sealed class EngineModuleDef : PartModuleDef
    {
        /// <summary>Thrust in vacuum at full throttle (N).</summary>
        public double ThrustVacuum { get; init; }
        /// <summary>Specific impulse in vacuum (s).</summary>
        public double IspVacuum { get; init; }
        /// <summary>Specific impulse at 1 atm (s).</summary>
        public double IspSeaLevel { get; init; }
        public List<PropellantDef> Propellants { get; init; } = new();
        /// <summary>Maximum gimbal deflection (degrees).</summary>
        public double GimbalRange { get; init; }
        public double MinThrottle { get; init; }
        /// <summary>Solid motors cannot be throttled or shut down once ignited.</summary>
        public bool IsSolid { get; init; }
        /// <summary>Where thrust is applied, in part-local coordinates (m).</summary>
        public Vector3d ThrustPoint { get; init; }
        /// <summary>Direction the exhaust leaves the nozzle in part-local coordinates. Thrust acts opposite.</summary>
        public Vector3d ExhaustDirection { get; init; } = -Vector3d.UnitY;
        /// <summary>Time constant of throttle response (s).</summary>
        public double SpoolTime { get; init; } = 0.15;
        public PlumeDef Plume { get; init; } = new();
        /// <summary>Mesh group that gimbals (optional).</summary>
        public string GimbalGroup { get; init; }
        public Vector3d GimbalPivot { get; init; }

        public override bool IsStageable => true;

        /// <summary>Total propellant mass flow at full throttle (kg/s): ṁ = F_vac / (Isp_vac · g0).</summary>
        public double MaxMassFlow => IspVacuum > 0 ? ThrustVacuum / (IspVacuum * PhysicsConstants.G0) : 0;

        /// <summary>Isp at a given ambient pressure in atmospheres, linearly interpolated and clamped at 0.</summary>
        public double IspAt(double pressureAtm) => System.Math.Max(0, IspVacuum + (IspSeaLevel - IspVacuum) * pressureAtm);

        /// <summary>Thrust at full throttle for the ambient pressure. Mass flow is constant; thrust scales with Isp.</summary>
        public double ThrustAt(double pressureAtm) => MaxMassFlow * IspAt(pressureAtm) * PhysicsConstants.G0;

        public override void Validate(PartDefinition part)
        {
            if (ThrustVacuum <= 0) throw new FormatException($"{part.Id}: engine thrustVacuum must be positive.");
            if (IspVacuum <= 0) throw new FormatException($"{part.Id}: engine ispVacuum must be positive.");
            if (Propellants.Count == 0) throw new FormatException($"{part.Id}: engine needs at least one propellant.");
            double sum = 0;
            foreach (var p in Propellants)
            {
                if (!ResourceLibrary.Exists(p.Resource)) throw new FormatException($"{part.Id}: unknown propellant '{p.Resource}'.");
                if (p.Ratio <= 0) throw new FormatException($"{part.Id}: propellant ratios must be positive.");
                sum += p.Ratio;
            }
            if (System.Math.Abs(sum - 1) > 1e-6) throw new FormatException($"{part.Id}: propellant ratios must sum to 1 (got {sum}).");
        }
    }

    public sealed class DecouplerModuleDef : PartModuleDef
    {
        /// <summary>Separation impulse applied equally and oppositely to both halves (N·s).</summary>
        public double EjectionImpulse { get; init; } = 500;
        /// <summary>Radial decouplers attach to the side of a stack; affects auto-staging only.</summary>
        public bool IsRadial { get; init; }
        public override bool IsStageable => true;
    }

    public sealed class ParachuteModuleDef : PartModuleDef
    {
        /// <summary>Drag coefficient × area when fully deployed (m²).</summary>
        public double DeployedDragArea { get; init; } = 50;
        /// <summary>Fraction of the full drag area while semi-deployed (streamer).</summary>
        public double SemiDeployedFraction { get; init; } = 0.06;
        /// <summary>Height above terrain at which the canopy fully opens (m).</summary>
        public double DeployAltitude { get; init; } = 800;
        /// <summary>Minimum static pressure for the canopy to inflate (atm).</summary>
        public double MinPressure { get; init; } = 0.04;
        /// <summary>Dynamic pressure above which a fully deployed canopy is torn off (Pa).</summary>
        public double MaxDynamicPressure { get; init; } = 60000;
        public AnimationDef Animation { get; init; }
        public override bool IsStageable => true;
    }

    public sealed class LandingLegModuleDef : PartModuleDef
    {
        /// <summary>Foot contact position (part-local) when deployed.</summary>
        public Vector3d FootDeployed { get; init; }
        /// <summary>Foot contact position (part-local) when stowed.</summary>
        public Vector3d FootStowed { get; init; }
        /// <summary>Suspension travel (m).</summary>
        public double Stroke { get; init; } = 0.3;
        /// <summary>Suspension spring natural frequency for this leg's share of the vessel (Hz).</summary>
        public double SpringFrequency { get; init; } = 2.0;
        public double DampingRatio { get; init; } = 0.8;
        public double Friction { get; init; } = 0.9;
        public AnimationDef Animation { get; init; }
        public bool StartDeployed { get; init; }
    }

    public sealed class ReactionWheelModuleDef : PartModuleDef
    {
        /// <summary>Maximum torque per axis (N·m).</summary>
        public double Torque { get; init; }
        /// <summary>Electric charge per second at full torque on one axis (EC/s).</summary>
        public double ElectricUsage { get; init; }
    }

    public sealed class RcsNozzleDef
    {
        public Vector3d Position { get; init; }
        /// <summary>Exhaust direction (part-local). Force acts opposite.</summary>
        public Vector3d Direction { get; init; }
    }

    public sealed class RcsModuleDef : PartModuleDef
    {
        public double ThrustPerNozzle { get; init; } = 1000;
        public double Isp { get; init; } = 240;
        public string Propellant { get; init; } = ResourceLibrary.Monopropellant;
        public List<RcsNozzleDef> Nozzles { get; init; } = new();

        public override void Validate(PartDefinition part)
        {
            if (Nozzles.Count == 0) throw new FormatException($"{part.Id}: RCS needs at least one nozzle.");
            if (!ResourceLibrary.Exists(Propellant)) throw new FormatException($"{part.Id}: unknown RCS propellant '{Propellant}'.");
        }
    }

    public sealed class SolarPanelModuleDef : PartModuleDef
    {
        /// <summary>Output facing the sun at the home planet's distance (EC/s). Scales with 1/r² and cos(incidence).</summary>
        public double PowerOutput { get; init; } = 1;
        /// <summary>Panel normal in part-local coordinates (when deployed).</summary>
        public Vector3d PanelNormal { get; init; } = Vector3d.UnitZ;
        /// <summary>Double-sided panels generate power from both faces.</summary>
        public bool DoubleSided { get; init; }
        public bool Deployable { get; init; }
        public AnimationDef Animation { get; init; }
        /// <summary>Deployed panels break above this dynamic pressure (Pa).</summary>
        public double MaxDynamicPressure { get; init; } = 3000;
    }

    public sealed class LiftingSurfaceModuleDef : PartModuleDef
    {
        /// <summary>Planform area (m²).</summary>
        public double Area { get; init; } = 0.5;
        /// <summary>Surface normal in part-local coordinates.</summary>
        public Vector3d Normal { get; init; } = Vector3d.UnitX;
        /// <summary>Lift curve slope multiplier (thin-plate theory ≈ 1).</summary>
        public double LiftScale { get; init; } = 1.0;
        /// <summary>Centre of pressure (part-local).</summary>
        public Vector3d CenterOfPressure { get; init; }
        /// <summary>Control surfaces deflect with pitch/yaw/roll input.</summary>
        public bool IsControlSurface { get; init; }
        public double MaxDeflection { get; init; } = 15;
        /// <summary>Hinge axis (part-local) for control surfaces.</summary>
        public Vector3d HingeAxis { get; init; } = Vector3d.UnitY;
        public string DeflectionGroup { get; init; }
    }

    public sealed class FuelLineModuleDef : PartModuleDef
    {
        /// <summary>Maximum distance to search for a target part along the line direction (m).</summary>
        public double Reach { get; init; } = 4;
    }

    public sealed class AntennaModuleDef : PartModuleDef
    {
        public double Range { get; init; } = 5e6;
        /// <summary>Science transmission rate (data units/s).</summary>
        public double TransmitRate { get; init; } = 2;
        /// <summary>EC per data unit transmitted.</summary>
        public double ElectricPerData { get; init; } = 6;
    }

    public sealed class ExperimentModuleDef : PartModuleDef
    {
        public string ExperimentId { get; init; }
    }

    public sealed class DockingPortModuleDef : PartModuleDef
    {
        /// <summary>Attach node used as the docking face.</summary>
        public string Node { get; init; } = "top";
        /// <summary>Ports dock only with ports of the same size.</summary>
        public int Size { get; init; } = 1;
        /// <summary>Distance at which magnetic capture engages (m).</summary>
        public double CaptureRange { get; init; } = 0.5;
        /// <summary>Maximum alignment error for capture (degrees).</summary>
        public double CaptureAngle { get; init; } = 15;
        /// <summary>Undocking separation impulse (N·s).</summary>
        public double UndockImpulse { get; init; } = 100;
    }

    /// <summary>Jettisonable shells (fairings, engine shrouds). Staging removes the mass and visual group.</summary>
    public sealed class JettisonModuleDef : PartModuleDef
    {
        public string Group { get; init; }
        public double JettisonMass { get; init; }
        /// <summary>Drag multiplier removed when jettisoned (e.g. fairing shielding).</summary>
        public double Impulse { get; init; } = 50;
        public override bool IsStageable => true;
    }

    public static class ModuleRegistry
    {
        public static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Command"] = typeof(CommandModuleDef),
            ["Engine"] = typeof(EngineModuleDef),
            ["Decoupler"] = typeof(DecouplerModuleDef),
            ["Parachute"] = typeof(ParachuteModuleDef),
            ["LandingLeg"] = typeof(LandingLegModuleDef),
            ["ReactionWheel"] = typeof(ReactionWheelModuleDef),
            ["RCS"] = typeof(RcsModuleDef),
            ["SolarPanel"] = typeof(SolarPanelModuleDef),
            ["LiftingSurface"] = typeof(LiftingSurfaceModuleDef),
            ["FuelLine"] = typeof(FuelLineModuleDef),
            ["Antenna"] = typeof(AntennaModuleDef),
            ["Experiment"] = typeof(ExperimentModuleDef),
            ["DockingPort"] = typeof(DockingPortModuleDef),
            ["Jettison"] = typeof(JettisonModuleDef),
        };
    }
}
