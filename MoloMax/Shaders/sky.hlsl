// MoloMax - sky behind the MAP view (NodeRunner's sky): horizon to zenith gradient, darker below the horizon, the sun
// disc and its glow; in the HDR image, the sky worked out from the air read from a small table around the sun.
// Matrices row_major, mul(rowVector, M).
cbuffer SkyVars : register(b0)
{
    row_major float4x4 InvViewProj;
    float4 Zenith;     // w: sky turned grey by the weather
    float4 Horizon;
    float4 SunDir;     // xyz towards the sun (the light's direction), w visibility of the disc
    float4 Disc;       // x brightness of the disc, y 1 = HDR image (linear colours), z 1 = sky from the table, w share of the night colours
    float4 Tint;       // colour of the sun through the air (table)
    float4 TrueSun;    // direction of the sun, even below the horizon
};
static const float LowestSky = 0.07f;
Texture2D Table : register(t0);
SamplerState LinearClamp : register(s0);

struct PS_IN { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_IN VSMain(uint id : SV_VertexID)
{
    PS_IN o;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0 - 1.0;
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
    float3 horizon = Horizon.rgb, zenith = Zenith.rgb;
    if (Disc.y > 0.5) { horizon = pow(abs(horizon), 1.0 / 2.2); zenith = pow(abs(zenith), 1.0 / 2.2); }
    float3 color = up >= 0.0 ? lerp(horizon, zenith, pow(saturate(up), 0.45)) : horizon * lerp(0.85, 0.55, saturate(-up * 4.0));
    if (Disc.y > 0.5) color = pow(color, 2.2);
    float facing = saturate(dot(ray, SunDir.xyz));
    if (Disc.z > 0.5)
    {
        float2 flat = ray.xy, sunFlat = TrueSun.xy;
        float around = dot(flat, flat) > 1e-8 && dot(sunFlat, sunFlat) > 1e-8 ? acos(clamp(dot(normalize(flat), normalize(sunFlat)), -1.0, 1.0)) / 3.14159265 : 0.0;
        float height = sqrt(saturate((asin(saturate(up)) - LowestSky) / (1.5707963 - LowestSky)));
        float3 sky = Table.SampleLevel(LinearClamp, float2(around, height), 0).rgb;
        if (up < 0.0) sky *= lerp(0.85, 0.55, saturate(-up * 4.0));
        sky = lerp(sky, dot(sky, float3(0.2126, 0.7152, 0.0722)), Zenith.w);
        color = lerp(sky, color, Disc.w);
        color += SunDir.w * pow(facing, 2000.0) * Disc.x * Tint.rgb;
        return float4(color, 1.0);
    }
    color += SunDir.w * (pow(facing, 2000.0) * Disc.x * float3(1.0, 0.95, 0.85) + pow(facing, 24.0) * 0.25 * Horizon.rgb);
    return float4(Disc.y > 0.5 ? color : saturate(color), 1.0);
}
