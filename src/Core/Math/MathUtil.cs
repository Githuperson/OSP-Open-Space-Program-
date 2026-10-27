namespace OSP.Core.Math
{
    public static class MathUtil
    {
        public const double TwoPi = 2.0 * System.Math.PI;
        public const double Deg2Rad = System.Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / System.Math.PI;

        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        public static double Clamp01(double v) => Clamp(v, 0, 1);
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Wraps an angle to [0, 2π).</summary>
        public static double WrapTwoPi(double a)
        {
            a %= TwoPi;
            return a < 0 ? a + TwoPi : a;
        }

        /// <summary>Wraps an angle to (-π, π].</summary>
        public static double WrapPi(double a)
        {
            a = WrapTwoPi(a);
            return a > System.Math.PI ? a - TwoPi : a;
        }

        public static double MoveTowards(double current, double target, double maxDelta)
        {
            if (System.Math.Abs(target - current) <= maxDelta) return target;
            return current + System.Math.Sign(target - current) * maxDelta;
        }
    }
}
