using OSP.Core.Mathx;

namespace OSP.Core.Flight;

public enum SimEventKind
{
    Info, Warning, StageActivated, Separation, EngineIgnition, EngineFlameout, JointFailed, PartDestroyed,
    Touchdown, Liftoff, Crashed, SoiChange, ParachuteOpened, ParachuteCut, CraftDestroyed, Recovered,
    AtmosphereEntered, AtmosphereLeft, OrbitEstablished, WarpLimited,
}

public sealed class SimEvent
{
    public double Time { get; init; }
    public SimEventKind Kind { get; init; }
    public string Message { get; init; } = "";
    public int CraftId { get; init; } = -1;
    /// <summary>Craft-frame position of the event (parts), when meaningful.</summary>
    public Vec3d? LocalPosition { get; init; }
    public int PartId { get; init; } = -1;
    public override string ToString() => $"[{Time:F1}s] {Kind}: {Message}";
}
