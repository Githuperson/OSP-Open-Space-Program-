namespace OSP.Core.Mathx;

/// <summary>Row-major 3x3 double matrix, used for inertia tensors.</summary>
public readonly struct Mat3d
{
    public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public Mat3d(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22;
    }

    public static readonly Mat3d Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public static readonly Mat3d Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Mat3d Diagonal(double x, double y, double z) => new(x, 0, 0, 0, y, 0, 0, 0, z);

    public static Mat3d FromQuat(QuatD q)
    {
        var x = q.Rotate(Vec3d.UnitX); var y = q.Rotate(Vec3d.UnitY); var z = q.Rotate(Vec3d.UnitZ);
        return new Mat3d(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
    }

    public Mat3d Transposed => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    public static Mat3d operator +(Mat3d a, Mat3d b) => new(
        a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
        a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
        a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

    public static Mat3d operator *(Mat3d a, double s) => new(
        a.M00 * s, a.M01 * s, a.M02 * s, a.M10 * s, a.M11 * s, a.M12 * s, a.M20 * s, a.M21 * s, a.M22 * s);

    public static Mat3d operator *(Mat3d a, Mat3d b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public static Vec3d operator *(Mat3d m, Vec3d v) => new(
        m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
        m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
        m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

    public double Determinant =>
        M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

    /// <summary>Matrix inverse; returns Identity scaled huge-safe fallback if singular.</summary>
    public Mat3d Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-30) return Identity;
        double id = 1.0 / det;
        return new Mat3d(
            (M11 * M22 - M12 * M21) * id, (M02 * M21 - M01 * M22) * id, (M01 * M12 - M02 * M11) * id,
            (M12 * M20 - M10 * M22) * id, (M00 * M22 - M02 * M20) * id, (M02 * M10 - M00 * M12) * id,
            (M10 * M21 - M11 * M20) * id, (M01 * M20 - M00 * M21) * id, (M00 * M11 - M01 * M10) * id);
    }

    /// <summary>Inertia tensor of a point mass m at offset r from the reference point (parallel-axis term).</summary>
    public static Mat3d PointMassInertia(double m, Vec3d r)
    {
        double r2 = r.LengthSquared;
        return new Mat3d(
            m * (r2 - r.X * r.X), -m * r.X * r.Y, -m * r.X * r.Z,
            -m * r.Y * r.X, m * (r2 - r.Y * r.Y), -m * r.Y * r.Z,
            -m * r.Z * r.X, -m * r.Z * r.Y, m * (r2 - r.Z * r.Z));
    }
}
