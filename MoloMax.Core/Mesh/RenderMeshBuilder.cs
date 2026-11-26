using System.Numerics;
using System.Runtime.InteropServices;

namespace MoloMax.Core.Mesh;

/// <summary>
/// GPU vertex in the GTA "PNCCTX" spirit: position, normal, colour 0, texcoord 0, tangent (w = handedness), colour 1 (RGBA8,
/// the terrain layer blend). 68 bytes, sequential, matches the input layout of gta_basic.hlsl.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RenderVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Colour;
    public Vector2 UV;
    public Vector4 Tangent;
    public uint Colour1;
    public Vector2 UV1;
    public const int SizeInBytes = 76;
    public static uint Pack(Vector4 c) => (uint)(B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24);
    public static Vector4 Unpack(uint c) => new((c & 255) / 255f, (c >> 8 & 255) / 255f, (c >> 16 & 255) / 255f, (c >> 24) / 255f);
    static int B(float f) => (int)Math.Clamp(f * 255f + 0.5f, 0, 255);
}

/// <summary>Triangles of one material, ready to upload.</summary>
public sealed class RenderGroup
{
    public int MaterialId;
    // the lists are made from the arrays only when something asks for them (the viewport upload reads the arrays: a
    // 500k-polygon mesh no longer copies 150 MB into lists it never reads)
    List<RenderVertex> vl; List<int> il, tl;
    public List<RenderVertex> Vertices { get => vl ??= VertexArray != null ? new(VertexArray) : new(); set => vl = value; }
    public List<int> Indices { get => il ??= IndexArray != null ? new(IndexArray) : new(); set => il = value; }
    /// <summary>Source polygon of each triangle (Indices.Count / 3 entries) — used for picking.</summary>
    public List<int> TriangleFace { get => tl ??= TriangleFaceArray != null ? new(TriangleFaceArray) : new(); set => tl = value; }
    /// <summary>Same data as arrays (filled by the builder; avoids a copy when uploading).</summary>
    public RenderVertex[] VertexArray; public int[] IndexArray; public int[] TriangleFaceArray;
    /// <summary>Mesh vertex of each render vertex (builder output): moving points only rewrites their positions.</summary>
    public int[] CornerVert;
    /// <summary>Source polygon of each render vertex (builder output).</summary>
    public int[] CornerFace;
    /// <summary>Triangle count without building the lists.</summary>
    public int IndexCount => IndexArray?.Length ?? il?.Count ?? 0;
}

public static class RenderMeshBuilder
{
    /// <summary>Per-corner normals using 3ds Max smoothing groups (0 = faceted).</summary>
    public static Vector3[][] CornerNormals(PolyMesh m)
    {
        int fc = m.Faces.Count;
        var fn = new Vector3[fc];
        var fw = new Vector3[fc];
        System.Threading.Tasks.Parallel.For(0, fc, i =>
        {
            fn[i] = m.FaceNormal(i);
            fw[i] = fn[i] * MathF.Max(m.FaceArea(m.Faces[i]), 1e-6f);
        });
        var (start, idx) = m.BuildVertexFacesPacked();
        var res = new Vector3[fc][];
        System.Threading.Tasks.Parallel.For(0, fc, fi =>
        {
            var f = m.Faces[fi];
            var arr = new Vector3[f.V.Length];
            for (int c = 0; c < f.V.Length; c++)
            {
                if (f.Smooth == 0) { arr[c] = fn[fi]; continue; }
                Vector3 s = Vector3.Zero;
                int v = f.V[c];
                for (int k = start[v]; k < start[v + 1]; k++)
                {
                    int g = idx[k];
                    if ((m.Faces[g].Smooth & f.Smooth) != 0) s += fw[g];
                }
                float l = s.Length();
                arr[c] = l > 1e-12f ? s / l : fn[fi];
            }
            res[fi] = arr;
        });
        return res;
    }

    /// <summary>Corner normals in one flat array: corner c of face f is at off[f] + c.</summary>
    static Vector3[] CornerNormalsFlat(PolyMesh m, int[] off)
    {
        int fc = m.Faces.Count;
        var fn = new Vector3[fc]; var fw = new Vector3[fc];
        System.Threading.Tasks.Parallel.For(0, fc, i =>
        {
            var f = m.Faces[i];
            fn[i] = m.FaceNormal(f);
            fw[i] = fn[i] * MathF.Max(m.FaceArea(f), 1e-6f);
        });
        var (start, idx) = m.BuildVertexFacesPacked();
        var res = new Vector3[off[fc]];
        System.Threading.Tasks.Parallel.For(0, fc, fi =>
        {
            var f = m.Faces[fi]; int o = off[fi];
            for (int c = 0; c < f.V.Length; c++)
            {
                if (f.Smooth == 0) { res[o + c] = fn[fi]; continue; }
                Vector3 s = Vector3.Zero; int v = f.V[c];
                for (int k = start[v]; k < start[v + 1]; k++) { int g = idx[k]; if ((m.Faces[g].Smooth & f.Smooth) != 0) s += fw[g]; }
                float l = s.Length();
                res[o + c] = l > 1e-12f ? s / l : fn[fi];
            }
        });
        return res;
    }

