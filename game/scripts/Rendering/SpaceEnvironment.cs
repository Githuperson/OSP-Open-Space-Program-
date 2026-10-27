using Godot;
using OSP.Core.Mathx;

namespace OSP.Game.Rendering;

/// <summary>Sky (starfield), sun light, ambient light and tonemapping shared by the menu, flight and map scenes.</summary>
public sealed partial class SpaceEnvironment : Node3D
{
    public WorldEnvironment Env { get; } = new();
    public DirectionalLight3D Sun { get; } = new();
    private readonly Godot.Environment _environment = new();
    private readonly ShaderMaterial _skyMat;

    public SpaceEnvironment()
    {
        var shader = GD.Load<Shader>("res://shaders/stars.gdshader");
        _skyMat = new ShaderMaterial { Shader = shader };
        var sky = new Sky { SkyMaterial = _skyMat, RadianceSize = Sky.RadianceSizeEnum.Size128, ProcessMode = Sky.ProcessModeEnum.Quality };
        _environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        _environment.Sky = sky;
        _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _environment.AmbientLightColor = new Color(0.25f, 0.3f, 0.4f);
        _environment.AmbientLightEnergy = 0.12f;
        _environment.TonemapMode = Godot.Environment.ToneMapper.Filmic;
        _environment.TonemapExposure = 1.0f;
        _environment.GlowEnabled = true;
        _environment.GlowIntensity = 0.5f;
        _environment.GlowBloom = 0.05f;
        _environment.GlowHdrThreshold = 1.0f;
        Env.Environment = _environment;
        AddChild(Env);

        Sun.LightEnergy = 1.1f;
        Sun.ShadowEnabled = true;
        Sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
        Sun.DirectionalShadowMaxDistance = 250f;
        Sun.ShadowBias = 0.04f;
        AddChild(Sun);
    }

    public void SetShadows(bool on, float maxDistance) { Sun.ShadowEnabled = on; Sun.DirectionalShadowMaxDistance = maxDistance; }

    /// <summary>Point the sun light along the star direction (from the scene towards the star) and fade ambient with atmosphere.</summary>
    public void Update(Vec3d towardSun, double sunStrength, double atmosphereDensityFactor)
    {
        var d = new Vector3((float)towardSun.X, (float)towardSun.Y, (float)towardSun.Z).Normalized();
        // Light travels from the star towards the scene: forward axis (-Z) = -towardSun
        var up = Mathf.Abs(d.Y) > 0.95f ? Vector3.Right : Vector3.Up;
        Sun.LookAtFromPosition(Vector3.Zero, -d, up);
        Sun.LightEnergy = (float)(1.1 * sunStrength);
        // Sky: blends from black space to a lit blue sky when low in a dense atmosphere (approximation of scattered ambient).
        float a = (float)Mathf.Clamp(atmosphereDensityFactor, 0, 1);
        _environment.AmbientLightEnergy = 0.1f + 0.55f * a;
        _environment.AmbientLightColor = new Color(0.25f, 0.3f, 0.4f).Lerp(new Color(0.55f, 0.7f, 1.0f), a);
        _skyMat.SetShaderParameter("brightness", 1.0f - a * 0.95f);
    }
}
