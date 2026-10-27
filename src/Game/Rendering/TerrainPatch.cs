using Godot;
using OSP.Core.Celestial;
using OSP.Core.Math;

namespace OSP.Game.Rendering
{
    /// <summary>Raw mesh arrays for one terrain patch, produced on a worker thread.</summary>
    public sealed class TerrainPatchData
    {
        public Vector3d Center;           // body-fixed patch origin (m)
        public Vector3[] Vertices;        // relative to Center, in output units
        public Vector3[] Normals;
        public Color[] Colors;
        public int[] Indices;
    }

    /// <summary>
    /// Cube-sphere terrain patch generation. Each cube face is parameterised by (u, v) ∈ [0, 1]²; a patch is a
    /// square sub-range sampled on an (N+1)² grid, projected onto the sphere and displaced by the body's terrain
    /// height function — the same function used for collision, so what you see is what you land on.
    /// Edges carry skirts to hide cracks between levels of detail.
    /// </summary>
    public static class TerrainPatch
    {
        // Face normal, u axis, v axis (u × v = normal, so increasing u then v is counter-clockwise from outside).
        public static readonly (Vector3d n, Vector3d a, Vector3d b)[] Faces =
        {
            (Vector3d.UnitX, -Vector3d.UnitZ, Vector3d.UnitY),
            (-Vector3d.UnitX, Vector3d.UnitZ, Vector3d.UnitY),
            (Vector3d.UnitY, Vector3d.UnitX, -Vector3d.UnitZ),
            (-Vector3d.UnitY, Vector3d.UnitX, Vector3d.UnitZ),
            (Vector3d.UnitZ, Vector3d.UnitX, Vector3d.UnitY),
            (-Vector3d.UnitZ, -Vector3d.UnitX, Vector3d.UnitY),
        };

        public static Vector3d Direction(int face, double u, double v)
        {
            var (n, a, b) = Faces[face];
            // Tangent warp makes cells more uniform in size than a plain normalised cube.
            double x = System.Math.Tan((2 * u - 1) * System.Math.PI / 4);
            double y = System.Math.Tan((2 * v - 1) * System.Math.PI / 4);
            return (n + a * x + b * y).Normalized();
        }

        /// <summary>
        /// Generates a patch. <paramref name="unitScale"/> converts metres to output units (1 for the near world,
        /// 0.001 for scaled space). <paramref name="relativeToCenter"/> chooses whether vertices are relative to the
        /// patch centre (near world, for float precision) or to the body centre (scaled space).
        /// </summary>
        public static TerrainPatchData Generate(CelestialBody body, int face, double u0, double v0, double size, int n,
            double unitScale, bool relativeToCenter, bool skirts)
        {
            TerrainGenerator terrain = body.Terrain;
            TerrainSettings ts = terrain?.Settings;
            double radius = body.Radius;
            int g = n + 3; // grid with a one-sample border for normals
            var pos = new Vector3d[g * g];
            var raw = new double[g * g];
            var water = new bool[g * g];
            for (int j = 0; j < g; j++)
            for (int i = 0; i < g; i++)
            {
                double u = u0 + (i - 1) * size / n;
                double v = v0 + (j - 1) * size / n;
                Vector3d dir = Direction(face, u, v);
                double h = terrain?.RawHeight(dir) ?? 0;
                bool w = ts != null && ts.HasOcean && h < 0;
                raw[j * g + i] = h;
                water[j * g + i] = w;
                pos[j * g + i] = dir * (radius + (w ? 0 : h));
            }

            Vector3d centerDir = Direction(face, u0 + size / 2, v0 + size / 2);
            double centerH = terrain?.SurfaceHeight(centerDir) ?? 0;
            Vector3d center = relativeToCenter ? centerDir * (radius + centerH) : Vector3d.Zero;

            int vCount = (n + 1) * (n + 1);
            int skirtCount = skirts ? 4 * (n + 1) : 0;
            var verts = new Vector3[vCount + skirtCount];
            var normals = new Vector3[vCount + skirtCount];
            var colors = new Color[vCount + skirtCount];

            Color low = ts?.LowColor.ToColor() ?? new Color(0.5f, 0.5f, 0.5f);
            Color high = ts?.HighColor.ToColor() ?? new Color(0.6f, 0.6f, 0.6f);
            Color peak = ts?.PeakColor.ToColor() ?? new Color(0.8f, 0.8f, 0.8f);
            Color shore = ts?.ShoreColor.ToColor() ?? low;
            Color ocean = ts?.OceanColor.ToColor() ?? new Color(0.05f, 0.1f, 0.3f);
            double snow = ts?.SnowHeight ?? 1e9;
            double relief = System.Math.Max(1, (ts?.MountainAmplitude ?? 1000) + (ts?.HillAmplitude ?? 500));

            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                int gi = (j + 1) * g + (i + 1);
                Vector3d p = pos[gi];
                Vector3d du = pos[gi + 1] - pos[gi - 1];
                Vector3d dv = pos[gi + g] - pos[gi - g];
                Vector3d nrm = Vector3d.Cross(du, dv).Normalized();
                Vector3d radial = p.Normalized();
                if (Vector3d.Dot(nrm, radial) < 0) nrm = -nrm;
                int vi = j * (n + 1) + i;
                verts[vi] = ((p - center) * unitScale).ToGodot();
                normals[vi] = nrm.ToGodot();
                colors[vi] = ColorFor(raw[gi], water[gi], Vector3d.Dot(nrm, radial), low, high, peak, shore, ocean, snow, relief, p);
            }

