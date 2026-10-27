using System;
using System.Collections.Generic;
using Godot;

namespace OSP.Game.Rendering;

/// <summary>Procedural mesh generation (lathe/revolve solids, boxes, spheres) used by all part models.</summary>
public static class MeshFactory
{
    /// <summary>
    /// Revolve a (y, r) profile (ordered bottom to top) about +Y. Adjacent profile segments are smoothed unless the
    /// crease angle exceeds ~40 degrees, in which case vertices are split so edges stay crisp.
    /// </summary>
    public static ArrayMesh Lathe(double[][] profile, int segments, bool smooth = true)
    {
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var idx = new List<int>();
        int n = profile.Length;
        if (n < 2) return new ArrayMesh();

        // Segment normals in the (r,y) plane
        var segN = new Vector2[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dy = profile[i + 1][0] - profile[i][0];
            double dr = profile[i + 1][1] - profile[i][1];
            var t = new Vector2((float)dr, (float)dy);
            segN[i] = t.Length() < 1e-9f ? new Vector2(1, 0) : new Vector2((float)dy, (float)-dr).Normalized();
        }

        float vAcc = 0;
        for (int i = 0; i < n - 1; i++)
        {
            float y0 = (float)profile[i][0], r0 = (float)profile[i][1];
            float y1 = (float)profile[i + 1][0], r1 = (float)profile[i + 1][1];
            if (r0 < 1e-6f && r1 < 1e-6f) continue;
            Vector2 n0 = segN[i], n1 = segN[i];
            if (smooth)
            {
                if (i > 0 && segN[i - 1].Dot(segN[i]) > 0.76f) n0 = (segN[i - 1] + segN[i]).Normalized();
                if (i < n - 2 && segN[i + 1].Dot(segN[i]) > 0.76f) n1 = (segN[i + 1] + segN[i]).Normalized();
            }
            int baseIdx = verts.Count;
            for (int j = 0; j <= segments; j++)
            {
                float a = j * Mathf.Tau / segments;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts.Add(new Vector3(r0 * c, y0, r0 * s));
                norms.Add(new Vector3(n0.X * c, n0.Y, n0.X * s).Normalized());
                uvs.Add(new Vector2((float)j / segments, vAcc));
                verts.Add(new Vector3(r1 * c, y1, r1 * s));
                norms.Add(new Vector3(n1.X * c, n1.Y, n1.X * s).Normalized());
                uvs.Add(new Vector2((float)j / segments, vAcc + 1f));
            }
            for (int j = 0; j < segments; j++)
            {
                int a0 = baseIdx + j * 2, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                // clockwise when viewed from outside (Godot front-face convention)
                idx.Add(a0); idx.Add(a1); idx.Add(b0);
                idx.Add(b0); idx.Add(a1); idx.Add(b1);
            }
            vAcc += 1f;
        }
        return Build(verts, norms, uvs, idx);
    }

    public static ArrayMesh Cylinder(double radius, double length, double radiusTop, int segments)
    {
        double rt = radiusTop >= 0 ? radiusTop : radius;
        double h = length / 2;
        var prof = new List<double[]> { new[] { -h, 0.0 }, new[] { -h, radius }, new[] { h, rt }, new[] { h, 0.0 } };
        return Lathe(prof.ToArray(), segments, false);
    }

    public static ArrayMesh Sphere(double radius, int segments, int rings = 0)
    {
        if (rings <= 0) rings = segments / 2;
        var prof = new double[rings + 1][];
        for (int i = 0; i <= rings; i++)
        {
            double a = -Math.PI / 2 + Math.PI * i / rings;
            prof[i] = new[] { Math.Sin(a) * radius, Math.Cos(a) * radius };
        }
        return Lathe(prof, segments, true);
    }

    public static ArrayMesh Box(Vector3 size)
    {
        var h = size / 2;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var idx = new List<int>();
        void Face(Vector3 nrm, Vector3 u, Vector3 v)
        {
            int b = verts.Count;
            var c = nrm * (Math.Abs(nrm.X) * h.X + Math.Abs(nrm.Y) * h.Y + Math.Abs(nrm.Z) * h.Z);
            var uu = u * (Math.Abs(u.X) * h.X + Math.Abs(u.Y) * h.Y + Math.Abs(u.Z) * h.Z);
            var vv = v * (Math.Abs(v.X) * h.X + Math.Abs(v.Y) * h.Y + Math.Abs(v.Z) * h.Z);
            verts.Add(c - uu - vv); verts.Add(c + uu - vv); verts.Add(c + uu + vv); verts.Add(c - uu + vv);
            for (int i = 0; i < 4; i++) norms.Add(nrm);
            uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
            // u x v == nrm -> counter-clockwise from outside; emit clockwise
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
            idx.Add(b); idx.Add(b + 3); idx.Add(b + 2);
        }
        Face(Vector3.Right, Vector3.Back, Vector3.Up);       // +X : z x y = -x ... see order below
        Face(Vector3.Left, Vector3.Up, Vector3.Back);
        Face(Vector3.Up, Vector3.Right, Vector3.Back);
        Face(Vector3.Down, Vector3.Back, Vector3.Right);
        Face(Vector3.Back, Vector3.Up, Vector3.Right);
        Face(Vector3.Forward, Vector3.Right, Vector3.Up);
        return Build(verts, norms, uvs, idx);
    }

    public static ArrayMesh Build(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> idx)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
