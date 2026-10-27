namespace OSP.Core.Flight
{
    /// <summary>Values computed by the simulation each step and shown by the HUD. All SI units.</summary>
    public sealed class FlightTelemetry
    {
        public double AltitudeSeaLevel;
        public double AltitudeTerrain;
        public double VerticalSpeed;
        public double HorizontalSurfaceSpeed;
        public double SurfaceSpeed;
        public double OrbitalSpeed;
        public double StaticPressure;
        public double Density;
        public double DynamicPressure;
        public double Mach;
        /// <summary>Proper acceleration magnitude in g (what an accelerometer reads).</summary>
        public double GForce;
        public double Thrust;
        public double MaxThrust;
        public double Twr;
        public double Latitude;
        public double Longitude;
        public bool InContact;
        public bool Splashed;
        public double ElectricGeneration;
        public double ElectricConsumption;
        public double SunExposure;
        public int ActiveEngines;
        public bool FlameOut;
        /// <summary>False when no crewed pod or powered probe core can command the vessel.</summary>
        public bool HasControl = true;
    }
}
