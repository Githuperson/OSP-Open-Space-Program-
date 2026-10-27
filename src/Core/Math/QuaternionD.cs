using System;

namespace OSP.Core.Math
{
    /// <summary>
    /// Double-precision unit quaternion representing a rotation. Rotate(v) maps a vector from the local
    /// (body) frame into the parent (world) frame.
    /// </summary>
    public readonly struct QuaternionD
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double W;

        public QuaternionD(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly QuaternionD Identity = new(0, 0, 0, 1);

        public static QuaternionD FromAxisAngle(Vector3d axis, double angle)
        {
            Vector3d n = axis.Normalized();
            double s = System.Math.Sin(angle * 0.5);
            return new QuaternionD(n.X * s, n.Y * s, n.Z * s, System.Math.Cos(angle * 0.5));
        }

        /// <summary>Shortest rotation taking unit vector <paramref name="from"/> onto <paramref name="to"/>.</summary>
        public static QuaternionD FromToRotation(Vector3d from, Vector3d to)
        {
            Vector3d f = from.Normalized();
            Vector3d t = to.Normalized();
            double d = Vector3d.Dot(f, t);
            if (d > 1.0 - 1e-12) return Identity;
            if (d < -1.0 + 1e-12)
            {
                Vector3d axis = Vector3d.Cross(Vector3d.UnitX, f);
                if (axis.LengthSquared < 1e-12) axis = Vector3d.Cross(Vector3d.UnitZ, f);
                return FromAxisAngle(axis, System.Math.PI);
            }
            Vector3d c = Vector3d.Cross(f, t);
            return new QuaternionD(c.X, c.Y, c.Z, 1.0 + d).Normalized();
        }

        /// <summary>
        /// Rotation whose local +Y axis points along <paramref name="up"/> and local -Z axis points as close as
        /// possible to <paramref name="forward"/>. Mirrors the Godot convention (forward = -Z).
        /// </summary>
        public static QuaternionD LookRotation(Vector3d forward, Vector3d up)
        {
            Vector3d zAxis = (-forward).Normalized();
            Vector3d xAxis = Vector3d.Cross(up, zAxis).Normalized();
            if (xAxis.LengthSquared < 1e-12)
            {
                xAxis = Vector3d.Cross(System.Math.Abs(zAxis.Y) < 0.9 ? Vector3d.UnitY : Vector3d.UnitX, zAxis).Normalized();
            }
            Vector3d yAxis = Vector3d.Cross(zAxis, xAxis);
            return FromBasis(xAxis, yAxis, zAxis);
        }

        /// <summary>Builds a quaternion from an orthonormal basis (columns are the rotated local axes).</summary>
        public static QuaternionD FromBasis(Vector3d x, Vector3d y, Vector3d z)
        {
            double m00 = x.X, m01 = y.X, m02 = z.X;
            double m10 = x.Y, m11 = y.Y, m12 = z.Y;
            double m20 = x.Z, m21 = y.Z, m22 = z.Z;
            double trace = m00 + m11 + m22;
            double qx, qy, qz, qw;
            if (trace > 0)
            {
                double s = System.Math.Sqrt(trace + 1.0) * 2;
                qw = 0.25 * s;
                qx = (m21 - m12) / s;
                qy = (m02 - m20) / s;
                qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                qw = (m21 - m12) / s;
                qx = 0.25 * s;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = 0.25 * s;
                qz = (m12 + m21) / s;
            }
            else
            {
                double s = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = 0.25 * s;
            }
            return new QuaternionD(qx, qy, qz, qw).Normalized();
        }

        public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);

        public QuaternionD Normalized()
        {
            double l = Length;
            if (l < 1e-300) return Identity;
            return new QuaternionD(X / l, Y / l, Z / l, W / l);
        }

        public QuaternionD Conjugate() => new(-X, -Y, -Z, W);
        public QuaternionD Inverse() => Conjugate();

        public static QuaternionD operator *(QuaternionD a, QuaternionD b) =>
            new(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public Vector3d Rotate(Vector3d v)
        {
            // v' = v + 2w(q x v) + 2 q x (q x v)
            Vector3d q = new(X, Y, Z);
            Vector3d t = 2.0 * Vector3d.Cross(q, v);
            return v + W * t + Vector3d.Cross(q, t);
        }

        public Vector3d InverseRotate(Vector3d v) => Conjugate().Rotate(v);

        public static Vector3d operator *(QuaternionD q, Vector3d v) => q.Rotate(v);

        /// <summary>Integrates an angular velocity (world frame, rad/s) over dt.</summary>
        public QuaternionD Integrate(Vector3d angularVelocityWorld, double dt)
        {
            double angle = angularVelocityWorld.Length * dt;
            if (angle < 1e-15) return this;
            QuaternionD dq = FromAxisAngle(angularVelocityWorld, angle);
            return (dq * this).Normalized();
        }

        public static QuaternionD Slerp(QuaternionD a, QuaternionD b, double t)
        {
            double cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (cos < 0)
            {
                b = new QuaternionD(-b.X, -b.Y, -b.Z, -b.W);
                cos = -cos;
            }
            if (cos > 0.9995)
            {
                return new QuaternionD(
                    a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized();
            }
            double theta = System.Math.Acos(cos);
            double sinTheta = System.Math.Sin(theta);
            double wa = System.Math.Sin((1 - t) * theta) / sinTheta;
            double wb = System.Math.Sin(t * theta) / sinTheta;
            return new QuaternionD(
                a.X * wa + b.X * wb, a.Y * wa + b.Y * wb,
                a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
        }

        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);

        public override string ToString() => $"({X:G5}, {Y:G5}, {Z:G5}, {W:G5})";
    }
}
