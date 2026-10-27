using OSP.Core.Mathx;

namespace OSP.Core.Flight;

public struct MassProperties
{
    public double Mass;
    /// <summary>Centre of mass in the craft frame.</summary>
    public Vec3d CenterOfMass;
    /// <summary>Inertia tensor about the CoM in the craft frame (kg m^2).</summary>
    public Mat3d Inertia;
    public Mat3d InertiaInverse;
}
