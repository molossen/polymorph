// MoloMax - sky behind the MAP view: horizon to zenith gradient of the hour, the sun disc and its glow,
// a darker band below the horizon (the far ground). Display colours. Matrices row_major, mul(rowVector, M).
cbuffer SkyVars : register(b0)
{
    row_major float4x4 InvViewProj;
    float4 Zenith;
    float4 Horizon;
    float4 SunDir;     // xyz towards the sun (true, below the horizon at night), w: how much of the disc shows
    float4 Below;      // rgb: far ground colour under the horizon
};

struct PS_IN { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_IN VSMain(uint id : SV_VertexID)
{
    PS_IN o;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0 - 1.0; // one triangle over the whole view
    o.pos = float4(p, 0.5, 1.0);
    o.ndc = p;
    return o;
}

float4 PSMain(PS_IN i) : SV_TARGET
{
    float4 a = mul(float4(i.ndc, 0.0, 1.0), InvViewProj);
    float4 b = mul(float4(i.ndc, 1.0, 1.0), InvViewProj);
    float3 ray = normalize(b.xyz / b.w - a.xyz / a.w);
    float up = ray.z;
    float3 c;
    if (up >= 0) c = lerp(Horizon.rgb, Zenith.rgb, pow(saturate(up), 0.45));
    else c = lerp(Horizon.rgb, Below.rgb, saturate(-up * 6.0));
    float facing = saturate(dot(ray, normalize(SunDir.xyz)));
    // disc, then a wide warm glow around it (stronger near the horizon at sunrise / sunset)
    c += SunDir.w * (pow(facing, 2000.0) * float3(1.0, 0.95, 0.85) * 1.5 + pow(facing, 24.0) * 0.25 * Horizon.rgb + pow(facing, 6.0) * 0.08 * Horizon.rgb);
    return float4(saturate(c), 1.0);
}
