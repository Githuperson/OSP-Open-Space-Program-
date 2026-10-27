using OSP.Core.Mathx;

namespace OSP.Core.Bodies;

/// <summary>Deterministic seeded 3D gradient (Perlin) noise with fBm helpers. Thread-safe (immutable after construction).</summary>
public sealed class Noise3
{
    private readonly byte[] _perm = new byte[512];

    public Noise3(int seed)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        // Fisher-Yates with a tiny LCG so results are identical across platforms/runtimes.
        uint s = (uint)seed * 2654435761u + 12345u;
        for (int i = 255; i > 0; i--)
        {
            s = s * 1664525u + 1013904223u;
            int j = (int)((s >> 8) % (uint)(i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    /// <summary>Gradient noise in roughly [-1, 1].</summary>
    public double Sample(double x, double y, double z)
    {
        int X = (int)Math.Floor(x) & 255, Y = (int)Math.Floor(y) & 255, Z = (int)Math.Floor(z) & 255;
        x -= Math.Floor(x); y -= Math.Floor(y); z -= Math.Floor(z);
        double u = Fade(x), v = Fade(y), w = Fade(z);
        int A = _perm[X] + Y, AA = _perm[A] + Z, AB = _perm[A + 1] + Z;
        int B = _perm[X + 1] + Y, BA = _perm[B] + Z, BB = _perm[B + 1] + Z;
        double l(double a, double b, double t) => a + t * (b - a);
        return l(
            l(l(Grad(_perm[AA], x, y, z), Grad(_perm[BA], x - 1, y, z), u),
              l(Grad(_perm[AB], x, y - 1, z), Grad(_perm[BB], x - 1, y - 1, z), u), v),
            l(l(Grad(_perm[AA + 1], x, y, z - 1), Grad(_perm[BA + 1], x - 1, y, z - 1), u),
              l(Grad(_perm[AB + 1], x, y - 1, z - 1), Grad(_perm[BB + 1], x - 1, y - 1, z - 1), u), v), w);
    }

    public double Fbm(Vec3d p, int octaves, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0;
        double fx = p.X, fy = p.Y, fz = p.Z;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Sample(fx, fy, fz);
            norm += amp;
            amp *= gain;
            fx *= lacunarity; fy *= lacunarity; fz *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>Ridged multifractal in [0, 1]; sharp crests suitable for mountain ranges.</summary>
    public double Ridged(Vec3d p, int octaves, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0;
        double fx = p.X, fy = p.Y, fz = p.Z;
        double weight = 1;
        for (int i = 0; i < octaves; i++)
        {
            double n = 1.0 - Math.Abs(Sample(fx, fy, fz));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2.0, 0, 1);
            sum += n * amp;
            norm += amp;
            amp *= gain;
            fx *= lacunarity; fy *= lacunarity; fz *= lacunarity;
        }
        return sum / norm;
    }
}
