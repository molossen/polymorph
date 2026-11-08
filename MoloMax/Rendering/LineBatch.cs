using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct LineVertex
{
    public Vector3 Position;
    public Vector4 Colour;
    public LineVertex(Vector3 p, Vector4 c) { Position = p; Colour = c; }
}

[StructLayout(LayoutKind.Sequential)]
struct LineVars
{
    public Matrix4x4 ViewProj;
    public Vector4 DepthBias;
    public Vector4 Tint;
}

/// <summary>Immediate-mode coloured lines and flat triangles (grid, wires, brackets, selection tint).</summary>
public sealed class LineBatch : IDisposable
{
    readonly Device device;
    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly InputLayout layout;
    readonly Buffer cb;
    Buffer vb;
    int capacity;

    public readonly List<LineVertex> Lines = new();
    public readonly List<LineVertex> Tris = new();

    public LineBatch(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using var vsb = ShaderLoader.Compile("lines.hlsl", "VSMain", "vs_4_0");
        using var psb = ShaderLoader.Compile("lines.hlsl", "PSMain", "ps_4_0");
        vs = new VertexShader(device, vsb);
        ps = new PixelShader(device, psb);
        layout = new InputLayout(device, vsb, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        });
        cb = new Buffer(device, Marshal.SizeOf<LineVars>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        Ensure(65536);
    }

    void Ensure(int count)
    {
        if (vb != null && count <= capacity) return;
        vb?.Dispose();
        capacity = Math.Max(count, capacity * 2);
        vb = new Buffer(device, capacity * Marshal.SizeOf<LineVertex>(), ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
    }

    public void Line(Vector3 a, Vector3 b, Vector4 c) { Lines.Add(new LineVertex(a, c)); Lines.Add(new LineVertex(b, c)); }
    public void Line(Vector3 a, Vector3 b, Vector4 ca, Vector4 cb2) { Lines.Add(new LineVertex(a, ca)); Lines.Add(new LineVertex(b, cb2)); }
    public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector4 col) { Tris.Add(new LineVertex(a, col)); Tris.Add(new LineVertex(b, col)); Tris.Add(new LineVertex(c, col)); }

    public void Clear() { Lines.Clear(); Tris.Clear(); }

    /// <summary>How the "pull towards the camera" of wires and selection overlays is done for the current view
    /// (set once per viewport): (per-unit fraction of the distance, B term of the projection, ortho NDC per unit).
    /// A fixed NDC offset grew with the square of the distance (metres when zoomed out: the back of the room showed
    /// through); now it is a fixed fraction of the distance, like 3ds Max.</summary>
    public Vector3 View;

    public void SetView(bool ortho, float near, float far, float orthoHeight)
    {
        const float k = 20f; // bias 0.0004 = 0.8 % of the distance (of the view height in orthographic views)
        View = ortho
            ? new Vector3(0, 0, k * MathF.Max(orthoHeight, 0.1f) / far) // the orthographic depth range is "far" metres
            : new Vector3(k, near * far / (near - far), 0);
    }

    Vector4 Bias(float b) => new(b, View.X, View.Y, View.Z);

    /// <summary>Draws and clears the queued primitives with the given camera and states.</summary>
    public void Flush(Matrix4x4 viewProj, float depthBias = 0f)
    {
        var vars = new LineVars { ViewProj = viewProj, DepthBias = Bias(depthBias), Tint = Vector4.One };
        ctx.UpdateSubresource(ref vars, cb);
        ctx.InputAssembler.InputLayout = layout;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps);
        ctx.GeometryShader.Set(null);
        Draw(Tris, PrimitiveTopology.TriangleList);
        Draw(Lines, PrimitiveTopology.LineList);
        Clear();
    }

    void Draw(List<LineVertex> list, PrimitiveTopology topo)
    {
        if (list.Count == 0) return;
        Ensure(list.Count);
        var box = ctx.MapSubresource(vb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        var span = CollectionsMarshal.AsSpan(list);
        unsafe
        {
            fixed (LineVertex* src = span)
                System.Buffer.MemoryCopy(src, (void*)box.DataPointer, (long)capacity * sizeof(LineVertex), (long)list.Count * sizeof(LineVertex));
        }
        ctx.UnmapSubresource(vb, 0);
        ctx.InputAssembler.PrimitiveTopology = topo;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, Marshal.SizeOf<LineVertex>(), 0));
        ctx.Draw(list.Count, 0);
    }

    /// <summary>Draws a prebuilt line-list buffer (object space) with its world matrix and a colour tint.</summary>
    public void DrawStatic(Buffer buffer, int vertexCount, Matrix4x4 worldViewProj, Vector4 tint, float depthBias)
    {
        if (buffer == null || vertexCount == 0) return;
        var vars = new LineVars { ViewProj = worldViewProj, DepthBias = Bias(depthBias), Tint = tint };
        ctx.UpdateSubresource(ref vars, cb);
        ctx.InputAssembler.InputLayout = layout;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps);
        ctx.GeometryShader.Set(null);
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(buffer, Marshal.SizeOf<LineVertex>(), 0));
        ctx.Draw(vertexCount, 0);
    }

    /// <summary>Draws a prebuilt triangle-list buffer (object space) with a colour tint.</summary>
    public void DrawStaticTris(Buffer buffer, int vertexCount, Matrix4x4 worldViewProj, Vector4 tint, float depthBias)
    {
        if (buffer == null || vertexCount == 0) return;
        var vars = new LineVars { ViewProj = worldViewProj, DepthBias = Bias(depthBias), Tint = tint };
        ctx.UpdateSubresource(ref vars, cb);
        ctx.InputAssembler.InputLayout = layout;
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps);
        ctx.GeometryShader.Set(null);
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(buffer, Marshal.SizeOf<LineVertex>(), 0));
        ctx.Draw(vertexCount, 0);
    }

    public Device Device => device;

    public void Dispose() { vb?.Dispose(); cb.Dispose(); layout.Dispose(); ps.Dispose(); vs.Dispose(); }
}

