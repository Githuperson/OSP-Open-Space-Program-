using Godot;

namespace OSP.Game.Rendering;

/// <summary>One-shot visual effects (explosions). They live in the render-origin frame and fade quickly, so no re-basing is needed.</summary>
public static class Effects
{
    private static GradientTexture2D? _blob;
    private static StandardMaterial3D? _mat;

    private static GradientTexture2D Blob()
    {
        if (_blob != null) return _blob;
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1)); g.SetColor(1, new Color(1, 1, 1, 0));
        _blob = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64 };
        return _blob;
    }

    public static void Explosion(Node parent, Vector3 pos, float size)
    {
        _mat ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoTexture = Blob(), VertexColorUseAsAlbedo = true,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
        };
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(1f, 0.9f, 0.6f, 1f)); gradient.SetColor(1, new Color(0.9f, 0.25f, 0.05f, 0f));
        var proc = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = 180f, InitialVelocityMin = 4f * size, InitialVelocityMax = 18f * size,
            Gravity = Vector3.Zero, ScaleMin = 1.2f * size, ScaleMax = 3.5f * size, Damping = new Vector2(2f, 4f),
            ColorRamp = new GradientTexture1D { Gradient = gradient },
        };
        var p = new GpuParticles3D
        {
            Amount = 48, Lifetime = 1.3, OneShot = true, Explosiveness = 1f, ProcessMaterial = proc,
            DrawPass1 = new QuadMesh { Size = Vector2.One, Material = _mat }, Emitting = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(new Vector3(-80, -80, -80), new Vector3(160, 160, 160)),
        };
        parent.AddChild(p);
        p.GlobalPosition = pos;
        p.GetTree().CreateTimer(2.5).Timeout += () => { if (GodotObject.IsInstanceValid(p)) p.QueueFree(); };
    }
}
