using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight.Systems;

/// <summary>
/// Terrain collision using sequential impulses on a set of sample points per part.
/// The terrain height comes from the same procedural function used for rendering.
/// Impact speeds above a part's CrashTolerance destroy that part.
/// </summary>
public static class ContactSystem
{
    public const double Restitution = 0.05;
    public const double HullFriction = 0.8;
    public const double Slop = 0.01;
    private const int MaxActivePoints = 28;
    private const int Iterations = 6;

    private static readonly Dictionary<string, Vec3d[]> PointCache = new();

    public struct ContactResult
    {
        public bool InContact;
        public double MaxPenetration;
        public List<(CraftPart part, double speed)> Crashed;
        public double FirstImpactSpeed;
    }

    private static Vec3d[] LocalPoints(PartDefinition def)
    {
        lock (PointCache)
        {
            if (PointCache.TryGetValue(def.Id, out var c)) return c;
            var pts = new List<Vec3d>();
            var sh = def.Shape;
            if (sh.IsBox)
            {
                double hx = sh.Size[0] / 2, hy = sh.Size[1] / 2, hz = sh.Size[2] / 2;
                foreach (var sx in new[] { -1, 1 }) foreach (var sy in new[] { -1, 1 }) foreach (var sz in new[] { -1, 1 })
                    pts.Add(new Vec3d(sx * hx, sy * hy, sz * hz));
            }
            else
            {
                double hl = sh.Length / 2;
                pts.Add(new Vec3d(0, -hl, 0)); pts.Add(new Vec3d(0, hl, 0));
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI / 2;
                    pts.Add(new Vec3d(Math.Cos(a) * sh.BottomRadius, -hl, Math.Sin(a) * sh.BottomRadius));
                    pts.Add(new Vec3d(Math.Cos(a) * sh.TopRadius, hl, Math.Sin(a) * sh.TopRadius));
                }
            }
            var arr = pts.ToArray();
            PointCache[def.Id] = arr;
            return arr;
        }
    }

    private struct Pt
    {
        public CraftPart Part; public Vec3d R; public Vec3d World; public double Height; public bool Foot;
        public double Pen; public Vec3d N; public double Mu; public double JnAcc; public double InitialVn; public bool Active;
    }

    public static ContactResult Solve(Craft craft, in FlightEnvironment env, double dt, double time, ForceAccumulator acc)
    {
        var res = new ContactResult { Crashed = new List<(CraftPart, double)>() };
        var body = craft.Body;
        var q = craft.Orientation;
        var com = craft.Mass.CenterOfMass;
        double bound = craft.BoundingRadius + 3.0;
        if (env.AltitudeTerrain > bound) { craft.InContact = false; return res; }

        // 1. Candidate points, keep the lowest few.
        var all = new List<Pt>(64);
        var up = env.Up;
        foreach (var part in craft.Parts)
        {
            foreach (var pl in LocalPoints(part.Def))
            {
                var pc = part.Position + part.Rotation.Rotate(pl);
                var r = q.Rotate(pc - com);
                all.Add(new Pt { Part = part, R = r, Height = Vec3d.Dot(r, up), Mu = HullFriction });
            }
            if (part.Def.Leg is { } leg && part.LegDeploy > 0.95)
            {
                var pc = part.Position + part.Rotation.Rotate(leg.FootV);
                var r = q.Rotate(pc - com);
                all.Add(new Pt { Part = part, R = r, Height = Vec3d.Dot(r, up), Mu = leg.Friction, Foot = true });
            }
        }
        all.Sort((a, b) => a.Height.CompareTo(b.Height));
        int n = Math.Min(MaxActivePoints, all.Count);

        var pts = new List<Pt>(n);
        var rotBody = body.RotationAt(time);
        for (int i = 0; i < n; i++)
        {
            var p = all[i];
            p.World = craft.Position + p.R;
            var local = rotBody.InverseRotate(p.World);
            double len = local.Length;
            var dir = local / len;
            double surfR = body.Radius + body.SurfaceHeightAt(dir);
            p.Pen = surfR - len;
            if (p.Pen > 0)
            {
                var nl = body.TerrainModel != null ? body.TerrainModel.SurfaceNormal(dir, 4.0) : dir;
                p.N = rotBody.Rotate(nl);
                p.Active = true;
                pts.Add(p);
            }
        }
        if (pts.Count == 0) { craft.InContact = false; return res; }
        craft.InContact = true;
        res.InContact = true;

        double m = craft.Mass.Mass;
        var Rm = Mat3d.FromQuat(q);
        var invI = Rm * craft.Mass.InertiaInverse * Rm.Transposed;
        var v = craft.Velocity;
        var wW = q.Rotate(craft.AngularVelocity);

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var vpt = v + Vec3d.Cross(wW, p.R) - body.SurfaceVelocityAt(p.World);
            p.InitialVn = Vec3d.Dot(vpt, p.N);
            pts[i] = p;
        }

        bool ocean = body.TerrainModel?.HasOcean == true;
        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var vpt = v + Vec3d.Cross(wW, p.R) - body.SurfaceVelocityAt(p.World);
                double vn = Vec3d.Dot(vpt, p.N);
                var rxn = Vec3d.Cross(p.R, p.N);
                double k = 1.0 / m + Vec3d.Dot(Vec3d.Cross(invI * rxn, p.R), p.N);
                double e = it == 0 && vn < -1.0 ? Restitution : 0.0;
                double jn = -(1 + e) * vn / k;
                double newAcc = Math.Max(0, p.JnAcc + jn);
                jn = newAcc - p.JnAcc;
                p.JnAcc = newAcc;
                v += p.N * (jn / m);
                wW += invI * Vec3d.Cross(p.R, p.N * jn);

                // friction
                vpt = v + Vec3d.Cross(wW, p.R) - body.SurfaceVelocityAt(p.World);
                var vt = vpt - p.N * Vec3d.Dot(vpt, p.N);
                double vtl = vt.Length;
                if (vtl > 1e-6)
                {
                    var tdir = vt / vtl;
                    var rxt = Vec3d.Cross(p.R, tdir);
                    double kt = 1.0 / m + Vec3d.Dot(Vec3d.Cross(invI * rxt, p.R), tdir);
                    double jt = -vtl / kt;
                    double maxJt = p.Mu * p.JnAcc;
                    jt = Math.Clamp(jt, -maxJt, maxJt);
                    v += tdir * (jt / m);
                    wW += invI * Vec3d.Cross(p.R, tdir * jt);
                }
                pts[i] = p;
            }
        }

        // Positional correction along the deepest contact's normal.
        int deepest = 0;
        for (int i = 1; i < pts.Count; i++) if (pts[i].Pen > pts[deepest].Pen) deepest = i;
        res.MaxPenetration = pts[deepest].Pen;
        if (res.MaxPenetration > Slop)
            craft.Position += pts[deepest].N * (res.MaxPenetration - Slop * 0.5);

        craft.Velocity = v;
        craft.AngularVelocity = q.InverseRotate(wW);

        // Crash detection + reaction forces for structural analysis.
        double worst = 0;
        foreach (var p in pts)
        {
            double impact = -p.InitialVn;
            if (impact > worst) worst = impact;
            double tol = p.Part.Def.CrashTolerance * (ocean && p.Part.Def.Leg == null ? 1.0 : 1.0);
            if (p.Foot && p.Part.Def.Leg != null) tol = Math.Max(tol, 14);
            if (ocean && craft.Body.SurfaceHeightAt((rotBody.InverseRotate(p.World)).Normalized) <= 0.001) tol *= 2.5;
            if (impact > tol && !res.Crashed.Any(c => c.part == p.Part)) res.Crashed.Add((p.Part, impact));
            if (p.JnAcc > 0)
                acc.AddContact(p.Part, q.InverseRotate(p.N * (p.JnAcc / dt)), p.Part.Position);
        }
        res.FirstImpactSpeed = worst;
        return res;
    }
}
