namespace OSP.Core.Parts;

/// <summary>
/// Declarative description of a part's 3D model. The Godot layer turns each element into a real mesh
/// (lathe/box/cylinder/…) so new parts can be added with data only. Core never builds meshes.
/// </summary>
public sealed class PartVisual
{
    public List<VisualElement> Elements { get; set; } = new();
}

public sealed class HingeDef
{
    public double[] Pivot { get; set; } = { 0, 0, 0 };
    public double[] Axis { get; set; } = { 0, 0, 1 };
    /// <summary>Rotation (deg) at full deployment; 0 at stowed.</summary>
    public double AngleDeg { get; set; } = 90;
    /// <summary>Which animated state drives the hinge: "leg", "solar", "chute".</summary>
    public string Driver { get; set; } = "solar";
}

public sealed class VisualElement
{
    /// <summary>lathe | box | cylinder | cone | sphere | fins | nozzle | panel | strut</summary>
    public string Type { get; set; } = "cylinder";
    public string Material { get; set; } = "hull_white";
    /// <summary>Lathe profile as [y, r] pairs from bottom to top (part-local metres).</summary>
    public double[][]? Profile { get; set; }
    public int Segments { get; set; } = 28;
    public bool Smooth { get; set; } = true;
    public double[] Offset { get; set; } = { 0, 0, 0 };
    /// <summary>Euler rotation (deg, XYZ).</summary>
    public double[] Rotation { get; set; } = { 0, 0, 0 };
    public double[] Size { get; set; } = { 1, 1, 1 };
    public double Radius { get; set; } = 0.5;
    public double RadiusTop { get; set; } = -1;
    public double Length { get; set; } = 1;
    public int Count { get; set; } = 1;
    /// <summary>Circular placement radius for repeated elements (fins, struts).</summary>
    public double RingRadius { get; set; }
    public HingeDef? Hinge { get; set; }
    /// <summary>Marks the element that is scaled/shown when a parachute canopy is open, etc.</summary>
    public string? Tag { get; set; }
}
