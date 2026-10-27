using System.Runtime.CompilerServices;

namespace OSP.Core.Mathx;

/// <summary>
/// Double-precision 3-vector. All authoritative simulation state uses this type.
/// Coordinate convention (shared with Godot): right-handed, +Y is "up"/north-pole axis,
/// the ecliptic (orbital plane of the planets) is the XZ plane, reference direction is +X.
/// Units are always SI (metres, seconds, kilograms, newtons).
/// </summary>
public readonly struct Vec3d : IEquatable<Vec3d>
{
    public readonly double X, Y, Z;

    public Vec3d(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3d Zero = new(0, 0, 0);
    public static readonly Vec3d UnitX = new(1, 0, 0);
    public static readonly Vec3d UnitY = new(0, 1, 0);
    public static readonly Vec3d UnitZ = new(0, 0, 1);

    public double Length { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Math.Sqrt(X * X + Y * Y + Z * Z); }
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3d Normalized
    {
        get
        {
            double l = Length;
            return l > 1e-300 ? new Vec3d(X / l, Y / l, Z / l) : Zero;
        }
    }

    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3d Cross(Vec3d a, Vec3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Angle(Vec3d a, Vec3d b)
    {
        double d = Dot(a.Normalized, b.Normalized);
        return Math.Acos(Math.Clamp(d, -1.0, 1.0));
    }

    /// <summary>Component of this vector that is perpendicular to a unit vector.</summary>
    public Vec3d RejectFrom(Vec3d unit) => this - unit * Dot(this, unit);

    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public bool Equals(Vec3d o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object? obj) => obj is Vec3d v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vec3d a, Vec3d b) => a.Equals(b);
    public static bool operator !=(Vec3d a, Vec3d b) => !a.Equals(b);
    public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
}
