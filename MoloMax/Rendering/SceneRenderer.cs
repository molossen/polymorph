using System.Numerics;
using System.Runtime.InteropServices;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

public enum ShadingMode : uint { Realistic = 0, Shaded = 1, VertexColour = 2, Normals = 3, UVChecker = 4, Unlit = 5 }

[StructLayout(LayoutKind.Sequential)]
struct SceneVarsCB
{
    public Matrix4x4 ViewProj;
    public Vector4 CameraPos;
    public Vector3 LightDir; public float LightHdr;
    public Vector4 LightDirColour, LightDirAmbColour, NaturalAmbUp, NaturalAmbDown, ArtificialAmbUp, ArtificialAmbDown;
    public uint RenderMode; public float Exposure; public float AmbientBoost; public float Pad;
}

[StructLayout(LayoutKind.Sequential)]
struct ObjectVarsCB
{
    public Matrix4x4 World;
    public Matrix4x4 NormalMatrix;
    public Vector4 WireColour;
    public Vector4 Highlight;
    /// <summary>UV animation rows (GTA globalAnimUV0 / 1); w = 1 when animated, 0 leaves the UVs as they are.</summary>
    public Vector4 UvRow0, UvRow1;
}

[StructLayout(LayoutKind.Sequential)]
struct MaterialVarsCB
{
    public uint EnableTexture, EnableNormalMap, EnableSpecMap, EnableDetailMap;
    public uint IsDecal, IsEmissive, IsGlass, AlphaTest;
    public float Bumpiness, SpecularIntensityMult, SpecularFalloffMult, SpecularFresnel;
    public Vector3 SpecMapIntMask; public float EmissiveMult;
    public Vector4 DetailSettings;
    public Vector4 ViewportColour;
    public uint IsTerrain, IsTint, TerrainMode, VertexColoured;
}

/// <summary>One scene light as the shader sees it.</summary>
public struct ViewLight
{
    public Vector3 Position, Direction, Colour; // colour already times intensity
    public float Range, HalfLength, CosInner, CosOuter, FalloffExponent, Radius, Diffuse, Specular;
    public int Kind; // 0 point, 1 spot, 2 capsule
}

[Flags] public enum RenderFlags : uint { None = 0, Filmic = 1, SunShadows = 2, SceneLights = 4, FaceOrientation = 8 }

/// <summary>Uploaded triangles of one SceneObject, split by material.</summary>
sealed class GpuMesh : IDisposable
{
    public int Version = -1;
    public bool Edit;
    public readonly List<(Buffer Vb, Buffer Ib, int Count, int MatId)> Groups = new();
    /// <summary>Edited object only: the vertices as sent, and which mesh point each one is, so a drag rewrites positions only.</summary>
    public List<(RenderVertex[] Cpu, int[] CornerVert, int[] CornerFace)> Cpu;
    /// <summary>Polygon → its render vertices, per group (built when UVs first change during a drag).</summary>
    public List<(int[] Start, int[] Idx)> FaceCorners;
    /// <summary>Mesh point → its render vertices, per group (built at the first drag).</summary>
    public List<(int[] Start, int[] Idx)> VertCorners;
    public void Dispose() { foreach (var g in Groups) { g.Vb.Dispose(); g.Ib.Dispose(); } Groups.Clear(); Cpu = null; VertCorners = null; FaceCorners = null; }
}

/// <summary>Draws scene objects with the GTA basic shader.</summary>
public sealed class SceneRenderer : IDisposable
{
    readonly Device device;
    readonly DeviceContext ctx;
    readonly VertexShader vs, vsInst;
    readonly PixelShader ps, psPick;
    readonly Buffer pickCB;

    // ---- GPU picking: objects drawn with their number (and depth) into two small-format targets, read back under one pixel
    Texture2D pickTex, pickZTex, pickDepth, pickStage, pickZStage;
    RenderTargetView pickRtv, pickZRtv; DepthStencilView pickDsv;
    int pickW, pickH, pickX, pickY;
    /// <summary>A pick pass is drawing: DrawObject / DrawInstanced write numbers, the blended pass is skipped.</summary>
    public bool Picking { get; private set; }

    /// <summary>Starts a pick pass over a view of the given size; only the pixel (x, y) of the view is drawn (scissor).
    /// Call BeginScene after it (with the view's matrix), then draw with <see cref="SetPickBase"/>.</summary>
    public void BeginPick(int viewW, int viewH, int x, int y)
    {
        viewW = Math.Max(1, viewW); viewH = Math.Max(1, viewH);
        if (pickTex == null || pickW != viewW || pickH != viewH)
        {
            pickRtv?.Dispose(); pickTex?.Dispose(); pickZRtv?.Dispose(); pickZTex?.Dispose(); pickDsv?.Dispose(); pickDepth?.Dispose();
            pickW = viewW; pickH = viewH;
            Texture2D T(Format f, BindFlags b) => new(device, new Texture2DDescription { Width = pickW, Height = pickH, MipLevels = 1, ArraySize = 1, Format = f, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = b });
            pickTex = T(Format.R32_UInt, BindFlags.RenderTarget); pickRtv = new RenderTargetView(device, pickTex);
            pickZTex = T(Format.R32_Float, BindFlags.RenderTarget); pickZRtv = new RenderTargetView(device, pickZTex);
            pickDepth = T(Format.D32_Float, BindFlags.DepthStencil); pickDsv = new DepthStencilView(device, pickDepth);
        }
        Texture2D S(Format f) => new(device, new Texture2DDescription { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = f, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read });
        pickStage ??= S(Format.R32_UInt); pickZStage ??= S(Format.R32_Float);
        pickX = Math.Clamp(x, 0, pickW - 1); pickY = Math.Clamp(y, 0, pickH - 1);
        ctx.OutputMerger.SetRenderTargets(pickDsv, pickRtv, pickZRtv);
        ctx.ClearRenderTargetView(pickRtv, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0));
        ctx.ClearRenderTargetView(pickZRtv, new SharpDX.Mathematics.Interop.RawColor4(1, 1, 1, 1));
        ctx.ClearDepthStencilView(pickDsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.Rasterizer.SetViewport(0, 0, pickW, pickH);
        ctx.Rasterizer.SetScissorRectangle(pickX, pickY, pickX + 1, pickY + 1);
        Picking = true;
    }

