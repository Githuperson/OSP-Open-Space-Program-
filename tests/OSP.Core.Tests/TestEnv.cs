using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Tests;

/// <summary>Loads the real game data (game/data) so tests exercise exactly what ships.</summary>
public static class TestEnv
{
    public static readonly string DataDir = FindDataDir();
    private static StarSystem? _system;
    private static PartCatalog? _catalog;

    private static string FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "game", "data");
            if (File.Exists(Path.Combine(cand, "system.json"))) return cand;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("game/data not found above " + AppContext.BaseDirectory);
    }

    public static StarSystem NewSystem() => StarSystem.FromJson(File.ReadAllText(Path.Combine(DataDir, "system.json")));
    public static StarSystem System => _system ??= NewSystem();

    public static PartCatalog Catalog => _catalog ??= PartCatalog.FromJson(
        Directory.GetFiles(Path.Combine(DataDir, "parts"), "*.json").OrderBy(f => f)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f))));

    public static CelestialBody Aldara => System.ById("aldara");
}

/// <summary>Fluent helper to assemble test rockets through the real VehicleEditor.</summary>
public sealed class RocketBuilder
{
    public VehicleDesign Design { get; } = new() { Name = "Test Rocket" };
    private readonly VehicleEditor _ed = new(TestEnv.Catalog);
    public int Last { get; private set; } = -1;

    public RocketBuilder Root(string partId) { Last = _ed.SetRoot(Design, partId).Id; return this; }

    /// <summary>Attach below the given part (default: last placed) using its bottom node.</summary>
    public RocketBuilder Below(string partId, int? parent = null, string childNode = "top")
    {
        int pid = parent ?? Last;
        var c = _ed.FindNodeCandidates(Design, partId).First(x => x.ParentId == pid && x.ParentNode == "bottom" && x.ChildNode == childNode);
        Last = _ed.Attach(Design, partId, c)[0].Id;
        return this;
    }

    public RocketBuilder Above(string partId, int? parent = null, string childNode = "bottom")
    {
        int pid = parent ?? Last;
        var c = _ed.FindNodeCandidates(Design, partId).First(x => x.ParentId == pid && x.ParentNode == "top" && x.ChildNode == childNode);
        Last = _ed.Attach(Design, partId, c)[0].Id;
        return this;
    }

    public RocketBuilder Radial(string partId, int parent, int symmetry, double angleDeg = 0, double offsetY = 0)
    {
        var c = _ed.RadialCandidate(Design, parent, partId, angleDeg, offsetY) ?? throw new InvalidOperationException("radial invalid");
        Last = _ed.Attach(Design, partId, c, symmetry)[0].Id;
        return this;
    }

    public RocketBuilder OnSocket(string partId, int decouplerId)
    {
        var c = _ed.FindNodeCandidates(Design, partId).First(x => x.ParentId == decouplerId && x.ParentNode == "outer");
        Last = _ed.Attach(Design, partId, c)[0].Id;
        return this;
    }
}
