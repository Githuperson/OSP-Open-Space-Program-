using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight.Systems;

/// <summary>
/// Attitude and translation control: manual input, SAS (stability assist / pointing modes), reaction wheels,
/// RCS thruster allocation and control-surface deflection.
///
/// The controller outputs a normalised control vector c (craft axes x = pitch, y = roll, z = yaw; each in [-1, 1]).
/// Every actuator (reaction wheel, RCS, gimbal, fins) then contributes c times its own authority, so their sum equals
/// the requested torque tau = c * total authority.
/// </summary>
public static class ControlSystem
{
    public const double MaxSasRate = 1.2;          // rad/s
    public const double SasResponseTime = 0.25;    // s
    public const double SasLinearGain = 1.6;       // 1/s, small-angle regime

    public struct ControlOutput
    {
        public Vec3d C;                 // normalised rotation command
        public Vec3d WheelTorque;       // applied reaction-wheel torque (craft frame)
        public bool HasControl;
    }

    public static ControlOutput Compute(Craft craft, in FlightEnvironment env, double dt, double time)
    {
        var ctl = craft.Control;
        var o = new ControlOutput();
        o.HasControl = HasControlSource(craft);
        if (!o.HasControl) return o;

        var I = craft.Mass.Inertia;
        double ix = Math.Max(1.0, I.M00), iy = Math.Max(1.0, I.M11), iz = Math.Max(1.0, I.M22);

        // --- authority ----------------------------------------------------------------------
        double wheel = 0; double wheelEc = 0;
        foreach (var p in craft.Parts)
            if (p.Def.ReactionWheel is { } rw) { wheel += rw.Torque; wheelEc += rw.ElectricRate; }
        bool hasEc = craft.ResourceTotal(PartCatalog.ElectricCharge) > 0.001;
        if (!hasEc) wheel = 0;

        var rcsAuth = ctl.RcsEnabled ? RcsAuthority(craft) : Vec3d.Zero;
        var gimbal = PropulsionSystem.GimbalAuthority(craft, env);
        var auth = new Vec3d(wheel + rcsAuth.X + gimbal.X, wheel + rcsAuth.Y + gimbal.Y, wheel + rcsAuth.Z + gimbal.Z);

        // control surfaces add authority that scales with dynamic pressure: ignored in the budget, used opportunistically.

        // --- command ----------------------------------------------------------------------------
        Vec3d c;
        // manual axes (Pitch -> x, Roll -> y, Yaw -> z)
        var manual = new Vec3d(ctl.Pitch, ctl.Roll, ctl.Yaw);
        Vec3d sasTorque = Vec3d.Zero;
        bool sasActive = ctl.SasEnabled;
        if (sasActive)
        {
            var q = craft.Orientation;
            if (ctl.HasManualRotation || !ctl.HoldValid)
            {
                ctl.HoldOrientation = q; ctl.HoldValid = true;
            }
            var target = SasTarget(craft, env, ctl);
            var err = AttitudeError(q, target);            // rotation vector in craft frame
            var w = craft.AngularVelocity;
            double aMaxX = Math.Max(1e-6, auth.X / ix), aMaxY = Math.Max(1e-6, auth.Y / iy), aMaxZ = Math.Max(1e-6, auth.Z / iz);
            double ax = AxisAlpha(err.X, w.X, aMaxX), ay = AxisAlpha(err.Y, w.Y, aMaxY), az = AxisAlpha(err.Z, w.Z, aMaxZ);
            sasTorque = new Vec3d(ax * ix, ay * iy, az * iz);
        }
        double cx = AxisFraction(manual.X, sasTorque.X, auth.X, sasActive);
        double cy = AxisFraction(manual.Y, sasTorque.Y, auth.Y, sasActive);
        double cz = AxisFraction(manual.Z, sasTorque.Z, auth.Z, sasActive);
        c = new Vec3d(cx, cy, cz);
        o.C = c;

        // --- reaction wheels ------------------------------------------------------------------
        if (wheel > 0 && c.LengthSquared > 1e-12)
        {
            double use = (Math.Abs(c.X) + Math.Abs(c.Y) + Math.Abs(c.Z)) / 3.0 * wheelEc * dt;
            double got = craft.DrawGlobal(PartCatalog.ElectricCharge, use);
            double f = use > 0 ? got / use : 1.0;
            o.WheelTorque = c * (wheel * f);
        }
        return o;
    }

