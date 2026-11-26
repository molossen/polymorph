using System.Numerics;

namespace MoloMax.Core.Gta;

/// <summary>
/// The global light block the GTA basic shader reads (CodeWalker's ShaderGlobalLightParams):
/// sun direction/colour, directional ambient, natural (sky) ambient and artificial (interior)
/// ambient, each split into up and down colours. Vertex colour R scales natural, G artificial.
/// </summary>
public struct GtaGlobalLights
{
    public Vector3 LightDir;       // toward the sun, normalised, Z >= 0
    public float LightHdr;
    public Vector4 LightDirColour;
    public Vector4 LightDirAmbColour;
    public Vector4 NaturalAmbUp;
    public Vector4 NaturalAmbDown;
    public Vector4 ArtificialAmbUp;
    public Vector4 ArtificialAmbDown;
}

public static class GtaLighting
{
    struct Key
    {
        public float Hour; public Vector3 Sun; public Vector3 SkyUp; public Vector3 SkyDown; public float DirAmb;
        public Key(float h, Vector3 sun, Vector3 up, Vector3 down, float dirAmb) { Hour = h; Sun = sun; SkyUp = up; SkyDown = down; DirAmb = dirAmb; }
    }

    // Hand-tuned to read like GTA's default "extrasunny" timecycle in a non-HDR viewport.
    static readonly Key[] keys =
    {
        new(0,  new(0.10f, 0.12f, 0.20f), new(0.06f, 0.08f, 0.14f), new(0.04f, 0.04f, 0.06f), 0.04f),
        new(5,  new(0.15f, 0.15f, 0.25f), new(0.10f, 0.12f, 0.18f), new(0.06f, 0.06f, 0.08f), 0.05f),
        new(7,  new(0.95f, 0.62f, 0.42f), new(0.35f, 0.38f, 0.48f), new(0.22f, 0.19f, 0.17f), 0.10f),
        new(12, new(1.00f, 0.95f, 0.86f), new(0.42f, 0.50f, 0.62f), new(0.30f, 0.27f, 0.23f), 0.14f),
        new(17, new(1.00f, 0.85f, 0.65f), new(0.40f, 0.45f, 0.56f), new(0.28f, 0.24f, 0.20f), 0.12f),
        new(20, new(0.90f, 0.45f, 0.25f), new(0.28f, 0.26f, 0.36f), new(0.16f, 0.13f, 0.12f), 0.08f),
        new(22, new(0.12f, 0.14f, 0.22f), new(0.08f, 0.09f, 0.15f), new(0.05f, 0.05f, 0.07f), 0.05f),
        new(24, new(0.10f, 0.12f, 0.20f), new(0.06f, 0.08f, 0.14f), new(0.04f, 0.04f, 0.06f), 0.04f),
    };

    /// <summary>Daylight cycle (hour 0..24). <paramref name="interiorAmbient"/> is the artificial ambient level.</summary>
    public static GtaGlobalLights TimeOfDay(float hour, float interiorAmbient = 0.38f)
    {
        hour = ((hour % 24) + 24) % 24;
        int k = 0; while (k < keys.Length - 2 && hour > keys[k + 1].Hour) k++;
        var a = keys[k]; var b = keys[k + 1];
        float t = Math.Clamp((hour - a.Hour) / MathF.Max(b.Hour - a.Hour, 1e-3f), 0, 1);

        // Sun rises in the east (+X), peaks south-ish, sets in the west. Moon takes over at night.
        bool day = hour >= 6f && hour <= 20.5f;
        float dayT = day ? (hour - 6f) / 14.5f : ((hour + 24 - 20.5f) % 24) / 9.5f;
        float az = MathF.PI * dayT;
        float el = MathF.Max(MathF.Sin(MathF.PI * dayT) * 1.15f, 0.06f);
        var dir = Vector3.Normalize(new Vector3(MathF.Cos(az), -0.35f, el));

        var sun = Vector3.Lerp(a.Sun, b.Sun, t);
        var up = Vector3.Lerp(a.SkyUp, b.SkyUp, t);
        var down = Vector3.Lerp(a.SkyDown, b.SkyDown, t);
        float dirAmb = a.DirAmb + (b.DirAmb - a.DirAmb) * t;
        var art = new Vector3(1.0f, 0.93f, 0.82f) * interiorAmbient;

        return new GtaGlobalLights
        {
            LightDir = dir,
            LightHdr = 1,
            LightDirColour = new Vector4(sun, 1),
            LightDirAmbColour = new Vector4(new Vector3(dirAmb), 1),
            NaturalAmbUp = new Vector4(up, 1),
            NaturalAmbDown = new Vector4(down, 1),
            ArtificialAmbUp = new Vector4(art * 0.85f, 1),
            ArtificialAmbDown = new Vector4(art, 1),
        };
    }

