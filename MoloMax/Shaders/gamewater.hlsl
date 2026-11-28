// MoloMax - the game's water (from the reference map view): its surfaces from water.xml, the open sea as a grid around
// the camera moved by the game's swell, waves rolling in along the shore, foam, the colour of each place, reflections and
// the bottom seen through it. Standard depth (the reference used reversed depth): see ViewDepth.

// Matrices are packed row-major (the loader's flag) and used as mul(rowVector, M), like System.Numerics.
cbuffer Constants : register(b0)
{
    float4x4 ViewProjection;
    float4 CameraTime;   // xyz: camera position, w: time (s).
    float4 SunDirection; // xyz: towards the sun (or the moon), w: near plane of the camera.
    float4 SunColor;     // a: visibility of the sun disc (0 at night).
    float4 SkyAmbient;
    float4 Zenith;
    float4 Horizon;      // Also the fog colour.
    float4 Settings;     // x: 1 = fog on, y: fog distance, z: 1 = scene depth available, w: 1 = scene colour available.
    float4 Viewport;     // xy: top-left pixel of the 3D view, zw: its size.
    float4 Options;      // x = 1: linear colours of an HDR image, y = 1: the open water moves (3D waves), z: its height,
                         // w = 1: ray-traced reflections (RayReflection).
    float4 WaveArea;     // xy: south-west corner of the image of the wave zones, zw: 1 / its size (m); z = 0: none.
    float4 MaskArea;     // The same for the image of where the open water is inside the map.
    float4 OceanBounds;  // The map's water file (min x, min y, max x, max y): open ocean all around; empty: none.
    float4 GridCenter;   // xy: centre of the grid of the open water.
    float4 ColorArea;    // xy: north-west corner of the game's water colour image, zw: 1 / its size (m, y negative); z = 0: none.
    float4 IslandArea;   // The same for Cayo Perico's image.
    float4 Caustics;     // x = 1: light pattern on the bottom available, y: pixels of the view per pixel of the ray images, z: near plane, w: far plane.
    float4 Patterns;     // x = 1: the game's wave patterns (swell, waves along the shore, ripples, foam) available.
};

// Depth mode of the view (see DepthMode.cs): x = 1 when the depth is reversed (1 near, 0 far).
cbuffer DepthModeVars : register(b13) { float4 DepthModeV; };
float StdDepth(float d) { return DepthModeV.x > 0.5 ? 1.0 - d : d; } // a depth value as standard depth (0 near, 1 far)
float NdcFar() { return DepthModeV.x > 0.5 ? 0.0 : 1.0; }             // depth of the far plane

// Water meshes of the models (pools, fountains, streams): placement and wave strength of the model drawn.
cbuffer MeshConstants : register(b1)
{
    float4x4 World;
    float4 MeshWaves; // x: wave strength.
};
Texture2DMS<float> SceneDepth : register(t0); // the window's multisampled depth (standard depth: 0 near, 1 far)
Texture2D SceneColor : register(t1);
Texture2D WaveTexture : register(t2);
Texture2D<float> WaterMask : register(t3);
Texture2D WaterColors : register(t4);
Texture2D IslandColors : register(t5);
Texture2D<float> CausticPattern : register(t6);
Texture2D<float> OceanWaves : register(t7);
Texture2D<float> ShoreWaves : register(t8);
Texture2D<float2> RipplePattern : register(t9);
Texture2D<float> FoamPattern : register(t10);
Texture2D RayReflection : register(t11);
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap : register(s1);

// Distance along the view of a depth value of the window (standard perspective: 0 at the near plane, 1 at the far one).
float ViewDepth(float d)
{
    float n = Caustics.z, f = Caustics.w;
    return n * f / max(f - d * (f - n), 1e-6f);
}

struct VS_INPUT { float3 pos : POSITION; float waves : TEXCOORD0; };
struct VS_MESH_INPUT { float3 pos : POSITION; float3 normal : NORMAL; };
struct VS_GRID_INPUT { float2 offset : POSITION; float spacing : TEXCOORD0; };
// world: the surface where it is drawn; rest: where it would be without the waves. kind.x = 1: open water's grid
// (only where the map has open water), kind.y = 1: swell of the open water.
struct PS_INPUT
{
    float4 pos : SV_POSITION; float3 world : TEXCOORD0; float waves : TEXCOORD1; float3 up : TEXCOORD2; float3 rest : TEXCOORD3;
    float2 kind : TEXCOORD4;
};

