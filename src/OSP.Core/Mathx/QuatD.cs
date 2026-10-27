namespace OSP.Core.Mathx;

/// <summary>Double-precision unit quaternion (W + Xi + Yj + Zk). Rotates vectors via <see cref="Rotate"/>.</summary>
public readonly struct QuatD : IEquatable<QuatD>
{
    public readonly double W, X, Y, Z;

    public QuatD(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }

    public static readonly QuatD Identity = new(1, 0, 0, 0);

    public static QuatD FromAxisAngle(Vec3d axis, double angleRad)
    {
        var a = axis.Normalized;
        double h = angleRad * 0.5, s = Math.Sin(h);
        return new QuatD(Math.Cos(h), a.X * s, a.Y * s, a.Z * s);
    }

    /// <summary>Shortest-arc rotation taking unit direction <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static QuatD FromTo(Vec3d from, Vec3d to)
    {
        var f = from.Normalized; var t = to.Normalized;
        double d = Vec3d.Dot(f, t);
        if (d > 1.0 - 1e-14) return Identity;
        if (d < -1.0 + 1e-14)
        {
            // 180 degrees: pick any perpendicular axis.
            var axis = Vec3d.Cross(f, Math.Abs(f.X) < 0.9 ? Vec3d.UnitX : Vec3d.UnitY);
            return FromAxisAngle(axis, Math.PI);
        }
        var c = Vec3d.Cross(f, t);
        return new QuatD(1 + d, c.X, c.Y, c.Z).Normalized;
    }

    /// <summary>Quaternion from an orthonormal right-handed basis (columns = images of the unit axes).</summary>
    public static QuatD FromBasis(Vec3d xAxis, Vec3d yAxis, Vec3d zAxis)
    {
        double m00 = xAxis.X, m01 = yAxis.X, m02 = zAxis.X;
        double m10 = xAxis.Y, m11 = yAxis.Y, m12 = zAxis.Y;
        double m20 = xAxis.Z, m21 = yAxis.Z, m22 = zAxis.Z;
        double tr = m00 + m11 + m22;
        double w, x, y, z;
        if (tr > 0)
        {
            double s = Math.Sqrt(tr + 1.0) * 2;
            w = 0.25 * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
        }
        return new QuatD(w, x, y, z).Normalized;
    }

    public double Length => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);

    public QuatD Normalized
    {
        get
        {
            double l = Length;
            return l > 1e-300 ? new QuatD(W / l, X / l, Y / l, Z / l) : Identity;
        }
    }

    public QuatD Conjugate => new(W, -X, -Y, -Z);

    public static QuatD operator *(QuatD a, QuatD b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    public Vec3d Rotate(Vec3d v)
    {
        // v' = v + 2w(q x v) + 2 q x (q x v)
        var q = new Vec3d(X, Y, Z);
        var t = Vec3d.Cross(q, v) * 2.0;
        return v + t * W + Vec3d.Cross(q, t);
    }

    public Vec3d InverseRotate(Vec3d v) => Conjugate.Rotate(v);

    /// <summary>
    /// Advance orientation by a world-frame angular velocity (rad/s) over dt using the exact
    /// axis-angle increment (no linearisation error for constant omega over the step).
    /// </summary>
    public QuatD IntegrateWorld(Vec3d omegaWorld, double dt)
    {
        double w = omegaWorld.Length;
        if (w * dt < 1e-12) return this;
        var dq = FromAxisAngle(omegaWorld, w * dt);
        return (dq * this).Normalized;
    }

    public bool Equals(QuatD o) => W == o.W && X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object? obj) => obj is QuatD q && Equals(q);
    public override int GetHashCode() => HashCode.Combine(W, X, Y, Z);
    public override string ToString() => $"({W:G6}, {X:G6}, {Y:G6}, {Z:G6})";
}
