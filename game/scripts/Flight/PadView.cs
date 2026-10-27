using Godot;
using OSP.Core.Bodies;
using OSP.Core.Mathx;
using OSP.Game.Rendering;

namespace OSP.Game.Flight;

/// <summary>Launch pad (concrete apron, flame trench ring, service tower, lamps) fixed to the surface at a launch site.</summary>
public sealed partial class PadView : Node3D
{
    private readonly Vec3d _localCenter;
    private readonly QuatD _localOrientation;
    private readonly CelestialBody _body;

    public PadView(CelestialBody body, LaunchSiteDef site)
    {
        _body = body;
        double lat = site.LatitudeDeg * MathUtil.Deg2Rad, lon = site.LongitudeDeg * MathUtil.Deg2Rad;
        var dir = CelestialBody.DirectionFromLatLon(lat, lon);
        var east = Vec3d.Cross(Vec3d.UnitY, dir).Normalized;
        var north = Vec3d.Cross(dir, east).Normalized;
        _localOrientation = QuatD.FromBasis(north, dir, east);
        _localCenter = dir * (body.Radius + body.SurfaceHeightAt(dir));
        Name = "Pad_" + site.Id;

        var pad = Materials.Get("pad"); var steel = Materials.Get("steel"); var dark = Materials.Get("dark");
        void Add(Mesh mesh, Material mat, Vector3 pos, bool shadow = true) =>
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off });
        Add(new CylinderMesh { TopRadius = 16, BottomRadius = 17, Height = 0.5f, RadialSegments = 48 }, pad, new Vector3(0, -0.25f + 0.02f, 0), false);
        Add(new CylinderMesh { TopRadius = 4.2f, BottomRadius = 4.2f, Height = 0.1f, RadialSegments = 32 }, dark, new Vector3(0, 0.06f, 0), false);
        // service tower to the north of the rocket
        float tx = 7.5f;
        for (int i = 0; i < 4; i++)
        {
            float sx = (i % 2 == 0 ? -1 : 1) * 0.9f, sz = (i < 2 ? -1 : 1) * 0.9f;
            Add(MeshFactory.Box(new Vector3(0.25f, 38, 0.25f)), steel, new Vector3(tx + sx, 19, sz));
        }
        for (int k = 1; k <= 12; k++)
        {
            float y = k * 3.0f;
            Add(MeshFactory.Box(new Vector3(2.1f, 0.18f, 0.18f)), steel, new Vector3(tx, y, 0.9f));
            Add(MeshFactory.Box(new Vector3(2.1f, 0.18f, 0.18f)), steel, new Vector3(tx, y, -0.9f));
            Add(MeshFactory.Box(new Vector3(0.18f, 0.18f, 2.1f)), steel, new Vector3(tx + 0.9f, y, 0));
            Add(MeshFactory.Box(new Vector3(0.18f, 0.18f, 2.1f)), steel, new Vector3(tx - 0.9f, y, 0));
        }
        Add(MeshFactory.Box(new Vector3(3.0f, 0.3f, 3.0f)), steel, new Vector3(tx, 38.2f, 0));
        for (int i = 0; i < 4; i++)
        {
            double a = i * Mathf.Pi / 2 + 0.78;
            Add(MeshFactory.Box(new Vector3(0.5f, 8f, 0.5f)), dark, new Vector3((float)(Mathf.Cos(a) * 15), 4f, (float)(Mathf.Sin(a) * 15)));
        }
    }

    public void UpdateView(double time, Vec3d bodyRelOrigin, double renderScale, double maxDistance = 30000)
    {
        var rot = _body.RotationAt(time);
        var o = (bodyRelOrigin + rot.Rotate(_localCenter));
        Visible = o.Length < maxDistance && renderScale > 0.99;
        if (!Visible) return;
        var q = rot * _localOrientation;
        Transform = new Transform3D(new Basis(new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W)), new Vector3((float)o.X, (float)o.Y, (float)o.Z));
    }
}
