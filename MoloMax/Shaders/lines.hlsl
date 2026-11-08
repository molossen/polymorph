// MoloMax — coloured lines and flat overlay triangles (grid, wireframe, gizmo, selection tint).
cbuffer LineVars : register(b0)
{
    row_major float4x4 ViewProj;
    float4 DepthBias;   // pull of wires / overlays towards the camera (keeps them on top of their own faces), see VSMain
    float4 Tint;        // multiplies the vertex colour (cached wires are white, tinted per object)
};

struct VS_INPUT
{
    float3 Position : POSITION;
    float4 Colour   : COLOR0;
};

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float4 Colour   : COLOR0;
};

VS_OUTPUT VSMain(VS_INPUT input)
{
    VS_OUTPUT o;
    o.Position = mul(float4(input.Position, 1.0), ViewProj);
    // x = requested pull, y = fraction of the distance per unit (perspective), z = projection's constant z term,
    // w = NDC per unit (orthographic). The point is moved towards the eye along its own ray: same place on screen.
    float b = DepthBias.x;
    if (DepthBias.y == 0 && DepthBias.w == 0) o.Position.z -= b * o.Position.w;
    else
    {
        float s = 1.0 - b * DepthBias.y;
        o.Position.z = (o.Position.z - DepthBias.z) * s + DepthBias.z - b * DepthBias.w * o.Position.w;
        o.Position.xyw *= s;
    }
    o.Colour = input.Colour * Tint;
    return o;
}

float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    return input.Colour;
}
