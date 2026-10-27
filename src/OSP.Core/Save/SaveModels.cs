using OSP.Core.Mathx;
using OSP.Core.Vehicles;

namespace OSP.Core.Save;

/// <summary>Player-facing settings stored separately from saved games (settings.json).</summary>
public sealed class PlayerSettings
{
    public int FormatVersion { get; set; } = 1;
    /// <summary>0 = Low, 1 = Medium, 2 = High.</summary>
    public int GraphicsQuality { get; set; } = 1;
    public double RenderScale { get; set; } = 1.0;
    public double MouseSensitivity { get; set; } = 1.0;
    public double FieldOfView { get; set; } = 70;
    public double MasterVolume { get; set; } = 0.8;
    public bool Vsync { get; set; } = true;
    public bool Fullscreen { get; set; }
    public Dictionary<string, string> KeyBindings { get; set; } = new();
}

public sealed class PartStateSave
{
    public int Id { get; set; }
    public string PartId { get; set; } = "";
    public int ParentId { get; set; } = -1;
    public int Mode { get; set; }
    public string ParentNode { get; set; } = "";
    public string ChildNode { get; set; } = "";
    public int Stage { get; set; } = -1;
    public int SymmetryGroup { get; set; }
    public double[] Position { get; set; } = new double[3];
    public double[] Rotation { get; set; } = { 1, 0, 0, 0 };
    public Dictionary<string, double> Resources { get; set; } = new();
    public bool Ignited { get; set; }
    public bool FlamedOut { get; set; }
    public bool LegsDown { get; set; }
    public double LegDeploy { get; set; }
    public bool PanelsOut { get; set; }
    public double SolarDeploy { get; set; }
    public int Chute { get; set; }
    public double ChuteProgress { get; set; }
}

public sealed class ManeuverSave
{
    public int Id { get; set; }
    public double Time { get; set; }
    public double Prograde { get; set; }
    public double Normal { get; set; }
    public double Radial { get; set; }
}

public sealed class CraftSave
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BodyId { get; set; } = "";
    public int Mode { get; set; }
    public double[] Position { get; set; } = new double[3];
    public double[] Velocity { get; set; } = new double[3];
    public double[] Orientation { get; set; } = { 1, 0, 0, 0 };
    public double[] AngularVelocity { get; set; } = new double[3];
    public double[] LandedLocalPosition { get; set; } = new double[3];
    public double[] LandedLocalOrientation { get; set; } = { 1, 0, 0, 0 };
    public int CurrentStage { get; set; }
    public bool HasLaunched { get; set; }
    public double MissionStartTime { get; set; }
    public double Throttle { get; set; }
    public bool SasEnabled { get; set; }
    public int SasMode { get; set; }
    public bool RcsEnabled { get; set; }
    public List<PartStateSave> Parts { get; set; } = new();
    public List<ManeuverSave> Maneuvers { get; set; } = new();
}

/// <summary>Career/science progress. Currently stores only the mode and a sandbox flag; fields are reserved for Milestone 5.</summary>
public sealed class ProgressSave
{
    public string GameMode { get; set; } = "sandbox";
    public double Funds { get; set; }
    public double Science { get; set; }
    public List<string> UnlockedTechNodes { get; set; } = new();
    public List<string> CompletedObjectives { get; set; } = new();
}

public sealed class SaveGame
{
    public const string Kind = "osp-save";
    public const int CurrentVersion = 1;

    public string KindTag { get; set; } = Kind;
    public int FormatVersion { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Quicksave";
    public string SavedAtUtc { get; set; } = "";
    public string SystemName { get; set; } = "";
    public double UniversalTime { get; set; }
    public int ActiveCraftId { get; set; } = -1;
    public int NextCraftId { get; set; } = 1;
    public List<CraftSave> Crafts { get; set; } = new();
    public ProgressSave Progress { get; set; } = new();
}

/// <summary>Wrapper for a vehicle blueprint file.</summary>
public sealed class VehicleFile
{
    public const string Kind = "osp-vehicle";
    public string KindTag { get; set; } = Kind;
    public int FormatVersion { get; set; } = VehicleDesign.CurrentFormatVersion;
    public VehicleDesign Design { get; set; } = new();
}
