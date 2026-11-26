// MoloMax - the game's water in the MAP HDR image, water: deep-water
// waves (Gerstner) and ripples for the normal, light through the crests, the sky and what is on screen mirrored, the
// bottom seen through shallow water (bent by the waves, dimmed as the water absorbs it), foam at the shore, haze.
// Matrices row_major, mul(rowVector, M); depth not reversed (0 near, 1 far).
cbuffer Constants : register(b0)
{
    row_major float4x4 ViewProjection;
    float4 CameraTime;   // xyz camera, w time (s)
    float4 SunDirection; // xyz towards the sun (or the moon), w near plane
    float4 SunColor;     // a visibility of the sun disc
    float4 SkyAmbient;   // w far plane
    float4 Zenith;
    float4 Horizon;      // also the haze colour
    float4 Settings;     // x 1 = haze on, y haze distance, z 1 = scene depth / colour available
    float4 Viewport;     // xy top-left pixel of the view, zw its size
    float4 Options;      // x 1 = HDR (linear colours)
};
Texture2DMS<float> SceneDepth : register(t0);
Texture2D SceneColor : register(t1);

struct VS_IN { float3 pos : POSITION; };
struct PS_IN { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };

PS_IN VSMain(VS_IN i)
{
    PS_IN o;
    o.pos = mul(float4(i.pos, 1), ViewProjection);
    o.world = i.pos;
    return o;
}

void AddWave(float2 p, float2 d, float length, float height, float steepness, inout float3 offset, inout float3 tx, inout float3 ty)
{
    float k = 6.2831853 / length;
    float phase = k * dot(d, p) - sqrt(9.81 * k) * CameraTime.w;
    float s, c; sincos(phase, s, c);
    offset += float3(d * (steepness / k) * c, height * s);
    tx += float3(-d.x * d.x * steepness * s, -d.x * d.y * steepness * s, d.x * k * height * c);
    ty += float3(-d.x * d.y * steepness * s, -d.y * d.y * steepness * s, d.y * k * height * c);
}

void Swell(float2 p, float strength, float spacing, out float3 offset, out float3 tx, out float3 ty)
{
    static const float4 waves[6] =
    {
        float4(20.0, 64.0, 0.30, 0.30), float4(-12.0, 41.0, 0.19, 0.28), float4(48.0, 26.0, 0.11, 0.24),
        float4(-38.0, 16.0, 0.065, 0.2), float4(75.0, 10.0, 0.038, 0.16), float4(-3.0, 6.5, 0.022, 0.12),
    };
    offset = 0; tx = 0; ty = 0;
    [unroll] for (int i = 0; i < 6; i++)
    {
        float angle = radians(waves[i].x);
        float fade = saturate(waves[i].y / (4.0 * spacing) - 0.5) * strength;
        AddWave(p, float2(cos(angle), sin(angle)), waves[i].y, waves[i].z * fade, waves[i].w * fade, offset, tx, ty);
    }
}

