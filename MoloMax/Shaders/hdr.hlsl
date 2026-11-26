// MoloMax - MAP HDR image brought to the screen (SceneImage composite): exposure, the filmic curve
// (fit of the ACES reference rendering), saturation, display colours, a little noise against bands.
cbuffer HdrVars : register(b0)
{
    float4 Settings; // x exposure, y share of the light the occlusion darkens (0: none), z share of the glow, w saturation
    float4 View;     // xy top-left of the view (pixels), zw its size
    float4 Window;   // xy 1 / size of the window's images
};
Texture2D SceneColor : register(t0);
Texture2D<float> Occlusion : register(t1);
Texture2D Glow : register(t2);
SamplerState LinearClamp : register(s0);

float4 VSFull(uint id : SV_VertexID) : SV_POSITION
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

float3 ToneMap(float3 c)
{
    static const float3x3 toAces = { 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 };
    static const float3x3 fromAces = { 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 };
    c = mul(toAces, c);
    float3 a = c * (c + 0.0245786) - 0.000090537;
    float3 b = c * (0.983729 * c + 0.4329510) + 0.238081;
    return saturate(mul(fromAces, a / b));
}

float3 ToDisplay(float3 c) { return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(abs(c), 1.0 / 2.4) - 0.055; }

float4 PSComposite(float4 pos : SV_POSITION) : SV_TARGET
{
    float3 c = max(SceneColor.Load(int3(pos.xy, 0)).rgb, 0);
    if (Settings.y > 0) { float ao = Occlusion.SampleLevel(LinearClamp, (pos.xy - View.xy) / View.zw, 0); c *= saturate(1 - (1 - ao) * Settings.y); }
    if (Settings.z > 0) c = lerp(c, Glow.SampleLevel(LinearClamp, pos.xy * Window.xy, 0).rgb, Settings.z);
    c = ToneMap(c * Settings.x);
    c = max(lerp(dot(c, float3(0.2126, 0.7152, 0.0722)), c, Settings.w), 0);
    c = ToDisplay(c);
    float noise = frac(52.9829189 * frac(dot(pos.xy, float2(0.06711056, 0.00583715))));
    return float4(c + (noise - 0.5) / 255.0, 1);
}
