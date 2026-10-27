using System.Collections.Generic;
using Godot;
using OSP.Core.Math;
using OSP.Core.Parts;

namespace OSP.Game.Rendering
{
    /// <summary>Meshes for one part definition: the static body plus one mesh per animated group.</summary>
    public sealed class PartMeshSet
    {
        public ArrayMesh Static;
        /// <summary>Group name → (mesh in pivot-local coordinates, pivot position in part coordinates).</summary>
        public readonly Dictionary<string, (ArrayMesh mesh, Vector3 pivot)> Groups = new();
    }

    /// <summary>
    /// Builds genuine 3D geometry from the procedural primitives in a part definition. Meshes are built once per
    /// part type and shared by all instances.
    /// </summary>
    public static class PartMeshFactory
    {
        private static readonly Dictionary<string, PartMeshSet> Cache = new();

        public static PartMeshSet Get(PartDefinition def)
        {
            if (Cache.TryGetValue(def.Id, out PartMeshSet set)) return set;
            set = Build(def);
            Cache[def.Id] = set;
            return set;
        }

        /// <summary>Pivot (part-local) for a named group, taken from whichever module animates it.</summary>
        public static Vector3d GroupPivot(PartDefinition def, string group)
        {
            foreach (PartModuleDef m in def.Modules)
            {
                switch (m)
                {
                    case ParachuteModuleDef p when p.Animation?.Group == group: return p.Animation.Pivot;
                    case LandingLegModuleDef l when l.Animation?.Group == group: return l.Animation.Pivot;
                    case SolarPanelModuleDef s when s.Animation?.Group == group: return s.Animation.Pivot;
                    case EngineModuleDef e when e.GimbalGroup == group: return e.GimbalPivot;
                }
            }
            return Vector3d.Zero;
        }

        private static PartMeshSet Build(PartDefinition def)
        {
            var set = new PartMeshSet();
            var staticBuilder = new MeshBuilder();
            var groupBuilders = new Dictionary<string, MeshBuilder>();
            foreach (MeshPrimitive prim in def.Mesh)
            {
                MeshBuilder target = staticBuilder;
                Vector3 pivot = Vector3.Zero;
                if (prim.Group != null)
                {
                    if (!groupBuilders.TryGetValue(prim.Group, out target)) groupBuilders[prim.Group] = target = new MeshBuilder();
                    pivot = GroupPivot(def, prim.Group).ToGodot();
                }
                int repeat = System.Math.Max(1, prim.Repeat);
                Basis rot = new(prim.RotationQuaternion().ToGodot());
                for (int k = 0; k < repeat; k++)
                {
                    Basis rep = new(Vector3.Up, k * Mathf.Tau / repeat);
                    var xf = new Transform3D(rep * rot, rep * prim.Offset.ToGodot() - pivot);
                    AddPrimitive(target, prim, xf);
                }
            }
            set.Static = staticBuilder.IsEmpty ? null : staticBuilder.Commit(MaterialLibrary.Get);
            foreach (var (name, b) in groupBuilders)
                set.Groups[name] = (b.Commit(MaterialLibrary.Get), GroupPivot(def, name).ToGodot());
            return set;
        }

        private static void AddPrimitive(MeshBuilder b, MeshPrimitive p, Transform3D xf)
        {
            switch (p.Type)
            {
                case "lathe":
                    if (p.Profile != null && p.Profile.Length >= 2)
                        Lathe(b, p.Material, ToProfile(p.Profile), p.Segments, p.Smooth, p.CapTop, p.CapBottom, xf);
                    break;
                case "cylinder":
                case "cone":
                {
                    float rt = (float)(double.IsNaN(p.RadiusTop) ? (p.Type == "cone" ? 0 : p.Radius) : p.RadiusTop);
                    var prof = new[] { new Vector2((float)p.Radius, (float)(-p.Height / 2)), new Vector2(rt, (float)(p.Height / 2)) };
                    Lathe(b, p.Material, prof, p.Segments, p.Smooth, p.CapTop, p.CapBottom, xf);
                    break;
                }
                case "box":
                    Box(b, p.Material, p.Size.ToGodot(), xf);
                    break;
                case "sphere":
                    Sphere(b, p.Material, (float)p.Radius, System.Math.Max(8, p.Segments), xf);
                    break;
                case "torus":
                    Torus(b, p.Material, (float)p.Radius, (float)p.Tube, System.Math.Max(8, p.Segments), xf);
                    break;
                case "prism":
                    if (p.Points != null && p.Points.Length >= 3)
                        Prism(b, p.Material, p.Points, (float)p.Thickness, xf);
                    break;
                default:
                    GD.PushWarning($"Unknown mesh primitive type '{p.Type}'.");
                    break;
            }
        }

        private static Vector2[] ToProfile(double[][] pts)
        {
            var r = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++) r[i] = new Vector2((float)pts[i][0], (float)pts[i][1]);
            return r;
        }

