using System.Numerics;
using System.Runtime.InteropServices;
using MoloMax.Core.Mesh;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>
/// Clean selection outline in object mode: only the outer contour of the selection, never its inner edges.
/// Pass 1 stamps every selected object into the stencil, pass 2 draws them inflated in screen space where the stencil is empty.
/// </summary>
public sealed class OutlineRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vars { public Matrix4x4 WVP; public Vector4 Colour; public Vector4 Params; }
    [StructLayout(LayoutKind.Sequential)]
    struct Vtx { public Vector3 P, N; }

    sealed class Mesh : IDisposable { public int Version; public Buffer Vb, Ib; public int Count; public void Dispose() { Vb?.Dispose(); Ib?.Dispose(); } }

    readonly Device device;
    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly InputLayout layout;
    readonly Buffer cb;
    readonly DepthStencilState stamp, ringStd, ringRev;
    readonly BlendState noColour, alpha;
    readonly RasterizerState noCull;
    readonly Dictionary<int, Mesh> meshes = new();

    public OutlineRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var vsb = ShaderLoader.Compile("outline.hlsl", "VSMain", "vs_4_0"))
        {
            vs = new VertexShader(device, vsb);
            layout = new InputLayout(device, vsb, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            });
        }
        using (var psb = ShaderLoader.Compile("outline.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, psb);
        cb = new Buffer(device, (Marshal.SizeOf<Vars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        DepthStencilOperationDescription Op(Comparison f, StencilOperation pass) => new()
        { Comparison = f, PassOperation = pass, FailOperation = StencilOperation.Keep, DepthFailOperation = StencilOperation.Keep };
        stamp = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Always,
            IsStencilEnabled = true, StencilReadMask = 0xFF, StencilWriteMask = 0xFF,
            FrontFace = Op(Comparison.Always, StencilOperation.Replace), BackFace = Op(Comparison.Always, StencilOperation.Replace),
        });
        ringStd = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = true, StencilReadMask = 0xFF, StencilWriteMask = 0x00,
            FrontFace = Op(Comparison.NotEqual, StencilOperation.Keep), BackFace = Op(Comparison.NotEqual, StencilOperation.Keep),
        });
        ringRev = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.GreaterEqual,
            IsStencilEnabled = true, StencilReadMask = 0xFF, StencilWriteMask = 0x00,
            FrontFace = Op(Comparison.NotEqual, StencilOperation.Keep), BackFace = Op(Comparison.NotEqual, StencilOperation.Keep),
        });
        var nb = BlendStateDescription.Default();
        nb.RenderTarget[0].RenderTargetWriteMask = (ColorWriteMaskFlags)0;
        noColour = new BlendState(device, nb);
        var ab = BlendStateDescription.Default();
        ab.RenderTarget[0].IsBlendEnabled = true;
        ab.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha; ab.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        ab.RenderTarget[0].SourceAlphaBlend = BlendOption.One; ab.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        alpha = new BlendState(device, ab);
        noCull = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, IsMultisampleEnabled = true, IsScissorEnabled = true,
        });
    }

    /// <summary>Triangles with smooth normals per shared vertex, so the inflated copy has no cracks at hard corners.</summary>
    Mesh Get(int id, int version, Func<PolyMesh> source)
    {
        if (meshes.TryGetValue(id, out var m) && m.Version == version) return m;
        m?.Dispose();
        var pm = source();
        var nrm = new Vector3[pm.Verts.Count];
        var idx = new List<int>();
        for (int fi = 0; fi < pm.Faces.Count; fi++)
        {
            var f = pm.Faces[fi];
            if (f.V.Length < 3) continue;
            var fn = pm.FaceNormal(fi);
            foreach (var v in f.V) if (v >= 0 && v < nrm.Length) nrm[v] += fn;
            foreach (var (a, b, c) in Triangulator.Triangulate(pm, f)) { idx.Add(f.V[a]); idx.Add(f.V[b]); idx.Add(f.V[c]); }
        }
        m = new Mesh { Version = version, Count = idx.Count };
        if (idx.Count > 0)
        {
            var verts = new Vtx[pm.Verts.Count];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = new Vtx { P = pm.Verts[i], N = nrm[i].LengthSquared() > 1e-12f ? Vector3.Normalize(nrm[i]) : Vector3.Zero };
            m.Vb = Buffer.Create(device, BindFlags.VertexBuffer, verts);
            m.Ib = Buffer.Create(device, BindFlags.IndexBuffer, idx.ToArray());
        }
        meshes[id] = m;
        return m;
    }

    public readonly record struct Item(int Id, int Version, Matrix4x4 World, Func<PolyMesh> Source, Vector4 Colour);

    /// <summary>Draws the outline of the given objects into the bound target. The depth-stencil view must be bound; its stencil is cleared here.</summary>
    public void Draw(DepthStencilView dsv, IReadOnlyList<Item> items, Matrix4x4 viewProj, float vpW, float vpH, float widthPx)
    {
        if (items.Count == 0) return;
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Stencil, 1f, 0);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.GeometryShader.Set(null);
        ctx.Rasterizer.State = noCull;
        var blendCol = new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0);
        var list = items.Select(it => (it, Get(it.Id, it.Version, it.Source))).Where(x => x.Item2.Count > 0).ToList();
        // forget only the meshes that are no longer outlined: with more than a few dozen selected objects the old
        // "keep 48" rule threw away and rebuilt every outline mesh each frame (a big selection fell to a few fps)
        if (meshes.Count > items.Count + 32)
        {
            var used = items.Select(it => it.Id).ToHashSet();
            foreach (var k in meshes.Keys.Where(k => !used.Contains(k)).ToList()) { meshes[k].Dispose(); meshes.Remove(k); }
        }
        // pass 1: stamp
        ctx.OutputMerger.SetDepthStencilState(stamp, 1);
        ctx.OutputMerger.SetBlendState(noColour, blendCol, -1);
        foreach (var (it, m) in list) Submit(it, m, viewProj, vpW, vpH, 0);
        // pass 2: ring outside the stamp
        ctx.OutputMerger.SetDepthStencilState(DepthMode.Reversed ? ringRev : ringStd, 1);
        ctx.OutputMerger.SetBlendState(alpha, blendCol, -1);
        foreach (var (it, m) in list) Submit(it, m, viewProj, vpW, vpH, widthPx);
    }

    void Submit(Item it, Mesh m, Matrix4x4 viewProj, float w, float h, float px)
    {
        var v = new Vars { WVP = it.World * viewProj, Colour = it.Colour, Params = new Vector4(px, w, h, 0) };
        ctx.UpdateSubresource(ref v, cb);
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(m.Vb, Marshal.SizeOf<Vtx>(), 0));
        ctx.InputAssembler.SetIndexBuffer(m.Ib, Format.R32_UInt, 0);
        ctx.DrawIndexed(m.Count, 0, 0);
    }

    public void Dispose()
    {
        foreach (var m in meshes.Values) m.Dispose();
        meshes.Clear();
        vs.Dispose(); ps.Dispose(); layout.Dispose(); cb.Dispose();
        stamp.Dispose(); ringStd.Dispose(); ringRev.Dispose(); noColour.Dispose(); alpha.Dispose(); noCull.Dispose();
    }
}
