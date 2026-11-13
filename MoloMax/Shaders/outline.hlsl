// MoloMax - selection outline (Blender style): the selected object is stamped into the stencil,
// then drawn again pushed outwards by a few pixels; only the ring outside the stamp gets coloured.
cbuffer OutlineVars : register(b0)
{
    row_major float4x4 WorldViewProj;
    float4 Colour;
    float4 Params;      // x = width in pixels, y = viewport width, z = viewport height
};

struct VS_INPUT { float3 Position : POSITION; float3 Normal : NORMAL; };

float4 VSMain(VS_INPUT input) : SV_POSITION
{
    float4 p = mul(float4(input.Position, 1.0), WorldViewProj);
    if (Params.x > 0)
    {
        float4 q = mul(float4(input.Position + input.Normal * 0.01, 1.0), WorldViewProj);
        float2 d = q.xy / max(q.w, 1e-5) - p.xy / max(p.w, 1e-5);
        float l = length(d);
        if (l > 1e-7) p.xy += d / l * Params.x * 2.0 / Params.yz * p.w;
    }
    return p;
}

float4 PSMain(float4 pos : SV_POSITION) : SV_TARGET { return Colour; }
