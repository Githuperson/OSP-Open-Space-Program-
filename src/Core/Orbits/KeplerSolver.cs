using System;
using OSP.Core.Math;

namespace OSP.Core.Orbits
{
    /// <summary>
    /// Two-body propagation with the universal-variable formulation (Curtis, "Orbital Mechanics for
    /// Engineering Students", Algorithms 3.3/3.4). One code path covers elliptic, parabolic and hyperbolic
    /// trajectories. The Kepler equation in χ is monotonic (dF/dχ = r &gt; 0) so a bracketed, safeguarded
    /// Newton iteration always converges.
    /// </summary>
    public static class KeplerSolver
    {
        /// <summary>Stumpff function C(z).</summary>
        public static double StumpffC(double z)
        {
            if (z > 1e-6)
            {
                double s = System.Math.Sqrt(z);
                return (1.0 - System.Math.Cos(s)) / z;
            }
            if (z < -1e-6)
            {
                double s = System.Math.Sqrt(-z);
                return (System.Math.Cosh(s) - 1.0) / (-z);
            }
            // Series: 1/2 - z/24 + z^2/720 - z^3/40320
            return 0.5 - z / 24.0 + z * z / 720.0 - z * z * z / 40320.0;
        }

        /// <summary>Stumpff function S(z).</summary>
        public static double StumpffS(double z)
        {
            if (z > 1e-6)
            {
                double s = System.Math.Sqrt(z);
                return (s - System.Math.Sin(s)) / (s * s * s);
            }
            if (z < -1e-6)
            {
                double s = System.Math.Sqrt(-z);
                return (System.Math.Sinh(s) - s) / (s * s * s);
            }
            // Series: 1/6 - z/120 + z^2/5040 - z^3/362880
            return 1.0 / 6.0 - z / 120.0 + z * z / 5040.0 - z * z * z / 362880.0;
        }

        /// <summary>
        /// Propagates the state (r0, v0) around a body with gravitational parameter mu by dt seconds.
        /// </summary>
        public static void Propagate(double mu, Vector3d r0, Vector3d v0, double dt, out Vector3d r, out Vector3d v)
        {
            if (dt == 0)
            {
                r = r0;
                v = v0;
                return;
            }

            double r0Len = r0.Length;
            double v0Sq = v0.LengthSquared;
            double vr0 = Vector3d.Dot(r0, v0) / r0Len;
            double alpha = 2.0 / r0Len - v0Sq / mu; // 1/a
            double sqrtMu = System.Math.Sqrt(mu);

            // For bound orbits reduce dt modulo the period so the iteration stays well conditioned.
            if (alpha > 1e-15)
            {
                double a = 1.0 / alpha;
                double period = MathUtil.TwoPi * System.Math.Sqrt(a * a * a / mu);
                if (System.Math.Abs(dt) > period)
                {
                    dt %= period;
                }
            }

            double chi = SolveUniversalAnomaly(mu, r0Len, vr0, alpha, dt);

            double z = alpha * chi * chi;
            double c = StumpffC(z);
            double s = StumpffS(z);
            double chi2 = chi * chi;
            double chi3 = chi2 * chi;

            double f = 1.0 - chi2 / r0Len * c;
            double g = dt - chi3 / sqrtMu * s;
            r = f * r0 + g * v0;
            double rLen = r.Length;
            double fDot = sqrtMu / (rLen * r0Len) * (alpha * chi3 * s - chi);
            double gDot = 1.0 - chi2 / rLen * c;
            v = fDot * r0 + gDot * v0;
        }

        private static double UniversalF(double chi, double r0, double vr0, double alpha, double sqrtMu, double dt)
        {
            double z = alpha * chi * chi;
            double c = StumpffC(z);
            double s = StumpffS(z);
            return r0 * vr0 / sqrtMu * chi * chi * c + (1.0 - alpha * r0) * chi * chi * chi * s + r0 * chi - sqrtMu * dt;
        }

        private static double UniversalDF(double chi, double r0, double vr0, double alpha, double sqrtMu)
        {
            double z = alpha * chi * chi;
            double c = StumpffC(z);
            double s = StumpffS(z);
            return r0 * vr0 / sqrtMu * chi * (1.0 - z * s) + (1.0 - alpha * r0) * chi * chi * c + r0;
        }

