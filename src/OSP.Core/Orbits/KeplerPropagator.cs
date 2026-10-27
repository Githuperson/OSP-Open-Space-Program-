using OSP.Core.Mathx;

namespace OSP.Core.Orbits;

/// <summary>
/// Two-body ("on rails") propagation using the universal-variable formulation with
/// Laguerre-Conway iteration (Battin / Vallado). It is exact for the Kepler problem for
/// elliptic, parabolic and hyperbolic orbits with one code path, so time warp of any size
/// gives a result independent of step count.
///
/// Universal Kepler equation (chi = universal anomaly, z = alpha chi^2, alpha = 1/a):
///   sqrt(mu) dt = (r0.v0 / sqrt(mu)) chi^2 C(z) + (1 - alpha r0) chi^3 S(z) + r0 chi
/// Lagrange coefficients: f = 1 - chi^2 C(z)/r0,  g = dt - chi^3 S(z)/sqrt(mu).
/// </summary>
public static class KeplerPropagator
{
    public static (Vec3d r, Vec3d v) Propagate(Vec3d r0, Vec3d v0, double mu, double dt)
    {
        if (dt == 0) return (r0, v0);
        double r0m = r0.Length;
        double v02 = v0.LengthSquared;
        double alpha = 2.0 / r0m - v02 / mu;          // 1/a (positive: ellipse, ~0: parabola, negative: hyperbola)

        // For closed orbits remove whole periods: avoids precision loss at huge dt and is exact.
        if (alpha > 1e-12)
        {
            double period = MathUtil.Tau / Math.Sqrt(mu * alpha * alpha * alpha);
            dt %= period;
            if (dt == 0) return (r0, v0);
        }

        double sqrtMu = Math.Sqrt(mu);
        double rv = Vec3d.Dot(r0, v0);
        double chi = InitialGuess(r0m, rv, mu, alpha, dt);

        for (int i = 0; i < 50; i++)
        {
            double chi2 = chi * chi;
            double z = alpha * chi2;
            double c = Stumpff.C(z), s = Stumpff.S(z);
            double F = rv / sqrtMu * chi2 * c + (1 - alpha * r0m) * chi2 * chi * s + r0m * chi - sqrtMu * dt;
            double dF = rv / sqrtMu * chi * (1 - z * s) + (1 - alpha * r0m) * chi2 * c + r0m;
            double ddF = rv / sqrtMu * (1 - z * c) + (1 - alpha * r0m) * chi * (1 - z * s);

            // Laguerre-Conway step (n = 5); robust where plain Newton can overshoot.
            double disc = 16.0 * dF * dF - 20.0 * F * ddF;
            double delta;
            if (disc < 0 || Math.Abs(dF) < 1e-300)
                delta = dF != 0 ? F / dF : 0;
            else
                delta = 5.0 * F / (dF + Math.Sign(dF) * Math.Sqrt(disc));
            chi -= delta;
            if (Math.Abs(delta) < 1e-10 * Math.Max(1.0, Math.Abs(chi))) break;
        }

        double x2 = chi * chi;
        double zf = alpha * x2;
        double C = Stumpff.C(zf), S = Stumpff.S(zf);
        double f = 1 - x2 / r0m * C;
        double g = dt - x2 * chi / sqrtMu * S;
        var r = r0 * f + v0 * g;
        double rm = r.Length;
        double fdot = sqrtMu / (rm * r0m) * chi * (zf * S - 1);
        double gdot = 1 - x2 / rm * C;
        var v = r0 * fdot + v0 * gdot;
        return (r, v);
    }

    private static double InitialGuess(double r0, double rv, double mu, double alpha, double dt)
    {
        double sqrtMu = Math.Sqrt(mu);
        if (alpha > 1e-9) return sqrtMu * dt * alpha;                      // elliptic
        if (Math.Abs(alpha) <= 1e-9)                                       // parabolic (Barker-like guess)
        {
            double hmag2 = 0; // unused; use simple guess
            _ = hmag2;
            return sqrtMu * dt / r0;
        }
        // hyperbolic
        double a = 1.0 / alpha;
        double sign = Math.Sign(dt);
        double arg = (-2.0 * mu * alpha * dt) / (rv + sign * Math.Sqrt(-mu * a) * (1 - r0 * alpha));
        if (!(arg > 0) || !double.IsFinite(arg)) return sign * Math.Sqrt(-a) * 1.0;
        return sign * Math.Sqrt(-a) * Math.Log(arg);
    }
}
