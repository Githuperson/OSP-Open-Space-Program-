using OSP.Core.Mathx;

namespace OSP.Core.Orbits;

/// <summary>
/// Classical Keplerian orbit about a point mass with gravitational parameter Mu.
/// Frame: game inertial frame (Y = north/spin axis, reference direction +X, ecliptic = XZ plane).
/// Internally the element conversions are performed in the textbook frame
/// (X = reference, Y, Z = north) and mapped with <see cref="ToGame"/>/<see cref="ToStd"/>.
/// Angles are radians, distances metres, times seconds. SemiMajorAxis is negative for hyperbolae.
/// </summary>
public sealed class Orbit
{
    public double Mu { get; }
    public double SemiMajorAxis { get; }
    public double Eccentricity { get; }
    public double Inclination { get; }
    public double LongitudeOfAscendingNode { get; }
    public double ArgumentOfPeriapsis { get; }
    /// <summary>Mean anomaly (elliptic) or hyperbolic mean anomaly at <see cref="Epoch"/>.</summary>
    public double MeanAnomalyAtEpoch { get; }
    public double Epoch { get; }

    public Orbit(double mu, double a, double e, double inc, double lan, double argPe, double meanAnomalyAtEpoch, double epoch)
    {
        Mu = mu; SemiMajorAxis = a; Eccentricity = e; Inclination = inc;
        LongitudeOfAscendingNode = lan; ArgumentOfPeriapsis = argPe;
        MeanAnomalyAtEpoch = meanAnomalyAtEpoch; Epoch = epoch;
    }

    // ---- frame mapping: game (x, y=north, z) <-> textbook (x, y, z=north) -------------------
    public static Vec3d ToGame(Vec3d s) => new(s.X, s.Z, -s.Y);
    public static Vec3d ToStd(Vec3d g) => new(g.X, -g.Z, g.Y);

