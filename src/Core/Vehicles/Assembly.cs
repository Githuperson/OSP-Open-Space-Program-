using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core.Math;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    public readonly struct AttachResult
    {
        public readonly bool Ok;
        public readonly string Reason;
        private AttachResult(bool ok, string reason)
        {
            Ok = ok;
            Reason = reason;
        }
        public static AttachResult Success => new(true, null);
        public static AttachResult Fail(string reason) => new(false, reason);
    }

    public readonly struct Placement
    {
        public readonly Vector3d Position;
        public readonly QuaternionD Rotation;
        public Placement(Vector3d position, QuaternionD rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    public readonly struct PartRayHit
    {
        public readonly PartInstance Part;
        public readonly Vector3d Point;
        public readonly Vector3d Normal;
        public readonly double Distance;
        public PartRayHit(PartInstance part, Vector3d point, Vector3d normal, double distance)
        {
            Part = part;
            Point = point;
            Normal = normal;
            Distance = distance;
        }
    }

    /// <summary>
    /// Geometry and rules for connecting parts. Rules (see docs/GAMEPLAY.md):
    ///  * Stack nodes connect face-to-face; each node holds at most one connection.
    ///  * Node sizes may differ by at most one size class.
    ///  * Surface attachment requires the child to define a surface-attach point and the parent to allow it.
    ///  * A new part may not deeply intersect any other part (oriented bounding-box test, 15% tolerance).
    /// </summary>
    public static class Assembly
    {
        public const double OverlapTolerance = 0.15;

        /// <summary>Placement that joins <paramref name="childNode"/> of a new part to <paramref name="parentNode"/>.</summary>
        public static Placement StackPlacement(PartInstance parent, AttachNodeDef parentNode, PartDefinition child, AttachNodeDef childNode, double spinRadians)
        {
            Vector3d targetDir = parent.DirectionToVessel(parentNode.Direction).Normalized();
            Vector3d targetPos = parent.LocalToVessel(parentNode.Position);
            // Child's node direction must point opposite the parent's node direction.
            QuaternionD align = QuaternionD.FromToRotation(childNode.Direction, -targetDir);
            // Keep the child's "up" roughly consistent with the parent's to avoid arbitrary twist.
            QuaternionD spin = QuaternionD.FromAxisAngle(targetDir, spinRadians);
            QuaternionD rot = (spin * TwistToMatch(align, parent.Rotation, targetDir)).Normalized();
            Vector3d pos = targetPos - rot.Rotate(childNode.Position);
            return new Placement(pos, rot);
        }

        /// <summary>Rotates an aligned orientation about <paramref name="axis"/> so its local X matches the reference's X as closely as possible.</summary>
        private static QuaternionD TwistToMatch(QuaternionD rot, QuaternionD reference, Vector3d axis)
        {
            Vector3d refX = reference.Rotate(Vector3d.UnitX).ProjectOnPlane(axis);
            Vector3d curX = rot.Rotate(Vector3d.UnitX).ProjectOnPlane(axis);
            if (refX.LengthSquared < 1e-9 || curX.LengthSquared < 1e-9)
            {
                refX = reference.Rotate(Vector3d.UnitZ).ProjectOnPlane(axis);
                curX = rot.Rotate(Vector3d.UnitZ).ProjectOnPlane(axis);
                if (refX.LengthSquared < 1e-9 || curX.LengthSquared < 1e-9) return rot;
            }
            double angle = System.Math.Atan2(Vector3d.Dot(Vector3d.Cross(curX, refX), axis.Normalized()), Vector3d.Dot(curX.Normalized(), refX.Normalized()));
            // Snap to multiples of 90° relative to the reference so symmetric parts line up.
            angle = System.Math.Round(angle / (System.Math.PI / 2)) * (System.Math.PI / 2);
            return QuaternionD.FromAxisAngle(axis, angle) * rot;
        }

        /// <summary>
        /// Placement for surface attachment at a hit point/normal on a parent. The part's surface-attach direction
        /// is aligned against the normal; its local +Y follows the vessel's +Y projected on the surface.
        /// </summary>
        public static Placement SurfacePlacement(PartDefinition child, Vector3d hitPoint, Vector3d hitNormal, double spinRadians)
        {
            SurfaceAttachDef sa = child.SurfaceAttach;
            Vector3d n = hitNormal.Normalized();
            Vector3d attachDir = sa.Direction.Normalized();
            // Build a frame: local attachDir -> -n, local +Y -> vessel up projected onto the surface plane.
            Vector3d up = Vector3d.UnitY.ProjectOnPlane(n);
            if (up.LengthSquared < 1e-6) up = Vector3d.UnitZ.ProjectOnPlane(n);
            up = up.Normalized();
            Vector3d localUp = Vector3d.UnitY.ProjectOnPlane(attachDir);
            if (localUp.LengthSquared < 1e-6) localUp = Vector3d.UnitZ.ProjectOnPlane(attachDir);
            localUp = localUp.Normalized();
            QuaternionD localFrame = QuaternionD.FromBasis(attachDir, localUp, Vector3d.Cross(attachDir, localUp));
            QuaternionD worldFrame = QuaternionD.FromBasis(-n, up, Vector3d.Cross(-n, up));
            QuaternionD rot = worldFrame * localFrame.Inverse();
            rot = (QuaternionD.FromAxisAngle(n, spinRadians) * rot).Normalized();
            Vector3d pos = hitPoint - rot.Rotate(sa.Position);
            return new Placement(pos, rot);
        }

        /// <summary>Copies of a placement rotated about the parent's axis for radial symmetry. Index 0 is the original.</summary>
        public static List<Placement> SymmetryPlacements(PartInstance parent, Placement original, int count)
        {
            var list = new List<Placement> { original };
            if (count <= 1 || parent == null) return list;
            Vector3d axis = parent.DirectionToVessel(Vector3d.UnitY).Normalized();
            Vector3d center = parent.Position;
            for (int i = 1; i < count; i++)
            {
                QuaternionD q = QuaternionD.FromAxisAngle(axis, i * MathUtil.TwoPi / count);
                Vector3d pos = center + q.Rotate(original.Position - center);
                list.Add(new Placement(pos, (q * original.Rotation).Normalized()));
            }
            return list;
        }

        public static AttachResult ValidateStack(VesselDesign design, PartInstance parent, string parentNodeId, PartDefinition child, string childNodeId)
        {
            AttachNodeDef pn = parent.Def?.GetNode(parentNodeId);
            AttachNodeDef cn = child.GetNode(childNodeId);
            if (pn == null) return AttachResult.Fail($"{parent.Def?.Name} has no node '{parentNodeId}'.");
            if (cn == null) return AttachResult.Fail($"{child.Name} has no node '{childNodeId}'.");
            if (!design.IsNodeFree(parent, parentNodeId)) return AttachResult.Fail("That attachment node is already occupied.");
            if (System.Math.Abs(pn.Size - cn.Size) > 1) return AttachResult.Fail($"Node sizes are incompatible ({SizeName(pn.Size)} vs {SizeName(cn.Size)}).");
            return AttachResult.Success;
        }

        public static AttachResult ValidateSurface(PartInstance parent, PartDefinition child)
        {
            if (child.SurfaceAttach == null) return AttachResult.Fail($"{child.Name} cannot be surface-attached.");
            if (parent.Def != null && !parent.Def.AllowSurfaceAttachOnThis) return AttachResult.Fail($"Parts cannot be mounted on the surface of {parent.Def.Name}.");
            return AttachResult.Success;
        }

        /// <summary>Checks that a part at the given placement does not deeply intersect other parts (excluding <paramref name="ignore"/>).</summary>
        public static AttachResult ValidateOverlap(VesselDesign design, PartDefinition def, Placement placement, ICollection<int> ignore)
        {
            Obb a = Obb.FromPart(def, placement.Position, placement.Rotation, OverlapTolerance);
            foreach (PartInstance other in design.Parts)
            {
                if (ignore != null && ignore.Contains(other.Id)) continue;
                if (other.Def == null) continue;
                Obb b = Obb.FromPart(other.Def, other.Position, other.Rotation, OverlapTolerance);
                if (a.Intersects(b)) return AttachResult.Fail($"Overlaps {other.Def.Name}.");
            }
            return AttachResult.Success;
        }

        public static string SizeName(int size) => size switch
        {
            0 => "0.625 m",
            1 => "1.25 m",
            2 => "2.5 m",
            3 => "3.75 m",
            _ => "radial",
        };

        // ----- Ray casting ---------------------------------------------------------------------------

        /// <summary>Closest part hit by a ray in vessel coordinates.</summary>
        public static PartRayHit? Raycast(VesselDesign design, Vector3d origin, Vector3d direction, ICollection<int> ignore = null)
        {
            PartRayHit? best = null;
            Vector3d dir = direction.Normalized();
            foreach (PartInstance p in design.Parts)
            {
                if (p.Def == null || (ignore != null && ignore.Contains(p.Id))) continue;
                PartRayHit? hit = RaycastPart(p, origin, dir);
                if (hit.HasValue && (!best.HasValue || hit.Value.Distance < best.Value.Distance)) best = hit;
            }
            return best;
        }

        public static PartRayHit? RaycastPart(PartInstance p, Vector3d origin, Vector3d dir)
        {
            Vector3d lo = p.VesselToLocal(origin);
            Vector3d ld = p.Rotation.InverseRotate(dir);
            PartBounds b = p.Def.Bounds;
            double t;
            Vector3d ln;
            bool ok = b.IsRound ? RayCylinder(lo, ld, b.Radius, b.Min.Y, b.Max.Y, out t, out ln) : RayBox(lo, ld, b.Min, b.Max, out t, out ln);
            if (!ok) return null;
            Vector3d point = origin + dir * t;
            return new PartRayHit(p, point, p.DirectionToVessel(ln).Normalized(), t);
        }

        public static bool RayCylinder(Vector3d o, Vector3d d, double r, double yMin, double yMax, out double tHit, out Vector3d normal)
        {
            tHit = double.MaxValue;
            normal = Vector3d.Zero;
            // Side.
            double a = d.X * d.X + d.Z * d.Z;
            double bq = 2 * (o.X * d.X + o.Z * d.Z);
            double c = o.X * o.X + o.Z * o.Z - r * r;
            if (a > 1e-12)
            {
                double disc = bq * bq - 4 * a * c;
                if (disc >= 0)
                {
                    double sq = System.Math.Sqrt(disc);
                    foreach (double t in new[] { (-bq - sq) / (2 * a), (-bq + sq) / (2 * a) })
                    {
                        if (t < 0) continue;
                        double y = o.Y + d.Y * t;
                        if (y >= yMin && y <= yMax && t < tHit)
                        {
                            tHit = t;
                            normal = new Vector3d(o.X + d.X * t, 0, o.Z + d.Z * t).Normalized();
                        }
                    }
                }
            }
            // Caps.
            if (System.Math.Abs(d.Y) > 1e-12)
            {
                foreach (double yc in new[] { yMin, yMax })
                {
                    double t = (yc - o.Y) / d.Y;
                    if (t < 0 || t >= tHit) continue;
                    double x = o.X + d.X * t, z = o.Z + d.Z * t;
                    if (x * x + z * z <= r * r)
                    {
                        tHit = t;
                        normal = yc == yMax ? Vector3d.UnitY : -Vector3d.UnitY;
                    }
                }
            }
            return tHit < double.MaxValue;
        }

        public static bool RayBox(Vector3d o, Vector3d d, Vector3d min, Vector3d max, out double tHit, out Vector3d normal)
        {
            double tMin = double.MinValue, tMax = double.MaxValue;
            normal = Vector3d.Zero;
            Vector3d nEnter = Vector3d.Zero;
            double[] oo = { o.X, o.Y, o.Z }, dd = { d.X, d.Y, d.Z }, mn = { min.X, min.Y, min.Z }, mx = { max.X, max.Y, max.Z };
            for (int i = 0; i < 3; i++)
            {
                if (System.Math.Abs(dd[i]) < 1e-12)
                {
                    if (oo[i] < mn[i] || oo[i] > mx[i]) { tHit = 0; return false; }
                    continue;
                }
                double t1 = (mn[i] - oo[i]) / dd[i];
                double t2 = (mx[i] - oo[i]) / dd[i];
                double sign = -1;
                if (t1 > t2) { (t1, t2) = (t2, t1); sign = 1; }
                if (t1 > tMin)
                {
                    tMin = t1;
                    nEnter = new Vector3d(i == 0 ? sign : 0, i == 1 ? sign : 0, i == 2 ? sign : 0);
                }
                tMax = System.Math.Min(tMax, t2);
                if (tMin > tMax) { tHit = 0; return false; }
            }
            if (tMax < 0) { tHit = 0; return false; }
            tHit = tMin >= 0 ? tMin : tMax;
            normal = nEnter;
            return true;
        }
    }

    /// <summary>Oriented bounding box used for overlap tests.</summary>
    public readonly struct Obb
    {
        public readonly Vector3d Center;
        public readonly Vector3d HalfExtents;
        public readonly Vector3d AxisX, AxisY, AxisZ;

        public Obb(Vector3d center, Vector3d halfExtents, QuaternionD rotation)
        {
            Center = center;
            HalfExtents = halfExtents;
            AxisX = rotation.Rotate(Vector3d.UnitX);
            AxisY = rotation.Rotate(Vector3d.UnitY);
            AxisZ = rotation.Rotate(Vector3d.UnitZ);
        }

        public static Obb FromPart(PartDefinition def, Vector3d position, QuaternionD rotation, double shrink)
        {
            PartBounds b = def.Bounds;
            Vector3d half = b.Size * 0.5 * (1 - shrink);
            Vector3d center = position + rotation.Rotate(b.Center);
            return new Obb(center, half, rotation);
        }

        private Vector3d Axis(int i) => i == 0 ? AxisX : (i == 1 ? AxisY : AxisZ);
        private double Half(int i) => i == 0 ? HalfExtents.X : (i == 1 ? HalfExtents.Y : HalfExtents.Z);

        private double ProjectedRadius(Vector3d axis) =>
            HalfExtents.X * System.Math.Abs(Vector3d.Dot(AxisX, axis)) +
            HalfExtents.Y * System.Math.Abs(Vector3d.Dot(AxisY, axis)) +
            HalfExtents.Z * System.Math.Abs(Vector3d.Dot(AxisZ, axis));

        /// <summary>Separating axis test over the 15 candidate axes.</summary>
        public bool Intersects(Obb other)
        {
            Vector3d t = other.Center - Center;
            var axes = new List<Vector3d>(15);
            for (int i = 0; i < 3; i++) axes.Add(Axis(i));
            for (int i = 0; i < 3; i++) axes.Add(other.Axis(i));
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Vector3d c = Vector3d.Cross(Axis(i), other.Axis(j));
                if (c.LengthSquared > 1e-10) axes.Add(c.Normalized());
            }
            foreach (Vector3d axis in axes)
            {
                double dist = System.Math.Abs(Vector3d.Dot(t, axis));
                if (dist > ProjectedRadius(axis) + other.ProjectedRadius(axis)) return false;
            }
            return true;
        }
    }
}
