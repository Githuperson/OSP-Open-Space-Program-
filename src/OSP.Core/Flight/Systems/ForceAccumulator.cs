using OSP.Core.Mathx;

namespace OSP.Core.Flight.Systems;

/// <summary>
/// Collects the external, non-gravitational forces acting on one craft during a step, all in the craft frame:
/// the net force, the net torque about the centre of mass, and per-part contributions (needed for joint-stress analysis).
/// </summary>
public sealed class ForceAccumulator
{
    public struct Item { public CraftPart Part; public Vec3d Force; public Vec3d Point; }

    public Vec3d Force;
    public Vec3d Torque;
    public readonly List<Item> Items = new(64);
    public Vec3d CenterOfMass;

    public void Reset(Vec3d com)
    {
        Force = Vec3d.Zero; Torque = Vec3d.Zero; Items.Clear(); CenterOfMass = com;
    }

    /// <summary>Apply force f (craft frame) at craft-frame point.</summary>
    public void Add(CraftPart part, Vec3d f, Vec3d point)
    {
        Force += f;
        Torque += Vec3d.Cross(point - CenterOfMass, f);
        Items.Add(new Item { Part = part, Force = f, Point = point });
    }

    /// <summary>Contact reaction, recorded for structural analysis exactly like any other external force.</summary>
    public void AddContact(CraftPart part, Vec3d f, Vec3d point) => Add(part, f, point);

    /// <summary>Apply a pure torque (e.g. reaction wheel) with no net force.</summary>
    public void AddTorque(Vec3d t) => Torque += t;
}
