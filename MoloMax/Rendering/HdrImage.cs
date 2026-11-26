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
    // ambient occlusion (NodeRunner's): half-size image, blurred across then down keeping the edges
    VertexShader aoVs; PixelShader aoPs, blurPs; Buffer aoCb; SamplerState clamp; bool aoFailed;
    Texture2D ao, aoB; RenderTargetView aoRtv, aoBRtv; ShaderResourceView aoSrv, aoBSrv; int aoW, aoH;
    [StructLayout(LayoutKind.Sequential)] struct AoVars { public System.Numerics.Vector4 Projection, Size, Settings, Direction, Origin; }
    Texture2D ms, resolved; RenderTargetView msRtv; ShaderResourceView resolvedSrv;
    int w, h, samples;

    public HdrImage(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("hdr.hlsl", "VSFull", "vs_4_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("hdr.hlsl", "PSComposite", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, 32, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        clamp = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue });
        try
        {
            if (device.FeatureLevel < FeatureLevel.Level_11_0) throw new NotSupportedException();
            using (var b = ShaderLoader.Compile("ao.hlsl", "VSFull", "vs_5_0")) aoVs = new VertexShader(device, b);
            using (var b = ShaderLoader.Compile("ao.hlsl", "PSOcclusion", "ps_5_0")) aoPs = new PixelShader(device, b);
            using (var b = ShaderLoader.Compile("ao.hlsl", "PSBlur", "ps_5_0")) blurPs = new PixelShader(device, b);
            aoCb = new Buffer(device, Marshal.SizeOf<AoVars>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        }
        catch { aoFailed = true; }
    }

    void EnsureAo(int width, int height)
    {
        if (ao != null && aoW == width && aoH == height) return;
        aoRtv?.Dispose(); aoSrv?.Dispose(); ao?.Dispose(); aoBRtv?.Dispose(); aoBSrv?.Dispose(); aoB?.Dispose();
        aoW = width; aoH = height;
        Texture2D T() => new(device, new Texture2DDescription { Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource });
        ao = T(); aoB = T();
        aoRtv = new RenderTargetView(device, ao); aoSrv = new ShaderResourceView(device, ao);
        aoBRtv = new RenderTargetView(device, aoB); aoBSrv = new ShaderResourceView(device, aoB);
    }

    /// <summary>Ambient occlusion of the view (x, y, w, h) from the window's depth (bound nowhere else meanwhile).
    /// proj: the view's projection; near / far planes.</summary>
    bool Occlusion(GpuDevice gpu, float x, float y, float w, float h, System.Numerics.Matrix4x4 proj, float near, float far)
    {
        if (aoFailed || gpu.SampleCount < 2 || gpu.DepthRead == null) return false;
        int width = Math.Max(1, (int)(w / 2)), height = Math.Max(1, (int)(h / 2));
        EnsureAo(width, height);
        var v = new AoVars
        {
            Projection = new(proj.M11, proj.M22, near, 1.6f), Size = new(width, height, w, h), Settings = new(far, 0, 10, 300),
            Origin = new(x, y, w, h),
        };
        ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, aoRtv);
        ctx.Rasterizer.SetViewport(0, 0, width, height);
        ctx.Rasterizer.SetScissorRectangle(0, 0, width, height);
        ctx.InputAssembler.InputLayout = null; ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(aoVs); ctx.GeometryShader.Set(null);
        ctx.PixelShader.SetSampler(0, clamp);
        ctx.PixelShader.SetShaderResource(0, gpu.DepthRead);
        ctx.UpdateSubresource(ref v, aoCb);
        ctx.PixelShader.SetConstantBuffer(0, aoCb); ctx.VertexShader.SetConstantBuffer(0, aoCb);
        ctx.PixelShader.Set(aoPs);
        ctx.Draw(3, 0);
        ctx.PixelShader.Set(blurPs);
        foreach (var (from, to, dir) in new[] { (aoSrv, aoBRtv, new System.Numerics.Vector4(1, 0, 0, 0)), (aoBSrv, aoRtv, new System.Numerics.Vector4(0, 1, 0, 0)) })
        {
            v.Direction = dir; ctx.UpdateSubresource(ref v, aoCb);
            ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, to);
            ctx.PixelShader.SetShaderResource(2, from);
            ctx.Draw(3, 0);
            ctx.PixelShader.SetShaderResource(2, null);
        }
        ctx.PixelShader.SetShaderResource(0, null);
        ctx.Rasterizer.SetViewport(x, y, w, h);
        ctx.Rasterizer.SetScissorRectangle((int)x, (int)y, (int)(x + w), (int)(y + h));
        return true;
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
    public void End(GpuDevice gpu, float exposure, float x, float y, float w, float h, System.Numerics.Matrix4x4 proj, float near, float far, float aoShare, float saturation = 1.05f)
    {
        ctx.ResolveSubresource(ms, 0, resolved, 0, Format.R16G16B16A16_Float);
        bool hasAo = aoShare > 0 && Occlusion(gpu, x, y, w, h, proj, near, far);
        gpu.BindBackBuffer();
        var s = new[] { new System.Numerics.Vector4(exposure, hasAo ? aoShare : 0, 0, saturation), new System.Numerics.Vector4(x, y, w, h) };
        ctx.UpdateSubresource(s, cb);
        ctx.PixelShader.SetSampler(0, clamp);
        ctx.PixelShader.SetShaderResource(1, hasAo ? aoSrv : null);
        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb); ctx.PixelShader.SetShaderResource(0, resolvedSrv);
        ctx.GeometryShader.Set(null);
        ctx.Draw(3, 0);
        ctx.PixelShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(1, null);
    }

    public void Dispose() { msRtv?.Dispose(); ms?.Dispose(); resolvedSrv?.Dispose(); resolved?.Dispose(); vs.Dispose(); ps.Dispose(); cb.Dispose(); clamp.Dispose(); aoVs?.Dispose(); aoPs?.Dispose(); blurPs?.Dispose(); aoCb?.Dispose(); aoRtv?.Dispose(); aoSrv?.Dispose(); ao?.Dispose(); aoBRtv?.Dispose(); aoBSrv?.Dispose(); aoB?.Dispose(); }
}