// The wave zones at a point: how calm (1 = open sea), size and way of the waves rolling in.
struct WaveState { float calm; float amplitude; float2 direction; };

WaveState WaveAt(float2 p)
{
    WaveState w;
    w.calm = 1.0f;
    w.amplitude = 0.0f;
    w.direction = float2(1.0f, 0.0f);
    if (WaveArea.z > 0.0f)
    {
        float2 uv = (p - WaveArea.xy) * WaveArea.zw;
        if (all(uv >= 0.0f) && all(uv <= 1.0f))
        {
            float4 t = WaveTexture.SampleLevel(LinearClamp, uv, 0);
            w.calm = t.r;
            w.amplitude = t.g * 2.5f;
            float2 d = t.ba * 2.0f - 1.0f;
            w.direction = dot(d, d) > 1e-4f ? normalize(d) : float2(1.0f, 0.0f);
        }
    }
    return w;
}

// One wave of the sum (Gerstner): its displacement of the point p, and how it changes along x and y (for the normal).
// steepness: how pointed its crests are (horizontal move times the wave number).
void AddWave(float2 p, float2 d, float length, float height, float steepness, inout float3 offset, inout float3 tx, inout float3 ty)
{
    float k = 6.2831853f / length;
    float phase = k * dot(d, p) - sqrt(9.81f * k) * CameraTime.w;
    float s, c;
    sincos(phase, s, c);
    offset += float3(d * (steepness / k) * c, height * s);
    tx += float3(-d.x * d.x * steepness * s, -d.x * d.y * steepness * s, d.x * k * height * c);
    ty += float3(-d.x * d.y * steepness * s, -d.y * d.y * steepness * s, d.y * k * height * c);
}

// Moves a pattern a whole number of times per hour (cycles), so the hourly wrap of the time does not show.
float Drift(float cycles)
{
    return CameraTime.w * cycles / 3600.0f;
}

// Mip level of a pattern of 128 texels over tile metres, seen with spacing metres between samples.
float PatternLevel(float tile, float spacing)
{
    return log2(max(spacing * 128.0f / tile, 1.0f));
}

// Height of the open sea from the game's swell pattern: three sizes drifting different ways, so its shape keeps
// changing. Direction (degrees), tile (m), cycles per hour, height (m).
static const float4 SwellLayers[3] =
{
    float4(20.0f, 150.0f, 96.0f, 0.85f), float4(62.0f, 61.0f, 150.0f, 0.32f), float4(-28.0f, 23.0f, 250.0f, 0.12f),
};

