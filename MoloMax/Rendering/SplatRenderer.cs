using System.Numerics;
using System.Runtime.InteropServices;
using MoloMax.Core.Scenes;
using MoloMax.Core.Splats;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

[StructLayout(LayoutKind.Sequential)]
struct SplatVars
{
    public Matrix4x4 WorldView, Proj;
    public Vector4 Screen, Look, Look2, CropMin, CropMax;
}

/// <summary>
/// Draws Gaussian splat captures. The splats sit in a GPU buffer once; per viewport an index list sorted back to front
/// is rebuilt on a worker thread when the camera turns or moves (the last order is drawn meanwhile).
/// </summary>
public sealed class SplatRenderer : IDisposable
{
    readonly Device device;
    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly InputLayout layout;
    readonly Buffer cb;
    readonly BlendState blend;
    readonly DepthStencilState depthReadStd, depthReadRev; DepthStencilState depthRead => DepthMode.Reversed ? depthReadRev : depthReadStd;
    readonly RasterizerState noCull;

    sealed class View
    {
        public Buffer Index; public int Capacity;
        public Matrix4x4 SortedFor; public bool HasOrder;
        public Task<uint[]> Sorting; public Matrix4x4 SortingFor;
    }
    sealed class GpuCloud
    {
        public SplatCloud Cloud; public Buffer Data; public ShaderResourceView Srv;
        public readonly View[] Views = new View[8];
        public int LastFrame;
    }
    readonly Dictionary<int, GpuCloud> clouds = new();
    int frame;

    /// <summary>Splats sent to the graphics card last frame (all viewports).</summary>
    public long Drawn { get; private set; }
    long drawnNow;

