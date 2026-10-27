using System;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Mathx;

namespace OSP.Game.Rendering;

public readonly record struct ChunkKey(int Face, int Level, int X, int Y)
{
    public ChunkKey Parent => new(Face, Level - 1, X >> 1, Y >> 1);
    public ChunkKey Child(int cx, int cy) => new(Face, Level + 1, X * 2 + cx, Y * 2 + cy);
}

/// <summary>Raw mesh data for one terrain chunk, produced off the main thread.</summary>
public sealed class ChunkData
{
    public ChunkKey Key;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Color[] Colors = Array.Empty<Color>();
    public Vector2[] Uv = Array.Empty<Vector2>();
    public Vector2[] Uv2 = Array.Empty<Vector2>();
    public Vec3d Center;            // body-fixed position of the chunk origin (m)
}

/// <summary>Cube-sphere geometry and chunk mesh generation. Thread-safe: only reads immutable terrain data.</summary>
public static class PlanetChunkBuilder
{
    public const int N = 48;                     // quads per chunk edge
    private static readonly Vec3d[] FaceN = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
    private static readonly Vec3d[] FaceU = { new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(-1, 0, 0) };
    private static readonly Vec3d[] FaceV = { new(0, 1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0) };

    public static readonly int[] Indices = BuildIndices();

    /// <summary>Unit direction on the sphere for face coordinates s,t in [0,1].</summary>
    public static Vec3d Direction(int face, double s, double t)
    {
        double a = Math.Tan(Math.PI / 4 * (2 * s - 1)), b = Math.Tan(Math.PI / 4 * (2 * t - 1));
        return (FaceN[face] + FaceU[face] * a + FaceV[face] * b).Normalized;
    }

    public static Vec3d CenterDirection(ChunkKey k)
    {
        double n = 1 << k.Level;
        return Direction(k.Face, (k.X + 0.5) / n, (k.Y + 0.5) / n);
    }

    /// <summary>Approximate edge length of a chunk in metres.</summary>
    public static double ChunkSize(double radius, int level) => Math.PI * 0.5 * radius / (1 << level);

    private static int[] BuildIndices()
    {
        var idx = new System.Collections.Generic.List<int>();
        int W = N + 1;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                int v00 = j * W + i, v10 = v00 + 1, v01 = v00 + W, v11 = v01 + 1;
                idx.Add(v00); idx.Add(v11); idx.Add(v10);
                idx.Add(v00); idx.Add(v01); idx.Add(v11);
            }
        // skirts: boundary walked counter-clockwise, skirt vertices appended after the grid in the same order
        int baseSkirt = W * W;
        var border = BorderOrder();
        for (int k = 0; k < border.Length; k++)
        {
            int a = border[k], b = border[(k + 1) % border.Length];
            int sa = baseSkirt + k, sb = baseSkirt + (k + 1) % border.Length;
            idx.Add(a); idx.Add(b); idx.Add(sb);
            idx.Add(a); idx.Add(sb); idx.Add(sa);
        }
        return idx.ToArray();
    }

    private static int[] BorderOrder()
    {
        int W = N + 1;
        var l = new System.Collections.Generic.List<int>();
        for (int i = 0; i < N; i++) l.Add(0 * W + i);          // bottom edge, +u
        for (int j = 0; j < N; j++) l.Add(j * W + N);          // right edge, +v
        for (int i = N; i > 0; i--) l.Add(N * W + i);          // top edge, -u
        for (int j = N; j > 0; j--) l.Add(j * W + 0);          // left edge, -v
        return l.ToArray();
    }

    public static ChunkData Build(CelestialBody body, ChunkKey key)
    {
        var tm = body.TerrainModel;
        double R = body.Radius;
        double n = 1 << key.Level;
        int W = N + 1;
        var centerDir = CenterDirection(key);
        var center = centerDir * R;

        int G = N + 3;                      // grid with one extra ring on every side for normals
        var pos = new Vec3d[G * G];
        var raw = new double[G * G];
        var dirs = new Vec3d[G * G];
        for (int j = -1; j <= N + 1; j++)
            for (int i = -1; i <= N + 1; i++)
            {
                var dir = Direction(key.Face, (key.X + (double)i / N) / n, (key.Y + (double)j / N) / n);
                double rh = tm?.RawHeight(dir) ?? 0.0;
                double h = (tm != null && tm.HasOcean && rh < 0) ? 0.0 : rh;
                int gi = (j + 1) * G + (i + 1);
                pos[gi] = dir * (R + h); raw[gi] = rh; dirs[gi] = dir;
            }

        int count = W * W + 4 * N;
        var d = new ChunkData { Key = key, Center = center };
        d.Vertices = new Vector3[count]; d.Normals = new Vector3[count]; d.Colors = new Color[count];
        d.Uv = new Vector2[count]; d.Uv2 = new Vector2[count];
        var def = body.Terrain;
        double skirt = Math.Max(3.0, ChunkSize(R, key.Level) * 0.06);

        for (int j = 0; j <= N; j++)
            for (int i = 0; i <= N; i++)
            {
                int gi = (j + 1) * G + (i + 1);
                var p = pos[gi];
                var tu = pos[gi + 1] - pos[gi - 1];
                var tv = pos[gi + G] - pos[gi - G];
                var nrm = Vec3d.Cross(tu, tv).Normalized;
                if (Vec3d.Dot(nrm, dirs[gi]) < 0) nrm = -nrm;
                double slope = Vec3d.Dot(nrm, dirs[gi]);
                var rel = p - center;
                int vi = j * W + i;
                d.Vertices[vi] = new Vector3((float)rel.X, (float)rel.Y, (float)rel.Z);
                d.Normals[vi] = new Vector3((float)nrm.X, (float)nrm.Y, (float)nrm.Z);
                d.Colors[vi] = def != null ? TerrainColors.Evaluate(def, tm!, raw[gi], slope, dirs[gi]) : new Color(0.5f, 0.5f, 0.5f, 1f);
                d.Uv[vi] = new Vector2((float)(p.X * 1e-3), (float)(p.Y * 1e-3));
                d.Uv2[vi] = new Vector2((float)(p.Z * 1e-3), 0f);
            }

        var border = BorderOrder();
        for (int k = 0; k < border.Length; k++)
        {
            int src = border[k], dst = W * W + k;
            var v = d.Vertices[src];
            // drop along the local down direction (towards the planet centre in chunk space)
            var down = -new Vector3((float)centerDir.X, (float)centerDir.Y, (float)centerDir.Z);
            d.Vertices[dst] = v + down * (float)skirt;
            d.Normals[dst] = d.Normals[src]; d.Colors[dst] = d.Colors[src]; d.Uv[dst] = d.Uv[src]; d.Uv2[dst] = d.Uv2[src];
        }
        return d;
    }
}

