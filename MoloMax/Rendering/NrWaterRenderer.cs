using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>NodeRunner's water in the MAP HDR image (premultiplied colour, drawn over the opaque scene, depth read).</summary>
public sealed class NrWaterRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vars
    {
        public Matrix4x4 ViewProjection; public Vector4 CameraTime, SunDirection, SunColor, SkyAmbient, Zenith, Horizon, Settings, Viewport, Options;
    }

    readonly DeviceContext ctx;
    readonly VertexShader vs; readonly PixelShader ps; readonly InputLayout layout; readonly Buffer cb; readonly BlendState premul;
    Buffer vb; int count;

    public NrWaterRenderer(Device device)
    {
        ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("nrwater.hlsl", "VSMain", "vs_5_0"))
        {
            vs = new VertexShader(device, b);
            layout = new InputLayout(device, b, new[] { new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0) });
        }
        using (var b = ShaderLoader.Compile("nrwater.hlsl", "PSMain", "ps_5_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Vars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true, SourceBlend = BlendOption.One, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.Zero, DestinationAlphaBlend = BlendOption.One, AlphaBlendOperation = BlendOperation.Add, RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        premul = new BlendState(device, bd);
    }

    public bool HasSurfaces => count > 0;

    public void SetSurfaces(Device device, IReadOnlyList<Vector3> tris)
    {
        vb?.Dispose(); vb = null; count = tris.Count;
        if (count > 0) vb = Buffer.Create(device, BindFlags.VertexBuffer, tris.ToArray());
    }

    public void Draw(in Vars v, ShaderResourceView color, ShaderResourceView depth)
    {
        if (vb == null) return;
        var x = v; ctx.UpdateSubresource(ref x, cb);
        ctx.OutputMerger.SetBlendState(premul, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, 12, 0));
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.SetShaderResource(0, depth); ctx.PixelShader.SetShaderResource(1, color);
        ctx.GeometryShader.Set(null);
        ctx.Draw(count, 0);
        ctx.PixelShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(1, null);
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); layout.Dispose(); cb.Dispose(); premul.Dispose(); vb?.Dispose(); }
}
