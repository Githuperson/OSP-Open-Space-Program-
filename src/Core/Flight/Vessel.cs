using System.Collections.Generic;
using System.Linq;
using OSP.Core.Celestial;
using OSP.Core.Math;
using OSP.Core.Orbits;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight
{
    public enum Situation
    {
        Prelaunch,
        Landed,
        Splashed,
        Flying,
        SubOrbital,
        Orbiting,
        Escaping,
        Destroyed,
    }

    public enum VesselType
    {
        Ship,
        Probe,
        Debris,
    }

    /// <summary>
    /// A vessel in flight. Authoritative state is double precision, in the non-rotating frame centred on
    /// <see cref="Body"/> (the body whose sphere of influence contains the vessel).
    ///
    /// <see cref="Position"/>/<see cref="Velocity"/> refer to the centre of mass. <see cref="Rotation"/> maps the
    /// vessel frame (root part at origin, +Y = nose) to the inertial frame. Part transforms are stored in the
    /// vessel frame, so a part's inertial position is Position + Rotation·(partPos − CoM).
    /// </summary>
    public sealed class Vessel
    {
        private static int _nextId = 1;

        public int Id { get; set; }
        public string Name { get; set; }
        public VesselType Type { get; set; }
        public List<PartInstance> Parts { get; } = new();
        public CelestialBody Body { get; set; }

        public Vector3d Position { get; set; }
        public Vector3d Velocity { get; set; }
        public QuaternionD Rotation { get; set; } = QuaternionD.Identity;
        /// <summary>Angular velocity in the inertial frame (rad/s).</summary>
        public Vector3d AngularVelocity { get; set; }
        /// <summary>Angular acceleration during the last physics step (rad/s²), used for joint loads.</summary>
        public Vector3d LastAngularAcceleration { get; set; }

        public VesselControl Control { get; } = new();
        public Autopilot Autopilot { get; } = new();
        /// <summary>Simulation scratch data (contact points, joint stress counters). Rebuilt with the topology.</summary>
        internal VesselPhysicsCache PhysicsCache { get; set; }
        /// <summary>Incremented whenever the part tree changes.</summary>
        public int TopologyVersion { get; private set; }
        /// <summary>Next stage to activate.</summary>
        public int CurrentStage { get; set; }
        public Situation Situation { get; set; } = Situation.Prelaunch;
        public List<ManeuverNode> Nodes { get; } = new();
        public double LaunchTime { get; set; }

        /// <summary>When landed and at rest the vessel is pinned to the rotating surface (no integration).</summary>
        public bool Pinned { get; set; }
        public Vector3d PinnedBodyFixedPosition { get; set; }
        public QuaternionD PinnedBodyFixedRotation { get; set; }

        /// <summary>True when the vessel is propagated analytically (time warp or far from the active vessel).</summary>
        public bool OnRails { get; set; }
        public Orbit RailsOrbit { get; set; }

        /// <summary>Within physics range of the active vessel.</summary>
        public bool Loaded { get; set; } = true;

        /// <summary>Integrated thrust/RCS Δv vector (inertial), used to track manoeuvre progress.</summary>
        public Vector3d AccumulatedThrustDeltaV { get; set; }

        // ----- Cached derived data ---------------------------------------------------------------------

        public MassProperties MassProps { get; private set; }
        public ResourceNetwork Network { get; private set; }
        public double Mass => MassProps.Mass;
        /// <summary>Centre of mass in the vessel frame.</summary>
        public Vector3d CenterOfMassLocal => MassProps.CenterOfMass;
        public FlightTelemetry Telemetry { get; } = new();

        private Dictionary<int, PartInstance> _byId = new();
        private Dictionary<int, List<PartInstance>> _children = new();

        public Vessel()
        {
            Id = _nextId++;
        }

        public static void EnsureIdAbove(int id)
        {
            if (_nextId <= id) _nextId = id + 1;
        }

        public PartInstance Root => Parts.FirstOrDefault(p => p.Parent < 0 || !_byId.ContainsKey(p.Parent));
        public PartInstance GetPart(int id) => _byId.TryGetValue(id, out var p) ? p : null;
        public IReadOnlyList<PartInstance> ChildrenOf(int id) => _children.TryGetValue(id, out var l) ? l : (IReadOnlyList<PartInstance>)System.Array.Empty<PartInstance>();

        public bool HasControl => Parts.Any(p => p.Def.GetModule<CommandModuleDef>() is { } c && (c.IsProbe || (p.State?.Crew.Count ?? 0) > 0 || c.CrewCapacity > 0));

        /// <summary>Rebuilds lookup tables, resource network and mass properties after the part tree changes.</summary>
        public void RebuildTopology()
        {
            _byId = Parts.ToDictionary(p => p.Id);
            _children = new Dictionary<int, List<PartInstance>>();
            foreach (PartInstance p in Parts)
            {
                p.State ??= new PartFlightState();
                if (p.Parent >= 0 && _byId.ContainsKey(p.Parent))
                {
                    if (!_children.TryGetValue(p.Parent, out var list)) _children[p.Parent] = list = new List<PartInstance>();
                    list.Add(p);
                }
            }
            Network = new ResourceNetwork(Parts);
            MassProps = MassProperties.Compute(Parts);
            TopologyVersion++;
            PhysicsCache = null;
        }

        /// <summary>
        /// Recomputes mass properties (after fuel use) and shifts the CoM state so parts do not jump in space:
        /// x_new = x_old + R·Δc, v_new = v_old + ω × (R·Δc).
        /// </summary>
        public void UpdateMassProperties()
        {
            Vector3d oldCom = MassProps.CenterOfMass;
            MassProps = MassProperties.Compute(Parts);
            Vector3d shift = Rotation.Rotate(MassProps.CenterOfMass - oldCom);
            if (shift.LengthSquared > 0)
            {
                Position += shift;
                Velocity += Vector3d.Cross(AngularVelocity, shift);
            }
        }

        /// <summary>Inertial position (relative to Body) of a point given in the vessel frame.</summary>
        public Vector3d VesselToWorld(Vector3d vesselPoint) => Position + Rotation.Rotate(vesselPoint - CenterOfMassLocal);
        public Vector3d WorldToVessel(Vector3d world) => Rotation.InverseRotate(world - Position) + CenterOfMassLocal;
        public Vector3d PartWorldPosition(PartInstance p) => VesselToWorld(p.Position);
        public QuaternionD PartWorldRotation(PartInstance p) => (Rotation * p.Rotation).Normalized();

        /// <summary>World velocity of a point attached to the vessel.</summary>
        public Vector3d PointVelocity(Vector3d worldPoint) => Velocity + Vector3d.Cross(AngularVelocity, worldPoint - Position);

        public Vector3d Forward => Rotation.Rotate(Vector3d.UnitY);

        /// <summary>Inertia tensor in the inertial frame: R I Rᵀ.</summary>
        public Matrix3d WorldInertia()
        {
            Matrix3d r = Matrix3d.FromQuaternion(Rotation);
            return r * MassProps.Inertia * r.Transposed();
        }

        public Orbit CurrentOrbit(double time)
        {
            if (OnRails && RailsOrbit != null) return RailsOrbit;
            if (Position.LengthSquared < 1) return null;
            return new Orbit(Body.Mu, Position, Velocity, time);
        }

        public double Altitude => Body == null ? 0 : Position.Length - Body.Radius;

        /// <summary>Velocity relative to the rotating surface/atmosphere.</summary>
        public Vector3d SurfaceVelocity => Velocity - Body.SurfaceVelocity(Position);

        public IEnumerable<PartInstance> PartsWith<T>() where T : PartModuleDef => Parts.Where(p => p.Def.HasModule<T>());

        public override string ToString() => $"{Name} [{Id}] around {Body?.Name}";
    }
}
