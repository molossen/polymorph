using System.Numerics;

namespace MoloMax.Core.Gta.Look;

// From NodeRunner, ported with the author's agreement.
// Repeating noise the clouds are carved from, made once on the processor:
// - Shape: a cube of ShapeSize³ texels, two bytes each: billows (Perlin noise eaten by cells, the round bulges of a
//   cumulus) and small cells (to fray the edges);
// - Weather: a square of WeatherSize² texels, two bytes each: where clouds gather (coverage) and their kind (flat
//   layers or tall heaps).
// Both wrap around without a seam.
public static class CloudNoise
{
    public const int ShapeSize = 64;
    public const int WeatherSize = 128;

    public static byte[] Shape()
    {
        const int n = ShapeSize;
        var texels = new byte[n * n * n * 2];
        Parallel.For(0, n, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector3(x, y, z) / n;
                    float perlin = Fbm3(p, 4, 4, 11);
                    float cells = Cells(p, 4, 21) * 0.625f + Cells(p, 8, 22) * 0.25f + Cells(p, 16, 23) * 0.125f;
                    // Perlin pushed up where the cells are dense: round billows.
                    float billows = Math.Clamp(Remap(perlin, cells - 1f, 1f, 0f, 1f), 0f, 1f);
                    float detail = Cells(p, 8, 31) * 0.625f + Cells(p, 16, 32) * 0.25f + Cells(p, 32, 33) * 0.125f;
                    int i = ((z * n + y) * n + x) * 2;
                    texels[i] = ToByte(billows);
                    texels[i + 1] = ToByte(detail);
                }
        });
        return texels;
    }

    public static byte[] Weather()
    {
        const int n = WeatherSize;
        var texels = new byte[n * n * 2];
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                var p = new Vector3(x, y, 0f) / n;
                int i = (y * n + x) * 2;
                texels[i] = ToByte(Fbm3(p, 3, 5, 41) * 1.3f - 0.15f);
                texels[i + 1] = ToByte(Fbm3(p, 2, 3, 51));
            }
        });
        return texels;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static float Remap(float v, float from0, float from1, float to0, float to1) =>
        to0 + (v - from0) / (from1 - from0) * (to1 - to0);

    // Sum of octaves of repeating gradient noise from period cells across, 0 to 1.
    private static float Fbm3(Vector3 p, int period, int octaves, uint seed)
    {
        float sum = 0f, amplitude = 0.5f, total = 0f;
        for (int o = 0; o < octaves; o++)
        {
            int cells = period << o;
            sum += Gradient(p * cells, cells, seed + (uint)o) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
        }
        return Math.Clamp(sum / total * 0.75f + 0.5f, 0f, 1f);
    }

    // Gradient noise repeating every period cells, about -1 to 1.
    private static float Gradient(Vector3 p, int period, uint seed)
    {
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        var f = p - new Vector3(x0, y0, z0);
        var u = f * f * f * (f * (f * 6f - new Vector3(15f)) + new Vector3(10f));
        float Corner(int dx, int dy, int dz)
        {
            uint h = Hash(Wrap(x0 + dx, period), Wrap(y0 + dy, period), Wrap(z0 + dz, period), seed);
            var g = GradientOf(h);
            return Vector3.Dot(g, f - new Vector3(dx, dy, dz));
        }
        float x00 = float.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), u.X);
        float x10 = float.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), u.X);
        float x01 = float.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), u.X);
        float x11 = float.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), u.X);
        return float.Lerp(float.Lerp(x00, x10, u.Y), float.Lerp(x01, x11, u.Y), u.Z);
    }

    private static readonly Vector3[] Gradients =
    [
        new(1, 1, 0), new(-1, 1, 0), new(1, -1, 0), new(-1, -1, 0), new(1, 0, 1), new(-1, 0, 1),
        new(1, 0, -1), new(-1, 0, -1), new(0, 1, 1), new(0, -1, 1), new(0, 1, -1), new(0, -1, -1),
        new(1, 1, 0), new(-1, 1, 0), new(0, -1, 1), new(0, -1, -1),
    ];

    private static Vector3 GradientOf(uint hash) => Gradients[hash & 15];

    // Cells: one random point per cell (period cells across, repeating); 1 on a point, 0 far from any.
    private static float Cells(Vector3 p, int period, uint seed)
    {
        var q = p * period;
        int cx = (int)MathF.Floor(q.X), cy = (int)MathF.Floor(q.Y), cz = (int)MathF.Floor(q.Z);
        float nearest = float.MaxValue;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    uint h = Hash(Wrap(x, period), Wrap(y, period), Wrap(z, period), seed);
                    var point = new Vector3(x + Unit(h), y + Unit(h * 747796405u + 1u), z + Unit(h * 2891336453u + 7u));
                    nearest = MathF.Min(nearest, Vector3.DistanceSquared(point, q));
                }
        return 1f - Math.Clamp(MathF.Sqrt(nearest), 0f, 1f);
    }

    private static int Wrap(int v, int period) => ((v % period) + period) % period;

    private static float Unit(uint h)
    {
        h ^= h >> 16;
        h *= 0x7feb352du;
        h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }

    private static uint Hash(int x, int y, int z, uint seed)
    {
        uint h = seed * 0x9E3779B9u;
        h ^= (uint)x * 0x85EBCA6Bu;
        h = (h << 13 | h >> 19) * 5u + 0xE6546B64u;
        h ^= (uint)y * 0xC2B2AE35u;
        h = (h << 13 | h >> 19) * 5u + 0xE6546B64u;
        h ^= (uint)z * 0x27D4EB2Fu;
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        return h;
    }
}
