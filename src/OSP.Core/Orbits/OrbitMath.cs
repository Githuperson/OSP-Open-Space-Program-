using OSP.Core.Mathx;

namespace OSP.Core.Orbits;

/// <summary>Closed-form orbital-mechanics helpers (all SI).</summary>
public static class OrbitMath
{
    /// <summary>Newtonian gravitational acceleration vector at position r (relative to the attracting centre).</summary>
    public static Vec3d GravityAcceleration(double mu, Vec3d r)
    {
        double d2 = r.LengthSquared;
        double d = Math.Sqrt(d2);
        return r * (-mu / (d2 * d));
    }

    /// <summary>Speed of a circular orbit at radius r: v = sqrt(mu / r).</summary>
    public static double CircularSpeed(double mu, double r) => Math.Sqrt(mu / r);

    /// <summary>Escape speed at radius r: v = sqrt(2 mu / r).</summary>
    public static double EscapeSpeed(double mu, double r) => Math.Sqrt(2.0 * mu / r);

    /// <summary>Vis-viva equation: v^2 = mu (2/r - 1/a).</summary>
    public static double VisViva(double mu, double r, double a) => Math.Sqrt(Math.Max(0, mu * (2.0 / r - 1.0 / a)));

    /// <summary>Orbital period T = 2 pi sqrt(a^3 / mu) (elliptic orbits).</summary>
    public static double Period(double mu, double a) => MathUtil.Tau * Math.Sqrt(a * a * a / mu);

    /// <summary>Laplace sphere-of-influence radius: r_soi = a (m/M)^(2/5).</summary>
    public static double SphereOfInfluence(double semiMajorAxis, double muBody, double muParent) =>
        semiMajorAxis * Math.Pow(muBody / muParent, 0.4);

    /// <summary>Hohmann transfer delta-v pair (burn1, burn2) between coplanar circular orbits r1 -> r2.</summary>
    public static (double dv1, double dv2) HohmannDeltaV(double mu, double r1, double r2)
    {
        double a = 0.5 * (r1 + r2);
        double v1 = Math.Sqrt(mu / r1), v2 = Math.Sqrt(mu / r2);
        double vt1 = VisViva(mu, r1, a), vt2 = VisViva(mu, r2, a);
        return (vt1 - v1, v2 - vt2);
    }

    /// <summary>Tsiolkovsky rocket equation: dv = ve ln(m0/m1).</summary>
    public static double Tsiolkovsky(double isp, double m0, double m1) =>
        m1 <= 0 || m0 <= m1 ? 0 : isp * MathUtil.G0 * Math.Log(m0 / m1);
}
