using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Util;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight;

public enum CraftMode
{
    /// <summary>Fully simulated rigid body (forces, contacts, aero).</summary>
    Physics = 0,
    /// <summary>Two-body Kepler propagation; used for coasting in vacuum and for time warp.</summary>
    Rails = 1,
    /// <summary>Resting on the surface, locked to the rotating body.</summary>
    Landed = 2,
}

/// <summary>
/// A flying vehicle: a rigid assembly of <see cref="CraftPart"/>s plus translational/rotational state.
/// State frame: body-centred inertial (non-rotating) frame of the current sphere-of-influence body.
/// Craft frame: +Y nose, +Z heading, +X right; origin = vehicle design origin (root part).
/// </summary>
public sealed class Craft
{
    public int Id { get; set; }
    public string Name { get; set; } = "Vehicle";
    public List<CraftPart> Parts { get; } = new();
    private readonly Dictionary<int, CraftPart> _byId = new();
    private Dictionary<int, List<CraftPart>> _children = new();
    public PartCatalog Catalog { get; }

    // ---- kinematic state ----
    public CelestialBody Body { get; set; } = null!;
    public Vec3d Position { get; set; }
    public Vec3d Velocity { get; set; }
    public QuatD Orientation { get; set; } = QuatD.Identity;
    /// <summary>Angular velocity in the craft frame (rad/s).</summary>
    public Vec3d AngularVelocity { get; set; }
    public CraftMode Mode { get; set; } = CraftMode.Physics;
    /// <summary>Body-fixed pose used while <see cref="CraftMode.Landed"/>.</summary>
    public Vec3d LandedLocalPosition { get; set; }
    public QuatD LandedLocalOrientation { get; set; } = QuatD.Identity;

    public ControlState Control { get; } = new();
    public int CurrentStage { get; set; }
    public bool IsDestroyed { get; set; }
    public bool HasLaunched { get; set; }
    public double MissionStartTime { get; set; }

    public MassProperties Mass { get; private set; }
    public bool MassDirty { get; set; } = true;
    private bool _massValid;

    /// <summary>Latest non-gravitational acceleration magnitude (m/s^2), for the G-meter.</summary>
    public double LastGForce { get; set; }
    /// <summary>Last per-step output used by effects/UI.</summary>
    public double LastTotalThrust { get; set; }
    public double LastDrag { get; set; }
    public double LastDynamicPressure { get; set; }
    public bool InContact { get; set; }
    internal Systems.ForceAccumulator Forces { get; } = new();
    internal double RestTimer { get; set; }
    public double LastElectricProduction { get; set; }
    /// <summary>Largest distance (m) from the CoM to any part extent; used to skip terrain tests when far from the ground.</summary>
    public double BoundingRadius { get; private set; } = 5;

    /// <summary>Sink for gameplay events (stage fired, part destroyed, …). Set by the owning simulation.</summary>
    public Action<SimEvent>? EventSink { get; set; }

    public Craft(PartCatalog catalog) { Catalog = catalog; }

    // ---------------------------------------------------------------------------------------
    //  Construction
    // ---------------------------------------------------------------------------------------
    public static Craft FromDesign(VehicleDesign design, PartCatalog catalog)
    {
        design.Recompute(catalog);
        var craft = new Craft(catalog) { Name = design.Name };
        foreach (var inst in design.Parts)
        {
            var def = catalog.Get(inst.PartId);
            craft.Parts.Add(new CraftPart(inst.Id, def, inst.ParentId, inst.Position, inst.Rotation, inst.Stage)
            {
                SymmetryGroup = inst.SymmetryGroup, Mode = inst.Mode, ParentNodeId = inst.ParentNode, ChildNodeId = inst.ChildNode,
            });
        }
        craft.CurrentStage = design.Parts.Count == 0 ? 0 : Math.Max(0, design.Parts.Max(p => p.Stage));
        craft.RebuildStructure();
        return craft;
    }

