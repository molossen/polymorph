// MoloMax - GTA V basic surface shader (forward).
// Lighting reproduces CodeWalker's BasicPS (the reference viewer for GTA V drawables):
//  * diffuse with opaque alpha test at 0.33 (decals/glass blend instead)
//  * normal map decoded from RG, Z rebuilt from the unscaled XY, bumpiness scales only XY
//  * spec intensity = dot(spec.xy^2, spec.z ; specMapIntMask) * specularIntensityMult,
//    sun highlight = LightDirColour * 0.00006 * (exp(10*saturate(R.L)) - 1) * spec.x * mult
//  * GlobalLighting: albedo * (dirAmb + sun * N.L) + hemisphere natural ambient * vc0.r
//    + hemisphere artificial ambient * saturate(vc0.g)   (vertex colour R/G are the ambient bakes)
//  * emissive adds the unlit albedo on top (times EmissiveMult)
// Matrices are row_major and used as mul(rowVector, M), matching System.Numerics.

cbuffer SceneVars : register(b0)
{
    row_major float4x4 ViewProj;
    float4 CameraPos;
    float3 LightDir;            float LightHdr;
    float4 LightDirColour;
    float4 LightDirAmbColour;
    float4 NaturalAmbUp;
    float4 NaturalAmbDown;
    float4 ArtificialAmbUp;
    float4 ArtificialAmbDown;
    uint   RenderMode;          // 0 realistic, 1 shaded (wire colour), 2 vertex colour, 3 normals, 4 UV checker, 5 unlit
    float  Exposure;
    float  AmbientBoost;        // adds a little light so unlit areas stay readable while modelling
    float  ScenePad;
};

cbuffer ObjectVars : register(b1)
{
    row_major float4x4 World;
    row_major float4x4 NormalMatrix; // transpose(inverse(World))
    float4 WireColour;
    float4 Highlight;                // rgb, a = strength (frozen/selected tint)
    float4 UvRow0;                   // UV animation (globalAnimUV0 / 1): u' = dot(xyz, (u, v, 1)); w = 1 when animated
    float4 UvRow1;
};

cbuffer MaterialVars : register(b2)
{
    uint  EnableTexture;
    uint  EnableNormalMap;
    uint  EnableSpecMap;
    uint  EnableDetailMap;
    uint  IsDecal;
    uint  IsEmissive;
    uint  IsGlass;
    uint  AlphaTest;
    float bumpiness;
    float specularIntensityMult;
    float specularFalloffMult;
    float specularFresnel;
    float3 specMapIntMask;
    float EmissiveMult;
    float4 detailSettings;
    float4 ViewportColour;
    uint  IsTerrain;            // terrain_cb_w_4lyr: 4 layers blended by vertex colour 1 (b, g)
    uint3 TerrainPad;
};

// Scene lights (realistic mode), sun shadow map and the final tone curve.
#define MAX_LIGHTS 32
cbuffer LightVars : register(b3)
{
    row_major float4x4 ShadowViewProj;
    uint  LightCount;
    uint  SceneFlags;           // 1 filmic tone curve, 2 sun shadows, 4 scene lights
    float ShadowTexel;
    float ShadowStrength;
    float4 LPos[MAX_LIGHTS];    // xyz position, w range
    float4 LCol[MAX_LIGHTS];    // rgb colour * intensity, w kind (0 point, 1 spot, 2 capsule)
    float4 LDir[MAX_LIGHTS];    // xyz direction (spot) / axis (capsule), w capsule half length
    float4 LPar[MAX_LIGHTS];    // cos inner, cos outer, falloff exponent, source radius
    float4 LFac[MAX_LIGHTS];    // diffuse factor, specular factor
    float4 FaceFront;           // face orientation overlay colours (flag 8)
    float4 FaceBack;
};

// MAP: far models are cut away inside these boxes (over the models the user hid)
cbuffer ClipVars : register(b4)
{
    float4 ClipMin[16];
    float4 ClipMax[16];
    float4 ClipCount;
};

Texture2D<float4> Colourmap : register(t0);
Texture2D<float> ShadowMap : register(t5);
SamplerComparisonState ShadowSS : register(s1);
Texture2D<float4> Bumpmap   : register(t2);
Texture2D<float4> Specmap   : register(t3);
Texture2D<float4> Detailmap : register(t4);
Texture2D<float4> Layer0 : register(t6);
Texture2D<float4> Layer1 : register(t7);
Texture2D<float4> Layer2 : register(t8);
Texture2D<float4> Layer3 : register(t9);
Texture2D<float4> LayerBump0 : register(t10);
Texture2D<float4> LayerBump1 : register(t11);
Texture2D<float4> LayerBump2 : register(t12);
Texture2D<float4> LayerBump3 : register(t13);
SamplerState TextureSS : register(s0);

