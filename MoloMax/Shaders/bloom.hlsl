// MoloMax - glow (bloom) of the MAP HDR image, NodeRunner's: the image shrunk step by step to 1/64 of its size, then
// enlarged back with each step added, so very bright pixels (sun, lamps, lit windows) spill a soft light around them.
cbuffer BloomVars : register(b0)
{
    float4 Texel; // xy 1 / size of the image read, z = 1 first step (very bright pixels weigh less, no flicker)
};
Texture2D Source : register(t0);
SamplerState LinearClamp : register(s0);
struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

VS_OUTPUT VSFull(uint id : SV_VertexID)
{
    VS_OUTPUT o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = p;
    return o;
}

float3 Read(float2 uv, float2 offset) { return max(Source.SampleLevel(LinearClamp, uv + offset * Texel.xy, 0).rgb, 0); }
float Weight(float3 c) { return Texel.z > 0.5 ? 1.0 / (1.0 + max(max(c.r, c.g), c.b)) : 1.0; }

float4 PSDown(VS_OUTPUT input) : SV_Target
{
    static const float2 offsets[13] =
    {
        float2(-2, -2), float2(0, -2), float2(2, -2), float2(-2, 0), float2(0, 0), float2(2, 0),
        float2(-2, 2), float2(0, 2), float2(2, 2), float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1),
    };
    static const float shares[13] = { 0.03125, 0.0625, 0.03125, 0.0625, 0.125, 0.0625, 0.03125, 0.0625, 0.03125, 0.125, 0.125, 0.125, 0.125 };
    float3 sum = 0; float weights = 0;
    [unroll] for (int i = 0; i < 13; i++) { float3 c = Read(input.uv, offsets[i]); float w = shares[i] * Weight(c); sum += c * w; weights += w; }
    return float4(sum / max(weights, 0.0001), 1);
}

float4 PSUp(VS_OUTPUT input) : SV_Target
{
    float3 c = Read(input.uv, float2(0, 0)) * 4
        + (Read(input.uv, float2(-1, 0)) + Read(input.uv, float2(1, 0)) + Read(input.uv, float2(0, -1)) + Read(input.uv, float2(0, 1))) * 2
        + Read(input.uv, float2(-1, -1)) + Read(input.uv, float2(1, -1)) + Read(input.uv, float2(-1, 1)) + Read(input.uv, float2(1, 1));
    return float4(c / 16, 1);
}
