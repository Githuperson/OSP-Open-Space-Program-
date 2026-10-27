using System.Collections.Generic;
using System.Linq;
using OSP.Core.Celestial;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight
{
    /// <summary>Environment data a physics step needs beyond the vessel itself.</summary>
    public struct FlightEnvironment
    {
        public double Time;
        /// <summary>Position of the star relative to the vessel's SOI body (m).</summary>
        public Vector3d SunPosition;
        /// <summary>Distance from the star at which solar panels produce their rated output (m).</summary>
        public double ReferenceSunDistance;
        /// <summary>Position of the current target relative to the vessel's SOI body, if any.</summary>
        public Vector3d? TargetPosition;
        public Vector3d TargetVelocity;
    }

    /// <summary>Things that happened during a step which the world must resolve (vessel splits, destruction).</summary>
    public sealed class PhysicsEvents
    {
        public readonly List<(int partId, string reason)> DestroyedParts = new();
        public readonly List<int> BrokenJoints = new();
        public readonly List<string> Messages = new();

        public bool Any => DestroyedParts.Count > 0 || BrokenJoints.Count > 0;
        public void Clear()
        {
            DestroyedParts.Clear();
            BrokenJoints.Clear();
            Messages.Clear();
        }
    }

    /// <summary>
    /// Rigid-body flight dynamics for one vessel (see docs/PHYSICS.md).
    ///
    /// Translational motion: RK4 integration of the CoM with gravity evaluated at every stage and the
    /// non-gravitational forces (thrust, aerodynamics, contact) held constant across the step.
    /// Rotational motion: semi-implicit Euler on the angular velocity with the full inertia tensor and the
    /// gyroscopic term, followed by exact quaternion integration of the rotation.
    /// All quantities SI; inertial frame centred on the vessel's SOI body.
    /// </summary>
    public static class VesselPhysics
    {
        public const double ContactFrequency = 3.0;      // Hz, hull contact spring
        public const double WaterFrequency = 1.2;        // Hz, buoyancy spring
        public const double ContactDamping = 0.8;
        public const double HullFriction = 0.8;
        public const int OverloadStepsToBreak = 3;

        /// <summary>Advances a vessel by dt seconds, subdividing near the ground for contact stability.</summary>
        public static void Step(Vessel v, FlightEnvironment env, double dt, PhysicsEvents events)
        {
            if (v.Parts.Count == 0 || v.Situation == Situation.Destroyed) return;
            v.PhysicsCache ??= new VesselPhysicsCache(v);
            double agl = v.Telemetry.AltitudeTerrain;
            int sub = (v.Telemetry.InContact || agl < 60 || v.Pinned) ? 4 : 1;
            double h = dt / sub;
            for (int i = 0; i < sub; i++)
            {
                SubStep(v, env, h, events);
                env.Time += h;
                if (events.Any) break; // let the world resolve topology changes first
            }
        }

        private static void SubStep(Vessel v, FlightEnvironment env, double dt, PhysicsEvents events)
        {
            CelestialBody body = v.Body;
            VesselPhysicsCache cache = v.PhysicsCache ??= new VesselPhysicsCache(v);
            double t = env.Time;

            // ---- Environment ------------------------------------------------------------------------
            double altitude = v.Position.Length - body.Radius;
            Atmosphere atmo = body.Atmosphere;
            double pressure = atmo?.Pressure(altitude) ?? 0;
            double pressureAtm = pressure / PhysicsConstants.StandardAtmosphere;
            double density = atmo?.Density(altitude) ?? 0;
            Vector3d airVelocity = v.Velocity - body.SurfaceVelocity(v.Position); // vessel relative to air
            double airspeed = airVelocity.Length;
            double q = 0.5 * density * airspeed * airspeed;
            double terrainHeight = body.SurfaceHeightBelow(v.Position, t);
            double agl = altitude - terrainHeight;

            UpdateModuleStates(v, dt, pressureAtm, agl, q, events);

            // ---- Pinned (resting on the surface) ---------------------------------------------------
            bool thrustCommanded = AnyThrustCommanded(v);
            if (v.Pinned)
            {
                if (thrustCommanded || v.Control.Translation.LengthSquared > 1e-6)
                {
                    v.Pinned = false;
                }
                else
                {
                    ApplyPinnedState(v, t + dt);
                    ProcessElectric(v, env, dt, Vector3d.Zero, 0);
                    FillTelemetry(v, env, t + dt, Vector3d.Zero, 0, 0, true, false);
                    return;
                }
            }

            int n = v.Parts.Count;
            var partForce = new Vector3d[n];
            Vector3d totalForce = Vector3d.Zero;
            Vector3d totalTorque = Vector3d.Zero;
            Vector3d thrustForce = Vector3d.Zero;
            QuaternionD rot = v.Rotation;
            double mass = v.Mass;
            Vector3d comLocal = v.CenterOfMassLocal;

            // ---- Control ------------------------------------------------------------------------------
            Vector3d authority = ControlAuthority(v, pressureAtm);
            Vector3d refVel = ReferenceVelocity(v, env);
            Vector3d sas = v.Autopilot.Update(v, authority, refVel, env.TargetPosition, t);
            VesselControl ctl = v.Control;
            double fine = ctl.PrecisionMode ? 0.35 : 1.0;
            Vector3d pilot = ctl.RotationVector * fine;
            Vector3d rotCmd = new(
                System.Math.Abs(pilot.X) > 1e-3 ? pilot.X : sas.X,
                System.Math.Abs(pilot.Y) > 1e-3 ? pilot.Y : sas.Y,
                System.Math.Abs(pilot.Z) > 1e-3 ? pilot.Z : sas.Z);
            bool hasPower = v.Network.Total(ResourceLibrary.ElectricCharge) > 1e-6;
            bool crewed = v.Parts.Any(p => p.Def.GetModule<CommandModuleDef>() is { IsProbe: false });
            bool controllable = crewed || (hasPower && v.Parts.Any(p => p.Def.GetModule<CommandModuleDef>() is { IsProbe: true }));
            if (!controllable)
            {
                rotCmd = Vector3d.Zero;
            }

            // ---- Engines --------------------------------------------------------------------------------
            double thrustTotal = 0, maxThrust = 0;
            int activeEngines = 0;
            bool flameout = false;
            foreach (VesselPhysicsCache.PartCache pc in cache.Parts)
            {
                PartInstance p = pc.Part;
                EngineModuleDef eng = p.Def.GetModule<EngineModuleDef>();
                if (eng == null || !p.State.Activated || p.State.Shutdown) continue;
                double target = eng.IsSolid ? 1.0 : (controllable || !crewed ? ctl.Throttle : 0);
                if (!eng.IsSolid && !controllable) target = 0;
                target *= p.ThrustLimit;
                if (target > 0 && target < eng.MinThrottle) target = eng.MinThrottle;
                double spool = eng.IsSolid ? 1 : 1 - System.Math.Exp(-dt / System.Math.Max(eng.SpoolTime, 1e-3));
                p.State.EngineThrottle += (target - p.State.EngineThrottle) * spool;
                double throttle = p.State.EngineThrottle;
                maxThrust += eng.ThrustAt(pressureAtm) * p.ThrustLimit;
                if (throttle < 1e-4) continue;
                double frac = Propulsion.ConsumePropellant(v.Network, p, eng, throttle, dt);
                if (frac <= 0)
                {
                    flameout = true;
                    if (eng.IsSolid) p.State.Shutdown = true; // burnt out
                    p.State.EngineThrottle = 0;
                    continue;
                }
                activeEngines++;
                double thrust = eng.ThrustAt(pressureAtm) * throttle * frac;
                Vector3d pointLocal = p.LocalToVessel(eng.ThrustPoint);
                Vector3d dirLocal = -p.DirectionToVessel(eng.ExhaustDirection).Normalized();
                if (eng.GimbalRange > 0 && controllable)
                {
                    Vector3d lever = pointLocal - comLocal;
                    Vector3d g = Vector3d.Cross(rotCmd, lever);
                    double ll = lever.Length;
                    if (ll > 1e-3) g /= ll;
                    g = g.ProjectOnPlane(dirLocal);
                    if (g.Length > 1) g = g.Normalized();
                    dirLocal = (dirLocal + g * System.Math.Tan(eng.GimbalRange * MathUtil.Deg2Rad)).Normalized();
                }
                Vector3d force = rot.Rotate(dirLocal) * thrust;
                Vector3d leverWorld = rot.Rotate(pointLocal - comLocal);
                partForce[pc.Index] += force;
                totalForce += force;
                thrustForce += force;
                totalTorque += Vector3d.Cross(leverWorld, force);
                thrustTotal += thrust;
            }

            // ---- RCS ------------------------------------------------------------------------------------
            if (ctl.RcsEnabled && controllable)
            {
                Vector3d trans = ctl.Translation;
                foreach (VesselPhysicsCache.PartCache pc in cache.Parts)
                {
                    PartInstance p = pc.Part;
                    RcsModuleDef rcs = p.Def.GetModule<RcsModuleDef>();
                    if (rcs == null) continue;
                    foreach (RcsNozzleDef nz in rcs.Nozzles)
                    {
                        Vector3d fLocal = -p.DirectionToVessel(nz.Direction).Normalized();
                        Vector3d lever = p.LocalToVessel(nz.Position) - comLocal;
                        double ll = System.Math.Max(lever.Length, 0.1);
                        double activation = Vector3d.Dot(fLocal, trans) + Vector3d.Dot(Vector3d.Cross(lever, fLocal) / ll, rotCmd);
                        activation = MathUtil.Clamp01(activation);
                        if (activation < 0.02) continue;
                        double thrust = rcs.ThrustPerNozzle * activation;
                        double need = thrust / (rcs.Isp * PhysicsConstants.G0) * dt;
                        double got = v.Network.Draw(p, rcs.Propellant, need);
                        if (got <= 0) continue;
                        thrust *= got / need;
                        Vector3d force = rot.Rotate(fLocal) * thrust;
                        partForce[pc.Index] += force;
                        totalForce += force;
                        thrustForce += force;
                        totalTorque += Vector3d.Cross(rot.Rotate(lever), force);
                    }
                }
            }

            // ---- Reaction wheels ---------------------------------------------------------------------
            double ecUse = 0;
            if (controllable && rotCmd.LengthSquared > 1e-8)
            {
                foreach (VesselPhysicsCache.PartCache pc in cache.Parts)
                {
                    ReactionWheelModuleDef rw = pc.Part.Def.GetModule<ReactionWheelModuleDef>();
                    if (rw == null) continue;
                    Vector3d cmd = new(MathUtil.Clamp(rotCmd.X, -1, 1), MathUtil.Clamp(rotCmd.Y, -1, 1), MathUtil.Clamp(rotCmd.Z, -1, 1));
                    double use = (System.Math.Abs(cmd.X) + System.Math.Abs(cmd.Y) + System.Math.Abs(cmd.Z)) * rw.ElectricUsage * dt;
                    double got = use > 0 ? v.Network.Draw(pc.Part, ResourceLibrary.ElectricCharge, use) : 0;
                    double eff = use > 0 ? got / use : 0;
                    ecUse += got;
                    totalTorque += rot.Rotate(cmd * (rw.Torque * eff));
                }
            }

            // ---- Aerodynamics ------------------------------------------------------------------------
            Vector3d omega = v.AngularVelocity;
            if (density > 0)
            {
                foreach (VesselPhysicsCache.PartCache pc in cache.Parts)
                {
                    PartInstance p = pc.Part;
                    QuaternionD partRot = rot * p.Rotation;
                    Vector3d centerLocal = p.LocalToVessel(p.Def.Bounds.Center);
                    Vector3d r = rot.Rotate(centerLocal - comLocal);
                    Vector3d vAir = airVelocity + Vector3d.Cross(omega, r);
                    double speed = vAir.Length;
                    if (speed < 1e-3) continue;
                    Vector3d u = partRot.InverseRotate(vAir);
                    Vector3d fLocal = PartDrag(pc, u, speed, density);
                    Vector3d drag = partRot.Rotate(fLocal);
                    partForce[pc.Index] += drag;
                    totalForce += drag;
                    totalTorque += Vector3d.Cross(r, drag);

                    LiftingSurfaceModuleDef wing = p.Def.GetModule<LiftingSurfaceModuleDef>();
                    if (wing != null)
                    {
                        Vector3d cp = rot.Rotate(p.LocalToVessel(wing.CenterOfPressure) - comLocal);
                        Vector3d vAt = airVelocity + Vector3d.Cross(omega, cp);
                        Vector3d nWorld = partRot.Rotate(wing.Normal).Normalized();
                        Vector3d lift = WingForce(vAt, nWorld, wing, density);
                        if (wing.IsControlSurface && controllable && rotCmd.LengthSquared > 1e-6)
                        {
                            // Deflect towards whichever direction produces torque along the command.
                            Vector3d hinge = partRot.Rotate(wing.HingeAxis).Normalized();
                            double defl = wing.MaxDeflection * MathUtil.Deg2Rad;
                            Vector3d nPlus = QuaternionD.FromAxisAngle(hinge, defl).Rotate(nWorld);
                            Vector3d deltaF = WingForce(vAt, nPlus, wing, density) - lift;
                            Vector3d deltaTorqueLocal = rot.InverseRotate(Vector3d.Cross(cp, deltaF));
                            double tl = deltaTorqueLocal.Length;
                            double k = tl > 1e-9 ? MathUtil.Clamp(Vector3d.Dot(deltaTorqueLocal, rotCmd) / tl, -1, 1) : 0;
                            p.State.Deploy = k; // used by the renderer to show deflection
                            Vector3d nDef = QuaternionD.FromAxisAngle(hinge, defl * k).Rotate(nWorld);
                            lift = WingForce(vAt, nDef, wing, density);
                        }
                        partForce[pc.Index] += lift;
                        totalForce += lift;
                        totalTorque += Vector3d.Cross(cp, lift);
                    }

                    ParachuteModuleDef chute = p.Def.GetModule<ParachuteModuleDef>();
                    if (chute != null && p.State.Parachute is ParachuteState.SemiDeployed or ParachuteState.Deployed)
                    {
                        double area = chute.DeployedDragArea * (p.State.Parachute == ParachuteState.Deployed
                            ? System.Math.Max(chute.SemiDeployedFraction, p.State.Deploy)
                            : chute.SemiDeployedFraction);
                        // The canopy pulls from a point above the part, which keeps the vessel hanging below it.
                        Vector3d attach = rot.Rotate(p.LocalToVessel(new Vector3d(0, p.Def.Bounds.Max.Y + 2, 0)) - comLocal);
                        Vector3d vChute = airVelocity + Vector3d.Cross(omega, attach);
                        Vector3d chuteForce = -vChute * (0.5 * density * vChute.Length * area);
                        partForce[pc.Index] += chuteForce;
                        totalForce += chuteForce;
                        totalTorque += Vector3d.Cross(attach, chuteForce);
                    }
                }
            }

            // ---- Terrain contact ---------------------------------------------------------------------
            bool inContact = false, splashed = false;
            ContactForces(v, cache, body, t, dt, partForce, ref totalForce, ref totalTorque, ref inContact, ref splashed, events);

            // ---- Integrate --------------------------------------------------------------------------------
            Vector3d aNonGrav = totalForce / mass;
            Integrate(v, body, aNonGrav, totalTorque, dt);
            v.AccumulatedThrustDeltaV += thrustForce / mass * dt;

            // ---- Structural loads -------------------------------------------------------------------------
            CheckJointLoads(v, cache, partForce, aNonGrav, events);

            // ---- Electricity, mass, telemetry ------------------------------------------------------------
            ProcessElectric(v, env, dt, Vector3d.Zero, ecUse);
            v.UpdateMassProperties();
            FillTelemetry(v, env, t + dt, aNonGrav, thrustTotal, maxThrust, inContact, splashed);
            v.Telemetry.ActiveEngines = activeEngines;
            v.Telemetry.FlameOut = flameout;

            // ---- Rest detection: pin to the surface when settled --------------------------------------------
            if (inContact && !thrustCommanded)
            {
                Vector3d surfVel = v.Velocity - body.SurfaceVelocity(v.Position);
                Vector3d relOmega = v.AngularVelocity - body.AngularVelocityVector;
                if (surfVel.Length < 0.05 && relOmega.Length < 0.01)
                {
                    PinToSurface(v, t + dt);
                }
            }
        }

        /// <summary>Body drag in part-local coordinates for local airflow u (vessel motion relative to air).</summary>
        private static Vector3d PartDrag(VesselPhysicsCache.PartCache pc, Vector3d u, double speed, double density)
        {
            PartDefinition def = pc.Part.Def;
            double half = 0.5 * density * speed;
            if (def.Bounds.IsRound)
            {
                double axial = u.Y;
                double cd = axial > 0 ? def.Drag.Top * pc.TopExposure : def.Drag.Bottom * pc.BottomExposure;
                double fy = -half * axial * pc.FaceArea * cd;
                Vector3d side = new(u.X, 0, u.Z);
                Vector3d fs = -side * (half * pc.SideArea * def.Drag.Side);
                return new Vector3d(fs.X, fy, fs.Z);
            }
            double cdx = def.Drag.Side, cdy = u.Y > 0 ? def.Drag.Top * pc.TopExposure : def.Drag.Bottom * pc.BottomExposure;
            return new Vector3d(
                -half * u.X * pc.BoxAreas.X * cdx,
                -half * u.Y * pc.BoxAreas.Y * cdy,
                -half * u.Z * pc.BoxAreas.Z * cdx);
        }

        /// <summary>Thin flat-plate normal force with a simple stall model, plus skin friction.</summary>
        public static Vector3d WingForce(Vector3d vAir, Vector3d normal, LiftingSurfaceModuleDef wing, double density)
        {
            double speed = vAir.Length;
            if (speed < 1e-3) return Vector3d.Zero;
            double vn = Vector3d.Dot(vAir, normal);
            double s = System.Math.Abs(vn) / speed; // |sin α|
            double cn = s < 0.2 ? MathUtil.TwoPi * s : 1.2566 + (1.6 - 1.2566) * ((s - 0.2) / 0.8);
            cn *= wing.LiftScale;
            double qa = 0.5 * density * speed * speed * wing.Area;
            Vector3d fn = -normal * (System.Math.Sign(vn) * cn * qa);
            Vector3d skin = -vAir / speed * (0.02 * qa);
            return fn + skin;
        }

        private static void ContactForces(Vessel v, VesselPhysicsCache cache, CelestialBody body, double t, double dt,
            Vector3d[] partForce, ref Vector3d totalForce, ref Vector3d totalTorque, ref bool inContact, ref bool splashed, PhysicsEvents events)
        {
            if (body.Terrain == null) return;
            double altitude = v.Position.Length - body.Radius;
            // Quick reject: far above the highest possible terrain.
            double boundR = 0;
            foreach (var pc in cache.Parts) boundR = System.Math.Max(boundR, (pc.Part.Position - v.CenterOfMassLocal).Length + pc.Part.Def.Bounds.Size.Length);
            if (altitude - boundR > 12000) return;

            double mass = v.Mass;
            Vector3d comLocal = v.CenterOfMassLocal;
            QuaternionD rot = v.Rotation;
            QuaternionD bodyRot = body.RotationAt(t);

            // Gather candidate contacts first so stiffness can be shared among the points in contact.
            var contacts = new List<(VesselPhysicsCache.PartCache pc, int idx, Vector3d world, double depth, Vector3d normal, bool water, bool foot, LandingLegModuleDef leg)>();
            foreach (VesselPhysicsCache.PartCache pc in cache.Parts)
            {
                PartInstance p = pc.Part;
                LandingLegModuleDef leg = p.Def.GetModule<LandingLegModuleDef>();
                for (int i = 0; i < pc.ContactPoints.Length; i++)
                {
                    Vector3d world = rot.Rotate(p.LocalToVessel(pc.ContactPoints[i]) - comLocal) + v.Position;
                    if (TryContact(body, bodyRot, world, out double depth, out Vector3d normal, out bool water))
                        contacts.Add((pc, i, world, depth, normal, water, false, null));
                    else pc.ContactLastStep[i] = false;
                }
                if (leg != null)
                {
                    Vector3d footLocal = Vector3d.Lerp(leg.FootStowed, leg.FootDeployed, p.State.Deploy);
                    Vector3d world = rot.Rotate(p.LocalToVessel(footLocal) - comLocal) + v.Position;
                    if (TryContact(body, bodyRot, world, out double depth, out Vector3d normal, out bool water))
                        contacts.Add((pc, -1, world, depth, normal, water, true, leg));
                    else pc.FootContactLastStep = false;
                }
            }
            if (contacts.Count == 0) return;
            inContact = true;

            int hullCount = contacts.Count(c => !c.foot);
            int footCount = contacts.Count(c => c.foot);
            foreach (var c in contacts)
            {
                PartInstance p = c.pc.Part;
                double share = mass / (c.foot ? footCount : System.Math.Max(hullCount + footCount * 0, 1));
                double freq = c.water ? WaterFrequency : (c.foot ? c.leg.SpringFrequency : ContactFrequency);
                double omega0 = MathUtil.TwoPi * freq;
                double k = share * omega0 * omega0;
                double damping = 2 * (c.foot ? c.leg.DampingRatio : ContactDamping) * share * omega0;
                if (c.foot && c.depth > c.leg.Stroke) k *= 1 + 9 * System.Math.Min(1, (c.depth - c.leg.Stroke) / c.leg.Stroke);

                Vector3d r = c.world - v.Position;
                Vector3d vPoint = v.Velocity + Vector3d.Cross(v.AngularVelocity, r) - body.SurfaceVelocity(c.world);
                double vn = Vector3d.Dot(vPoint, c.normal);

                // Impact check on first touch.
                bool wasInContact = c.foot ? c.pc.FootContactLastStep : c.pc.ContactLastStep[c.idx];
                if (!wasInContact)
                {
                    double tolerance = p.Def.CrashTolerance * (c.water ? 1.5 : 1.0);
                    if (-vn > tolerance)
                    {
                        if (!events.DestroyedParts.Any(d => d.partId == p.Id))
                            events.DestroyedParts.Add((p.Id, $"{p.Def.Name} hit the {(c.water ? "water" : "ground")} at {-vn:F1} m/s"));
                    }
                }
                if (c.foot) c.pc.FootContactLastStep = true; else c.pc.ContactLastStep[c.idx] = true;
                if (c.water) splashed = true;

                double normalMag = System.Math.Max(0, k * c.depth - damping * vn);
                // Cap per-step impulse so a stiff contact cannot reverse velocity in one step.
                Vector3d fn = c.normal * normalMag;
                Vector3d vt = vPoint - c.normal * vn;
                double vtLen = vt.Length;
                Vector3d ft = Vector3d.Zero;
                if (vtLen > 1e-5)
                {
                    double mu = c.water ? 0.25 : (c.foot ? c.leg.Friction : HullFriction);
                    double coulomb = mu * normalMag;
                    double viscousCap = share * vtLen / dt * 0.5;
                    ft = -vt / vtLen * System.Math.Min(coulomb, viscousCap);
                }
                Vector3d f = fn + ft;
                partForce[c.pc.Index] += f;
                totalForce += f;
                totalTorque += Vector3d.Cross(r, f);
            }
        }

        private static bool TryContact(CelestialBody body, QuaternionD bodyRot, Vector3d world, out double depth, out Vector3d normal, out bool water)
        {
            Vector3d bf = bodyRot.InverseRotate(world);
            double rLen = bf.Length;
            Vector3d dir = bf / rLen;
            double raw = body.Terrain.RawHeight(dir);
            water = body.Terrain.Settings.HasOcean && raw < 0;
            double surface = body.Radius + (water ? 0 : raw);
            depth = surface - rLen;
            normal = Vector3d.Zero;
            if (depth <= 0) return false;
            Vector3d nBf = water ? dir : body.Terrain.SurfaceNormal(dir);
            depth *= System.Math.Max(0.2, Vector3d.Dot(nBf, dir));
            normal = bodyRot.Rotate(nBf);
            return true;
        }

        private static void Integrate(Vessel v, CelestialBody body, Vector3d aNonGrav, Vector3d torque, double dt)
        {
            double mu = body.Mu;
            Vector3d Accel(Vector3d x)
            {
                double r2 = x.LengthSquared;
                double r = System.Math.Sqrt(r2);
                return x * (-mu / (r2 * r)) + aNonGrav;
            }
            Vector3d x0 = v.Position, v0 = v.Velocity;
            Vector3d k1v = Accel(x0), k1x = v0;
            Vector3d k2v = Accel(x0 + k1x * (dt / 2)), k2x = v0 + k1v * (dt / 2);
            Vector3d k3v = Accel(x0 + k2x * (dt / 2)), k3x = v0 + k2v * (dt / 2);
            Vector3d k4v = Accel(x0 + k3x * dt), k4x = v0 + k3v * dt;
            v.Position = x0 + (k1x + 2 * k2x + 2 * k3x + k4x) * (dt / 6);
            v.Velocity = v0 + (k1v + 2 * k2v + 2 * k3v + k4v) * (dt / 6);

            Matrix3d I = v.WorldInertia();
            Matrix3d Iinv = I.Inverse();
            Vector3d w = v.AngularVelocity;
            Vector3d gyro = Vector3d.Cross(w, I * w);
            Vector3d alpha = Iinv * (torque - gyro);
            w += alpha * dt;
            // Guard against numerical blow-up from extreme inertia ratios.
            if (!w.IsFinite) w = Vector3d.Zero;
            if (w.Length > 50) w = w.Normalized() * 50;
            v.AngularVelocity = w;
            v.Rotation = v.Rotation.Integrate(w, dt);
            v.LastAngularAcceleration = alpha;
        }

        /// <summary>
        /// Estimates the force carried by each joint as the net external non-gravitational force on the subtree
        /// minus what is needed to give the subtree the vessel's (rigid-body) acceleration:
        /// F_joint = Σ_subtree [F_i − m_i (a + α × r_i + ω × (ω × r_i))]. Joints overloaded for several consecutive
        /// steps break.
        /// </summary>
        private static void CheckJointLoads(Vessel v, VesselPhysicsCache cache, Vector3d[] partForce, Vector3d aNonGrav, PhysicsEvents events)
        {
            int n = cache.Parts.Count;
            if (n < 2) return;
            var load = new Vector3d[n];
            Vector3d omega = v.AngularVelocity;
            Vector3d alpha = v.LastAngularAcceleration;
            foreach (int idx in cache.PostOrder)
            {
                VesselPhysicsCache.PartCache pc = cache.Parts[idx];
                PartInstance p = pc.Part;
                Vector3d r = v.Rotation.Rotate(p.LocalToVessel(p.Def.Bounds.Center) - v.CenterOfMassLocal);
                Vector3d aLocal = aNonGrav + Vector3d.Cross(alpha, r) + Vector3d.Cross(omega, Vector3d.Cross(omega, r));
                load[idx] += partForce[idx] - aLocal * p.TotalMass;
                if (pc.ParentIndex >= 0)
                {
                    load[pc.ParentIndex] += load[idx];
                    PartInstance parent = cache.Parts[pc.ParentIndex].Part;
                    double limit = System.Math.Min(p.Def.BreakingForce, parent.Def.BreakingForce);
                    if (load[idx].Length > limit)
                    {
                        pc.OverloadSteps++;
                        if (pc.OverloadSteps >= OverloadStepsToBreak && !events.BrokenJoints.Contains(p.Id))
                        {
                            events.BrokenJoints.Add(p.Id);
                            events.Messages.Add($"Structural failure: {p.Def.Name} tore away from {parent.Def.Name} ({load[idx].Length / 1000:F0} kN).");
                        }
                    }
                    else pc.OverloadSteps = 0;
                }
            }
        }

        // ---- Module state machines ------------------------------------------------------------------------

        private static void UpdateModuleStates(Vessel v, double dt, double pressureAtm, double agl, double q, PhysicsEvents events)
        {
            foreach (PartInstance p in v.Parts)
            {
                PartFlightState s = p.State;
                ParachuteModuleDef chute = p.Def.GetModule<ParachuteModuleDef>();
                if (chute != null)
                {
                    switch (s.Parachute)
                    {
                        case ParachuteState.Armed:
                            if (pressureAtm >= chute.MinPressure) s.Parachute = ParachuteState.SemiDeployed;
                            break;
                        case ParachuteState.SemiDeployed:
                            s.Deploy = MoveTo(s.Deploy, chute.SemiDeployedFraction, dt / 1.0);
                            if (agl < chute.DeployAltitude) s.Parachute = ParachuteState.Deployed;
                            break;
                        case ParachuteState.Deployed:
                            s.Deploy = MoveTo(s.Deploy, 1, dt / (chute.Animation?.Duration ?? 3.0));
                            if (q > chute.MaxDynamicPressure)
                            {
                                s.Parachute = ParachuteState.Cut;
                                s.Broken = true;
                                events.Messages.Add($"{p.Def.Name} was torn off by aerodynamic forces ({q / 1000:F1} kPa).");
                            }
                            break;
                        case ParachuteState.Cut:
                            s.Deploy = 0;
                            break;
                    }
                }

                LandingLegModuleDef leg = p.Def.GetModule<LandingLegModuleDef>();
                if (leg != null)
                    s.Deploy = MoveTo(s.Deploy, s.DeployTarget ? 1 : 0, dt / (leg.Animation?.Duration ?? 1.5));

                SolarPanelModuleDef panel = p.Def.GetModule<SolarPanelModuleDef>();
                if (panel != null && panel.Deployable && !s.Broken)
                {
                    s.Deploy = MoveTo(s.Deploy, s.DeployTarget ? 1 : 0, dt / (panel.Animation?.Duration ?? 3.0));
                    if (s.Deploy > 0.05 && q > panel.MaxDynamicPressure)
                    {
                        s.Broken = true;
                        events.Messages.Add($"{p.Def.Name} snapped off in the airflow.");
                    }
                }
            }
        }

        private static double MoveTo(double current, double target, double step) => MathUtil.MoveTowards(current, target, step);

        private static bool AnyThrustCommanded(Vessel v)
        {
            foreach (PartInstance p in v.Parts)
            {
                EngineModuleDef e = p.Def.GetModule<EngineModuleDef>();
                if (e == null || !p.State.Activated || p.State.Shutdown) continue;
                if (e.IsSolid) return true;
                if (v.Control.Throttle * p.ThrustLimit > 1e-4 && Propulsion.HasPropellant(v.Network, p, e)) return true;
            }
            return false;
        }

        /// <summary>Maximum angular acceleration about each vessel axis from wheels, gimbals and RCS (rad/s²).</summary>
        public static Vector3d ControlAuthority(Vessel v, double pressureAtm)
        {
            Vector3d torque = Vector3d.Zero;
            Vector3d comLocal = v.CenterOfMassLocal;
            foreach (PartInstance p in v.Parts)
            {
                if (p.Def.GetModule<ReactionWheelModuleDef>() is { } rw) torque += Vector3d.One * rw.Torque;
                if (p.Def.GetModule<EngineModuleDef>() is { } e && p.State != null && p.State.Activated && !p.State.Shutdown && e.GimbalRange > 0)
                {
                    double thrust = e.ThrustAt(pressureAtm) * System.Math.Max(p.State.EngineThrottle, 0.0);
                    Vector3d lever = p.LocalToVessel(e.ThrustPoint) - comLocal;
                    double s = System.Math.Sin(e.GimbalRange * MathUtil.Deg2Rad) * thrust;
                    torque += new Vector3d(s * System.Math.Sqrt(lever.Y * lever.Y + lever.Z * lever.Z),
                                           s * System.Math.Sqrt(lever.X * lever.X + lever.Z * lever.Z) * 0.5,
                                           s * System.Math.Sqrt(lever.X * lever.X + lever.Y * lever.Y));
                }
                if (v.Control.RcsEnabled && p.Def.GetModule<RcsModuleDef>() is { } rcs)
                {
                    double arm = (p.Position - comLocal).Length;
                    torque += Vector3d.One * (rcs.ThrustPerNozzle * arm);
                }
                if (p.Def.GetModule<LiftingSurfaceModuleDef>() is { IsControlSurface: true } cs)
                {
                    double arm = (p.Position - comLocal).Length;
                    double qGuess = v.Telemetry.DynamicPressure;
                    torque += Vector3d.One * (qGuess * cs.Area * 1.5 * arm * 0.5);
                }
            }
            Matrix3d I = v.MassProps.Inertia;
            return new Vector3d(
                torque.X / System.Math.Max(I.M00, 1e-3),
                torque.Y / System.Math.Max(I.M11, 1e-3),
                torque.Z / System.Math.Max(I.M22, 1e-3));
        }

        /// <summary>Velocity the navball/SAS prograde modes refer to.</summary>
        public static Vector3d ReferenceVelocity(Vessel v, FlightEnvironment env)
        {
            return v.Control.SpeedReference switch
            {
                SpeedReference.Surface => v.Velocity - v.Body.SurfaceVelocity(v.Position),
                SpeedReference.Target => env.TargetPosition.HasValue ? v.Velocity - env.TargetVelocity : v.Velocity,
                _ => v.Velocity,
            };
        }

        // ---- Electricity --------------------------------------------------------------------------------

        private static void ProcessElectric(Vessel v, FlightEnvironment env, double dt, Vector3d unused, double alreadyUsed)
        {
            double use = 0;
            foreach (PartInstance p in v.Parts)
            {
                if (p.Def.GetModule<CommandModuleDef>() is { IsProbe: true } cmd) use += cmd.ElectricUsage * dt;
            }
            double drawn = use > 0 ? v.Network.Draw(v.Root, ResourceLibrary.ElectricCharge, use) : 0;

            double gen = SolarGeneration(v, env);
            double stored = v.Network.Add(ResourceLibrary.ElectricCharge, gen * dt);
            v.Telemetry.ElectricGeneration = gen;
            v.Telemetry.ElectricConsumption = dt > 0 ? (drawn + alreadyUsed) / dt : 0;
            _ = stored;
        }

        /// <summary>Total solar output (EC/s): rated power × (d_ref/d)² × cos(incidence), zero in the body's shadow.</summary>
        public static double SolarGeneration(Vessel v, FlightEnvironment env)
        {
            Vector3d toSun = env.SunPosition - v.Position;
            double dist = toSun.Length;
            if (dist < 1) return 0;
            Vector3d sunDir = toSun / dist;
            bool shadow = InShadow(v.Position, sunDir, v.Body.Radius);
            v.Telemetry.SunExposure = shadow ? 0 : 1;
            if (shadow) return 0;
            double distFactor = env.ReferenceSunDistance > 0 ? (env.ReferenceSunDistance / dist) * (env.ReferenceSunDistance / dist) : 1;
            double total = 0;
            foreach (PartInstance p in v.Parts)
            {
                SolarPanelModuleDef sp = p.Def.GetModule<SolarPanelModuleDef>();
                if (sp == null || p.State.Broken) continue;
                double deploy = sp.Deployable ? p.State.Deploy : 1;
                if (deploy < 0.99) continue;
                Vector3d n = (v.Rotation * p.Rotation).Rotate(sp.PanelNormal).Normalized();
                double c = Vector3d.Dot(n, sunDir);
                if (sp.DoubleSided) c = System.Math.Abs(c);
                total += sp.PowerOutput * System.Math.Max(0, c) * distFactor;
            }
            return total;
        }

        /// <summary>True if the segment towards the sun from <paramref name="position"/> is blocked by a sphere at the origin.</summary>
        public static bool InShadow(Vector3d position, Vector3d sunDir, double radius)
        {
            double along = Vector3d.Dot(position, sunDir);
            if (along > 0) return false; // on the day side
            Vector3d perp = position - sunDir * along;
            return perp.Length < radius;
        }

        // ---- Surface pinning ------------------------------------------------------------------------------

        public static void PinToSurface(Vessel v, double time)
        {
            QuaternionD bodyRot = v.Body.RotationAt(time);
            v.Pinned = true;
            v.PinnedBodyFixedPosition = bodyRot.InverseRotate(v.Position);
            v.PinnedBodyFixedRotation = (bodyRot.Inverse() * v.Rotation).Normalized();
            ApplyPinnedState(v, time);
        }

        public static void ApplyPinnedState(Vessel v, double time)
        {
            QuaternionD bodyRot = v.Body.RotationAt(time);
            v.Position = bodyRot.Rotate(v.PinnedBodyFixedPosition);
            v.Velocity = v.Body.SurfaceVelocity(v.Position);
            v.Rotation = (bodyRot * v.PinnedBodyFixedRotation).Normalized();
            v.AngularVelocity = v.Body.AngularVelocityVector;
        }

        // ---- Telemetry ------------------------------------------------------------------------------------

        public static void FillTelemetry(Vessel v, FlightEnvironment env, double time, Vector3d aNonGrav, double thrust, double maxThrust, bool contact, bool splashed)
        {
            CelestialBody body = v.Body;
            FlightTelemetry tm = v.Telemetry;
            double r = v.Position.Length;
            tm.AltitudeSeaLevel = r - body.Radius;
            Vector3d bf = body.InertialToBodyFixed(v.Position, time);
            double terrain = body.SurfaceHeight(bf);
            tm.AltitudeTerrain = tm.AltitudeSeaLevel - System.Math.Max(0, terrain);
            if (body.Terrain != null && !body.Terrain.Settings.HasOcean) tm.AltitudeTerrain = tm.AltitudeSeaLevel - terrain;
            CelestialBody.LatLonFromDirection(bf, out tm.Latitude, out tm.Longitude);
            Vector3d up = v.Position / r;
            Vector3d surfVel = v.Velocity - body.SurfaceVelocity(v.Position);
            tm.VerticalSpeed = Vector3d.Dot(v.Velocity, up);
            tm.SurfaceSpeed = surfVel.Length;
            tm.HorizontalSurfaceSpeed = surfVel.ProjectOnPlane(up).Length;
            tm.OrbitalSpeed = v.Velocity.Length;
            Atmosphere atmo = body.Atmosphere;
            tm.StaticPressure = atmo?.Pressure(tm.AltitudeSeaLevel) ?? 0;
            tm.Density = atmo?.Density(tm.AltitudeSeaLevel) ?? 0;
            tm.DynamicPressure = 0.5 * tm.Density * tm.SurfaceSpeed * tm.SurfaceSpeed;
            tm.Mach = atmo != null ? tm.SurfaceSpeed / atmo.SpeedOfSound : 0;
            tm.GForce = aNonGrav.Length / PhysicsConstants.G0;
            tm.Thrust = thrust;
            tm.MaxThrust = maxThrust;
            double localG = body.Mu / (r * r);
            tm.Twr = v.Mass > 0 ? maxThrust / (v.Mass * localG) : 0;
            tm.InContact = contact;
            tm.Splashed = splashed;
        }
    }
}
