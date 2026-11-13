// MoloMax - 3D Gaussian splats (scans). Each splat is a screen-facing quad shaped by its projected 3D covariance
// (EWA splatting, as in "3D Gaussian Splatting", Kerbl et al. 2023). Drawn back to front, premultiplied alpha.
cbuffer SplatVars : register(b0)
{
    row_major float4x4 WorldView;
    row_major float4x4 Proj;
    float4 Screen;   // viewport width, height, focal x, focal y (pixels)
    float4 Look;     // size, opacity, brightness, ortho (1 = orthographic)
    float4 Look2;    // min opacity, crop (1 = on), near plane, -
    float4 CropMin;
    float4 CropMax;
};

Buffer<float4> Splats : register(t0); // 4 per splat: centre + opacity, colour, cov xx xy xz yy, cov yz zz

struct VS_INPUT
{
    uint Index : SPLAT;
    uint Corner : SV_VertexID;
};

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float4 Colour : COLOR0;
    float2 Q : TEXCOORD0;
};

VS_OUTPUT Hidden()
{
    VS_OUTPUT o;
    o.Position = float4(0, 0, 2, 1); // outside the depth range: dropped
    o.Colour = 0; o.Q = 0;
    return o;
}

VS_OUTPUT VSMain(VS_INPUT input)
{
    uint b = input.Index * 4;
    float4 a = Splats.Load(b);
    float4 c = Splats.Load(b + 1);
    float4 s0 = Splats.Load(b + 2);
    float4 s1 = Splats.Load(b + 3);
    if (a.w < Look2.x) return Hidden();
    if (Look2.y > 0.5 && (any(a.xyz < CropMin.xyz) || any(a.xyz > CropMax.xyz))) return Hidden();

    float4 t = mul(float4(a.xyz, 1), WorldView);
    bool ortho = Look.w > 0.5;
    if (!ortho && -t.z < Look2.z) return Hidden();

    float k = Look.x * Look.x;
    float3x3 S = float3x3(s0.x, s0.y, s0.z,
                          s0.y, s0.w, s1.x,
                          s0.z, s1.x, s1.y) * k;
    float3x3 M = (float3x3)WorldView;                 // row vectors: v_view = v * M
    float3x3 V = mul(transpose(M), mul(S, M));        // covariance in view space

    float fx = Screen.z, fy = Screen.w;
    float3x3 J;
    if (ortho) J = float3x3(fx, 0, 0, 0, fy, 0, 0, 0, 0);
    else
    {
        float iz = 1.0 / -t.z;
        // keep the Jacobian sane for splats far outside the view (as the reference renderer does)
        float lx = 1.3 * Screen.x * 0.5 / fx, ly = 1.3 * Screen.y * 0.5 / fy;
        float tx = clamp(t.x * iz, -lx, lx) / iz, ty = clamp(t.y * iz, -ly, ly) / iz;
        J = float3x3(fx * iz, 0, fx * tx * iz * iz,
                     0, fy * iz, fy * ty * iz * iz,
                     0, 0, 0);
    }
    float3x3 C = mul(J, mul(V, transpose(J)));
    float xx = C[0][0] + 0.3, xy = C[0][1], yy = C[1][1] + 0.3;

    float mid = 0.5 * (xx + yy);
    float rad = length(float2(0.5 * (xx - yy), xy));
    float l1 = mid + rad, l2 = mid - rad;
    if (l2 <= 0) return Hidden();
    float2 v1 = abs(xy) > 1e-7 ? normalize(float2(xy, l1 - xx)) : (xx >= yy ? float2(1, 0) : float2(0, 1));
    float2 major = min(sqrt(2.0 * l1), 2048.0) * v1;
    float2 minor = min(sqrt(2.0 * l2), 2048.0) * float2(v1.y, -v1.x);

    float2 q = float2((input.Corner & 1) ? 2.0 : -2.0, (input.Corner & 2) ? 2.0 : -2.0);
    float4 clip = mul(t, Proj);
    float2 px = q.x * major + q.y * minor;
    clip.xy += px / (Screen.xy * 0.5) * clip.w;

    VS_OUTPUT o;
    o.Position = clip;
    o.Colour = float4(saturate(c.rgb * Look.z), saturate(a.w * Look.y));
    o.Q = q;
    return o;
}

float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    float d = dot(input.Q, input.Q);
    if (d > 4.0) discard;
    float alpha = min(0.99, input.Colour.a * exp(-d));
    if (alpha < 1.0 / 255.0) discard;
    return float4(input.Colour.rgb * alpha, alpha);
}
