using System.Collections.Generic;
using OSP.Core.Celestial;
using OSP.Core.Math;

namespace OSP.Core.Orbits
{
    public enum PatchEnd
    {
        /// <summary>Prediction horizon reached.</summary>
        None,
        /// <summary>Leaves the body's sphere of influence into the parent's.</summary>
        Escape,
        /// <summary>Enters a child body's sphere of influence.</summary>
        Encounter,
        /// <summary>Hits the surface (datum radius) of the body.</summary>
        Impact,
        /// <summary>A manoeuvre node changes the orbit.</summary>
        Maneuver,
    }

    /// <summary>A segment of a predicted trajectory around one body.</summary>
    public sealed class OrbitPatch
    {
        public Orbit Orbit;
        public CelestialBody Body;
        public double StartTime;
        public double EndTime;
        public PatchEnd End;
        /// <summary>Body entered or returned to at the end of the patch.</summary>
        public CelestialBody NextBody;
        /// <summary>Time the trajectory enters the atmosphere during this patch, or NaN.</summary>
        public double AtmosphereEntryTime = double.NaN;
        /// <summary>True for patches after a planned manoeuvre.</summary>
        public bool IsPlanned;
    }

    /// <summary>
    /// Patched-conic approximation (docs/PHYSICS.md): a vessel feels only the gravity of the body whose sphere of
    /// influence (SOI, Laplace radius a·(m/M)^0.4) it is in. At an SOI boundary the state is re-expressed relative
    /// to the new body using the bodies' exact Keplerian positions, and a new conic begins.
    /// </summary>
    public static class PatchedConics
    {
        public const int MaxSamplesPerChild = 1500;

        public readonly struct Transition
        {
            public readonly double Time;
            public readonly CelestialBody NewBody;
            public readonly PatchEnd Kind;
            public Transition(double time, CelestialBody newBody, PatchEnd kind)
            {
                Time = time;
                NewBody = newBody;
                Kind = kind;
            }
        }

        /// <summary>Earliest SOI transition of an orbit around <paramref name="body"/> in (t0, t1], or null.</summary>
        public static Transition? FindTransition(Orbit orbit, CelestialBody body, double t0, double t1)
        {
            Transition? best = null;

            if (body.Parent != null && (!orbit.IsElliptic || orbit.ApoapsisRadius > body.SphereOfInfluence))
            {
                double tEsc = orbit.NextTimeAtRadius(body.SphereOfInfluence, t0, true);
                if (!double.IsNaN(tEsc) && tEsc <= t1) best = new Transition(tEsc, body.Parent, PatchEnd.Escape);
            }

            double end = best?.Time ?? t1;
            foreach (CelestialBody child in body.Children)
            {
                double? hit = FindEncounter(orbit, child, t0, end);
                if (hit.HasValue && hit.Value < end)
                {
                    end = hit.Value;
                    best = new Transition(hit.Value, child, PatchEnd.Encounter);
                }
            }
            return best;
        }

        /// <summary>First time in (t0, t1] at which the orbit enters the child's SOI (sampled, then bisected).</summary>
        public static double? FindEncounter(Orbit orbit, CelestialBody child, double t0, double t1)
        {
            if (t1 <= t0) return null;
            double soi = child.SphereOfInfluence;
            // Skip if the orbits can never come close: radial ranges don't overlap.
            double childMin = child.Orbit.PeriapsisRadius - soi, childMax = child.Orbit.ApoapsisRadius + soi;
            if (orbit.PeriapsisRadius > childMax) return null;
            if (orbit.IsElliptic && orbit.ApoapsisRadius < childMin) return null;

            // Step so the relative motion per sample is a fraction of the SOI radius.
            double a = orbit.SemiMajorAxis;
            double vMax = System.Math.Sqrt(orbit.Mu * System.Math.Abs(2.0 / orbit.PeriapsisRadius - (double.IsInfinity(a) ? 0 : 1.0 / a)));
            vMax += child.Orbit.VelocityAt(t0).Length;
            double step = soi * 0.3 / System.Math.Max(vMax, 1);
            int n = (int)System.Math.Ceiling((t1 - t0) / step);
            n = System.Math.Clamp(n, 2, MaxSamplesPerChild);
            double dt = (t1 - t0) / n;

            double Dist(double t) => (orbit.PositionAt(t) - child.PositionRelativeToParent(t)).Length - soi;

            double prevT = t0, prev = Dist(t0);
            if (prev < 0) return null; // already inside (should be in the child's frame)
            for (int i = 1; i <= n; i++)
            {
                double t = t0 + i * dt;
                double d = Dist(t);
                if (d < 0)
                {
                    double lo = prevT, hi = t;
                    for (int k = 0; k < 60; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Dist(mid) < 0) hi = mid; else lo = mid;
                        if (hi - lo < 1e-3) break;
                    }
                    return hi;
                }
                prevT = t;
                prev = d;
            }
            return null;
        }

