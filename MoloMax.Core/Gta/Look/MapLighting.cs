using System.Numerics;

namespace MoloMax.Core.Gta.Look;

/// <summary>
/// Light and air of the MAP view at an hour, worked out: the sun (or the
/// moon), the light from the sky and from the ground, the sky colours and the distance haze. Display colours.
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

    // ---- HDR image ("High" look): linear colours, a sun several times stronger than the sky's light, the
    //      sky worked out from the air, the eye getting used to the dark (Adaptation)
    public bool IsHdr { get; init; }
    public float Adaptation { get; init; }
    public bool PhysicalSky { get; init; }
    public Vector3 FogSun { get; init; }
    public Vector3 SunTint { get; init; }
    /// <summary>Light given off by lit windows and lamps in the HDR image, compared with a white surface in the sun.</summary>
    public const float HdrEmission = 3f;
    const float HdrSun = 3f, HdrAmbient = 2f, HdrSky = 1.25f, HorizonHeight = 0.09f;

    public MapLighting ForHdr()
    {
        var hdr = HdrColours();
        return hdr with { Adaptation = Math.Clamp(MathF.Pow(DayKey / MathF.Max(Key(hdr), 1e-3f), 0.45f), 1f, 3.5f) };
    }

    MapLighting HdrColours() => this with
    {
        SunColor = Linear(SunColor) * HdrSun, SkyAmbient = Linear(SkyAmbient) * HdrAmbient, GroundAmbient = Linear(GroundAmbient) * HdrAmbient,
        Zenith = Linear(Zenith) * HdrSky, Horizon = Linear(Horizon) * HdrSky, IsHdr = true,
    };

    public MapLighting WithPhysicalSky()
    {
        if (!IsHdr || Sun == Vector3.Zero) return this;
        var scale = SkyBalance;
        var zenith = Atmosphere.Radiance(Vector3.UnitZ, Sun) * scale;
        var horizon = HorizonAround(Sun) * scale;
        var flat = new Vector2(Sun.X, Sun.Y);
        flat = flat.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(flat);
        var towardSun = Atmosphere.Radiance(new Vector3(flat * MathF.Cos(HorizonHeight), MathF.Sin(HorizonHeight)), Sun) * scale;
        float night = NightLights;
        return this with
        {
            PhysicalSky = true, Zenith = Vector3.Lerp(zenith, Zenith, night), Horizon = Vector3.Lerp(horizon, Horizon, night),
            FogSun = Vector3.Max(towardSun - horizon, Vector3.Zero) * (1f - night), SunTint = Atmosphere.Transmittance(Sun),
        };
    }

    static Vector3 HorizonAround(Vector3 sun)
    {
        const int Around = 8; var sum = Vector3.Zero;
        for (int i = 0; i < Around; i++)
        {
            float a = MathF.Tau * i / Around;
            sum += Atmosphere.Radiance(new Vector3(MathF.Cos(a) * MathF.Cos(HorizonHeight), MathF.Sin(a) * MathF.Cos(HorizonHeight), MathF.Sin(HorizonHeight)), sun);
        }
        return sum / Around;
    }

    /// <summary>Worked-out sky -> HDR image, per colour: at noon the horizon of the same hue as the colours above.</summary>
    public static Vector3 SkyBalance => skyBalance ??= MakeSkyBalance();
    static Vector3? skyBalance;
    static Vector3 MakeSkyBalance()
    {
        var noon = ForHour(13f, 0f);
        var target = noon.HdrColours();
        var horizon = HorizonAround(noon.Sun);
        var hue = target.Horizon / MathF.Max(Luminance(target.Horizon), 1e-9f) / (horizon / MathF.Max(Luminance(horizon), 1e-9f));
        var zenith = Atmosphere.Radiance(Vector3.UnitZ, noon.Sun) * hue;
        return hue * (Luminance(target.Zenith) / MathF.Max(Luminance(zenith), 1e-9f));
    }

    static float Key(MapLighting l) => Luminance(l.SkyAmbient) + Luminance(l.SunColor) * MathF.Max(l.SunDirection.Z, 0.1f);
    static readonly float DayKey = Key(ForHour(13f, 0f).HdrColours());
    static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    static Vector3 Linear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    static MapLighting Blend(MapLighting a, MapLighting b, float t) => a with
    {
        SunColor = Vector3.Lerp(a.SunColor, b.SunColor, t), SkyAmbient = Vector3.Lerp(a.SkyAmbient, b.SkyAmbient, t),
        GroundAmbient = Vector3.Lerp(a.GroundAmbient, b.GroundAmbient, t), Zenith = Vector3.Lerp(a.Zenith, b.Zenith, t),
        Horizon = Vector3.Lerp(a.Horizon, b.Horizon, t), SunDisc = float.Lerp(a.SunDisc, b.SunDisc, t), NightLights = float.Lerp(a.NightLights, b.NightLights, t),
    };

    static float SmoothStep(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f); return t * t * (3f - 2f * t); }
}
