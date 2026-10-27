using System.Collections.Generic;
using Godot;

namespace OSP.Game.Rendering;

/// <summary>Shared physically-based materials for spacecraft parts (one instance per name: cheap and consistent).</summary>
public static class Materials
{
    private static readonly Dictionary<string, StandardMaterial3D> Cache = new();

    public static StandardMaterial3D Get(string name)
    {
        if (Cache.TryGetValue(name, out var m)) return m;
        m = Create(name);
        Cache[name] = m;
        return m;
    }

    private static StandardMaterial3D Mat(Color c, float metallic, float rough, bool doubleSided = false)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = c, Metallic = metallic, Roughness = rough,
            CullMode = doubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        };
        return m;
    }

    private static StandardMaterial3D Create(string name)
    {
        switch (name)
        {
            case "white": return Mat(new Color(0.88f, 0.89f, 0.9f), 0.15f, 0.5f);
            case "grey": return Mat(new Color(0.45f, 0.47f, 0.5f), 0.3f, 0.5f);
            case "dark": return Mat(new Color(0.11f, 0.12f, 0.14f), 0.35f, 0.55f);
            case "orange": return Mat(new Color(0.95f, 0.45f, 0.08f), 0.2f, 0.5f);
            case "red": return Mat(new Color(0.78f, 0.12f, 0.1f), 0.2f, 0.5f);
            case "yellow": return Mat(new Color(0.95f, 0.78f, 0.12f), 0.2f, 0.5f);
            case "metal": return Mat(new Color(0.7f, 0.72f, 0.75f), 0.95f, 0.3f);
            case "engine":
                { var m = Mat(new Color(0.42f, 0.34f, 0.28f), 0.9f, 0.38f, true); return m; }
            case "gold": return Mat(new Color(0.9f, 0.7f, 0.25f), 1.0f, 0.28f);
            case "heatshield": return Mat(new Color(0.16f, 0.1f, 0.07f), 0.0f, 0.95f);
            case "glass":
                {
                    var m = Mat(new Color(0.12f, 0.3f, 0.55f), 0.6f, 0.08f);
                    m.EmissionEnabled = true; m.Emission = new Color(0.05f, 0.15f, 0.3f); m.EmissionEnergyMultiplier = 0.8f;
                    return m;
                }
            case "solar":
                {
                    var m = Mat(new Color(0.06f, 0.1f, 0.3f), 0.55f, 0.22f, true);
                    return m;
                }
            case "chute":
                {
                    var m = Mat(new Color(0.95f, 0.5f, 0.12f), 0.0f, 0.9f, true);
                    return m;
                }
            case "pad": return Mat(new Color(0.38f, 0.38f, 0.39f), 0.0f, 0.95f);
            case "steel": return Mat(new Color(0.55f, 0.57f, 0.6f), 0.8f, 0.45f);
            default: return Mat(new Color(0.8f, 0.2f, 0.8f), 0.0f, 0.6f);   // loud magenta = unknown material name
        }
    }
}
