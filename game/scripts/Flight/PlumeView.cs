using System;
using Godot;
using OSP.Core.Parts;
using OSP.Game.Rendering;

namespace OSP.Game.Flight;

/// <summary>Visual exhaust plume attached below an engine nozzle. Intensity follows throttle; the plume widens in vacuum.</summary>
public sealed partial class PlumeView : Node3D
{
    private static Shader? _shader;
    private readonly ShaderMaterial _mat;
    private readonly MeshInstance3D _outer, _inner;
    private readonly double _baseLen, _baseRadius;

    public PlumeView(EngineModule e)
    {
        _shader ??= GD.Load<Shader>("res://shaders/plume.gdshader");
        _baseLen = e.PlumeLength; _baseRadius = e.PlumeRadius;
        _mat = new ShaderMaterial { Shader = _shader };
        _mat.SetShaderParameter("length_m", (float)e.PlumeLength);
        // profile along -Y: tip at the bottom, nozzle exit plane at y = 0
        double L = e.PlumeLength, r = e.PlumeRadius;
        var outerProfile = new[] { new[] { -L, 0.0 }, new[] { -L * 0.75, r * 0.45 }, new[] { -L * 0.4, r * 0.85 }, new[] { -L * 0.1, r * 1.0 }, new[] { 0.0, r * 0.9 } };
        _outer = new MeshInstance3D { Mesh = MeshFactory.Lathe(outerProfile, 20, true), MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var innerProfile = new[] { new[] { -L * 0.55, 0.0 }, new[] { -L * 0.3, r * 0.35 }, new[] { 0.0, r * 0.55 } };
        var innerMat = (ShaderMaterial)_mat.Duplicate();
        innerMat.SetShaderParameter("length_m", (float)(L * 0.55));
        innerMat.SetShaderParameter("core_color", new Color(1f, 0.97f, 0.9f));
        innerMat.SetShaderParameter("edge_color", new Color(1f, 0.75f, 0.4f));
        _inner = new MeshInstance3D { Mesh = MeshFactory.Lathe(innerProfile, 16, true), MaterialOverride = innerMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_outer); AddChild(_inner);
        Visible = false;
    }

    /// <summary>Update from the simulation: intensity 0..1, ambient pressure in atmospheres.</summary>
    public void SetState(double intensity, double pressureAtm)
    {
        if (intensity < 0.01) { Visible = false; return; }
        Visible = true;
        double expand = 1.0 + 1.6 * (1.0 - Math.Clamp(pressureAtm, 0, 1));
        float len = (float)(0.55 + 0.45 * intensity);
        float wid = (float)(expand * (0.6 + 0.4 * intensity));
        Scale = new Vector3(wid, len * (float)(1.0 + 0.3 * (1.0 - Math.Clamp(pressureAtm, 0, 1))), wid);
        _mat.SetShaderParameter("intensity", (float)Math.Min(1.0, 0.55 + intensity));
        ((ShaderMaterial)_inner.MaterialOverride!).SetShaderParameter("intensity", (float)Math.Min(1.0, 0.7 + intensity));
        // a high-expansion vacuum plume is paler and bluer
        _mat.SetShaderParameter("edge_color", new Color(0.45f, 0.55f, 1.0f).Lerp(new Color(0.5f, 0.6f, 1f), (float)(1 - pressureAtm)));
    }
}