        private static double SolveUniversalAnomaly(double mu, double r0, double vr0, double alpha, double dt)
        {
            double sqrtMu = System.Math.Sqrt(mu);

            // Initial guess.
            double chi;
            if (alpha > 1e-15)
            {
                chi = sqrtMu * alpha * dt;
            }
            else if (alpha < -1e-15)
            {
                double a = 1.0 / alpha;
                double sign = System.Math.Sign(dt);
                // Vallado's hyperbolic guess: sign(dt) sqrt(-a) ln(-2 mu alpha dt / (r0.v0 + sign(dt) sqrt(-mu a)(1 - r0 alpha)))
                double arg = -2.0 * mu * alpha * dt /
                             (r0 * vr0 + sign * System.Math.Sqrt(-mu * a) * (1.0 - r0 * alpha));
                // Fall back to a simple guess if the log argument is not usable.
                chi = arg > 0 ? sign * System.Math.Sqrt(-a) * System.Math.Log(arg) : sqrtMu * dt / r0;
            }
            else
            {
                chi = sqrtMu * dt / r0;
            }

            // Bracket the root. F is monotonically increasing in chi.
            double lo, hi;
            double fChi = UniversalF(chi, r0, vr0, alpha, sqrtMu, dt);
            if (!double.IsFinite(fChi))
            {
                chi = sqrtMu * dt / r0;
                fChi = UniversalF(chi, r0, vr0, alpha, sqrtMu, dt);
            }
            double step = System.Math.Max(System.Math.Abs(chi), 1.0);
            if (fChi > 0)
            {
                hi = chi;
                lo = chi - step;
                int guard = 0;
                while (UniversalF(lo, r0, vr0, alpha, sqrtMu, dt) > 0 && guard++ < 200)
                {
                    step *= 2;
                    lo = chi - step;
                }
            }
            else
            {
                lo = chi;
                hi = chi + step;
                int guard = 0;
                while (UniversalF(hi, r0, vr0, alpha, sqrtMu, dt) < 0 && guard++ < 200)
                {
                    step *= 2;
                    hi = chi + step;
                }
            }

            // Safeguarded Newton.
            chi = 0.5 * (lo + hi);
            for (int i = 0; i < 100; i++)
            {
                double fv = UniversalF(chi, r0, vr0, alpha, sqrtMu, dt);
                if (fv > 0) hi = chi; else lo = chi;
                double dfv = UniversalDF(chi, r0, vr0, alpha, sqrtMu);
                double next = chi - fv / dfv;
                if (!(next > lo && next < hi) || !double.IsFinite(next))
                {
                    next = 0.5 * (lo + hi);
                }
                if (System.Math.Abs(next - chi) <= 1e-12 * System.Math.Max(1.0, System.Math.Abs(chi)))
                {
                    return next;
                }
                chi = next;
            }
            return chi;
        }

        /// <summary>Solves Kepler's equation M = E - e sin E for elliptic orbits.</summary>
        public static double SolveEccentricAnomaly(double meanAnomaly, double e)
        {
            double m = MathUtil.WrapPi(meanAnomaly);
            double E = e < 0.8 ? m : System.Math.PI * System.Math.Sign(m);
            for (int i = 0; i < 50; i++)
            {
                double f = E - e * System.Math.Sin(E) - m;
                double fp = 1 - e * System.Math.Cos(E);
                double dE = f / fp;
                E -= dE;
                if (System.Math.Abs(dE) < 1e-14) break;
            }
            return E;
        }

        /// <summary>Solves the hyperbolic Kepler equation M = e sinh H - H.</summary>
        public static double SolveHyperbolicAnomaly(double meanAnomaly, double e)
        {
            double H = System.Math.Asinh(meanAnomaly / e);
            for (int i = 0; i < 100; i++)
            {
                double f = e * System.Math.Sinh(H) - H - meanAnomaly;
                double fp = e * System.Math.Cosh(H) - 1;
                double dH = f / fp;
                H -= dH;
                if (System.Math.Abs(dH) < 1e-13 * System.Math.Max(1, System.Math.Abs(H))) break;
            }
            return H;
        }
    }
}
