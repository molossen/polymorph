using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>The sky behind the MAP view: gradient of the hour, sun disc and glow (one full-view triangle, no depth).</summary>
public sealed class SkyRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct SkyVars { public Matrix4x4 InvViewProj; public Vector4 Zenith, Horizon, SunDir, Below; }

    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly Buffer cb;

    public SkyRenderer(Device device)
    {
        ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("sky.hlsl", "VSMain", "vs_4_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("sky.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<SkyVars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    /// <summary>Draws the sky over the bound viewport (depth and blend states set by the caller: off / opaque).</summary>
    public void Draw(Matrix4x4 viewProj, Vector3 zenith, Vector3 horizon, Vector3 sunDir, float disc, Vector3 below)
    {
        Matrix4x4.Invert(viewProj, out var inv);
        var v = new SkyVars { InvViewProj = inv, Zenith = new(zenith, 1), Horizon = new(horizon, 1), SunDir = new(sunDir, disc), Below = new(below, 1) };
        ctx.UpdateSubresource(ref v, cb);
        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.GeometryShader.Set(null);
        ctx.Draw(3, 0);
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); cb.Dispose(); }
}
