using System.Text.Json.Serialization;
using OSP.Core.Mathx;

namespace OSP.Core.Vehicles;

public enum AttachMode
{
    /// <summary>The first part of a vehicle; defines the vehicle frame origin.</summary>
    Root = 0,
    /// <summary>Node-to-node mating (stack nodes of equal size, or a surface node onto a socket).</summary>
    Stack = 1,
    /// <summary>Surface-mounted on the curved side of a cylindrical parent at (AngleDeg, OffsetY).</summary>
    Radial = 2,
}

/// <summary>
/// One placed part inside a <see cref="VehicleDesign"/>. Only the attachment parameters are
/// authoritative; <see cref="Position"/> and <see cref="Rotation"/> are derived by <see cref="VehicleDesign.Recompute"/>.
/// </summary>
public sealed class PartInstance
{
    public int Id { get; set; }
    public string PartId { get; set; } = "";
    public int ParentId { get; set; } = -1;
    public AttachMode Mode { get; set; } = AttachMode.Root;
    /// <summary>Node on the parent used (empty for plain surface mounting).</summary>
    public string ParentNode { get; set; } = "";
    /// <summary>Node on this part that connects to the parent.</summary>
    public string ChildNode { get; set; } = "";
    /// <summary>Rotation about the attachment axis in degrees.</summary>
    public double RollDeg { get; set; }
    /// <summary>Radial mounts: angle around the parent axis (deg, 0 = parent +X, increasing toward +Z).</summary>
    public double AngleDeg { get; set; }
    /// <summary>Radial mounts: height along the parent axis (m, parent-local).</summary>
    public double OffsetY { get; set; }
    /// <summary>Stage in which this part's action fires; -1 = not staged. Higher numbers fire first.</summary>
    public int Stage { get; set; } = -1;
    /// <summary>Parts placed together by symmetry share a non-zero group id.</summary>
    public int SymmetryGroup { get; set; }

    [JsonIgnore] public Vec3d Position { get; set; }
    [JsonIgnore] public QuatD Rotation { get; set; } = QuatD.Identity;

    public PartInstance Clone() => (PartInstance)MemberwiseClone();
}