    public CraftPart? Part(int id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<CraftPart> ChildrenOf(int id) =>
        _children.TryGetValue(id, out var l) ? l : (IReadOnlyList<CraftPart>)Array.Empty<CraftPart>();

    public bool HasCommand => Parts.Any(p => p.Def.Command != null);
    public CraftPart? RootPart => Parts.FirstOrDefault(p => p.ParentId < 0);

    // ---------------------------------------------------------------------------------------
    //  Structure
    // ---------------------------------------------------------------------------------------
    private int _structureVersion;
    private readonly Dictionary<(int, string), List<CraftPart>> _supplyCache = new();

    public void RebuildStructure()
    {
        _structureVersion++;
        _byId.Clear();
        foreach (var p in Parts) _byId[p.Id] = p;
        _children = Parts.Where(p => p.ParentId >= 0).GroupBy(p => p.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        _supplyCache.Clear();
        RebuildAeroExposure();
        MassDirty = true;
    }

    private static double NodeRadius(int size) => size switch { 0 => 0.3125, 1 => 0.625, 2 => 1.25, _ => 1.875 };

    private void RebuildAeroExposure()
    {
        foreach (var p in Parts)
        {
            var sh = p.Def.Shape;
            double topArea = sh.IsBox ? sh.Size[0] * sh.Size[2] : Math.PI * sh.TopRadius * sh.TopRadius;
            double botArea = sh.IsBox ? sh.Size[0] * sh.Size[2] : Math.PI * sh.BottomRadius * sh.BottomRadius;
            if (p.Def.AeroSurface != null) { topArea = 0; botArea = 0; }
            // Radially-mounted small parts expose only their own outline; keep full areas (they are small).
            var covered = new List<string>();
            if (p.Mode == AttachMode.Stack && p.ChildNodeId != "") covered.Add(p.ChildNodeId);
            foreach (var c in ChildrenOf(p.Id)) if (c.Mode == AttachMode.Stack && c.ParentNodeId != "") covered.Add(c.ParentNodeId);
            foreach (var nodeId in covered)
            {
                var n = p.Def.Node(nodeId);
                if (n == null || !n.IsStack) continue;
                double a = Math.PI * NodeRadius(n.Size) * NodeRadius(n.Size);
                if (n.DirectionV.Y > 0.7) topArea = Math.Max(0, topArea - a);
                else if (n.DirectionV.Y < -0.7) botArea = Math.Max(0, botArea - a);
            }
            p.ExposedTopArea = topArea; p.ExposedBottomArea = botArea;
        }
    }

    // ---------------------------------------------------------------------------------------
    //  Mass properties
    // ---------------------------------------------------------------------------------------
    public void UpdateMassProperties()
    {
        double m = 0; var com = Vec3d.Zero;
        foreach (var p in Parts)
        {
            double pm = p.Mass; m += pm; com += p.Position * pm;
        }
        if (m <= 0) { Mass = new MassProperties { Mass = 1, Inertia = Mat3d.Identity, InertiaInverse = Mat3d.Identity }; _massValid = true; MassDirty = false; return; }
        com /= m;
        var inertia = Mat3d.Zero;
        foreach (var p in Parts)
        {
            double pm = p.Mass;
            var sh = p.Def.Shape;
            Mat3d local;
            if (sh.IsBox)
            {
                double sx = sh.Size[0], sy = sh.Size[1], sz = sh.Size[2];
                local = Mat3d.Diagonal(pm / 12 * (sy * sy + sz * sz), pm / 12 * (sx * sx + sz * sz), pm / 12 * (sx * sx + sy * sy));
            }
            else
            {
                double r = 0.5 * (sh.TopRadius + sh.BottomRadius), L = sh.Length;
                local = Mat3d.Diagonal(pm * (3 * r * r + L * L) / 12, 0.5 * pm * r * r, pm * (3 * r * r + L * L) / 12);
            }
            var rot = Mat3d.FromQuat(p.Rotation);
            var world = rot * local * rot.Transposed;
            inertia = inertia + world + Mat3d.PointMassInertia(pm, p.Position - com);
        }
        var newMass = new MassProperties { Mass = m, CenterOfMass = com, Inertia = inertia, InertiaInverse = inertia.Inverse() };

        if (_massValid && Mode != CraftMode.Landed)
        {
            // Keep every part fixed in space while the CoM moves: shift the state point.
            var dLocal = com - Mass.CenterOfMass;
            if (dLocal.LengthSquared > 1e-18)
            {
                var dWorld = Orientation.Rotate(dLocal);
                Position += dWorld;
                Velocity += Orientation.Rotate(Vec3d.Cross(AngularVelocity, dLocal));
            }
        }
        Mass = newMass; _massValid = true; MassDirty = false;
        double br = 1;
        foreach (var p in Parts) br = Math.Max(br, (p.Position - com).Length + Math.Max(p.Def.Shape.HalfLength, p.Def.Shape.MaxRadius) + (p.Def.Leg != null ? 1.5 : 0));
        BoundingRadius = br;
    }

    public double TotalMass { get { if (MassDirty) UpdateMassProperties(); return Mass.Mass; } }

    public double DryMass => Parts.Sum(p => p.Def.DryMass);

    public double ResourceTotal(string name) => Parts.Sum(p => p.Amount(name));
    public double ResourceCapacityTotal(string name) => Parts.Sum(p => p.Capacity(name));

    // ---------------------------------------------------------------------------------------
    //  Resource flow
    // ---------------------------------------------------------------------------------------
    /// <summary>Parts from which <paramref name="consumer"/> can pull <paramref name="resource"/>.</summary>
    public List<CraftPart> SupplySet(CraftPart consumer, string resource)
    {
        var key = (consumer.Id, resource);
        if (_supplyCache.TryGetValue(key, out var cached)) return cached;
        var result = new List<CraftPart>();
        bool solid = consumer.Def.Engine?.Solid == true;
        if (resource == PartCatalog.ElectricCharge)
        {
            foreach (var p in Parts) if (p.Capacity(resource) > 0) result.Add(p);
        }
        else if (solid)
        {
            if (consumer.Capacity(resource) > 0) result.Add(consumer);
        }
        else
        {
            var seen = new HashSet<int> { consumer.Id };
            var q = new Queue<CraftPart>();
            q.Enqueue(consumer);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur.Capacity(resource) > 0) result.Add(cur);
                var neighbours = new List<CraftPart>();
                if (cur.ParentId >= 0 && _byId.TryGetValue(cur.ParentId, out var par)) neighbours.Add(par);
                neighbours.AddRange(ChildrenOf(cur.Id));
                foreach (var n in neighbours)
                {
                    if (!seen.Add(n.Id)) continue;
                    // A flow-blocking part (decoupler) cannot be crossed in either direction.
                    if (n.Def.BlocksFlow || (cur.Def.BlocksFlow && cur != consumer)) continue;
                    q.Enqueue(n);
                }
            }
        }
        _supplyCache[key] = result;
        return result;
    }