        /// <summary>Surface of revolution about +Y. Profile points are (radius, y), bottom to top.</summary>
        public static void Lathe(MeshBuilder b, string mat, Vector2[] profile, int segments, bool smooth, bool capTop, bool capBottom, Transform3D xf)
        {
            segments = System.Math.Max(3, segments);
            Basis nb = xf.Basis.Inverse().Transposed();
            for (int s = 0; s < profile.Length - 1; s++)
            {
                Vector2 p0 = profile[s], p1 = profile[s + 1];
                Vector2 d = p1 - p0;
                if (d.LengthSquared() < 1e-12f) continue;
                // Outward normal in the (r, y) plane.
                Vector2 n2 = new Vector2(d.Y, -d.X).Normalized();
                if (smooth)
                {
                    var ring0 = new int[segments + 1];
                    var ring1 = new int[segments + 1];
                    for (int i = 0; i <= segments; i++)
                    {
                        float a = i * Mathf.Tau / segments;
                        float c = Mathf.Cos(a), sn = Mathf.Sin(a);
                        Vector3 n = (nb * new Vector3(n2.X * c, n2.Y, n2.X * sn)).Normalized();
                        float u = (float)i / segments;
                        ring0[i] = b.AddVertex(mat, xf * new Vector3(p0.X * c, p0.Y, p0.X * sn), n, new Vector2(u, s));
                        ring1[i] = b.AddVertex(mat, xf * new Vector3(p1.X * c, p1.Y, p1.X * sn), n, new Vector2(u, s + 1));
                    }
                    for (int i = 0; i < segments; i++)
                    {
                        // Counter-clockwise seen from outside: angle increases from +X towards +Z.
                        b.AddTriangle(mat, ring0[i], ring1[i], ring1[i + 1]);
                        b.AddTriangle(mat, ring0[i], ring1[i + 1], ring0[i + 1]);
                    }
                }
                else
                {
                    for (int i = 0; i < segments; i++)
                    {
                        float a0 = i * Mathf.Tau / segments, a1 = (i + 1) * Mathf.Tau / segments, am = (a0 + a1) / 2;
                        Vector3 n = (nb * new Vector3(n2.X * Mathf.Cos(am), n2.Y, n2.X * Mathf.Sin(am))).Normalized();
                        Vector3 v00 = xf * new Vector3(p0.X * Mathf.Cos(a0), p0.Y, p0.X * Mathf.Sin(a0));
                        Vector3 v01 = xf * new Vector3(p0.X * Mathf.Cos(a1), p0.Y, p0.X * Mathf.Sin(a1));
                        Vector3 v10 = xf * new Vector3(p1.X * Mathf.Cos(a0), p1.Y, p1.X * Mathf.Sin(a0));
                        Vector3 v11 = xf * new Vector3(p1.X * Mathf.Cos(a1), p1.Y, p1.X * Mathf.Sin(a1));
                        b.AddQuad(mat, v00, v10, v11, v01, n);
                    }
                }
            }
            if (capBottom && profile[0].X > 1e-4f) Disc(b, mat, profile[0].X, profile[0].Y, false, segments, smooth, xf);
            if (capTop && profile[^1].X > 1e-4f) Disc(b, mat, profile[^1].X, profile[^1].Y, true, segments, smooth, xf);
        }

