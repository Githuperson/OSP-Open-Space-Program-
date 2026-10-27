using System.Text.Json.Serialization;
using OSP.Core.Mathx;

namespace OSP.Core.Parts;

/// <summary>Attachment node. Positions/directions are in the part-local frame (+Y = nose/top).</summary>
public sealed class AttachNodeDef
{
    public string Id { get; set; } = "";
    /// <summary>"stack" (end-to-end), "surface" (this part mounts on the side of another) or
    /// "socket" (a fixed receptacle that accepts a surface-mounted child, e.g. a radial decoupler's outer face).</summary>
    public string Kind { get; set; } = "stack";
    public double[] Position { get; set; } = { 0, 0, 0 };
    /// <summary>Outward unit direction of the node (points away from the part body toward the neighbour).</summary>
    public double[] Direction { get; set; } = { 0, 1, 0 };
    /// <summary>Diameter class: 1 = 1.25 m, 2 = 2.5 m, 3 = 3.75 m. Stack nodes only mate when sizes match.</summary>
    public int Size { get; set; } = 1;

    [JsonIgnore] public Vec3d PositionV => new(Position[0], Position[1], Position[2]);
    [JsonIgnore] public Vec3d DirectionV => new Vec3d(Direction[0], Direction[1], Direction[2]).Normalized;
    [JsonIgnore] public bool IsStack => Kind == "stack";
    [JsonIgnore] public bool IsSurface => Kind == "surface";
    [JsonIgnore] public bool IsSocket => Kind == "socket";
}

public sealed class ResourceSlotDef
{
    public string Name { get; set; } = "";
    public double Capacity { get; set; }
    public double Initial { get; set; } = -1;   // < 0 means "full"
}

/// <summary>Simplified rigid primitive used for inertia, drag area, radial mounting and collision.</summary>
public sealed class PartShape
{
    /// <summary>"cylinder" (or frustum when RadiusTop != RadiusBottom) or "box".</summary>
    public string Kind { get; set; } = "cylinder";
    public double Radius { get; set; } = 0.625;
    /// <summary>Frustum radii; &lt;= 0 means "same as Radius".</summary>
    public double RadiusTop { get; set; } = -1;
    public double RadiusBottom { get; set; } = -1;
    public double Length { get; set; } = 1.0;
    public double[] Size { get; set; } = { 1, 1, 1 };       // box extents (x, y, z)

    [JsonIgnore] public double TopRadius => RadiusTop > 0 ? RadiusTop : Radius;
    [JsonIgnore] public double BottomRadius => RadiusBottom > 0 ? RadiusBottom : Radius;
    [JsonIgnore] public bool IsBox => Kind == "box";
    [JsonIgnore] public double HalfLength => IsBox ? Size[1] * 0.5 : Length * 0.5;
    [JsonIgnore] public double FullLength => IsBox ? Size[1] : Length;

    /// <summary>Radius of the cylinder/frustum at local height y (clamped).</summary>
    public double RadiusAt(double y)
    {
        if (IsBox) return Math.Max(Size[0], Size[2]) * 0.5;
        double t = Length > 0 ? Math.Clamp((y + Length * 0.5) / Length, 0, 1) : 0.5;
        return MathUtil.Lerp(BottomRadius, TopRadius, t);
    }

    [JsonIgnore] public double MaxRadius => IsBox ? Math.Max(Size[0], Size[2]) * 0.5 : Math.Max(TopRadius, BottomRadius);

    [JsonIgnore] public double Volume => IsBox ? Size[0] * Size[1] * Size[2]
        : Math.PI * Length / 3.0 * (TopRadius * TopRadius + TopRadius * BottomRadius + BottomRadius * BottomRadius);

    /// <summary>Cross-section area seen end-on (largest of the two ends).</summary>
    [JsonIgnore] public double EndArea => IsBox ? Size[0] * Size[2] : Math.PI * MaxRadius * MaxRadius;

    /// <summary>Side (broadside) projected area.</summary>
    [JsonIgnore] public double SideArea => IsBox ? Size[1] * Math.Max(Size[0], Size[2]) : (TopRadius + BottomRadius) * Length;
}

public sealed class AeroDef
{
    /// <summary>Drag coefficient for flow hitting an exposed top (forward) face.</summary>
    public double CdTop { get; set; } = 0.35;
    /// <summary>Drag coefficient for flow hitting an exposed bottom face.</summary>
    public double CdBottom { get; set; } = 0.8;
    /// <summary>Base (wake) drag coefficient of an exposed face on the lee side.</summary>
    public double CdBase { get; set; } = 0.12;
    public double CdSide { get; set; } = 1.0;
}

public sealed class PartDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Catalogue category (Command, Fuel, Engines, Boosters, Separation, Structural, Aerodynamics, Landing, Recovery, Power, Control).</summary>
    public string Category { get; set; } = "Structural";
    public string Manufacturer { get; set; } = "Open Space Works";
    public double Cost { get; set; }
    /// <summary>Empty mass in kilograms.</summary>
    public double DryMass { get; set; }
    /// <summary>Required research node for science/career modes ("start" = always available).</summary>
    public string TechNode { get; set; } = "start";

    public PartShape Shape { get; set; } = new();
    public AeroDef Aero { get; set; } = new();
    public List<AttachNodeDef> Nodes { get; set; } = new();
    /// <summary>Whether other parts may mount radially on this part's curved side.</summary>
    public bool AcceptsRadial { get; set; }
    /// <summary>If true, propellant does not flow through this part's connections (e.g. decouplers).</summary>
    public bool BlocksFlow { get; set; }
    public List<ResourceSlotDef> Resources { get; set; } = new();

    /// <summary>Impact speed (m/s) above which this part is destroyed.</summary>
    public double CrashTolerance { get; set; } = 8.0;
    /// <summary>Maximum force (N) the joint to the parent can carry before it fails.</summary>
    public double JointStrength { get; set; } = 400_000;
    /// <summary>Maximum bending torque (N m) the joint can carry before it fails.</summary>
    public double JointTorque { get; set; } = 250_000;

    // ---- functional modules (null = part does not have this behaviour) -----------------------
    public EngineModule? Engine { get; set; }
    public DecouplerModule? Decoupler { get; set; }
    public CommandModule? Command { get; set; }
    public ReactionWheelModule? ReactionWheel { get; set; }
    public RcsModule? Rcs { get; set; }
    public SolarModule? Solar { get; set; }
    public ParachuteModule? Parachute { get; set; }
    public LegModule? Leg { get; set; }
    public AeroSurfaceModule? AeroSurface { get; set; }

    public PartVisual Visual { get; set; } = new();

    public AttachNodeDef? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);
    [JsonIgnore] public bool CanStage => Engine != null || Decoupler != null;
    [JsonIgnore] public bool HasSurfaceNode => Nodes.Any(n => n.IsSurface);
    public double ResourceCapacity(string name) => Resources.Where(r => r.Name == name).Sum(r => r.Capacity);
}