    /// <summary>Draw up to <paramref name="amount"/> of a resource, draining all connected tanks evenly. Returns amount obtained.</summary>
    public double Draw(CraftPart consumer, string resource, double amount)
    {
        if (amount <= 0) return 0;
        var supply = SupplySet(consumer, resource);
        double remaining = amount;
        for (int iter = 0; iter < 4 && remaining > 1e-12; iter++)
        {
            int n = 0;
            foreach (var s in supply) if (s.Amount(resource) > 1e-12) n++;
            if (n == 0) break;
            double share = remaining / n;
            foreach (var s in supply)
            {
                double have = s.Amount(resource);
                if (have <= 1e-12) continue;
                double take = Math.Min(share, have);
                s.Resources[resource] = have - take;
                remaining -= take;
            }
        }
        double got = amount - Math.Max(0, remaining);
        if (got > 0 && resource != PartCatalog.ElectricCharge) MassDirty = true;
        return got;
    }

    public double AvailableFor(CraftPart consumer, string resource) => SupplySet(consumer, resource).Sum(p => p.Amount(resource));

    /// <summary>Add resource (e.g. electric charge) to parts with free capacity. Returns amount stored.</summary>
    public double Store(string resource, double amount)
    {
        double left = amount;
        foreach (var p in Parts)
        {
            double cap = p.Capacity(resource);
            if (cap <= 0) continue;
            double room = cap - p.Amount(resource);
            if (room <= 0) continue;
            double add = Math.Min(room, left);
            p.Resources[resource] = p.Amount(resource) + add;
            left -= add;
            if (left <= 1e-12) break;
        }
        return amount - left;
    }

