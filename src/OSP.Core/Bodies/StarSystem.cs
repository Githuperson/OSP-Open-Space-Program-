using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Util;

namespace OSP.Core.Bodies;

/// <summary>
/// A complete planetary system: bodies on Keplerian rails around their parents (patched-conic model).
/// Universal time t is seconds since the start of the game universe.
/// </summary>
public sealed class StarSystem
{
    public string Name { get; set; } = "";
    /// <summary>Distance (m) at which the star's <c>SolarFluxAtReference</c> is quoted.</summary>
    public double ReferenceDistance { get; set; } = 1.0;
    public string HomeBodyId { get; set; } = "";
    public List<CelestialBody> Bodies { get; set; } = new();

    private readonly Dictionary<string, CelestialBody> _byId = new();
    public CelestialBody Root { get; private set; } = null!;
    public CelestialBody Home => ById(HomeBodyId);

    public CelestialBody ById(string id) =>
        _byId.TryGetValue(id, out var b) ? b : throw new DataException($"Unknown celestial body '{id}'");

    public bool TryGet(string id, out CelestialBody body) => _byId.TryGetValue(id, out body!);

    public static StarSystem FromJson(string json)
    {
        var sys = Json.Deserialize<StarSystem>(json, "star system");
        sys.Link();
        return sys;
    }

    /// <summary>Resolve parent links, build orbits and spheres of influence, create terrain models and validate.</summary>
    public void Link()
    {
        _byId.Clear();
        foreach (var b in Bodies)
        {
            if (string.IsNullOrWhiteSpace(b.Id)) throw new DataException("Body without id");
            if (!_byId.TryAdd(b.Id, b)) throw new DataException($"Duplicate body id '{b.Id}'");
            if (!(b.Mu > 0) || !(b.Radius > 0)) throw new DataException($"Body '{b.Id}': Mu and Radius must be > 0");
            b.Children.Clear();
        }
        CelestialBody? root = null;
        foreach (var b in Bodies)
        {
            if (b.ParentId == null) { if (root != null) throw new DataException("More than one root body"); root = b; continue; }
            if (!_byId.TryGetValue(b.ParentId, out var p)) throw new DataException($"Body '{b.Id}': unknown parent '{b.ParentId}'");
            b.Parent = p; p.Children.Add(b);
        }
        Root = root ?? throw new DataException("No root (star) body defined");
        if (!_byId.ContainsKey(HomeBodyId)) throw new DataException($"Home body '{HomeBodyId}' not found");

        foreach (var b in Bodies)
        {
            if (b.Parent == null) { b.SphereOfInfluence = double.PositiveInfinity; continue; }
            var od = b.OrbitDef ?? throw new DataException($"Body '{b.Id}' needs an orbit");
            b.Orbit = new Orbit(b.Parent.Mu, od.SemiMajorAxis, od.Eccentricity,
                od.InclinationDeg * MathUtil.Deg2Rad, od.LanDeg * MathUtil.Deg2Rad,
                od.ArgPeriapsisDeg * MathUtil.Deg2Rad, od.MeanAnomalyAtEpochDeg * MathUtil.Deg2Rad, 0.0);
            b.SphereOfInfluence = OrbitMath.SphereOfInfluence(od.SemiMajorAxis, b.Mu, b.Parent.Mu);
            if (b.SphereOfInfluence <= b.Radius) throw new DataException($"Body '{b.Id}': sphere of influence smaller than the body");
        }
        foreach (var b in Bodies)
            b.TerrainModel = b.Terrain != null ? new TerrainModel(b, b.Terrain) : null;
    }

    /// <summary>Position/velocity of a body relative to its parent at time t (zero for the root).</summary>
    public (Vec3d r, Vec3d v) StateRelativeToParent(CelestialBody b, double t) =>
        b.Orbit == null ? (Vec3d.Zero, Vec3d.Zero) : b.Orbit.StateAt(t);

    /// <summary>Position/velocity of a body in the root (star-centred) inertial frame.</summary>
    public (Vec3d r, Vec3d v) StateInRoot(CelestialBody b, double t)
    {
        Vec3d r = Vec3d.Zero, v = Vec3d.Zero;
        for (var cur = b; cur.Parent != null; cur = cur.Parent)
        {
            var (cr, cv) = cur.Orbit!.StateAt(t);
            r += cr; v += cv;
        }
        return (r, v);
    }

    /// <summary>Convert a state expressed relative to body <paramref name="from"/> into one relative to <paramref name="to"/>.</summary>
    public (Vec3d r, Vec3d v) ConvertFrame(Vec3d r, Vec3d v, CelestialBody from, CelestialBody to, double t)
    {
        if (from == to) return (r, v);
        var (fr, fv) = StateInRoot(from, t);
        var (tr, tv) = StateInRoot(to, t);
        return (r + fr - tr, v + fv - tv);
    }

    /// <summary>Incident stellar flux (W/m^2) at a root-frame position (inverse-square law).</summary>
    public double SolarFluxAtRootPosition(Vec3d rootPos)
    {
        double d = Math.Max(1.0, rootPos.Length);
        return Root.SolarFluxAtReference * (ReferenceDistance * ReferenceDistance) / (d * d);
    }
}
