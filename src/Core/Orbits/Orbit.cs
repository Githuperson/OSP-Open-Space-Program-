using System;
using OSP.Core.Math;

namespace OSP.Core.Orbits
{
    /// <summary>
    /// A two-body (Keplerian) trajectory around a body with gravitational parameter <see cref="Mu"/>.
    ///
    /// Coordinate convention (shared by the whole simulation): a non-rotating, right-handed frame centred on the
    /// parent body with +Y pointing to the body's north pole (the reference pole). The reference direction
    /// for the longitude of the ascending node is +X. A prograde equatorial orbit therefore has angular
    /// momentum along +Y.
    ///
    /// The orbit is stored as a state vector at an epoch; propagation is done with the universal-variable
    /// Kepler solver, so it is exact (to floating point) for elliptic, parabolic and hyperbolic orbits.
    /// Classical elements are derived for display and for event-time queries.
    /// </summary>
    public sealed class Orbit
    {
        public static readonly Vector3d ReferencePole = Vector3d.UnitY;
        public static readonly Vector3d ReferenceDirection = Vector3d.UnitX;

        private const double CircularThreshold = 1e-9;
        private const double EquatorialThreshold = 1e-9;

        public double Mu { get; }
        public Vector3d EpochPosition { get; }
        public Vector3d EpochVelocity { get; }
        public double Epoch { get; }

        // Derived quantities.
        public Vector3d AngularMomentum { get; }
        public Vector3d EccentricityVector { get; }
        public double Eccentricity { get; }
        public double SpecificEnergy { get; }
        /// <summary>Semi-major axis (m). Negative for hyperbolic orbits, infinite for parabolic.</summary>
        public double SemiMajorAxis { get; }
        /// <summary>Semi-latus rectum p = h²/μ (m).</summary>
        public double SemiLatusRectum { get; }
        public double Inclination { get; }
        public double LongitudeOfAscendingNode { get; }
        public double ArgumentOfPeriapsis { get; }
        public double TrueAnomalyAtEpoch { get; }
        /// <summary>Unit vector towards periapsis (or the epoch position for circular orbits).</summary>
        public Vector3d PeriapsisDirection { get; }
        /// <summary>Unit vector 90° ahead of periapsis in the direction of motion.</summary>
        public Vector3d SemiMinorDirection { get; }
        public Vector3d OrbitNormal { get; }

        public bool IsElliptic => Eccentricity < 1.0 - 1e-10;
        public bool IsHyperbolic => Eccentricity > 1.0 + 1e-10;

        public double PeriapsisRadius => SemiLatusRectum / (1.0 + Eccentricity);
        /// <summary>Apoapsis radius, or +∞ for open trajectories.</summary>
        public double ApoapsisRadius => IsElliptic ? SemiLatusRectum / (1.0 - Eccentricity) : double.PositiveInfinity;

        /// <summary>Orbital period, or +∞ for open trajectories.</summary>
        public double Period => IsElliptic
            ? MathUtil.TwoPi * System.Math.Sqrt(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis / Mu)
            : double.PositiveInfinity;

