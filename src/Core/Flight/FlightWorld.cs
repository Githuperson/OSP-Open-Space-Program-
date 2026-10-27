using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core.Celestial;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight
{
    public sealed class FlightLogEntry
    {
        public double Time;
        public string Message;
        public bool Important;
    }

    /// <summary>
    /// The flight simulation: universal time, all vessels, time warp, staging, vessel splitting/docking and
    /// sphere-of-influence changes. Rendering code reads state from here and never writes physics state directly.
    /// </summary>
    public sealed class FlightWorld
    {
        /// <summary>Fixed physics step (s).</summary>
        public const double PhysicsStep = 0.02;
        /// <summary>Vessels within this distance of the active vessel are simulated with full physics (m).</summary>
        public const double LoadDistance = 2500;
        /// <summary>Maximum physics steps per frame before the simulation slows down instead of spiralling.</summary>
        public const int MaxStepsPerFrame = 24;

        public CelestialSystem SolarSystem { get; }
        public PartCatalog Catalog { get; }
        public double Time { get; set; }
        public List<Vessel> Vessels { get; } = new();
        public Vessel ActiveVessel { get; private set; }
        public TimeWarp Warp { get; } = new();
        public bool Paused { get; set; }
        public List<FlightLogEntry> Log { get; } = new();
        public event Action<FlightLogEntry> Logged;
        /// <summary>Raised when a vessel's part tree changes (staging, breakage, docking) so renderers can rebuild.</summary>
        public event Action<Vessel> VesselChanged;
        public event Action<Vessel> VesselRemoved;
        public event Action<Vessel> VesselAdded;

        public CelestialBody TargetBody { get; set; }
        public Vessel TargetVessel { get; set; }

        private double _accumulator;
        private readonly PhysicsEvents _events = new();
        private static readonly string[] CrewNames = { "Ada Voss", "Tomas Reyes", "Mira Okafor", "Lin Haldane", "Sven Ortega", "Priya Nandi", "Jonah Kell", "Ilse Marrow" };
        private int _crewIndex;

        public FlightWorld(CelestialSystem system, PartCatalog catalog)
        {
            SolarSystem = system;
            Catalog = catalog;
        }

        // ----- Logging -----------------------------------------------------------------------------------

        public void Message(string text, bool important = false)
        {
            var e = new FlightLogEntry { Time = Time, Message = text, Important = important };
            Log.Add(e);
            if (Log.Count > 200) Log.RemoveAt(0);
            Logged?.Invoke(e);
        }

        // ----- Vessel creation ---------------------------------------------------------------------------

        /// <summary>Creates a vessel from a design and places it at rest on the launch site.</summary>
        public Vessel Launch(VesselDesign design)
        {
            if (design.Parts.Count == 0) throw new InvalidOperationException("Cannot launch an empty design.");
            Vessel v = CreateFromDesign(design);
            CelestialBody body = SolarSystem.HomeBody;
            FlatSite site = SolarSystem.LaunchSite;
            PlaceOnSurface(v, body, site.Latitude, site.Longitude, 0.05);
            v.Situation = Situation.Prelaunch;
            v.LaunchTime = Time;
            AddVessel(v);
            SetActive(v);
            Message($"{v.Name} is on the pad at {site.Name}.");
            return v;
        }

        public Vessel CreateFromDesign(VesselDesign design)
        {
            var v = new Vessel { Name = design.Name };
            foreach (PartInstance src in design.Parts)
            {
                PartInstance p = src.Clone();
                p.Def = src.Def ?? Catalog.Get(src.Part) ?? throw new InvalidOperationException($"Unknown part '{src.Part}'.");
                p.State = new PartFlightState();
                foreach (ResourceAmountDef r in p.Def.Resources)
                    if (!p.Resources.ContainsKey(r.Resource)) p.Resources[r.Resource] = r.Amount;
                if (p.Def.GetModule<CommandModuleDef>() is { CrewCapacity: > 0 } cmd)
                {
                    for (int i = 0; i < cmd.CrewCapacity; i++) p.State.Crew.Add(CrewNames[_crewIndex++ % CrewNames.Length]);
                }
                if (p.Def.GetModule<LandingLegModuleDef>() is { StartDeployed: true }) { p.State.Deploy = 1; p.State.DeployTarget = true; }
                if (p.Def.GetModule<SolarPanelModuleDef>() is { Deployable: false }) p.State.Deploy = 1;
                v.Parts.Add(p);
            }
            // Root first.
            PartInstance root = v.Parts.FirstOrDefault(p => p.Parent < 0);
            if (root != null && v.Parts[0] != root)
            {
                v.Parts.Remove(root);
                v.Parts.Insert(0, root);
            }
            v.RebuildTopology();
            v.Type = ClassifyType(v);
            return v;
        }

        /// <summary>Places a vessel upright on the surface: vessel +Y = local up, +X = east. Lowest hull point sits <paramref name="clearance"/> above the terrain.</summary>
        public void PlaceOnSurface(Vessel v, CelestialBody body, double latDeg, double lonDeg, double clearance)
        {
            Vector3d up = CelestialBody.DirectionFromLatLon(latDeg, lonDeg);
            Vector3d east = Vector3d.Cross(Vector3d.UnitY, up).Normalized();
            if (east.LengthSquared < 0.5) east = Vector3d.UnitX;
            Vector3d north = Vector3d.Cross(up, east);
            QuaternionD bodyFixedRot = QuaternionD.FromBasis(east, up, -north);

            // Lowest point of the vessel along its +Y axis, relative to the CoM.
            double lowest = double.MaxValue;
            foreach (PartInstance p in v.Parts)
            {
                foreach (Vector3d c in VesselPhysicsCache.ContactPointsFor(p.Def))
                    lowest = System.Math.Min(lowest, p.LocalToVessel(c).Y);
                if (p.Def.GetModule<LandingLegModuleDef>() is { } leg)
                    lowest = System.Math.Min(lowest, p.LocalToVessel(Vector3d.Lerp(leg.FootStowed, leg.FootDeployed, p.State.Deploy)).Y);
            }
            double comHeight = v.CenterOfMassLocal.Y - lowest;
            double ground = body.Radius + body.SurfaceHeight(up);
            Vector3d bodyFixedPos = up * (ground + comHeight + clearance);
            // Correct for horizontal CoM offset (keeps the lowest point directly above the site).
            Vector3d comOffset = bodyFixedRot.Rotate(new Vector3d(v.CenterOfMassLocal.X, 0, v.CenterOfMassLocal.Z));
            bodyFixedPos += comOffset;

            v.Body = body;
            v.PinnedBodyFixedPosition = bodyFixedPos;
            v.PinnedBodyFixedRotation = bodyFixedRot;
            v.Pinned = true;
            VesselPhysics.ApplyPinnedState(v, Time);
            v.OnRails = false;
            VesselPhysics.FillTelemetry(v, Environment(v), Time, Vector3d.Zero, 0, 0, true, false);
        }

        /// <summary>Places a vessel in a circular equatorial orbit (used by tests and the debug menu).</summary>
        public void PlaceInOrbit(Vessel v, CelestialBody body, double altitude, double inclinationDeg = 0)
        {
            Orbit o = Orbit.Circular(body.Mu, body.Radius + altitude, inclinationDeg * MathUtil.Deg2Rad, 0, Time);
            v.Body = body;
            v.Position = o.EpochPosition;
            v.Velocity = o.EpochVelocity;
            v.Rotation = QuaternionD.FromToRotation(Vector3d.UnitY, o.EpochVelocity.Normalized()); // nose prograde
            v.AngularVelocity = Vector3d.Zero;
            v.Pinned = false;
            v.OnRails = false;
            v.Telemetry.InContact = false;
            v.Telemetry.Splashed = false;
            v.Situation = Situation.Orbiting;
            UpdateSituation(v);
        }

        public void AddVessel(Vessel v)
        {
            Vessels.Add(v);
            VesselAdded?.Invoke(v);
        }

        public void RemoveVessel(Vessel v)
        {
            Vessels.Remove(v);
            if (TargetVessel == v) TargetVessel = null;
            VesselRemoved?.Invoke(v);
        }

        public void SetActive(Vessel v)
        {
            if (v == null) return;
            ActiveVessel = v;
            if (TargetVessel == v) TargetVessel = null;
            if (v.OnRails && Warp.IsRealTime) GoOffRails(v);
            UpdateLoading();
        }

        /// <summary>Cycles the active vessel among loaded vessels (prefers controllable ones).</summary>
        public void SwitchVessel(int direction)
        {
            var candidates = Vessels.Where(x => x.Loaded && x.Situation != Situation.Destroyed).ToList();
            if (candidates.Count < 2 || ActiveVessel == null) return;
            int i = candidates.IndexOf(ActiveVessel);
            Vessel next = candidates[((i + direction) % candidates.Count + candidates.Count) % candidates.Count];
            SetActive(next);
            Message($"Switched to {next.Name}.");
        }

        private static VesselType ClassifyType(Vessel v)
        {
            if (v.Parts.Any(p => p.Def.GetModule<CommandModuleDef>() is { IsProbe: false })) return VesselType.Ship;
            if (v.Parts.Any(p => p.Def.HasModule<CommandModuleDef>())) return VesselType.Probe;
            return VesselType.Debris;
        }

        // ----- Environment -----------------------------------------------------------------------------

        public FlightEnvironment Environment(Vessel v)
        {
            CelestialBody home = SolarSystem.HomeBody;
            double refDist = home?.AbsolutePosition(Time).Length ?? 1.36e10;
            var env = new FlightEnvironment
            {
                Time = Time,
                SunPosition = -v.Body.AbsolutePosition(Time),
                ReferenceSunDistance = refDist,
            };
            if (TryGetTargetState(v.Body, out Vector3d tp, out Vector3d tv))
            {
                env.TargetPosition = tp;
                env.TargetVelocity = tv;
            }
            return env;
        }

        /// <summary>Target position/velocity relative to <paramref name="frame"/>.</summary>
        public bool TryGetTargetState(CelestialBody frame, out Vector3d position, out Vector3d velocity)
        {
            if (TargetVessel != null && Vessels.Contains(TargetVessel))
            {
                Vessel t = TargetVessel;
                PatchedConics.Transform(t.Body, frame, Time, t.Position, t.Velocity, out position, out velocity);
                return true;
            }
            if (TargetBody != null)
            {
                position = TargetBody.AbsolutePosition(Time) - frame.AbsolutePosition(Time);
                velocity = TargetBody.AbsoluteVelocity(Time) - frame.AbsoluteVelocity(Time);
                return true;
            }
            position = velocity = Vector3d.Zero;
            return false;
        }

        // ----- Main loop ---------------------------------------------------------------------------------

        /// <summary>Advances the simulation by a frame of real time (s).</summary>
        public void Update(double realDelta)
        {
            if (Paused || ActiveVessel == null) return;
            realDelta = System.Math.Min(realDelta, 0.1);
            EnforceWarpLimits();

            if (Warp.IsRails)
            {
                _accumulator = 0;
                AdvanceRails(realDelta * Warp.Rate);
                return;
            }

            _accumulator += realDelta * Warp.Rate;
            int steps = 0;
            while (_accumulator >= PhysicsStep && steps < MaxStepsPerFrame)
            {
                StepPhysics(PhysicsStep);
                _accumulator -= PhysicsStep;
                steps++;
            }
            if (steps >= MaxStepsPerFrame) _accumulator = 0; // drop time rather than spiral
        }

        /// <summary>One fixed physics step for every vessel.</summary>
        public void StepPhysics(double dt)
        {
            foreach (Vessel v in Vessels.ToList())
            {
                if (!Vessels.Contains(v)) continue;
                if (v.Loaded && !v.OnRails)
                {
                    _events.Clear();
                    VesselPhysics.Step(v, Environment(v), dt, _events);
                    foreach (string m in _events.Messages) Message(m, true);
                    if (_events.Any) ResolveEvents(v, _events);
                    if (Vessels.Contains(v)) UpdateSituation(v);
                }
                else if (v.Pinned)
                {
                    VesselPhysics.ApplyPinnedState(v, Time + dt);
                }
                else if (v.OnRails && v.RailsOrbit != null)
                {
                    v.RailsOrbit.GetStateAt(Time + dt, out Vector3d p, out Vector3d vel);
                    v.Position = p;
                    v.Velocity = vel;
                    v.Rotation = v.Rotation.Integrate(v.AngularVelocity, dt);
                }
            }
            Time += dt;
            foreach (Vessel v in Vessels.ToList()) CheckSoi(v);
            CheckDocking();
            UpdateLoading();
        }

        private void AdvanceRails(double simDelta)
        {
            double end = Time + simDelta;
            int guard = 0;
            while (Time < end && guard++ < 32)
            {
                double chunkEnd = end;
                Vessel active = ActiveVessel;
                // Danger: the active vessel descending below the altitude allowed for this warp rate.
                if (active.OnRails && active.RailsOrbit != null)
                {
                    double limit = WarpAltitudeLimit(active.Body, Warp.Index);
                    double rLimit = active.Body.Radius + limit;
                    if (active.RailsOrbit.PeriapsisRadius < rLimit && active.Position.Length > rLimit)
                    {
                        double tDanger = active.RailsOrbit.NextTimeAtRadius(rLimit, Time, false);
                        if (!double.IsNaN(tDanger) && tDanger < chunkEnd)
                        {
                            chunkEnd = tDanger;
                        }
                    }
                }
                // SOI transitions for every vessel on rails within the chunk.
                foreach (Vessel v in Vessels)
                {
                    if (!v.OnRails || v.RailsOrbit == null) continue;
                    var tr = PatchedConics.FindTransition(v.RailsOrbit, v.Body, Time, chunkEnd);
                    if (tr.HasValue && tr.Value.Time < chunkEnd) chunkEnd = System.Math.Max(Time + 1e-3, tr.Value.Time);
                }

                foreach (Vessel v in Vessels)
                {
                    if (v.Pinned) VesselPhysics.ApplyPinnedState(v, chunkEnd);
                    else if (v.OnRails && v.RailsOrbit != null)
                    {
                        v.RailsOrbit.GetStateAt(chunkEnd, out Vector3d p, out Vector3d vel);
                        v.Position = p;
                        v.Velocity = vel;
                    }
                }
                Time = chunkEnd;
                foreach (Vessel v in Vessels.ToList()) CheckSoi(v);
                foreach (Vessel v in Vessels.ToList()) UpdateSituation(v);
                if (Time < end) EnforceWarpLimits();
                if (!Warp.IsRails) break;
            }
            VesselPhysics.FillTelemetry(ActiveVessel, Environment(ActiveVessel), Time, Vector3d.Zero, 0, 0, ActiveVessel.Pinned, ActiveVessel.Telemetry.Splashed);
        }

        // ----- Time warp ---------------------------------------------------------------------------------

        public static double WarpAltitudeLimit(CelestialBody body, int railsIndex)
        {
            if (railsIndex <= 0) return 0;
            double[] limits = body.WarpAltitudeLimits;
            if (limits == null || limits.Length == 0) return body.AtmosphereHeight;
            return limits[System.Math.Min(railsIndex - 1, limits.Length - 1)];
        }

        /// <summary>Whether rails warp at the given index is currently allowed. Reason is set when not.</summary>
        public bool CanRailsWarp(int index, out string reason)
        {
            reason = null;
            if (index <= 0) return true;
            foreach (Vessel v in Vessels.Where(v => v.Loaded))
            {
                if (v.Pinned) continue;
                if (v.Telemetry.Thrust > 1e-3) { reason = "Cannot warp while engines are firing."; return false; }
                if (v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereHeight && v == ActiveVessel) { reason = "Cannot warp in the atmosphere."; return false; }
                if (v.Telemetry.InContact) { reason = "Cannot warp while moving on the surface."; return false; }
            }
            Vessel a = ActiveVessel;
            if (!a.Pinned)
            {
                double limit = WarpAltitudeLimit(a.Body, index);
                if (a.Altitude < limit) { reason = $"Too close to {a.Body.Name} for {TimeWarp.RailsRates[index]:0}× (needs {limit / 1000:0} km)."; return false; }
            }
            return true;
        }

        public void IncreaseWarp()
        {
            if (Warp.PhysicsMode || (Warp.IsRealTime && !CanRailsWarp(1, out _)))
            {
                // Physics ladder when rails warp is not possible.
                if (Warp.IsRails) return;
                int next = Warp.PhysicsMode ? Warp.Index + 1 : 1;
                if (next < TimeWarp.PhysicsRates.Length)
                {
                    Warp.SetPhysics(next);
                    Message($"Physics warp {Warp.Rate:0}×");
                }
                return;
            }
            int target = Warp.Index + 1;
            if (target >= TimeWarp.RailsRates.Length) return;
            if (!CanRailsWarp(target, out string reason))
            {
                Message(reason);
                return;
            }
            if (Warp.IsRealTime) GoOnRailsAll();
            Warp.SetRails(target);
        }

        public void DecreaseWarp()
        {
            if (Warp.IsRealTime) return;
            if (Warp.PhysicsMode) Warp.SetPhysics(Warp.Index - 1);
            else
            {
                Warp.SetRails(Warp.Index - 1);
                if (Warp.IsRealTime) GoOffRailsAll();
            }
        }

        public void StopWarp()
        {
            bool wasRails = Warp.IsRails;
            Warp.Stop();
            if (wasRails) GoOffRailsAll();
        }

        /// <summary>Warps on rails until <paramref name="targetTime"/> (used for "warp to manoeuvre").</summary>
        public double? WarpTarget { get; private set; }

        public void WarpTo(double targetTime)
        {
            if (targetTime <= Time) return;
            WarpTarget = targetTime;
        }

        public void CancelWarpTo() => WarpTarget = null;

        private void EnforceWarpLimits()
        {
            if (WarpTarget.HasValue)
            {
                double remaining = WarpTarget.Value - Time;
                if (remaining <= 0.5)
                {
                    WarpTarget = null;
                    StopWarp();
                }
                else
                {
                    // Choose the largest rate that will not overshoot in ~one second of real time.
                    int best = 0;
                    for (int i = TimeWarp.RailsRates.Length - 1; i >= 1; i--)
                    {
                        if (TimeWarp.RailsRates[i] * 1.0 <= remaining && CanRailsWarp(i, out _)) { best = i; break; }
                    }
                    if (best == 0)
                    {
                        if (Warp.IsRails) StopWarp();
                    }
                    else
                    {
                        if (!Warp.IsRails) GoOnRailsAll();
                        Warp.SetRails(best);
                    }
                }
            }
            if (!Warp.IsRails) return;
            if (!CanRailsWarp(Warp.Index, out _))
            {
                int allowed = Warp.Index;
                while (allowed > 0 && !CanRailsWarp(allowed, out _)) allowed--;
                Warp.SetRails(allowed);
                if (allowed == 0)
                {
                    GoOffRailsAll();
                    Message("Time warp stopped.");
                }
            }
        }

        private void GoOnRailsAll()
        {
            foreach (Vessel v in Vessels) GoOnRails(v);
        }

        private void GoOffRailsAll()
        {
            foreach (Vessel v in Vessels.Where(v => v.Loaded)) GoOffRails(v);
        }

        public void GoOnRails(Vessel v)
        {
            if (v.Pinned) { v.OnRails = false; return; }
            v.RailsOrbit = new Orbit(v.Body.Mu, v.Position, v.Velocity, Time);
            v.OnRails = true;
            // Stop rotation drift on rails except a slow spin.
            v.AngularVelocity = v.AngularVelocity.Length < 0.01 ? Vector3d.Zero : v.AngularVelocity;
        }

        public void GoOffRails(Vessel v)
        {
            if (!v.OnRails) return;
            if (v.RailsOrbit != null)
            {
                v.RailsOrbit.GetStateAt(Time, out Vector3d p, out Vector3d vel);
                v.Position = p;
                v.Velocity = vel;
            }
            v.OnRails = false;
            v.RailsOrbit = null;
        }

        // ----- Loading -------------------------------------------------------------------------------------

        private void UpdateLoading()
        {
            Vessel a = ActiveVessel;
            if (a == null) return;
            a.Loaded = true;
            foreach (Vessel v in Vessels.ToList())
            {
                if (v == a) continue;
                bool near = v.Body == a.Body && (v.Position - a.Position).Length < LoadDistance;
                if (near && !v.Loaded)
                {
                    v.Loaded = true;
                    if (!Warp.IsRails) GoOffRails(v);
                }
                else if (!near && v.Loaded)
                {
                    v.Loaded = false;
                    if (v.Pinned) continue;
                    if (v.Body.HasAtmosphere && v.Altitude < v.Body.AtmosphereHeight)
                    {
                        // Unloaded vessels are not simulated inside an atmosphere (see docs/LIMITATIONS.md).
                        Message($"{v.Name} left physics range in the atmosphere and was lost.");
                        v.Situation = Situation.Destroyed;
                        RemoveVessel(v);
                        continue;
                    }
                    GoOnRails(v);
                }
                else if (!v.Loaded && !v.OnRails && !v.Pinned)
                {
                    GoOnRails(v);
                }
            }
        }

        // ----- SOI ---------------------------------------------------------------------------------------

        private void CheckSoi(Vessel v)
        {
            if (v.Pinned) return;
            CelestialBody body = v.Body;
            CelestialBody next = null;
            if (body.Parent != null && v.Position.Length > body.SphereOfInfluence) next = body.Parent;
            else
            {
                foreach (CelestialBody child in body.Children)
                {
                    if ((v.Position - child.PositionRelativeToParent(Time)).Length < child.SphereOfInfluence)
                    {
                        next = child;
                        break;
                    }
                }
            }
            if (next == null) return;
            PatchedConics.Transform(body, next, Time, v.Position, v.Velocity, out Vector3d p, out Vector3d vel);
            v.Position = p;
            v.Velocity = vel;
            v.Body = next;
            if (v.OnRails) v.RailsOrbit = new Orbit(next.Mu, p, vel, Time);
            if (v == ActiveVessel) Message($"Entered the sphere of influence of {next.Name}.", true);
        }

        // ----- Situation ---------------------------------------------------------------------------------

        public void UpdateSituation(Vessel v)
        {
            if (v.Situation == Situation.Destroyed) return;
            CelestialBody b = v.Body;
            bool contact = v.Pinned || v.Telemetry.InContact;
            if (contact)
            {
                if (v.Situation == Situation.Prelaunch) return;
                v.Situation = v.Telemetry.Splashed ? Situation.Splashed : Situation.Landed;
                return;
            }
            if (v.Situation == Situation.Prelaunch) v.Situation = Situation.Flying;
            double alt = v.Altitude;
            if (b.HasAtmosphere && alt < b.AtmosphereHeight)
            {
                v.Situation = Situation.Flying;
                return;
            }
            Orbit o = v.CurrentOrbit(Time);
            if (o == null) return;
            double floor = b.Radius + (b.HasAtmosphere ? b.AtmosphereHeight : 0);
            if (!o.IsElliptic || (b.Parent != null && o.ApoapsisRadius > b.SphereOfInfluence)) v.Situation = Situation.Escaping;
            else if (o.PeriapsisRadius < floor) v.Situation = Situation.SubOrbital;
            else v.Situation = Situation.Orbiting;
        }

        // ----- Staging -------------------------------------------------------------------------------------

        /// <summary>Activates the next stage of a vessel: ignites engines, fires decouplers, arms parachutes.</summary>
        public void ActivateNextStage(Vessel v)
        {
            int stageCount = v.Parts.Where(p => p.Stage >= 0).Select(p => p.Stage + 1).DefaultIfEmpty(0).Max();
            if (v.CurrentStage >= stageCount)
            {
                Message("No more stages.");
                return;
            }
            int s = v.CurrentStage;
            v.CurrentStage++;
            var stageParts = v.Parts.Where(p => p.Stage == s).ToList();
            var decouplers = new List<PartInstance>();
            foreach (PartInstance p in stageParts)
            {
                if (p.Def.HasModule<EngineModuleDef>())
                {
                    p.State.Activated = true;
                    p.State.Shutdown = false;
                }
                if (p.Def.HasModule<DecouplerModuleDef>() && !p.State.Activated)
                {
                    p.State.Activated = true;
                    decouplers.Add(p);
                }
                if (p.Def.HasModule<ParachuteModuleDef>() && p.State.Parachute == ParachuteState.Stowed)
                    p.State.Parachute = ParachuteState.Armed;
                if (p.Def.HasModule<JettisonModuleDef>() && !p.State.Jettisoned)
                {
                    p.State.Jettisoned = true;
                    v.UpdateMassProperties();
                    VesselChanged?.Invoke(v);
                }
            }
            if (v.Pinned && stageParts.Any(p => p.Def.HasModule<EngineModuleDef>() || p.Def.HasModule<DecouplerModuleDef>()))
            {
                v.Pinned = false;
            }
            if (v.Situation == Situation.Prelaunch) v.LaunchTime = Time;
            foreach (PartInstance d in decouplers)
            {
                if (d.Parent < 0 || v.GetPart(d.Id) == null) continue;
                PartInstance parent = v.GetPart(d.Parent);
                double impulse = d.Def.GetModule<DecouplerModuleDef>().EjectionImpulse;
                Vector3d axis = v.Rotation.Rotate((d.Position - (parent?.Position ?? d.Position)).Normalized());
                if (axis.LengthSquared < 0.5) axis = v.Rotation.Rotate(-Vector3d.UnitY);
                SplitOff(v, d, impulse, axis);
            }
            Message($"Stage {s + 1} activated.");
        }

        /// <summary>Detaches the subtree rooted at <paramref name="subRoot"/> into a new vessel.</summary>
        public Vessel SplitOff(Vessel v, PartInstance subRoot, double impulse, Vector3d axisWorld)
        {
            var ids = new HashSet<int>();
            var stack = new Stack<PartInstance>();
            stack.Push(subRoot);
            while (stack.Count > 0)
            {
                PartInstance p = stack.Pop();
                ids.Add(p.Id);
                foreach (PartInstance c in v.ChildrenOf(p.Id)) stack.Push(c);
            }
            Vector3d oldCom = v.CenterOfMassLocal;
            Vector3d oldPos = v.Position, oldVel = v.Velocity, omega = v.AngularVelocity;
            bool wasPinned = v.Pinned;

            var moved = v.Parts.Where(p => ids.Contains(p.Id)).ToList();
            v.Parts.RemoveAll(p => ids.Contains(p.Id));
            subRoot.Parent = -1;
            subRoot.ParentNode = null;
            subRoot.SelfNode = null;
            foreach (PartInstance p in v.Parts)
                if (ids.Contains(p.FuelLineTarget)) p.FuelLineTarget = -1;
            foreach (PartInstance p in moved)
                if (!ids.Contains(p.FuelLineTarget)) p.FuelLineTarget = -1;

            var nv = new Vessel
            {
                Body = v.Body,
                Rotation = v.Rotation,
                AngularVelocity = omega,
                CurrentStage = v.CurrentStage,
                Situation = v.Situation == Situation.Prelaunch ? Situation.Flying : v.Situation,
                LaunchTime = v.LaunchTime,
            };
            nv.Parts.Add(subRoot);
            nv.Parts.AddRange(moved.Where(p => p != subRoot));
            nv.RebuildTopology();
            nv.Type = ClassifyType(nv);
            nv.Name = nv.Type == VesselType.Debris ? $"{v.Name} Debris" : $"{v.Name} ({subRoot.Def.Name})";

            if (v.Parts.Count > 0)
            {
                v.RebuildTopology();
                Vector3d offA = v.Rotation.Rotate(v.CenterOfMassLocal - oldCom);
                v.Position = oldPos + offA;
                v.Velocity = oldVel + Vector3d.Cross(omega, offA);
            }
            Vector3d offB = nv.Rotation.Rotate(nv.CenterOfMassLocal - oldCom);
            nv.Position = oldPos + offB;
            nv.Velocity = oldVel + Vector3d.Cross(omega, offB);

            if (impulse > 0 && v.Parts.Count > 0)
            {
                Vector3d axis = axisWorld.Normalized();
                nv.Velocity += axis * (impulse / nv.Mass);
                v.Velocity -= axis * (impulse / v.Mass);
            }
            if (wasPinned)
            {
                v.Pinned = false;
                nv.Pinned = false;
            }
            nv.Telemetry.AltitudeTerrain = v.Telemetry.AltitudeTerrain;
            AddVessel(nv);
            if (v.Parts.Count > 0) VesselChanged?.Invoke(v);
            return nv;
        }

        // ----- Damage --------------------------------------------------------------------------------------

        private void ResolveEvents(Vessel v, PhysicsEvents events)
        {
            foreach (int partId in events.BrokenJoints.ToList())
            {
                PartInstance p = v.GetPart(partId);
                if (p == null || p.Parent < 0) continue;
                SplitOff(v, p, 0, Vector3d.Zero);
            }
            foreach (var (partId, reason) in events.DestroyedParts.ToList())
            {
                Vessel owner = Vessels.FirstOrDefault(x => x.GetPart(partId) != null);
                if (owner == null) continue;
                Message(reason, true);
                DestroyPart(owner, owner.GetPart(partId));
            }
        }

        public void DestroyPart(Vessel v, PartInstance part)
        {
            var newVessels = new List<Vessel>();
            foreach (PartInstance child in v.ChildrenOf(part.Id).ToList())
                newVessels.Add(SplitOff(v, child, 0, Vector3d.Zero));
            Vector3d oldCom = v.CenterOfMassLocal;
            v.Parts.Remove(part);
            if (v.Parts.Count == 0)
            {
                v.Situation = Situation.Destroyed;
                RemoveVessel(v);
                if (v == ActiveVessel)
                {
                    Vessel next = newVessels.Where(x => x.Type != VesselType.Debris).OrderByDescending(x => x.Mass).FirstOrDefault()
                                  ?? newVessels.OrderByDescending(x => x.Mass).FirstOrDefault();
                    if (next != null) SetActive(next);
                    else
                    {
                        Message($"{v.Name} was destroyed.", true);
                        ActiveVesselLost = true;
                    }
                }
                return;
            }
            v.RebuildTopology();
            Vector3d off = v.Rotation.Rotate(v.CenterOfMassLocal - oldCom);
            v.Position += off;
            v.Velocity += Vector3d.Cross(v.AngularVelocity, off);
            v.Type = ClassifyType(v);
            VesselChanged?.Invoke(v);
        }

        /// <summary>Set when the active vessel has been completely destroyed.</summary>
        public bool ActiveVesselLost { get; private set; }

        // ----- Docking -------------------------------------------------------------------------------------

        private void CheckDocking()
        {
            Vessel a = ActiveVessel;
            if (a == null || !a.Loaded) return;
            foreach (Vessel b in Vessels.ToList())
            {
                if (b == a || !b.Loaded || b.Body != a.Body) continue;
                if ((b.Position - a.Position).Length > 200) continue;
                foreach (PartInstance pa in a.PartsWith<DockingPortModuleDef>().ToList())
                {
                    if (!PortFree(a, pa)) continue;
                    foreach (PartInstance pb in b.PartsWith<DockingPortModuleDef>().ToList())
                    {
                        if (!PortFree(b, pb)) continue;
                        if (TryDock(a, pa, b, pb)) return;
                    }
                }
            }
        }

        private static bool PortFree(Vessel v, PartInstance port)
        {
            DockingPortModuleDef d = port.Def.GetModule<DockingPortModuleDef>();
            if (port.State.Activated) return false; // recently undocked: wait until separated
            // The docking face must not have a part attached.
            if (port.SelfNode == d.Node && port.Parent >= 0) return false;
            return !v.ChildrenOf(port.Id).Any(c => c.ParentNode == d.Node);
        }

        private bool TryDock(Vessel a, PartInstance pa, Vessel b, PartInstance pb)
        {
            DockingPortModuleDef da = pa.Def.GetModule<DockingPortModuleDef>();
            DockingPortModuleDef db = pb.Def.GetModule<DockingPortModuleDef>();
            if (da.Size != db.Size) return false;
            AttachNodeDef na = pa.Def.GetNode(da.Node), nb = pb.Def.GetNode(db.Node);
            if (na == null || nb == null) return false;
            Vector3d posA = a.VesselToWorld(pa.LocalToVessel(na.Position));
            Vector3d posB = b.VesselToWorld(pb.LocalToVessel(nb.Position));
            Vector3d dirA = a.Rotation.Rotate(pa.DirectionToVessel(na.Direction)).Normalized();
            Vector3d dirB = b.Rotation.Rotate(pb.DirectionToVessel(nb.Direction)).Normalized();
            double dist = (posA - posB).Length;
            double angle = Vector3d.Angle(dirA, -dirB) * MathUtil.Rad2Deg;
            if (dist > System.Math.Min(da.CaptureRange, db.CaptureRange) || angle > System.Math.Min(da.CaptureAngle, db.CaptureAngle)) return false;
            Vector3d relVel = a.PointVelocity(posA) - b.PointVelocity(posB);
            if (relVel.Length > 1.5) return false;
            Dock(a, pa, b, pb);
            return true;
        }

        /// <summary>Merges vessel <paramref name="b"/> into <paramref name="a"/>, joining the two ports. Conserves linear momentum.</summary>
        public void Dock(Vessel a, PartInstance pa, Vessel b, PartInstance pb)
        {
            DockingPortModuleDef da = pa.Def.GetModule<DockingPortModuleDef>();
            DockingPortModuleDef db = pb.Def.GetModule<DockingPortModuleDef>();
            double ma = a.Mass, mb = b.Mass;
            Vector3d momentum = a.Velocity * ma + b.Velocity * mb;
            Vector3d angular = (a.AngularVelocity * ma + b.AngularVelocity * mb) / (ma + mb);

            // Re-root b at its port so it can hang off a's port.
            var tmp = new VesselDesign { Parts = b.Parts };
            tmp.Reroot(pb.Id);

            // Snap b's port exactly onto a's port: build b's parts in a's vessel frame.
            AttachNodeDef na = pa.Def.GetNode(da.Node), nb = pb.Def.GetNode(db.Node);
            Vector3d dirAv = pa.DirectionToVessel(na.Direction).Normalized();
            QuaternionD relRot = a.Rotation.Inverse() * b.Rotation; // b vessel frame -> a vessel frame
            Vector3d dirBv = relRot.Rotate(pb.DirectionToVessel(nb.Direction)).Normalized();
            QuaternionD correction = QuaternionD.FromToRotation(dirBv, -dirAv);
            QuaternionD toA = (correction * relRot).Normalized();
            Vector3d portA = pa.LocalToVessel(na.Position);
            Vector3d portB = toA.Rotate(pb.LocalToVessel(nb.Position));
            Vector3d offset = portA - portB;

            foreach (PartInstance p in b.Parts)
            {
                p.Position = toA.Rotate(p.Position) + offset;
                p.Rotation = (toA * p.Rotation).Normalized();
                // Stage indices of the docked vessel are appended after a's remaining stages.
            }
            pb.Parent = pa.Id;
            pb.ParentNode = da.Node;
            pb.SelfNode = db.Node;
            int idOffset = 0;
            var aIds = new HashSet<int>(a.Parts.Select(p => p.Id));
            if (b.Parts.Any(p => aIds.Contains(p.Id)))
            {
                // Ids are only unique per launch; renumber the docked vessel's parts.
                idOffset = a.Parts.Max(p => p.Id) + 1000;
                var map = b.Parts.ToDictionary(p => p.Id, p => p.Id + idOffset);
                foreach (PartInstance p in b.Parts)
                {
                    p.Id = map[p.Id];
                    if (p != pb && map.ContainsKey(p.Parent)) p.Parent = map[p.Parent];
                    if (map.ContainsKey(p.FuelLineTarget)) p.FuelLineTarget = map[p.FuelLineTarget];
                }
            }
            foreach (PartInstance p in b.Parts) p.Stage = -1; // docked vessel's stages are cleared to avoid accidents
            Vector3d oldComWorld = a.Position;
            Vector3d oldComLocal = a.CenterOfMassLocal;
            a.Parts.AddRange(b.Parts);
            b.Parts.Clear();
            RemoveVessel(b);
            a.RebuildTopology();
            Vector3d off = a.Rotation.Rotate(a.CenterOfMassLocal - oldComLocal);
            a.Position = oldComWorld + off * 1.0;
            a.Velocity = momentum / (ma + mb);
            a.AngularVelocity = angular;
            a.Type = ClassifyType(a);
            Message($"Docked with {b.Name}.", true);
            VesselChanged?.Invoke(a);
        }

        /// <summary>Undocks the vessel at a docking port: the port's subtree (on the far side) becomes a separate vessel.</summary>
        public Vessel Undock(Vessel v, PartInstance port)
        {
            DockingPortModuleDef d = port.Def.GetModule<DockingPortModuleDef>();
            if (d == null) return null;
            // Find the docked partner: a child attached at the port's node, or the parent if attached via it.
            PartInstance child = v.ChildrenOf(port.Id).FirstOrDefault(c => c.ParentNode == d.Node && c.Def.HasModule<DockingPortModuleDef>());
            PartInstance split = child ?? (port.SelfNode == d.Node && port.Parent >= 0 && v.GetPart(port.Parent).Def.HasModule<DockingPortModuleDef>() ? port : null);
            if (split == null) return null;
            Vector3d axis = v.Rotation.Rotate(port.DirectionToVessel(port.Def.GetNode(d.Node).Direction));
            if (split == port) axis = -axis;
            Vessel nv = SplitOff(v, split, d.UndockImpulse, axis);
            port.State.Activated = true;
            split.State.Activated = true;
            UndockedPorts.Add((port, Time));
            UndockedPorts.Add((split, Time));
            Message($"Undocked {nv.Name}.", true);
            return nv;
        }

        private readonly List<(PartInstance port, double time)> UndockedPorts = new();

        /// <summary>Re-arms docking ports a few seconds after undocking.</summary>
        public void RearmPorts()
        {
            for (int i = UndockedPorts.Count - 1; i >= 0; i--)
            {
                if (Time - UndockedPorts[i].time > 5)
                {
                    UndockedPorts[i].port.State.Activated = false;
                    UndockedPorts.RemoveAt(i);
                }
            }
        }

        // ----- Queries -------------------------------------------------------------------------------------

        public List<OrbitPatch> PredictTrajectory(Vessel v, int maxPatches = 5)
        {
            if (v.Pinned) return new List<OrbitPatch>();
            return PatchedConics.Predict(v.Body, v.Position, v.Velocity, Time, v.Nodes, maxPatches);
        }

        /// <summary>Burn time estimate (s) for a Δv using the current active engines' thrust: t = m0(1 − e^(−Δv/vₑ))/ṁ.</summary>
        public static double EstimateBurnTime(Vessel v, double deltaV)
        {
            double thrust = 0, mdot = 0;
            foreach (PartInstance p in v.Parts)
            {
                EngineModuleDef e = p.Def.GetModule<EngineModuleDef>();
                if (e == null || !p.State.Activated || p.State.Shutdown || !Propulsion.HasPropellant(v.Network, p, e)) continue;
                thrust += e.ThrustAt(0) * p.ThrustLimit;
                mdot += e.MaxMassFlow * p.ThrustLimit;
            }
            if (thrust <= 0 || mdot <= 0) return double.NaN;
            double ve = thrust / mdot;
            return v.Mass * (1 - System.Math.Exp(-deltaV / ve)) / mdot;
        }
    }
}
