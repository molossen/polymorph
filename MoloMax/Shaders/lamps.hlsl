// MoloMax - the map's lamps at night lighting the surfaces around them (street lamps, lit windows, signs).
// One patch per lamp facing the camera; each pixel of it finds the surface behind from the depth, its facing from the
// depth next to it, and adds the lamp's light on it. The colour of the surface is worked out from a copy of the image
// drawn so far: that colour stands for the surface under the night's sky light, so (colour / sky light) is the surface's
// own colour, never more than white. A glowing thing (a bulb, a lit sign, a window) therefore takes no more light than a
// white wall would: multiplying what was on screen made every lamp globe and neon near a lamp blow up into a glare.
cbuffer LampVars : register(b0)
{
    row_major float4x4 ViewProj;   // camera-relative point -> image
    row_major float4x4 Unproject;  // image (x, y, depth) -> camera-relative point
    float4 Camera;   // xyz camera, w distance up to which the lamps light (m)
    float4 Right;    // xyz camera right, w strength (1 / the night's sky light)
    float4 Up;       // xyz camera up, w hour (0 to 23)
    float4 Origin;   // xy top-left of the view in the image (pixels), zw its size
    float4 Sky;      // rgb the night's sky light the surfaces were drawn under
};
struct Lamp
{
    float3 Position; float Reach; float3 Color; float Corona; float3 Direction; float CosOuter; float CosInner; uint Hours; float2 Spare;
};
StructuredBuffer<Lamp> Lamps : register(t0);
Texture2DMS<float> Depth : register(t1);
Texture2D<float4> Scene : register(t2);

static const float4 Hidden = float4(-4, -4, 0.5, 1);

struct LAMP_OUT { float4 pos : SV_POSITION; nointerpolation uint id : LAMP; nointerpolation float fade : FADE; };

LAMP_OUT VSLamp(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Lamp lamp = Lamps[instance];
    LAMP_OUT o;
    o.id = instance;
    float3 rel = lamp.Position - Camera.xyz;
    float dist = length(rel);
    o.fade = saturate((Camera.w - (dist - lamp.Reach)) / (0.2 * Camera.w));
    float2 c = float2((vertex & 1) ? 1 : -1, (vertex & 2) ? 1 : -1);
    bool on = ((lamp.Hours >> (uint)Up.w) & 1) != 0;
    if (!on || o.fade <= 0 || lamp.Reach <= 0) o.pos = Hidden;
    else if (dist < lamp.Reach * 1.5 + 2) o.pos = float4(c, 0.5, 1);
    else o.pos = mul(float4(rel + (Right.xyz * c.x + Up.xyz * c.y) * (lamp.Reach * 1.5), 1), ViewProj);
    return o;
}

float3 PointAt(int2 px)
{
    px = clamp(px, (int2)Origin.xy, (int2)(Origin.xy + Origin.zw) - 1);
    float d = Depth.Load(px, 0);
    float2 ndc = ((px + 0.5 - Origin.xy) / Origin.zw) * float2(2, -2) + float2(-1, 1);
    float4 p = mul(float4(ndc, d, 1), Unproject);
    return p.xyz / p.w;
}

float4 PSLamp(LAMP_OUT i) : SV_Target
{
    int2 px = int2(i.pos.xy);
    float d = Depth.Load(px, 0);
    if (d >= 1) discard;
    float3 p = PointAt(px);
    Lamp lamp = Lamps[i.id];
    float3 toLamp = (lamp.Position - Camera.xyz) - p;
    float d2 = dot(toLamp, toLamp), r2 = lamp.Reach * lamp.Reach;
    if (d2 >= r2) discard;
    // facing: from the surface next to it (the side that stays on the same surface)
    float3 px1 = PointAt(px + int2(1, 0)), nx1 = PointAt(px - int2(1, 0));
    float3 py1 = PointAt(px + int2(0, 1)), ny1 = PointAt(px - int2(0, 1));
    float3 dx = dot(px1 - p, px1 - p) < dot(nx1 - p, nx1 - p) ? px1 - p : p - nx1;
    float3 dy = dot(py1 - p, py1 - p) < dot(ny1 - p, ny1 - p) ? py1 - p : p - ny1;
    float3 n = normalize(cross(dx, dy));
    if (dot(n, -p) < 0) n = -n;
    float dd = sqrt(d2);
    float3 l = toLamp / max(dd, 1e-4);
    float facing = saturate(dot(n, l) * 0.85 + 0.15);
    float window = 1 - d2 / r2;
    float strength = window * window * 3 / (1 + d2 * 0.02);
    if (lamp.CosOuter > -1.5) strength *= smoothstep(lamp.CosOuter, lamp.CosInner, dot(-l, lamp.Direction));
    float3 surface = saturate(Scene.Load(int3(px, 0)).rgb / Sky.rgb);
    // dark surfaces stay dark, but not black: a wet road or a dark wall still shows the pool of light
    surface = max(surface, 0.04);
    float3 light = min(lamp.Color * (strength * facing * Right.w * i.fade), 6);
    return float4(surface * light, 0);
}
