// MoloMax - ambient occlusion of the MAP HDR image (NodeRunner's): how much of the sky's light the surroundings hide
// from each pixel (nooks, the ground under objects, the foot of walls), from the depth; blurred keeping the edges.
cbuffer AoVars : register(b0)
{
    float4 Projection;   // x, y scale of the projection, z near (m), w search radius (m)
    float4 Size;         // xy size of the occlusion image, zw size of the scene images (pixels)
    float4 Settings;     // x far plane (m), z samples, w distance where it fades out
    float4 Direction;    // xy blur direction (pixels)
    float4 Origin;       // xy top-left of the view in the scene images (pixels), zw size of the view
};
Texture2DMS<float> Depth : register(t0);
Texture2D<float> Occlusion : register(t2);
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

float DistanceAt(float2 uv)
{
    int2 px = (int2)clamp(Origin.xy + uv * Origin.zw, Origin.xy, Origin.xy + Origin.zw - 1);
    float d = Depth.Load(px, 0);
    float n = Projection.z, f = Settings.x;
    return d >= 1.0 ? 1e9 : f * n / (f - d * (f - n));
}

float3 ViewPoint(float2 uv, float dist)
{
    float2 ndc = (uv * 2 - 1) * float2(1, -1);
    return float3(ndc.x * dist / Projection.x, ndc.y * dist / Projection.y, -dist);
}

float PSOcclusion(VS_OUTPUT input) : SV_Target
{
    float dist = DistanceAt(input.uv);
    if (dist > 1e8) return 1;
    float3 p = ViewPoint(input.uv, dist);
    float2 t = 1.0 / Origin.zw;
    float3 px = ViewPoint(input.uv + float2(t.x, 0), DistanceAt(input.uv + float2(t.x, 0)));
    float3 py = ViewPoint(input.uv + float2(0, t.y), DistanceAt(input.uv + float2(0, t.y)));
    float3 nx = ViewPoint(input.uv - float2(t.x, 0), DistanceAt(input.uv - float2(t.x, 0)));
    float3 ny = ViewPoint(input.uv - float2(0, t.y), DistanceAt(input.uv - float2(0, t.y)));
    float3 dx = abs(px.z - p.z) < abs(nx.z - p.z) ? px - p : p - nx;
    float3 dy = abs(py.z - p.z) < abs(ny.z - p.z) ? py - p : p - ny;
    float3 n = normalize(cross(dy, dx));
    if (dot(n, -p) < 0) n = -n;
    float3 tangent = normalize(abs(n.z) < 0.9 ? cross(n, float3(0, 0, 1)) : cross(n, float3(1, 0, 0)));
    float3 bitangent = cross(n, tangent);
    float noise = frac(52.9829189 * frac(dot(input.pos.xy, float2(0.06711056, 0.00583715))));
    float angle = noise * 6.2831853;
    int count = (int)Settings.z;
    float radius = Projection.w;
    float hidden = 0;
    [loop] for (int i = 0; i < count; i++)
    {
        float fr = (i + 0.5) / count;
        float r = sqrt(fr);
        float theta = i * 2.3999632 + angle;
        float3 dir = tangent * (cos(theta) * r) + bitangent * (sin(theta) * r) + n * sqrt(1 - fr);
        float reach = lerp(0.15, 1.0, frac(fr + noise * 0.61803));
        float3 s = p + dir * (radius * reach * reach);
        if (s.z >= -Projection.z) continue;
        float2 ndc = float2(s.x * Projection.x, s.y * Projection.y) / -s.z;
        float2 uv = ndc * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0) || any(uv > 1)) continue;
        float sceneDistance = DistanceAt(uv);
        float inFront = -s.z - sceneDistance;
        if (inFront > 0.02 * radius) hidden += saturate(radius / max(inFront, 0.001) - 0.5);
    }
    float occlusion = 1 - hidden / max(count, 1);
    float fade = saturate(-p.z / Settings.w);
    return lerp(occlusion, 1, fade * fade);
}

float PSBlur(VS_OUTPUT input) : SV_Target
{
    float center = DistanceAt(input.uv);
    float sum = 0, weights = 0;
    [unroll] for (int i = -4; i <= 4; i++)
    {
        float2 uv = input.uv + Direction.xy * i / Size.xy;
        float d = DistanceAt(uv);
        float w = exp(-i * i / 8.0) * saturate(1 - abs(d - center) / (0.03 * center + 0.05));
        sum += Occlusion.SampleLevel(LinearClamp, uv, 0) * w;
        weights += w;
    }
    return sum / max(weights, 0.0001);
}
