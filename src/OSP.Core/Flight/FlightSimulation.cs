using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight;

public readonly record struct WarpLevel(string Label, double Factor, bool Rails);

/// <summary>
/// The whole flight universe: universal time, all crafts, time warp, mode switching between full physics and Kepler rails,
/// sphere-of-influence transitions, staging, and event emission. Frame-rate independent: <see cref="Advance"/> takes the
/// real elapsed time and consumes it with fixed physics steps.
/// </summary>
public sealed class FlightSimulation
{
    public static readonly WarpLevel[] WarpLevels =
    {
        new("1x", 1, false), new("2x", 2, false), new("3x", 3, false), new("4x", 4, false),
        new("5x", 5, true), new("10x", 10, true), new("50x", 50, true), new("100x", 100, true),
        new("1,000x", 1000, true), new("10,000x", 10000, true), new("100,000x", 100000, true),
    };

    public StarSystem System { get; }
    public PartCatalog Catalog { get; }
    public double Time { get; set; }
    public List<Craft> Crafts { get; } = new();
    public Craft? Active { get; private set; }
    public int WarpIndex { get; private set; }
    public bool Paused { get; set; }
    public WarpLevel Warp => WarpLevels[WarpIndex];
    public string? WarpLimitReason { get; private set; }
    public List<SimEvent> EventLog { get; } = new();
    public event Action<SimEvent>? EventRaised;
    public int MaxPhysicsStepsPerFrame { get; set; } = 10;
    public int MaxDebris { get; set; } = 24;
    public Dictionary<int, List<ManeuverNode>> Maneuvers { get; } = new();

    private int _nextCraftId = 1;
    private int _nextNodeId = 1;
    private double _accumulator;
    private readonly StepOutcome _outcome = new();

    public FlightSimulation(StarSystem system, PartCatalog catalog) { System = system; Catalog = catalog; }

    // ---------------------------------------------------------------------------------------
    //  Craft management
    // ---------------------------------------------------------------------------------------
    private int AllocateCraftId() => _nextCraftId++;

    public void AddCraft(Craft c, bool makeActive)
    {
        if (c.Id == 0) c.Id = AllocateCraftId(); else _nextCraftId = Math.Max(_nextCraftId, c.Id + 1);
        c.EventSink = Raise;
        Crafts.Add(c);
        if (makeActive || Active == null) Active = c;
    }

    public void SetActive(Craft c)
    {
        if (Crafts.Contains(c)) { Active = c; ClampWarp(); }
    }

    public Craft? FindCraft(int id) => Crafts.FirstOrDefault(c => c.Id == id);

    /// <summary>Place a new vehicle on the surface at a launch site, nose up, with +Z heading east, resting on its lowest point.</summary>
    public Craft Launch(VehicleDesign design, CelestialBody body, LaunchSiteDef site)
    {
        var craft = Craft.FromDesign(design, Catalog);
        craft.Body = body;
        craft.UpdateMassProperties();

        double lat = site.LatitudeDeg * MathUtil.Deg2Rad, lon = site.LongitudeDeg * MathUtil.Deg2Rad;
        var dir = CelestialBody.DirectionFromLatLon(lat, lon);
        // Local ENU frame in the body-fixed frame.
        var east = Vec3d.Cross(Vec3d.UnitY, dir).Normalized;
        if (east.LengthSquared < 0.5) east = Vec3d.UnitZ;
        var north = Vec3d.Cross(dir, east).Normalized;
        // Craft: +Y up, +Z east, +X = Y x Z = up x east = north.
        var qLocal = QuatD.FromBasis(north, dir, east);
        double groundR = body.Radius + body.SurfaceHeightAt(dir);

        // Lowest point along craft -Y relative to CoM
        var com = craft.Mass.CenterOfMass;
        double lowest = double.MaxValue;
        foreach (var p in craft.Parts)
        {
            var def = p.Def;
            double half = def.Shape.HalfLength;
            var axis = p.Rotation.Rotate(Vec3d.UnitY);
            double ext = Math.Abs(axis.Y) * half + Math.Sqrt(Math.Max(0, 1 - axis.Y * axis.Y)) * def.Shape.MaxRadius;
            double y = p.Position.Y - com.Y - ext;
            if (def.Leg != null) y = Math.Min(y, p.Position.Y - com.Y + def.Leg.FootPosition[1]);
            lowest = Math.Min(lowest, y);
        }
        var localPos = dir * (groundR - lowest + 0.02);
        craft.LandedLocalPosition = localPos;
        craft.LandedLocalOrientation = qLocal;
        craft.Mode = CraftMode.Landed;
        craft.SyncFromSurface(Time);
        craft.Control.SasEnabled = true;
        craft.Control.Sas = SasMode.StabilityAssist;
        craft.Control.HoldOrientation = craft.Orientation;
        craft.Control.HoldValid = true;
        craft.CurrentStage = design.Parts.Count == 0 ? 0 : Math.Max(0, design.Parts.Max(p => p.Stage));
        craft.MissionStartTime = Time;
        AddCraft(craft, true);
        return craft;
    }

