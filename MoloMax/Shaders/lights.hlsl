// MoloMax - the game's distant lights at night: one soft glowing dot per light, a few pixels wide at any distance,
// added over the map (no depth write). Colour packed: low byte intensity, then red, green, blue.
cbuffer LightVars : register(b0)
{
    row_major float4x4 ViewProj;
    float4 Eye;      // xyz camera, w: angular size of one pixel
    float4 Right;    // xyz camera right
    float4 Up;       // xyz camera up, w: night strength (0 by day)
};

struct VS_IN { float3 pos : POSITION; uint col : COLOR0; uint vid : SV_VertexID; };
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };

PS_IN VSMain(VS_IN i)
{
    PS_IN o;
    static const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(-1,1), float2(-1,1), float2(1,-1), float2(1,1) };
    float2 c = corners[i.vid % 6];
    float inten = (i.col & 255) / 255.0;
    float3 rgb = float3((i.col >> 8) & 255, (i.col >> 16) & 255, (i.col >> 24) & 255) / 255.0;
    float d = length(i.pos - Eye.xyz);
    // a few pixels wide far away, a real lamp size (about 1 m) close by
    float px = 2.0 + 3.0 * inten;
    float size = max(d * Eye.w * px, min(d * 0.02, 0.8));
    float3 wp = i.pos + (Right.xyz * c.x + Up.xyz * c.y) * size;
    o.pos = mul(float4(wp, 1), ViewProj);
    o.uv = c;
    // close lights fade (the real lamps take over), far ones dim a little in the haze
    float near = saturate((d - 40) / 120);
    o.col = float4(rgb, (0.35 + 0.65 * inten) * Up.w * near);
    return o;
}

float4 PSMain(PS_IN i) : SV_TARGET
{
    float r = length(i.uv);
    float a = saturate(1 - r);
    a = a * a * (0.6 + 0.4 * saturate(1 - r * 3));
    return float4(i.col.rgb * a * i.col.a * 1.6, 0);
}
