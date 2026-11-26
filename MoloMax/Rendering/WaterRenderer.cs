using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>The game's water surfaces in the MAP view (one vertex buffer, drawn blended after the opaque map, no depth write).</summary>
public sealed class WaterRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vars
    {
        public Matrix4x4 ViewProj; public Vector4 Eye, SunDir, Zenith, Horizon, Deep, FogColour, FogRange;
    }

    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly InputLayout layout;
    readonly Buffer cb;
    Buffer vb; int count;

    public WaterRenderer(Device device)
    {
        ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("water.hlsl", "VSMain", "vs_4_0"))
        {
            vs = new VertexShader(device, b);
            layout = new InputLayout(device, b, new[] { new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0) });
        }
        using (var b = ShaderLoader.Compile("water.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Vars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    public bool HasSurfaces => count > 0;

    public void SetSurfaces(Device device, IReadOnlyList<Vector3> tris)
    {
        vb?.Dispose(); vb = null; count = tris.Count;
        if (count == 0) return;
        var arr = tris.ToArray();
        vb = Buffer.Create(device, BindFlags.VertexBuffer, arr);
    }

    /// <summary>Draws the water (blend and depth states set by the caller: alpha blend, depth read).</summary>
    public void Draw(in Vars v)
    {
        if (vb == null) return;
        var x = v; ctx.UpdateSubresource(ref x, cb);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, 12, 0));
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.GeometryShader.Set(null);
        ctx.Draw(count, 0);
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); layout.Dispose(); cb.Dispose(); vb?.Dispose(); }
}
