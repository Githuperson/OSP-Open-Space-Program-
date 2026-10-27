namespace OSP.Core.Math
{
    /// <summary>Row-major 3x3 double matrix, used for inertia tensors (kg m^2).</summary>
    public readonly struct Matrix3d
    {
        public readonly double M00, M01, M02;
        public readonly double M10, M11, M12;
        public readonly double M20, M21, M22;

        public Matrix3d(double m00, double m01, double m02,
                        double m10, double m11, double m12,
                        double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static readonly Matrix3d Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        public static readonly Matrix3d Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static Matrix3d Diagonal(double a, double b, double c) => new(a, 0, 0, 0, b, 0, 0, 0, c);

        public static Matrix3d FromQuaternion(QuaternionD q)
        {
            Vector3d x = q.Rotate(Vector3d.UnitX);
            Vector3d y = q.Rotate(Vector3d.UnitY);
            Vector3d z = q.Rotate(Vector3d.UnitZ);
            return new Matrix3d(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
        }

        public Matrix3d Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public static Matrix3d operator +(Matrix3d a, Matrix3d b) => new(
            a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
            a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
            a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

        public static Matrix3d operator *(Matrix3d a, double s) => new(
            a.M00 * s, a.M01 * s, a.M02 * s,
            a.M10 * s, a.M11 * s, a.M12 * s,
            a.M20 * s, a.M21 * s, a.M22 * s);

        public static Matrix3d operator *(Matrix3d a, Matrix3d b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public static Vector3d operator *(Matrix3d m, Vector3d v) => new(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        public double Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public Matrix3d Inverse()
        {
            double det = Determinant;
            if (System.Math.Abs(det) < 1e-300) return Zero;
            double inv = 1.0 / det;
            return new Matrix3d(
                (M11 * M22 - M12 * M21) * inv, (M02 * M21 - M01 * M22) * inv, (M01 * M12 - M02 * M11) * inv,
                (M12 * M20 - M10 * M22) * inv, (M00 * M22 - M02 * M20) * inv, (M02 * M10 - M00 * M12) * inv,
                (M10 * M21 - M11 * M20) * inv, (M01 * M20 - M00 * M21) * inv, (M00 * M11 - M01 * M10) * inv);
        }

        /// <summary>Parallel-axis term m*(|r|^2 I - r r^T) for moving an inertia tensor by offset r.</summary>
        public static Matrix3d ParallelAxis(double mass, Vector3d r)
        {
            double r2 = r.LengthSquared;
            return new Matrix3d(
                mass * (r2 - r.X * r.X), -mass * r.X * r.Y, -mass * r.X * r.Z,
                -mass * r.Y * r.X, mass * (r2 - r.Y * r.Y), -mass * r.Y * r.Z,
                -mass * r.Z * r.X, -mass * r.Z * r.Y, mass * (r2 - r.Z * r.Z));
        }
    }
}