    public double DrawGlobal(string resource, double amount)
    {
        double left = amount;
        foreach (var p in Parts)
        {
            double have = p.Amount(resource);
            if (have <= 0) continue;
            double take = Math.Min(have, left);
            p.Resources[resource] = have - take;
            left -= take;
            if (left <= 1e-12) break;
        }
        return amount - left;
    }

    // ---------------------------------------------------------------------------------------
    //  Surface lock
    // ---------------------------------------------------------------------------------------
    public void LockToSurface(double time)
    {
        LandedLocalPosition = Body.InertialToLocal(Position, time);
        LandedLocalOrientation = Body.RotationAt(time).Conjugate * Orientation;
        AngularVelocity = Vec3d.Zero;
        Mode = CraftMode.Landed;
    }

    public void SyncFromSurface(double time)
    {
        var rot = Body.RotationAt(time);
        Position = rot.Rotate(LandedLocalPosition);
        Orientation = (rot * LandedLocalOrientation).Normalized;
        Velocity = Body.SurfaceVelocityAt(Position);
        AngularVelocity = Vec3d.Zero;
    }

    // ---------------------------------------------------------------------------------------
    //  Splitting
    // ---------------------------------------------------------------------------------------
    /// <summary>
    /// Cut the connection between <paramref name="partId"/> and its parent. The subtree below leaves and becomes a
    /// new craft that inherits the rigid-body velocity of its own centre of mass. The caller assigns its Id.
    /// </summary>
    public Craft Detach(int partId)
    {
        var root = _byId[partId];
        if (root.ParentId < 0) throw new InvalidOperationException("Cannot detach the root part");
        if (MassDirty) UpdateMassProperties();

        var ids = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(partId);
        while (stack.Count > 0)
        {
            int cur = stack.Pop(); ids.Add(cur);
            foreach (var c in ChildrenOf(cur)) stack.Push(c.Id);
        }
        var moved = Parts.Where(p => ids.Contains(p.Id)).ToList();
        var nc = new Craft(Catalog) { Name = Name + " Debris", Body = Body, Mode = Mode == CraftMode.Landed ? CraftMode.Physics : Mode };
        var oldCom = Mass.CenterOfMass;
        foreach (var p in moved) { Parts.Remove(p); nc.Parts.Add(p); }
        root.ParentId = -1; root.Mode = AttachMode.Root;
        nc.RebuildStructure();
        RebuildStructure();
        nc.UpdateMassPropertiesFresh();

        // Rigid-body kinematics of the new CoM.
        var dLocal = nc.Mass.CenterOfMass - oldCom;
        nc.Orientation = Orientation;
        nc.AngularVelocity = AngularVelocity;
        nc.Position = Position + Orientation.Rotate(dLocal);
        nc.Velocity = Velocity + Orientation.Rotate(Vec3d.Cross(AngularVelocity, dLocal));
        nc.CurrentStage = 0;
        nc.EventSink = EventSink;

        // Original craft keeps its own state; its CoM shifts on the next mass update.
        UpdateMassProperties();
        return nc;
    }

    /// <summary>Recompute mass properties from scratch without shifting the state point (use after assembling a craft by hand).</summary>
    public void UpdateMassPropertiesFresh()
    {
        _massValid = false;
        UpdateMassProperties();
    }

    /// <summary>Remove a single part without affecting others (children become debris separately). Returns detached sub-crafts.</summary>
    public List<Craft> DestroyPart(CraftPart part, Func<int> idAllocator)
    {
        var result = new List<Craft>();
        if (!_byId.ContainsKey(part.Id)) return result;
        foreach (var child in ChildrenOf(part.Id).ToList())
        {
            // Children of a destroyed part drift away as separate debris.
            var sub = DetachFromDestroyed(child);
            sub.Id = idAllocator();
            result.Add(sub);
        }
        Parts.Remove(part);
        RebuildStructure();
        UpdateMassProperties();
        return result;
    }

    private Craft DetachFromDestroyed(CraftPart child)
    {
        // Reuse Detach (it cuts at the child's own parent link).
        return Detach(child.Id);
    }

    public void Emit(SimEventKind kind, string message, double time, CraftPart? part = null) =>
        EventSink?.Invoke(new SimEvent { Time = time, Kind = kind, Message = message, CraftId = Id, PartId = part?.Id ?? -1, LocalPosition = part?.Position });
}
