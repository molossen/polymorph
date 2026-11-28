// MoloMax - sun rays of the MAP HDR image: the bright sky around the sun kept (everything in front of it black),
// then smeared away from the sun, so the light spills between the buildings, the trees and the hills.
cbuffer RayVars : register(b0)
{
    float4 SunPosition; // xy the sun in the view (0 to 1), z exposure, w width / height of the view
    float4 Origin;      // xy top-left of the view in the scene images (pixels), zw size of the view
    float4 Window;      // xy 1 / size of the scene images
};

// Depth mode of the view (see DepthMode.cs): x = 1 when the depth is reversed (1 near, 0 far).
cbuffer DepthModeVars : register(b13) { float4 DepthModeV; };
float StdDepth(float d) { return DepthModeV.x > 0.5 ? 1.0 - d : d; } // a depth value as standard depth (0 near, 1 far)
float NdcFar() { return DepthModeV.x > 0.5 ? 0.0 : 1.0; }             // depth of the far plane

Texture2D Scene : register(t0);
Texture2DMS<float> Depth : register(t1);
Texture2D Mask : register(t2);
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

float4 PSMask(VS_OUTPUT input) : SV_Target
{
    float2 px = clamp(Origin.xy + input.uv * Origin.zw, Origin.xy, Origin.xy + Origin.zw - 1);
    if (StdDepth(Depth.Load((int2)px, 0)) < 1.0) return 0;
    float3 color = min(max(Scene.SampleLevel(LinearClamp, px * Window.xy, 0).rgb, 0) * SunPosition.z, 4.0);
    float2 offset = (input.uv - SunPosition.xy) * float2(SunPosition.w, 1.0);
    float near = saturate(1.0 - length(offset) / 0.7);
    return float4(color * near * near, 1);
}

float4 PSRays(VS_OUTPUT input) : SV_Target
{
    const int Samples = 40;
    float2 step = (input.uv - SunPosition.xy) * (0.85 / Samples);
    float noise = frac(52.9829189 * frac(dot(input.pos.xy, float2(0.06711056, 0.00583715))));
    float2 uv = input.uv - step * noise;
    float3 sum = 0;
    float decay = 1, weights = 0;
    for (int i = 0; i < Samples; i++)
    {
        sum += Mask.SampleLevel(LinearClamp, uv, 0).rgb * decay;
        weights += decay;
        decay *= 0.965;
        uv -= step;
    }
    return float4(sum / weights, 1);
}
