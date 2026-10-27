namespace OSP.Core.Orbits;

/// <summary>Stumpff functions C(z) and S(z) used by the universal-variable Kepler solver.</summary>
public static class Stumpff
{
    public static double C(double z)
    {
        if (Math.Abs(z) < 1e-6)
            return 0.5 - z / 24.0 + z * z / 720.0 - z * z * z / 40320.0;
        if (z > 0) return (1.0 - Math.Cos(Math.Sqrt(z))) / z;
        return (Math.Cosh(Math.Sqrt(-z)) - 1.0) / (-z);
    }

    public static double S(double z)
    {
        if (Math.Abs(z) < 1e-6)
            return 1.0 / 6.0 - z / 120.0 + z * z / 5040.0 - z * z * z / 362880.0;
        if (z > 0)
        {
            double sz = Math.Sqrt(z);
            return (sz - Math.Sin(sz)) / (sz * sz * sz);
        }
        double s = Math.Sqrt(-z);
        return (Math.Sinh(s) - s) / (s * s * s);
    }
}