    // ---- derived quantities ---------------------------------------------------------------
    public bool IsElliptic => Eccentricity < 1.0;
    public bool IsHyperbolic => Eccentricity > 1.0;
    public double SemiLatusRectum => SemiMajorAxis * (1 - Eccentricity * Eccentricity);
    public double PeriapsisRadius => SemiMajorAxis * (1 - Eccentricity);
    /// <summary>Apoapsis radius; +infinity when the orbit is not closed.</summary>
    public double ApoapsisRadius => IsElliptic ? SemiMajorAxis * (1 + Eccentricity) : double.PositiveInfinity;
    public double MeanMotion => Math.Sqrt(Mu / Math.Abs(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
    public double Period => IsElliptic ? MathUtil.Tau / MeanMotion : double.PositiveInfinity;
    public double SpecificEnergy => -Mu / (2.0 * SemiMajorAxis);

    // ---- construction from a state vector ---------------------------------------------------
    public static Orbit FromState(Vec3d rGame, Vec3d vGame, double mu, double epoch)
    {
        var r = ToStd(rGame); var v = ToStd(vGame);
        double rm = r.Length, v2 = v.LengthSquared;
        var h = Vec3d.Cross(r, v);
        double hm = h.Length;
        var eVec = Vec3d.Cross(v, h) / mu - r / rm;
        double e = eVec.Length;
        double energy = v2 / 2 - mu / rm;
        // A parabola (energy == 0) has no finite semi-major axis; nudge it onto a very slightly
        // bound orbit so every derived quantity stays finite. Propagation never uses this path.
        double minEnergy = 1e-12 * mu / rm;
        if (Math.Abs(energy) < minEnergy) energy = -minEnergy;
        double a = -mu / (2 * energy);

        double inc = Math.Acos(Math.Clamp(h.Z / hm, -1, 1));
        var nVec = new Vec3d(-h.Y, h.X, 0);   // k x h
        double nm = nVec.Length;
        const double eps = 1e-10;
        bool equatorial = nm < eps * hm;
        bool circular = e < eps;

        double lan, argPe, nu;
        lan = equatorial ? 0 : Math.Atan2(nVec.Y, nVec.X);
        if (circular)
        {
            argPe = 0;
            if (equatorial) nu = Math.Atan2(r.Y, r.X) * (h.Z >= 0 ? 1 : -1);
            else
            {
                nu = Math.Acos(Math.Clamp(Vec3d.Dot(nVec, r) / (nm * rm), -1, 1));
                if (r.Z < 0) nu = MathUtil.Tau - nu;
            }
        }
        else
        {
            if (equatorial)
            {
                argPe = Math.Atan2(eVec.Y, eVec.X);
                if (h.Z < 0) argPe = MathUtil.Tau - argPe;
            }
            else
            {
                argPe = Math.Acos(Math.Clamp(Vec3d.Dot(nVec, eVec) / (nm * e), -1, 1));
                if (eVec.Z < 0) argPe = MathUtil.Tau - argPe;
            }
            nu = Math.Acos(Math.Clamp(Vec3d.Dot(eVec, r) / (e * rm), -1, 1));
            if (Vec3d.Dot(r, v) < 0) nu = MathUtil.Tau - nu;
        }
        argPe = MathUtil.WrapTwoPi(argPe); lan = MathUtil.WrapTwoPi(lan); nu = MathUtil.WrapTwoPi(nu);
        double m0 = MeanFromTrue(nu, e);
        return new Orbit(mu, a, e, inc, lan, argPe, m0, epoch);
    }

    // ---- anomaly conversions ----------------------------------------------------------------
    /// <summary>True anomaly (0..2pi) -> mean anomaly (elliptic) or hyperbolic mean anomaly (signed).</summary>
    public static double MeanFromTrue(double nu, double e)
    {
        if (e < 1.0)
        {
            double E = 2 * Math.Atan2(Math.Sqrt(1 - e) * Math.Sin(nu / 2), Math.Sqrt(1 + e) * Math.Cos(nu / 2));
            return MathUtil.WrapTwoPi(E - e * Math.Sin(E));
        }
        double nuS = MathUtil.WrapPi(nu);
        double H = 2 * Math.Atanh(Math.Sqrt((e - 1) / (e + 1)) * Math.Tan(nuS / 2));
        return e * Math.Sinh(H) - H;
    }

    public static double TrueFromMean(double M, double e)
    {
        if (e < 1.0)
        {
            double E = SolveKepler(M, e);
            double nu = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(E / 2), Math.Sqrt(1 - e) * Math.Cos(E / 2));
            return MathUtil.WrapTwoPi(nu);
        }
        double H = SolveKeplerHyperbolic(M, e);
        return 2 * Math.Atan(Math.Sqrt((e + 1) / (e - 1)) * Math.Tanh(H / 2));
    }

    /// <summary>Solve M = E - e sin E for E (Newton with a safe starting guess, converges for 0 &lt;= e &lt; 1).</summary>
    public static double SolveKepler(double M, double e)
    {
        M = MathUtil.WrapTwoPi(M);
        double E = e < 0.8 ? M : Math.PI;
        for (int i = 0; i < 60; i++)
        {
            double f = E - e * Math.Sin(E) - M;
            double d = 1 - e * Math.Cos(E);
            double dE = f / d;
            E -= dE;
            if (Math.Abs(dE) < 1e-14) break;
        }
        return E;
    }

    public static double SolveKeplerHyperbolic(double M, double e)
    {
        double H = Math.Asinh(M / e);
        if (!double.IsFinite(H)) H = 0;
        for (int i = 0; i < 80; i++)
        {
            double f = e * Math.Sinh(H) - H - M;
            double d = e * Math.Cosh(H) - 1;
            double dH = f / d;
            H -= dH;
            if (Math.Abs(dH) < 1e-14) break;
        }
        return H;
    }

    // ---- evaluation -------------------------------------------------------------------------
    public double MeanAnomalyAt(double t)
    {
        double M = MeanAnomalyAtEpoch + MeanMotion * (t - Epoch);
        return IsElliptic ? MathUtil.WrapTwoPi(M) : M;
    }

    public double TrueAnomalyAt(double t) => TrueFromMean(MeanAnomalyAt(t), Eccentricity);

    public (Vec3d r, Vec3d v) StateAt(double t) => StateAtTrueAnomaly(TrueAnomalyAt(t));

    public (Vec3d r, Vec3d v) StateAtTrueAnomaly(double nu)
    {
        double e = Eccentricity, p = SemiLatusRectum;
        double rm = p / (1 + e * Math.Cos(nu));
        var rPf = new Vec3d(rm * Math.Cos(nu), rm * Math.Sin(nu), 0);
        double k = Math.Sqrt(Mu / p);
        var vPf = new Vec3d(-k * Math.Sin(nu), k * (e + Math.Cos(nu)), 0);
        return (ToGame(PerifocalToStd(rPf)), ToGame(PerifocalToStd(vPf)));
    }

    private Vec3d PerifocalToStd(Vec3d pf)
    {
        double cO = Math.Cos(LongitudeOfAscendingNode), sO = Math.Sin(LongitudeOfAscendingNode);
        double ci = Math.Cos(Inclination), si = Math.Sin(Inclination);
        double cw = Math.Cos(ArgumentOfPeriapsis), sw = Math.Sin(ArgumentOfPeriapsis);
        // R = Rz(LAN) Rx(inc) Rz(argPe)
        double x1 = cw * pf.X - sw * pf.Y, y1 = sw * pf.X + cw * pf.Y, z1 = pf.Z;
        double x2 = x1, y2 = ci * y1 - si * z1, z2 = si * y1 + ci * z1;
        return new Vec3d(cO * x2 - sO * y2, sO * x2 + cO * y2, z2);
    }

    /// <summary>Radius at a given true anomaly: r = p / (1 + e cos nu).</summary>
    public double RadiusAtTrueAnomaly(double nu) => SemiLatusRectum / (1 + Eccentricity * Math.Cos(nu));

    /// <summary>Seconds until the next periapsis passage (NaN for a hyperbola already past periapsis).</summary>
    public double TimeToPeriapsis(double t)
    {
        double M = MeanAnomalyAt(t);
        if (IsElliptic) return MathUtil.WrapTwoPi(MathUtil.Tau - M) / MeanMotion % Period;
        return M < 0 ? -M / MeanMotion : double.NaN;
    }

    /// <summary>Seconds until the next apoapsis passage (NaN when the orbit is open).</summary>
    public double TimeToApoapsis(double t)
    {
        if (!IsElliptic) return double.NaN;
        double M = MeanAnomalyAt(t);
        return MathUtil.WrapTwoPi(Math.PI - M) / MeanMotion;
    }

    /// <summary>Time from <paramref name="t"/> until the orbit next reaches true anomaly <paramref name="nu"/>.</summary>
    public double TimeToTrueAnomaly(double t, double nu)
    {
        double Mt = MeanFromTrue(MathUtil.WrapTwoPi(nu), Eccentricity);
        double M = MeanAnomalyAt(t);
        if (IsElliptic) return MathUtil.WrapTwoPi(Mt - M) / MeanMotion;
        double dt = (Mt - M) / MeanMotion;
        return dt >= 0 ? dt : double.NaN;
    }

    /// <summary>Unit orbit normal (r x v direction) in the game frame.</summary>
    public Vec3d NormalAxis
    {
        get
        {
            var (r, v) = StateAt(Epoch);
            return Vec3d.Cross(r, v).Normalized;
        }
    }

    /// <summary>Same orbit re-expressed at another epoch (state continues unchanged).</summary>
    public Orbit WithEpoch(double newEpoch) =>
        new(Mu, SemiMajorAxis, Eccentricity, Inclination, LongitudeOfAscendingNode, ArgumentOfPeriapsis, MeanAnomalyAt(newEpoch), newEpoch);
}
