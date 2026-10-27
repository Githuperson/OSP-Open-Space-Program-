namespace OSP.Core.Celestial
{
    /// <summary>
    /// Deterministic 3D gradient noise (Perlin "improved noise") in double precision. The same function is
    /// evaluated by the terrain renderer and by terrain collision so they always agree.
    /// </summary>
    public sealed class Noise
    {
        private readonly int[] _perm = new int[512];

        public Noise(int seed)
        {
            int[] p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            // Deterministic Fisher-Yates using a simple LCG so results are identical on every platform.
            uint state = (uint)seed * 747796405u + 2891336453u;
            for (int i = 255; i > 0; i--)
            {
                state = state * 1664525u + 1013904223u;
                int j = (int)((state >> 8) % (uint)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static double Lerp(double t, double a, double b) => a + t * (b - a);

        private static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>Noise value in roughly [-1, 1].</summary>
        public double Sample(double x, double y, double z)
        {
            double fx = System.Math.Floor(x), fy = System.Math.Floor(y), fz = System.Math.Floor(z);
            int X = (int)((long)fx & 255), Y = (int)((long)fy & 255), Z = (int)((long)fz & 255);
            x -= fx;
            y -= fy;
            z -= fz;
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int[] p = _perm;
            int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
            int B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;
            return Lerp(w,
                Lerp(v, Lerp(u, Grad(p[AA], x, y, z), Grad(p[BA], x - 1, y, z)),
                        Lerp(u, Grad(p[AB], x, y - 1, z), Grad(p[BB], x - 1, y - 1, z))),
                Lerp(v, Lerp(u, Grad(p[AA + 1], x, y, z - 1), Grad(p[BA + 1], x - 1, y, z - 1)),
                        Lerp(u, Grad(p[AB + 1], x, y - 1, z - 1), Grad(p[BB + 1], x - 1, y - 1, z - 1))));
        }

        /// <summary>Fractal Brownian motion.</summary>
        public double Fbm(double x, double y, double z, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Sample(x, y, z);
                norm += amp;
                amp *= gain;
                x *= lacunarity;
                y *= lacunarity;
                z *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>Ridged multifractal noise in [0, 1], good for mountain ranges.</summary>
        public double Ridged(double x, double y, double z, int octaves)
        {
            double sum = 0, amp = 0.5, norm = 0, weight = 1;
            for (int i = 0; i < octaves; i++)
            {
                double n = 1.0 - System.Math.Abs(Sample(x, y, z));
                n *= n;
                n *= weight;
                weight = System.Math.Clamp(n * 2, 0, 1);
                sum += n * amp;
                norm += amp;
                amp *= 0.5;
                x *= 2.03;
                y *= 2.03;
                z *= 2.03;
            }
            return sum / norm;
        }
    }
}