/// <summary>Biome colouring of terrain: elevation bands, slope rock, shores, polar caps and ocean.</summary>
public static class TerrainColors
{
    private static Color C(double[] c) => new((float)c[0], (float)c[1], (float)c[2]);

    public static Color Evaluate(TerrainDef def, TerrainModel tm, double rawHeight, double slopeDot, Vec3d dir)
    {
        if (def.HasOcean && rawHeight < 0)
        {
            double depth = Math.Clamp(-rawHeight / 900.0, 0, 1);
            var shallow = C(def.ColorOcean).Lerp(new Color(0.1f, 0.45f, 0.55f), 0.55f);
            var c = shallow.Lerp(C(def.ColorOcean), (float)depth);
            return new Color(c.R, c.G, c.B, 0f);
        }
        double e = Math.Clamp(rawHeight / Math.Max(1.0, def.Amplitude), 0, 1.2);
        Color low = C(def.ColorLow), mid = C(def.ColorMid), high = C(def.ColorHigh);
        Color col = e < 0.35 ? low.Lerp(mid, (float)(e / 0.35)) : mid.Lerp(high, (float)Math.Clamp((e - 0.35) / 0.4, 0, 1));
        if (def.HasOcean && rawHeight < 9) col = C(def.ColorShore).Lerp(col, (float)Math.Clamp(rawHeight / 9.0, 0, 1));
        double rock = Math.Clamp((0.93 - slopeDot) * 9.0, 0, 1);
        if (rock > 0) col = col.Lerp(high.Lerp(mid, 0.4f) * 0.8f, (float)rock);
        double latDeg = Math.Abs(Math.Asin(Math.Clamp(dir.Y, -1, 1))) * MathUtil.Rad2Deg;
        if (latDeg > def.PolarLatitudeDeg - 4)
        {
            double k = Math.Clamp((latDeg - (def.PolarLatitudeDeg - 4)) / 8.0, 0, 1);
            col = col.Lerp(C(def.ColorPolar), (float)k * (float)(1 - rock * 0.6));
        }
        return new Color(col.R, col.G, col.B, 1f);
    }
}
