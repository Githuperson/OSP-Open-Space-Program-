using System;
using System.Collections.Generic;
using Godot;
using OSP.Core.Parts;

namespace OSP.Game.Rendering;

/// <summary>
/// Builds the 3D node tree for a part definition from its declarative <see cref="PartVisual"/> and exposes handles for
/// animation (hinged legs/panels, parachute canopy) and effects (engine nozzle).
/// </summary>
public sealed partial class PartModel : Node3D
{
    public PartDefinition Def { get; private set; } = null!;
    public readonly List<(Node3D node, HingeDef hinge)> Hinges = new();
    public readonly List<Node3D> CanopyNodes = new();
    public readonly List<Node3D> PackNodes = new();
    private static readonly Dictionary<string, Mesh> MeshCache = new();

    public static PartModel Build(PartDefinition def)
    {
        var root = new PartModel { Def = def, Name = def.Id };
        int i = 0;
        foreach (var e in def.Visual.Elements)
        {
            Node3D? n = BuildElement(def, e, i++);
            if (n == null) continue;
            root.AddChild(n);
            if (e.Hinge != null) root.Hinges.Add((n, e.Hinge));
            if (e.Tag == "canopy") { root.CanopyNodes.Add(n); n.Visible = false; }
            if (e.Tag == "pack") root.PackNodes.Add(n);
        }
        return root;
    }

    private static Mesh GetMesh(string key, Func<Mesh> make)
    {
        if (!MeshCache.TryGetValue(key, out var m)) MeshCache[key] = m = make();
        return m;
    }

    private static Node3D? BuildElement(PartDefinition def, VisualElement e, int index)
    {
        string key = $"{def.Id}#{index}";
        Mesh? mesh = null;
        switch (e.Type)
        {
            case "lathe":
                if (e.Profile == null) return null;
                mesh = GetMesh(key, () => MeshFactory.Lathe(e.Profile, e.Segments, e.Smooth));
                break;
            case "cylinder":
                mesh = GetMesh(key, () => MeshFactory.Cylinder(e.Radius, e.Length, e.RadiusTop, e.Segments));
                break;
            case "cone":
                mesh = GetMesh(key, () => MeshFactory.Cylinder(e.Radius, e.Length, 0.0, e.Segments));
                break;
            case "box":
                mesh = GetMesh(key, () => MeshFactory.Box(new Vector3((float)e.Size[0], (float)e.Size[1], (float)e.Size[2])));
                break;
            case "sphere":
                mesh = GetMesh(key, () => MeshFactory.Sphere(e.Radius, e.Segments));
                break;
            default:
                GD.PushWarning($"[OSP] Part {def.Id}: unknown visual element type '{e.Type}'");
                return null;
        }

        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = Materials.Get(e.Material) };
        mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        if (e.Type == "sphere" && (e.Size[0] != 1 || e.Size[1] != 1 || e.Size[2] != 1))
            mi.Scale = new Vector3((float)e.Size[0], (float)e.Size[1], (float)e.Size[2]);
        var offset = new Vector3((float)e.Offset[0], (float)e.Offset[1], (float)e.Offset[2]);
        var rot = new Vector3(Mathf.DegToRad((float)e.Rotation[0]), Mathf.DegToRad((float)e.Rotation[1]), Mathf.DegToRad((float)e.Rotation[2]));

        if (e.Hinge != null)
        {
            var pivotPos = new Vector3((float)e.Hinge.Pivot[0], (float)e.Hinge.Pivot[1], (float)e.Hinge.Pivot[2]);
            var pivot = new Node3D { Position = pivotPos };
            mi.Position = offset - pivotPos;
            mi.Rotation = rot;
            pivot.AddChild(mi);
            return pivot;
        }
        mi.Position = offset; mi.Rotation = rot;
        return mi;
    }

    /// <summary>Apply deployment animation. <paramref name="legs"/>, <paramref name="solar"/> in 0..1.</summary>
    public void Animate(double legs, double solar, bool chuteOpen, double chuteScale)
    {
        foreach (var (node, h) in Hinges)
        {
            double f = h.Driver == "leg" ? legs : h.Driver == "solar" ? solar : 0;
            var axis = new Vector3((float)h.Axis[0], (float)h.Axis[1], (float)h.Axis[2]).Normalized();
            node.Basis = new Basis(axis, Mathf.DegToRad((float)(h.AngleDeg * f)));
        }
        foreach (var c in CanopyNodes)
        {
            c.Visible = chuteOpen;
            float s = (float)Math.Max(0.05, chuteScale);
            c.Scale = new Vector3(s, s, s);
        }
        foreach (var p in PackNodes) p.Visible = !chuteOpen;
    }
}