        /// <summary>Re-expresses a state from one body's frame into another (parent or child).</summary>
        public static void Transform(CelestialBody from, CelestialBody to, double time, Vector3d r, Vector3d v, out Vector3d r2, out Vector3d v2)
        {
            Vector3d offsetP = from.AbsolutePosition(time) - to.AbsolutePosition(time);
            Vector3d offsetV = from.AbsoluteVelocity(time) - to.AbsoluteVelocity(time);
            r2 = r + offsetP;
            v2 = v + offsetV;
        }

        /// <summary>
        /// Predicts a sequence of conic patches from an initial state, applying manoeuvre nodes as impulses.
        /// </summary>
        public static List<OrbitPatch> Predict(CelestialBody body, Vector3d r, Vector3d v, double time,
            IReadOnlyList<Flight.ManeuverNode> nodes = null, int maxPatches = 5, double maxHorizon = 4e7)
        {
            var patches = new List<OrbitPatch>();
            int nodeIndex = 0;
            bool planned = false;
            double horizonEnd = time + maxHorizon;
            for (int i = 0; i < maxPatches && time < horizonEnd; i++)
            {
                if (r.LengthSquared < 1) break;
                var orbit = new Orbit(body.Mu, r, v, time);
                double windowEnd;
                if (orbit.IsElliptic && orbit.ApoapsisRadius < body.SphereOfInfluence)
                    windowEnd = time + System.Math.Min(orbit.Period, maxHorizon);
                else
                    windowEnd = time + System.Math.Min(maxHorizon, EscapeWindow(orbit, body, time));
                windowEnd = System.Math.Min(windowEnd, horizonEnd);

                var patch = new OrbitPatch { Orbit = orbit, Body = body, StartTime = time, EndTime = windowEnd, End = PatchEnd.None, IsPlanned = planned };

                // Manoeuvre node inside this window?
                double nodeTime = double.NaN;
                if (nodes != null && nodeIndex < nodes.Count && nodes[nodeIndex].Time > time && nodes[nodeIndex].Time <= windowEnd)
                    nodeTime = nodes[nodeIndex].Time;

                double searchEnd = double.IsNaN(nodeTime) ? windowEnd : nodeTime;
                Transition? tr = FindTransition(orbit, body, time, searchEnd);

                // Impact with the datum surface.
                double impact = double.NaN;
                if (orbit.PeriapsisRadius < body.Radius)
                {
                    impact = orbit.NextTimeAtRadius(body.Radius, time, false);
                    if (orbit.EpochPosition.Length <= body.Radius + 1) impact = double.NaN;
                }
                if (body.Atmosphere != null && orbit.PeriapsisRadius < body.Radius + body.Atmosphere.Height && r.Length > body.Radius + body.Atmosphere.Height)
                    patch.AtmosphereEntryTime = orbit.NextTimeAtRadius(body.Radius + body.Atmosphere.Height, time, false);

                if (!double.IsNaN(impact) && impact <= searchEnd && (!tr.HasValue || impact < tr.Value.Time))
                {
                    patch.EndTime = impact;
                    patch.End = PatchEnd.Impact;
                    patches.Add(patch);
                    break;
                }
                if (tr.HasValue)
                {
                    patch.EndTime = tr.Value.Time;
                    patch.End = tr.Value.Kind;
                    patch.NextBody = tr.Value.NewBody;
                    patches.Add(patch);
                    orbit.GetStateAt(tr.Value.Time, out Vector3d rr, out Vector3d vv);
                    Transform(body, tr.Value.NewBody, tr.Value.Time, rr, vv, out r, out v);
                    body = tr.Value.NewBody;
                    time = tr.Value.Time;
                    continue;
                }
                if (!double.IsNaN(nodeTime))
                {
                    patch.EndTime = nodeTime;
                    patch.End = PatchEnd.Maneuver;
                    patches.Add(patch);
                    orbit.GetStateAt(nodeTime, out Vector3d rr, out Vector3d vv);
                    v = vv + nodes[nodeIndex].WorldDeltaV(rr, vv);
                    r = rr;
                    time = nodeTime;
                    nodeIndex++;
                    planned = true;
                    continue;
                }
                patches.Add(patch);
                break;
            }
            return patches;
        }

        private static double EscapeWindow(Orbit orbit, CelestialBody body, double time)
        {
            if (body.Parent == null) return orbit.IsElliptic ? orbit.Period : 1e8;
            double t = orbit.NextTimeAtRadius(body.SphereOfInfluence, time, true);
            return double.IsNaN(t) ? 1e7 : (t - time) + 1;
        }
    }
}
