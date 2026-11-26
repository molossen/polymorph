// MoloMax - clouds in volume (the): for each pixel of a smaller image a
// ray goes through the cloud layer, adding the light the clouds send towards the eye and how much sky still shows.

cbuffer Constants : register(b0)
{
    row_major float4x4 InverseViewProjection;
    float4 Eye;      // xyz: camera, w: frame (0: the same steps every frame)
    float4 Layer;    // x: base (m), y: thickness (m), z: coverage, w: density (per metre)
    float4 Look;     // x: heaps, y: dark, z: steps, w: steps towards the sun
    float4 Wind;     // xyz: drift of the shapes (m)
    float4 SunDir;   // xyz: towards the light (sun or moon)
    float4 SunLight; // rgb: its colour
    float4 SkyLight; // rgb: light from the sky above
    float4 GroundLight; // rgb: light from below
};
Texture3D<float2> Shape : register(t0);
Texture2D<float2> WeatherMap : register(t1);
SamplerState Wrap : register(s0);

static const float Earth = 6360000.0f;
static const float ShapeScale = 4200.0f;
static const float DetailScale = 840.0f;
static const float DetailDistance = 14000.0f; // Beyond, the frayed edges would only flicker.
static const float WeatherScale = 24000.0f;
static const float FarthestStart = 60000.0f;
static const float LongestRay = 16000.0f;
static const float FadeStart = 6000.0f;
static const float FadeDistance = 22000.0f;

