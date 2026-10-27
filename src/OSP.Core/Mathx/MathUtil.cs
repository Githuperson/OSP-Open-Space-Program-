namespace OSP.Core.Mathx;

public static class MathUtil
{
    public const double Tau = Math.PI * 2.0;
    public const double Deg2Rad = Math.PI / 180.0;
    public const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>Standard gravity used to convert specific impulse (s) to exhaust velocity (m/s).</summary>
    public const double G0 = 9.80665;

    public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }

    public static double WrapTwoPi(double a)
    {
        a %= Tau;
        return a < 0 ? a + Tau : a;
    }

    public static double WrapPi(double a)
    {
        a = WrapTwoPi(a);
        return a > Math.PI ? a - Tau : a;
    }
}
