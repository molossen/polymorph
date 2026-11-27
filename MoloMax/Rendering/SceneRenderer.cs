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
    public uint Parallax; public float HeightScale, HeightBias; public uint EmissiveNight;
    public Vector4 LayerHeightScale, LayerHeightBias;
}

/// <summary>One scene light as the shader sees it.</summary>
public struct ViewLight
{
    public Vector3 Position, Direction, Colour; // colour already times intensity
    public float Range, HalfLength, CosInner, CosOuter, FalloffExponent, Radius, Diffuse, Specular;
    public int Kind; // 0 point, 1 spot, 2 capsule
}

[Flags] public enum RenderFlags : uint { None = 0, Filmic = 1, SunShadows = 2, SceneLights = 4, FaceOrientation = 8, MapLook = 16 }

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
    public const int MaxLights = 32, ShadowSize = 4096; // the sun shadow images (4096 like the reference map view)
    const int LightVec4s = 7 + MaxLights * 5;
    public Vector3 FaceFront = new(0.19f, 0.36f, 1f), FaceBack = new(1f, 0.12f, 0.12f);
    readonly Vector4[] lightData = new Vector4[LightVec4s];
    Texture2D shadowTex;
    DepthStencilView shadowDsv;
    readonly DepthStencilView[] shadowSlices = new DepthStencilView[3];
    readonly Texture2D[] shadowTexs = new Texture2D[3];
    readonly ShaderResourceView[] shadowSrvs = new ShaderResourceView[3];
    Buffer cascadeCB, mapLookCB, nrShadowCB;
    RasterizerState shadowRasClamp;
    readonly Vector4[] nrShadowData = new Vector4[15];
    readonly Vector4[] mapLookData = new Vector4[10];
    readonly Vector4[] cascadeData = new Vector4[9];
    ShaderResourceView shadowSrv;
    readonly SamplerState shadowSampler;
    readonly RasterizerState shadowRas;
    readonly SamplerState sampler;
    public readonly TextureCache Textures;
    readonly Dictionary<int, GpuMesh> meshes = new();
    /// <summary>Meshes on the graphics card (own + shared by instances).</summary>
    public int MeshCount => meshes.Count + instMeshes.Count;

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
        cascadeCB = new Buffer(device, cascadeData.Length * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        nrShadowCB = new Buffer(device, nrShadowData.Length * 16 + 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        mapLookCB = new Buffer(device, mapLookData.Length * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        ctx.UpdateSubresource(cascadeData, cascadeCB);

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
        shadowRasClamp = new RasterizerState(device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = false, DepthBias = 0, SlopeScaledDepthBias = 2.5f, DepthBiasClamp = 0.01f,
        });
        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.Anisotropic, MaximumAnisotropy = 16,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Always, MinimumLod = 0, MaximumLod = float.MaxValue,
        });
        Textures = new TextureCache(device);
        SharedDevice = device;

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
    /// <summary>Their vertex and index buffers too, made by the reading thread (one per group, same order): the first
    /// upload only takes them, nothing is copied to the graphics card on this thread.</summary>
    public readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, List<(Buffer Vb, Buffer Ib, int Count)>> PrebuiltGpu = new();
    /// <summary>The device, for the reading threads (creating buffers is allowed from any thread in Direct3D 11).</summary>
    public static Device SharedDevice { get; private set; }
    /// <summary>Background thread: the buffers of these groups on the graphics card (empty groups get none).</summary>
    public static List<(Buffer Vb, Buffer Ib, int Count)> MakeBuffers(List<RenderGroup> groups)
    {
        var d = SharedDevice; if (d == null || groups == null) return null;
        var res = new List<(Buffer, Buffer, int)>(groups.Count);
        foreach (var g in groups)
        {
            if (g.IndexCount == 0) { res.Add((null, null, 0)); continue; }
            var verts = g.VertexArray ?? g.Vertices.ToArray(); var idx = g.IndexArray ?? g.Indices.ToArray();
            res.Add((Buffer.Create(d, BindFlags.VertexBuffer, verts), Buffer.Create(d, BindFlags.IndexBuffer, idx), idx.Length));
        }
        return res;
    }
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
        bool prebuilt = false;
        if (!edit && Prebuilt.TryGetValue(src, out var pre)) { groups = pre; Prebuilt.Remove(src); prebuilt = true; }
        else groups = RenderMeshBuilder.Build(src);
        if (prebuilt && PrebuiltGpu.TryGetValue(src, out var bufs))
        {
            PrebuiltGpu.Remove(src);
            if (bufs.Count == groups.Count)
            {
                for (int i = 0; i < groups.Count; i++) if (bufs[i].Count > 0) gm.Groups.Add((bufs[i].Vb, bufs[i].Ib, bufs[i].Count, groups[i].MaterialId));
                UploadMs += upSw.Elapsed.TotalMilliseconds;
                return gm;
            }
            foreach (var b in bufs) { b.Vb?.Dispose(); b.Ib?.Dispose(); }
        }
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
        double ms = upSw.Elapsed.TotalMilliseconds;
        UploadMs += ms;
        if (ms > SlowestUploadMs) { SlowestUploadMs = ms; SlowestUpload = $"{o.Name} ({src.Faces.Count:N0} faces, {(prebuilt ? "built ahead" : edit ? "edit mesh" : "built here")}, {ms:0.0} ms)"; }
        return gm;
    }
    /// <summary>The slowest single mesh upload since the last reset (profiling of hitches).</summary>
    public double SlowestUploadMs; public string SlowestUpload;

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

    /// <summary>Binds the scene shaders and buffers again after another renderer (water) used the pipeline.</summary>
    public void RebindScene()
    {
        lastMat = null;
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
        ctx.PixelShader.SetShaderResource(21, shadowSrvs[1]); ctx.PixelShader.SetShaderResource(22, shadowSrvs[2]);
        ctx.PixelShader.SetConstantBuffer(8, cascadeCB); ctx.PixelShader.SetConstantBuffer(9, mapLookCB); ctx.PixelShader.SetConstantBuffer(10, nrShadowCB);
    }

    /// <summary>Last haze sent (for the water, drawn with the same).</summary>
    public (Vector4 Colour, Vector4 Range) Fog => (fogData[0], fogData[1]);

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
        ctx.PixelShader.SetShaderResource(21, shadowSrvs[1]); ctx.PixelShader.SetShaderResource(22, shadowSrvs[2]);
        ctx.PixelShader.SetConstantBuffer(8, cascadeCB); ctx.PixelShader.SetConstantBuffer(9, mapLookCB); ctx.PixelShader.SetConstantBuffer(10, nrShadowCB);
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
        // 0: the near map (MODEL mode, and around the camera target in MAP mode); 1 and 2: wider maps in MAP mode
        // (three textures, not an array: comparison sampling of arrays needs a newer shader model)
        for (int i = 0; i < 3; i++)
        {
            shadowTexs[i] = new Texture2D(device, new Texture2DDescription
            {
                Width = ShadowSize, Height = ShadowSize, MipLevels = 1, ArraySize = 1, Format = Format.R32_Typeless, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            });
            shadowSlices[i] = new DepthStencilView(device, shadowTexs[i], new DepthStencilViewDescription { Format = Format.D32_Float, Dimension = DepthStencilViewDimension.Texture2D });
            shadowSrvs[i] = new ShaderResourceView(device, shadowTexs[i], new ShaderResourceViewDescription { Format = Format.R32_Float, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 } });
            ctx.ClearDepthStencilView(shadowSlices[i], DepthStencilClearFlags.Depth, 1f, 0);
        }
        shadowTex = shadowTexs[0]; shadowDsv = shadowSlices[0]; shadowSrv = shadowSrvs[0];
    }

    /// <summary>MAP look: the sun shadow slices (matrices, far end of each slice along the view, texel sizes, on / off).</summary>
    public void SetMapShadows(MapShadowSlices sl, bool on)
    {
        var d = nrShadowData;
        for (int i = 0; i < 3; i++) { var m = sl.Cascades[i]; d[i * 4] = new(m.M11, m.M12, m.M13, m.M14); d[i * 4 + 1] = new(m.M21, m.M22, m.M23, m.M24); d[i * 4 + 2] = new(m.M31, m.M32, m.M33, m.M34); d[i * 4 + 3] = new(m.M41, m.M42, m.M43, m.M44); }
        d[12] = new(sl.Splits[0], sl.Splits[1], sl.Splits[2], on ? 1 : 0);
        d[13] = new(sl.TexelSizes[0], sl.TexelSizes[1], sl.TexelSizes[2], 1f / sl.Size);
        d[14] = new(sl.CameraForward, 0);
        ctx.UpdateSubresource(d, nrShadowCB);
    }

    /// <summary>MAP look (lighting): the light of the hour, for the RenderFlags.MapLook pass.</summary>
    public void SetMapLook(in MoloMax.Core.Gta.Look.MapLighting l, Vector3 camera, bool haze)
    {
        mapLookData[0] = new(l.SunDirection, 0); mapLookData[1] = new(l.SunColor, 0); mapLookData[2] = new(l.SkyAmbient, 0);
        mapLookData[3] = new(l.GroundAmbient, 0); mapLookData[4] = new(l.Horizon, haze && l.FogDistance > 0 ? 1 : 0);
        mapLookData[5] = new(camera, MathF.Max(l.FogDistance, 1)); mapLookData[6] = new(l.Zenith, 0); mapLookData[7] = new(l.NightLights, l.IsHdr ? 1 : 0, MoloMax.Core.Gta.Look.MapLighting.HdrEmission, 0);
        mapLookData[8] = new(l.FogSun, 0); mapLookData[9] = new(l.Sun, 0);
        ctx.UpdateSubresource(mapLookData, mapLookCB);
    }

    /// <summary>MAP: the wider shadow maps (slices 1 and 2) and how many are in use (0 = only the near one).</summary>
    public void SetCascades(Matrix4x4 vp1, float texel1, Matrix4x4 vp2, float texel2, int count)
    {
        void M(int at, Matrix4x4 m) { cascadeData[at] = new(m.M11, m.M12, m.M13, m.M14); cascadeData[at + 1] = new(m.M21, m.M22, m.M23, m.M24); cascadeData[at + 2] = new(m.M31, m.M32, m.M33, m.M34); cascadeData[at + 3] = new(m.M41, m.M42, m.M43, m.M44); }
        M(0, vp1); M(4, vp2); cascadeData[8] = new(texel1, texel2, count, 0);
        ctx.UpdateSubresource(cascadeData, cascadeCB);
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
    public bool RenderShadows(Scene scene, IEnumerable<SceneObject> casters, Matrix4x4 lightViewProj, in GtaGlobalLights l, double uploadBudgetMs = double.MaxValue,
        IReadOnlyList<(SceneObject Template, Matrix4x4[] Worlds, int Count)> instanced = null, int slice = 0, bool noDepthClip = false)
    {
        double up0 = UploadMs; bool complete = true;
        EnsureShadowMap();
        ctx.PixelShader.SetShaderResource(5, null);
        ctx.PixelShader.SetShaderResource(5, null); ctx.PixelShader.SetShaderResource(21, null); ctx.PixelShader.SetShaderResource(22, null); // read by the scene, written here
        var dsv = shadowSlices[Math.Clamp(slice, 0, 2)];
        ctx.OutputMerger.SetRenderTargets(dsv, (RenderTargetView)null);
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.Rasterizer.SetViewport(0, 0, ShadowSize, ShadowSize);
        ctx.Rasterizer.SetScissorRectangle(0, 0, ShadowSize, ShadowSize);
        BeginScene(lightViewProj, Vector3.Zero, l, ShadingMode.Realistic, 1, 0);
        ctx.PixelShader.SetShaderResource(5, null);
        ctx.PixelShader.Set(null);
        ctx.Rasterizer.State = noDepthClip ? shadowRasClamp : shadowRas;
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
        if (instanced != null)
            foreach (var (t, w, n) in instanced)
            {
                if (UploadMs - up0 > uploadBudgetMs && NeedsUpload(t)) { complete = false; continue; }
                DrawInstanced(scene, t, w, n, false, false, depthOnly: true);
            }
        ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
        ctx.PixelShader.Set(ps);
        return complete;
    }

    /// <summary>Textures of a material as last looked up: thousands of map draws skip four path lookups each.</summary>
    sealed class MatTex
    {
        public int Epoch = -1; public bool Pending; public string D, B, S, T, Tn, Lk; public GtaShaderPreset P; public ShaderResourceView Diff, Bump, Spec, Det, Tint, Look;
        public string HgtN, Sh, NightD; public bool Terrain; public string[] LayN = new string[4], LayBN = new string[4], LayHN = new string[4]; public bool NightE, CpvOnly, Night; public ShaderResourceView[] Lay = new ShaderResourceView[4], LayB = new ShaderResourceView[4], LayH = new ShaderResourceView[4]; public ShaderResourceView Hgt;
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
        // layers and height maps compared name by name (no text built at every draw)
        bool layersSame = mt.Terrain == terrain && ReferenceEquals(mt.HgtN, m.HeightTexture);
        for (int i = 0; i < 4 && layersSame; i++)
            layersSame = ReferenceEquals(mt.LayN[i], terrain ? m.Layer(i) : null) && ReferenceEquals(mt.LayBN[i], terrain ? m.LayerBump(i) : null) && ReferenceEquals(mt.LayHN[i], m.LayerHeight(i));
        if (mt.Pending || mt.Epoch != Textures.Epoch || !ReferenceEquals(mt.P, p) || !ReferenceEquals(mt.D, m.DiffuseTexture) || !ReferenceEquals(mt.B, m.BumpTexture)
            || !ReferenceEquals(mt.S, m.SpecTexture) || !ReferenceEquals(mt.T, m.DetailTexture) || !ReferenceEquals(mt.Tn, m.TintTexture) || !ReferenceEquals(mt.Lk, m.LookupTexture) || !layersSame)
        {
            bool pending = false;
            mt.Diff = Tex(m.DiffuseTexture, ref pending);
            mt.Bump = p.Bump ? Tex(m.BumpTexture, ref pending) : null;
            mt.Spec = p.Spec ? Tex(m.SpecTexture, ref pending) : null;
            mt.Det = p.Detail ? Tex(m.DetailTexture, ref pending) : null;
            mt.Tint = m.TintTexture != null ? Tex(m.TintTexture, ref pending) : null;
            mt.Look = terrain && m.LookupTexture != null ? Tex(m.LookupTexture, ref pending) : null;
            mt.Terrain = terrain; mt.HgtN = m.HeightTexture;
            for (int i = 0; i < 4; i++) { mt.LayN[i] = terrain ? m.Layer(i) : null; mt.LayBN[i] = terrain ? m.LayerBump(i) : null; mt.LayHN[i] = m.LayerHeight(i); }
            mt.Hgt = m.HeightTexture != null ? Tex(m.HeightTexture, ref pending) : null;
            for (int i = 0; i < 4; i++) mt.LayH[i] = terrain && m.LayerHeight(i) != null ? Tex(m.LayerHeight(i), ref pending) : null;
            for (int i = 0; i < 4; i++)
            {
                mt.Lay[i] = terrain ? Tex(m.Layer(i) ?? m.DiffuseTexture, ref pending) : null;
                mt.LayB[i] = terrain ? Tex(m.LayerBump(i) ?? m.BumpTexture, ref pending) : null;
            }
            mt.Pending = pending; mt.Epoch = Textures.Epoch; mt.P = p;
            mt.D = m.DiffuseTexture; mt.B = m.BumpTexture; mt.S = m.SpecTexture; mt.T = m.DetailTexture; mt.Tn = m.TintTexture; mt.Lk = m.LookupTexture;
        }
        if (!ReferenceEquals(mt.Sh, m.Shader) || !ReferenceEquals(mt.NightD, m.DiffuseTexture) || mt.NightE != p.Emissive)
        {
            // worked out from the names once per material, not at every draw
            mt.Sh = m.Shader; mt.NightD = m.DiffuseTexture; mt.NightE = p.Emissive;
            mt.CpvOnly = string.Equals(m.Shader, "cpv_only", StringComparison.OrdinalIgnoreCase);
            mt.Night = m.Shader != null && (m.Shader.Contains("night", StringComparison.OrdinalIgnoreCase)
                || p.Emissive && m.DiffuseTexture != null && Path.GetFileNameWithoutExtension(m.DiffuseTexture).Contains("night", StringComparison.OrdinalIgnoreCase));
        }
        var diff = mt.Diff; var bump = mt.Bump; var spec = mt.Spec; var det = mt.Det;
        var mv = new MaterialVarsCB
        {
            EnableTexture = diff != null || mt.Pending && m.DiffuseTexture != null ? 1u : 0u,
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
            VertexColoured = diff == null && mt.CpvOnly ? 1u : 0u,
            // lit only at night: the emissivenight shaders, and emissive windows whose texture is a night texture (..._nightem, ..._night)
            EmissiveNight = mt.Night ? 1u : 0u,
        };
        if (terrain) { mv.EnableTexture = 1; mv.EnableNormalMap = 1; mv.EnableDetailMap = 0; }
        // parallax (PXM): the game's tessellation values are far bigger than what a parallax wants: brought to its range
        if (Parallax && bump != null && (terrain ? mt.LayH[0] != null || mt.LayH[1] != null || mt.LayH[2] != null || mt.LayH[3] != null : mt.Hgt != null))
        {
            float hs = m.HeightScale, hb = m.HeightBias;
            if (MathF.Abs(hs) > 0.1f || hb < 0) { hs = MathF.Abs(hs) * 0.075f; hb = hs * 0.5f; }
            static Vector4 Fix(Vector4 v) => new(Fx(v.X), Fx(v.Y), Fx(v.Z), Fx(v.W));
            static float Fx(float x) => MathF.Abs(x) > 0.1f ? MathF.Abs(x) * 0.075f : x;
            mv.Parallax = terrain ? 2u : 1u; mv.HeightScale = hs; mv.HeightBias = hb;
            mv.LayerHeightScale = Fix(m.LayerHeightScale);
            var lb = m.LayerHeightBias; mv.LayerHeightBias = new Vector4(lb.X < 0 ? mv.LayerHeightScale.X * 0.5f : lb.X, lb.Y < 0 ? mv.LayerHeightScale.Y * 0.5f : lb.Y, lb.Z < 0 ? mv.LayerHeightScale.Z * 0.5f : lb.Z, lb.W < 0 ? mv.LayerHeightScale.W * 0.5f : lb.W);
        }
        ctx.UpdateSubresource(ref mv, materialCB);
        // the textures in three calls (not nineteen): thousands of materials a frame in MAP mode
        ctx.PixelShader.SetShaderResource(0, diff ?? (mt.Pending && m.DiffuseTexture != null ? Textures.Waiting : Textures.White));
        srv3[0] = bump ?? Textures.FlatNormal; srv3[1] = spec ?? Textures.DefaultSpec; srv3[2] = det ?? Textures.FlatNormal;
        ctx.PixelShader.SetShaderResources(2, srv3);
        for (int i = 0; i < 4; i++)
        {
            srv15[i] = mt.Lay[i] ?? diff ?? (mt.Pending ? Textures.Waiting : Textures.White);
            srv15[4 + i] = mt.LayB[i] ?? Textures.FlatNormal;
            srv15[11 + i] = mt.LayH[i] ?? Textures.White;
        }
        srv15[8] = mt.Tint ?? Textures.White; srv15[9] = mt.Look ?? Textures.White; srv15[10] = mt.Hgt ?? Textures.White;
        ctx.PixelShader.SetShaderResources(6, srv15);
    }
    readonly ShaderResourceView[] srv3 = new ShaderResourceView[3], srv15 = new ShaderResourceView[15];

    /// <summary>Tint shaders take their colour from the palette (MAP mode: the game's buildings in their real colours).</summary>
    public bool TintPalettes = true;
    /// <summary>Shapes the game never shows (trees' shadow balls, mirrors' placeholders...) are not drawn.</summary>
    /// <summary>Parallax (PXM) on the materials that have a height map (realistic shading).</summary>
    public bool Parallax = true;
    public bool HideNeverDrawn = true;
    /// <summary>MAP: the water_* shader meshes (sea foam, pools, rivers) are left to the water surface renderer.</summary>
    public bool HideWaterShaders;
    /// <summary>MAP by day: the glow-only decals of the night (decal_emissive_only, decal_emissivenight_only) are not drawn.</summary>
    public bool HideNightGlows;
    /// <summary>The hidden water_* parts met this frame (pools, fountains, rivers): drawn by the water surface renderer with their placement.</summary>
    public readonly List<(Buffer Vb, Buffer Ib, int Count, Matrix4x4 World)> WaterMeshes = new();

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
            if (HideWaterShaders && mat.IsWaterShader) { if (!alphaPass && !Picking) WaterMeshes.Add((g.Vb, g.Ib, g.Count, world)); continue; }
            if (HideNeverDrawn && mat.NeverDrawn || HideNightGlows && mat.IsNightGlow) continue;
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
        bool any = false; // nothing of this model in this pass: no instance copy at all (most models have no blended part)
        foreach (var g in gm.Groups) if (scene.Material(g.MatId).Preset.AlphaBlended == alphaPass) { any = true; break; }
        if (HideWaterShaders && !alphaPass && !Picking && !depthOnly)
            foreach (var g in gm.Groups)
                if (scene.Material(g.MatId).IsWaterShader)
                    for (int k = 0; k < count && k < worlds.Length; k++) WaterMeshes.Add((g.Vb, g.Ib, g.Count, worlds[k]));
        if (!any) return;
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
        // shadow of a tree: its shadow shapes stand in for the leaves (cut-out cards would cast solid squares without a pixel shader)
        bool proxy = depthOnly && gm.Groups.Any(g => ShadowProxy(scene.Material(g.MatId)));
        foreach (var g in gm.Groups)
        {
            var mat = scene.Material(g.MatId);
            if (depthOnly && (mat.NeverDrawn && !ShadowProxy(mat) || proxy && mat.Preset.Cutout && !ShadowProxy(mat))) continue;
            if (mat.Preset.AlphaBlended != alphaPass || HideNeverDrawn && mat.NeverDrawn && !depthOnly || HideWaterShaders && mat.IsWaterShader || HideNightGlows && mat.IsNightGlow) continue;
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

    static bool ShadowProxy(GtaMaterial m) => m.ShadowOnly || string.Equals(m.Shader, "trees_shadow_proxy", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var m in meshes.Values) m.Dispose();
        instBuf?.Dispose(); layoutInst.Dispose(); vsInst.Dispose();
        Textures.Dispose();
        CullBack.Dispose(); CullNone.Dispose(); LinesState.Dispose();
        DepthDefault.Dispose(); DepthRead.Dispose(); DepthOff.Dispose(); Opaque.Dispose(); Alpha.Dispose();
        foreach (var d in shadowSrvs) d?.Dispose(); foreach (var d in shadowSlices) d?.Dispose(); foreach (var d in shadowTexs) d?.Dispose(); cascadeCB?.Dispose(); mapLookCB?.Dispose(); nrShadowCB?.Dispose(); shadowRasClamp?.Dispose(); shadowSampler.Dispose(); shadowRas.Dispose(); lightCB.Dispose(); clipCB.Dispose(); fogCB.Dispose(); pickCB.Dispose(); psPick.Dispose(); pickRtv?.Dispose(); pickTex?.Dispose(); pickZRtv?.Dispose(); pickZTex?.Dispose(); pickDsv?.Dispose(); pickDepth?.Dispose(); pickStage?.Dispose(); pickZStage?.Dispose();
        sampler.Dispose(); sceneCB.Dispose(); objectCB.Dispose(); materialCB.Dispose();
        layout.Dispose(); ps.Dispose(); vs.Dispose();
    }
}
