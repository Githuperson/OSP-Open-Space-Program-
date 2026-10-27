using OSP.Core.Flight;

namespace OSP.Game.Flight;

/// <summary>Read-only hooks for the automated self test.</summary>
public sealed partial class FlightScene
{
    public FlightSimulation DebugSim => Sim;
    public bool DebugPaused => _paused;
    public bool DebugMapMode => _mapMode;
}
