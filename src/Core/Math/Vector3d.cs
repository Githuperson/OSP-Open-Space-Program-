using System;
using System.Globalization;

namespace OSP.Core.Math
{
    /// <summary>
    /// Double-precision 3D vector. All authoritative simulation positions, velocities and forces use this type.
    /// Units are whatever the caller uses; the simulation uses SI (m, m/s, N).
    /// </summary>
    public readonly struct Vector3d : IEquatable<Vector3d>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vector3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vector3d Zero = new(0, 0, 0);
        public static readonly Vector3d One = new(1, 1, 1);
        public static readonly Vector3d UnitX = new(1, 0, 0);
        public static readonly Vector3d UnitY = new(0, 1, 0);
        public static readonly Vector3d UnitZ = new(0, 0, 1);

        public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;

        public Vector3d Normalized()
        {
            double l = Length;
            return l > 1e-300 ? new Vector3d(X / l, Y / l, Z / l) : Zero;
        }

        public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3d Cross(Vector3d a, Vector3d b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public double Dot(Vector3d b) => Dot(this, b);
        public Vector3d Cross(Vector3d b) => Cross(this, b);

        public static double Distance(Vector3d a, Vector3d b) => (a - b).Length;

        public static Vector3d Lerp(Vector3d a, Vector3d b, double t) => a + (b - a) * t;

        /// <summary>Angle between two vectors in radians, numerically robust for small angles.</summary>
        public static double Angle(Vector3d a, Vector3d b)
        {
            return System.Math.Atan2(Cross(a, b).Length, Dot(a, b));
        }

        /// <summary>Component of this vector perpendicular to <paramref name="normal"/> (normal need not be unit).</summary>
        public Vector3d ProjectOnPlane(Vector3d normal)
        {
            double n2 = normal.LengthSquared;
            if (n2 < 1e-300) return this;
            return this - normal * (Dot(this, normal) / n2);
        }

        public Vector3d MultiplyComponents(Vector3d b) => new(X * b.X, Y * b.Y, Z * b.Z);

        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator -(Vector3d a) => new(-a.X, -a.Y, -a.Z);
        public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3d operator *(double s, Vector3d a) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3d operator /(Vector3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);
        public static bool operator ==(Vector3d a, Vector3d b) => a.Equals(b);
        public static bool operator !=(Vector3d a, Vector3d b) => !a.Equals(b);

        public bool Equals(Vector3d other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vector3d v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public bool ApproxEquals(Vector3d other, double tolerance) => (this - other).Length <= tolerance;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", X, Y, Z);
    }
}
