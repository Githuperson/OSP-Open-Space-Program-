using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Math;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// "Scaled space": a separate 3D world, rendered into a background texture, that contains every celestial body,
    /// the star, atmospheres and the starfield at 1:<see cref="UnitMeters"/> scale. Distances of billions of metres
    /// therefore fit comfortably in single-precision transforms. The near (full-scale) world, with vessels and
    /// close terrain, is drawn on top of it. Both cameras share orientation and field of view, and everything is
    /// positioned relative to a double-precision floating origin each frame.
    /// The map view reuses this world with its own camera and orbit lines.
    /// </summary>
    public partial class ScaledSpace : Node
    {
        public const double UnitMeters = 1000.0;
        /// <summary>
        /// Depth range of the scaled camera (units). Godot needs a bounded near/far ratio to build frustums, so bodies
        /// beyond <see cref="CompressDistance"/> are pulled closer and shrunk by the same factor, which preserves
        /// their apparent (angular) size and direction exactly.
        /// </summary>
        public const float NearPlane = 0.5f;
        public const float FarPlane = 400000f;
        public const double CompressDistance = 250000;

        public SubViewport Viewport { get; private set; }
        public Camera3D Camera { get; private set; }
        public Node3D Root { get; private set; }
        public DirectionalLight3D Sun { get; private set; }
        public Environment Environment { get; private set; }

        private sealed class BodyVisual
        {
            public CelestialBody Body;
            public Node3D Node;
            public Node3D Surface;
            public MeshInstance3D Atmosphere;
            public ShaderMaterial AtmosphereMaterial;
            public MeshInstance3D Glow;
            public bool MeshRequested;
        }

        private readonly List<BodyVisual> _bodies = new();
        private CelestialSystem _system;
        private ShaderMaterial _skyMaterial;
        private ShaderMaterial _planetMaterial;

        public static ScaledSpace Create(CelestialSystem system)
        {
            var s = new ScaledSpace { Name = "ScaledSpace", _system = system };
            s.Viewport = new SubViewport
            {
                Name = "ScaledViewport",
                OwnWorld3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Msaa3D = Godot.Viewport.Msaa.Disabled,
                TransparentBg = false,
            };
            s.AddChild(s.Viewport);
            s.Root = new Node3D { Name = "Root" };
            s.Viewport.AddChild(s.Root);
            s.Camera = new Camera3D { Name = "ScaledCamera", Near = NearPlane, Far = FarPlane, Current = true };
            s.Viewport.AddChild(s.Camera);

            s._skyMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/starfield.gdshader") };
            s.Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = s._skyMaterial, RadianceSize = Sky.RadianceSizeEnum.Size64 },
                AmbientLightSource = Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.04f, 0.045f, 0.06f),
                AmbientLightEnergy = 1.0f,
                TonemapMode = Environment.ToneMapper.Filmic,
                GlowEnabled = true,
                GlowIntensity = 0.6f,
                GlowBloom = 0.05f,
            };
            s.Viewport.AddChild(new WorldEnvironment { Environment = s.Environment });
            s.Sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.6f, ShadowEnabled = false };
            s.Viewport.AddChild(s.Sun);

            s._planetMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
            s._planetMaterial.SetShaderParameter("detail_strength", 0.0f);

            foreach (CelestialBody b in system.Bodies) s._bodies.Add(s.CreateBody(b));
            return s;
        }

        private BodyVisual CreateBody(CelestialBody b)
        {
            var bv = new BodyVisual { Body = b, Node = new Node3D { Name = b.Id } };
            Root.AddChild(bv.Node);
            float r = (float)(b.Radius / UnitMeters);
            if (b.IsStar)
            {
                var sphere = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = r, Height = 2 * r, RadialSegments = 48, Rings = 24 },
                    MaterialOverride = MaterialLibrary.Unshaded(b.Color.ToColor() * 6.0f),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                bv.Node.AddChild(sphere);
                var glowMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sun_glow.gdshader") };
                glowMat.SetShaderParameter("color", b.Color.ToColor());
                bv.Glow = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(1, 1) }, MaterialOverride = glowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                Root.AddChild(bv.Glow);
                return bv;
            }

            bv.Surface = new Node3D { Name = "Surface" };
            bv.Node.AddChild(bv.Surface);
            // Placeholder sphere until the detailed scaled mesh is generated.
            var placeholder = new MeshInstance3D
            {
                Name = "Placeholder",
                Mesh = new SphereMesh { Radius = r, Height = 2 * r, RadialSegments = 32, Rings = 16 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = b.Color.ToColor(), Roughness = 0.9f },
            };
            bv.Surface.AddChild(placeholder);

            if (b.Atmosphere != null)
            {
                float ra = (float)((b.Radius + b.Atmosphere.Height) / UnitMeters);
                bv.AtmosphereMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/atmosphere.gdshader") };
                bv.AtmosphereMaterial.SetShaderParameter("planet_radius", r);
                bv.AtmosphereMaterial.SetShaderParameter("atmo_radius", ra);
                bv.AtmosphereMaterial.SetShaderParameter("scale_height", (float)(b.Atmosphere.ScaleHeight / UnitMeters));
                bv.AtmosphereMaterial.SetShaderParameter("sky_color", b.Atmosphere.SkyColor.ToColor());
                bv.AtmosphereMaterial.SetShaderParameter("density", (float)(b.Atmosphere.SeaLevelPressure / 101325.0 * 1.0 + 0.15));
                bv.AtmosphereMaterial.SetShaderParameter("intensity", 2.2f);
                bv.Atmosphere = new MeshInstance3D
                {
                    Name = "Atmosphere",
                    Mesh = new SphereMesh { Radius = ra, Height = 2 * ra, RadialSegments = 48, Rings = 24 },
                    MaterialOverride = bv.AtmosphereMaterial,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                bv.Node.AddChild(bv.Atmosphere);
            }
            return bv;
        }

        private void RequestPlanetMesh(BodyVisual bv)
        {
            bv.MeshRequested = true;
            CelestialBody b = bv.Body;
            Task.Run(() =>
            {
                var patches = new TerrainPatchData[6 * 4];
                int k = 0;
                for (int f = 0; f < 6; f++)
                for (int q = 0; q < 4; q++)
                {
                    double u0 = (q % 2) * 0.5, v0 = (q / 2) * 0.5;
                    patches[k++] = TerrainPatch.Generate(b, f, u0, v0, 0.5, 32, 1.0 / UnitMeters, false, false);
                }
                return patches;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    GD.PushError($"Scaled mesh generation failed for {b.Id}: {t.Exception}");
                    return;
                }
                Callable.From(() => ApplyPlanetMesh(bv, t.Result)).CallDeferred();
            });
        }

        private void ApplyPlanetMesh(BodyVisual bv, TerrainPatchData[] patches)
        {
            if (!IsInstanceValid(this) || bv.Surface == null || !IsInstanceValid(bv.Surface)) return;
            foreach (Node c in bv.Surface.GetChildren()) c.QueueFree();
            foreach (TerrainPatchData p in patches)
                bv.Surface.AddChild(new MeshInstance3D { Mesh = TerrainPatch.ToMesh(p), MaterialOverride = _planetMaterial });
        }

        public override void _Ready()
        {
            foreach (BodyVisual bv in _bodies)
                if (!bv.Body.IsStar && !bv.MeshRequested) RequestPlanetMesh(bv);
        }

        /// <summary>Matches the background resolution to the window.</summary>
        public void Resize(Vector2I size)
        {
            if (Viewport.Size != size) Viewport.Size = size;
        }

        /// <summary>
        /// Positions everything relative to <paramref name="originAbsolute"/> (root-relative metres) and points the
        /// scaled camera like the main camera. <paramref name="cameraOffset"/> is the main camera position relative
        /// to the origin, in metres.
        /// </summary>
        /// <summary>Distance beyond which bodies are compressed (see <see cref="CompressDistance"/>). The map view changes it.</summary>
        public double Compress { get; set; } = CompressDistance;

        public void Sync(double time, Vector3d originAbsolute, Vector3d cameraOffset, Basis cameraBasis, float fov)
        {
            Camera.Fov = fov;
            Camera.Near = NearPlane;
            Camera.Far = FarPlane;
            Compress = CompressDistance;
            Camera.Transform = new Transform3D(cameraBasis, (cameraOffset / UnitMeters).ToGodot());
            SyncBodies(time, originAbsolute, (cameraOffset / UnitMeters).ToGodot());
        }

        /// <summary>Places bodies for an externally controlled camera (map view).</summary>
        public void SyncBodies(double time, Vector3d originAbsolute, Vector3 cameraScaledPosition)
        {
            Vector3d starAbs = _system.Root.AbsolutePosition(time);
            Vector3d sunDir = (starAbs - originAbsolute).Normalized(); // towards the star
            if (sunDir.LengthSquared < 0.5) sunDir = Vector3d.UnitX;
            Sun.Basis = Basis.LookingAt(-sunDir.ToGodot(), Mathf.Abs((float)sunDir.Y) > 0.99f ? Vector3.Right : Vector3.Up);

            foreach (BodyVisual bv in _bodies)
            {
                Vector3d rel = (bv.Body.AbsolutePosition(time) - originAbsolute) / UnitMeters;
                Vector3d camRel = rel - cameraScaledPosition.ToCore();
                double dist = camRel.Length;
                double k = dist > Compress ? Compress / dist : 1.0;
                Vector3 pos = (cameraScaledPosition.ToCore() + camRel * k).ToGodot();
                bv.Node.Position = pos;
                bv.Node.Scale = Vector3.One * (float)k;
                if (!bv.Body.IsStar) bv.Surface.Basis = new Basis(bv.Body.RotationAt(time).ToGodot());
                if (bv.AtmosphereMaterial != null)
                {
                    bv.AtmosphereMaterial.SetShaderParameter("planet_center", pos);
                    bv.AtmosphereMaterial.SetShaderParameter("planet_radius", (float)(bv.Body.Radius / UnitMeters * k));
                    bv.AtmosphereMaterial.SetShaderParameter("atmo_radius", (float)((bv.Body.Radius + bv.Body.AtmosphereHeight) / UnitMeters * k));
                    bv.AtmosphereMaterial.SetShaderParameter("scale_height", (float)(bv.Body.Atmosphere.ScaleHeight / UnitMeters * k));
                    Vector3d bodySun = (starAbs - bv.Body.AbsolutePosition(time)).Normalized();
                    bv.AtmosphereMaterial.SetShaderParameter("sun_dir", bodySun.ToGodot());
                }
                if (bv.Glow != null)
                {
                    float d = (pos - cameraScaledPosition).Length();
                    bv.Glow.Position = pos;
                    bv.Glow.Scale = Vector3.One * d * 0.35f;
                }
            }
        }

        /// <summary>0 in space, up to 1 in daylight inside a thick atmosphere (fades the stars).</summary>
        public void SetSkyBrightness(float b) => _skyMaterial.SetShaderParameter("sky_brightness", b);

        public Vector3 ScaledPosition(Vector3d absolute, Vector3d originAbsolute) => ((absolute - originAbsolute) / UnitMeters).ToGodot();
    }
}