float2 WaveSlope(float2 p, float t, float footprint, int first)
{
    static const float3 waves[7] =
    {
        float3(20.0, 47.0, 0.10), float3(-15.0, 26.0, 0.09), float3(55.0, 15.3, 0.08), float3(-40.0, 8.7, 0.07),
        float3(5.0, 4.9, 0.06), float3(75.0, 2.7, 0.05), float3(-65.0, 1.45, 0.05),
    };
    float2 slope = 0;
    [unroll] for (int i = 0; i < 7; i++)
    {
        if (i < first) continue;
        float angle = radians(waves[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float k = 6.2831853 / waves[i].y;
        float phase = k * dot(d, p) - sqrt(9.81 * k) * t + i * 1.7;
        float fade = saturate(waves[i].y / (6.0 * footprint) - 0.5);
        slope += d * waves[i].z * cos(phase) * fade;
    }
    return slope;
}

float Hash(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float Noise(float2 p)
{
    p -= floor(p / 1024.0) * 1024.0;
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
}

float3 SkyColor(float3 ray)
{
    float3 horizon = Horizon.rgb, zenith = Zenith.rgb;
    if (Options.x > 0.5) return pow(lerp(pow(abs(horizon), 1.0 / 2.2), pow(abs(zenith), 1.0 / 2.2), pow(saturate(ray.z), 0.45)), 2.2);
    return lerp(horizon, zenith, pow(saturate(ray.z), 0.45));
}

float ViewDistance(float depth) { float n = SunDirection.w, f = SkyAmbient.w; return depth >= 1.0 ? 1e7 : f * n / (f - depth * (f - n)); }

float Behind(float3 p, out int2 pixel, out float2 ndc)
{
    float4 clip = mul(float4(p, 1), ViewProjection);
    pixel = int2(0, 0); ndc = float2(2, 2);
    if (clip.w <= SunDirection.w) return -1e9;
    ndc = clip.xy / clip.w;
    if (abs(ndc.x) >= 1 || abs(ndc.y) >= 1) return -1e9;
    pixel = int2(Viewport.xy + (ndc * float2(0.5, -0.5) + 0.5) * Viewport.zw);
    float depth = SceneDepth.Load(pixel, 0);
    return depth < 1.0 ? clip.w - ViewDistance(depth) : -1e9;
}

float4 ScreenReflection(float3 start, float3 r, float distance)
{
    float step = max(0.3, distance * 0.008);
    float3 previous = start, p = start;
    [loop] for (int i = 0; i < 32; i++)
    {
        previous = p; p += r * step;
        int2 pixel; float2 ndc;
        float behind = Behind(p, pixel, ndc);
        if (abs(ndc.x) >= 1 || abs(ndc.y) >= 1) return 0;
        if (behind > 0 && behind < step * 3 + 2 && p.z > start.z + 0.3) // only what stands above the water
        {
            float3 a = previous, b = p;
            [unroll] for (int j = 0; j < 5; j++)
            {
                float3 m = 0.5 * (a + b); int2 mp; float2 mn;
                if (Behind(m, mp, mn) > 0) { b = m; pixel = mp; ndc = mn; } else a = m;
            }
            float2 edge = saturate((1 - abs(ndc)) * 6);
            return float4(SceneColor.Load(int3(pixel, 0)).rgb, edge.x * edge.y);
        }
        step *= 1.2;
    }
    return 0;
}

float4 PSMain(PS_IN input) : SV_Target
{
    float3 camera = CameraTime.xyz;
    float3 toCamera = camera - input.world;
    float distance = length(toCamera);
    float3 v = toCamera / max(distance, 0.001);
    float3 up = float3(0, 0, 1);
    bool hdr = Options.x > 0.5;
    float footprint = max(length(fwidth(input.world.xy)), 0.001);

    // surface: swell of the open sea (sea level), small ripples over it
    float3 offset, tx, ty;
    float openSea = abs(input.world.z) < 0.5 ? 1 : 0.35;
    Swell(input.world.xy, openSea, footprint, offset, tx, ty);
    float3 dx = float3(1, 0, 0) + tx, dy = float3(0, 1, 0) + ty;
    float3 n = normalize(cross(dx, dy));
    float squeeze = dx.x * dy.y - dx.y * dy.x, height = offset.z;
    float2 ripples = WaveSlope(input.world.xy, CameraTime.w, footprint, 3);
    n = normalize(n + float3(-ripples, 0) * max(n.z, 0.2));
    float2 slope = -n.xy / max(n.z, 0.2);

    float3 light = SkyAmbient.rgb * 1.3 + SunColor.rgb * saturate(SunDirection.z) * 0.9;
    float3 deepColor = float3(0.012, 0.060, 0.080), shallowColor = float3(0.050, 0.190, 0.180);
    float3 absorb = 0.30;
    if (hdr) { deepColor = pow(deepColor, 2.2) * 2.0; shallowColor = pow(shallowColor, 2.2) * 2.0; }
    float3 deep = deepColor * light, shallow = shallowColor * light;
    if (dot(toCamera, up) < 0) return float4(deep * 1.5, 0.85);

    float thickness = 1000, behindRatio = 1e4;
    bool detailed = Settings.z > 0.5;
    if (detailed)
    {
        float sceneDepth = SceneDepth.Load(int2(input.pos.xy), 0);
        float waterView = ViewDistance(input.pos.z);
        float sceneView = ViewDistance(sceneDepth);
        behindRatio = sceneView / max(waterView, 0.001);
        thickness = max(sceneView - waterView, 0) * distance / max(waterView, 0.001);
    }
    float vertical = thickness * abs(dot(v, up));
    float edge = saturate(vertical * 4);
    float3 transmit = exp(-thickness * absorb);
    float opacity = 1 - dot(transmit, 1.0 / 3.0);
    float3 body = lerp(shallow, deep, 1 - exp(-thickness * 0.12));
    float3 through = (hdr ? float3(0.02, 0.12, 0.1) : float3(0.1, 0.3, 0.28)) * SunColor.rgb
        * pow(saturate(dot(-v, SunDirection.xyz) * 0.5 + 0.5), 4) * saturate(height * 1.5 + 0.2);
    body += through * (1 - edge * 0.3);

    float fresnel = (0.02 + 0.98 * pow(1 - saturate(dot(n, v)), 5)) * edge;
    float3 r = reflect(-v, n); r.z = abs(r.z);
    float3 reflection = SkyColor(r);
    if (detailed) { float4 mirrored = ScreenReflection(input.world, r, distance); reflection = lerp(reflection, mirrored.rgb, mirrored.a); }
    float facing = saturate(dot(r, SunDirection.xyz));
    float3 glint = SunColor.rgb * (pow(facing, 900) * (2 + 10 * SunColor.a) + pow(facing, 60) * 0.25);

    float pattern = Noise(input.world.xy * 0.9 + CameraTime.w * 0.15) * 0.6 + Noise(input.world.xy * 3.1 - CameraTime.w * 0.2) * 0.4;
    float foam = (1 - smoothstep(0.05, 0.7, vertical)) * edge * saturate(0.5 + 3 * (slope.x + slope.y));
    foam += saturate((0.45 - squeeze) * 3) * saturate(pattern * 1.6 - 0.6) * 0.3; // crests: less without the game's foam pattern
    foam = saturate(foam) * saturate(1 - distance / 2500);
    float3 foamColor = light * 0.85;

    float3 color = reflection * fresnel + body * (1 - fresnel) * (1 - transmit) + glint * edge;
    float alpha = saturate(fresnel + (1 - fresnel) * opacity);
    color = lerp(color, foamColor, foam);
    alpha = lerp(alpha, 1, foam);
    if (detailed && alpha < 0.999)
    {
        float2 bend = slope * float2(1, -1) * min(thickness, 4) * Viewport.w * 0.012 / max(distance * 0.05, 1);
        int2 pixel = int2(input.pos.xy + bend);
        if (SceneDepth.Load(pixel, 0) <= input.pos.z) pixel = int2(input.pos.xy);
        float3 bottom = SceneColor.Load(int3(pixel, 0)).rgb;
        color += bottom * transmit * (1 - fresnel) * (1 - foam);
        alpha = 1;
    }
    float fog = Settings.x * saturate((distance - 0.25 * Settings.y) / (0.75 * Settings.y));
    fog = fog * fog * (3 - 2 * fog);
    if (hdr) fog = pow(fog, 1.5);
    return float4(lerp(color, Horizon.rgb * alpha, fog), alpha);
}
