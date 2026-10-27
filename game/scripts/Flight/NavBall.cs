using System;
using System.Collections.Generic;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Mathx;

namespace OSP.Game.Flight;

/// <summary>
/// Attitude indicator. A textured sphere in its own 3D viewport is rotated so the pilot-frame direction under the cursor is
/// the nose; markers for prograde/retrograde/normal/radial/manoeuvre are projected on top. All data comes from the simulation.
/// </summary>
public sealed partial class NavBall : Control
{
    public const int SizePx = 230;
    private readonly Node3D _ball;
    private readonly NavMarkers _markers = new();
    public double Pitch, Heading, Roll;

    public NavBall()
    {
        CustomMinimumSize = new Vector2(SizePx, SizePx);
        MouseFilter = MouseFilterEnum.Ignore;
        var container = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(SizePx, SizePx), MouseFilter = MouseFilterEnum.Ignore };
        container.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(container);
        var vp = new SubViewport { Size = new Vector2I(SizePx, SizePx), TransparentBg = true, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
        container.AddChild(vp);
        var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 2.1f, Position = new Vector3(0, 0, 3), Near = 0.1f, Far = 10f, Current = true };
        vp.AddChild(cam);
        var shader = GD.Load<Shader>("res://shaders/navball.gdshader");
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("ball_tex", MakeTexture());
        _ball = new Node3D();
        _ball.AddChild(new MeshInstance3D { Mesh = Rendering.MeshFactory.Sphere(1.0, 64, 32), MaterialOverride = mat });
        vp.AddChild(_ball);
        _markers.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_markers);
    }

    // ---------------------------------------------------------------------------------------------------- texture
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['N'] = new[] { "10001", "11001", "10101", "10011", "10001", "10001", "10001" },
        ['E'] = new[] { "11111", "10000", "11110", "10000", "10000", "10000", "11111" },
        ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
        ['W'] = new[] { "10001", "10001", "10001", "10101", "10101", "11011", "10001" },
    };

    private static ImageTexture MakeTexture()
    {
        const int W = 1024, H = 512;
        var img = Image.CreateEmpty(W, H, false, Image.Format.Rgb8);
        for (int y = 0; y < H; y++)
        {
            float lat = 90f - 180f * y / H;
            var c = lat >= 0 ? new Color(0.13f, 0.38f, 0.78f).Lerp(new Color(0.35f, 0.6f, 0.95f), 1f - lat / 90f)
                             : new Color(0.42f, 0.25f, 0.1f).Lerp(new Color(0.26f, 0.15f, 0.07f), -lat / 90f);
            for (int x = 0; x < W; x++) img.SetPixel(x, y, c);
        }
        void Hline(float lat, int thick, Color col)
        {
            int y0 = (int)((90f - lat) / 180f * H);
            for (int dy = -thick / 2; dy <= thick / 2; dy++)
                for (int x = 0; x < W; x++) { int yy = y0 + dy; if (yy >= 0 && yy < H) img.SetPixel(x, yy, col); }
        }
        for (int l = -80; l <= 80; l += 10)
        {
            if (l == 0) continue;
            Hline(l, l % 30 == 0 ? 3 : 1, new Color(1, 1, 1, 1) * (l % 30 == 0 ? 0.95f : 0.55f));
        }
        Hline(0, 5, Colors.White);
        // heading ticks
        for (int h = 0; h < 360; h += 10)
        {
            int x0 = (int)(h / 360f * W);
            int len = h % 30 == 0 ? 22 : 10;
            int yMid = H / 2;
            for (int dy = -len; dy <= len; dy++) for (int dx = -1; dx <= 1; dx++)
                img.SetPixel(((x0 + dx) % W + W) % W, Math.Clamp(yMid + dy, 0, H - 1), h == 0 ? new Color(1f, 0.25f, 0.2f) : Colors.White);
        }
        // N E S W letters (3x scale) above the horizon
        void Letter(char ch, int headingDeg)
        {
            var g = Font[ch];
            int cx = (int)(headingDeg / 360f * W) - 7 * 3 / 2 * 1;
            int cy = H / 2 - 38;
            for (int r = 0; r < 7; r++) for (int c = 0; c < 5; c++)
                if (g[r][c] == '1')
                    for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                        img.SetPixel(((cx + c * 4 + sx) % W + W) % W, cy + r * 4 + sy, ch == 'N' ? new Color(1f, 0.3f, 0.25f) : Colors.White);
        }
        Letter('N', 0); Letter('E', 90); Letter('S', 180); Letter('W', 270);
        // zenith / nadir markers
        return ImageTexture.CreateFromImage(img);
    }

    // ---------------------------------------------------------------------------------------------------- update
    public void SetAttitude(QuatD q, Vec3d position, List<(Vec3d dir, string kind)> markers)
    {
        var up = position.Normalized;
        var east = Vec3d.Cross(Vec3d.UnitY, up);
        if (east.LengthSquared < 1e-12) east = Vec3d.UnitX;
        east = east.Normalized;
        var north = Vec3d.Cross(up, east).Normalized;
        var south = -north;
        var right = q.Rotate(new Vec3d(-1, 0, 0));
        var upP = q.Rotate(new Vec3d(0, 0, -1));
        var back = q.Rotate(new Vec3d(0, -1, 0));
        Vector3 Col(Vec3d f) => new((float)Vec3d.Dot(right, f), (float)Vec3d.Dot(upP, f), (float)Vec3d.Dot(back, f));
        _ball.Basis = new Basis(Col(east), Col(up), Col(south));

        // attitude angles (pitch, heading, roll) for the numeric readout
        var nose = q.Rotate(Vec3d.UnitY);
        Pitch = Math.Asin(Math.Clamp(Vec3d.Dot(nose, up), -1, 1)) * MathUtil.Rad2Deg;
        double h = Math.Atan2(Vec3d.Dot(nose, east), Vec3d.Dot(nose, north)) * MathUtil.Rad2Deg;
        if (Math.Abs(Pitch) > 89.5) h = Math.Atan2(Vec3d.Dot(q.Rotate(new Vec3d(0, 0, 1)), east), Vec3d.Dot(q.Rotate(new Vec3d(0, 0, 1)), north)) * MathUtil.Rad2Deg;
        Heading = (h + 360) % 360;
        var hUp = up - nose * Vec3d.Dot(up, nose);
        if (hUp.LengthSquared < 1e-8) hUp = north - nose * Vec3d.Dot(north, nose);
        hUp = hUp.Normalized;
        Roll = Math.Atan2(Vec3d.Dot(Vec3d.Cross(upP, hUp), nose), Vec3d.Dot(upP, hUp)) * MathUtil.Rad2Deg;

        _markers.Set(right, upP, back, markers);
    }

    private sealed partial class NavMarkers : Control
    {
        private Vec3d _r, _u, _b;
        private List<(Vec3d dir, string kind)> _list = new();
        public NavMarkers() { MouseFilter = MouseFilterEnum.Ignore; }
        public void Set(Vec3d r, Vec3d u, Vec3d b, List<(Vec3d, string)> list) { _r = r; _u = u; _b = b; _list = list; QueueRedraw(); }

        public override void _Draw()
        {
            var c = new Vector2(SizePx / 2f, SizePx / 2f);
            float R = SizePx / 2f / 2.1f * 2f * 0.5f * 2.1f / 2f * 1.0f;   // ball radius in pixels = (SizePx/2.1)
            R = SizePx / 2.1f;
            // outer ring
            DrawArc(c, R + 1, 0, Mathf.Tau, 64, new Color(0.9f, 0.95f, 1f, 0.8f), 2f);
            foreach (var (dir, kind) in _list)
            {
                float x = (float)Vec3d.Dot(_r, dir), y = (float)Vec3d.Dot(_u, dir), z = (float)Vec3d.Dot(_b, dir);
                if (z > 0.02f) continue;            // behind the ball
                var p = c + new Vector2(x, -y) * R;
                DrawMarker(p, kind);
            }
            // nose marker (fixed at centre)
            var o = new Color(1f, 0.6f, 0.1f);
            DrawArc(c, 9, 0, Mathf.Tau, 20, o, 2.5f);
            DrawLine(c + new Vector2(-24, 0), c + new Vector2(-9, 0), o, 3f); DrawLine(c + new Vector2(9, 0), c + new Vector2(24, 0), o, 3f);
            DrawLine(c + new Vector2(0, -9), c + new Vector2(0, -2), o, 3f);
        }

        private void DrawMarker(Vector2 p, string kind)
        {
            Color green = new(0.55f, 1f, 0.35f), purple = new(0.8f, 0.45f, 1f), cyan = new(0.35f, 0.9f, 1f), blue = new(0.35f, 0.5f, 1f), mag = new(1f, 0.3f, 0.8f);
            switch (kind)
            {
                case "pro": DrawArc(p, 9, 0, Mathf.Tau, 20, green, 2.5f); DrawCircle(p, 2.5f, green); DrawLine(p + new Vector2(-15, 0), p + new Vector2(-9, 0), green, 2.5f); DrawLine(p + new Vector2(15, 0), p + new Vector2(9, 0), green, 2.5f); DrawLine(p + new Vector2(0, -15), p + new Vector2(0, -9), green, 2.5f); break;
                case "retro": DrawArc(p, 9, 0, Mathf.Tau, 20, green, 2.5f); DrawLine(p + new Vector2(-6, -6), p + new Vector2(6, 6), green, 2.5f); DrawLine(p + new Vector2(-6, 6), p + new Vector2(6, -6), green, 2.5f); break;
                case "normal": DrawColoredPolygon(new[] { p + new Vector2(0, -9), p + new Vector2(8, 7), p + new Vector2(-8, 7) }, purple); break;
                case "anti": DrawColoredPolygon(new[] { p + new Vector2(0, 9), p + new Vector2(8, -7), p + new Vector2(-8, -7) }, purple * 0.8f); DrawLine(p + new Vector2(-8, 0), p + new Vector2(8, 0), Colors.Black, 2f); break;
                case "rad_out": DrawArc(p, 8, 0, Mathf.Tau, 20, cyan, 2.5f); DrawLine(p + new Vector2(-5, 0), p + new Vector2(5, 0), cyan, 2.5f); DrawLine(p + new Vector2(0, -5), p + new Vector2(0, 5), cyan, 2.5f); break;
                case "rad_in": DrawArc(p, 8, 0, Mathf.Tau, 20, cyan, 2.5f); DrawLine(p + new Vector2(-5, 0), p + new Vector2(5, 0), cyan, 2.5f); break;
                case "node": DrawColoredPolygon(new[] { p + new Vector2(0, -10), p + new Vector2(9, 0), p + new Vector2(0, 10), p + new Vector2(-9, 0) }, blue); break;
                case "target": DrawArc(p, 9, 0, Mathf.Tau, 20, mag, 2.5f); DrawCircle(p, 3f, mag); break;
            }
        }
    }
}
