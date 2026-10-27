namespace OSP.Core.Flight
{
    /// <summary>
    /// Time acceleration state. Two ladders exist:
    ///  * Physics warp (1-4×): the full physics step runs more times per frame. Allowed anywhere.
    ///  * Rails warp (5-100000×): vessels follow exact Keplerian orbits (or stay pinned to the surface).
    ///    Allowed only when no vessel is thrusting or flying in an atmosphere and the active vessel is above the
    ///    body's altitude limit for that rate (or landed).
    /// </summary>
    public sealed class TimeWarp
    {
        public static readonly double[] RailsRates = { 1, 5, 10, 50, 100, 1000, 10000, 100000 };
        public static readonly double[] PhysicsRates = { 1, 2, 3, 4 };

        public bool PhysicsMode { get; private set; }
        public int Index { get; private set; }

        public double Rate => PhysicsMode ? PhysicsRates[Index] : RailsRates[Index];
        public bool IsRails => !PhysicsMode && Index > 0;
        public bool IsPhysicsWarp => PhysicsMode && Index > 0;
        public bool IsRealTime => Index == 0;

        public void SetRails(int index)
        {
            PhysicsMode = false;
            Index = System.Math.Clamp(index, 0, RailsRates.Length - 1);
        }

        public void SetPhysics(int index)
        {
            PhysicsMode = true;
            Index = System.Math.Clamp(index, 0, PhysicsRates.Length - 1);
            if (Index == 0) PhysicsMode = false;
        }

        public void Stop()
        {
            Index = 0;
            PhysicsMode = false;
        }

        public override string ToString() => Index == 0 ? "1×" : $"{Rate:0}×{(PhysicsMode ? " (physics)" : "")}";
    }
}
