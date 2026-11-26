using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>
/// MAP view drawn as an HDR image (light beyond white: sun, sky, lamps), then brought to the screen by a filmic curve,
/// like NodeRunner's High quality. Same multisampling and depth buffer as the window: what is drawn over it after
/// (wires, gizmos) still meets the scene's depth.
/// </summary>
public sealed class HdrImage : IDisposable
{
    readonly Device device; readonly DeviceContext ctx;
    readonly VertexShader vs; readonly PixelShader ps; readonly Buffer cb;
    Texture2D ms, resolved; RenderTargetView msRtv; ShaderResourceView resolvedSrv;
    int w, h, samples;

    public HdrImage(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("hdr.hlsl", "VSFull", "vs_4_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("hdr.hlsl", "PSComposite", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    void Ensure(int width, int height, int sampleCount)
    {
        if (ms != null && w == width && h == height && samples == sampleCount) return;
        msRtv?.Dispose(); ms?.Dispose(); resolvedSrv?.Dispose(); resolved?.Dispose();
        w = width; h = height; samples = sampleCount;
        ms = new Texture2D(device, new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(samples, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        msRtv = new RenderTargetView(device, ms);
        resolved = new Texture2D(device, new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        resolvedSrv = new ShaderResourceView(device, resolved);
    }

    /// <summary>Binds the HDR image (window sized) with the window's depth buffer.</summary>
    public void Begin(GpuDevice gpu)
    {
        Ensure(gpu.Width, gpu.Height, gpu.SampleCount);
        ctx.OutputMerger.SetRenderTargets(gpu.DepthView, msRtv);
        ctx.ClearRenderTargetView(msRtv, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
    }

    /// <summary>Brings the image to the window (the current viewport only), the window's image bound again after.</summary>
    public void End(GpuDevice gpu, float exposure, float saturation = 1.05f)
    {
        ctx.ResolveSubresource(ms, 0, resolved, 0, Format.R16G16B16A16_Float);
        gpu.BindBackBuffer();
        var s = new System.Numerics.Vector4(exposure, 0, 0, saturation);
        ctx.UpdateSubresource(ref s, cb);
        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb); ctx.PixelShader.SetShaderResource(0, resolvedSrv);
        ctx.GeometryShader.Set(null);
        ctx.Draw(3, 0);
        ctx.PixelShader.SetShaderResource(0, null);
    }

    public void Dispose() { msRtv?.Dispose(); ms?.Dispose(); resolvedSrv?.Dispose(); resolved?.Dispose(); vs.Dispose(); ps.Dispose(); cb.Dispose(); }
}
