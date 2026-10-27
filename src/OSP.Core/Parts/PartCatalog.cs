using OSP.Core.Mathx;
using OSP.Core.Util;

namespace OSP.Core.Parts;

public sealed class PartFile
{
    public List<PartDefinition> Parts { get; set; } = new();
}

public sealed class ResourceInfo
{
    public string Name { get; init; } = "";
    /// <summary>Mass in kg of one unit of the resource (0 for electric charge).</summary>
    public double MassPerUnit { get; init; }
    public string Unit { get; init; } = "kg";
}

/// <summary>Immutable registry of all part definitions plus the resource table. Built from JSON data files.</summary>
public sealed class PartCatalog
{
    public const string Propellant = "Propellant";
    public const string SolidFuel = "SolidFuel";
    public const string Monopropellant = "Monopropellant";
    public const string ElectricCharge = "ElectricCharge";

    public static readonly IReadOnlyDictionary<string, ResourceInfo> Resources = new Dictionary<string, ResourceInfo>
    {
        [Propellant] = new ResourceInfo { Name = Propellant, MassPerUnit = 1.0, Unit = "kg" },
        [SolidFuel] = new ResourceInfo { Name = SolidFuel, MassPerUnit = 1.0, Unit = "kg" },
        [Monopropellant] = new ResourceInfo { Name = Monopropellant, MassPerUnit = 1.0, Unit = "kg" },
        [ElectricCharge] = new ResourceInfo { Name = ElectricCharge, MassPerUnit = 0.0, Unit = "EC" },
    };

    private readonly Dictionary<string, PartDefinition> _parts = new();
    private readonly List<PartDefinition> _ordered = new();

    public IReadOnlyList<PartDefinition> All => _ordered;
    public IEnumerable<string> Categories => _ordered.Select(p => p.Category).Distinct();

    public PartDefinition Get(string id) =>
        _parts.TryGetValue(id, out var p) ? p : throw new DataException($"Unknown part '{id}'");

    public bool TryGet(string id, out PartDefinition part) => _parts.TryGetValue(id, out part!);

    public static PartCatalog FromJson(IEnumerable<(string name, string json)> files)
    {
        var cat = new PartCatalog();
        foreach (var (name, json) in files)
        {
            var file = Json.Deserialize<PartFile>(json, $"part file '{name}'");
            foreach (var p in file.Parts) cat.Add(p, name);
        }
        return cat;
    }

    public void Add(PartDefinition p, string source = "?")
    {
        Validate(p, source);
        if (!_parts.TryAdd(p.Id, p)) throw new DataException($"{source}: duplicate part id '{p.Id}'");
        _ordered.Add(p);
    }

    private static void Validate(PartDefinition p, string source)
    {
        string who = $"{source}: part '{p.Id}'";
        if (string.IsNullOrWhiteSpace(p.Id)) throw new DataException($"{source}: part without id");
        if (p.DryMass <= 0) throw new DataException($"{who}: DryMass must be > 0");
        if (p.Shape.Length <= 0 && !p.Shape.IsBox) throw new DataException($"{who}: Shape.Length must be > 0");
        var seen = new HashSet<string>();
        foreach (var n in p.Nodes)
        {
            if (!seen.Add(n.Id)) throw new DataException($"{who}: duplicate node '{n.Id}'");
            if (n.Position.Length != 3 || n.Direction.Length != 3) throw new DataException($"{who}: node '{n.Id}' needs 3-component vectors");
            if (n.Kind != "stack" && n.Kind != "surface" && n.Kind != "socket") throw new DataException($"{who}: node '{n.Id}' has unknown kind '{n.Kind}'");
        }
        foreach (var r in p.Resources)
        {
            if (!Resources.ContainsKey(r.Name)) throw new DataException($"{who}: unknown resource '{r.Name}'");
            if (r.Capacity < 0) throw new DataException($"{who}: negative capacity for '{r.Name}'");
        }
        if (p.Engine is { } e)
        {
            if (e.ThrustVac <= 0 || e.IspVac <= 0 || e.IspAtm <= 0) throw new DataException($"{who}: engine thrust/Isp must be > 0");
            if (!Resources.ContainsKey(e.Propellant)) throw new DataException($"{who}: engine propellant '{e.Propellant}' unknown");
        }
        if (p.Decoupler is { } d && p.Node(d.CutNode) == null)
            throw new DataException($"{who}: decoupler cut node '{d.CutNode}' does not exist");
    }

    /// <summary>Mass of a part including its initial resource load, kg.</summary>
    public static double WetMass(PartDefinition p)
    {
        double m = p.DryMass;
        foreach (var r in p.Resources)
        {
            double amount = r.Initial < 0 ? r.Capacity : r.Initial;
            m += amount * Resources[r.Name].MassPerUnit;
        }
        return m;
    }
}