            var indices = new System.Collections.Generic.List<int>(n * n * 6 + skirtCount * 6);
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + (n + 1) + 1, d = a + (n + 1);
                // Counter-clockwise from outside (u right, v up) then reversed for Godot's clockwise front faces.
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(a); indices.Add(d); indices.Add(c);
            }

            if (skirts)
            {
                double depth = System.Math.Max(20, size * radius * 0.02);
                int s = vCount;
                // Edge loops: bottom (j=0), right (i=n), top (j=n), left (i=0), each going counter-clockwise.
                int[] edge = new int[4 * (n + 1)];
                int k = 0;
                for (int i = 0; i <= n; i++) edge[k++] = i;                         // bottom, i increasing
                for (int j = 0; j <= n; j++) edge[k++] = j * (n + 1) + n;           // right, j increasing
                for (int i = n; i >= 0; i--) edge[k++] = n * (n + 1) + i;           // top, i decreasing
                for (int j = n; j >= 0; j--) edge[k++] = j * (n + 1);               // left, j decreasing
                for (int e = 0; e < edge.Length; e++)
                {
                    int src = edge[e];
                    Vector3d worldP = ((Vector3d)new Vector3d(verts[src].X, verts[src].Y, verts[src].Z)) / unitScale + center;
                    Vector3d down = worldP.Normalized() * (-depth);
                    verts[s + e] = ((worldP + down - center) * unitScale).ToGodot();
                    normals[s + e] = normals[src];
                    colors[s + e] = colors[src];
                }
                for (int side = 0; side < 4; side++)
                {
                    for (int q = 0; q < n; q++)
                    {
                        int e0 = side * (n + 1) + q, e1 = e0 + 1;
                        int top0 = edge[e0], top1 = edge[e1], bot0 = s + e0, bot1 = s + e1;
                        // Skirt faces outward from the patch.
                        indices.Add(top0); indices.Add(bot1); indices.Add(bot0);
                        indices.Add(top0); indices.Add(top1); indices.Add(bot1);
                    }
                }
            }

            return new TerrainPatchData { Center = center, Vertices = verts, Normals = normals, Colors = colors, Indices = indices.ToArray() };
        }

        private static Color ColorFor(double h, bool water, double flatness, Color low, Color high, Color peak, Color shore, Color ocean,
            double snow, double relief, Vector3d p)
        {
            if (water)
            {
                float depthT = (float)MathUtil.Clamp01(-h / 1500.0);
                Color c = ocean.Lerp(ocean * 0.55f, depthT);
                c.A = 1;
                return c;
            }
            // Cheap deterministic variation so large areas are not uniform.
            double jitter = System.Math.Sin(p.X * 0.00071 + p.Z * 0.00053) * System.Math.Cos(p.Y * 0.00063 - p.X * 0.00041);
            float t = (float)MathUtil.Clamp01(h / relief * 1.4 + jitter * 0.08);
            Color c2 = low.Lerp(high, Mathf.SmoothStep(0.05f, 0.65f, t));
            if (h < 25 && shore != low) c2 = shore.Lerp(c2, (float)MathUtil.Clamp01(h / 25.0));
            float steep = (float)MathUtil.Clamp01((0.93 - flatness) / 0.15);
            c2 = c2.Lerp(high * 0.7f, steep * 0.75f);
            if (h > snow * (0.85 + jitter * 0.1) && flatness > 0.8) c2 = c2.Lerp(peak, (float)MathUtil.Clamp01((h - snow * 0.85) / (snow * 0.2)));
            c2.A = 0;
            return c2;
        }

        public static ArrayMesh ToMesh(TerrainPatchData d)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = d.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = d.Normals;
            arrays[(int)Mesh.ArrayType.Color] = d.Colors;
            arrays[(int)Mesh.ArrayType.Index] = d.Indices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }
}
