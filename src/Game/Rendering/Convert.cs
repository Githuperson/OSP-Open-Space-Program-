using Godot;
using OSP.Core.Math;

namespace OSP.Game.Rendering
{
    /// <summary>
    /// Conversions between the double-precision simulation types and Godot's single-precision types. The two frames
    /// share axes (right-handed, +Y up), so conversion is a plain component cast. Only convert values that are already
    /// relative to the floating origin (small magnitudes) to avoid losing precision.
    /// </summary>
    public static class Convert
    {
        public static Vector3 ToGodot(this Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        public static Vector3d ToCore(this Vector3 v) => new(v.X, v.Y, v.Z);
        public static Quaternion ToGodot(this QuaternionD q) => new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W).Normalized();
        public static QuaternionD ToCore(this Quaternion q) => new QuaternionD(q.X, q.Y, q.Z, q.W).Normalized();
        public static Color ToColor(this double[] c, float alpha = 1f) =>
            c == null || c.Length < 3 ? new Color(1, 0, 1, alpha) : new Color((float)c[0], (float)c[1], (float)c[2], alpha);
    }
}
