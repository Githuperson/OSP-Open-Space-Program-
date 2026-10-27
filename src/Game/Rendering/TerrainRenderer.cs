using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Math;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// Level-of-detail terrain for one body in the near (full-scale) world. Six quadtrees (one per cube face) are
    /// refined around the camera: a node splits when the camera is closer than <see cref="SplitFactor"/> × its
    /// size. Patch meshes are generated on worker threads and uploaded a few per frame. Nodes beyond the horizon or
    /// the far plane are hidden, and distant detail is freed, so memory stays bounded regardless of where the
    /// player flies.
    /// </summary>
    public partial class TerrainRenderer : Node3D
    {
        public const int GridSize = 24;
        public const double SplitFactor = 2.6;
        public const int UploadsPerFrame = 6;
        public const int MaxInFlight = 12;

        private sealed class QuadNode
        {
            public int Face;
            public int Level;
            public double U0, V0, Size;
            public QuadNode[] Children;
            public MeshInstance3D Mesh;
            public TerrainPatchData Data;
            public bool Requested;
            public Vector3d CenterDir;
            public double ArcSize; // approximate edge length on the surface (m)
            public bool Visible;
        }

        public CelestialBody Body { get; private set; }
        public int ActiveChunks { get; private set; }
        public int PendingChunks => _inFlight;

        private readonly List<QuadNode> _roots = new();
        private readonly ConcurrentQueue<QuadNode> _ready = new();
        private ShaderMaterial _material;
        private int _inFlight;
        private int _maxLevel;
        private Vector3d _cameraBodyFixed;
        private double _farDistance = 2.5e6;
        private bool _disposed;

        public static TerrainRenderer Create(CelestialBody body)
        {
            var t = new TerrainRenderer { Body = body, Name = "Terrain_" + body.Id };
            t._material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
            double rootArc = body.Radius * System.Math.PI / 2;
            t._maxLevel = (int)System.Math.Ceiling(System.Math.Log(rootArc / 60.0, 2));
            for (int f = 0; f < 6; f++)
                t._roots.Add(t.MakeNode(f, 0, 0, 0, 1));
            return t;
        }

        private QuadNode MakeNode(int face, int level, double u0, double v0, double size) => new()
        {
            Face = face,
            Level = level,
            U0 = u0,
            V0 = v0,
            Size = size,
            CenterDir = TerrainPatch.Direction(face, u0 + size / 2, v0 + size / 2),
            ArcSize = Body.Radius * System.Math.PI / 2 * size,
        };

        /// <summary>
        /// Refines the quadtree for a camera at <paramref name="cameraBodyFixed"/> (body-fixed metres) and positions
        /// visible patches relative to the floating origin. <paramref name="bodyOffset"/> = body centre − origin.
        /// </summary>
        public void UpdateTerrain(Vector3d cameraBodyFixed, Vector3d bodyOffset, QuaternionD bodyRotation, double farDistance)
        {
            _cameraBodyFixed = cameraBodyFixed;
            _farDistance = farDistance;

            while (_ready.TryDequeue(out QuadNode done))
            {
                _inFlight--;
                if (_disposed) continue;
                done.Requested = false;
                if (done.Data == null) continue;
                done.Mesh?.QueueFree();
                done.Mesh = new MeshInstance3D { Mesh = TerrainPatch.ToMesh(done.Data), MaterialOverride = _material, Visible = false };
                done.Mesh.SetInstanceShaderParameter("detail_origin", done.Data.Center.ToGodot());
                AddChild(done.Mesh);
                done.Data.Vertices = null;
            }

            ActiveChunks = 0;
            int budget = UploadsPerFrame;
            foreach (QuadNode r in _roots) Visit(r, ref budget);

            Basis basis = new(bodyRotation.ToGodot());
            foreach (QuadNode r in _roots) Place(r, bodyOffset, bodyRotation, basis);
        }

        private bool BelowHorizon(QuadNode node)
        {
            double camR = _cameraBodyFixed.Length;
            double r = Body.Radius;
            if (camR <= r * 1.0001) return false;
            // Angle from the sub-camera point to the horizon, plus the node's angular radius and terrain allowance.
            double horizon = System.Math.Acos(System.Math.Clamp(r / camR, -1, 1));
            double relief = 8000;
            double horizonExtra = System.Math.Acos(System.Math.Clamp(r / (r + relief), -1, 1));
            double nodeRadius = node.ArcSize * 0.75 / r;
            double angle = Vector3d.Angle(_cameraBodyFixed, node.CenterDir);
            return angle > horizon + horizonExtra + nodeRadius;
        }

        private double DistanceTo(QuadNode node)
        {
            Vector3d p = node.CenterDir * Body.Radius;
            double d = (p - _cameraBodyFixed).Length - node.ArcSize * 0.7;
            return System.Math.Max(d, 0);
        }

        /// <summary>Returns true when this node (or its children) has geometry covering its area.</summary>
        private bool Visit(QuadNode node, ref int budget)
        {
            bool culled = BelowHorizon(node) || DistanceTo(node) > _farDistance;
            if (culled && node.Level > 0)
            {
                Collapse(node);
                node.Visible = false;
                if (node.Mesh != null) node.Mesh.Visible = false;
                return true;
            }

            double dist = DistanceTo(node);
            bool wantSplit = node.Level < _maxLevel && dist < node.ArcSize * SplitFactor && !culled;
            if (wantSplit)
            {
                if (node.Children == null)
                {
                    double h = node.Size / 2;
                    node.Children = new[]
                    {
                        MakeNode(node.Face, node.Level + 1, node.U0, node.V0, h),
                        MakeNode(node.Face, node.Level + 1, node.U0 + h, node.V0, h),
                        MakeNode(node.Face, node.Level + 1, node.U0, node.V0 + h, h),
                        MakeNode(node.Face, node.Level + 1, node.U0 + h, node.V0 + h, h),
                    };
                }
                bool allReady = true;
                foreach (QuadNode c in node.Children) allReady &= Visit(c, ref budget);
                if (allReady)
                {
                    node.Visible = false;
                    if (node.Mesh != null) node.Mesh.Visible = false;
                    return true;
                }
                // Children still generating: keep showing this level, hide children to avoid overlap.
                foreach (QuadNode c in node.Children) HideSubtree(c);
            }
            else
            {
                Collapse(node);
            }

            if (node.Mesh == null)
            {
                RequestMesh(node, ref budget);
                node.Visible = false;
                return false;
            }
            node.Visible = !culled;
            node.Mesh.Visible = node.Visible;
            if (node.Visible) ActiveChunks++;
            return true;
        }

        private void HideSubtree(QuadNode n)
        {
            n.Visible = false;
            if (n.Mesh != null) n.Mesh.Visible = false;
            if (n.Children != null) foreach (QuadNode c in n.Children) HideSubtree(c);
        }

        private void Collapse(QuadNode node)
        {
            if (node.Children == null) return;
            foreach (QuadNode c in node.Children)
            {
                Collapse(c);
                if (c.Mesh != null)
                {
                    c.Mesh.QueueFree();
                    c.Mesh = null;
                }
            }
            // Only drop children whose meshes are not still generating.
            foreach (QuadNode c in node.Children) if (c.Requested) return;
            node.Children = null;
        }

        private void RequestMesh(QuadNode node, ref int budget)
        {
            if (node.Requested || budget <= 0 || _inFlight >= MaxInFlight) return;
            node.Requested = true;
            budget--;
            _inFlight++;
            CelestialBody body = Body;
            Task.Run(() =>
            {
                try
                {
                    node.Data = TerrainPatch.Generate(body, node.Face, node.U0, node.V0, node.Size, GridSize, 1.0, true, true);
                }
                catch (System.Exception ex)
                {
                    GD.PushError($"Terrain generation failed for {body.Id}: {ex}");
                    node.Data = null;
                }
                _ready.Enqueue(node);
            });
        }

        private void Place(QuadNode node, Vector3d bodyOffset, QuaternionD bodyRotation, Basis basis)
        {
            if (node.Mesh != null && node.Mesh.Visible && node.Data != null)
            {
                Vector3d p = bodyOffset + bodyRotation.Rotate(node.Data.Center);
                node.Mesh.Transform = new Transform3D(basis, p.ToGodot());
            }
            if (node.Children != null) foreach (QuadNode c in node.Children) Place(c, bodyOffset, bodyRotation, basis);
        }

        public override void _ExitTree()
        {
            _disposed = true;
        }
    }
}
