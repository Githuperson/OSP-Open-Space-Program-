using Godot;
using OSP.Core.Celestial;
using OSP.Core.Math;
using OSP.Game.Rendering;

namespace OSP.Game.Flight
{
    /// <summary>
    /// Procedural launch complex at the home body's launch site: a concrete pad with flame trench, a service
    /// tower, floodlight masts and a few outbuildings. Positioned in body-fixed coordinates.
    /// </summary>
    public partial class LaunchSite : Node3D
    {
        private CelestialBody _body;
        private Vector3d _bodyFixedPosition;
        private QuaternionD _bodyFixedRotation;

        public static LaunchSite Create(CelestialSystem system)
        {
            var site = new LaunchSite { Name = "LaunchSite", _body = system.HomeBody };
            FlatSite s = system.LaunchSite;
            Vector3d up = CelestialBody.DirectionFromLatLon(s.Latitude, s.Longitude);
            Vector3d east = Vector3d.Cross(Vector3d.UnitY, up).Normalized();
            Vector3d north = Vector3d.Cross(up, east);
            site._bodyFixedRotation = QuaternionD.FromBasis(east, up, -north);
            site._bodyFixedPosition = up * (system.HomeBody.Radius + system.HomeBody.SurfaceHeight(up));
            site.Build();
            return site;
        }

        private void Box(Vector3 size, Vector3 pos, string mat, Vector3 rot = default)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Mat(mat), Position = pos, RotationDegrees = rot };
            AddChild(mi);
        }

        private static StandardMaterial3D _concrete, _metal, _red, _light, _asphalt;
        private static Material Mat(string m) => m switch
        {
            "concrete" => _concrete ??= new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.53f), Roughness = 0.95f },
            "asphalt" => _asphalt ??= new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.9f },
            "metal" => _metal ??= new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.36f, 0.38f), Metallic = 0.7f, Roughness = 0.5f },
            "red" => _red ??= new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.15f, 0.1f), Roughness = 0.6f },
            "light" => _light ??= new StandardMaterial3D { AlbedoColor = new Color(1, 0.95f, 0.8f), EmissionEnabled = true, Emission = new Color(1, 0.9f, 0.7f), EmissionEnergyMultiplier = 3 },
            _ => MaterialLibrary.Get(m),
        };

        private void Build()
        {
            // Pad slab (top surface at y = 0 is the terrain level; it extends below to hide the ground).
            Box(new Vector3(36, 3, 36), new Vector3(0, -1.55f, 0), "concrete");
            Box(new Vector3(70, 1, 70), new Vector3(0, -2.6f, 0), "asphalt");
            // Flame trench.
            Box(new Vector3(6, 0.2f, 22), new Vector3(0, -0.02f, 9), "asphalt");
            // Launch ring around the rocket.
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.Tau / 8;
                Box(new Vector3(0.8f, 0.6f, 0.8f), new Vector3(3.2f * Mathf.Cos(a), 0.3f, 3.2f * Mathf.Sin(a)), "metal");
            }
            // Service tower: four legs, cross bracing, platforms.
            Vector3 towerBase = new(-9, 0, 0);
            float h = 34;
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1.5f : 1.5f), z = (i < 2 ? -1.5f : 1.5f);
                Box(new Vector3(0.4f, h, 0.4f), towerBase + new Vector3(x, h / 2, z), "red");
            }
            for (float y = 3; y < h; y += 4)
            {
                Box(new Vector3(3.4f, 0.25f, 3.4f), towerBase + new Vector3(0, y, 0), "metal");
                Box(new Vector3(0.15f, 4.6f, 0.15f), towerBase + new Vector3(-1.5f, y + 2, 0), "red", new Vector3(45, 0, 0));
                Box(new Vector3(0.15f, 4.6f, 0.15f), towerBase + new Vector3(1.5f, y + 2, 0), "red", new Vector3(-45, 0, 0));
            }
            // Swing arms reaching towards the vehicle.
            Box(new Vector3(6, 0.4f, 0.8f), towerBase + new Vector3(4.2f, 12, 0), "metal");
            Box(new Vector3(6, 0.4f, 0.8f), towerBase + new Vector3(4.2f, 22, 0), "metal");
            // Floodlight masts.
            for (int i = 0; i < 4; i++)
            {
                float a = Mathf.Pi / 4 + i * Mathf.Tau / 4;
                Vector3 p = new(24 * Mathf.Cos(a), 0, 24 * Mathf.Sin(a));
                Box(new Vector3(0.35f, 20, 0.35f), p + new Vector3(0, 10, 0), "metal");
                Box(new Vector3(1.6f, 0.8f, 0.4f), p + new Vector3(0, 20, 0), "light");
            }
            // Outbuildings: hangar and control bunker.
            Box(new Vector3(30, 14, 22), new Vector3(-60, 6, -40), "concrete");
            Box(new Vector3(31, 1, 23), new Vector3(-60, 13.5f, -40), "metal");
            Box(new Vector3(14, 5, 10), new Vector3(55, 1.5f, 35), "concrete");
            Box(new Vector3(70, 0.15f, 8), new Vector3(-25, -0.04f, -20), "asphalt", new Vector3(0, 30, 0));
        }

        /// <summary>Positions the complex. <paramref name="bodyOffset"/> = body centre − floating origin.</summary>
        public void Sync(Vector3d bodyOffset, QuaternionD bodyRotation)
        {
            Vector3d p = bodyOffset + bodyRotation.Rotate(_bodyFixedPosition);
            QuaternionD r = (bodyRotation * _bodyFixedRotation).Normalized();
            Transform = new Transform3D(new Basis(r.ToGodot()), p.ToGodot());
        }

        public CelestialBody Body => _body;
    }
}