        private static void Disc(MeshBuilder b, string mat, float r, float y, bool up, int segments, bool smooth, Transform3D xf)
        {
            Basis nb = xf.Basis.Inverse().Transposed();
            Vector3 n = (nb * (up ? Vector3.Up : Vector3.Down)).Normalized();
            int center = b.AddVertex(mat, xf * new Vector3(0, y, 0), n, new Vector2(0.5f, 0.5f));
            var ring = new int[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.Tau / segments;
                ring[i] = b.AddVertex(mat, xf * new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a)), n, new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
            }
            for (int i = 0; i < segments; i++)
            {
                // Seen from above (+Y), angle increasing from +X to +Z is clockwise; reverse for the top cap.
                if (up) b.AddTriangle(mat, center, ring[i + 1], ring[i]);
                else b.AddTriangle(mat, center, ring[i], ring[i + 1]);
            }
        }

        public static void Box(MeshBuilder b, string mat, Vector3 size, Transform3D xf)
        {
            Vector3 h = size * 0.5f;
            Basis nb = xf.Basis.Inverse().Transposed();
            void Face(Vector3 n, Vector3 u, Vector3 v)
            {
                // u × v = n: corners c - u - v, c + u - v, c + u + v, c - u + v are counter-clockwise seen from +n.
                Vector3 c = n * Mathf.Abs(n.Dot(h));
                Vector3 uu = u * Mathf.Abs(u.Dot(h)), vv = v * Mathf.Abs(v.Dot(h));
                Vector3 nw = (nb * n).Normalized();
                b.AddQuad(mat, xf * (c - uu - vv), xf * (c + uu - vv), xf * (c + uu + vv), xf * (c - uu + vv), nw);
            }
            Face(Vector3.Right, Vector3.Back * -1, Vector3.Up);     // +X: u = -Z, v = +Y  (−Z × Y = +X)
            Face(Vector3.Left, Vector3.Back, Vector3.Up);           // −X: u = +Z, v = +Y
            Face(Vector3.Up, Vector3.Right, Vector3.Forward);       // +Y: u = +X, v = −Z
            Face(Vector3.Down, Vector3.Right, Vector3.Back);        // −Y: u = +X, v = +Z
            Face(Vector3.Back, Vector3.Right, Vector3.Up);          // +Z: u = +X, v = +Y
            Face(Vector3.Forward, Vector3.Left, Vector3.Up);        // −Z: u = −X, v = +Y
        }

        public static void Sphere(MeshBuilder b, string mat, float r, int segments, Transform3D xf)
        {
            int rings = segments / 2;
            Basis nb = xf.Basis.Inverse().Transposed();
            var idx = new int[rings + 1, segments + 1];
            for (int j = 0; j <= rings; j++)
            {
                float phi = Mathf.Pi * j / rings - Mathf.Pi / 2; // -90° .. +90°
                for (int i = 0; i <= segments; i++)
                {
                    float th = Mathf.Tau * i / segments;
                    Vector3 n = new(Mathf.Cos(phi) * Mathf.Cos(th), Mathf.Sin(phi), Mathf.Cos(phi) * Mathf.Sin(th));
                    idx[j, i] = b.AddVertex(mat, xf * (n * r), (nb * n).Normalized(), new Vector2((float)i / segments, (float)j / rings));
                }
            }
            for (int j = 0; j < rings; j++)
            for (int i = 0; i < segments; i++)
            {
                b.AddTriangle(mat, idx[j, i], idx[j + 1, i], idx[j + 1, i + 1]);
                b.AddTriangle(mat, idx[j, i], idx[j + 1, i + 1], idx[j, i + 1]);
            }
        }

        public static void Torus(MeshBuilder b, string mat, float major, float minor, int segments, Transform3D xf)
        {
            int tubeSeg = 10;
            Basis nb = xf.Basis.Inverse().Transposed();
            var idx = new int[segments + 1, tubeSeg + 1];
            for (int i = 0; i <= segments; i++)
            {
                float th = Mathf.Tau * i / segments;
                Vector3 dir = new(Mathf.Cos(th), 0, Mathf.Sin(th));
                for (int j = 0; j <= tubeSeg; j++)
                {
                    float ph = Mathf.Tau * j / tubeSeg;
                    Vector3 n = dir * Mathf.Cos(ph) + Vector3.Up * Mathf.Sin(ph);
                    idx[i, j] = b.AddVertex(mat, xf * (dir * major + n * minor), (nb * n).Normalized());
                }
            }
            for (int i = 0; i < segments; i++)
            for (int j = 0; j < tubeSeg; j++)
            {
                b.AddTriangle(mat, idx[i, j], idx[i, j + 1], idx[i + 1, j + 1]);
                b.AddTriangle(mat, idx[i, j], idx[i + 1, j + 1], idx[i + 1, j]);
            }
        }

        /// <summary>Convex polygon in the XY plane extruded symmetrically along Z.</summary>
        public static void Prism(MeshBuilder b, string mat, double[][] points, float thickness, Transform3D xf)
        {
            int n = points.Length;
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++) pts[i] = new Vector2((float)points[i][0], (float)points[i][1]);
            // Ensure counter-clockwise order in XY.
            float area = 0;
            for (int i = 0; i < n; i++) area += pts[i].X * pts[(i + 1) % n].Y - pts[(i + 1) % n].X * pts[i].Y;
            if (area < 0) System.Array.Reverse(pts);
            float hz = thickness / 2;
            Basis nb = xf.Basis.Inverse().Transposed();
            Vector3 nf = (nb * Vector3.Back).Normalized(), nk = (nb * Vector3.Forward).Normalized();
            int c0 = b.AddVertex(mat, xf * new Vector3(pts[0].X, pts[0].Y, hz), nf);
            var front = new int[n];
            var back = new int[n];
            front[0] = c0;
            for (int i = 1; i < n; i++) front[i] = b.AddVertex(mat, xf * new Vector3(pts[i].X, pts[i].Y, hz), nf);
            for (int i = 0; i < n; i++) back[i] = b.AddVertex(mat, xf * new Vector3(pts[i].X, pts[i].Y, -hz), nk);
            for (int i = 1; i < n - 1; i++)
            {
                b.AddTriangle(mat, front[0], front[i], front[i + 1]);   // CCW seen from +Z
                b.AddTriangle(mat, back[0], back[i + 1], back[i]);      // CCW seen from −Z
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 a = pts[i], c = pts[(i + 1) % n];
                Vector2 e = c - a;
                Vector3 sideN = (nb * new Vector3(e.Y, -e.X, 0)).Normalized();
                b.AddQuad(mat, xf * new Vector3(a.X, a.Y, -hz), xf * new Vector3(c.X, c.Y, -hz), xf * new Vector3(c.X, c.Y, hz), xf * new Vector3(a.X, a.Y, hz), sideN);
            }
        }
    }
}
