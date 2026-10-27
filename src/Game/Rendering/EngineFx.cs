using Godot;
using OSP.Core.Parts;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// Exhaust plume for one engine: a layered additive cone driven by a shader, a flickering light, and smoke
    /// particles for solid motors in an atmosphere. The plume lengthens with throttle and widens as ambient pressure
    /// drops (under-expanded exhaust in vacuum).
    /// </summary>
    public partial class EngineFx : Node3D
    {
        private static Shader _shader;
        private static ArrayMesh _coneMesh;

        private MeshInstance3D _plume;
        private MeshInstance3D _inner;
        private OmniLight3D _light;
        private CpuParticles3D _smoke;
        private ShaderMaterial _mat;
        private ShaderMaterial _innerMat;
        private EngineModuleDef _def;
        private float _phase;

        public static EngineFx Create(EngineModuleDef def)
        {
            _shader ??= GD.Load<Shader>("res://shaders/plume.gdshader");
            _coneMesh ??= BuildCone();
            var fx = new EngineFx { _def = def, Name = "EngineFx" };
            fx._phase = (float)GD.RandRange(0, 100);
            Color c = def.Plume.Color.ToColor();
            fx._mat = new ShaderMaterial { Shader = _shader };
            fx._mat.SetShaderParameter("core_color", c.Lerp(Colors.White, 0.6f));
            fx._mat.SetShaderParameter("edge_color", c);
            fx._mat.SetShaderParameter("time_offset", fx._phase);
            fx._plume = new MeshInstance3D { Mesh = _coneMesh, MaterialOverride = fx._mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            fx.AddChild(fx._plume);
            fx._innerMat = (ShaderMaterial)fx._mat.Duplicate();
            fx._innerMat.SetShaderParameter("core_color", Colors.White);
            fx._innerMat.SetShaderParameter("edge_color", c.Lerp(Colors.White, 0.3f));
            fx._inner = new MeshInstance3D { Mesh = _coneMesh, MaterialOverride = fx._innerMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            fx.AddChild(fx._inner);
            fx._light = new OmniLight3D { LightColor = c, OmniRange = (float)def.Plume.Length * 3, LightEnergy = 0, ShadowEnabled = false };
            fx._light.Position = new Vector3(0, -(float)def.Plume.Length * 0.3f, 0);
            fx.AddChild(fx._light);
            if (def.Plume.Smoky || def.IsSolid)
            {
                fx._smoke = new CpuParticles3D
                {
                    Amount = 120,
                    Lifetime = 4.0f,
                    Emitting = false,
                    LocalCoords = false,
                    Direction = new Vector3(0, -1, 0),
                    Spread = 8,
                    InitialVelocityMin = 25,
                    InitialVelocityMax = 40,
                    Gravity = Vector3.Zero,
                    DampingMin = 6,
                    DampingMax = 10,
                    ScaleAmountMin = 1.5f,
                    ScaleAmountMax = 3.5f,
                    Mesh = new QuadMesh { Size = new Vector2(1, 1) },
                };
                var smokeMat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    AlbedoColor = new Color(0.85f, 0.83f, 0.8f, 0.18f),
                    VertexColorUseAsAlbedo = true,
                };
                fx._smoke.MaterialOverride = smokeMat;
                var ramp = new Gradient();
                ramp.SetColor(0, new Color(1, 1, 1, 0.5f));
                ramp.SetColor(1, new Color(1, 1, 1, 0));
                fx._smoke.ColorRamp = ramp;
                var curve = new Curve();
                curve.AddPoint(new Vector2(0, 0.3f));
                curve.AddPoint(new Vector2(1, 1));
                fx._smoke.ScaleAmountCurve = curve;
                fx._smoke.Position = new Vector3(0, -0.5f, 0);
                fx.AddChild(fx._smoke);
            }
            fx.Visible = false;
            return fx;
        }

        /// <summary>Unit cone: radius 1 at y = -1 (far end), small at y = 0 (nozzle), pointing down −Y.</summary>
        private static ArrayMesh BuildCone()
        {
            var b = new MeshBuilder();
            var profile = new[] { new Vector2(1.0f, -1.0f), new Vector2(0.8f, -0.6f), new Vector2(0.55f, -0.2f), new Vector2(0.4f, 0f) };
            PartMeshFactory.Lathe(b, "plume", profile, 24, true, false, false, Transform3D.Identity);
            return b.Commit(_ => null);
        }

        /// <summary>Updates the plume for the current throttle (0..1) and ambient pressure (atm).</summary>
        public void UpdateFx(float throttle, float pressureAtm, float delta)
        {
            bool on = throttle > 0.01f;
            Visible = on || (_smoke != null && _smoke.Emitting);
            float len = (float)_def.Plume.Length * (0.4f + 0.6f * throttle);
            float expand = 1.0f + 1.8f * Mathf.Clamp(1.0f - pressureAtm, 0, 1);
            float rad = (float)_def.Plume.Radius * expand;
            if (on)
            {
                _plume.Visible = true;
                _inner.Visible = true;
                _plume.Scale = new Vector3(rad, len, rad);
                _inner.Scale = new Vector3(rad * 0.45f, len * 0.55f, rad * 0.45f);
                _mat.SetShaderParameter("intensity", 0.6f + 0.5f * throttle);
                _innerMat.SetShaderParameter("intensity", 0.8f + 0.6f * throttle);
                _light.LightEnergy = 2.5f * throttle * (0.9f + 0.1f * Mathf.Sin(Time.GetTicksMsec() * 0.05f + _phase));
            }
            else
            {
                _plume.Visible = false;
                _inner.Visible = false;
                _light.LightEnergy = 0;
            }
            if (_smoke != null) _smoke.Emitting = on && pressureAtm > 0.05f;
        }
    }
}