    /// <summary>Convex-enough quad: which diagonal keeps both triangles facing the polygon's way (same rule as the triangulator).</summary>
    static bool QuadSplit(PolyMesh m, Face f, out bool alt)
    {
        var p0 = m.Verts[f.V[0]]; var p1 = m.Verts[f.V[1]]; var p2 = m.Verts[f.V[2]]; var p3 = m.Verts[f.V[3]];
        var nrm = m.FaceNormal(f);
        alt = false;
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), nrm) > 0 && Vector3.Dot(Vector3.Cross(p2 - p0, p3 - p0), nrm) > 0) return true;
        alt = true;
        return Vector3.Dot(Vector3.Cross(p1 - p0, p3 - p0), nrm) > 0 && Vector3.Dot(Vector3.Cross(p2 - p1, p3 - p1), nrm) > 0;
    }

    /// <summary>Triangulates the mesh into one group per material id (parallel, no per-face allocation for tris / quads).</summary>
    public static List<RenderGroup> Build(PolyMesh m)
    {
        int fc = m.Faces.Count;
        var cOff = new int[fc + 1];
        for (int i = 0; i < fc; i++) cOff[i + 1] = cOff[i] + m.Faces[i].V.Length;
        var normals = CornerNormalsFlat(m, cOff);
        // offsets inside each material group (a polygon of n corners always gives n - 2 triangles)
        var groups = new Dictionary<int, (RenderGroup G, int V, int T)>();
        var vOff = new int[fc]; var tOff = new int[fc]; var gOf = new RenderGroup[fc];
        for (int fi = 0; fi < fc; fi++)
        {
            var f = m.Faces[fi];
            if (!groups.TryGetValue(f.MatId, out var e)) e = (new RenderGroup { MaterialId = f.MatId }, 0, 0);
            vOff[fi] = e.V; tOff[fi] = e.T; gOf[fi] = e.G;
            groups[f.MatId] = (e.G, e.V + f.V.Length, e.T + Math.Max(0, f.V.Length - 2));
        }
        var verts = new Dictionary<RenderGroup, RenderVertex[]>(); var idx = new Dictionary<RenderGroup, int[]>(); var tf = new Dictionary<RenderGroup, int[]>();
        foreach (var e in groups.Values) { verts[e.G] = new RenderVertex[e.V]; idx[e.G] = new int[e.T * 3]; tf[e.G] = new int[e.T]; e.G.CornerVert = new int[e.V]; e.G.CornerFace = new int[e.V]; }
        System.Threading.Tasks.Parallel.For(0, fc, () => new List<(int, int, int)>(8), (fi, st, tr) =>
        {
            var f = m.Faces[fi]; var g = gOf[fi]; int n = f.V.Length;
            if (n < 3) return tr;
            tr.Clear();
            if (n == 3) tr.Add((0, 1, 2));
            else if (n == 4 && QuadSplit(m, f, out bool alt)) { if (!alt) { tr.Add((0, 1, 2)); tr.Add((0, 2, 3)); } else { tr.Add((0, 1, 3)); tr.Add((1, 2, 3)); } }
            else tr.AddRange(Triangulator.Triangulate(m, f));
            while (tr.Count < n - 2) tr.Add((0, 0, 0)); // degenerate filler keeps the buffer layout
            var vArr = verts[g]; var iArr = idx[g]; var tArr = tf[g];
            Vector3 tan = Vector3.Zero, bit = Vector3.Zero;
            for (int q = 0; q < n - 2; q++)
            {
                var (a, b, c) = tr[q];
                var p0 = m.Verts[f.V[a]]; var p1 = m.Verts[f.V[b]]; var p2 = m.Verts[f.V[c]];
                var t0 = f.UV[a]; var t1 = f.UV[b]; var t2 = f.UV[c];
                var e1 = p1 - p0; var e2 = p2 - p0;
                var d1 = t1 - t0; var d2 = t2 - t0;
                float r = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(r) < 1e-12f) continue;
                r = 1f / r;
                tan += (e1 * d2.Y - e2 * d1.Y) * r;
                bit += (e2 * d1.X - e1 * d2.X) * r;
            }
            int baseIndex = vOff[fi], co = cOff[fi];
            for (int c = 0; c < n; c++)
            {
                var nr = normals[co + c];
                var t = tan - nr * Vector3.Dot(nr, tan);
                if (t.LengthSquared() < 1e-12f) { Triangulator.Basis(nr, out var bu, out _); t = bu; }
                t = Vector3.Normalize(t);
                float w = Vector3.Dot(Vector3.Cross(nr, t), bit) < 0 ? -1f : 1f;
                g.CornerVert[baseIndex + c] = f.V[c]; g.CornerFace[baseIndex + c] = fi;
                vArr[baseIndex + c] = new RenderVertex { Position = m.Verts[f.V[c]], Normal = nr, Colour = f.Col[c], UV = f.UV[c], Tangent = new Vector4(t, w), Colour1 = f.Col1 != null && c < f.Col1.Length ? RenderVertex.Pack(f.Col1[c]) : 0u, UV1 = f.UV1 != null && c < f.UV1.Length ? f.UV1[c] : f.UV[c] };
            }
            int ti = tOff[fi];
            for (int q = 0; q < n - 2; q++)
            {
                var (a, b, c) = tr[q];
                iArr[ti * 3] = baseIndex + a; iArr[ti * 3 + 1] = baseIndex + b; iArr[ti * 3 + 2] = baseIndex + c;
                tArr[ti] = fi; ti++;
            }
            return tr;
        }, _ => { });
        var res = new List<RenderGroup>();
        foreach (var e in groups.Values.OrderBy(x => x.G.MaterialId))
        {
            e.G.VertexArray = verts[e.G]; e.G.IndexArray = idx[e.G]; e.G.TriangleFaceArray = tf[e.G];
            res.Add(e.G);
        }
        return res;
    }
}
