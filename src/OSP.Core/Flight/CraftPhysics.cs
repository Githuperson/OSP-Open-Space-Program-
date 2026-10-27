using OSP.Core.Bodies;
using OSP.Core.Flight.Systems;
using OSP.Core.Mathx;
using OSP.Core.Orbits;

namespace OSP.Core.Flight;

public sealed class StepOutcome
{
    public readonly List<CraftPart> Crashed = new();
    public readonly List<CraftPart> BrokenJoints = new();
    public bool Touchdown;
    public double ImpactSpeed;
    public void Clear() { Crashed.Clear(); BrokenJoints.Clear(); Touchdown = false; ImpactSpeed = 0; }
}

/// <summary>
/// Fixed-step rigid-body integration of one craft in the body-centred inertial frame.
///
/// Translation uses kick-drift-kick (leapfrog / velocity-Verlet) for gravity, which is symplectic and second order, so
/// orbital energy does not drift secularly; non-gravitational forces (thrust, drag, contact) are applied as a full kick
/// with the first half. Rotation integrates I dw/dt = tau - w x I w with the exact axis-angle orientation increment.
/// </summary>
public static class CraftPhysics
{
    public const double FixedDt = 0.02;

    public static void Step(Craft craft, StarSystem system, double time, double dt, StepOutcome outcome)
    {
        outcome.Clear();
        var body = craft.Body;

        if (craft.Mode == CraftMode.Landed)
        {
            craft.SyncFromSurface(time);
            var envL = FlightEnvironment.Compute(system, body, time, craft.Position, craft.Velocity, false);
            DeployableSystem.Update(craft, envL, dt, time);
            if (!WantsToLeaveSurface(craft)) return;
            craft.Mode = CraftMode.Physics;
        }

        if (craft.MassDirty) craft.UpdateMassProperties();
        var env = FlightEnvironment.Compute(system, body, time, craft.Position, craft.Velocity);
        DeployableSystem.Update(craft, env, dt, time);   // may change drag state; uses last dynamic pressure
        if (craft.MassDirty) craft.UpdateMassProperties();

        var acc = craft.Forces;
        acc.Reset(craft.Mass.CenterOfMass);

        var ctl = ControlSystem.Compute(craft, env, dt, time);
        PropulsionSystem.Update(craft, env, dt, ctl.C, acc, time);
        AeroSystem.Update(craft, env, acc, ctl.C, dt);
        ControlSystem.ApplyRcs(craft, ctl.C, dt, acc, time);
        acc.AddTorque(ctl.WheelTorque);

        double m = craft.Mass.Mass;
        var q = craft.Orientation;
        var aNg = q.Rotate(acc.Force) / m;
        craft.LastGForce = aNg.Length / 9.80665;

        // --- translation: kick, drift, kick ---
        double mu = body.Mu;
        var v = craft.Velocity + (OrbitMath.GravityAcceleration(mu, craft.Position) * 0.5 + aNg) * dt;
        var p = craft.Position + v * dt;
        v += OrbitMath.GravityAcceleration(mu, p) * 0.5 * dt;
        craft.Position = p; craft.Velocity = v;

        // --- rotation ---
        var I = craft.Mass.Inertia;
        var w = craft.AngularVelocity;
        var gyro = Vec3d.Cross(w, I * w);
        var alpha = craft.Mass.InertiaInverse * (acc.Torque - gyro);
        w += alpha * dt;
        craft.AngularVelocity = w;
        craft.Orientation = q.IntegrateWorld(q.Rotate(w), dt);

        // --- ground contact ---
        var env2 = FlightEnvironment.Compute(system, body, time + dt, craft.Position, craft.Velocity);
        bool wasContact = craft.InContact;
        var cr = ContactSystem.Solve(craft, env2, dt, time + dt, acc);
        if (cr.InContact)
        {
            foreach (var (part, speed) in cr.Crashed) outcome.Crashed.Add(part);
            outcome.ImpactSpeed = cr.FirstImpactSpeed;
            if (!wasContact && cr.FirstImpactSpeed > 0.5 && craft.HasLaunched) outcome.Touchdown = true;
            TryRest(craft, env2, dt, time + dt);
        }
        else craft.RestTimer = 0;

        if (craft.LastTotalThrust > 0 && !craft.InContact) craft.HasLaunched = true;
        if (craft.InContact && craft.LastTotalThrust > 0 && env2.AltitudeTerrain > 2) craft.HasLaunched = true;

        // --- structure ---
        if (craft.HasLaunched || craft.InContact)
        {
            var failed = StructureSystem.Analyze(craft, acc, alpha, dt);
            outcome.BrokenJoints.AddRange(failed);
        }
    }

    private static bool WantsToLeaveSurface(Craft craft)
    {
        foreach (var p in craft.Parts)
        {
            var e = p.Def.Engine;
            if (e == null || !p.Ignited) continue;
            if (e.Solid) return true;
            if (craft.HasCommand && craft.Control.Throttle > 0.01) return true;
        }
        return craft.Control.RcsEnabled && craft.Control.Translation.LengthSquared > 0.01;
    }

    private static void TryRest(Craft craft, in FlightEnvironment env, double dt, double time)
    {
        var vRel = craft.Velocity - craft.Body.SurfaceVelocityAt(craft.Position);
        bool still = vRel.Length < 0.1 && craft.AngularVelocity.Length < 0.03 && craft.LastTotalThrust <= 0;
        if (still) craft.RestTimer += dt; else craft.RestTimer = 0;
        if (craft.RestTimer > 1.0)
        {
            craft.LockToSurface(time);
            craft.RestTimer = 0;
        }
    }
}
