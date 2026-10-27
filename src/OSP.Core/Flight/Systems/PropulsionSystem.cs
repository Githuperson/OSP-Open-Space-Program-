using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight.Systems;

/// <summary>Engines: throttle, propellant draw, thrust (pressure-dependent Isp), gimbal.</summary>
public static class PropulsionSystem
{
    public const double GimbalRateRadPerSec = 0.8;   // ~46 deg/s

    /// <summary>Estimated gimbal control torque authority per craft axis (N m), assuming current throttle.</summary>
    public static Vec3d GimbalAuthority(Craft craft, in FlightEnvironment env)
    {
        double ax = 0, ay = 0, az = 0;
        var com = craft.Mass.CenterOfMass;
        foreach (var p in craft.Parts)
        {
            var e = p.Def.Engine;
            if (e == null || !p.Ignited || e.GimbalDeg <= 0) continue;
            double thr = e.Solid ? 1 : craft.Control.Throttle;
            if (thr <= 0.01) continue;
            double T = e.Thrust(thr, env.PressureAtm);
            double s = Math.Sin(e.GimbalDeg * MathUtil.Deg2Rad);
            var r = p.Position - com;
            var dir = p.Rotation.Rotate(e.ThrustDirV);
            // Maximum torque from a lateral thrust component s*T acting at lever r: per-axis bound.
            ax += T * s * Math.Sqrt(r.Y * r.Y + r.Z * r.Z) * (1 - Math.Abs(dir.X));
            az += T * s * Math.Sqrt(r.X * r.X + r.Y * r.Y) * (1 - Math.Abs(dir.Z));
            ay += T * s * Math.Sqrt(r.X * r.X + r.Z * r.Z) * 0.5;
        }
        return new Vec3d(ax, ay, az);
    }

    public static void Update(Craft craft, in FlightEnvironment env, double dt, Vec3d controlVec,
        ForceAccumulator acc, double time)
    {
        var com = craft.Mass.CenterOfMass;
        double totalThrust = 0;
        bool hasThrottleControl = craft.HasCommand;
        foreach (var p in craft.Parts)
        {
            var e = p.Def.Engine;
            if (e == null) continue;
            if (!p.Ignited)
            {
                p.CurrentThrust = 0; p.PlumeIntensity = 0;
                RelaxGimbal(p, dt);
                continue;
            }

            double throttle;
            if (e.Solid) throttle = 1.0;
            else throttle = hasThrottleControl ? craft.Control.Throttle : 0.0;
            if (!e.Solid && throttle > 1e-4 && e.MinThrottle > 0) throttle = MathUtil.Lerp(e.MinThrottle, 1.0, throttle);

            if (throttle <= 1e-4)
            {
                p.CurrentThrust = 0; p.PlumeIntensity = 0;
                RelaxGimbal(p, dt);
                continue;
            }

            double mdotMax = e.MaxMassFlow;
            double request = mdotMax * throttle * dt;
            double got = craft.Draw(p, e.Propellant, request);
            double frac = request > 0 ? got / request : 0;
            if (got <= 1e-9)
            {
                p.Ignited = false; p.FlamedOut = true; p.CurrentThrust = 0; p.PlumeIntensity = 0;
                craft.Emit(SimEventKind.EngineFlameout, $"{p.Def.Name} {(e.Solid ? "burnt out" : "flamed out (no propellant)")}", time, p);
                continue;
            }

            double thrust = frac * throttle * mdotMax * e.Isp(env.PressureAtm) * MathUtil.G0;
            p.CurrentThrust = thrust;
            p.PlumeIntensity = Math.Clamp(throttle * frac, 0, 1);
            totalThrust += thrust;

            UpdateGimbal(p, e, com, controlVec, dt);
            var dir = p.ThrustDirection();
            acc.Add(p, dir * thrust, p.Position);
        }
        craft.LastTotalThrust = totalThrust;
    }

    private static void UpdateGimbal(CraftPart p, EngineModule e, Vec3d com, Vec3d c, double dt)
    {
        if (e.GimbalDeg <= 0) { p.GimbalOffset = Vec3d.Zero; return; }
        double maxOffset = Math.Sin(e.GimbalDeg * MathUtil.Deg2Rad);
        var axis = p.Rotation.Rotate(e.ThrustDirV);
        var r = p.Position - com;
        // Force direction maximising torque along c is proportional to c x r (tau . c = F . (c x r)).
        var want = Vec3d.Cross(c, r);
        want = want.RejectFrom(axis);
        double wl = want.Length;
        Vec3d target = Vec3d.Zero;
        if (wl > 1e-9) target = want / wl * (maxOffset * Math.Min(1.0, c.Length));
        var delta = target - p.GimbalOffset;
        double dl = delta.Length;
        double maxStep = GimbalRateRadPerSec * dt;
        p.GimbalOffset = dl <= maxStep ? target : p.GimbalOffset + delta / dl * maxStep;
    }

    private static void RelaxGimbal(CraftPart p, double dt)
    {
        if (p.GimbalOffset.LengthSquared < 1e-12) { p.GimbalOffset = Vec3d.Zero; return; }
        double l = p.GimbalOffset.Length, step = GimbalRateRadPerSec * dt;
        p.GimbalOffset = l <= step ? Vec3d.Zero : p.GimbalOffset * (1 - step / l);
    }
}