struct PS_INPUT { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_INPUT VS(uint id : SV_VertexID)
{
    PS_INPUT output;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0f - 1.0f;
    output.pos = float4(p, 0.0f, 1.0f);
    output.ndc = p;
    return output;
}

// Distances along d from o (relative to the Earth's centre) to the sphere of radius r; negative: none.
float2 Sphere(float3 o, float3 d, float r)
{
    float b = dot(o, d);
    float h = b * b - (dot(o, o) - r * r);
    if (h < 0.0f)
        return float2(-1.0f, -1.0f);
    h = sqrt(h);
    return float2(-b - h, -b + h);
}

float Remap(float v, float a, float b, float c, float d) { return c + (v - a) / (b - a) * (d - c); }

// Cloud density (per metre) at world point p, height 0 to 1 in the layer; detail: frayed edges.
float Density(float3 p, float height, bool detail)
{
    if (height <= 0.0f || height >= 1.0f)
        return 0.0f;
    float2 w = WeatherMap.SampleLevel(Wrap, (p.xy + Wind.xy) / WeatherScale, 0);
    float coverage = saturate(Layer.z + (w.x - 0.5f) * 1.1f * (1.0f - Layer.z * Layer.z));
    float heaps = saturate(Look.x + (w.y - 0.5f) * 0.7f);
    // Flat bottom, rounded top, lower for flat layers.
    float top = lerp(0.3f, 1.0f, heaps);
    float profile = saturate(Remap(height, 0.0f, 0.07f, 0.0f, 1.0f)) * saturate(Remap(height, top * 0.55f, top, 1.0f, 0.0f));
    float shape = Shape.SampleLevel(Wrap, (p + Wind.xyz) / ShapeScale, 0).x * profile;
    float cloud = saturate(Remap(shape, 1.0f - coverage, 1.0f, 0.0f, 1.0f));
    if (detail && cloud > 0.0f)
    {
        float d = Shape.SampleLevel(Wrap, (p + Wind.xyz * 1.5f) / DetailScale, 0).y;
        // Wisps at the bottom, billows above.
        float erode = lerp(1.0f - d, d, saturate(height * 3.0f)) * 0.4f;
        cloud = saturate(Remap(cloud, erode, 1.0f, 0.0f, 1.0f));
    }
    return cloud * Layer.w;
}

// Henyey-Greenstein, 1 for light scattered evenly.
float Phase(float mu, float g)
{
    float g2 = g * g;
    return (1.0f - g2) / pow(abs(1.0f + g2 - 2.0f * g * mu), 1.5f);
}

// Light from the sun at a point behind an optical depth od towards it, with the light scattered several times inside
// (each time weaker, less absorbed, less forward).
float SunScatter(float od, float mu)
{
    float sum = 0.0f, a = 1.0f, b = 1.0f, c = 1.0f;
    [unroll] for (int k = 0; k < 3; k++)
    {
        sum += a * exp(-b * od) * lerp(Phase(mu, 0.7f * c), Phase(mu, -0.25f * c), 0.35f);
        a *= 0.5f; b *= 0.4f; c *= 0.5f;
    }
    return sum;
}

float4 PS(PS_INPUT input) : SV_Target
{
    float4 a = mul(float4(input.ndc, 0.0f, 1.0f), InverseViewProjection);
    float4 b = mul(float4(input.ndc, 1.0f, 1.0f), InverseViewProjection);
    float3 ray = normalize(b.xyz / b.w - a.xyz / a.w);

    float height = Earth + max(Eye.z, 1.0f);
    float3 o = float3(0.0f, 0.0f, height);
    float inner = Earth + Layer.x, outer = inner + Layer.y;
    float2 hitIn = Sphere(o, ray, inner), hitOut = Sphere(o, ray, outer);
    float start, end;
    if (height < inner)
    {
        if (Sphere(o, ray, Earth).x > 0.0f)
            return float4(0.0f, 0.0f, 0.0f, 1.0f); // The ground first.
        start = hitIn.y;
        end = hitOut.y;
    }
    else if (height < outer)
    {
        start = 0.0f;
        end = hitIn.x > 0.0f ? hitIn.x : hitOut.y;
    }
    else
    {
        if (hitOut.x < 0.0f)
            return float4(0.0f, 0.0f, 0.0f, 1.0f);
        start = hitOut.x;
        end = hitIn.x > 0.0f ? hitIn.x : hitOut.y;
    }
    if (start > FarthestStart || end <= start)
        return float4(0.0f, 0.0f, 0.0f, 1.0f);
    end = min(end, start + LongestRay);

    int steps = (int)Look.z, lightSteps = (int)Look.w;
    float dt = (end - start) / steps;
    // A different start along the step for neighbouring pixels (and frames): smooth instead of banded.
    float jitter = frac(52.9829189f * frac(dot(input.pos.xy, float2(0.06711056f, 0.00583715f))) + Eye.w * 0.61803399f);
    float3 sun = normalize(SunDir.xyz);
    float mu = dot(ray, sun);
    float albedo = lerp(0.98f, 0.75f, Look.y);
    float lightStep = Layer.y * 0.6f / max(lightSteps, 1);
    float3 origin = float3(Eye.xy, 0.0f);

    float3 light = 0.0f;
    float transmittance = 1.0f, near = 0.0f, weight = 0.0f;
    float t = start + dt * jitter;
    [loop] for (int i = 0; i < steps; i++)
    {
        float3 p = o + ray * t;
        float h = (length(p) - inner) / Layer.y;
        float3 world = origin + float3(p.xy, p.z - Earth);
        float sigma = Density(world, h, t < DetailDistance);
        if (sigma > 0.0f)
        {
            float od = 0.0f;
            [loop] for (int j = 0; j < lightSteps; j++)
            {
                float s = lightStep * (j + 0.5f);
                float3 q = p + sun * s;
                od += Density(world + sun * s, (length(q) - inner) / Layer.y, j < 2) * lightStep;
            }
            float3 lit = SunLight.rgb * SunScatter(od, mu) + SkyLight.rgb * lerp(0.6f, 1.1f, saturate(h)) + GroundLight.rgb * 0.3f * (1.0f - saturate(h));
            float3 source = lit * albedo;
            float step = exp(-sigma * dt);
            light += transmittance * (source - source * step);
            near += t * transmittance * (1.0f - step);
            weight += transmittance * (1.0f - step);
            transmittance *= step;
            if (transmittance < 0.01f)
                break;
        }
        t += dt;
    }

    // Far clouds fade into the haze of the horizon.
    float distance = weight > 0.0f ? near / weight : start;
    float fade = exp(-max(distance - FadeStart, 0.0f) / FadeDistance);
    return float4(light * fade, lerp(1.0f, transmittance, fade));
}