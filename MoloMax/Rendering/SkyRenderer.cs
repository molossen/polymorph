using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>The sky behind the MAP view : gradient of the hour, sun disc and glow; in the HDR image the sky
/// worked out from the air, from a small table made again when the sun moves (one full-view triangle, no depth).</summary>
public sealed class SkyRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct SkyVars { public Matrix4x4 InvViewProj; public Vector4 Zenith, Horizon, SunDir, Disc, Tint, TrueSun; }

    readonly Device device;
    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly Buffer cb;
    readonly SamplerState clamp;
    Texture2D table; ShaderResourceView tableSrv;
    float tableSunZ = float.NaN;

    public SkyRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("sky.hlsl", "VSMain", "vs_4_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("sky.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<SkyVars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        clamp = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue,
        });
    }

    /// <summary>The worked-out sky table for the sun at this height (made again only when the sun moved).</summary>
    public void PrepareTable(float sunZ, Vector3 balance)
    {
        if (MathF.Abs(MathF.Asin(Math.Clamp(sunZ, -1, 1)) - MathF.Asin(Math.Clamp(float.IsNaN(tableSunZ) ? 9 : tableSunZ, -1, 1))) < 0.002f && tableSrv != null) return;
        tableSunZ = sunZ;
        var data = MoloMax.Core.Gta.Look.Atmosphere.Table(sunZ, balance);
        tableSrv?.Dispose(); table?.Dispose();
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            table = new Texture2D(device, new Texture2DDescription
            {
                Width = MoloMax.Core.Gta.Look.Atmosphere.TableWidth, Height = MoloMax.Core.Gta.Look.Atmosphere.TableHeight, MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new SharpDX.DataRectangle(h.AddrOfPinnedObject(), MoloMax.Core.Gta.Look.Atmosphere.TableWidth * 16));
        }
        finally { h.Free(); }
        tableSrv = new ShaderResourceView(device, table);
    }

    /// <summary>Draws the sky (depth and blend states set by the caller: off / opaque). The older call: display colours.</summary>
    public void Draw(Matrix4x4 viewProj, Vector3 zenith, Vector3 horizon, Vector3 sunDir, float disc, Vector3 below)
        => Draw(viewProj, zenith, horizon, sunDir, disc, false, false, 0, Vector3.One, sunDir, 0);

    public void Draw(Matrix4x4 viewProj, Vector3 zenith, Vector3 horizon, Vector3 sunDir, float disc, bool hdr, bool physical, float night, Vector3 tint, Vector3 trueSun, float grey, ShaderResourceView clouds = null)
    {
        DepthMode.Rebind(ctx);
        Matrix4x4.Invert(viewProj, out var inv);
        var v = new SkyVars
        {
            InvViewProj = inv, Zenith = new(zenith, grey), Horizon = new(horizon, 1), SunDir = new(sunDir, disc),
            Disc = new(hdr ? 60 : 6, hdr ? 1 : 0, physical && tableSrv != null ? 1 : 0, night), Tint = new(tint, 0), TrueSun = new(trueSun, clouds != null ? 1 : 0),
        };
        ctx.UpdateSubresource(ref v, cb);
        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.SetSampler(0, clamp);
        ctx.PixelShader.SetShaderResource(0, physical ? tableSrv : null);
        ctx.PixelShader.SetShaderResource(1, clouds);
        ctx.GeometryShader.Set(null);
        ctx.Draw(3, 0);
        ctx.PixelShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(1, null);
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); cb.Dispose(); clamp.Dispose(); tableSrv?.Dispose(); table?.Dispose(); }
}