        /// <summary>Mean motion n (rad/s).</summary>
        public double MeanMotion
        {
            get
            {
                if (IsElliptic) return System.Math.Sqrt(Mu / (SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
                if (IsHyperbolic) return System.Math.Sqrt(Mu / -(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
                return 2.0 * System.Math.Sqrt(Mu / (SemiLatusRectum * SemiLatusRectum * SemiLatusRectum));
            }
        }

        public Orbit(double mu, Vector3d position, Vector3d velocity, double epoch)
        {
            if (mu <= 0) throw new ArgumentOutOfRangeException(nameof(mu), "Gravitational parameter must be positive.");
            if (!position.IsFinite || !velocity.IsFinite) throw new ArgumentException("Orbit state must be finite.");
            if (position.LengthSquared < 1e-12) throw new ArgumentException("Orbit position cannot be at the body centre.");

            Mu = mu;
            EpochPosition = position;
            EpochVelocity = velocity;
            Epoch = epoch;

            double r = position.Length;
            double v2 = velocity.LengthSquared;
            Vector3d h = Vector3d.Cross(position, velocity);
            AngularMomentum = h;
            SpecificEnergy = v2 / 2.0 - mu / r;
            Vector3d eVec = ((v2 - mu / r) * position - Vector3d.Dot(position, velocity) * velocity) / mu;
            EccentricityVector = eVec;
            Eccentricity = eVec.Length;
            SemiLatusRectum = h.LengthSquared / mu;
            SemiMajorAxis = System.Math.Abs(SpecificEnergy) < 1e-12 ? double.PositiveInfinity : -mu / (2.0 * SpecificEnergy);

            double hLen = h.Length;
            if (hLen < 1e-9)
            {
                // Degenerate (radial) trajectory. Build an arbitrary plane containing the radius vector.
                Vector3d any = System.Math.Abs(Vector3d.Dot(position.Normalized(), ReferencePole)) < 0.9 ? ReferencePole : ReferenceDirection;
                OrbitNormal = Vector3d.Cross(position, any).Normalized();
            }
            else
            {
                OrbitNormal = h / hLen;
            }

            Inclination = System.Math.Acos(MathUtil.Clamp(Vector3d.Dot(OrbitNormal, ReferencePole), -1, 1));

            Vector3d node = Vector3d.Cross(ReferencePole, OrbitNormal);
            Vector3d refJ = Vector3d.Cross(ReferencePole, ReferenceDirection); // completes the right-handed frame
            bool equatorial = node.Length < EquatorialThreshold;
            if (equatorial)
            {
                node = ReferenceDirection;
                LongitudeOfAscendingNode = 0;
            }
            else
            {
                node = node.Normalized();
                LongitudeOfAscendingNode = MathUtil.WrapTwoPi(System.Math.Atan2(Vector3d.Dot(node, refJ), Vector3d.Dot(node, ReferenceDirection)));
            }

            Vector3d inPlaneNode = Vector3d.Cross(OrbitNormal, node); // 90° ahead of the node within the plane
            if (Eccentricity > CircularThreshold)
            {
                PeriapsisDirection = eVec / Eccentricity;
                ArgumentOfPeriapsis = MathUtil.WrapTwoPi(System.Math.Atan2(Vector3d.Dot(PeriapsisDirection, inPlaneNode), Vector3d.Dot(PeriapsisDirection, node)));
            }
            else
            {
                // Circular: measure anomalies from the ascending node (or reference direction).
                PeriapsisDirection = node;
                ArgumentOfPeriapsis = 0;
            }
            SemiMinorDirection = Vector3d.Cross(OrbitNormal, PeriapsisDirection).Normalized();
            TrueAnomalyAtEpoch = MathUtil.WrapTwoPi(System.Math.Atan2(Vector3d.Dot(position, SemiMinorDirection), Vector3d.Dot(position, PeriapsisDirection)));
        }

        /// <summary>
        /// Creates an orbit from classical elements. Angles in radians. For hyperbolic orbits pass a negative
        /// semi-major axis. Mean anomaly is at <paramref name="epoch"/>.
        /// </summary>
        public static Orbit FromElements(double mu, double semiMajorAxis, double eccentricity, double inclination,
            double lan, double argPe, double meanAnomalyAtEpoch, double epoch)
        {
            double nu;
            if (eccentricity < 1.0)
            {
                double E = KeplerSolver.SolveEccentricAnomaly(meanAnomalyAtEpoch, eccentricity);
                nu = 2.0 * System.Math.Atan2(System.Math.Sqrt(1 + eccentricity) * System.Math.Sin(E / 2), System.Math.Sqrt(1 - eccentricity) * System.Math.Cos(E / 2));
            }
            else
            {
                double H = KeplerSolver.SolveHyperbolicAnomaly(meanAnomalyAtEpoch, eccentricity);
                nu = 2.0 * System.Math.Atan(System.Math.Sqrt((eccentricity + 1) / (eccentricity - 1)) * System.Math.Tanh(H / 2));
            }
            double p = semiMajorAxis * (1 - eccentricity * eccentricity);
            BuildPerifocalBasis(inclination, lan, argPe, out Vector3d P, out Vector3d Q);
            double r = p / (1 + eccentricity * System.Math.Cos(nu));
            Vector3d pos = r * (System.Math.Cos(nu) * P + System.Math.Sin(nu) * Q);
            double k = System.Math.Sqrt(mu / p);
            Vector3d vel = k * (-System.Math.Sin(nu) * P + (eccentricity + System.Math.Cos(nu)) * Q);
            return new Orbit(mu, pos, vel, epoch);
        }

        /// <summary>Circular orbit of the given radius, inclination and LAN, starting at the ascending node.</summary>
        public static Orbit Circular(double mu, double radius, double inclination, double lan, double epoch, double phase = 0)
        {
            return FromElements(mu, radius, 0, inclination, lan, 0, phase, epoch);
        }

        private static void BuildPerifocalBasis(double inc, double lan, double argPe, out Vector3d P, out Vector3d Q)
        {
            // Work in a Z-up frame (a = X, b = -Z, c = Y) and map back.
            double cO = System.Math.Cos(lan), sO = System.Math.Sin(lan);
            double ci = System.Math.Cos(inc), si = System.Math.Sin(inc);
            double cw = System.Math.Cos(argPe), sw = System.Math.Sin(argPe);
            double pA = cO * cw - sO * sw * ci;
            double pB = sO * cw + cO * sw * ci;
            double pC = sw * si;
            double qA = -cO * sw - sO * cw * ci;
            double qB = -sO * sw + cO * cw * ci;
            double qC = cw * si;
            P = FromZUp(pA, pB, pC);
            Q = FromZUp(qA, qB, qC);
        }

        private static Vector3d FromZUp(double a, double b, double c)
        {
            Vector3d j = Vector3d.Cross(ReferencePole, ReferenceDirection);
            return a * ReferenceDirection + b * j + c * ReferencePole;
        }

        /// <summary>Position and velocity at universal time <paramref name="time"/>.</summary>
        public void GetStateAt(double time, out Vector3d position, out Vector3d velocity)
        {
            KeplerSolver.Propagate(Mu, EpochPosition, EpochVelocity, time - Epoch, out position, out velocity);
        }

        public Vector3d PositionAt(double time)
        {
            GetStateAt(time, out Vector3d p, out _);
            return p;
        }

        public Vector3d VelocityAt(double time)
        {
            GetStateAt(time, out _, out Vector3d v);
            return v;
        }

        /// <summary>Radius at a given true anomaly.</summary>
        public double RadiusAtTrueAnomaly(double nu) => SemiLatusRectum / (1.0 + Eccentricity * System.Math.Cos(nu));

        public Vector3d PositionAtTrueAnomaly(double nu)
        {
            double r = RadiusAtTrueAnomaly(nu);
            return r * (System.Math.Cos(nu) * PeriapsisDirection + System.Math.Sin(nu) * SemiMinorDirection);
        }

        public double TrueAnomalyOfPosition(Vector3d position) =>
            MathUtil.WrapTwoPi(System.Math.Atan2(Vector3d.Dot(position, SemiMinorDirection), Vector3d.Dot(position, PeriapsisDirection)));

        public double TrueAnomalyAt(double time) => TrueAnomalyOfPosition(PositionAt(time));

        /// <summary>
        /// Time since periapsis passage for a given true anomaly. For elliptic orbits the result is in
        /// [0, period); for open orbits it is negative before periapsis.
        /// </summary>
        public double TimeSincePeriapsis(double nu)
        {
            double e = Eccentricity;
            if (IsElliptic)
            {
                double E = 2.0 * System.Math.Atan2(System.Math.Sqrt(1 - e) * System.Math.Sin(nu / 2), System.Math.Sqrt(1 + e) * System.Math.Cos(nu / 2));
                double M = MathUtil.WrapTwoPi(E - e * System.Math.Sin(E));
                return M / MeanMotion;
            }
            double nuW = MathUtil.WrapPi(nu);
            if (IsHyperbolic)
            {
                double t = System.Math.Tan(nuW / 2) * System.Math.Sqrt((e - 1) / (e + 1));
                if (t >= 1) t = 1 - 1e-15;
                if (t <= -1) t = -1 + 1e-15;
                double H = 2.0 * System.Math.Atanh(t);
                double M = e * System.Math.Sinh(H) - H;
                return M / MeanMotion;
            }
            // Parabolic: Barker's equation.
            double D = System.Math.Tan(nuW / 2);
            return 0.5 * System.Math.Sqrt(SemiLatusRectum * SemiLatusRectum * SemiLatusRectum / Mu) * (D + D * D * D / 3.0);
        }

        /// <summary>Universal time of the periapsis passage closest at or after epoch for open orbits, or the last one for ellipses.</summary>
        public double PeriapsisTimeReference => Epoch - TimeSincePeriapsis(TrueAnomalyAtEpoch);

        /// <summary>
        /// The next universal time strictly after <paramref name="fromTime"/> at which the true anomaly equals
        /// <paramref name="nu"/>, or NaN if never reached (open orbits beyond the asymptote, or in the past).
        /// </summary>
        public double NextTimeAtTrueAnomaly(double nu, double fromTime)
        {
            if (!IsElliptic)
            {
                double nuW = MathUtil.WrapPi(nu);
                double limit = MaxTrueAnomalyOpen();
                if (System.Math.Abs(nuW) >= limit) return double.NaN;
                double t = PeriapsisTimeReference + TimeSincePeriapsis(nuW);
                return t > fromTime ? t : double.NaN;
            }
            double period = Period;
            double tp = PeriapsisTimeReference;
            double target = tp + TimeSincePeriapsis(nu);
            double k = System.Math.Floor((fromTime - target) / period) + 1;
            double result = target + k * period;
            if (result <= fromTime) result += period;
            return result;
        }

        /// <summary>Asymptotic true anomaly limit for open orbits (rad).</summary>
        public double MaxTrueAnomalyOpen()
        {
            if (IsElliptic) return System.Math.PI;
            if (!IsHyperbolic) return System.Math.PI;
            return System.Math.Acos(-1.0 / Eccentricity);
        }

        public double TimeToPeriapsis(double now)
        {
            double t = NextTimeAtTrueAnomaly(0, now);
            return double.IsNaN(t) ? double.NaN : t - now;
        }

        public double TimeToApoapsis(double now)
        {
            if (!IsElliptic) return double.NaN;
            return NextTimeAtTrueAnomaly(System.Math.PI, now) - now;
        }

        /// <summary>
        /// Next time after <paramref name="fromTime"/> at which the orbit crosses <paramref name="radius"/>.
        /// <paramref name="outbound"/> selects the ascending (r increasing) crossing. NaN if never.
        /// </summary>
        public double NextTimeAtRadius(double radius, double fromTime, bool outbound)
        {
            if (radius < PeriapsisRadius || radius > ApoapsisRadius) return double.NaN;
            double e = Eccentricity;
            if (e < 1e-12) return double.NaN;
            double cosNu = (SemiLatusRectum / radius - 1.0) / e;
            cosNu = MathUtil.Clamp(cosNu, -1, 1);
            double nu = System.Math.Acos(cosNu);
            if (!outbound) nu = -nu;
            return NextTimeAtTrueAnomaly(nu, fromTime);
        }

        /// <summary>Returns a new orbit with an impulsive velocity change applied at time t.</summary>
        public Orbit WithImpulse(double time, Vector3d deltaV)
        {
            GetStateAt(time, out Vector3d p, out Vector3d v);
            return new Orbit(Mu, p, v + deltaV, time);
        }

        /// <summary>Re-epochs the orbit at a new time (same trajectory, fresh state vector; limits round-off growth).</summary>
        public Orbit Rebased(double time)
        {
            GetStateAt(time, out Vector3d p, out Vector3d v);
            return new Orbit(Mu, p, v, time);
        }

        public static double CircularVelocity(double mu, double radius) => System.Math.Sqrt(mu / radius);
        public static double EscapeVelocity(double mu, double radius) => System.Math.Sqrt(2.0 * mu / radius);
        public static double PeriodForSemiMajorAxis(double mu, double a) => MathUtil.TwoPi * System.Math.Sqrt(a * a * a / mu);

        /// <summary>Vis-viva: speed at radius r on an orbit with semi-major axis a.</summary>
        public static double VisViva(double mu, double r, double a) => System.Math.Sqrt(mu * (2.0 / r - 1.0 / a));
    }
}
