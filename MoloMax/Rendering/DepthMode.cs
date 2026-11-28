using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace MoloMax.Rendering;

/// <summary>Which way the depth of the window runs while a view is drawn. Standard: 0 at the near plane, 1 at the far
/// one. Reversed (MAP views): 1 near, 0 far, in a 32-bit float depth buffer: the depth stays precise to millimetres
/// kilometres away (with standard depth, surfaces laid a few centimetres over others - decals, night windows, props
/// on props - flickered through each other from a few hundred metres). The shaders that read the depth get the mode
/// in constant buffer b13 (DepthMode.x = 1: reversed) and turn a reversed value into a standard one (1 - d).</summary>
public static class DepthMode
{
    /// <summary>Set while a reversed view (or its pick pass) is drawn; false otherwise (previews, shadow maps, model views).</summary>
    public static bool Reversed { get; private set; }

    /// <summary>The value the depth buffer is cleared to (the far end).</summary>
    public static float Far => Reversed ? 0f : 1f;

    static Buffer cb; static bool bound; static bool boundValue;

    public static void Set(DeviceContext ctx, bool reversed)
    {
        Reversed = reversed;
        if (cb == null)
            cb = new Buffer(ctx.Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        if (!bound || boundValue != reversed)
        {
            var v = new System.Numerics.Vector4(reversed ? 1 : 0, 0, 0, 0);
            ctx.UpdateSubresource(ref v, cb);
            boundValue = reversed; bound = true;
        }
        ctx.VertexShader.SetConstantBuffer(13, cb);
        ctx.PixelShader.SetConstantBuffer(13, cb);
    }

    /// <summary>Binds the mode's constant buffer again (another pass may have taken slot 13).</summary>
    public static void Rebind(DeviceContext ctx) => Set(ctx, Reversed);

    /// <summary>A projection matrix (standard depth) turned to reversed depth: clip z becomes w - z.</summary>
    public static System.Numerics.Matrix4x4 Reverse(System.Numerics.Matrix4x4 p)
    {
        p.M13 = p.M14 - p.M13; p.M23 = p.M24 - p.M23; p.M33 = p.M34 - p.M33; p.M43 = p.M44 - p.M43;
        return p;
    }
}
