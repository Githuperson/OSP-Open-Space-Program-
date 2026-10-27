using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight.Systems;

/// <summary>Landing legs, solar panels, parachutes and electrical power.</summary>
public static class DeployableSystem
{
    public static void Update(Craft craft, in FlightEnvironment env, double dt, double time)
    {
        foreach (var p in craft.Parts)
        {
            if (p.Def.Leg is { } leg)
                p.LegDeploy = Approach(p.LegDeploy, p.LegsDeployedTarget ? 1 : 0, dt / Math.Max(0.1, leg.DeployTime));
            if (p.Def.Solar is { } sol)
                p.SolarDeploy = Approach(p.SolarDeploy, p.SolarDeployedTarget ? 1 : 0, dt / Math.Max(0.1, sol.DeployTime));
            if (p.Def.Parachute is { } pc) UpdateChute(craft, p, pc, env, dt, time);
        }
        UpdatePower(craft, env, dt);
    }

    private static double Approach(double v, double target, double step) =>
        v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

    private static void UpdateChute(Craft craft, CraftPart p, ParachuteModule pc, in FlightEnvironment env, double dt, double time)
    {
        double qd = craft.LastDynamicPressure;
        switch (p.Chute)
        {
            case ChuteState.Armed:
                if (env.PressurePa >= pc.MinPressure && env.SurfaceVelocity.Length > 5)
                {
                    p.Chute = ChuteState.Semi; p.ChuteProgress = 0;
                    craft.Emit(SimEventKind.ParachuteOpened, $"{p.Def.Name} semi-deployed", time, p);
                }
                break;
            case ChuteState.Semi:
                p.ChuteProgress = Math.Min(1, p.ChuteProgress + dt / pc.SemiDeployTime);
                if (qd > pc.MaxDynamicPressure) Cut(craft, p, time);
                else if (env.AltitudeTerrain < pc.FullDeployAltitude && env.PressurePa >= pc.MinPressure)
                {
                    p.Chute = ChuteState.Deployed; p.ChuteProgress = 0;
                    craft.Emit(SimEventKind.ParachuteOpened, $"{p.Def.Name} fully deployed", time, p);
                }
                break;
            case ChuteState.Deployed:
                p.ChuteProgress = Math.Min(1, p.ChuteProgress + dt / pc.FullDeployTime);
                if (qd > pc.MaxDynamicPressure * 1.5) Cut(craft, p, time);
                break;
        }
    }

    private static void Cut(Craft craft, CraftPart p, double time)
    {
        p.Chute = ChuteState.Cut; p.ChuteProgress = 0;
        craft.Emit(SimEventKind.ParachuteCut, $"{p.Def.Name} torn away by excessive dynamic pressure!", time, p);
    }

    private static void UpdatePower(Craft craft, in FlightEnvironment env, double dt)
    {
        double produced = 0, drawn = 0;
        foreach (var p in craft.Parts)
        {
            if (p.Def.Solar is { } s && p.SolarDeploy >= 0.999 && !env.InShadow)
            {
                double facing = 1.0;
                if (!s.Tracking)
                {
                    var n = craft.Orientation.Rotate(p.Rotation.Rotate(s.NormalV));
                    facing = Math.Max(0, Vec3d.Dot(n, env.SunDirection));
                }
                produced += s.ChargeRate * env.SolarFluxRatio * facing;
            }
            if (p.Def.Command is { } cm && cm.ElectricRate > 0) drawn += cm.ElectricRate;
        }
        if (produced > 0) craft.Store(PartCatalog.ElectricCharge, produced * dt);
        if (drawn > 0) craft.DrawGlobal(PartCatalog.ElectricCharge, drawn * dt);
        craft.LastElectricProduction = produced;
    }
}