struct VS_INPUT
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Colour0  : COLOR0;
    float2 Texcoord : TEXCOORD0;
    float4 Tangent  : TANGENT;
    float4 Colour1  : COLOR1;
};

struct VS_OUTPUT
{
    float4 Position  : SV_POSITION;
    float3 Normal    : NORMAL;
    float2 Texcoord  : TEXCOORD0;
    float4 Colour0   : COLOR0;
    float4 Tangent   : TEXCOORD1;
    float4 Bitangent : TEXCOORD2;
    float3 WorldPos  : TEXCOORD3;
    float4 Colour1   : COLOR1;
};

VS_OUTPUT VSMain(VS_INPUT input)
{
    VS_OUTPUT o;
    float4 wp = mul(float4(input.Position, 1.0), World);
    o.Position = mul(wp, ViewProj);
    o.WorldPos = wp.xyz;
    float3 n = normalize(mul(input.Normal, (float3x3)NormalMatrix));
    float3 t = normalize(mul(input.Tangent.xyz, (float3x3)World));
    o.Normal = n;
    o.Tangent = float4(t, input.Tangent.w);
    o.Bitangent = float4(cross(t, n) * input.Tangent.w, 0);
    o.Texcoord = input.Texcoord;
    if (UvRow0.w > 0.5) o.Texcoord = float2(dot(UvRow0.xyz, float3(input.Texcoord, 1)), dot(UvRow1.xyz, float3(input.Texcoord, 1)));
    o.Colour0 = input.Colour0;
    o.Colour1 = input.Colour1;
    return o;
}

// Instanced draw (map mode: one archetype placed many times): the world matrix comes per instance.
struct VS_INPUT_INST
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Colour0  : COLOR0;
    float2 Texcoord : TEXCOORD0;
    float4 Tangent  : TANGENT;
    float4 Colour1  : COLOR1;
    float4 R0 : TEXCOORD4;
    float4 R1 : TEXCOORD5;
    float4 R2 : TEXCOORD6;
    float4 R3 : TEXCOORD7;
};

VS_OUTPUT VSInstanced(VS_INPUT_INST input)
{
    VS_OUTPUT o;
    float4x4 W = float4x4(input.R0, input.R1, input.R2, input.R3); // rows, used as mul(rowVector, W)
    float4 wp = mul(float4(input.Position, 1.0), W);
    o.Position = mul(wp, ViewProj);
    o.WorldPos = wp.xyz;
    float3x3 W3 = (float3x3)W;
    float3 n = normalize(mul(input.Normal, W3));
    float3 t = normalize(mul(input.Tangent.xyz, W3));
    o.Normal = n;
    o.Tangent = float4(t, input.Tangent.w);
    o.Bitangent = float4(cross(t, n) * input.Tangent.w, 0);
    o.Texcoord = input.Texcoord;
    o.Colour0 = input.Colour0;
    o.Colour1 = input.Colour1;
    return o;
}

float3 NormalMap(float2 nmv, float bumpinezz, float3 norm, float3 tang, float3 bita)
{
    float2 nxy = nmv.xy * 2 - 1;
    float2 bxy = nxy * max(bumpinezz, 0.001);
    float bxyz = sqrt(abs(1 - dot(nxy, nxy)));
    float3 t1 = tang * bxy.x;
    float3 t2 = bita * bxy.y + t1;
    float3 t3 = norm * bxyz + t2;
    return normalize(t3);
}

// Sun + sky the way RAGE does it (linear albedo): natural ambient wraps from the sky colour down, scaled by the baked
// natural amount (vertex colour R); the directional ambient follows N.L; artificial ambient scaled by vertex colour G.
float3 GlobalLighting(float3 diff, float3 norm, float4 vc0, float lf)
{
    float downMult = max(0.0, (norm.z + 1.0) * 0.5);
    float natS = saturate(vc0.r), artS = saturate(vc0.g);
    float3 amb = (NaturalAmbUp.rgb * downMult + NaturalAmbDown.rgb) * natS;
    amb += LightDirAmbColour.rgb * saturate(dot(LightDir, norm)) * natS;
    amb += (ArtificialAmbUp.rgb * downMult + ArtificialAmbDown.rgb) * artS;
    return diff * LightDirColour.rgb * lf + amb * diff;
}

float3 SrgbToLinear(float3 c) { return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045)); }

