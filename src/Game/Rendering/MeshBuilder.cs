using System.Collections.Generic;
using Godot;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// Accumulates triangles grouped by material and produces an <see cref="ArrayMesh"/> with one surface per
    /// material. Triangles are supplied counter-clockwise as seen from their front side; <see cref="Commit"/>
    /// converts to Godot's clockwise front-face convention.
    /// </summary>
    public sealed class MeshBuilder
    {
        private sealed class Surface
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector2> Uvs = new();
            public readonly List<int> Indices = new();
        }

        private readonly Dictionary<string, Surface> _surfaces = new();

        public bool IsEmpty => _surfaces.Count == 0;

        private Surface Get(string material)
        {
            if (!_surfaces.TryGetValue(material, out Surface s)) _surfaces[material] = s = new Surface();
            return s;
        }

        public int AddVertex(string material, Vector3 position, Vector3 normal, Vector2 uv = default)
        {
            Surface s = Get(material);
            s.Vertices.Add(position);
            s.Normals.Add(normal);
            s.Uvs.Add(uv);
            return s.Vertices.Count - 1;
        }

        /// <summary>Adds a triangle by vertex indices, counter-clockwise from the front.</summary>
        public void AddTriangle(string material, int a, int b, int c)
        {
            List<int> idx = Get(material).Indices;
            // Godot treats clockwise winding as front-facing: emit reversed.
            idx.Add(a);
            idx.Add(c);
            idx.Add(b);
        }

        /// <summary>Adds a flat quad (corners counter-clockwise from the front) with a single normal.</summary>
        public void AddQuad(string material, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
        {
            int a = AddVertex(material, p0, normal, new Vector2(0, 0));
            int b = AddVertex(material, p1, normal, new Vector2(1, 0));
            int c = AddVertex(material, p2, normal, new Vector2(1, 1));
            int d = AddVertex(material, p3, normal, new Vector2(0, 1));
            AddTriangle(material, a, b, c);
            AddTriangle(material, a, c, d);
        }

        public ArrayMesh Commit(System.Func<string, Material> materialLookup)
        {
            var mesh = new ArrayMesh();
            foreach (var (material, s) in _surfaces)
            {
                if (s.Indices.Count == 0) continue;
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = s.Vertices.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = s.Normals.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs.ToArray();
                arrays[(int)Mesh.ArrayType.Index] = s.Indices.ToArray();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materialLookup(material));
            }
            return mesh;
        }
    }
}