    private static bool HasControlSource(Craft craft)
    {
        foreach (var p in craft.Parts)
        {
            var cm = p.Def.Command;
            if (cm == null) continue;
            if (!cm.Probe) return true;                       // crewed pods always have control
            if (craft.ResourceTotal(PartCatalog.ElectricCharge) > 0.001) return true;   // probes need power
        }
        return false;
    }

    private static double AxisFraction(double manual, double sasTorque, double auth, bool sas)
    {
        if (auth <= 1e-9) return 0;
        if (Math.Abs(manual) > 1e-6) return Math.Clamp(manual, -1, 1);
        if (!sas) return 0;
        return Math.Clamp(sasTorque / auth, -1, 1);
    }

    /// <summary>Angular acceleration command that drives an axis to zero error with a braking curve (no overshoot).</summary>
    private static double AxisAlpha(double err, double w, double aMax)
    {
        double wDesLin = SasLinearGain * err;
        double wDesBrake = Math.Sign(err) * Math.Sqrt(2.0 * 0.6 * aMax * Math.Abs(err));
        double wDes = Math.Abs(wDesLin) < Math.Abs(wDesBrake) ? wDesLin : wDesBrake;
        wDes = Math.Clamp(wDes, -MaxSasRate, MaxSasRate);
        double a = (wDes - w) / SasResponseTime;
        return Math.Clamp(a, -aMax, aMax);
    }

    /// <summary>Rotation vector (craft frame) that takes the current attitude to the target attitude.</summary>
    public static Vec3d AttitudeError(QuatD current, QuatD target)
    {
        // q_err (inertial) = target * current^-1 ; express its axis-angle in the craft frame.
        var qe = (target * current.Conjugate).Normalized;
        double w = Math.Clamp(qe.W, -1, 1);
        if (w < 0) qe = new QuatD(-qe.W, -qe.X, -qe.Y, -qe.Z);   // shortest path
        w = Math.Clamp(qe.W, -1, 1);
        double angle = 2 * Math.Acos(w);
        double s = Math.Sqrt(Math.Max(0, 1 - w * w));
        Vec3d axis = s < 1e-9 ? Vec3d.Zero : new Vec3d(qe.X / s, qe.Y / s, qe.Z / s);
        return current.InverseRotate(axis * angle);
    }

    /// <summary>Desired orientation (craft -> inertial) for the current SAS mode.</summary>
    public static QuatD SasTarget(Craft craft, in FlightEnvironment env, ControlState ctl)
    {
        var q = craft.Orientation;
        if (ctl.Sas == SasMode.StabilityAssist) return ctl.HoldOrientation;
        var dir = SasDirection(craft, env, ctl);
        if (dir == null) return ctl.HoldOrientation;
        var nose = q.Rotate(Vec3d.UnitY);
        // Minimal rotation of the nose onto the target direction, preserving roll.
        return (QuatD.FromTo(nose, dir.Value) * q).Normalized;
    }

