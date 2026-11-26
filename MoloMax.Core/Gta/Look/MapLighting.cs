using System.Numerics;

namespace MoloMax.Core.Gta.Look;

/// <summary>
/// Light and air of the MAP view at an hour, as NodeRunner (Molo Modding's partner tool) works it out: the sun (or the
/// moon), the light from the sky and from the ground, the sky colours and the distance haze. Display colours.
/// Ported with the author's agreement.
/// </summary>
public readonly record struct MapLighting
{
    public Vector3 SunDirection { get; init; }
    public Vector3 SunColor { get; init; }
    public Vector3 SkyAmbient { get; init; }
    public Vector3 GroundAmbient { get; init; }
    public Vector3 Zenith { get; init; }
    public Vector3 Horizon { get; init; }
    public float SunDisc { get; init; }
    public float NightLights { get; init; }
    public float FogDistance { get; init; }
    /// <summary>Where the sun really is (below the horizon at night).</summary>
    public Vector3 Sun { get; init; }

    public static MapLighting Neutral { get; } = new()
    {
        SunDirection = Vector3.Normalize(new Vector3(0.35f, 0.25f, 0.9f)),
        SunColor = new Vector3(0.6f), SkyAmbient = new Vector3(0.55f), GroundAmbient = new Vector3(0.25f),
        Zenith = new Vector3(0.25f, 0.45f, 0.78f), Horizon = new Vector3(0.70f, 0.80f, 0.90f), SunDisc = 1f,
    };

    static readonly MapLighting Day = Neutral with
    {
        SunColor = new Vector3(0.62f, 0.60f, 0.56f), SkyAmbient = new Vector3(0.52f, 0.55f, 0.60f), GroundAmbient = new Vector3(0.26f, 0.25f, 0.23f),
        Zenith = new Vector3(0.22f, 0.42f, 0.76f), Horizon = new Vector3(0.68f, 0.78f, 0.90f),
    };

    static readonly MapLighting Golden = Neutral with
    {
        SunColor = new Vector3(0.75f, 0.48f, 0.28f), SkyAmbient = new Vector3(0.42f, 0.38f, 0.42f), GroundAmbient = new Vector3(0.20f, 0.16f, 0.14f),
        Zenith = new Vector3(0.26f, 0.32f, 0.55f), Horizon = new Vector3(0.95f, 0.60f, 0.36f),
    };

    static readonly MapLighting Night = Neutral with
    {
        SunColor = new Vector3(0.20f, 0.25f, 0.38f), SkyAmbient = new Vector3(0.16f, 0.19f, 0.28f), GroundAmbient = new Vector3(0.07f, 0.07f, 0.10f),
        Zenith = new Vector3(0.015f, 0.025f, 0.06f), Horizon = new Vector3(0.06f, 0.08f, 0.14f), SunDisc = 0f, NightLights = 1f,
    };

    /// <summary>Light at an hour (0 to 24): the sun rises in the east (+X) at 6:00, is high in the south (-Y) at noon, sets
    /// in the west at 18:00; at night the moon lights the scene faintly, opposite the sun.</summary>
    public static MapLighting ForHour(float hour, float fogDistance)
    {
        float angle = (hour - 6f) / 12f * MathF.PI;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), -0.45f * MathF.Sin(angle), 0.9f * MathF.Sin(angle)));
        float height = sun.Z;
        var l = height < 0.05f ? Blend(Night, Golden, SmoothStep(-0.15f, 0.05f, height)) : Blend(Golden, Day, SmoothStep(0.05f, 0.35f, height));
        var direction = height < -0.05f ? Vector3.Normalize(-sun with { Z = MathF.Max(-sun.Z, 0.3f) })
            : Vector3.Normalize(sun with { Z = MathF.Max(sun.Z, 0.08f) });
        return l with { SunDirection = direction, FogDistance = fogDistance, Sun = sun };
    }

    static MapLighting Blend(MapLighting a, MapLighting b, float t) => a with
    {
        SunColor = Vector3.Lerp(a.SunColor, b.SunColor, t), SkyAmbient = Vector3.Lerp(a.SkyAmbient, b.SkyAmbient, t),
        GroundAmbient = Vector3.Lerp(a.GroundAmbient, b.GroundAmbient, t), Zenith = Vector3.Lerp(a.Zenith, b.Zenith, t),
        Horizon = Vector3.Lerp(a.Horizon, b.Horizon, t), SunDisc = float.Lerp(a.SunDisc, b.SunDisc, t), NightLights = float.Lerp(a.NightLights, b.NightLights, t),
    };

    static float SmoothStep(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f); return t * t * (3f - 2f * t); }
}
