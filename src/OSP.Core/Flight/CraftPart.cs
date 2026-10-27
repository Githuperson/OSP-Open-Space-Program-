using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight;

public enum ChuteState { Stowed = 0, Armed = 1, Semi = 2, Deployed = 3, Cut = 4 }

/// <summary>Live state of one part on a craft. Poses are in the craft frame and never change after assembly.</summary>
public sealed class CraftPart
{
    public int Id { get; }
    public PartDefinition Def { get; }
    public int ParentId { get; set; }
    public int Stage { get; set; }
    public Vec3d Position { get; }
    public QuatD Rotation { get; }
    public int SymmetryGroup { get; set; }
    /// <summary>How this part connects to its parent (Stack/Radial) and through which nodes (for drag exposure).</summary>
    public Vehicles.AttachMode Mode { get; set; }
    public string ParentNodeId { get; set; } = "";
    public string ChildNodeId { get; set; } = "";

    public Dictionary<string, double> Resources { get; } = new();

    // --- engine ---
    public bool Ignited { get; set; }
    public bool FlamedOut { get; set; }
    /// <summary>Effective thrust produced in the last step (N), for UI and effects.</summary>
    public double CurrentThrust { get; set; }
    /// <summary>Fraction (0..1) of full output for visual plume intensity.</summary>
    public double PlumeIntensity { get; set; }
    /// <summary>Current gimbal deflection as a lateral vector added to the thrust direction (craft frame).</summary>
    public Vec3d GimbalOffset { get; set; }

    // --- deployables ---
    public bool LegsDeployedTarget { get; set; }
    public double LegDeploy { get; set; }
    public bool SolarDeployedTarget { get; set; }
    public double SolarDeploy { get; set; }
    public ChuteState Chute { get; set; } = ChuteState.Stowed;
    /// <summary>0..1 inflation of the canopy while opening.</summary>
    public double ChuteProgress { get; set; }
    public double ControlSurfaceDeflection { get; set; }

    // --- derived aerodynamic exposure (set by Craft.RebuildStructure) ---
    public double ExposedTopArea { get; set; }
    public double ExposedBottomArea { get; set; }

    // --- structural stress ---
    public double OverloadTime { get; set; }
    public double LastJointForce { get; set; }
    public double LastJointTorque { get; set; }

    public CraftPart(int id, PartDefinition def, int parentId, Vec3d pos, QuatD rot, int stage)
    {
        Id = id; Def = def; ParentId = parentId; Position = pos; Rotation = rot; Stage = stage;
        foreach (var r in def.Resources)
            Resources[r.Name] = r.Initial < 0 ? r.Capacity : Math.Min(r.Initial, r.Capacity);
    }

    public double Amount(string res) => Resources.TryGetValue(res, out var v) ? v : 0.0;
    public double Capacity(string res) => Def.ResourceCapacity(res);

    public double ResourceMass()
    {
        double m = 0;
        foreach (var (name, amt) in Resources) m += amt * PartCatalog.Resources[name].MassPerUnit;
        return m;
    }

    public double Mass => Def.DryMass + ResourceMass();

    public bool IsEngine => Def.Engine != null;

    /// <summary>Engine thrust direction in the craft frame including gimbal deflection.</summary>
    public Vec3d ThrustDirection()
    {
        var d = Rotation.Rotate(Def.Engine!.ThrustDirV);
        return (d + GimbalOffset).Normalized;
    }
}