    /// <summary>Pick pass: the next draws are numbered from this base (+ instance + 1).</summary>
    public void SetPickBase(uint b)
    {
        var d = new[] { b, 0u, 0u, 0u };
        ctx.UpdateSubresource(d, pickCB);
    }

    /// <summary>After BeginScene in a pick pass: the number shader instead of the colour one.</summary>
    void PickShaders()
    {
        ctx.PixelShader.Set(psPick);
        ctx.PixelShader.SetConstantBuffer(7, pickCB);
        ctx.OutputMerger.SetDepthStencilState(DepthDefault);
        ctx.OutputMerger.SetBlendState(Opaque, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
    }

    /// <summary>Ends the pick pass: the number under the pixel (0 = nothing) and its depth (0..1, 1 = nothing).
    /// Leaves no render target bound.</summary>
    public (uint Id, float Depth) EndPick()
    {
        Picking = false;
        ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
        var box = new ResourceRegion(pickX, pickY, 0, pickX + 1, pickY + 1, 1);
        ctx.CopySubresourceRegion(pickTex, 0, box, pickStage, 0);
        ctx.CopySubresourceRegion(pickZTex, 0, box, pickZStage, 0);
        uint id; float depth;
        var m = ctx.MapSubresource(pickStage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        unsafe { id = *(uint*)m.DataPointer; }
        ctx.UnmapSubresource(pickStage, 0);
        m = ctx.MapSubresource(pickZStage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        unsafe { depth = *(float*)m.DataPointer; }
        ctx.UnmapSubresource(pickZStage, 0);
        ctx.PixelShader.Set(ps);
        return (id, depth);
    }
    readonly InputLayout layout, layoutInst;
    Buffer instBuf; int instCap, instOff;
    VertexBufferBinding[] instBindings = new VertexBufferBinding[2];
    readonly Buffer sceneCB, objectCB, materialCB, lightCB, clipCB, fogCB;
    readonly Vector4[] fogData = new Vector4[4];
    /// <summary>MAP: distance haze (strength 0: off). Colours in display space.</summary>
    public void SetFog(Vector3 colour, float strength, float start, float full, Vector3 sunAdd, Vector3 sunDir)
    {
        fogData[0] = new(colour, strength); fogData[1] = new(start, full, 0.004f, 0); fogData[2] = new(sunAdd, 0); fogData[3] = new(sunDir, 0);
        ctx.UpdateSubresource(fogData, fogCB);
    }
    public void FogOff() { if (fogData[0].W == 0) return; fogData[0].W = 0; ctx.UpdateSubresource(fogData, fogCB); }
    readonly Vector4[] clipData = new Vector4[ClipMax * 2 + 1];
    public const int ClipMax = 16;
    bool clipOn;
    /// <summary>MAP: boxes where far models are not drawn (over the models the user hid). Empty = off.</summary>
    public void SetClip(IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes)
    {
        int n = boxes == null ? 0 : Math.Min(boxes.Count, ClipMax);
        if (n == 0 && !clipOn) return;
        for (int i = 0; i < n; i++) { clipData[i] = new Vector4(boxes[i].Min, 0); clipData[ClipMax + i] = new Vector4(boxes[i].Max, 0); }
        clipData[ClipMax * 2] = new Vector4(n, 0, 0, 0);
        ctx.UpdateSubresource(clipData, clipCB);
        clipOn = n > 0;
    }
    public const int MaxLights = 32, ShadowSize = 2048;
    const int LightVec4s = 7 + MaxLights * 5;
    public Vector3 FaceFront = new(0.19f, 0.36f, 1f), FaceBack = new(1f, 0.12f, 0.12f);
    readonly Vector4[] lightData = new Vector4[LightVec4s];
    Texture2D shadowTex;
    DepthStencilView shadowDsv;
    ShaderResourceView shadowSrv;
    readonly SamplerState shadowSampler;
    readonly RasterizerState shadowRas;
    readonly SamplerState sampler;
    public readonly TextureCache Textures;
    readonly Dictionary<int, GpuMesh> meshes = new();

    public readonly RasterizerState CullBack, CullNone, LinesState, CullFront;
    public readonly DepthStencilState DepthDefault, DepthRead, DepthOff;
    public readonly BlendState Opaque, Alpha;

    public SceneRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var vsb = ShaderLoader.Compile("gta_basic.hlsl", "VSMain", "vs_4_0"))
        {
            vs = new VertexShader(device, vsb);
            layout = new InputLayout(device, vsb, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32A32_Float, 48, 0),
                new InputElement("COLOR", 1, Format.R8G8B8A8_UNorm, 64, 0),
                new InputElement("TEXCOORD", 1, Format.R32G32_Float, 68, 0),
            });
        }
        using (var vsi = ShaderLoader.Compile("gta_basic.hlsl", "VSInstanced", "vs_4_0"))
        {
            vsInst = new VertexShader(device, vsi);
            layoutInst = new InputLayout(device, vsi, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32A32_Float, 48, 0),
                new InputElement("COLOR", 1, Format.R8G8B8A8_UNorm, 64, 0),
                new InputElement("TEXCOORD", 1, Format.R32G32_Float, 68, 0),
                new InputElement("TEXCOORD", 4, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 5, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 6, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 7, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1),
            });
        }
        using (var psb = ShaderLoader.Compile("gta_basic.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, psb);
        using (var psp = ShaderLoader.Compile("gta_basic.hlsl", "PSPick", "ps_4_0")) psPick = new PixelShader(device, psp);
        pickCB = new Buffer(device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        sceneCB = CB<SceneVarsCB>(); objectCB = CB<ObjectVarsCB>(); materialCB = CB<MaterialVarsCB>();
        lightCB = new Buffer(device, LightVec4s * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        ctx.UpdateSubresource(lightData, lightCB);
        clipCB = new Buffer(device, clipData.Length * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        fogCB = new Buffer(device, fogData.Length * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        ctx.UpdateSubresource(fogData, fogCB);
        ctx.UpdateSubresource(clipData, clipCB);
        shadowSampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint, AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border, AddressW = TextureAddressMode.Border,
            BorderColor = new SharpDX.Mathematics.Interop.RawColor4(1, 1, 1, 1), ComparisonFunction = Comparison.LessEqual, MaximumLod = float.MaxValue,
        });
        shadowRas = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, DepthBias = 0, SlopeScaledDepthBias = 2.5f, DepthBiasClamp = 0.01f,
        });
        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.Anisotropic, MaximumAnisotropy = 8,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Always, MinimumLod = 0, MaximumLod = float.MaxValue,
        });
        Textures = new TextureCache(device);

        RasterizerState Ras(CullMode c, bool lines = false) => new(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = c, IsFrontCounterClockwise = true, IsDepthClipEnabled = true,
            IsMultisampleEnabled = true, IsAntialiasedLineEnabled = lines, IsScissorEnabled = true,
        });
        CullBack = Ras(CullMode.Back); CullNone = Ras(CullMode.None); LinesState = Ras(CullMode.None, true); CullFront = Ras(CullMode.Front);
        DepthDefault = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.LessEqual });
        DepthRead = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual });
        DepthOff = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Always });
        Opaque = new BlendState(device, BlendStateDescription.Default());
        var bd = BlendStateDescription.Default();
        bd.RenderTarget[0].IsBlendEnabled = true;
        bd.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha; bd.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        bd.RenderTarget[0].SourceAlphaBlend = BlendOption.One; bd.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        Alpha = new BlendState(device, bd);
    }

    Buffer CB<T>() where T : struct =>
        new(device, (Marshal.SizeOf<T>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

    /// <summary>Object in edit mode: its modifiers show only when "Edit Mode" is on in their header (Blender).</summary>
    public int EditObjectId = -1;
    /// <summary>Draw both sides of every polygon (face orientation view).</summary>
    public bool NoCull;
    /// <summary>Meshes sent to the GPU since start (profiling: should stay still when nothing changes).</summary>
    public int Uploads;
    /// <summary>Draw calls and triangles sent since the last reset (profiling).</summary>
    public int StatDraws; public long StatTris;

    // instances share one GPU mesh (thousands of rocks / chairs cost one upload)
    readonly Dictionary<string, GpuMesh> instMeshes = new();
    /// <summary>Triangles already built in the background for a mesh (map models): used once, at its first upload.</summary>
    public readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, List<RenderGroup>> Prebuilt = new();
    /// <summary>Milliseconds spent sending meshes to the GPU (profiling).</summary>
    public double UploadMs;
    readonly System.Diagnostics.Stopwatch upSw = new();

    GpuMesh Upload(SceneObject o)
    {
        bool edit = o.Id == EditObjectId;
        GpuMesh gm;
        if (o.InstanceKey != null && !edit) { if (!instMeshes.TryGetValue(o.InstanceKey, out gm)) instMeshes[o.InstanceKey] = gm = new GpuMesh(); }
        else if (!meshes.TryGetValue(o.Id, out gm)) meshes[o.Id] = gm = new GpuMesh();
        if (gm.Version == o.Version && gm.Edit == edit && gm.Groups.Count > 0) return gm;
        upSw.Restart();
        if (edit && TryMovePoints(o, gm)) { UploadMs += upSw.Elapsed.TotalMilliseconds; return gm; }
        Uploads++;
        gm.Dispose();
        gm.Version = o.Version; gm.Edit = edit;
        var src = o.Evaluated(edit);
        List<RenderGroup> groups;
        if (!edit && Prebuilt.TryGetValue(src, out var pre)) { groups = pre; Prebuilt.Remove(src); }
        else groups = RenderMeshBuilder.Build(src);
        bool keepCpu = edit && ReferenceEquals(src, o.Mesh) && groups.All(g => g.CornerVert != null && g.CornerFace != null && g.VertexArray != null);
        if (keepCpu) gm.Cpu = new();
        foreach (var g in groups)
        {
            if (g.IndexCount == 0) continue;
            var verts = g.VertexArray ?? g.Vertices.ToArray(); var idx = g.IndexArray ?? g.Indices.ToArray();
            var vb = Buffer.Create(device, BindFlags.VertexBuffer, verts);
            var ib = Buffer.Create(device, BindFlags.IndexBuffer, idx);
            gm.Groups.Add((vb, ib, idx.Length, g.MaterialId));
            if (keepCpu) gm.Cpu.Add((verts, g.CornerVert, g.CornerFace));
        }
        UploadMs += upSw.Elapsed.TotalMilliseconds;
        return gm;
    }

    // ---- points moved without changing the faces (gizmo drags in edit mode): only their positions are rewritten
    int hintObj = -1, hintFrom, hintTo; int[] hintVerts, hintUvFaces;

    /// <summary>The editor moved these points of the object (version <paramref name="from"/> → <paramref name="to"/>) and changed
    /// nothing else, except the UVs of <paramref name="uvFaces"/> when given.</summary>
    public void HintPointsMoved(int objId, int from, int to, int[] verts, int[] uvFaces = null) { hintObj = objId; hintFrom = from; hintTo = to; hintVerts = verts; hintUvFaces = uvFaces; }

    bool[] markBuf = Array.Empty<bool>();
    bool[] Marks(int n) { if (markBuf.Length < n) markBuf = new bool[n]; return markBuf; }

    /// <summary>Marked ranges between lo and hi (marks cleared), merged across small gaps; too many: one span.</summary>
    internal static List<(int A, int B)> Runs(bool[] mark, int lo, int hi)
    {
        var runs = new List<(int A, int B)>(); int ra = -1, rb = -1;
        for (int c = lo; c <= hi; c++)
        {
            if (!mark[c]) continue;
            mark[c] = false;
            if (ra < 0) { ra = rb = c; continue; }
            if (c - rb <= 64) rb = c; else { runs.Add((ra, rb)); ra = rb = c; }
        }
        if (ra >= 0) runs.Add((ra, rb));
        if (runs.Count > 256) runs = new() { (lo, hi) };
        return runs;
    }

    /// <summary>Self test: render vertices of the edited object whose position is not their mesh point's (-1: no CPU copy).</summary>
    public int DebugMismatch(SceneObject o)
    {
        if (!meshes.TryGetValue(o.Id, out var gm) || gm.Cpu == null) return -1;
        int bad = 0; var v = o.Mesh.Verts;
        foreach (var (cpu, cv, _) in gm.Cpu)
            for (int c = 0; c < cpu.Length; c++) if (cv[c] >= v.Count || Vector3.DistanceSquared(cpu[c].Position, v[cv[c]]) > 1e-8f) bad++;
        return bad;
    }
    public int DebugVersion(SceneObject o) => meshes.TryGetValue(o.Id, out var gm) ? gm.Version : -2;

    bool TryMovePoints(SceneObject o, GpuMesh gm)
    {
        if (hintObj != o.Id || hintTo != o.Version || gm.Version != hintFrom || !gm.Edit || gm.Cpu == null || gm.Cpu.Count != gm.Groups.Count || hintVerts == null) return false;
        if (o.Modifiers is { Count: > 0 }) return false;
        var verts = o.Mesh.Verts; int nv = verts.Count;
        if (gm.VertCorners == null)
        {
            // point → render vertices (CSR), once per edit session
            gm.VertCorners = new();
            foreach (var (_, cv, _) in gm.Cpu)
            {
                var start = new int[nv + 1];
                foreach (var v in cv) if (v < nv) start[v + 1]++;
                for (int i = 0; i < nv; i++) start[i + 1] += start[i];
                var fill = (int[])start.Clone(); var idx = new int[start[nv]];
                for (int c = 0; c < cv.Length; c++) { int v = cv[c]; if (v < nv) idx[fill[v]++] = c; }
                gm.VertCorners.Add((start, idx));
            }
        }
        for (int gi = 0; gi < gm.Groups.Count; gi++)
        {
            var (cpu, _, cf) = gm.Cpu[gi]; var (start, idx) = gm.VertCorners[gi];
            if (start.Length != nv + 1) return false;
            int lo = int.MaxValue, hi = -1;
            var mark = Marks(cpu.Length);
            foreach (var v in hintVerts)
            {
                if (v < 0 || v >= nv) continue;
                var p = verts[v];
                for (int k = start[v]; k < start[v + 1]; k++) { int c = idx[k]; cpu[c].Position = p; mark[c] = true; if (c < lo) lo = c; if (c > hi) hi = c; }
            }
            if (hintUvFaces != null && hintUvFaces.Length > 0)
            {
                // UVs of a few polygons changed too (extrude sides): their corners, in order
                var faces = o.Mesh.Faces; int nf = faces.Count;
                gm.FaceCorners ??= new();
                while (gm.FaceCorners.Count <= gi) gm.FaceCorners.Add(default);
                var (fs, fi2) = gm.FaceCorners[gi];
                if (fs == null || fs.Length != nf + 1)
                {
                    fs = new int[nf + 1];
                    foreach (var f in cf) if (f < nf) fs[f + 1]++;
                    for (int i = 0; i < nf; i++) fs[i + 1] += fs[i];
                    var fill = (int[])fs.Clone(); fi2 = new int[fs[nf]];
                    for (int c = 0; c < cf.Length; c++) { int f = cf[c]; if (f < nf) fi2[fill[f]++] = c; }
                    gm.FaceCorners[gi] = (fs, fi2);
                }
                foreach (var f in hintUvFaces)
                {
                    if (f < 0 || f >= nf) continue;
                    var uv = faces[f].UV;
                    for (int k = fs[f], j = 0; k < fs[f + 1] && j < uv.Length; k++, j++) { int c = fi2[k]; cpu[c].UV = uv[j]; mark[c] = true; if (c < lo) lo = c; if (c > hi) hi = c; }
                }
            }
            if (hi < 0) continue;
            var runs = Runs(mark, lo, hi);
            var vb = gm.Groups[gi].Vb; int sz = RenderVertex.SizeInBytes;
            unsafe
            {
                fixed (RenderVertex* basePtr = cpu)
                    foreach (var (a, b) in runs)
                    {
                        var box = new ResourceRegion(a * sz, 0, 0, (b + 1) * sz, 1, 1);
                        ctx.UpdateSubresource(new SharpDX.DataBox((IntPtr)(basePtr + a), 0, 0), vb, 0, box);
                    }
            }
        }
        gm.Version = o.Version;
        return true;
    }

    /// <summary>Frees GPU meshes of objects that left the scene.</summary>
    /// <summary>Instance keys kept on the GPU even when no scene object uses them (the map mode's far world).</summary>
    public HashSet<string> KeepInstanceKeys;

    public void Collect(Scene scene)
    {
        var alive = new HashSet<int>(scene.Objects.Where(o => o.InstanceKey == null || o.Id == EditObjectId).Select(o => o.Id));
        // negative ids: preview objects (game files viewer), freed by their owner
        foreach (var id in meshes.Keys.Where(k => k >= 0 && !alive.Contains(k)).ToList()) { meshes[id].Dispose(); meshes.Remove(id); }
        if (instMeshes.Count > 0)
        {
            var keys = new HashSet<string>(scene.Objects.Where(o => o.InstanceKey != null).Select(o => o.InstanceKey));
            if (KeepInstanceKeys != null) keys.UnionWith(KeepInstanceKeys);
            foreach (var k in instMeshes.Keys.Where(k => !keys.Contains(k)).ToList()) { instMeshes[k].Dispose(); instMeshes.Remove(k); }
        }
    }

    /// <summary>Frees the GPU meshes of preview objects (negative ids).</summary>
    public void FreePreview(IEnumerable<int> ids) { foreach (var id in ids) if (meshes.Remove(id, out var m)) m.Dispose(); }

    /// <summary>Forces every mesh to re-upload (after an undo replaced the scene).</summary>
    public void Invalidate() { foreach (var m in meshes.Values) m.Dispose(); meshes.Clear(); foreach (var m in instMeshes.Values) m.Dispose(); instMeshes.Clear(); }

    public void BeginScene(Matrix4x4 viewProj, Vector3 eye, in GtaGlobalLights l, ShadingMode mode, float exposure, float ambientBoost)
    {
        lastMat = null;
        var s = new SceneVarsCB
        {
            ViewProj = viewProj, CameraPos = new Vector4(eye, 1),
            LightDir = l.LightDir, LightHdr = l.LightHdr,
            LightDirColour = l.LightDirColour, LightDirAmbColour = l.LightDirAmbColour,
            NaturalAmbUp = l.NaturalAmbUp, NaturalAmbDown = l.NaturalAmbDown,
            ArtificialAmbUp = l.ArtificialAmbUp, ArtificialAmbDown = l.ArtificialAmbDown,
            RenderMode = (uint)mode, Exposure = exposure, AmbientBoost = ambientBoost,
        };
        ctx.UpdateSubresource(ref s, sceneCB);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs);
        ctx.VertexShader.SetConstantBuffer(0, sceneCB); ctx.VertexShader.SetConstantBuffer(1, objectCB); ctx.VertexShader.SetConstantBuffer(2, materialCB);
        ctx.PixelShader.Set(ps);
        ctx.PixelShader.SetConstantBuffer(0, sceneCB); ctx.PixelShader.SetConstantBuffer(1, objectCB); ctx.PixelShader.SetConstantBuffer(2, materialCB);
        ctx.PixelShader.SetSampler(0, sampler);
        ctx.PixelShader.SetConstantBuffer(3, lightCB);
        ctx.PixelShader.SetConstantBuffer(4, clipCB);
        ctx.PixelShader.SetConstantBuffer(6, fogCB);
        ctx.PixelShader.SetSampler(1, shadowSampler);
        ctx.PixelShader.SetShaderResource(5, shadowSrv);
        ctx.GeometryShader.Set(null);
        if (Picking) PickShaders();
    }

    static float Bits(uint u) => BitConverter.UInt32BitsToSingle(u);

    /// <summary>Scene lights, flags and the sun shadow matrix for the next draws (realistic mode).</summary>
    public void SetLights(IReadOnlyList<ViewLight> lights, RenderFlags flags, Matrix4x4 shadowViewProj, float shadowTexelWorld, float shadowStrength)
    {
        int n = Math.Min(lights?.Count ?? 0, MaxLights);
        if (shadowSrv == null) flags &= ~RenderFlags.SunShadows;
        lightData[0] = new(shadowViewProj.M11, shadowViewProj.M12, shadowViewProj.M13, shadowViewProj.M14);
        lightData[1] = new(shadowViewProj.M21, shadowViewProj.M22, shadowViewProj.M23, shadowViewProj.M24);
        lightData[2] = new(shadowViewProj.M31, shadowViewProj.M32, shadowViewProj.M33, shadowViewProj.M34);
        lightData[3] = new(shadowViewProj.M41, shadowViewProj.M42, shadowViewProj.M43, shadowViewProj.M44);
        lightData[4] = new(Bits((uint)n), Bits((uint)flags), shadowTexelWorld, shadowStrength);
        for (int i = 0; i < MaxLights; i++)
        {
            var l = i < n ? lights[i] : default;
            lightData[5 + i] = new(l.Position, l.Range);
            lightData[5 + MaxLights + i] = new(l.Colour, l.Kind);
            lightData[5 + MaxLights * 2 + i] = new(l.Direction, l.HalfLength);
            lightData[5 + MaxLights * 3 + i] = new(l.CosInner, l.CosOuter, l.FalloffExponent, l.Radius);
            lightData[5 + MaxLights * 4 + i] = new(l.Diffuse, l.Specular, 0, 0);
        }
        lightData[5 + MaxLights * 5] = new(FaceFront, 1);
        lightData[6 + MaxLights * 5] = new(FaceBack, 1);
        ctx.UpdateSubresource(lightData, lightCB);
    }

    void EnsureShadowMap()
    {
        if (shadowTex != null) return;
        shadowTex = new Texture2D(device, new Texture2DDescription
        {
            Width = ShadowSize, Height = ShadowSize, MipLevels = 1, ArraySize = 1, Format = Format.R32_Typeless, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        shadowDsv = new DepthStencilView(device, shadowTex, new DepthStencilViewDescription { Format = Format.D32_Float, Dimension = DepthStencilViewDimension.Texture2D });
        shadowSrv = new ShaderResourceView(device, shadowTex, new ShaderResourceViewDescription { Format = Format.R32_Float, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 } });
    }

    /// <summary>
    /// Renders the depth of the opaque objects seen from the sun into the shadow map.
    /// Leaves no render target bound: the caller binds its own target afterwards.
    /// </summary>
    /// <summary>Map mode: milliseconds of mesh sending allowed per frame for instanced draws (0: no limit).</summary>
    public double UploadBudgetMs;
    double frameUpload0;
    /// <summary>Call once per frame before drawing.</summary>
    public void BeginFrameUploads() => frameUpload0 = UploadMs;

    /// <summary>True when the object's GPU mesh is not up to date (drawing it now would send it first).</summary>
    public bool NeedsUpload(SceneObject o)
    {
        bool edit = o.Id == EditObjectId;
        GpuMesh gm;
        if (o.InstanceKey != null && !edit) { if (!instMeshes.TryGetValue(o.InstanceKey, out gm)) return true; }
        else if (!meshes.TryGetValue(o.Id, out gm)) return true;
        return !(gm.Version == o.Version && gm.Edit == edit && gm.Groups.Count > 0);
    }

    /// <summary>Returns false when it stopped sending new meshes to stay smooth (some casters left out: call again soon).</summary>
    public bool RenderShadows(Scene scene, IEnumerable<SceneObject> casters, Matrix4x4 lightViewProj, in GtaGlobalLights l, double uploadBudgetMs = double.MaxValue)
    {
        double up0 = UploadMs; bool complete = true;
        EnsureShadowMap();
        ctx.PixelShader.SetShaderResource(5, null);
        ctx.OutputMerger.SetRenderTargets(shadowDsv, (RenderTargetView)null);
        ctx.ClearDepthStencilView(shadowDsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.Rasterizer.SetViewport(0, 0, ShadowSize, ShadowSize);
        ctx.Rasterizer.SetScissorRectangle(0, 0, ShadowSize, ShadowSize);
        BeginScene(lightViewProj, Vector3.Zero, l, ShadingMode.Realistic, 1, 0);
        ctx.PixelShader.SetShaderResource(5, null);
        ctx.PixelShader.Set(null);
        ctx.Rasterizer.State = shadowRas;
        ctx.OutputMerger.SetDepthStencilState(DepthDefault);
        foreach (var o in casters)
        {
            if (UploadMs - up0 > uploadBudgetMs && NeedsUpload(o)) { complete = false; continue; } // sent over the next frames
            var gm = Upload(o);
            var world = o.World;
            var ov = new ObjectVarsCB { World = world, NormalMatrix = Matrix4x4.Identity };
            ctx.UpdateSubresource(ref ov, objectCB);
            foreach (var g in gm.Groups)
            {
                if (scene.Material(g.MatId).Preset.AlphaBlended) continue;
                ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(g.Vb, RenderVertex.SizeInBytes, 0));
                ctx.InputAssembler.SetIndexBuffer(g.Ib, Format.R32_UInt, 0);
                ctx.DrawIndexed(g.Count, 0, 0);
            }
        }
        ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
        ctx.PixelShader.Set(ps);
        return complete;
    }

    /// <summary>Textures of a material as last looked up: thousands of map draws skip four path lookups each.</summary>
    sealed class MatTex
    {
        public int Epoch = -1; public bool Pending; public string D, B, S, T, Tn, Lk; public GtaShaderPreset P; public ShaderResourceView Diff, Bump, Spec, Det, Tint, Look;
        public string LayerKey; public ShaderResourceView[] Lay = new ShaderResourceView[4], LayB = new ShaderResourceView[4];
    }
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<GtaMaterial, MatTex> matTex = new();

    ShaderResourceView Tex(string name, ref bool pending)
    {
        var t = Textures.Get(name);
        pending |= Textures.LastGetPending;
        return t;
    }

    /// <summary>Material bound last (reset each scene): the same material twice in a row is not sent again.</summary>
    GtaMaterial lastMat;

    void SetMaterial(GtaMaterial m)
    {
        var p = m.Preset;
        var mt = matTex.GetValue(m, _ => new MatTex());
        if (ReferenceEquals(m, lastMat) && !mt.Pending && mt.Epoch == Textures.Epoch) return;
        lastMat = m;
        bool terrain = m.IsTerrain;
        string lk = terrain ? string.Join("|", Enumerable.Range(0, 4).Select(i => m.Layer(i) + ">" + m.LayerBump(i))) : null;
        if (mt.Pending || mt.Epoch != Textures.Epoch || !ReferenceEquals(mt.P, p) || !ReferenceEquals(mt.D, m.DiffuseTexture) || !ReferenceEquals(mt.B, m.BumpTexture)
            || !ReferenceEquals(mt.S, m.SpecTexture) || !ReferenceEquals(mt.T, m.DetailTexture) || !ReferenceEquals(mt.Tn, m.TintTexture) || !ReferenceEquals(mt.Lk, m.LookupTexture) || mt.LayerKey != lk)
        {
            bool pending = false;
            mt.Diff = Tex(m.DiffuseTexture, ref pending);
            mt.Bump = p.Bump ? Tex(m.BumpTexture, ref pending) : null;
            mt.Spec = p.Spec ? Tex(m.SpecTexture, ref pending) : null;
            mt.Det = p.Detail ? Tex(m.DetailTexture, ref pending) : null;
            mt.Tint = m.TintTexture != null ? Tex(m.TintTexture, ref pending) : null;
            mt.Look = terrain && m.LookupTexture != null ? Tex(m.LookupTexture, ref pending) : null;
            mt.LayerKey = lk;
            for (int i = 0; i < 4; i++)
            {
                mt.Lay[i] = terrain ? Tex(m.Layer(i) ?? m.DiffuseTexture, ref pending) : null;
                mt.LayB[i] = terrain ? Tex(m.LayerBump(i) ?? m.BumpTexture, ref pending) : null;
            }
            mt.Pending = pending; mt.Epoch = Textures.Epoch; mt.P = p;
            mt.D = m.DiffuseTexture; mt.B = m.BumpTexture; mt.S = m.SpecTexture; mt.T = m.DetailTexture; mt.Tn = m.TintTexture; mt.Lk = m.LookupTexture;
        }
        var diff = mt.Diff; var bump = mt.Bump; var spec = mt.Spec; var det = mt.Det;
        var mv = new MaterialVarsCB
        {
            EnableTexture = diff != null ? 1u : 0u,
            EnableNormalMap = bump != null ? 1u : 0u,
            EnableSpecMap = spec != null ? 1u : 0u,
            EnableDetailMap = det != null && bump != null ? 1u : 0u,
            IsDecal = p.Decal ? 1u : 0u, IsEmissive = p.Emissive ? 1u : 0u, IsGlass = p.Glass ? 1u : 0u, AlphaTest = 1,
            Bumpiness = m.Bumpiness,
            SpecularIntensityMult = p.Spec || p.Glass ? m.SpecularIntensity : 0.05f,
            SpecularFalloffMult = m.SpecularFalloff, SpecularFresnel = m.SpecularFresnel,
            SpecMapIntMask = m.SpecMapIntMask, EmissiveMult = m.EmissiveMultiplier,
            DetailSettings = m.DetailSettings,
            ViewportColour = new Vector4(m.ViewportColour, 1),
            IsTerrain = terrain ? 1u : 0u,
            IsTint = mt.Tint != null && TintPalettes ? 1u : 0u,
            TerrainMode = terrain ? (uint)(mt.Look != null ? m.TerrainBlend + 1 : 1) : 0u,
            VertexColoured = diff == null && string.Equals(m.Shader, "cpv_only", StringComparison.OrdinalIgnoreCase) ? 1u : 0u,
        };
        if (terrain) { mv.EnableTexture = 1; mv.EnableNormalMap = 1; mv.EnableDetailMap = 0; }
        ctx.UpdateSubresource(ref mv, materialCB);
        for (int i = 0; i < 4; i++)
        {
            ctx.PixelShader.SetShaderResource(6 + i, mt.Lay[i] ?? diff ?? Textures.White);
            ctx.PixelShader.SetShaderResource(10 + i, mt.LayB[i] ?? Textures.FlatNormal);
        }
        ctx.PixelShader.SetShaderResource(0, diff ?? Textures.White);
        ctx.PixelShader.SetShaderResource(2, bump ?? Textures.FlatNormal);
        ctx.PixelShader.SetShaderResource(3, spec ?? Textures.DefaultSpec);
        ctx.PixelShader.SetShaderResource(4, det ?? Textures.FlatNormal);
        ctx.PixelShader.SetShaderResource(14, mt.Tint ?? Textures.White);
        ctx.PixelShader.SetShaderResource(15, mt.Look ?? Textures.White);
    }

    /// <summary>Tint shaders take their colour from the palette (MAP mode: the game's buildings in their real colours).</summary>
    public bool TintPalettes = true;
    /// <summary>Shapes the game never shows (trees' shadow balls, mirrors' placeholders...) are not drawn.</summary>
    public bool HideNeverDrawn = true;

    /// <summary>
    /// Draws one object. <paramref name="alphaPass"/> selects the blended materials (decals, glass);
    /// the opaque pass draws everything else.
    /// </summary>
    public void DrawObject(Scene scene, SceneObject o, bool alphaPass, Vector4 highlight, bool backfaceCull)
    {
        if (Picking && (alphaPass || o.GtaType is GtaObjectType.Collision or GtaObjectType.Portal)) return;
        if (UploadBudgetMs > 0 && o.Id != EditObjectId && UploadMs - frameUpload0 > UploadBudgetMs && NeedsUpload(o)) return; // map: shown next frame
        var gm = Upload(o);
        var world = o.World;
        Matrix4x4.Invert(world, out var inv);
        var ov = new ObjectVarsCB
        {
            World = world, NormalMatrix = Matrix4x4.Transpose(inv),
            WireColour = new Vector4(o.WireColour, 1), Highlight = highlight, UvRow0 = o.UvRow0, UvRow1 = o.UvRow1,
        };
        ctx.UpdateSubresource(ref ov, objectCB);
        // MLO rooms are meant to be seen from inside: always cull so the walls vanish from outside like in game.
        ctx.Rasterizer.State = o.GtaType == GtaObjectType.Portal || NoCull ? CullNone : backfaceCull || o.GtaType == GtaObjectType.MloRoom ? CullBack : CullNone;
        foreach (var g in gm.Groups)
        {
            var mat = scene.Material(g.MatId);
            if (HideNeverDrawn && mat.NeverDrawn) continue;
            bool blended = mat.Preset.AlphaBlended || o.GtaType == GtaObjectType.Collision || o.GtaType == GtaObjectType.Portal;
            if (blended != alphaPass) continue;
            SetMaterial(mat);
            if (o.GtaType == GtaObjectType.Collision || o.GtaType == GtaObjectType.Portal)
            {
                // collision and portals: tinted see-through so the shell stays readable
                var mv = new MaterialVarsCB
                {
                    IsGlass = 1, ViewportColour = o.GtaType == GtaObjectType.Collision ? new Vector4(MoloMax.Core.Gta.Export.CollisionMaterials.Colour(o.Gta.CollisionMaterial), 1) : new Vector4(0.3f, 0.85f, 0.95f, 1),
                    SpecMapIntMask = Vector3.UnitX, Bumpiness = 1, EmissiveMult = 1,
                };
                ctx.UpdateSubresource(ref mv, materialCB);
                ctx.PixelShader.SetShaderResource(0, Textures.White);
                lastMat = null;
            }
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(g.Vb, RenderVertex.SizeInBytes, 0));
            ctx.InputAssembler.SetIndexBuffer(g.Ib, Format.R32_UInt, 0);
            ctx.DrawIndexed(g.Count, 0, 0);
            StatDraws++; StatTris += g.Count / 3;
        }
    }

    /// <summary>
    /// Draws one model placed many times in one call per material (map mode). <paramref name="template"/> gives the
    /// mesh (and its GPU upload); <paramref name="worlds"/> the placements.
    /// </summary>
    public void DrawInstanced(Scene scene, SceneObject template, Matrix4x4[] worlds, int count, bool alphaPass, bool backfaceCull, bool depthOnly = false)
    {
        if (count <= 0 || Picking && alphaPass) return;
        // map mode: new meshes sent at most ~UploadBudgetMs per frame, the others show a frame or two later (no hitch)
        if (UploadBudgetMs > 0 && UploadMs - frameUpload0 > UploadBudgetMs && NeedsUpload(template)) return;
        var gm = Upload(template);
        if (gm.Groups.Count == 0) return;
        // one big buffer filled one after the other (no-overwrite), emptied only when full: thousands of map draws a frame
        // otherwise each ask the driver for a fresh buffer
        if (instBuf == null || instCap < count)
        {
            instBuf?.Dispose();
            instCap = Math.Max(1 << 16, count * 2);
            instBuf = new Buffer(device, instCap * 64, ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
            instOff = instCap;
        }
        bool wrap = instOff + count > instCap;
        if (wrap) instOff = 0;
        var box = ctx.MapSubresource(instBuf, 0, wrap ? MapMode.WriteDiscard : MapMode.WriteNoOverwrite, SharpDX.Direct3D11.MapFlags.None);
        unsafe { fixed (Matrix4x4* src = worlds) System.Buffer.MemoryCopy(src, (byte*)box.DataPointer + (long)instOff * 64, (long)(instCap - instOff) * 64, (long)count * 64); }
        ctx.UnmapSubresource(instBuf, 0);
        var ov = new ObjectVarsCB { World = Matrix4x4.Identity, NormalMatrix = Matrix4x4.Identity, WireColour = new Vector4(template.WireColour, 1), Highlight = Vector4.Zero };
        ctx.UpdateSubresource(ref ov, objectCB);
        ctx.InputAssembler.InputLayout = layoutInst;
        ctx.VertexShader.Set(vsInst);
        if (!depthOnly) ctx.Rasterizer.State = Picking ? CullNone : NoCull ? CullNone : backfaceCull ? CullBack : CullNone;
        foreach (var g in gm.Groups)
        {
            var mat = scene.Material(g.MatId);
            if (mat.Preset.AlphaBlended != alphaPass || HideNeverDrawn && mat.NeverDrawn && !depthOnly) continue;
            if (!depthOnly) SetMaterial(mat);
            instBindings[0] = new VertexBufferBinding(g.Vb, RenderVertex.SizeInBytes, 0);
            instBindings[1] = new VertexBufferBinding(instBuf, 64, instOff * 64);
            ctx.InputAssembler.SetVertexBuffers(0, instBindings);
            ctx.InputAssembler.SetIndexBuffer(g.Ib, Format.R32_UInt, 0);
            ctx.DrawIndexedInstanced(g.Count, count, 0, 0, 0);
            StatDraws++; StatTris += (long)g.Count / 3 * count;
        }
        instOff += count;
        ctx.InputAssembler.InputLayout = layout;
        ctx.VertexShader.Set(vs);
    }

    public void Dispose()
    {
        foreach (var m in meshes.Values) m.Dispose();
        instBuf?.Dispose(); layoutInst.Dispose(); vsInst.Dispose();
        Textures.Dispose();
        CullBack.Dispose(); CullNone.Dispose(); LinesState.Dispose();
        DepthDefault.Dispose(); DepthRead.Dispose(); DepthOff.Dispose(); Opaque.Dispose(); Alpha.Dispose();
        shadowSrv?.Dispose(); shadowDsv?.Dispose(); shadowTex?.Dispose(); shadowSampler.Dispose(); shadowRas.Dispose(); lightCB.Dispose(); clipCB.Dispose(); fogCB.Dispose(); pickCB.Dispose(); psPick.Dispose(); pickRtv?.Dispose(); pickTex?.Dispose(); pickZRtv?.Dispose(); pickZTex?.Dispose(); pickDsv?.Dispose(); pickDepth?.Dispose(); pickStage?.Dispose(); pickZStage?.Dispose();
        sampler.Dispose(); sceneCB.Dispose(); objectCB.Dispose(); materialCB.Dispose();
        layout.Dispose(); ps.Dispose(); vs.Dispose();
    }
}
