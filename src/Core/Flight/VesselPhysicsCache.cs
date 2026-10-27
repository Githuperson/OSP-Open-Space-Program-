using System.Collections.Generic;
using OSP.Core.Math;
using OSP.Core.Parts;
using OSP.Core.Vehicles;

namespace OSP.Core.Flight
{
    /// <summary>Per-vessel data derived from the part tree, rebuilt when the topology changes.</summary>
    internal sealed class VesselPhysicsCache
    {
        public sealed class PartCache
        {
            public PartInstance Part;
            public int Index;
            public int ParentIndex = -1;
            /// <summary>Hull contact points in part-local coordinates.</summary>
            public Vector3d[] ContactPoints;
            public bool[] ContactLastStep;
            public bool FootContactLastStep;
            /// <summary>Fraction of the top/bottom face area exposed to the airflow (0..1).</summary>
            public double TopExposure;
            public double BottomExposure;
            public double FaceArea;
            public double SideArea;
            public Vector3d BoxAreas;
            public int OverloadSteps;
        }

        public readonly List<PartCache> Parts = new();
        /// <summary>Indices in post-order (children before parents) for joint-load accumulation.</summary>
        public readonly List<int> PostOrder = new();

        public VesselPhysicsCache(Vessel v)
        {
            var index = new Dictionary<int, int>();
            for (int i = 0; i < v.Parts.Count; i++)
            {
                PartInstance p = v.Parts[i];
                index[p.Id] = i;
                Vector3d[] contacts = ContactPointsFor(p.Def);
                Parts.Add(new PartCache
                {
                    Part = p,
                    Index = i,
                    ContactPoints = contacts,
                    ContactLastStep = new bool[contacts.Length],
                });
            }
            foreach (PartCache pc in Parts)
                if (pc.Part.Parent >= 0 && index.TryGetValue(pc.Part.Parent, out int pi)) pc.ParentIndex = pi;

            // Aerodynamic exposure of stack faces.
            foreach (PartCache pc in Parts)
            {
                PartDefinition def = pc.Part.Def;
                PartBounds b = def.Bounds;
                double r = System.Math.Max(b.Radius, 0.02);
                pc.FaceArea = System.Math.PI * r * r;
                pc.SideArea = 2 * r * System.Math.Max(b.Height, 0.02);
                Vector3d s = b.Size;
                pc.BoxAreas = new Vector3d(s.Y * s.Z, s.X * s.Z, s.X * s.Y);
                pc.TopExposure = Exposure(v, pc.Part, true);
                pc.BottomExposure = Exposure(v, pc.Part, false);
            }

            // Post-order traversal.
            var children = new List<int>[Parts.Count];
            for (int i = 0; i < Parts.Count; i++) children[i] = new List<int>();
            var roots = new List<int>();
            foreach (PartCache pc in Parts)
            {
                if (pc.ParentIndex >= 0) children[pc.ParentIndex].Add(pc.Index);
                else roots.Add(pc.Index);
            }
            var stack = new Stack<(int idx, bool expanded)>();
            foreach (int r0 in roots) stack.Push((r0, false));
            while (stack.Count > 0)
            {
                var (idx, expanded) = stack.Pop();
                if (expanded)
                {
                    PostOrder.Add(idx);
                    continue;
                }
                stack.Push((idx, true));
                foreach (int c in children[idx]) stack.Push((c, false));
            }
        }

        /// <summary>How much of a part's top (or bottom) face is exposed, based on what is attached to the node on that side.</summary>
        private static double Exposure(Vessel v, PartInstance p, bool top)
        {
            double r = System.Math.Max(p.Def.Bounds.Radius, 0.02);
            double faceY = top ? p.Def.Bounds.Max.Y : p.Def.Bounds.Min.Y;
            double covered = 0;
            foreach (PartInstance other in Neighbours(v, p))
            {
                if (other.IsSurfaceAttached || p.IsSurfaceAttached && other.Id == p.Parent) continue;
                // Is the neighbour on the requested side?
                Vector3d rel = p.VesselToLocal(other.LocalToVessel(other.Def.Bounds.Center));
                if (top ? rel.Y <= faceY - 0.05 : rel.Y >= faceY + 0.05) continue;
                double ro = other.Def.Bounds.Radius;
                covered = System.Math.Max(covered, System.Math.Min(1, (ro * ro) / (r * r)));
            }
            return 1 - covered;
        }

        private static IEnumerable<PartInstance> Neighbours(Vessel v, PartInstance p)
        {
            if (p.Parent >= 0 && v.GetPart(p.Parent) is { } parent) yield return parent;
            foreach (PartInstance c in v.ChildrenOf(p.Id)) yield return c;
        }

        private static readonly Dictionary<string, Vector3d[]> ContactCache = new();

        /// <summary>Hull contact points: rims of the bounding cylinder for round parts, box corners otherwise.</summary>
        public static Vector3d[] ContactPointsFor(PartDefinition def)
        {
            lock (ContactCache)
            {
                if (ContactCache.TryGetValue(def.Id, out var pts)) return pts;
                PartBounds b = def.Bounds;
                var list = new List<Vector3d>();
                if (b.IsRound)
                {
                    double r = b.Radius * 0.98;
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * System.Math.PI / 4;
                        double x = r * System.Math.Cos(a), z = r * System.Math.Sin(a);
                        list.Add(new Vector3d(x, b.Min.Y, z));
                        list.Add(new Vector3d(x, b.Max.Y, z));
                    }
                    list.Add(new Vector3d(0, b.Min.Y, 0));
                    list.Add(new Vector3d(0, b.Max.Y, 0));
                }
                else
                {
                    for (int i = 0; i < 8; i++)
                        list.Add(new Vector3d((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
                }
                pts = list.ToArray();
                ContactCache[def.Id] = pts;
                return pts;
            }
        }
    }
}