float SwellHeight(float2 p, float spacing)
{
    float h = 0.0f;
    [unroll] for (int i = 0; i < 3; i++)
    {
        float angle = radians(SwellLayers[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float2 uv = float2(dot(p, d), dot(p, float2(-d.y, d.x))) / SwellLayers[i].y - float2(Drift(SwellLayers[i].z), 0.0f);
        h += (OceanWaves.SampleLevel(LinearWrap, uv, PatternLevel(SwellLayers[i].y, spacing)) - 0.5f) * 2.0f * SwellLayers[i].w;
    }
    return h;
}

// How big the waves rolling in are along their crest, from the game's pattern (0.75 to 1.5): they break unevenly.
float ShoreStrength(float2 p, float2 direction, float spacing)
{
    float2 side = float2(-direction.y, direction.x);
    float2 uv = float2(dot(p, side) / 70.0f, dot(p, direction) / 350.0f + Drift(12.0f));
    float s = ShoreWaves.SampleLevel(LinearWrap, uv, PatternLevel(70.0f, spacing));
    return 0.75f + 0.75f * saturate((s - 0.42f) / 0.58f);
}

// The open water's swell at p: waves of the open sea (smaller in calm zones) and waves rolling in towards the shore.
// Waves too small for spacing (m between the points that show them) fade out instead of flickering. shore: crest of
// the waves rolling in (0 to 1), for their foam.
void Swell(float2 p, WaveState w, float strength, float spacing, out float3 offset, out float3 tx, out float3 ty, out float shore)
{
    if (Patterns.x > 0.5f)
    {
        float sea = strength * w.calm;
        float e = max(spacing, 0.25f);
        float h = SwellHeight(p, spacing);
        float2 slope = float2(SwellHeight(p + float2(e, 0.0f), spacing) - h, SwellHeight(p + float2(0.0f, e), spacing) - h) / e;
        offset = float3(0.0f, 0.0f, h * sea);
        tx = float3(0.0f, 0.0f, slope.x * sea);
        ty = float3(0.0f, 0.0f, slope.y * sea);
        shore = 0.0f;
        float rolling = strength * w.amplitude;
        if (rolling > 0.01f)
        {
            rolling *= ShoreStrength(p, w.direction, spacing);
            float2 side = float2(-w.direction.y, w.direction.x);
            float fade1 = saturate(22.0f / (4.0f * spacing) - 0.5f), fade2 = saturate(12.0f / (4.0f * spacing) - 0.5f);
            float sharp = saturate(rolling);
            AddWave(p, w.direction, 22.0f, 0.28f * rolling * fade1, 0.45f * sharp * fade1, offset, tx, ty);
            AddWave(p, normalize(w.direction + side * 0.25f), 12.0f, 0.1f * rolling * fade2, 0.3f * sharp * fade2, offset, tx, ty);
            float k = 6.2831853f / 22.0f;
            shore = saturate(sin(k * dot(w.direction, p) - sqrt(9.81f * k) * CameraTime.w) * 4.0f - 3.0f) * saturate(rolling - 0.3f);
        }
        return;
    }
    // Direction (degrees), length (m), height (m), steepness.
    static const float4 waves[6] =
    {
        float4(20.0f, 64.0f, 0.30f, 0.30f), float4(-12.0f, 41.0f, 0.19f, 0.28f), float4(48.0f, 26.0f, 0.11f, 0.24f),
        float4(-38.0f, 16.0f, 0.065f, 0.2f), float4(75.0f, 10.0f, 0.038f, 0.16f), float4(-3.0f, 6.5f, 0.022f, 0.12f),
    };
    offset = 0.0f;
    tx = 0.0f;
    ty = 0.0f;
    shore = 0.0f;
    float sea = strength * w.calm;
    [unroll] for (int i = 0; i < 6; i++)
    {
        float angle = radians(waves[i].x);
        float fade = saturate(waves[i].y / (4.0f * spacing) - 0.5f) * sea;
        AddWave(p, float2(cos(angle), sin(angle)), waves[i].y, waves[i].z * fade, waves[i].w * fade, offset, tx, ty);
    }
    float rolling = strength * w.amplitude;
    if (rolling > 0.01f)
    {
        float2 side = float2(-w.direction.y, w.direction.x);
        float fade1 = saturate(22.0f / (4.0f * spacing) - 0.5f), fade2 = saturate(12.0f / (4.0f * spacing) - 0.5f);
        float sharp = saturate(rolling);
        AddWave(p, w.direction, 22.0f, 0.28f * rolling * fade1, 0.45f * sharp * fade1, offset, tx, ty);
        AddWave(p, normalize(w.direction + side * 0.25f), 12.0f, 0.1f * rolling * fade2, 0.3f * sharp * fade2, offset, tx, ty);
        float k = 6.2831853f / 22.0f;
        shore = saturate(sin(k * dot(w.direction, p) - sqrt(9.81f * k) * CameraTime.w) * 4.0f - 3.0f) * saturate(rolling - 0.3f);
    }
}

// Inside the map, open water only where its file has a surface at the sea's height; open ocean all around.
bool IsOpenWater(float2 p)
{
    if (OceanBounds.z > OceanBounds.x && (any(p < OceanBounds.xy) || any(p > OceanBounds.zw)))
        return true;
    if (MaskArea.z <= 0.0f)
        return false;
    float2 uv = (p - MaskArea.xy) * MaskArea.zw;
    if (any(uv < 0.0f) || any(uv > 1.0f))
        return false;
    return WaterMask.SampleLevel(LinearClamp, uv, 0) > 0.5f;
}

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(float4(input.pos, 1.0f), ViewProjection);
    output.world = input.pos;
    output.rest = input.pos;
    output.waves = input.waves;
    output.up = float3(0.0f, 0.0f, 1.0f);
    output.kind = float2(0.0f, 1.0f);
    return output;
}

// The open water's grid around the camera, moved by the swell (less and less far away).
PS_INPUT VSGrid(VS_GRID_INPUT input)
{
    PS_INPUT output;
    float3 rest = float3(GridCenter.xy + input.offset, Options.z);
    float3 world = rest;
    float distance = length(rest - CameraTime.xyz);
    float reach = saturate(1.0f - distance / 6000.0f);
    if (reach > 0.0f)
    {
        float3 offset, tx, ty;
        float shore;
        Swell(rest.xy, WaveAt(rest.xy), 1.0f, input.spacing, offset, tx, ty, shore);
        world += offset * reach;
    }
    output.pos = mul(float4(world, 1.0f), ViewProjection);
    output.world = world;
    output.rest = rest;
    output.waves = 1.0f;
    output.up = float3(0.0f, 0.0f, 1.0f);
    output.kind = float2(1.0f, 1.0f);
    return output;
}

PS_INPUT VSMesh(VS_MESH_INPUT input)
{
    PS_INPUT output;
    float4 world = mul(float4(input.pos, 1.0f), World);
    output.pos = mul(world, ViewProjection);
    output.world = world.xyz;
    output.rest = world.xyz;
    output.waves = MeshWaves.x;
    output.up = mul(input.normal, (float3x3)World);
    output.kind = float2(0.0f, 0.0f);
    return output;
}

// Slope of small ripples over the swell: deep-water waves (speed from their length), around a main wind direction.
// Waves much smaller than a pixel fade out instead of flickering.
float2 WaveSlope(float2 p, float t, float footprint, int first)
{
    static const float3 waves[7] =
    {
        // Direction (degrees), length (m), steepness.
        float3(20.0f, 47.0f, 0.10f),
        float3(-15.0f, 26.0f, 0.09f),
        float3(55.0f, 15.3f, 0.08f),
        float3(-40.0f, 8.7f, 0.07f),
        float3(5.0f, 4.9f, 0.06f),
        float3(75.0f, 2.7f, 0.05f),
        float3(-65.0f, 1.45f, 0.05f),
    };
    float2 slope = 0.0f;
    [unroll] for (int i = 0; i < 7; i++)
    {
        if (i < first)
            continue;
        float angle = radians(waves[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float k = 6.2831853f / waves[i].y;
        float phase = k * dot(d, p) - sqrt(9.81f * k) * t + i * 1.7f;
        float fade = saturate(waves[i].y / (6.0f * footprint) - 0.5f);
        slope += d * waves[i].z * cos(phase) * fade;
    }
    return slope;
}

// Random value (0 to 1) of a cell, without sines: precise even far from the map's centre.
float Hash(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * 0.1031f);
    p += dot(p, p.yzx + 33.33f);
    return frac((p.x + p.y) * p.z);
}

// Soft random pattern (0 to 1), for the foam; repeats every kilometre.
float Noise(float2 p)
{
    p -= floor(p / 1024.0f) * 1024.0f;
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    return lerp(lerp(Hash(i), Hash(i + float2(1.0f, 0.0f)), f.x), lerp(Hash(i + float2(0.0f, 1.0f)), Hash(i + float2(1.0f, 1.0f)), f.x), f.y);
}

// Colour of deep water at p from the game's images (Cayo Perico's wins inside its area, blended at its edges).
float3 WaterTint(float2 p)
{
    float3 tint = WaterColors.SampleLevel(LinearClamp, (p - ColorArea.xy) * ColorArea.zw, 0).rgb;
    if (IslandArea.z != 0.0f)
    {
        float2 uv = (p - IslandArea.xy) * IslandArea.zw;
        float2 inside = saturate(min(uv, 1.0f - uv) * 12.0f);
        tint = lerp(tint, IslandColors.SampleLevel(LinearClamp, uv, 0).rgb, inside.x * inside.y);
    }
    return tint;
}

// Light gathered on the bottom under shallow water by the waves (two drifting layers of the game's pattern).
float CausticLight(float2 p, float t)
{
    float a = CausticPattern.SampleLevel(LinearWrap, p * 0.24f + float2(t * 0.021f, t * 0.013f), 0);
    float b = CausticPattern.SampleLevel(LinearWrap, float2(p.y, -p.x) * 0.19f - float2(t * 0.017f, -t * 0.011f), 0);
    return (a + b) * 0.5f;
}

float3 SkyColor(float3 ray)
{
    float3 horizon = Horizon.rgb, zenith = Zenith.rgb;
    if (Options.x > 0.5f)
        return pow(lerp(pow(abs(horizon), 1.0f / 2.2f), pow(abs(zenith), 1.0f / 2.2f), pow(saturate(ray.z), 0.45f)), 2.2f);
    return lerp(horizon, zenith, pow(saturate(ray.z), 0.45f));
}

// How far behind what is drawn a point is (m along the view, > 0 = hidden), and the pixel it falls on.
// Returns -1e9 off screen or where only the sky is drawn.
float Behind(float3 p, out int2 pixel, out float2 ndc)
{
    float4 clip = mul(float4(p, 1.0f), ViewProjection);
    pixel = int2(0, 0);
    ndc = float2(2.0f, 2.0f);
    if (clip.w <= SunDirection.w)
        return -1e9f;
    ndc = clip.xy / clip.w;
    if (abs(ndc.x) >= 1.0f || abs(ndc.y) >= 1.0f)
        return -1e9f;
    pixel = int2(Viewport.xy + (ndc * float2(0.5f, -0.5f) + 0.5f) * Viewport.zw);
    float depth = StdDepth(SceneDepth.Load(pixel, 0));
    return depth < 1.0f ? clip.w - ViewDepth(depth) : -1e9f;
}

// What the water mirrors among the things drawn on screen (buildings, bridges, boats): the reflected ray is followed
// in steps that grow with the distance until it goes behind something. a: how much to trust it (0 = the sky only).
float4 ScreenReflection(float3 start, float3 r, float distance)
{
    float step = max(0.3f, distance * 0.008f);
    float3 previous = start;
    float3 p = start;
    [loop] for (int i = 0; i < 32; i++)
    {
        previous = p;
        p += r * step;
        int2 pixel;
        float2 ndc;
        float behind = Behind(p, pixel, ndc);
        if (abs(ndc.x) >= 1.0f || abs(ndc.y) >= 1.0f)
            return 0.0f; // Off screen (or behind the camera): it does not come back.
        if (behind > 0.0f && behind < step * 3.0f + 2.0f)
        {
            // Closer to the surface hit, by halves.
            float3 a = previous, b = p;
            [unroll] for (int j = 0; j < 5; j++)
            {
                float3 m = 0.5f * (a + b);
                int2 mp;
                float2 mn;
                if (Behind(m, mp, mn) > 0.0f)
                {
                    b = m;
                    pixel = mp;
                    ndc = mn;
                }
                else
                {
                    a = m;
                }
            }
            // Faded near the edges of the view, where the next pixel would be missing.
            float2 edge = saturate((1.0f - abs(ndc)) * 6.0f);
            return float4(SceneColor.Load(int3(pixel, 0)).rgb, edge.x * edge.y);
        }
        step *= 1.2f;
    }
    return 0.0f;
}

// The surface's normal: the swell's where the point came from, small ripples over it. squeeze (below 1 where the
// surface is pressed together), height, shore and calm for the foam and the light through the crests.
float3 SurfaceNormal(PS_INPUT input, float3 up, float footprint, out float squeeze, out float height, out float shore, out float calm)
{
    float3 n = up;
    squeeze = 1.0f;
    height = 0.0f;
    shore = 0.0f;
    calm = 1.0f;
    if (input.kind.y > 0.5f)
    {
        WaveState w = WaveAt(input.rest.xy);
        float3 offset, tx, ty;
        Swell(input.rest.xy, w, input.waves, footprint, offset, tx, ty, shore);
        float3 dx = float3(1.0f, 0.0f, 0.0f) + tx, dy = float3(0.0f, 1.0f, 0.0f) + ty;
        n = normalize(cross(dx, dy));
        squeeze = dx.x * dy.y - dx.y * dy.x; // Below 1 on the crests, where the surface is pressed together.
        height = offset.z;
        calm = lerp(0.35f, 1.0f, w.calm);
        // The game's swell only moves up and down: its highest crests whiten instead.
        if (Patterns.x > 0.5f)
            squeeze -= saturate((height - 0.4f) / 0.4f) * 0.6f;
    }
    // Over the swell, only the short ripples (the long ones are part of it): the game's ripple pattern at two sizes.
    float2 ripples;
    if (Patterns.x > 0.5f)
    {
        float2 p = input.rest.xy;
        float2 a = RipplePattern.SampleLevel(LinearWrap, p / 9.0f + float2(Drift(140.0f), Drift(50.0f)), PatternLevel(9.0f, footprint)) * 2.0f - 1.0f;
        float2 b = RipplePattern.SampleLevel(LinearWrap, float2(p.y, -p.x) / 3.4f - float2(Drift(230.0f), Drift(-120.0f)), PatternLevel(3.4f, footprint)) * 2.0f - 1.0f;
        ripples = (a * 0.35f + b * 0.25f) * input.waves * calm;
    }
    else
    {
        ripples = WaveSlope(input.rest.xy, CameraTime.w, footprint, input.kind.y > 0.5f ? 3 : 0) * input.waves * calm;
    }
    return normalize(n + float3(-ripples, 0.0f) * max(n.z, 0.2f));
}

float3 SurfaceUp(PS_INPUT input)
{
    float len = length(input.up);
    return len > 0.0001f ? input.up / len : float3(0.0f, 0.0f, 1.0f);
}

// For the ray-traced reflections: where each pixel's reflected ray leaves the water (from the camera, w = 1), and its
// direction.
struct RAY_STARTS { float4 start : SV_Target0; float4 direction : SV_Target1; };

RAY_STARTS PSRays(PS_INPUT input)
{
    if (input.kind.x > 0.5f && !IsOpenWater(input.rest.xy))
        discard;
    // Hidden behind something drawn (reversed Z), or seen from below.
    if (StdDepth(SceneDepth.Load(int2(input.pos.xy * Caustics.y), 0)) <= StdDepth(input.pos.z))
        discard;
    float3 toCamera = CameraTime.xyz - input.world;
    float3 up = SurfaceUp(input);
    if (dot(toCamera, up) < 0.0f)
        discard;
    float squeeze, height, shore, calm;
    float3 n = SurfaceNormal(input, up, max(length(fwidth(input.rest.xy)) / Caustics.y, 0.001f), squeeze, height, shore, calm);
    float3 r = reflect(-normalize(toCamera), n);
    r.z = abs(r.z);
    RAY_STARTS o;
    o.start = float4(input.world - CameraTime.xyz, 1.0f);
    o.direction = float4(r, 0.0f);
    return o;
}

float4 PS(PS_INPUT input) : SV_Target
{
    if (Patterns.y > 0.5f)
        return float4(1.0f, 0.0f, 0.0f, 1.0f); // self test: plain red where the water is drawn
    if (input.kind.x > 0.5f && !IsOpenWater(input.rest.xy))
        discard;
    float3 camera = CameraTime.xyz;
    float3 toCamera = camera - input.world;
    float distance = length(toCamera);
    float3 v = toCamera / max(distance, 0.001f);
    float3 up = SurfaceUp(input);
    bool below = dot(toCamera, up) < 0.0f;
    bool hdr = Options.x > 0.5f;

    float footprint = max(length(fwidth(input.rest.xy)), 0.001f);
    float squeeze, height, shore, calm;
    float3 n = SurfaceNormal(input, up, footprint, squeeze, height, shore, calm);
    float2 slope = -n.xy / max(n.z, 0.2f);

    // Light received by the water body: sky all around, plus the sun from above.
    float3 light = SkyAmbient.rgb * 1.3f + SunColor.rgb * saturate(SunDirection.z) * 0.9f;
    float3 deepColor = float3(0.012f, 0.060f, 0.080f), shallowColor = float3(0.050f, 0.190f, 0.180f);
    // Light lost per metre of water crossed. On the map's water, the game's colour for the place, and the water as
    // clear as it says: brownish = muddy (lakes, rivers); red goes first in clear water, blue in muddy water.
    bool tinted = input.kind.y > 0.5f && ColorArea.z != 0.0f;
    float3 absorb = 0.30f;
    if (tinted)
    {
        float3 tint = WaterTint(input.rest.xy);
        float murk = saturate((tint.r - tint.b) * 12.0f + 0.6f);
        absorb = lerp(0.09f, 0.45f, murk) * (1.0f + (1.0f - tint / max(max(tint.r, tint.g), max(tint.b, 0.02f))) * 1.5f);
        deepColor = tint * 0.6f;
    }
    if (hdr)
    {
        deepColor = pow(deepColor, 2.2f) * 2.0f;
        shallowColor = pow(shallowColor, 2.2f) * 2.0f;
    }
    float3 deep = deepColor * light;
    float3 shallow = shallowColor * light;

    if (below)
        return float4(deep * 1.5f, 0.85f);

    // Water thickness along the view ray, from the depth of what is behind (reversed Z: depth = near / view depth).
    float thickness = 1000.0f;
    float behindRatio = 1e4f; // Distance to what is behind the water, over the distance to the water.
    if (Settings.z > 0.5f)
    {
        float sceneDepth = StdDepth(SceneDepth.Load(int2(input.pos.xy), 0));
        float waterView = ViewDepth(StdDepth(input.pos.z));
        float sceneView = sceneDepth < 1.0f ? ViewDepth(sceneDepth) : 1e7f;
        behindRatio = sceneView / max(waterView, 0.001f);
        thickness = max(sceneView - waterView, 0.0f) * distance / max(waterView, 0.001f);
    }
    float vertical = thickness * abs(dot(v, up)); // Depth below the surface, roughly.
    float edge = saturate(vertical * 4.0f);

    // See-through when shallow, then the water's own colour.
    float3 transmit = exp(-thickness * absorb);
    float opacity = 1.0f - dot(transmit, 1.0f / 3.0f);
    float3 body = tinted ? deep : lerp(shallow, deep, 1.0f - exp(-thickness * 0.12f));
    // Sunlight passing through the crests of the waves, seen against the sun: a lighter green-blue.
    float3 through = (hdr ? float3(0.02f, 0.12f, 0.1f) : float3(0.1f, 0.3f, 0.28f)) * SunColor.rgb
        * pow(saturate(dot(-v, SunDirection.xyz) * 0.5f + 0.5f), 4.0f) * saturate(height * 1.5f + 0.2f);
    body += through * (1.0f - edge * 0.3f);

    // Reflection of the sky, stronger at grazing angles; the sun or moon glints on the waves.
    float fresnel = (0.02f + 0.98f * pow(1.0f - saturate(dot(n, v)), 5.0f)) * edge;
    float3 r = reflect(-v, n);
    r.z = abs(r.z);
    float3 reflection = SkyColor(r);
    bool detailed = Settings.z > 0.5f && Settings.w > 0.5f;
    if (Options.w > 0.5f)
    {
        // Traced at a lower resolution: nothing found = black and transparent, so the filtered colour is already
        // weighted by what was found.
        float4 traced = RayReflection.SampleLevel(LinearClamp, (input.pos.xy - Viewport.xy) / Viewport.zw, 0);
        reflection = reflection * (1.0f - traced.a) + traced.rgb;
    }
    else if (detailed)
    {
        float4 mirrored = ScreenReflection(input.world, r, distance);
        reflection = lerp(reflection, mirrored.rgb, mirrored.a);
    }
    float facing = saturate(dot(r, SunDirection.xyz));
    float3 glint = SunColor.rgb * (pow(facing, 900.0f) * (2.0f + 10.0f * SunColor.a) + pow(facing, 60.0f) * 0.25f);

    // Foam: where the water meets the shore, on the crests pressed together, on the waves rolling in; broken up.
    float pattern;
    if (Patterns.x > 0.5f)
        pattern = FoamPattern.SampleLevel(LinearWrap, input.rest.xy / 20.0f + Drift(60.0f), PatternLevel(20.0f * 8.0f, footprint)) * 0.6f
            + FoamPattern.SampleLevel(LinearWrap, input.rest.xy / 7.5f - Drift(90.0f), PatternLevel(7.5f * 8.0f, footprint)) * 0.4f;
    else
        pattern = Noise(input.rest.xy * 0.9f + CameraTime.w * 0.15f) * 0.6f + Noise(input.rest.xy * 3.1f - CameraTime.w * 0.2f) * 0.4f;
    float foam = (1.0f - smoothstep(0.05f, 0.7f, vertical)) * edge * saturate(0.5f + 3.0f * (slope.x + slope.y)) * input.waves;
    // Over deep water the crests only whiten in patches (white horses), not along their whole length: no regular bands
    // of foam across the open sea seen from above.
    float offshore = smoothstep(2.0f, 12.0f, vertical);
    foam += saturate((0.6f - squeeze) * 3.0f) * saturate(pattern * lerp(1.6f, 2.6f, offshore) - lerp(0.5f, 1.45f, offshore)) * lerp(0.7f, 0.5f, offshore);
    // The waves rolling in break into foam only where the water gets shallow.
    foam += shore * saturate(pattern * 2.0f - 0.5f) * (1.0f - smoothstep(1.0f, 6.0f, vertical));
    foam = saturate(foam) * saturate(1.0f - distance / 2500.0f);
    float3 foamColor = light * 0.85f;

    // Premultiplied alpha: the reflection is opaque, the body as opaque as the water is thick.
    float3 color = reflection * fresnel + body * (1.0f - fresnel) * (1.0f - transmit) + glint * edge;
    float alpha = saturate(fresnel + (1.0f - fresnel) * opacity);
    color = lerp(color, foamColor, foam);
    alpha = lerp(alpha, 1.0f, foam);

    // The bottom seen through the water, bent by the waves (not where something in front of the water would show),
    // each colour dimmed as much as the water absorbs it, with the light gathered by the waves on it in the sun.
    if (detailed && alpha < 0.999f)
    {
        float2 bend = slope * float2(1.0f, -1.0f) * min(thickness, 4.0f) * Viewport.w * 0.012f / max(distance * 0.05f, 1.0f);
        int2 pixel = int2(input.pos.xy + bend);
        float waterDepth = StdDepth(input.pos.z);
        if (StdDepth(SceneDepth.Load(pixel, 0)) <= waterDepth)
            pixel = int2(input.pos.xy);
        float3 bottom = SceneColor.Load(int3(pixel, 0)).rgb;
        if (Caustics.x > 0.5f && behindRatio < 1e3f)
        {
            float3 ground = camera + (input.world - camera) * behindRatio;
            float under = input.world.z - ground.z;
            float strength = saturate(under * 2.0f) * exp(-under * 0.15f) * saturate(1.0f - distance / 200.0f)
                * saturate((0.25f - footprint) * 10.0f) * saturate(SunDirection.z * 3.0f) * SunColor.a;
            if (strength > 0.0f)
                bottom *= 1.0f + CausticLight(ground.xy, CameraTime.w) * 1.5f * strength;
        }
        color += bottom * transmit * (1.0f - fresnel) * (1.0f - foam);
        alpha = 1.0f;
    }

    // Same fog as the models.
    float fog = Settings.x * saturate((distance - 0.25f * Settings.y) / (0.75f * Settings.y));
    fog = fog * fog * (3.0f - 2.0f * fog);
    if (hdr)
        fog = pow(fog, 1.5f);
    return float4(lerp(color, Horizon.rgb * alpha, fog), alpha);
}