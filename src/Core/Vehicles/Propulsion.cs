using OSP.Core.Parts;

namespace OSP.Core.Vehicles
{
    /// <summary>
    /// Engine propellant model shared by flight and the delta-v calculator.
    /// Mass flow at throttle τ: ṁ = τ·F_vac/(Isp_vac·g0). Thrust: F = ṁ·Isp(p)·g0 (constant mass flow, Isp varies with
    /// ambient pressure). Propellants are drawn in their mass ratios; if any runs short the engine produces the
    /// proportionally reduced thrust.
    /// </summary>
    public static class Propulsion
    {
        /// <summary>
        /// Draws propellant for one engine over <paramref name="dt"/> seconds. Returns the achieved fraction of the
        /// requested flow (0 = flame-out, 1 = full).
        /// </summary>
        public static double ConsumePropellant(ResourceNetwork network, PartInstance engine, EngineModuleDef def, double throttle, double dt)
        {
            double mdot = def.MaxMassFlow * throttle;
            double total = mdot * dt;
            if (total <= 0) return 0;
            double fraction = 1.0;
            foreach (PropellantDef prop in def.Propellants)
            {
                double need = total * prop.Ratio;
                double avail = network.Available(engine, prop.Resource);
                if (avail < need) fraction = System.Math.Min(fraction, avail / need);
            }
            if (fraction <= 1e-9) return 0;
            foreach (PropellantDef prop in def.Propellants)
                network.Draw(engine, prop.Resource, total * prop.Ratio * fraction);
            return fraction;
        }

        public static bool HasPropellant(ResourceNetwork network, PartInstance engine, EngineModuleDef def)
        {
            foreach (PropellantDef prop in def.Propellants)
                if (network.Available(engine, prop.Resource) <= 1e-6) return false;
            return true;
        }

        /// <summary>Effective exhaust velocity (m/s) at a given pressure.</summary>
        public static double ExhaustVelocity(EngineModuleDef def, double pressureAtm) => def.IspAt(pressureAtm) * PhysicsConstants.G0;
    }
}
