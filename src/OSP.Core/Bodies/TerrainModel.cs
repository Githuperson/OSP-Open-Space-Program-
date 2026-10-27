using OSP.Core.Mathx;

namespace OSP.Core.Bodies;

/// <summary>
/// Deterministic procedural terrain height field defined on the unit sphere (body-fixed frame).
/// This is the single source of truth for both rendering (Godot chunk generation) and collision
/// (flight physics), so what you see is exactly what you hit.
/// </summary>
public sealed class TerrainModel
{
    private readonly TerrainDef _def;
    private readonly CelestialBody _body;
    private readonly Noise3 _noise;
    private readonly Noise3 _noise2;

    public TerrainModel(CelestialBody body, TerrainDef def)
    {
        _body = body; _def = def;
        _noise = new Noise3(def.Seed);
        _noise2 = new Noise3(def.Seed * 7 + 13);
    }

    public bool HasOcean => _def.HasOcean;
    public TerrainDef Definition => _def;

    /// <summary>Raw (unclamped) height in metres above the reference radius.</summary>
    public double RawHeight(Vec3d dir)
    {
        var d = dir.Normalized;
        double A = _def.Amplitude;

        double cont = _noise.Fbm(d * _def.ContinentFrequency, 5) * 1.6 + _def.LandBias;
        double h = cont * A * 0.30;

        double mask = MathUtil.SmoothStep(0.02, 0.55, cont);
        double ridge = _noise2.Ridged(d * _def.MountainFrequency, 6);
        h += ridge * mask * A * 0.95;

        // Fine detail (adds scale variety when flying low); small amplitude so pads stay flat after blending.
        h += _noise.Fbm(d * _def.DetailFrequency, 4) * A * 0.02;

        if (_def.CraterStrength > 0)
        {
            double c = _noise2.Fbm(d * (_def.MountainFrequency * 2.0), 3);
            double pit = Math.Max(0, 1 - Math.Abs(c) * 6.0);        // circular-ish basins around noise zero-crossings
            h -= pit * pit * A * 0.35 * _def.CraterStrength;
            h += Math.Max(0, 0.15 - Math.Abs(Math.Abs(c) * 6.0 - 1.0)) * A * 0.4 * _def.CraterStrength;
        }

        h = ApplyLaunchSites(d, h);
        return h;
    }

    /// <summary>Collision/visual surface height: raw height, clamped to sea level where the body has an ocean.</summary>
    public double SurfaceHeight(Vec3d dir)
    {
        double h = RawHeight(dir);
        return _def.HasOcean && h < 0 ? 0.0 : h;
    }

    private double ApplyLaunchSites(Vec3d d, double h)
    {
        foreach (var s in _body.LaunchSites)
        {
            var sd = CelestialBody.DirectionFromLatLon(s.LatitudeDeg * MathUtil.Deg2Rad, s.LongitudeDeg * MathUtil.Deg2Rad);
            double cosA = Vec3d.Dot(d, sd);
            if (cosA < 0.9) continue;
            double dist = Math.Acos(Math.Clamp(cosA, -1, 1)) * _body.Radius;
            double outer = s.FlatRadius * 4.0;
            if (dist >= outer) continue;
            double w = 1.0 - MathUtil.SmoothStep(s.FlatRadius, outer, dist);
            // Blend toward the pad height; far field keeps natural terrain (lowered only if it would poke above the pad).
            h = MathUtil.Lerp(h, s.Height, w);
        }
        return h;
    }

    /// <summary>Approximate outward surface normal at a direction via central differences (body-fixed frame).</summary>
    public Vec3d SurfaceNormal(Vec3d dir, double sampleMeters = 5.0)
    {
        var d = dir.Normalized;
        var t1 = Vec3d.Cross(d, Math.Abs(d.Y) < 0.99 ? Vec3d.UnitY : Vec3d.UnitX).Normalized;
        var t2 = Vec3d.Cross(d, t1);
        double e = sampleMeters / _body.Radius;
        double R = _body.Radius;
        var pxp = (d + t1 * e).Normalized; var pxn = (d - t1 * e).Normalized;
        var pyp = (d + t2 * e).Normalized; var pyn = (d - t2 * e).Normalized;
        double hxp = SurfaceHeight(pxp), hxn = SurfaceHeight(pxn), hyp = SurfaceHeight(pyp), hyn = SurfaceHeight(pyn);
        var a = (pxp * (R + hxp)) - (pxn * (R + hxn));
        var b = (pyp * (R + hyp)) - (pyn * (R + hyn));
        var n = Vec3d.Cross(a, b).Normalized;
        if (Vec3d.Dot(n, d) < 0) n = -n;
        return n;
    }

    /// <summary>Named surface biome used by science: depends on latitude, altitude and water.</summary>
    public string Biome(Vec3d dir)
    {
        var d = dir.Normalized;
        var (lat, _) = CelestialBody.LatLonFromDirection(d);
        double latDeg = Math.Abs(lat) * MathUtil.Rad2Deg;
        double raw = RawHeight(d);
        foreach (var s in _body.LaunchSites)
        {
            var sd = CelestialBody.DirectionFromLatLon(s.LatitudeDeg * MathUtil.Deg2Rad, s.LongitudeDeg * MathUtil.Deg2Rad);
            if (Math.Acos(Math.Clamp(Vec3d.Dot(d, sd), -1, 1)) * _body.Radius < s.FlatRadius) return "Launch Site";
        }
        if (_def.HasOcean && raw < -5) return "Ocean";
        if (latDeg >= _def.PolarLatitudeDeg) return "Polar Caps";
        if (_def.HasOcean && raw < 40) return "Shores";
        if (raw > _def.Amplitude * 0.45) return "Mountains";
        if (raw > _def.Amplitude * 0.18) return "Highlands";
        return "Lowlands";
    }
}
