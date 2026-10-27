namespace OSP.Game.Rendering;

/// <summary>Maps the Low/Medium/High graphics preset onto concrete rendering parameters.</summary>
public static class Quality
{
    public static double TerrainSplit(int q) => q switch { 0 => 1.6, 1 => 2.2, _ => 3.0 };
    public static int AtmosphereSteps(int q) => q switch { 0 => 6, 1 => 10, _ => 16 };
    public static bool Shadows(int q) => q >= 1;
    public static float ShadowDistance(int q) => q switch { 0 => 80f, 1 => 220f, _ => 450f };
}