    /// <summary>Sky of the hour (display colours): overhead, at the horizon (also the far haze colour), the true sun direction
    /// (below the horizon at night) and how much of the sun disc shows. Same sun path as <see cref="TimeOfDay"/>.</summary>
    public static (Vector3 Zenith, Vector3 Horizon, Vector3 Sun, float Disc) SkyOf(float hour)
    {
        hour = ((hour % 24) + 24) % 24;
        var day = (Zenith: new Vector3(0.22f, 0.42f, 0.76f), Horizon: new Vector3(0.70f, 0.80f, 0.90f));
        var golden = (Zenith: new Vector3(0.26f, 0.32f, 0.55f), Horizon: new Vector3(0.95f, 0.62f, 0.38f));
        var night = (Zenith: new Vector3(0.015f, 0.025f, 0.06f), Horizon: new Vector3(0.06f, 0.08f, 0.14f));
        bool isDay = hour >= 6f && hour <= 20.5f;
        float dayT = isDay ? (hour - 6f) / 14.5f : 0;
        float height = isDay ? MathF.Sin(MathF.PI * dayT) : -0.4f;
        // sunrise / sunset glow around the horizon, night below
        if (hour > 5.2f && hour < 6.4f) height = (hour - 6f) * 0.25f;
        if (hour > 20.1f && hour < 21.3f) height = (20.5f - hour) * 0.25f;
        static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        Vector3 z, h;
        if (height < 0.05f) { float t = Smooth(-0.15f, 0.05f, height); z = Vector3.Lerp(night.Zenith, golden.Zenith, t); h = Vector3.Lerp(night.Horizon, golden.Horizon, t); }
        else { float t = Smooth(0.05f, 0.35f, height); z = Vector3.Lerp(golden.Zenith, day.Zenith, t); h = Vector3.Lerp(golden.Horizon, day.Horizon, t); }
        float az = MathF.PI * (isDay ? dayT : 0);
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(az), -0.35f, height * 1.15f));
        return (z, h, sun, Smooth(-0.05f, 0.05f, height));
    }

    /// <summary>Interior check: no sun, artificial ambient only (what green vertex colour sees).</summary>
    public static GtaGlobalLights Interior(float level = 0.5f)
    {
        var art = new Vector3(1.0f, 0.94f, 0.84f) * level;
        return new GtaGlobalLights
        {
            LightDir = Vector3.Normalize(new Vector3(0.3f, -0.2f, 0.9f)),
            LightHdr = 1,
            LightDirColour = Vector4.Zero,
            LightDirAmbColour = new Vector4(0.05f, 0.05f, 0.05f, 1),
            NaturalAmbUp = new Vector4(0.05f, 0.05f, 0.06f, 1),
            NaturalAmbDown = new Vector4(0.03f, 0.03f, 0.03f, 1),
            ArtificialAmbUp = new Vector4(art * 0.85f, 1),
            ArtificialAmbDown = new Vector4(art, 1),
        };
    }
}