    // ---------------------------------------------------------------------------------------
    //  Warp
    // ---------------------------------------------------------------------------------------
    public int MaxAllowedWarpIndex(out string? reason)
    {
        reason = null;
        var a = Active;
        if (a == null) return WarpLevels.Length - 1;
        int lastPhysics = 3;
        // Rails warp requires a coasting vessel in space.
        if (a.Mode == CraftMode.Landed) return WarpLevels.Length - 1;
        if (a.LastTotalThrust > 0 || a.Control.Throttle > 0 && a.Parts.Any(p => p.Ignited))
        { reason = "engines running"; return lastPhysics; }
        if (a.Parts.Any(p => p.Def.Engine is { Solid: true } && p.Ignited)) { reason = "solid motor burning"; return lastPhysics; }
        if (a.InContact) { reason = "in contact with the ground"; return lastPhysics; }
        var env = FlightEnvironment.Compute(System, a.Body, Time, a.Position, a.Velocity, false);
        double atmo = a.Body.Atmosphere?.Height ?? 0;
        if (env.AltitudeSeaLevel < atmo) { reason = "in atmosphere"; return lastPhysics; }
        if (a.Body.Atmosphere == null && env.AltitudeSeaLevel < 5000) { reason = "too close to the surface"; return lastPhysics; }
        if (a.Control.RcsEnabled && a.Control.Translation.LengthSquared > 0) { reason = "RCS firing"; return lastPhysics; }
        return WarpLevels.Length - 1;
    }