    public static Vec3d? SasDirection(Craft craft, in FlightEnvironment env, ControlState ctl)
    {
        bool useSurface = ctl.Reference == SpeedReference.Surface ||
            (ctl.Reference == SpeedReference.Auto && env.Body.HasAtmosphere && env.AltitudeSeaLevel < env.Body.Atmosphere!.Height * 0.5);
        var v = useSurface ? env.SurfaceVelocity : env.Velocity;
        if (ctl.Sas == SasMode.Maneuver)
            return ctl.ManeuverDirection.LengthSquared > 0.5 ? ctl.ManeuverDirection : null;
        if (v.Length < 0.5) return null;
        var pro = v.Normalized;
        var h = Vec3d.Cross(env.Position, v);
        var normal = h.LengthSquared > 1e-12 ? h.Normalized : Vec3d.UnitY;
        switch (ctl.Sas)
        {
            case SasMode.Prograde: return pro;
            case SasMode.Retrograde: return -pro;
            case SasMode.Normal: return normal;
            case SasMode.Antinormal: return -normal;
            case SasMode.RadialOut: return Vec3d.Cross(pro, normal).Normalized;
            case SasMode.RadialIn: return -Vec3d.Cross(pro, normal).Normalized;
            default: return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    //  RCS
    // ---------------------------------------------------------------------------------------
    private static Vec3d RcsAuthority(Craft craft)
    {
        double px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0;
        var com = craft.Mass.CenterOfMass;
        foreach (var p in craft.Parts)
        {
            var rcs = p.Def.Rcs;
            if (rcs == null || craft.AvailableFor(p, rcs.Propellant) < 1e-6) continue;
            foreach (var n in rcs.Nozzles)
            {
                var pos = p.Position + p.Rotation.Rotate(new Vec3d(n.Position[0], n.Position[1], n.Position[2]));
                var dir = p.Rotation.Rotate(new Vec3d(n.Direction[0], n.Direction[1], n.Direction[2]).Normalized);
                var t = Vec3d.Cross(pos - com, dir * n.Thrust);
                if (t.X > 0) px += t.X; else nx -= t.X;
                if (t.Y > 0) py += t.Y; else ny -= t.Y;
                if (t.Z > 0) pz += t.Z; else nz -= t.Z;
            }
        }
        return new Vec3d(Math.Min(px, nx) > 0 ? Math.Min(px, nx) : Math.Max(px, nx) * 0.5,
                         Math.Min(py, ny) > 0 ? Math.Min(py, ny) : Math.Max(py, ny) * 0.5,
                         Math.Min(pz, nz) > 0 ? Math.Min(pz, nz) : Math.Max(pz, nz) * 0.5);
    }

    /// <summary>Fire RCS nozzles to satisfy rotation command c and translation request.</summary>
    public static void ApplyRcs(Craft craft, Vec3d c, double dt, ForceAccumulator acc, double time)
    {
        var ctl = craft.Control;
        if (!ctl.RcsEnabled) return;
        var trans = ctl.Translation;
        bool wantRot = c.LengthSquared > 1e-4, wantTrans = trans.LengthSquared > 1e-6;
        if (!wantRot && !wantTrans) return;
        var com = craft.Mass.CenterOfMass;
        foreach (var p in craft.Parts)
        {
            var rcs = p.Def.Rcs;
            if (rcs == null) continue;
            foreach (var n in rcs.Nozzles)
            {
                var pos = p.Position + p.Rotation.Rotate(new Vec3d(n.Position[0], n.Position[1], n.Position[2]));
                var dir = p.Rotation.Rotate(new Vec3d(n.Direction[0], n.Direction[1], n.Direction[2]).Normalized);
                var tau = Vec3d.Cross(pos - com, dir * n.Thrust);
                double tl = tau.Length;
                double level = 0;
                if (wantRot && tl > 1e-9)
                {
                    double s = Vec3d.Dot(tau / tl, c);
                    if (s > 0.2) level = Math.Max(level, Math.Min(1.0, s * Math.Min(1.0, c.Length) * 1.5));
                }
                if (wantTrans)
                {
                    double s = Vec3d.Dot(dir, trans);
                    if (s > 0.3) level = Math.Max(level, Math.Min(1.0, s));
                }
                if (level <= 0) continue;
                double mdot = n.Thrust / (rcs.Isp * MathUtil.G0) * level;
                double got = craft.Draw(p, rcs.Propellant, mdot * dt);
                double f = mdot * dt > 0 ? got / (mdot * dt) : 0;
                if (f <= 0) continue;
                acc.Add(p, dir * (n.Thrust * level * f), pos);
            }
        }
    }
}
