namespace OSP.Core
{
    /// <summary>Physical constants in SI units.</summary>
    public static class PhysicsConstants
    {
        /// <summary>Standard gravity used to convert specific impulse (s) to exhaust velocity (m/s). m/s^2.</summary>
        public const double G0 = 9.80665;

        /// <summary>Newtonian gravitational constant, m^3 kg^-1 s^-2.</summary>
        public const double G = 6.67430e-11;

        /// <summary>One standard atmosphere, Pa.</summary>
        public const double StandardAtmosphere = 101325.0;
    }
}
