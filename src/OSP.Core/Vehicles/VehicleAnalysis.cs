using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles;

public sealed class StageStats
{
    public int Stage { get; init; }
    public double StartMass { get; init; }
    public double EndMass { get; init; }
    public double DeltaV { get; init; }
    public double BurnTime { get; init; }
    public double Thrust { get; init; }
    public double Isp { get; init; }
    /// <summary>Thrust-to-weight at stage start against the reference gravity.</summary>
    public double Twr { get; init; }
}

public sealed class VehicleStats
{
    public int PartCount { get; init; }
    public double WetMass { get; init; }
    public double DryMass { get; init; }
    public double Cost { get; init; }
    public int Crew { get; init; }
    public double PropellantMass { get; init; }
    public double ElectricCapacity { get; init; }
    public double MonopropMass { get; init; }
    public List<StageStats> StagesVacuum { get; init; } = new();
    public List<StageStats> StagesAtmosphere { get; init; } = new();
    public double DeltaVVacuum => StagesVacuum.Sum(s => s.DeltaV);
    public double DeltaVAtmosphere => StagesAtmosphere.Sum(s => s.DeltaV);
    /// <summary>Thrust (N) of all engines that fire at launch, at sea level and in vacuum.</summary>
    public double LaunchThrustVac { get; init; }
    public double LaunchThrustAsl { get; init; }
    public double LaunchTwr { get; init; }
    public double MaxTotalThrustVac { get; init; }
}

/// <summary>Design-time analysis: masses, thrust-to-weight and per-stage delta-v by running the real staging/propulsion logic.</summary>
public static class VehicleAnalysis
{
    public static VehicleStats Analyze(VehicleDesign design, PartCatalog catalog, double surfaceGravity = 9.81, double seaLevelPressureAtm = 1.0)
    {
        if (design.IsEmpty) return new VehicleStats();
        var craft0 = Craft.FromDesign(design, catalog);
        double wet = craft0.TotalMass;

        // Launch thrust: engines whose stage is the launch stage.
        int launch = craft0.CurrentStage;
        double tv = 0, ta = 0;
        foreach (var p in craft0.Parts)
            if (p.Def.Engine is { } e && p.Stage == launch) { tv += e.ThrustVac; ta += e.Thrust(1, seaLevelPressureAtm); }

        return new VehicleStats
        {
            PartCount = design.Parts.Count,
            WetMass = wet,
            DryMass = craft0.DryMass,
            Cost = design.Parts.Sum(p => catalog.Get(p.PartId).Cost),
            Crew = design.Parts.Sum(p => catalog.Get(p.PartId).Command?.CrewCapacity ?? 0),
            PropellantMass = craft0.ResourceTotal(PartCatalog.Propellant) + craft0.ResourceTotal(PartCatalog.SolidFuel),
            MonopropMass = craft0.ResourceTotal(PartCatalog.Monopropellant),
            ElectricCapacity = craft0.ResourceCapacityTotal(PartCatalog.ElectricCharge),
            StagesVacuum = RunStages(design, catalog, 0.0, surfaceGravity),
            StagesAtmosphere = RunStages(design, catalog, seaLevelPressureAtm, surfaceGravity),
            LaunchThrustVac = tv, LaunchThrustAsl = ta,
            LaunchTwr = wet > 0 ? ta / (wet * surfaceGravity) : 0,
            MaxTotalThrustVac = craft0.Parts.Where(p => p.Def.Engine != null).Sum(p => p.Def.Engine!.ThrustVac),
        };
    }

    /// <summary>
    /// Burn each stage to depletion at full throttle in a fixed ambient pressure with no gravity or drag, integrating
    /// dv = F/m dt (0.1 s steps, midpoint mass) through the same propellant-draw code the flight simulation uses.
    /// </summary>
    public static List<StageStats> RunStages(VehicleDesign design, PartCatalog catalog, double pressureAtm, double g)
    {
        var result = new List<StageStats>();
        var craft = Craft.FromDesign(design, catalog);
        craft.Control.Throttle = 1.0;
        int nextId = 1000;
        var env = new FlightEnvironment { PressureAtm = pressureAtm, PressurePa = pressureAtm * 101325.0, Density = 0 };

        for (int guard = 0; guard < 32; guard++)
        {
            int stage = StagingController.NextStage(craft);
            if (stage < 0) break;
            var res = StagingController.Activate(craft, 0, () => nextId++);
            craft.UpdateMassProperties();
            var active = craft.Parts.Where(p => p.Ignited && p.Def.Engine != null).ToList();
            if (active.Count == 0) continue;

            double m0 = craft.TotalMass, dv = 0, t = 0;
            double thrust0 = active.Sum(p => p.Def.Engine!.Thrust(1, pressureAtm));
            double mdot0 = active.Sum(p => p.Def.Engine!.MaxMassFlow);
            double isp0 = mdot0 > 0 ? thrust0 / (mdot0 * MathUtil.G0) : 0;
            const double dt = 0.1;
            for (int i = 0; i < 200000; i++)
            {
                double mBefore = craft.TotalMass;
                double thrust = 0;
                foreach (var p in craft.Parts.Where(q => q.Ignited && q.Def.Engine != null).ToList())
                {
                    var e = p.Def.Engine!;
                    double req = e.MaxMassFlow * dt;
                    double got = craft.Draw(p, e.Propellant, req);
                    if (got <= 1e-9) { p.Ignited = false; p.FlamedOut = true; continue; }
                    thrust += got / dt * e.Isp(pressureAtm) * MathUtil.G0;
                }
                if (thrust <= 0) break;
                craft.UpdateMassProperties();
                double mAfter = craft.TotalMass;
                dv += thrust / (0.5 * (mBefore + mAfter)) * dt;
                t += dt;
            }
            result.Add(new StageStats
            {
                Stage = stage, StartMass = m0, EndMass = craft.TotalMass, DeltaV = dv, BurnTime = t,
                Thrust = thrust0, Isp = isp0, Twr = thrust0 / (m0 * g),
            });
            // Spent engines remain as dead weight until their stage is jettisoned: handled by decouplers in later stages.
        }
        return result;
    }
}
