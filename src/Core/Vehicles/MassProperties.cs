using System.Collections.Generic;
using OSP.Core.Math;
using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    /// <summary>Mass (kg), centre of mass (vessel frame, m) and inertia tensor about the COM (vessel frame, kg·m²).</summary>
    public readonly struct MassProperties
    {
        public readonly double Mass;
        public readonly Vector3d CenterOfMass;
        public readonly Matrix3d Inertia;

        public MassProperties(double mass, Vector3d com, Matrix3d inertia)
        {
            Mass = mass;
            CenterOfMass = com;
            Inertia = inertia;
        }

        /// <summary>
        /// Each part is modelled as a uniform solid cylinder (round parts) or box (others) filling its bounds,
        /// combined with the parallel-axis theorem: I = Σ (R I_local Rᵀ + m(|r|²E - r rᵀ)).
        /// </summary>
        public static MassProperties Compute(IEnumerable<PartInstance> parts)
        {
            double mass = 0;
            Vector3d moment = Vector3d.Zero;
            var list = new List<(PartInstance p, double m, Vector3d c)>();
            foreach (PartInstance p in parts)
            {
                if (p.Def == null) continue;
                double m = p.TotalMass;
                Vector3d c = p.LocalToVessel(p.Def.Bounds.Center);
                mass += m;
                moment += c * m;
                list.Add((p, m, c));
            }
            if (mass <= 0) return new MassProperties(0, Vector3d.Zero, Matrix3d.Zero);
            Vector3d com = moment / mass;

            Matrix3d inertia = Matrix3d.Zero;
            foreach (var (p, m, c) in list)
            {
                PartBounds b = p.Def.Bounds;
                Matrix3d local;
                if (b.IsRound)
                {
                    double r = System.Math.Max(b.Radius, 0.05);
                    double h = System.Math.Max(b.Height, 0.05);
                    double axial = 0.5 * m * r * r;
                    double trans = m * (3 * r * r + h * h) / 12.0;
                    local = Matrix3d.Diagonal(trans, axial, trans);
                }
                else
                {
                    Vector3d s = b.Size;
                    double x = System.Math.Max(s.X, 0.05), y = System.Math.Max(s.Y, 0.05), z = System.Math.Max(s.Z, 0.05);
                    local = Matrix3d.Diagonal(m * (y * y + z * z) / 12.0, m * (x * x + z * z) / 12.0, m * (x * x + y * y) / 12.0);
                }
                Matrix3d R = Matrix3d.FromQuaternion(p.Rotation);
                inertia = inertia + R * local * R.Transposed() + Matrix3d.ParallelAxis(m, c - com);
            }
            return new MassProperties(mass, com, inertia);
        }
    }
}
