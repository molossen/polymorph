using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MoloMax.Core.Gta.Look;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>
/// Clouds in volume over the MAP sky (the): drawn into a smaller image, then
/// the sky is drawn over with it. The cloud shapes are made on the processor a moment after start-up.
/// </summary>
public sealed class CloudRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants { public Matrix4x4 InverseViewProjection; public Vector4 Eye, Layer, Look, Wind, SunDir, SunLight, SkyLight, GroundLight, View; }

    static readonly (int Divider, int Steps, int LightSteps)[] Qualities = { (4, 24, 3), (2, 48, 5), (2, 72, 6) };
    const float WindPeriod = 168000f;

    readonly Device device; readonly DeviceContext ctx;
    readonly VertexShader vs; readonly PixelShader ps; readonly Buffer cb; readonly SamplerState wrap;
    readonly System.Threading.Tasks.Task<(byte[] Shape, byte[] Weather)> noise;
    Texture3D shape; ShaderResourceView shapeSrv; Texture2D weatherTex; ShaderResourceView weatherSrv;
    Texture2D image; ShaderResourceView imageSrv; RenderTargetView imageRtv; int width, height;
    readonly Stopwatch clock = Stopwatch.StartNew(); double lastTime; Vector3 drift; int frame;

    public CloudRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("clouds.hlsl", "VS", "vs_5_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("clouds.hlsl", "PS", "ps_5_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Constants>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        wrap = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaximumLod = float.MaxValue });
        noise = System.Threading.Tasks.Task.Run(() => (CloudNoise.Shape(), CloudNoise.Weather()));
    }

    bool EnsureNoise()
    {
        if (shapeSrv != null) return true;
        if (!noise.IsCompletedSuccessfully) return false;
        var (sh, we) = noise.Result;
        const int s = CloudNoise.ShapeSize, w = CloudNoise.WeatherSize;
        var h1 = GCHandle.Alloc(sh, GCHandleType.Pinned);
        try
        {
            shape = new Texture3D(device, new Texture3DDescription { Width = s, Height = s, Depth = s, MipLevels = 1, Format = Format.R8G8_UNorm, Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource },
                new[] { new SharpDX.DataBox(h1.AddrOfPinnedObject(), s * 2, s * s * 2) });
        }
        finally { h1.Free(); }
        var h2 = GCHandle.Alloc(we, GCHandleType.Pinned);
        try
        {
            weatherTex = new Texture2D(device, new Texture2DDescription { Width = w, Height = w, MipLevels = 1, ArraySize = 1, Format = Format.R8G8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource },
                new SharpDX.DataRectangle(h2.AddrOfPinnedObject(), w * 2));
        }
        finally { h2.Free(); }
        shapeSrv = new ShaderResourceView(device, shape); weatherSrv = new ShaderResourceView(device, weatherTex);
        return true;
    }

    void EnsureImage(int w, int h)
    {
        if (image != null && w == width && h == height) return;
        imageRtv?.Dispose(); imageSrv?.Dispose(); image?.Dispose();
        width = w; height = h;
        image = new Texture2D(device, new Texture2DDescription { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource });
        imageSrv = new ShaderResourceView(device, image); imageRtv = new RenderTargetView(device, image);
    }

    /// <summary>Draws the clouds of the view into their own image (binds it: bind the scene's image again after). Null:
    /// no clouds yet.</summary>
    public ShaderResourceView Render(Matrix4x4 viewProjection, Vector3 camera, in MapLighting lighting, CloudWeather weather, int viewWidth, int viewHeight, int quality, ShaderResourceView sceneDepth = null, float viewX = 0, float viewY = 0)
    {
        double now = clock.Elapsed.TotalSeconds;
        float elapsed = (float)Math.Clamp(now - lastTime, 0.0, 0.25); lastTime = now;
        var windDirection = Vector3.Normalize(new Vector3(1f, 0.35f, 0.04f));
        drift += windDirection * weather.Wind * elapsed;
        drift = new Vector3(drift.X % WindPeriod, drift.Y % WindPeriod, drift.Z % WindPeriod);
        if (!EnsureNoise() || !Matrix4x4.Invert(viewProjection, out var inverse)) return null;
        var (divider, steps, lightSteps) = Qualities[Math.Clamp(quality, 1, Qualities.Length) - 1];
        EnsureImage(Math.Max(1, viewWidth / divider), Math.Max(1, viewHeight / divider));
        frame = (frame + 1) % 64;
        var sunDirection = lighting.Sun.Z > -0.1f && lighting.Sun != Vector3.Zero ? Vector3.Normalize(lighting.Sun) : lighting.SunDirection;
        var tint = lighting.PhysicalSky ? Atmosphere.Transmittance(Vector3.Normalize(sunDirection with { Z = MathF.Max(sunDirection.Z, 0.035f) })) : Vector3.One;
        var sunLight = lighting.SunColor * tint;
        float night = Math.Clamp(lighting.NightLights, 0f, 1f);
        float lum = 0.2126f * sunLight.X + 0.7152f * sunLight.Y + 0.0722f * sunLight.Z;
        sunLight = Vector3.Lerp(sunLight, Vector3.Lerp(sunLight, new Vector3(lum), 0.6f) * 0.35f, night);
        var c = new Constants
        {
            InverseViewProjection = inverse, Eye = new Vector4(camera, 0),
            Layer = new Vector4(weather.Base, weather.Thickness, Math.Clamp(weather.Coverage, 0f, 1f), weather.Density * 0.03f),
            Look = new Vector4(weather.Heaps, weather.Dark, steps, lightSteps), Wind = new Vector4(drift, sceneDepth != null ? 1f : 0f),
            View = new Vector4(viewX, viewY, viewWidth, viewHeight),
            SunDir = new Vector4(sunDirection, 0f), SunLight = new Vector4(sunLight, 0f),
            SkyLight = new Vector4(lighting.SkyAmbient * (1f - 0.6f * night), 0f), GroundLight = new Vector4(lighting.GroundAmbient * (1f - 0.6f * night), 0f),
        };
        ctx.UpdateSubresource(ref c, cb);
        ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, imageRtv);
        ctx.Rasterizer.SetViewport(0, 0, width, height);
        ctx.Rasterizer.SetScissorRectangle(0, 0, width, height);
        ctx.InputAssembler.InputLayout = null; ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.SetShaderResource(0, shapeSrv); ctx.PixelShader.SetShaderResource(1, weatherSrv); ctx.PixelShader.SetShaderResource(2, sceneDepth);
        ctx.PixelShader.SetSampler(0, wrap);
        ctx.GeometryShader.Set(null);
        ctx.OutputMerger.SetBlendState(null, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.Draw(3, 0);
        ctx.PixelShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(1, null); ctx.PixelShader.SetShaderResource(2, null);
        return imageSrv;
    }

    public void Dispose() { imageRtv?.Dispose(); imageSrv?.Dispose(); image?.Dispose(); weatherSrv?.Dispose(); weatherTex?.Dispose(); shapeSrv?.Dispose(); shape?.Dispose(); wrap.Dispose(); cb.Dispose(); ps.Dispose(); vs.Dispose(); }
}