// sun visibility from the shadow map, 3x3 PCF (soft edge)
float SunShadow(float3 wp, float3 n)
{
    if ((SceneFlags & 2) == 0) return 1;
    float4 sp = mul(float4(wp + n * ShadowTexel * 1.5, 1), ShadowViewProj);
    float2 uv = sp.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv > 1) || sp.z > 1) return 1;
    float z = sp.z - 0.0015;
    float sum = 0;
    float t = 1.0 / 2048.0;
    [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
            sum += ShadowMap.SampleCmpLevelZero(ShadowSS, uv + float2(x, y) * t * 1.25, z);
    return lerp(1, sum / 9.0, ShadowStrength);
}

// GTA lights like the game: distance falloff = RagePowApprox(1 - d^2/R^2, exponent), linear spot cone, capsule = nearest
// point on a segment; normalised Blinn-Phong with a Schlick fresnel (exponent from the spec map green x falloff mult).
float RagePowApprox(float a, float b) { return a / ((1.0 - b) * a + b); }

float3 SceneLights(float3 albedo, float3 n, float3 wp, float3 v, float specR, float specE, float fres)
{
    float3 sum = 0;
    [loop] for (uint i = 0; i < LightCount; i++)
    {
        float3 lp = LPos[i].xyz;
        uint kind = (uint)LCol[i].w;
        if (kind == 2)
        {
            float3 a = lp - LDir[i].xyz * LDir[i].w, b = lp + LDir[i].xyz * LDir[i].w;
            float3 ab = b - a;
            float t = saturate(dot(wp - a, ab) / max(dot(ab, ab), 1e-6));
            lp = a + ab * t;
        }
        float3 d = lp - wp;
        float dist = length(d);
        float range = max(LPos[i].w, 0.01);
        if (dist >= range) continue;
        float3 L = d / max(dist, 1e-4);
        float atten = RagePowApprox(saturate(1.0 - (dist * dist) / (range * range)), max(LPar[i].z, 0.0));
        if (kind == 1) atten *= saturate((dot(-L, LDir[i].xyz) - LPar[i].y) / max(LPar[i].x - LPar[i].y, 1e-4));
        if (atten <= 0) continue;
        float nl = saturate(dot(n, L));
        float3 rad = LCol[i].rgb * atten * nl;
        sum += albedo * rad * LFac[i].x;
        float3 h = normalize(L + v);
        // a bigger source gives a wider, softer highlight
        float e = specE / (1 + LPar[i].w * 8 / max(dist, 0.05));
        float F = (1.0 - fres) + fres * pow(1.0 - saturate(dot(h, v)), 5.0);
        float sp = F * pow(saturate(dot(n, h)), e) * (2.0 + e) / 8.0;
        sum += rad * sp * specR * LFac[i].y;
    }
    return sum;
}

// linear light -> screen: RAGE's tone curve (mid grey kept where the old view had it, highlights roll off instead of
// clipping), or the filmic fit; then gamma 2.2
float3 ToneCurve(float3 c)
{
    c = max(c, 0);
    if ((SceneFlags & 1) != 0) c = saturate(c * (2.51 * c + 0.03) / (c * (2.43 * c + 0.59) + 0.14));
    else
    {
        c *= 0.72 / 0.64;
        c *= (1.0 + c / 1.5);
        c /= (1.0 + c);
    }
    return pow(saturate(c), 1.0 / 2.2);
}

float Checker(float2 uv)
{
    float2 f = floor(uv * 8.0);
    return fmod(abs(f.x + f.y), 2.0) < 1.0 ? 0.85 : 0.35;
}