/// <summary>
/// Object wires kept on the GPU: built once per mesh version instead of re-sending every edge every frame
/// (a 1M-vertex scene has ~2M edges).
/// </summary>
public sealed class WireCache : IDisposable
{
    sealed class Entry { public int Version; public Buffer Vb; public int Count; public LineVertex[] Cpu; public int[] EndVert; public int[] Start, Idx; }
    readonly Dictionary<(int Id, bool Edit), Entry> cache = new();

    public (Buffer Vb, int Count) Get(Device device, int id, bool edit, int version, Func<(IReadOnlyList<Vector3> Verts, IReadOnlyList<MoloMax.Core.Mesh.EdgeKey> Edges)> source)
    {
        if (cache.TryGetValue((id, edit), out var e) && e.Version == version) return (e.Vb, e.Count);
        if (e != null && edit && TryMovePoints(device, id, e, version)) return (e.Vb, e.Count);
        e?.Vb?.Dispose();
        var (verts, edges) = source();
        var data = new LineVertex[edges.Count * 2];
        var ends = edit ? new int[edges.Count * 2] : null;
        var white = Vector4.One;
        System.Threading.Tasks.Parallel.For(0, edges.Count, i =>
        {
            var k = edges[i];
            data[i * 2] = new LineVertex(verts[k.A], white); data[i * 2 + 1] = new LineVertex(verts[k.B], white);
            if (ends != null) { ends[i * 2] = k.A; ends[i * 2 + 1] = k.B; }
        });
        Buffer vb = data.Length > 0 ? Buffer.Create(device, BindFlags.VertexBuffer, data) : null;
        cache[(id, edit)] = new Entry { Version = version, Vb = vb, Count = data.Length, Cpu = edit ? data : null, EndVert = ends };
        return (vb, data.Length);
    }

    // ---- edit-mode drags: the edges stay, only the moved points are rewritten
    int hintObj = -1, hintFrom, hintTo; int[] hintVerts; IReadOnlyList<Vector3> hintPos;
    public void HintPointsMoved(int objId, int from, int to, int[] verts, IReadOnlyList<Vector3> positions) { hintObj = objId; hintFrom = from; hintTo = to; hintVerts = verts; hintPos = positions; }

    bool[] markBuf = Array.Empty<bool>();

    bool TryMovePoints(Device device, int id, Entry e, int version)
    {
        if (hintObj != id || hintTo != version || e.Version != hintFrom || e.Cpu == null || e.Vb == null || hintVerts == null) return false;
        int nv = hintPos.Count;
        if (e.Start == null || e.Start.Length != nv + 1)
        {
            var start = new int[nv + 1];
            foreach (var v in e.EndVert) { if (v >= nv) return false; start[v + 1]++; }
            for (int i = 0; i < nv; i++) start[i + 1] += start[i];
            var fill = (int[])start.Clone(); var idx = new int[start[nv]];
            for (int c = 0; c < e.EndVert.Length; c++) idx[fill[e.EndVert[c]]++] = c;
            e.Start = start; e.Idx = idx;
        }
        if (markBuf.Length < e.Cpu.Length) markBuf = new bool[e.Cpu.Length];
        int lo = int.MaxValue, hi = -1;
        foreach (var v in hintVerts)
        {
            if (v < 0 || v >= nv) continue;
            var p = hintPos[v];
            for (int k = e.Start[v]; k < e.Start[v + 1]; k++) { int c = e.Idx[k]; e.Cpu[c].Position = p; markBuf[c] = true; if (c < lo) lo = c; if (c > hi) hi = c; }
        }
        if (hi >= 0)
        {
            var runs = SceneRenderer.Runs(markBuf, lo, hi);
            int sz = Marshal.SizeOf<LineVertex>(); var ctx = device.ImmediateContext;
            unsafe
            {
                fixed (LineVertex* bp = e.Cpu)
                    foreach (var (a, b) in runs)
                        ctx.UpdateSubresource(new SharpDX.DataBox((IntPtr)(bp + a), 0, 0), e.Vb, 0, new ResourceRegion(a * sz, 0, 0, (b + 1) * sz, 1, 1));
            }
        }
        e.Version = version;
        return true;
    }

    /// <summary>Frees wires of objects that left the scene.</summary>
    public void Collect(ICollection<int> alive)
    {
        foreach (var k in cache.Keys.Where(k => !alive.Contains(k.Id)).ToList()) { cache[k].Vb?.Dispose(); cache.Remove(k); }
    }

    public void Dispose() { foreach (var e in cache.Values) e.Vb?.Dispose(); cache.Clear(); }
}
