using System.Numerics;

namespace MoloMax.Core.Gta.Look;

/// <summary>
/// Light of the sky worked out from the air (single scattering): sunlight scattered by the air (Rayleigh: blue
/// overhead) and by the haze (Mie: the bright halo around the sun), dimmed along its way (red sunsets).
/// </summary>
public static class Atmosphere
{
    const float EarthRadius = 6360000f, AirRadius = 6420000f, RayleighHeight = 7994f, MieHeight = 1200f, MieG = 0.76f, MieScattering = 8e-6f;
    static readonly Vector3 RayleighScattering = new(5.8e-6f, 13.5e-6f, 33.1e-6f);

    static float ToSphere(Vector3 p, Vector3 d, float r)
    {
        float b = Vector3.Dot(p, d), c = Vector3.Dot(p, p) - r * r;
        return -b + MathF.Sqrt(MathF.Max(b * b - c, 0f));
    }

    /// <summary>Light of the sky seen in direction d (unit, up = +Z) with the sun towards sun (unit), sun of brightness 1.</summary>
    public static Vector3 Radiance(Vector3 d, Vector3 sun)
    {
        const int Steps = 16, LightSteps = 8;
        var origin = new Vector3(0f, 0f, EarthRadius + 2f);
        float step = ToSphere(origin, d, AirRadius) / Steps;
        float opticalR = 0f, opticalM = 0f;
        Vector3 sumR = Vector3.Zero, sumM = Vector3.Zero;
        for (int i = 0; i < Steps; i++)
        {
            var p = origin + d * (step * (i + 0.5f));
            float h = MathF.Max(p.Length() - EarthRadius, 0f);
            float hr = MathF.Exp(-h / RayleighHeight) * step, hm = MathF.Exp(-h / MieHeight) * step;
            opticalR += hr; opticalM += hm;
            float lightStep = ToSphere(p, sun, AirRadius) / LightSteps;
            float lightR = 0f, lightM = 0f; bool lit = true;
            for (int j = 0; j < LightSteps; j++)
            {
                var q = p + sun * (lightStep * (j + 0.5f));
                float hq = q.Length() - EarthRadius;
                if (hq < 0f) { lit = false; break; }
                lightR += MathF.Exp(-hq / RayleighHeight) * lightStep;
                lightM += MathF.Exp(-hq / MieHeight) * lightStep;
            }
            if (!lit) continue;
            var tau = RayleighScattering * (opticalR + lightR) + new Vector3(MieScattering * 1.1f * (opticalM + lightM));
            var att = new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
            sumR += att * hr; sumM += att * hm;
        }
        float mu = Vector3.Dot(d, sun);
        float phaseR = 3f / (16f * MathF.PI) * (1f + mu * mu);
        float g2 = MieG * MieG;
        float phaseM = 3f / (8f * MathF.PI) * ((1f - g2) * (1f + mu * mu)) / ((2f + g2) * MathF.Pow(MathF.Abs(1f + g2 - 2f * MieG * mu), 1.5f));
        return sumR * RayleighScattering * phaseR + sumM * MieScattering * phaseM;
    }

    /// <summary>Share of the sunlight that crosses the air towards sun: the colour of the sun low on the horizon.</summary>
    public static Vector3 Transmittance(Vector3 sun)
    {
        const int Steps = 16;
        var origin = new Vector3(0f, 0f, EarthRadius + 2f);
        var d = Vector3.Normalize(sun with { Z = MathF.Max(sun.Z, -0.02f) });
        float step = ToSphere(origin, d, AirRadius) / Steps;
        float opticalR = 0f, opticalM = 0f;
        for (int i = 0; i < Steps; i++)
        {
            float h = MathF.Max((origin + d * (step * (i + 0.5f))).Length() - EarthRadius, 0f);
            opticalR += MathF.Exp(-h / RayleighHeight) * step; opticalM += MathF.Exp(-h / MieHeight) * step;
        }
        var tau = RayleighScattering * opticalR + new Vector3(MieScattering * 1.1f * opticalM);
        return new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
    }

    public const float LowestSky = 0.07f;
    public const int TableWidth = 64, TableHeight = 96;

    /// <summary>The sky table the sky is drawn from (PSTable): across, the angle around from the sun (0 to 180°);
    /// down, the height above the horizon (square root: finer near the horizon). RGB floats, row by row.</summary>
    public static float[] Table(float sunZ, Vector3 balance)
    {
        var t = new float[TableWidth * TableHeight * 4];
        float sunHeight = MathF.Asin(Math.Clamp(sunZ, -1f, 1f));
        var sun = new Vector3(MathF.Cos(sunHeight), 0f, MathF.Sin(sunHeight));
        System.Threading.Tasks.Parallel.For(0, TableHeight, y =>
        {
            float v = (y + 0.5f) / TableHeight;
            float height = LowestSky + v * v * (MathF.PI / 2 - LowestSky);
            for (int x = 0; x < TableWidth; x++)
            {
                float around = (x + 0.5f) / TableWidth * MathF.PI;
                var d = new Vector3(MathF.Cos(around) * MathF.Cos(height), MathF.Sin(around) * MathF.Cos(height), MathF.Sin(height));
                var c = Radiance(d, sun) * balance;
                int o = (y * TableWidth + x) * 4; t[o] = c.X; t[o + 1] = c.Y; t[o + 2] = c.Z; t[o + 3] = 1;
            }
        });
        return t;
    }
}