float4 PSMain(VS_OUTPUT input, bool front : SV_IsFrontFace) : SV_TARGET
{
    [loop] for (uint ci = 0; ci < (uint)ClipCount.x; ci++)
        if (all(input.WorldPos >= ClipMin[ci].xyz) && all(input.WorldPos <= ClipMax[ci].xyz)) discard;
    float3 nrm = normalize(input.Normal);
    if ((SceneFlags & 8) != 0)
    {
        // face orientation: front faces one colour, back faces (flipped normals) the other, lightly shaded
        float3 fn = front ? nrm : -nrm;
        float d = 0.55 + 0.45 * abs(dot(fn, normalize(CameraPos.xyz - input.WorldPos)));
        return float4((front ? FaceFront.rgb : FaceBack.rgb) * d, IsGlass > 0 ? 0.45 : 1);
    }


    if (RenderMode == 1) // 3ds Max "Shaded": object colour, two simple lights
    {
        float3 L1 = normalize(float3(0.4, -0.6, 0.7));
        float d = saturate(dot(nrm, L1)) * 0.75 + saturate(dot(nrm, -L1)) * 0.15 + 0.25;
        float3 sc = WireColour.rgb * d;
        sc = lerp(sc, Highlight.rgb, Highlight.a);
        return float4(sc, 1);
    }
    if (RenderMode == 2) return float4(IsTerrain > 0 ? input.Colour1.rgb : input.Colour0.rgb, 1);
    if (RenderMode == 3) return float4(nrm * 0.5 + 0.5, 1);
    if (RenderMode == 4)
    {
        float k = Checker(input.Texcoord);
        return float4(k, k * 0.92, k * 0.85, 1);
    }

    float4 c = float4(ViewportColour.rgb, 1);
    // terrain: the 4 layers mixed like the game (blue mixes 0-1 and 2-3, green mixes the two pairs)
    float tb = saturate(input.Colour1.b), tg = saturate(input.Colour1.g);
    if (IsTerrain > 0)
    {
        float2 tc = input.Texcoord;
        float4 t1 = lerp(Layer0.Sample(TextureSS, tc), Layer1.Sample(TextureSS, tc), tb);
        float4 t2 = lerp(Layer2.Sample(TextureSS, tc), Layer3.Sample(TextureSS, tc), tb);
        c = lerp(t1, t2, tg);
        c.a = 1;
    }
    else if (EnableTexture > 0)
    {
        c = Colourmap.Sample(TextureSS, input.Texcoord);
        if (IsDecal == 0 && IsGlass == 0)
        {
            if (AlphaTest > 0 && c.a <= 0.33) discard;
            c.a = 1;
        }
        else if (c.a <= 0.0) discard;
    }
    if (IsDecal > 0) c.a *= input.Colour0.a;
    if (IsGlass > 0 && EnableTexture == 0) c.a = 0.35;

    if (RenderMode == 5) return float4(c.rgb * Exposure, c.a);

    float3 norm = nrm;
    float3 spec = 0;
    float4 nv = Bumpmap.Sample(TextureSS, input.Texcoord);
    if (IsTerrain > 0)
    {
        float2 tc = input.Texcoord;
        nv = lerp(lerp(LayerBump0.Sample(TextureSS, tc), LayerBump1.Sample(TextureSS, tc), tb),
                  lerp(LayerBump2.Sample(TextureSS, tc), LayerBump3.Sample(TextureSS, tc), tb), tg);
    }
    float4 sv = Specmap.Sample(TextureSS, input.Texcoord);
    if (EnableNormalMap > 0)
    {
        float2 nmv = nv.xy;
        if (EnableDetailMap > 0)
        {
            float4 r0;
            r0.xy = input.Texcoord * detailSettings.zw;
            r0.zw = r0.xy * 3.17;
            r0.xy = Detailmap.Sample(TextureSS, r0.xy).xy - 0.5;
            r0.zw = Detailmap.Sample(TextureSS, r0.zw).xy - 0.5;
            r0.xy = r0.xy + r0.zw;
            float2 dd = r0.xy * detailSettings.y;
            nmv = dd * sv.w + nv.xy;
        }
        norm = NormalMap(nmv, bumpiness, nrm, input.Tangent.xyz, input.Bitangent.xyz);
    }
    if (EnableSpecMap == 0) sv = float4(0.1, 0.1, 0.1, 0.1);
    float4 svRaw = sv;               // local lights use the map as is
    sv.xy = sv.xy * sv.xy;           // the sun term squares it (CodeWalker)

    float3 incident = normalize(input.WorldPos - CameraPos.xyz);
    float3 refl = normalize(reflect(incident, norm));
    float specb = saturate(dot(refl, LightDir));
    float specp = max(exp(specb * 10) - 1, 0);
    spec += LightDirColour.rgb * 0.00006 * specp * sv.x * specularIntensityMult;

    c.rgb = SrgbToLinear(saturate(c.rgb)); // light is added in linear space
    float4 fc = c;
    float sh = SunShadow(input.WorldPos, nrm);
    float lf = saturate(dot(norm, LightDir)) * sh;
    spec *= sh;
    float3 lit = GlobalLighting(c.rgb, norm, input.Colour0, lf) + spec;
    if ((SceneFlags & 4) != 0 && LightCount > 0)
    {
        float specR = dot(svRaw.xyz, specMapIntMask) * specularIntensityMult;
        float specE = max(svRaw.y * specularFalloffMult, 1.0);
        lit += SceneLights(c.rgb, norm, input.WorldPos, -incident, specR, specE, saturate(specularFresnel));
    }
    lit += c.rgb * AmbientBoost;
    if (IsEmissive > 0) lit += fc.rgb * EmissiveMult; // linear, unclamped: bright signs stay bright

    float3 outc = ToneCurve(lit * Exposure);
    outc = lerp(outc, Highlight.rgb, Highlight.a);
    if (IsGlass > 0) c.a = saturate(c.a + specp * 0.002);
    return float4(outc, saturate(c.a));
}