    public SplatRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using var vsb = ShaderLoader.Compile("splat.hlsl", "VSMain", "vs_4_0");
        using var psb = ShaderLoader.Compile("splat.hlsl", "PSMain", "ps_4_0");
        vs = new VertexShader(device, vsb);
        ps = new PixelShader(device, psb);
        layout = new InputLayout(device, vsb, new[] { new InputElement("SPLAT", 0, Format.R32_UInt, 0, 0, InputClassification.PerInstanceData, 1) });
        cb = new Buffer(device, Marshal.SizeOf<SplatVars>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.One, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One, DestinationAlphaBlend = BlendOption.InverseSourceAlpha, AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        blend = new BlendState(device, bd);
        depthReadStd = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual });
        depthReadRev = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.GreaterEqual });
        noCull = new RasterizerState(device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = true, IsScissorEnabled = true });
    }

    public void BeginFrame() { Drawn = drawnNow; drawnNow = 0; frame++; }

    GpuCloud Get(SceneObject o)
    {
        var cloud = o.SplatCloud;
        if (clouds.TryGetValue(o.Id, out var g) && ReferenceEquals(g.Cloud, cloud)) return g;
        if (g != null) Free(g);
        g = new GpuCloud { Cloud = cloud };
        int n = cloud.Count;
        var data = new Vector4[n * 4];
        for (int i = 0; i < n; i++)
        {
            data[i * 4] = new Vector4(cloud.Pos[i * 3], cloud.Pos[i * 3 + 1], cloud.Pos[i * 3 + 2], cloud.Rgba[i * 4 + 3]);
            data[i * 4 + 1] = new Vector4(cloud.Rgba[i * 4], cloud.Rgba[i * 4 + 1], cloud.Rgba[i * 4 + 2], 0);
            data[i * 4 + 2] = new Vector4(cloud.Cov[i * 6], cloud.Cov[i * 6 + 1], cloud.Cov[i * 6 + 2], cloud.Cov[i * 6 + 3]);
            data[i * 4 + 3] = new Vector4(cloud.Cov[i * 6 + 4], cloud.Cov[i * 6 + 5], 0, 0);
        }
        g.Data = Buffer.Create(device, BindFlags.ShaderResource, data, 0, ResourceUsage.Immutable);
        g.Srv = new ShaderResourceView(device, g.Data, new ShaderResourceViewDescription
        {
            Format = Format.R32G32B32A32_Float, Dimension = ShaderResourceViewDimension.Buffer,
            Buffer = new ShaderResourceViewDescription.BufferResource { FirstElement = 0, ElementCount = n * 4 },
        });
        clouds[o.Id] = g;
        return g;
    }

    static void Free(GpuCloud g)
    {
        g.Srv?.Dispose(); g.Data?.Dispose();
        foreach (var v in g.Views) v?.Index?.Dispose();
    }

    /// <summary>Back-to-front order for the camera: 16-bit counting sort on the view depth (two passes, stable).</summary>
    static uint[] Sort(SplatCloud c, Matrix4x4 worldView)
    {
        int n = c.Count;
        var keys = new float[n];
        float m13 = worldView.M13, m23 = worldView.M23, m33 = worldView.M33, m43 = worldView.M43;
        float mn = float.MaxValue, mx = float.MinValue;
        var p = c.Pos;
        for (int i = 0; i < n; i++)
        {
            float z = p[i * 3] * m13 + p[i * 3 + 1] * m23 + p[i * 3 + 2] * m33 + m43; // view z: more negative = farther
            keys[i] = z; if (z < mn) mn = z; if (z > mx) mx = z;
        }
        float scale = mx > mn ? 65535f / (mx - mn) : 0;
        var counts = new int[65536];
        var bucket = new ushort[n];
        for (int i = 0; i < n; i++) { int b = (int)((keys[i] - mn) * scale); bucket[i] = (ushort)b; counts[b]++; }
        int sum = 0; for (int b = 0; b < 65536; b++) { int t = counts[b]; counts[b] = sum; sum += t; }
        var order = new uint[n];
        for (int i = 0; i < n; i++) order[counts[bucket[i]]++] = (uint)i; // farthest (smallest z) first
        return order;
    }

    static bool Moved(Matrix4x4 a, Matrix4x4 b)
    {
        // the view depth column decides the order: re-sort when it changed noticeably
        return MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M33 - b.M33) > 0.002f || MathF.Abs(a.M43 - b.M43) > 0.05f;
    }

    /// <summary>Draws one splat object in the current viewport (index = which viewport, each keeps its own order).</summary>
    public void Draw(SceneObject o, Matrix4x4 view, Matrix4x4 proj, int viewIndex, float width, float height, bool ortho, float near)
    {
        var cloud = o.SplatCloud;
        if (cloud == null || cloud.Count == 0 || width < 2 || height < 2) return;
        var g = Get(o); g.LastFrame = frame;
        var wv = o.World * view;
        var v = g.Views[viewIndex & 7] ??= new View();

        // order: first time sorted right away, then in the background
        if (v.Sorting != null && v.Sorting.IsCompleted)
        {
            if (!v.Sorting.IsFaulted) Upload(v, v.Sorting.Result, v.SortingFor);
            v.Sorting = null;
        }
        if (!v.HasOrder) Upload(v, Sort(cloud, wv), wv);
        else if (v.Sorting == null && Moved(v.SortedFor, wv))
        {
            var cap = wv; v.SortingFor = cap;
            v.Sorting = Task.Run(() => Sort(cloud, cap));
        }

        var look = o.Splat ?? new SplatLook();
        var vars = new SplatVars
        {
            WorldView = wv, Proj = proj,
            Screen = new Vector4(width, height, proj.M11 * width * 0.5f, proj.M22 * height * 0.5f),
            Look = new Vector4(Math.Clamp(look.Size, 0.05f, 10f), Math.Clamp(look.Opacity, 0f, 1f), Math.Clamp(look.Brightness, 0f, 5f), ortho ? 1 : 0),
            Look2 = new Vector4(Math.Clamp(look.MinOpacity, 0f, 1f), look.Crop ? 1 : 0, MathF.Max(near, 0.01f), 0),
            CropMin = new Vector4(look.CropMin, 0), CropMax = new Vector4(look.CropMax, 0),
        };
        ctx.UpdateSubresource(ref vars, cb);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(v.Index, 4, 0));
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb); ctx.VertexShader.SetShaderResource(0, g.Srv);
        ctx.GeometryShader.Set(null);
        ctx.PixelShader.Set(ps);
        ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.OutputMerger.SetDepthStencilState(depthRead);
        ctx.Rasterizer.State = noCull;
        ctx.DrawInstanced(4, cloud.Count, 0, 0);
        ctx.VertexShader.SetShaderResource(0, null);
        drawnNow += cloud.Count;
    }

    void Upload(View v, uint[] order, Matrix4x4 sortedFor)
    {
        if (v.Index == null || v.Capacity < order.Length)
        {
            v.Index?.Dispose();
            v.Capacity = order.Length;
            v.Index = new Buffer(device, Math.Max(4, order.Length * 4), ResourceUsage.Default, BindFlags.VertexBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        }
        ctx.UpdateSubresource(order, v.Index);
        v.SortedFor = sortedFor; v.HasOrder = true;
    }

    /// <summary>Frees captures not drawn for a while (deleted objects, closed scenes).</summary>
    public void Trim()
    {
        foreach (var id in clouds.Where(kv => frame - kv.Value.LastFrame > 600).Select(kv => kv.Key).ToList())
        { Free(clouds[id]); clouds.Remove(id); }
    }

    public void Dispose()
    {
        foreach (var g in clouds.Values) Free(g);
        clouds.Clear();
        vs.Dispose(); ps.Dispose(); layout.Dispose(); cb.Dispose(); blend.Dispose(); depthReadStd.Dispose(); depthReadRev.Dispose(); noCull.Dispose();
    }
}
