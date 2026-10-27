using System.Collections.Generic;
using Godot;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// Shared physically-based materials referenced by name from part definitions. Materials are created once and
    /// reused by every part instance (keeps draw-call state changes and memory low).
    /// </summary>
    public static class MaterialLibrary
    {
        private static readonly Dictionary<string, Material> Cache = new();
        private static StandardMaterial3D _highlight;
        private static StandardMaterial3D _invalid;
        private static StandardMaterial3D _ghost;

        public static Material Get(string name)
        {
            name ??= "white";
            if (Cache.TryGetValue(name, out Material m)) return m;
            m = Create(name);
            Cache[name] = m;
            return m;
        }

        private static StandardMaterial3D Pbr(Color albedo, float metallic, float roughness, bool doubleSided = false)
        {
            var m = new StandardMaterial3D
            {
                AlbedoColor = albedo,
                Metallic = metallic,
                Roughness = roughness,
                MetallicSpecular = 0.5f,
            };
            if (doubleSided) m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            return m;
        }

        private static Material Create(string name)
        {
            switch (name)
            {
                case "white": return Pbr(new Color(0.88f, 0.88f, 0.86f), 0.0f, 0.42f);
                case "lightgray": return Pbr(new Color(0.62f, 0.63f, 0.64f), 0.2f, 0.45f);
                case "darkgray": return Pbr(new Color(0.17f, 0.18f, 0.19f), 0.35f, 0.5f);
                case "black": return Pbr(new Color(0.04f, 0.04f, 0.045f), 0.1f, 0.6f);
                case "steel": return Pbr(new Color(0.58f, 0.58f, 0.6f), 0.9f, 0.35f);
                case "chrome": return Pbr(new Color(0.9f, 0.9f, 0.92f), 1.0f, 0.08f);
                case "copper": return Pbr(new Color(0.82f, 0.48f, 0.3f), 0.9f, 0.35f);
                case "gold": return Pbr(new Color(1.0f, 0.76f, 0.33f), 1.0f, 0.28f);
                case "orange": return Pbr(new Color(0.78f, 0.38f, 0.12f), 0.0f, 0.82f);
                case "red": return Pbr(new Color(0.72f, 0.1f, 0.08f), 0.0f, 0.45f);
                case "yellow": return Pbr(new Color(0.95f, 0.72f, 0.08f), 0.0f, 0.45f);
                case "glass": return Pbr(new Color(0.06f, 0.1f, 0.16f), 0.3f, 0.05f);
                case "heatshield": return Pbr(new Color(0.22f, 0.15f, 0.1f), 0.0f, 0.92f);
                case "solar": return Pbr(new Color(0.06f, 0.1f, 0.32f), 0.6f, 0.22f);
                case "bell": return Pbr(new Color(0.3f, 0.29f, 0.28f), 0.85f, 0.42f, doubleSided: true);
                case "canopy": return Pbr(new Color(0.95f, 0.48f, 0.12f), 0.0f, 0.8f, doubleSided: true);
                default:
                    GD.PushWarning($"Unknown material '{name}', using magenta.");
                    return Pbr(new Color(1, 0, 1), 0, 0.5f);
            }
        }

        /// <summary>Additive overlay used for selected/hovered parts in the editor.</summary>
        public static StandardMaterial3D Highlight => _highlight ??= Overlay(new Color(0.2f, 0.6f, 1.0f, 0.35f));
        /// <summary>Overlay for an attachment that would be invalid.</summary>
        public static StandardMaterial3D Invalid => _invalid ??= Overlay(new Color(1.0f, 0.15f, 0.1f, 0.45f));
        /// <summary>Overlay for a part being placed.</summary>
        public static StandardMaterial3D Ghost => _ghost ??= Overlay(new Color(0.3f, 1.0f, 0.4f, 0.25f));

        private static StandardMaterial3D Overlay(Color c) => new()
        {
            AlbedoColor = c,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            NoDepthTest = false,
        };

        public static StandardMaterial3D Unshaded(Color c, bool transparent = false, bool noDepth = false)
        {
            var m = new StandardMaterial3D
            {
                AlbedoColor = c,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = noDepth,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            if (transparent || c.A < 1) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            return m;
        }
    }
}
