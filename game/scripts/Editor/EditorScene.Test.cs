using System.Linq;
using Godot;
using OSP.Core.Vehicles;

namespace OSP.Game.Editor;

/// <summary>Hooks used by the automated self test (scripted input through the real event path).</summary>
public sealed partial class EditorScene
{
    public VehicleDesign DebugDesign => _design;
    public int DebugSymmetry { get => _symmetry; set => _symmetry = value; }
    public void DebugPick(string partId) => PickCatalogPart(_cat.Get(partId));
    public void DebugUndo() => DoUndo();
    public void DebugRedo() => DoRedo();
    public int DebugSelected => _selected;
    public Camera3D DebugCamera => _cam;

    /// <summary>Screen position of a node of a placed part (for aiming the mouse).</summary>
    public Vector2 DebugNodeScreen(int partId, string node)
    {
        var inst = _design.Get(partId);
        var def = _cat.Get(inst.PartId);
        var (p, _) = AttachGeometry.NodeWorld(inst, def.Node(node)!);
        return _cam.UnprojectPosition(ToV(p) + _vehicleRoot.Position);
    }

    public Vector2 DebugPartScreen(int partId, double localX = 0, double localY = 0)
    {
        var inst = _design.Get(partId);
        var w = ToV(inst.Position + inst.Rotation.Rotate(new OSP.Core.Mathx.Vec3d(localX, localY, 0))) + _vehicleRoot.Position;
        return _cam.UnprojectPosition(w);
    }
}