    public bool SetWarp(int index)
    {
        index = Math.Clamp(index, 0, WarpLevels.Length - 1);
        int max = MaxAllowedWarpIndex(out var reason);
        if (index > max)
        {
            WarpIndex = max; WarpLimitReason = reason;
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.WarpLimited, Message = $"Time warp limited: {reason}" });
            return false;
        }
        WarpIndex = index; WarpLimitReason = null;
        return true;
    }

    public void StopWarp() { WarpIndex = 0; }

    private void ClampWarp()
    {
        int max = MaxAllowedWarpIndex(out var reason);
        if (WarpIndex > max) { WarpIndex = max; WarpLimitReason = reason; }
    }

    // ---------------------------------------------------------------------------------------
    //  Main loop
    // ---------------------------------------------------------------------------------------
    public void Advance(double realDt)
    {
        if (Paused || realDt <= 0) return;
        realDt = Math.Min(realDt, 0.1);
        ClampWarp();
        var w = Warp;
        if (!w.Rails)
        {
            _accumulator += realDt * w.Factor;
            int steps = 0;
            while (_accumulator >= CraftPhysics.FixedDt && steps < MaxPhysicsStepsPerFrame)
            {
                StepAll(CraftPhysics.FixedDt);
                _accumulator -= CraftPhysics.FixedDt;
                steps++;
            }
            if (_accumulator > CraftPhysics.FixedDt) _accumulator = CraftPhysics.FixedDt;    // drop backlog rather than spiral
        }
        else
        {
            _accumulator = 0;
            AdvanceRails(realDt * w.Factor);
        }
    }

    /// <summary>Advance exactly one fixed physics step (used by tests and the headless runner).</summary>
    public void StepFixed(double dt) => StepAll(dt);

    private void StepAll(double dt)
    {
        UpdateModes(false);
        for (int i = Crafts.Count - 1; i >= 0; i--)
        {
            var c = Crafts[i];
            if (c.IsDestroyed) continue;
            if (c.Mode == CraftMode.Rails) PropagateRails(c, dt);
            else
            {
                CraftPhysics.Step(c, System, Time, dt, _outcome);
                ProcessOutcome(c, _outcome);
                if (!c.IsDestroyed) CheckSoi(c, Time + dt);
            }
        }
        Time += dt;
        CleanupDestroyed();
    }

    private void AdvanceRails(double total)
    {
        UpdateModes(true);
        double remaining = total;
        int guard = 0;
        while (remaining > 1e-9 && guard++ < 2000)
        {
            double step = remaining;
            // Hazard guard: don't coast through the atmosphere / surface.
            if (Active != null && Active.Mode == CraftMode.Rails)
            {
                double? th = TimeToHazard(Active);
                if (th.HasValue && th.Value - 10 < step)
                {
                    step = Math.Max(0, th.Value - 10);
                    if (step < 0.5)
                    {
                        WarpIndex = 0; WarpLimitReason = "approaching atmosphere / surface";
                        Raise(new SimEvent { Time = Time, Kind = SimEventKind.WarpLimited, Message = "Time warp stopped: approaching atmosphere or surface" });
                        Active.Mode = CraftMode.Physics;
                        return;
                    }
                }
            }
            step = Math.Min(step, MaxRailsSubstep());
            foreach (var c in Crafts.ToList())
            {
                if (c.IsDestroyed) continue;
                if (c.Mode == CraftMode.Landed) { c.SyncFromSurface(Time + step); continue; }
                if (c.Mode == CraftMode.Rails) { PropagateRails(c, step); }
                else { CraftPhysics.Step(c, System, Time, Math.Min(step, CraftPhysics.FixedDt), _outcome); ProcessOutcome(c, _outcome); }
                if (!c.IsDestroyed) CheckSoi(c, Time + step);
            }
            Time += step;
            remaining -= step;
        }
        CleanupDestroyed();
    }

    private double MaxRailsSubstep()
    {
        double m = 1e9;
        foreach (var c in Crafts)
        {
            if (c.Mode != CraftMode.Rails || c.IsDestroyed) continue;
            double speed = Math.Max(c.Velocity.Length, 10);
            double r = c.Position.Length;
            var body = c.Body;
            // approaching a child sphere of influence
            foreach (var ch in body.Children)
            {
                var (cr, cv) = System.StateRelativeToParent(ch, Time);
                double gap = (c.Position - cr).Length - ch.SphereOfInfluence;
                double relSpeed = Math.Max((c.Velocity - cv).Length, 10);
                m = Math.Min(m, Math.Max(2.0, 0.15 * Math.Abs(gap) / relSpeed));
            }
            if (body.Parent != null)
            {
                double gap = body.SphereOfInfluence - r;
                m = Math.Min(m, Math.Max(2.0, 0.15 * Math.Abs(gap) / speed));
            }
            double hz = HazardRadius(body);
            if (r > hz) m = Math.Min(m, Math.Max(1.0, 0.15 * (r - hz) / speed));
            if (m > 1e8 && Time >= 0) { }
            var o = Orbit.FromState(c.Position, c.Velocity, body.Mu, Time);
            if (o.IsElliptic) m = Math.Min(m, o.Period / 40.0);
        }
        return m;
    }

    private static double HazardRadius(CelestialBody b) =>
        b.Radius + (b.Atmosphere?.Height ?? (b.Terrain != null ? b.Terrain.Amplitude * 1.2 : 0)) + 200;

    /// <summary>Seconds until the craft's current conic next descends through the hazard radius, if ever.</summary>
    public double? TimeToHazard(Craft c)
    {
        var o = Orbit.FromState(c.Position, c.Velocity, c.Body.Mu, Time);
        double rh = HazardRadius(c.Body);
        if (c.Position.Length <= rh) return 0;
        var t = TrajectoryPredictor.TimeAtRadius(o, rh, Time, outbound: false);
        return t.HasValue ? t.Value - Time : null;
    }

    private void PropagateRails(Craft c, double dt)
    {
        var (r, v) = KeplerPropagator.Propagate(c.Position, c.Velocity, c.Body.Mu, dt);
        c.Position = r; c.Velocity = v;
        c.AngularVelocity = Vec3d.Zero;
        // Hold attitude relative to the orbit when a pointing SAS mode is selected.
        if (c.Control.SasEnabled && c.Control.Sas != SasMode.StabilityAssist)
        {
            var env = FlightEnvironment.Compute(System, c.Body, Time + dt, r, v, false);
            var dir = Systems.ControlSystem.SasDirection(c, env, c.Control);
            if (dir.HasValue)
                c.Orientation = (QuatD.FromTo(c.Orientation.Rotate(Vec3d.UnitY), dir.Value) * c.Orientation).Normalized;
        }
    }

    private void UpdateModes(bool warpRails)
    {
        foreach (var c in Crafts)
        {
            if (c.IsDestroyed || c.Mode == CraftMode.Landed) continue;
            var env = FlightEnvironment.Compute(System, c.Body, Time, c.Position, c.Velocity, false);
            double atmo = c.Body.Atmosphere?.Height ?? 0;
            bool thrusting = c.Parts.Any(p => p.Ignited && p.Def.Engine != null) && (c.Control.Throttle > 0 || c.Parts.Any(p => p.Ignited && p.Def.Engine!.Solid));
            bool lowOrAtmo = env.AltitudeSeaLevel < atmo + 200 || (c.Body.Atmosphere == null && env.AltitudeSeaLevel < HazardRadius(c.Body) - c.Body.Radius);
            bool needPhysics = thrusting || lowOrAtmo || c.InContact || c.Control.RcsEnabled && c.Control.Translation.LengthSquared > 0;
            if (c == Active)
            {
                if (warpRails && !needPhysics) c.Mode = CraftMode.Rails;
                else c.Mode = CraftMode.Physics;
            }
            else
            {
                bool nearActive = Active != null && Active.Body == c.Body && (Active.Position - c.Position).Length < 25000;
                bool rails = !needPhysics && (warpRails || !nearActive || env.AltitudeSeaLevel > atmo + 200);
                c.Mode = rails ? CraftMode.Rails : CraftMode.Physics;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    //  Sphere of influence
    // ---------------------------------------------------------------------------------------
    private void CheckSoi(Craft c, double t)
    {
        var b = c.Body;
        if (b.Parent != null && c.Position.Length > b.SphereOfInfluence)
        {
            var (r, v) = System.ConvertFrame(c.Position, c.Velocity, b, b.Parent, t);
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.SoiChange, CraftId = c.Id, Message = $"{c.Name}: left {b.Name} sphere of influence, entering {b.Parent.Name}" });
            c.Body = b.Parent; c.Position = r; c.Velocity = v;
            return;
        }
        foreach (var ch in b.Children)
        {
            var (cr, cv) = System.StateRelativeToParent(ch, t);
            if ((c.Position - cr).Length < ch.SphereOfInfluence)
            {
                Raise(new SimEvent { Time = Time, Kind = SimEventKind.SoiChange, CraftId = c.Id, Message = $"{c.Name}: entering {ch.Name} sphere of influence" });
                c.Body = ch; c.Position -= cr; c.Velocity -= cv;
                return;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    //  Outcomes
    // ---------------------------------------------------------------------------------------
    private void ProcessOutcome(Craft c, StepOutcome o)
    {
        if (o.Touchdown)
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.Touchdown, CraftId = c.Id, Message = $"{c.Name}: touchdown at {o.ImpactSpeed:F1} m/s" });

        foreach (var j in o.BrokenJoints.ToList())
        {
            if (c.Part(j.Id) == null || j.ParentId < 0) continue;
            var nc = c.Detach(j.Id);
            nc.Id = AllocateCraftId(); nc.EventSink = Raise; nc.Control.SasEnabled = false;
            Crafts.Add(nc);
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.JointFailed, CraftId = c.Id, PartId = j.Id, LocalPosition = j.Position, Message = $"Structural failure: {j.Def.Name} broke away ({j.LastJointForce / 1000:F0} kN, {j.LastJointTorque / 1000:F0} kN·m)" });
            AfterSplit(c, nc);
        }
        foreach (var p in o.Crashed.ToList())
        {
            if (c.Part(p.Id) == null) continue;
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.PartDestroyed, CraftId = c.Id, PartId = p.Id, LocalPosition = p.Position, Message = $"{p.Def.Name} destroyed in impact" });
            var pieces = c.DestroyPart(p, AllocateCraftId);
            foreach (var d in pieces)
            {
                d.EventSink = Raise; d.Control.SasEnabled = false; Crafts.Add(d);
                AfterSplit(c, d);
            }
            if (c.Parts.Count == 0) break;
        }
        if (c.Parts.Count == 0 && !c.IsDestroyed)
        {
            c.IsDestroyed = true;
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.CraftDestroyed, CraftId = c.Id, Message = $"{c.Name} was destroyed" });
        }
        else if (!c.IsDestroyed && c.Parts.Count > 0 && c == Active && !c.HasCommand && o.Crashed.Count > 0)
            Raise(new SimEvent { Time = Time, Kind = SimEventKind.Crashed, CraftId = c.Id, Message = $"{c.Name} lost its command module" });
    }

    private void AfterSplit(Craft original, Craft spawned)
    {
        if (original == Active && !original.HasCommand && spawned.HasCommand)
        {
            CopyControl(original, spawned);
            Active = spawned;
        }
    }

    private static void CopyControl(Craft from, Craft to)
    {
        to.Control.Throttle = from.Control.Throttle;
        to.Control.SasEnabled = from.Control.SasEnabled;
        to.Control.Sas = from.Control.Sas;
        to.Control.RcsEnabled = from.Control.RcsEnabled;
        to.Control.HoldOrientation = to.Orientation; to.Control.HoldValid = true;
        to.CurrentStage = from.CurrentStage;
    }

    private void CleanupDestroyed()
    {
        for (int i = Crafts.Count - 1; i >= 0; i--)
            if (Crafts[i].IsDestroyed || Crafts[i].Parts.Count == 0)
            {
                if (Crafts[i] == Active) Active = Crafts.FirstOrDefault(c => c != Crafts[i] && c.HasCommand);
                Crafts.RemoveAt(i);
            }
        // Limit debris: remove the oldest uncontrolled crafts that are far from the active vessel.
        var debris = Crafts.Where(c => c != Active && !c.HasCommand).ToList();
        for (int i = 0; i < debris.Count - MaxDebris; i++) Crafts.Remove(debris[i]);
    }

    // ---------------------------------------------------------------------------------------
    //  Staging and actions
    // ---------------------------------------------------------------------------------------
    public StagingController.Result? ActivateStage()
    {
        var c = Active;
        if (c == null || !c.HasCommand) return null;
        if (c.Mode == CraftMode.Landed) { c.Mode = CraftMode.Landed; }
        var res = StagingController.Activate(c, Time, AllocateCraftId);
        foreach (var nc in res.NewCrafts)
        {
            nc.EventSink = Raise; Crafts.Add(nc);
            AfterSplit(c, nc);
        }
        c.HasLaunched |= c.Parts.Any(p => p.Ignited);
        return res;
    }

    public void SetGear(bool down) { if (Active == null) return; foreach (var p in Active.Parts) if (p.Def.Leg != null) p.LegsDeployedTarget = down; }
    public void SetPanels(bool out_) { if (Active == null) return; foreach (var p in Active.Parts) if (p.Def.Solar != null) p.SolarDeployedTarget = out_; }
    public void ArmParachutes()
    {
        if (Active == null) return;
        foreach (var p in Active.Parts)
            if (p.Def.Parachute != null && p.Chute == ChuteState.Stowed) { p.Chute = ChuteState.Armed; Raise(new SimEvent { Time = Time, Kind = SimEventKind.Info, CraftId = Active.Id, Message = $"{p.Def.Name} armed" }); }
    }

    // ---------------------------------------------------------------------------------------
    //  Maneuvers
    // ---------------------------------------------------------------------------------------
    public List<ManeuverNode> NodesFor(Craft c)
    {
        if (!Maneuvers.TryGetValue(c.Id, out var l)) Maneuvers[c.Id] = l = new List<ManeuverNode>();
        return l;
    }

    public ManeuverNode AddNode(Craft c, double time, double pro = 0, double normal = 0, double radial = 0)
    {
        var n = new ManeuverNode { Id = _nextNodeId++, Time = time, Prograde = pro, Normal = normal, Radial = radial };
        NodesFor(c).Add(n);
        return n;
    }

    public void RemoveNode(Craft c, ManeuverNode n) => NodesFor(c).Remove(n);

    /// <summary>Inertial burn direction (unit) for the next node, for the Maneuver SAS mode.</summary>
    public Vec3d? NextNodeDirection(Craft c, out ManeuverNode? node)
    {
        node = NodesFor(c).Where(n => n.Time >= Time - 1).OrderBy(n => n.Time).FirstOrDefault();
        if (node == null) return null;
        var (r, v) = KeplerPropagator.Propagate(c.Position, c.Velocity, c.Body.Mu, node.Time - Time);
        var dv = node.DeltaVVector(r, v);
        return dv.Normalized;
    }

    public List<PredictedPatch> PredictFor(Craft c, int maxPatches = 6, double horizon = 3.0e7)
    {
        if (c.Mode == CraftMode.Landed) return new List<PredictedPatch>();
        return TrajectoryPredictor.Predict(System, c.Body, c.Position, c.Velocity, Time, NodesFor(c), maxPatches, horizon);
    }

    private void Raise(SimEvent e)
    {
        EventLog.Add(e);
        if (EventLog.Count > 500) EventLog.RemoveRange(0, 100);
        EventRaised?.Invoke(e);
    }
}
