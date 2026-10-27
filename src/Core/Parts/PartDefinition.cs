using System;
using System.Collections.Generic;
using System.Linq;
using OSP.Core.Math;

namespace OSP.Core.Parts
{
    public enum PartCategory
    {
        Command,
        FuelTank,
        Engine,
        Coupling,
        Structural,
        Aerodynamic,
        Control,
        Ground,
        Electrical,
        Utility,
        Science,
        Communication,
    }

    /// <summary>A stack attachment node. Position and direction are in part-local coordinates (m).</summary>
    public sealed class AttachNodeDef
    {
        public string Id { get; init; }
        public Vector3d Position { get; init; }
        /// <summary>Outward direction of the node (e.g. +Y for a top node).</summary>
        public Vector3d Direction { get; init; } = Vector3d.UnitY;
        /// <summary>Diameter class: 0 = 0.625 m, 1 = 1.25 m, 2 = 2.5 m.</summary>
        public int Size { get; init; } = 1;
    }

    /// <summary>Where this part touches a surface when attached radially.</summary>
    public sealed class SurfaceAttachDef
    {
        public Vector3d Position { get; init; }
        /// <summary>Direction from the part towards the surface it is mounted on (part-local).</summary>
        public Vector3d Direction { get; init; } = -Vector3d.UnitX;
    }

    public sealed class ResourceAmountDef
    {
        public string Resource { get; init; }
        public double Amount { get; init; }
        public double Max { get; init; }
    }

    public sealed class DragDef
    {
        /// <summary>Drag coefficient of the exposed top face.</summary>
        public double Top { get; init; } = 0.9;
        public double Bottom { get; init; } = 0.9;
        /// <summary>Drag coefficient for cross-flow over the side.</summary>
        public double Side { get; init; } = 0.6;
    }

    /// <summary>
    /// One piece of procedural geometry. The renderer turns primitives into meshes; the core uses them only for
    /// bounds. Types: lathe (revolved profile), cylinder, cone, box, prism (extruded polygon), sphere, torus.
    /// </summary>
    public sealed class MeshPrimitive
    {
        public string Type { get; init; } = "cylinder";
        /// <summary>Lathe profile as [radius, y] pairs, bottom to top.</summary>
        public double[][] Profile { get; init; }
        public int Segments { get; init; } = 32;
        public double Radius { get; init; } = 0.5;
        public double RadiusTop { get; init; } = double.NaN;
        public double Height { get; init; } = 1;
        /// <summary>Minor radius for torus.</summary>
        public double Tube { get; init; } = 0.05;
        public Vector3d Size { get; init; } = Vector3d.One;
        /// <summary>Prism outline as [x, y] pairs in the local XY plane, extruded along Z.</summary>
        public double[][] Points { get; init; }
        public double Thickness { get; init; } = 0.05;
        public Vector3d Offset { get; init; }
        /// <summary>Euler rotation in degrees (applied Y, then X, then Z).</summary>
        public Vector3d Rotation { get; init; }
        public string Material { get; init; } = "white";
        /// <summary>Animation group name; animated modules transform all primitives in a group together.</summary>
        public string Group { get; init; }
        public bool CapTop { get; init; } = true;
        public bool CapBottom { get; init; } = true;
        /// <summary>Flat-shaded lathes look faceted (good for engine bells with panels).</summary>
        public bool Smooth { get; init; } = true;
        /// <summary>Radial symmetry: repeat the primitive N times around the Y axis.</summary>
        public int Repeat { get; init; } = 1;

        public QuaternionD RotationQuaternion()
        {
            Vector3d r = Rotation * MathUtil.Deg2Rad;
            QuaternionD qy = QuaternionD.FromAxisAngle(Vector3d.UnitY, r.Y);
            QuaternionD qx = QuaternionD.FromAxisAngle(Vector3d.UnitX, r.X);
            QuaternionD qz = QuaternionD.FromAxisAngle(Vector3d.UnitZ, r.Z);
            return qy * qx * qz;
        }

