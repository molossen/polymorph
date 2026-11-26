using System.Numerics;
using System.Runtime.InteropServices;
using MoloMax.Core.Gta.WaterLook;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace MoloMax.Rendering;

/// <summary>
/// The game's water in the MAP HDR image (taken from the reference map view): its surfaces from water.xml, the open sea
/// as a grid of rings around the camera moved by the game's swell, the zones of calm water and of waves rolling in, the
/// colour of each place (waterfog images), foam, light on the bottom, reflections of the scene and the bottom seen through
/// it. Drawn after the opaque map with a copy of its depth and colour; premultiplied blending.
/// </summary>
public sealed class GameWaterRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraTime, SunDirection, SunColor, SkyAmbient, Zenith, Horizon, Settings, Viewport, Options,
            WaveArea, MaskArea, OceanBounds, GridCenter, ColorArea, IslandArea, Caustics, Patterns;
    }
    [StructLayout(LayoutKind.Sequential)] struct WaterVertex { public Vector3 Position; public float Waves; public WaterVertex(Vector3 p, float w) { Position = p; Waves = w; } }
    [StructLayout(LayoutKind.Sequential)] struct GridVertex { public Vector2 Offset; public float Spacing; public GridVertex(Vector2 o, float s) { Offset = o; Spacing = s; } }

    const float OceanReach = 40_000f, InlandWaves = 0.35f;
    const int GridAngles = 320, GridRings = 200; const float GridInner = 0.6f;
    const float WaveTexel = 8f, MaskTexel = 4f;

    readonly Device device; readonly DeviceContext ctx;
    readonly VertexShader vs, gridVs; readonly PixelShader ps; readonly InputLayout layout, gridLayout;
    readonly Buffer gridVb, gridIb; readonly int gridCount;
    readonly Buffer cb, meshCb; readonly RasterizerState ras; readonly DepthStencilState depth; readonly BlendState blend;
    readonly SamplerState linearClamp, linearWrap;
    Constants c;
    (Buffer Buf, int Count) allSurfaces, inlandSurfaces;
    (Texture2D T, ShaderResourceView V)? waveTex, maskTex, colorTex, islandTex, causticTex;
    (Texture2D T, ShaderResourceView V)[] patterns;
    Vector4 waveArea, maskArea, oceanBounds, colorArea, islandArea;
    float oceanHeight; bool openWater, waves3D;

    public GameWaterRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("gamewater.hlsl", "VS", "vs_5_0"))
        {
            vs = new VertexShader(device, b);
            layout = new InputLayout(device, b, new[] { new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0), new InputElement("TEXCOORD", 0, Format.R32_Float, 12, 0) });
        }
        using (var b = ShaderLoader.Compile("gamewater.hlsl", "VSGrid", "vs_5_0"))
        {
            gridVs = new VertexShader(device, b);
            gridLayout = new InputLayout(device, b, new[] { new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0), new InputElement("TEXCOORD", 0, Format.R32_Float, 8, 0) });
        }
        using (var b = ShaderLoader.Compile("gamewater.hlsl", "PS", "ps_5_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Constants>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        meshCb = new Buffer(device, 80, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        (gridVb, gridIb, gridCount) = CreateGrid(device);
        // seen from above and from below (under water)
        ras = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, IsMultisampleEnabled = true, IsScissorEnabled = false });
        // standard depth, strictly in front: where two surfaces overlap at the same height, only the first is drawn
        depth = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Less });
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true, SourceBlend = BlendOption.One, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One, DestinationAlphaBlend = BlendOption.InverseSourceAlpha, AlphaBlendOperation = BlendOperation.Add, RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        blend = new BlendState(device, bd);
        linearClamp = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue });
        linearWrap = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue });
    }

    /// <summary>Rings of points around the centre (one in the middle), each a few percent further than the previous one.</summary>
    static (Buffer, Buffer, int) CreateGrid(Device device)
    {
        float growth = MathF.Pow(OceanReach / GridInner, 1f / (GridRings - 1));
        float spacingShare = MathF.Max(growth - 1f, 2f * MathF.PI / GridAngles);
        var vertices = new GridVertex[GridAngles * GridRings + 1];
        vertices[0] = new GridVertex(Vector2.Zero, GridInner);
        for (int ring = 0; ring < GridRings; ring++)
        {
            float radius = GridInner * MathF.Pow(growth, ring);
            for (int a = 0; a < GridAngles; a++)
            {
                float angle = 2f * MathF.PI * a / GridAngles;
                vertices[1 + ring * GridAngles + a] = new GridVertex(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, radius * spacingShare);
            }
        }
        var indices = new List<uint>(GridAngles * GridRings * 6);
        for (int a = 0; a < GridAngles; a++) indices.AddRange(new[] { 0u, (uint)(1 + a), (uint)(1 + (a + 1) % GridAngles) });
        for (int ring = 0; ring + 1 < GridRings; ring++)
            for (int a = 0; a < GridAngles; a++)
            {
                uint i0 = (uint)(1 + ring * GridAngles + a), i1 = (uint)(1 + ring * GridAngles + (a + 1) % GridAngles);
                uint o0 = i0 + GridAngles, o1 = i1 + GridAngles;
                indices.AddRange(new[] { i0, o0, i1, i1, o0, o1 });
            }
        var ia = indices.ToArray();
        return (Buffer.Create(device, BindFlags.VertexBuffer, vertices), Buffer.Create(device, BindFlags.IndexBuffer, ia), ia.Length);
    }

    public bool HasSurfaces => allSurfaces.Count > 0;

    /// <summary>The map's surfaces, the open ocean all around the main water file, its calm and wave zones and its look.</summary>
    public void SetSurfaces(WaterMap water)
    {
        DisposeSurfaces();
        if (water == null) return;
        var bounds = water.OceanBounds; float oh = water.OceanHeight;
        bool hasOcean = bounds.Z > bounds.X && bounds.W > bounds.Y;
        bool AtSeaLevel(WaterQuad q) => MathF.Abs(q.Z - oh) < 0.05f;
        var all = new List<WaterVertex>(); var inland = new List<WaterVertex>();
        foreach (var quad in water.Quads)
        {
            bool sea = AtSeaLevel(quad);
            foreach (var corner in quad.Triangles())
            {
                var v = new WaterVertex(corner, MathF.Abs(corner.Z - oh) < 0.5f ? 1f : InlandWaves);
                all.Add(v);
                if (!sea) inland.Add(v);
            }
        }
        if (hasOcean)
        {
            float far = OceanReach;
            AddRectangle(all, -far, bounds.Y, bounds.X, bounds.W, oh);
            AddRectangle(all, bounds.Z, bounds.Y, far, bounds.W, oh);
            AddRectangle(all, -far, -far, far, bounds.Y, oh);
            AddRectangle(all, -far, bounds.W, far, far, oh);
        }
        allSurfaces = Surfaces(all); inlandSurfaces = Surfaces(inland);
        oceanBounds = hasOcean ? bounds : Vector4.Zero; oceanHeight = oh;
        openWater = hasOcean || water.Quads.Any(AtSeaLevel);
        CreateMask(water.Quads.Where(AtSeaLevel).ToList());
        CreateWaveZones(water);
        if (water.ColorMaps.Count > 0) (colorTex, colorArea) = CreateColorMap(water.ColorMaps[0]);
        if (water.ColorMaps.Count > 1) (islandTex, islandArea) = CreateColorMap(water.ColorMaps[1]);
        if (water.Caustics is { } caustics) causticTex = CreatePattern(caustics);
        if (water is { OceanWaves: { } ocean, ShoreWaves: { } shore, Ripples: { } ripples, Foam: { } foam })
            patterns = new[] { CreatePattern(ocean), CreatePattern(shore), CreatePattern(ripples), CreatePattern(foam) };
    }

    /// <summary>A tiling pattern with all its smaller levels (averages of 2 x 2 texels, wrapping round).</summary>
    (Texture2D, ShaderResourceView) CreatePattern(WaterPattern pattern)
    {
        int channels = pattern.Channels;
        var levels = new List<(int Width, int Height, byte[] Values)> { (pattern.Width, pattern.Height, pattern.Values) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var (w, h, values) = levels[^1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * channels];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) sum += values[(((y * 2 + dy) % h) * w + (x * 2 + dx) % w) * channels + ch];
                        next[(y * nw + x) * channels + ch] = (byte)((sum + 2) / 4);
                    }
            levels.Add((nw, nh, next));
        }
        var handles = levels.Select(l => GCHandle.Alloc(l.Values, GCHandleType.Pinned)).ToArray();
        try
        {
            var boxes = levels.Select((l, i) => new DataBox(handles[i].AddrOfPinnedObject(), l.Width * channels, l.Width * channels * l.Height)).ToArray();
            var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = pattern.Width, Height = pattern.Height, MipLevels = levels.Count, ArraySize = 1,
                Format = channels == 2 ? Format.R8G8_UNorm : Format.R8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, boxes);
            return (tex, new ShaderResourceView(device, tex));
        }
        finally { foreach (var h in handles) h.Free(); }
    }

    /// <summary>The image's first row is the north edge: sampled from the north-west corner, y going south.</summary>
    ((Texture2D, ShaderResourceView), Vector4) CreateColorMap(WaterColorMap map)
    {
        var area = map.Area;
        return (CreateTexture(map.Width, map.Height, Format.R8G8B8A8_UNorm, map.Rgba, 4), new Vector4(area.X, area.W, 1f / (area.Z - area.X), -1f / (area.W - area.Y)));
    }

    (Buffer, int) Surfaces(List<WaterVertex> v) => v.Count == 0 ? (null, 0) : (Buffer.Create(device, BindFlags.VertexBuffer, v.ToArray()), v.Count);

    static void AddRectangle(List<WaterVertex> v, float minX, float minY, float maxX, float maxY, float z)
    {
        var a = new WaterVertex(new Vector3(minX, minY, z), 1f); var b = new WaterVertex(new Vector3(maxX, minY, z), 1f);
        var c = new WaterVertex(new Vector3(minX, maxY, z), 1f); var d = new WaterVertex(new Vector3(maxX, maxY, z), 1f);
        v.AddRange(new[] { a, b, c, c, b, d });
    }

    /// <summary>Where the map has open water (surfaces at the sea's height), one byte per few metres: 255 water, 0 land.</summary>
    void CreateMask(List<WaterQuad> quads)
    {
        if (quads.Count == 0) return;
        float minX = quads.Min(q => q.MinX), minY = quads.Min(q => q.MinY), maxX = quads.Max(q => q.MaxX), maxY = quads.Max(q => q.MaxY);
        float texel = MathF.Max(MaskTexel, MathF.Max(maxX - minX, maxY - minY) / 4096f);
        int width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / texel)), height = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / texel));
        var mask = new byte[width * height];
        foreach (var q in quads)
        {
            int x0 = Math.Max(0, (int)((q.MinX - minX) / texel) - 1), x1 = Math.Min(width - 1, (int)((q.MaxX - minX) / texel) + 1);
            int y0 = Math.Max(0, (int)((q.MinY - minY) / texel) - 1), y1 = Math.Min(height - 1, (int)((q.MaxY - minY) / texel) + 1);
            for (int y = y0; y <= y1; y++)
            {
                float py = minY + (y + 0.5f) * texel;
                for (int x = x0; x <= x1; x++) if (q.Contains(minX + (x + 0.5f) * texel, py)) mask[y * width + x] = 255;
            }
        }
        maskTex = CreateTexture(width, height, Format.R8_UNorm, mask, 1);
        maskArea = new Vector4(minX, minY, 1f / (width * texel), 1f / (height * texel));
    }

    /// <summary>The calm zones and the zones of waves rolling in, blurred so their edges do not show.</summary>
    void CreateWaveZones(WaterMap water)
    {
        if (water.Calming.Count == 0 && water.Waves.Count == 0) return;
        var zones = water.Calming.Select(z => (z.MinX, z.MinY, z.MaxX, z.MaxY)).Concat(water.Waves.Select(w => (w.MinX, w.MinY, w.MaxX, w.MaxY))).ToList();
        float margin = 8f * WaveTexel;
        float minX = zones.Min(z => z.MinX) - margin, minY = zones.Min(z => z.MinY) - margin;
        float maxX = zones.Max(z => z.MaxX) + margin, maxY = zones.Max(z => z.MaxY) + margin;
        float texel = MathF.Max(WaveTexel, MathF.Max(maxX - minX, maxY - minY) / 4096f);
        int width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / texel)), height = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / texel));
        int count = width * height;
        var calm = new float[count]; var size = new float[count]; var wayX = new float[count]; var wayY = new float[count];
        Array.Fill(calm, 1f);
        (int X0, int X1, int Y0, int Y1) Cells(float zx0, float zy0, float zx1, float zy1) =>
            (Math.Max(0, (int)((zx0 - minX) / texel)), Math.Min(width - 1, (int)((zx1 - minX) / texel)), Math.Max(0, (int)((zy0 - minY) / texel)), Math.Min(height - 1, (int)((zy1 - minY) / texel)));
        foreach (var z in water.Calming)
        {
            var (x0, x1, y0, y1) = Cells(z.MinX, z.MinY, z.MaxX, z.MaxY);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) calm[y * width + x] = MathF.Min(calm[y * width + x], z.Dampening);
        }
        foreach (var z in water.Waves)
        {
            var (x0, x1, y0, y1) = Cells(z.MinX, z.MinY, z.MaxX, z.MaxY);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * width + x;
                    size[i] = MathF.Max(size[i], z.Amplitude); wayX[i] += z.Direction.X * z.Amplitude; wayY[i] += z.Direction.Y * z.Amplitude;
                }
        }
        foreach (var ch in new[] { calm, size, wayX, wayY }) Blur(ch, width, height, 4);
        var texels = new byte[count * 4];
        static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
        for (int i = 0; i < count; i++)
        {
            var way = new Vector2(wayX[i], wayY[i]);
            way = way.LengthSquared() > 1e-8f ? Vector2.Normalize(way) : Vector2.Zero;
            texels[i * 4] = B(calm[i]); texels[i * 4 + 1] = B(size[i] / 2.5f); texels[i * 4 + 2] = B(way.X * 0.5f + 0.5f); texels[i * 4 + 3] = B(way.Y * 0.5f + 0.5f);
        }
        waveTex = CreateTexture(width, height, Format.R8G8B8A8_UNorm, texels, 4);
        waveArea = new Vector4(minX, minY, 1f / (width * texel), 1f / (height * texel));
    }

    static void Blur(float[] values, int width, int height, int radius)
    {
        var line = new float[Math.Max(width, height)];
        for (int pass = 0; pass < 2; pass++)
        {
            bool across = pass == 0;
            int lines = across ? height : width, length = across ? width : height;
            for (int l = 0; l < lines; l++)
            {
                int Index(int i) => across ? l * width + i : i * width + l;
                for (int i = 0; i < length; i++) line[i] = values[Index(i)];
                float sum = 0f; int n = 0;
                for (int i = -radius; i <= radius; i++) if (i >= 0 && i < length) { sum += line[i]; n++; }
                for (int i = 0; i < length; i++)
                {
                    values[Index(i)] = sum / Math.Max(n, 1);
                    int leaving = i - radius, entering = i + radius + 1;
                    if (leaving >= 0) { sum -= line[leaving]; n--; }
                    if (entering < length) { sum += line[entering]; n++; }
                }
            }
        }
    }

    (Texture2D, ShaderResourceView) CreateTexture(int width, int height, Format format, byte[] texels, int bpt)
    {
        var h = GCHandle.Alloc(texels, GCHandleType.Pinned);
        try
        {
            var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(h.AddrOfPinnedObject(), width * bpt));
            return (tex, new ShaderResourceView(device, tex));
        }
        finally { h.Free(); }
    }

    /// <summary>Light, sky and fog of the frame (linear colours when hdr).</summary>
    public struct Light
    {
        public Vector3 SunDirection, SunColor, SkyAmbient, Zenith, Horizon; public float SunDisc, FogDistance; public bool Hdr, Fog;
    }

    /// <summary>Draws the water into the bound image (its depth bound too). sceneDepth: multisampled copy of the window's
    /// depth; sceneColor: resolved copy of the image so far; viewport: x, y, width, height of the view in the image.</summary>
    public void Draw(Matrix4x4 viewProj, Vector3 eye, float near, float far, float time, Light l, ShaderResourceView sceneDepth, ShaderResourceView sceneColor, Vector4 viewport, bool waves = true)
    {
        if (!HasSurfaces) return;
        waves3D = waves && openWater && patterns != null;
        var center = new Vector2(MathF.Round(eye.X * 4f) / 4f, MathF.Round(eye.Y * 4f) / 4f);
        c = new Constants
        {
            ViewProjection = viewProj,
            CameraTime = new Vector4(eye, time),
            SunDirection = new Vector4(l.SunDirection, near),
            SunColor = new Vector4(l.SunColor, l.SunDisc),
            SkyAmbient = new Vector4(l.SkyAmbient, 0), Zenith = new Vector4(l.Zenith, 0), Horizon = new Vector4(l.Horizon, 0),
            Settings = new Vector4(l.Fog && l.FogDistance > 0 ? 1 : 0, MathF.Max(l.FogDistance, 1), sceneDepth == null ? 0 : 1, sceneColor == null ? 0 : 1),
            Viewport = viewport,
            Options = new Vector4(l.Hdr ? 1 : 0, waves3D ? 1 : 0, oceanHeight, 0),
            WaveArea = waveTex == null ? Vector4.Zero : waveArea, MaskArea = maskTex == null ? Vector4.Zero : maskArea,
            OceanBounds = oceanBounds, GridCenter = new Vector4(center, 0, 0),
            ColorArea = colorTex == null ? Vector4.Zero : colorArea, IslandArea = islandTex == null ? Vector4.Zero : islandArea,
            Caustics = new Vector4(causticTex == null ? 0 : 1, 0, near, far),
            Patterns = new Vector4(patterns == null ? 0 : 1, 0, 0, 0),
        };
        ctx.UpdateSubresource(ref c, cb);
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.SetConstantBuffer(0, cb); ctx.VertexShader.SetConstantBuffer(1, meshCb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.GeometryShader.Set(null);
        ctx.PixelShader.SetShaderResource(0, sceneDepth); ctx.PixelShader.SetShaderResource(1, sceneColor);
        if (waveTex is { } wt) { ctx.VertexShader.SetShaderResource(2, wt.V); ctx.PixelShader.SetShaderResource(2, wt.V); }
        if (maskTex is { } mt) ctx.PixelShader.SetShaderResource(3, mt.V);
        if (colorTex is { } ct) ctx.PixelShader.SetShaderResource(4, ct.V);
        if (islandTex is { } it) ctx.PixelShader.SetShaderResource(5, it.V);
        if (causticTex is { } kt) ctx.PixelShader.SetShaderResource(6, kt.V);
        if (patterns != null)
        {
            for (int i = 0; i < patterns.Length; i++) ctx.PixelShader.SetShaderResource(7 + i, patterns[i].V);
            ctx.VertexShader.SetShaderResource(7, patterns[0].V); ctx.VertexShader.SetShaderResource(8, patterns[1].V);
            ctx.VertexShader.SetSampler(1, linearWrap);
        }
        ctx.VertexShader.SetSampler(0, linearClamp);
        ctx.PixelShader.SetSampler(0, linearClamp); ctx.PixelShader.SetSampler(1, linearWrap);
        ctx.OutputMerger.SetDepthStencilState(depth, 0);
        ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        var prevRas = ctx.Rasterizer.State;
        ctx.Rasterizer.State = ras;
        var (buf, count) = waves3D ? inlandSurfaces : allSurfaces;
        if (buf != null)
        {
            ctx.InputAssembler.InputLayout = layout;
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(buf, Marshal.SizeOf<WaterVertex>(), 0));
            ctx.VertexShader.Set(vs);
            ctx.Draw(count, 0);
        }
        if (waves3D)
        {
            ctx.InputAssembler.InputLayout = gridLayout;
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(gridVb, Marshal.SizeOf<GridVertex>(), 0));
            ctx.InputAssembler.SetIndexBuffer(gridIb, Format.R32_UInt, 0);
            ctx.VertexShader.Set(gridVs);
            ctx.DrawIndexed(gridCount, 0, 0);
        }
        ctx.Rasterizer.State = prevRas; prevRas?.Dispose();
        for (int s = 0; s < 12; s++) ctx.PixelShader.SetShaderResource(s, null);
        ctx.VertexShader.SetShaderResource(2, null); ctx.VertexShader.SetShaderResource(7, null); ctx.VertexShader.SetShaderResource(8, null);
        ctx.OutputMerger.SetDepthStencilState(null, 0);
    }

    void DisposeSurfaces()
    {
        allSurfaces.Buf?.Dispose(); allSurfaces = default; inlandSurfaces.Buf?.Dispose(); inlandSurfaces = default;
        foreach (var t in new[] { waveTex, maskTex, colorTex, islandTex, causticTex }) if (t is { } x) { x.V.Dispose(); x.T.Dispose(); }
        waveTex = maskTex = colorTex = islandTex = causticTex = null;
        if (patterns != null) foreach (var (t, v) in patterns) { v.Dispose(); t.Dispose(); }
        patterns = null; openWater = false;
    }

    public void Dispose()
    {
        DisposeSurfaces();
        linearClamp.Dispose(); linearWrap.Dispose(); gridVb.Dispose(); gridIb.Dispose(); gridLayout.Dispose(); gridVs.Dispose();
        blend.Dispose(); depth.Dispose(); ras.Dispose(); cb.Dispose(); meshCb.Dispose(); layout.Dispose(); ps.Dispose(); vs.Dispose();
    }
}
