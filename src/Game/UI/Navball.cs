using System.Collections.Generic;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Math;
using OSP.Game.Rendering;

namespace OSP.Game.UI
{
    /// <summary>
    /// Attitude indicator. A textured sphere (sky/ground with pitch and heading lines) is rendered in its own small
    /// viewport and rotated so its centre shows where the vessel's nose points relative to the local horizon
    /// (east-up-north frame). Velocity and manoeuvre markers are projected on top in 2D.
    /// </summary>
    public partial class Navball : Control
    {
        private SubViewport _vp;
        private MeshInstance3D _ball;
        private TextureRect _view;
        private Basis _ballBasis = Basis.Identity;
        private readonly List<(Vector3 screen, Color color, string kind)> _markers = new();

        public double Heading { get; private set; }
        public double PitchDeg { get; private set; }
        public double RollDeg { get; private set; }
        public float BallSize { get; set; } = 190;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(BallSize, BallSize);
            MouseFilter = MouseFilterEnum.Ignore;
            _vp = new SubViewport { Size = new Vector2I(256, 256), TransparentBg = true, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
            AddChild(_vp);
            var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 2.04f, Position = new Vector3(0, 0, 3), Current = true };
            _vp.AddChild(cam);
            _vp.AddChild(new DirectionalLight3D { Basis = Basis.LookingAt(new Vector3(-0.3f, -0.4f, -1), Vector3.Up), LightEnergy = 1.1f });
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.ClearColor, BackgroundColor = new Color(0, 0, 0, 0), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.55f) };
            _vp.AddChild(new WorldEnvironment { Environment = env });
            _ball = new MeshInstance3D { Mesh = BuildBallMesh(), MaterialOverride = new StandardMaterial3D { AlbedoTexture = BuildTexture(), Roughness = 0.6f, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps, CullMode = BaseMaterial3D.CullModeEnum.Disabled } };
            _vp.AddChild(_ball);
            _view = new TextureRect { Texture = _vp.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            _view.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_view);
        }

        /// <summary>Navball-local frame: x = east, y = up, z = south (matches a vessel standing on the pad).</summary>
        private static Mesh BuildBallMesh()
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            int seg = 64, rings = 32;
            for (int j = 0; j < rings; j++)
            for (int i = 0; i < seg; i++)
            {
                void V(int ii, int jj)
                {
                    float lat = Mathf.Pi / 2 - Mathf.Pi * jj / rings;  // +90 .. -90
                    float hdg = Mathf.Tau * ii / seg;                   // 0 = north, increasing towards east
                    var d = new Vector3(Mathf.Cos(lat) * Mathf.Sin(hdg), Mathf.Sin(lat), -Mathf.Cos(lat) * Mathf.Cos(hdg));
                    st.SetNormal(d);
                    st.SetUV(new Vector2((float)ii / seg, (float)jj / rings));
                    st.AddVertex(d);
                }
                // (i, j) → (i+1, j) → (i+1, j+1) is counter-clockwise from outside; emit clockwise for Godot.
                V(i, j); V(i + 1, j + 1); V(i + 1, j);
                V(i, j); V(i, j + 1); V(i + 1, j + 1);
            }
            return st.Commit();
        }

        private static ImageTexture BuildTexture()
        {
            int w = 1024, h = 512;
            var img = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
            var sky = new Color(0.22f, 0.48f, 0.82f);
            var ground = new Color(0.55f, 0.33f, 0.14f);
            for (int y = 0; y < h; y++)
            {
                float lat = 90 - 180f * y / h;
                for (int x = 0; x < w; x++)
                {
                    float hdg = 360f * x / w;
                    Color c = lat >= 0 ? sky.Lerp(sky * 0.75f, lat / 90f) : ground.Lerp(ground * 0.7f, -lat / 90f);
                    float latMod = Mathf.Abs(lat % 10f);
                    bool pitchLine = (latMod < 0.35f || latMod > 9.65f) && Mathf.Abs(lat) < 85;
                    bool majorPitch = Mathf.Abs(lat % 30f) < 0.5f || Mathf.Abs(lat % 30f) > 29.5f;
                    float hdgMod = hdg % 45f;
                    bool hdgLine = (hdgMod < 0.4f || hdgMod > 44.6f) && Mathf.Abs(lat) < 80;
                    if (Mathf.Abs(lat) < 0.6f) c = Colors.White;
                    else if (majorPitch && Mathf.Abs(lat) < 85) c = c.Lerp(Colors.White, 0.8f);
                    else if (pitchLine) c = c.Lerp(Colors.White, 0.45f);
                    else if (hdgLine) c = c.Lerp(Colors.White, 0.55f);
                    // Cardinal markers near the horizon.
                    if (Mathf.Abs(lat) < 6 && Mathf.Abs(lat) > 1.5f)
                    {
                        foreach ((float hd, Color col) in new[] { (0f, new Color(1, 0.3f, 0.3f)), (90f, new Color(1, 0.9f, 0.3f)), (180f, Colors.White), (270f, Colors.White) })
                            if (Mathf.Abs(Mathf.Wrap(hdg - hd, -180, 180)) < 2.2f) c = col;
                    }
                    img.SetPixel(x, y, c);
                }
            }
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }

        /// <summary>Updates the ball and markers from the vessel state.</summary>
        public void UpdateFromVessel(Vessel v, Vector3d referenceVelocity, Vector3d? maneuverDir, Vector3d? targetDir)
        {
            Vector3d up = v.Position.Normalized();
            Vector3d east = Vector3d.Cross(Vector3d.UnitY, up);
            if (east.LengthSquared < 1e-9) east = Vector3d.UnitX.ProjectOnPlane(up);
            east = east.Normalized();
            Vector3d north = Vector3d.Cross(up, east);
            Vector3d south = -north;
            Vector3d ToLocal(Vector3d w) => new(Vector3d.Dot(w, east), Vector3d.Dot(w, up), Vector3d.Dot(w, south));

            Vector3d xv = ToLocal(v.Rotation.Rotate(Vector3d.UnitX));
            Vector3d yv = ToLocal(v.Rotation.Rotate(Vector3d.UnitY));
            Vector3d zv = ToLocal(v.Rotation.Rotate(Vector3d.UnitZ));
            // Instrument axes: right = vessel +X, up = dorsal +Z, towards the viewer = nose (+Y). Like any attitude
            // indicator this is a mirror image of the sky, so the basis has determinant −1 (the ball is double-sided).
            var rows = new Basis(xv.ToGodot(), zv.ToGodot(), yv.ToGodot());
            _ballBasis = rows.Transposed();
            if (_ball != null) _ball.Basis = _ballBasis;

            Vector3d nose = v.Forward;
            PitchDeg = System.Math.Asin(MathUtil.Clamp(Vector3d.Dot(nose, up), -1, 1)) * MathUtil.Rad2Deg;
            Vector3d horiz = nose.ProjectOnPlane(up);
            if (horiz.LengthSquared < 1e-8) horiz = v.Rotation.Rotate(-Vector3d.UnitZ).ProjectOnPlane(up); // vertical: the side W pitches towards
            Heading = MathUtil.WrapTwoPi(System.Math.Atan2(Vector3d.Dot(horiz, east), Vector3d.Dot(horiz, north))) * MathUtil.Rad2Deg;
            Vector3d top = v.Rotation.Rotate(Vector3d.UnitZ);
            Vector3d right = v.Rotation.Rotate(Vector3d.UnitX);
            RollDeg = System.Math.Atan2(-Vector3d.Dot(right, up), Vector3d.Dot(top, up)) * MathUtil.Rad2Deg;

            _markers.Clear();
            void Add(Vector3d worldDir, Color c, string kind)
            {
                if (worldDir.LengthSquared < 1e-12) return;
                _markers.Add((_ballBasis * ToLocal(worldDir.Normalized()).ToGodot(), c, kind));
            }
            if (referenceVelocity.Length > 0.1)
            {
                Add(referenceVelocity, new Color(0.9f, 0.9f, 0.2f), "prograde");
                Add(-referenceVelocity, new Color(0.9f, 0.9f, 0.2f), "retrograde");
            }
            ManeuverNode.OrbitalFrame(v.Position, v.Velocity, out _, out Vector3d nrm, out Vector3d rad);
            Add(nrm, new Color(0.8f, 0.3f, 0.9f), "normal");
            Add(-nrm, new Color(0.8f, 0.3f, 0.9f), "antinormal");
            Add(rad, new Color(0.3f, 0.85f, 0.95f), "radial");
            Add(-rad, new Color(0.3f, 0.85f, 0.95f), "radialin");
            if (maneuverDir.HasValue) Add(maneuverDir.Value, new Color(0.2f, 0.5f, 1.0f), "maneuver");
            if (targetDir.HasValue)
            {
                Add(targetDir.Value, new Color(0.95f, 0.45f, 0.9f), "target");
                Add(-targetDir.Value, new Color(0.95f, 0.45f, 0.9f), "antitarget");
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            Vector2 c = Size / 2;
            float r = Mathf.Min(Size.X, Size.Y) / 2;
            DrawArc(c, r, 0, Mathf.Tau, 64, new Color(0.15f, 0.16f, 0.18f), 4, true);
            // Fixed vessel reticle.
            Color ret = new(1.0f, 0.6f, 0.1f);
            DrawLine(c + new Vector2(-r * 0.32f, 0), c + new Vector2(-r * 0.1f, 0), ret, 3);
            DrawLine(c + new Vector2(r * 0.1f, 0), c + new Vector2(r * 0.32f, 0), ret, 3);
            DrawLine(c + new Vector2(-r * 0.1f, 0), c + new Vector2(0, r * 0.08f), ret, 3);
            DrawLine(c + new Vector2(r * 0.1f, 0), c + new Vector2(0, r * 0.08f), ret, 3);
            DrawCircle(c, 2.5f, ret);
            foreach (var (s, col, kind) in _markers)
            {
                if (s.Z < -0.05f) continue; // far side of the ball
                Vector2 p = c + new Vector2(s.X, -s.Y) * r * 0.98f;
                float alpha = Mathf.Clamp(s.Z * 4 + 0.6f, 0.3f, 1);
                Color cc = new(col, alpha);
                float m = r * 0.075f;
                switch (kind)
                {
                    case "prograde":
                        DrawArc(p, m, 0, Mathf.Tau, 20, cc, 2.5f, true);
                        DrawCircle(p, 2, cc);
                        DrawLine(p + new Vector2(0, -m), p + new Vector2(0, -m * 1.8f), cc, 2.5f);
                        DrawLine(p + new Vector2(m, 0), p + new Vector2(m * 1.8f, 0), cc, 2.5f);
                        DrawLine(p + new Vector2(-m, 0), p + new Vector2(-m * 1.8f, 0), cc, 2.5f);
                        break;
                    case "retrograde":
                        DrawArc(p, m, 0, Mathf.Tau, 20, cc, 2.5f, true);
                        DrawLine(p + new Vector2(-m * 0.7f, -m * 0.7f), p + new Vector2(m * 0.7f, m * 0.7f), cc, 2.5f);
                        DrawLine(p + new Vector2(-m * 0.7f, m * 0.7f), p + new Vector2(m * 0.7f, -m * 0.7f), cc, 2.5f);
                        DrawLine(p + new Vector2(0, m), p + new Vector2(0, m * 1.8f), cc, 2.5f);
                        break;
                    case "normal":
                    case "antinormal":
                    {
                        var tri = new[] { p + new Vector2(0, -m), p + new Vector2(m * 0.9f, m * 0.6f), p + new Vector2(-m * 0.9f, m * 0.6f), p + new Vector2(0, -m) };
                        DrawPolyline(tri, cc, 2.5f, true);
                        if (kind == "antinormal") DrawCircle(p, 2.5f, cc);
                        break;
                    }
                    case "radial":
                    case "radialin":
                        DrawArc(p, m * 0.6f, 0, Mathf.Tau, 16, cc, 2.5f, true);
                        for (int i = 0; i < 4; i++)
                        {
                            Vector2 d = Vector2.FromAngle(i * Mathf.Pi / 2 + Mathf.Pi / 4);
                            if (kind == "radial") DrawLine(p + d * m * 0.6f, p + d * m * 1.3f, cc, 2.5f);
                            else DrawLine(p + d * m * 1.4f, p + d * m * 0.8f, cc, 2.5f);
                        }
                        break;
                    case "maneuver":
                        DrawArc(p, m * 1.1f, 0, Mathf.Tau, 20, cc, 3f, true);
                        DrawCircle(p, m * 0.35f, cc);
                        break;
                    default:
                        DrawArc(p, m * 0.9f, 0, Mathf.Tau, 4, cc, 2.5f, true);
                        if (kind == "antitarget") DrawLine(p - new Vector2(m, 0), p + new Vector2(m, 0), cc, 2);
                        break;
                }
            }
        }
    }
}