        /// <summary>Representative local points for bounding-box computation (before Offset/Rotation).</summary>
        public IEnumerable<Vector3d> BoundPoints()
        {
            switch (Type)
            {
                case "lathe":
                {
                    if (Profile == null) yield break;
                    foreach (double[] p in Profile)
                        foreach (Vector3d c in Ring(p[0], p[1])) yield return c;
                    break;
                }
                case "cylinder":
                case "cone":
                {
                    double rt = double.IsNaN(RadiusTop) ? (Type == "cone" ? 0 : Radius) : RadiusTop;
                    foreach (Vector3d c in Ring(Radius, -Height / 2)) yield return c;
                    foreach (Vector3d c in Ring(rt, Height / 2)) yield return c;
                    break;
                }
                case "box":
                {
                    Vector3d h = Size * 0.5;
                    for (int i = 0; i < 8; i++)
                        yield return new Vector3d((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
                    break;
                }
                case "prism":
                {
                    if (Points == null) yield break;
                    foreach (double[] p in Points)
                    {
                        yield return new Vector3d(p[0], p[1], -Thickness / 2);
                        yield return new Vector3d(p[0], p[1], Thickness / 2);
                    }
                    break;
                }
                case "sphere":
                {
                    for (int i = 0; i < 8; i++)
                        yield return new Vector3d((i & 1) == 0 ? -Radius : Radius, (i & 2) == 0 ? -Radius : Radius, (i & 4) == 0 ? -Radius : Radius);
                    break;
                }
                case "torus":
                {
                    double R = Radius + Tube;
                    foreach (Vector3d c in Ring(R, -Tube)) yield return c;
                    foreach (Vector3d c in Ring(R, Tube)) yield return c;
                    break;
                }
            }
        }

        private static IEnumerable<Vector3d> Ring(double r, double y)
        {
            for (int i = 0; i < 8; i++)
            {
                double a = i * System.Math.PI / 4;
                yield return new Vector3d(r * System.Math.Cos(a), y, r * System.Math.Sin(a));
            }
        }
    }

    /// <summary>Axis-aligned bounds of a part in its local frame, plus a bounding cylinder about local Y.</summary>
    public readonly struct PartBounds
    {
        public readonly Vector3d Min;
        public readonly Vector3d Max;
        /// <summary>Maximum distance from the local Y axis.</summary>
        public readonly double Radius;
        /// <summary>True when the geometry is essentially round about the Y axis (tanks, pods, engines).</summary>
        public readonly bool IsRound;

        public PartBounds(Vector3d min, Vector3d max, double radius, bool isRound)
        {
            Min = min;
            Max = max;
            Radius = radius;
            IsRound = isRound;
        }

        public Vector3d Center => (Min + Max) * 0.5;
        public Vector3d Size => Max - Min;
        public double Height => Max.Y - Min.Y;
    }

    /// <summary>
    /// Static, data-driven description of a part type. Loaded from data/parts/*.json. Instances placed on a
    /// vessel are <see cref="Vehicles.PartInstance"/>.
    /// </summary>
    public sealed class PartDefinition
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public PartCategory Category { get; init; }
        public string Manufacturer { get; init; } = "Open Space Works";
        public string Description { get; init; } = "";
        public double Cost { get; init; }
        /// <summary>Dry mass in kg (without resources).</summary>
        public double Mass { get; init; }
        /// <summary>Technology node that unlocks this part in science/career modes.</summary>
        public string Tech { get; init; } = "start";
        public List<AttachNodeDef> AttachNodes { get; init; } = new();
        /// <summary>Null if the part cannot be attached to surfaces.</summary>
        public SurfaceAttachDef SurfaceAttach { get; init; }
        /// <summary>Whether other parts may be surface-attached to this one.</summary>
        public bool AllowSurfaceAttachOnThis { get; init; } = true;
        public List<ResourceAmountDef> Resources { get; init; } = new();
        public DragDef Drag { get; init; } = new();
        /// <summary>Force a joint to this part can carry before breaking (N).</summary>
        public double BreakingForce { get; init; } = 3.0e6;
        /// <summary>Impact speed the part survives (m/s).</summary>
        public double CrashTolerance { get; init; } = 8;
        /// <summary>Maximum skin temperature (K). Reserved for the heating milestone.</summary>
        public double MaxTemperature { get; init; } = 2000;
        public List<MeshPrimitive> Mesh { get; init; } = new();

        /// <summary>Populated by <see cref="PartCatalog"/> from the JSON "modules" array.</summary>
        public List<PartModuleDef> Modules { get; } = new();

        public PartBounds Bounds { get; private set; }

        public T GetModule<T>() where T : PartModuleDef => Modules.OfType<T>().FirstOrDefault();
        public IEnumerable<T> GetModules<T>() where T : PartModuleDef => Modules.OfType<T>();
        public bool HasModule<T>() where T : PartModuleDef => Modules.OfType<T>().Any();
        public bool IsStageable => Modules.Any(m => m.IsStageable);

        public AttachNodeDef GetNode(string id) => AttachNodes.FirstOrDefault(n => n.Id == id);

        public double ResourceMass => Resources.Sum(r => r.Amount * (ResourceLibrary.Get(r.Resource)?.Density ?? 0));
        public double WetMass => Mass + ResourceMass;

        public void FinishLoading()
        {
            if (string.IsNullOrWhiteSpace(Id)) throw new FormatException("Part without id.");
            if (string.IsNullOrWhiteSpace(Name)) throw new FormatException($"{Id}: part needs a name.");
            if (Mass <= 0) throw new FormatException($"{Id}: dry mass must be positive.");
            if (Mesh.Count == 0) throw new FormatException($"{Id}: part needs at least one mesh primitive.");
            foreach (ResourceAmountDef r in Resources)
            {
                if (!ResourceLibrary.Exists(r.Resource)) throw new FormatException($"{Id}: unknown resource '{r.Resource}'.");
                if (r.Max <= 0 || r.Amount < 0 || r.Amount > r.Max) throw new FormatException($"{Id}: invalid amount for '{r.Resource}'.");
            }
            var seen = new HashSet<string>();
            foreach (AttachNodeDef n in AttachNodes)
            {
                if (string.IsNullOrWhiteSpace(n.Id) || !seen.Add(n.Id)) throw new FormatException($"{Id}: attach node ids must be unique and non-empty.");
                if (n.Direction.LengthSquared < 1e-9) throw new FormatException($"{Id}: attach node '{n.Id}' needs a direction.");
            }
            foreach (PartModuleDef m in Modules) m.Validate(this);
            ComputeBounds();
        }

        /// <summary>Mesh groups that move out of the part when deployed (canopies, legs, panels). They do not count towards bounds.</summary>
        public HashSet<string> DeployableGroups()
        {
            var set = new HashSet<string>();
            foreach (PartModuleDef m in Modules)
            {
                AnimationDef a = m switch
                {
                    ParachuteModuleDef p => p.Animation,
                    LandingLegModuleDef l => l.Animation,
                    SolarPanelModuleDef s => s.Animation,
                    _ => null,
                };
                if (a?.Group != null) set.Add(a.Group);
            }
            return set;
        }

        private void ComputeBounds()
        {
            HashSet<string> deployable = DeployableGroups();
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            double radius = 0;
            bool round = true;
            foreach (MeshPrimitive prim in Mesh)
            {
                if (prim.Group != null && deployable.Contains(prim.Group)) continue;
                QuaternionD rot = prim.RotationQuaternion();
                bool axisAligned = prim.Rotation.LengthSquared < 1e-9 && System.Math.Abs(prim.Offset.X) < 1e-6 && System.Math.Abs(prim.Offset.Z) < 1e-6;
                bool primRound = axisAligned && (prim.Type is "lathe" or "cylinder" or "cone" or "sphere" or "torus");
                // Small details (antennas, struts) don't make a part non-round.
                foreach (Vector3d p0 in prim.BoundPoints())
                {
                    for (int k = 0; k < System.Math.Max(1, prim.Repeat); k++)
                    {
                        QuaternionD rep = QuaternionD.FromAxisAngle(Vector3d.UnitY, k * MathUtil.TwoPi / System.Math.Max(1, prim.Repeat));
                        Vector3d p = rep.Rotate(rot.Rotate(p0) + prim.Offset);
                        minX = System.Math.Min(minX, p.X); maxX = System.Math.Max(maxX, p.X);
                        minY = System.Math.Min(minY, p.Y); maxY = System.Math.Max(maxY, p.Y);
                        minZ = System.Math.Min(minZ, p.Z); maxZ = System.Math.Max(maxZ, p.Z);
                        radius = System.Math.Max(radius, System.Math.Sqrt(p.X * p.X + p.Z * p.Z));
                    }
                }
                if (!primRound && prim.Group == null && prim.Repeat <= 1) round = false;
            }
            // A part is "round" if its footprint is nearly square and centred.
            double sx = maxX - minX, sz = maxZ - minZ;
            if (System.Math.Abs(sx - sz) > 0.15 * System.Math.Max(sx, sz)) round = false;
            Bounds = new PartBounds(new Vector3d(minX, minY, minZ), new Vector3d(maxX, maxY, maxZ), radius, round);
        }

        public override string ToString() => $"{Name} ({Id})";
    }
}
