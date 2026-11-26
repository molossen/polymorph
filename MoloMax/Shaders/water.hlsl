// MoloMax - the game's water in the MAP view: flat surfaces of water.xml moved by small waves (normals only),
// sky reflection by the angle (Fresnel), the sun's glint, deep colour below, the same distance haze as the map.
cbuffer WaterVars : register(b0)
{
    row_major float4x4 ViewProj;
    float4 Eye;        // xyz camera, w time (s)
    float4 SunDir;     // xyz towards the sun, w sun strength (0 at night)
    float4 Zenith;     // rgb sky overhead
    float4 Horizon;    // rgb sky at the horizon
    float4 Deep;       // rgb water body colour (already lit), a: opacity when looking down
    float4 FogColour;  // rgb, a strength
    float4 FogRange;   // x start, y full
};

struct VS_IN { float3 pos : POSITION; };
struct PS_IN { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; };

PS_IN VSMain(VS_IN i)
{
    PS_IN o;
    o.wp = i.pos;
    o.pos = mul(float4(i.pos, 1), ViewProj);
    return o;
}

float2 Wave(float2 p, float2 dir, float freq, float speed, float t)
{
    float ph = dot(p, dir) * freq + t * speed;
    return dir * cos(ph) * freq;
}

float4 PSMain(PS_IN i) : SV_TARGET
{
    float t = Eye.w;
    float2 p = i.wp.xy;
    float dist = length(Eye.xyz - i.wp);
    // a few crossing waves: the slope gives the normal; calmer far away (no shimmer)
    float2 g = Wave(p, normalize(float2(1, 0.3)), 0.35, 1.3, t) * 0.06
             + Wave(p, normalize(float2(-0.4, 1)), 0.55, 1.7, t) * 0.04
             + Wave(p, normalize(float2(0.7, -0.7)), 1.3, 2.4, t) * 0.015
             + Wave(p, normalize(float2(-1, -0.2)), 2.9, 3.1, t) * 0.006;
    g *= saturate(1 - dist / 1500) * 0.9 + 0.1;
    float3 n = normalize(float3(-g.x, -g.y, 1));
    float3 v = normalize(Eye.xyz - i.wp);
    float3 r = reflect(-v, n);
    float up = saturate(r.z);
    float3 sky = lerp(Horizon.rgb, Zenith.rgb, pow(up, 0.45));
    float fres = 0.02 + 0.98 * pow(1 - saturate(dot(v, n)), 5);
    float3 c = lerp(Deep.rgb, sky, fres);
    // the sun's glint on the waves
    float3 sd = normalize(SunDir.xyz);
    c += pow(saturate(dot(r, sd)), 350) * 4 * SunDir.w * float3(1, 0.95, 0.85);
    float a = lerp(Deep.a, 1, fres);
    // same haze as the map
    float f = saturate((dist - FogRange.x) / max(FogRange.y - FogRange.x, 1)) * FogColour.a;
    c = lerp(c, FogColour.rgb, f);
    return float4(saturate(c), a);
}
