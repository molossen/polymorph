using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>Undirected edge key, always stored with A &lt; B.</summary>
public readonly record struct EdgeKey(int A, int B)
{
    public static EdgeKey Make(int a, int b) => a < b ? new EdgeKey(a, b) : new EdgeKey(b, a);
    public bool Has(int v) => A == v || B == v;
    public int Other(int v) => v == A ? B : A;
}

/// <summary>
/// One polygon of an editable poly. Vertices are listed counter-clockwise seen from the
/// outside (right-handed, Z up, like 3ds Max and GTA). UVs and vertex colours are stored per
/// corner so seams and hard colour changes cost nothing.
/// </summary>
public sealed class Face
{
    public int[] V;
    public Vector2[] UV;
    /// <summary>GTA vertex colour 0 per corner (0..1). R = natural ambient, G = artificial ambient, B = tint, A = alpha.</summary>
    public Vector4[] Col;
    /// <summary>GTA vertex colour 1 per corner, or null. Terrain shaders (terrain_cb_w_4lyr) blend their 4 layers with it:
    /// blue picks layer 0/1 (and 2/3), green picks the pair 0-1 / 2-3.</summary>
    public Vector4[] Col1;
    /// <summary>Second texture coordinates (game terrain: where the blend mask is read). Null = none.</summary>
    public Vector2[] UV1;
    /// <summary>Material id (GtaMaterial.Id) of this polygon.</summary>
    public int MatId;
    /// <summary>3ds Max smoothing groups bitmask (bit 0 = group 1). 0 = flat.</summary>
    public uint Smooth;

    public int Count => V.Length;

    public Face(int[] v, int matId = 0, uint smooth = 0)
    {
        V = v;
        UV = new Vector2[v.Length];
        Col = new Vector4[v.Length];
        for (int i = 0; i < v.Length; i++) Col[i] = PolyMesh.DefaultColour;
        MatId = matId;
        Smooth = smooth;
    }

    Face() { }

    /// <summary>A face from ready arrays (no default fill: file loading builds millions of them).</summary>
    public static Face FromArrays(int[] v, Vector2[] uv, Vector4[] col, Vector4[] col1, int matId, uint smooth) => new() { V = v, UV = uv, Col = col, Col1 = col1, MatId = matId, Smooth = smooth };

    /// <summary>Copy of the corner arrays (3 allocations, no default fill: undo snapshots clone millions of faces).</summary>
    public Face Clone() => new() { V = (int[])V.Clone(), UV = (Vector2[])UV.Clone(), Col = (Vector4[])Col.Clone(), Col1 = (Vector4[])Col1?.Clone(), UV1 = (Vector2[])UV1?.Clone(), MatId = MatId, Smooth = Smooth };

    public int IndexOf(int vert) => Array.IndexOf(V, vert);

    /// <summary>Reverse the winding, keeping each corner's attributes with its vertex.</summary>
    public void Flip()
    {
        Array.Reverse(V);
        Array.Reverse(UV);
        Array.Reverse(Col);
        if (Col1 != null) Array.Reverse(Col1);
        if (UV1 != null) Array.Reverse(UV1);
    }

    /// <summary>Colour 1 of a corner (zero when the face has none: layer 0 of a terrain).</summary>
    public Vector4 Col1At(int i) => Col1 != null && i < Col1.Length ? Col1[i] : Vector4.Zero;
}

/// <summary>The editable poly mesh: shared vertex positions plus polygons.</summary>
public sealed class PolyMesh
{
    /// <summary>Green: the GTA convention for anything that is not emissive (artificial ambient only).</summary>
    public static readonly Vector4 DefaultColour = new(0f, 1f, 0f, 1f);

    public List<Vector3> Verts = new();
    public List<Face> Faces = new();

    public int AddVertex(Vector3 p)
    {
        Verts.Add(p);
        return Verts.Count - 1;
    }

    public Face AddFace(int[] v, int matId = 0, uint smooth = 0)
    {
        var f = new Face(v, matId, smooth);
        Faces.Add(f);
        return f;
    }

    public PolyMesh Clone()
    {
        var m = new PolyMesh { Verts = new List<Vector3>(Verts) };
        int n = Faces.Count;
        if (n < 20000)
        {
            m.Faces = new List<Face>(n);
            foreach (var f in Faces) m.Faces.Add(f.Clone());
            return m;
        }
        // big meshes: faces copied on all cores
        var src = Faces; var arr = new Face[n];
        System.Threading.Tasks.Parallel.For(0, (n + 8191) / 8192, c =>
        {
            int end = Math.Min(n, (c + 1) * 8192);
            for (int i = c * 8192; i < end; i++) arr[i] = src[i].Clone();
        });
        m.Faces = new List<Face>(arr);
        return m;
    }

    public Vector3 FaceNormal(int f) => FaceNormal(Faces[f]);

    public Vector3 FaceNormal(Face f)
    {
        // Newell's method: robust for n-gons and slightly non-planar polygons.
        float x = 0, y = 0, z = 0;
        int n = f.V.Length;
        for (int i = 0; i < n; i++)
        {
            var a = Verts[f.V[i]];
            var b = Verts[f.V[(i + 1) % n]];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        var v = new Vector3(x, y, z);
        float l = v.Length();
        return l > 1e-12f ? v / l : Vector3.UnitZ;
    }

    /// <summary>Twice the polygon area vector length (Newell, unnormalised) halved: the area.</summary>
    public float FaceArea(Face f)
    {
        Vector3 s = Vector3.Zero;
        int n = f.V.Length;
        for (int i = 0; i < n; i++) s += Vector3.Cross(Verts[f.V[i]], Verts[f.V[(i + 1) % n]]);
        return s.Length() * 0.5f;
    }

    public Vector3 FaceCenter(int f) => FaceCenter(Faces[f]);

    public Vector3 FaceCenter(Face f)
    {
        Vector3 c = Vector3.Zero;
        foreach (var i in f.V) c += Verts[i];
        return c / f.V.Length;
    }

    public IEnumerable<EdgeKey> FaceEdges(Face f)
    {
        for (int i = 0; i < f.V.Length; i++) yield return EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]);
    }

    /// <summary>Edge → list of faces using it.</summary>
    public Dictionary<EdgeKey, List<int>> BuildEdgeMap()
    {
        var map = new Dictionary<EdgeKey, List<int>>();
        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var f = Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                var k = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]);
                if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>(2);
                l.Add(fi);
            }
        }
        return map;
    }

    public List<EdgeKey> AllEdges() => BuildEdgeMap().Keys.ToList();

    /// <summary>
    /// Edges for drawing, fast on big meshes: each edge once on well-wound meshes (an edge whose two faces
    /// wind it the same way can appear twice, which only matters for counting, not for drawing).
    /// </summary>
    public EdgeKey[] DrawEdges()
    {
        var (start, idx) = BuildVertexFacesPacked();
        var parts = new System.Collections.Concurrent.ConcurrentBag<List<EdgeKey>>();
        System.Threading.Tasks.Parallel.For(0, Faces.Count, () => new List<EdgeKey>(), (fi, st, acc) =>
        {
            var v = Faces[fi].V;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (a == b) continue;
                if (a < b) { acc.Add(new EdgeKey(a, b)); continue; }
                // a > b: keep only if no face walks it the other way (b -> a), i.e. a border or flipped neighbour
                bool twin = false;
                for (int k = start[b]; k < start[b + 1] && !twin; k++)
                {
                    var g = Faces[idx[k]].V;
                    for (int q = 0; q < g.Length; q++) if (g[q] == b && g[(q + 1) % g.Length] == a) { twin = true; break; }
                }
                if (!twin) acc.Add(new EdgeKey(b, a));
            }
            return acc;
        }, acc => parts.Add(acc));
        var res = new EdgeKey[parts.Sum(p => p.Count)]; int o = 0;
        foreach (var p in parts) { p.CopyTo(res, o); o += p.Count; }
        return res;
    }

    /// <summary>Vertex → faces using it, packed (CSR): faces of v are Idx[Start[v] .. Start[v+1]).</summary>
    public (int[] Start, int[] Idx) BuildVertexFacesPacked()
    {
        var start = new int[Verts.Count + 1];
        foreach (var f in Faces) foreach (var v in f.V) start[v + 1]++;
        for (int i = 0; i < Verts.Count; i++) start[i + 1] += start[i];
        var idx = new int[start[Verts.Count]];
        var fill = (int[])start.Clone();
        for (int fi = 0; fi < Faces.Count; fi++) foreach (var v in Faces[fi].V) idx[fill[v]++] = fi;
        return (start, idx);
    }

    /// <summary>Vertex → faces using it.</summary>
    public List<int>[] BuildVertexFaces()
    {
        var vf = new List<int>[Verts.Count];
        for (int i = 0; i < vf.Length; i++) vf[i] = new List<int>(4);
        for (int fi = 0; fi < Faces.Count; fi++)
            foreach (var v in Faces[fi].V) vf[v].Add(fi);
        return vf;
    }

    /// <summary>Removes vertices no face uses. Returns old→new index map (-1 = removed).</summary>
    public int[] RemoveUnusedVertices()
    {
        var used = new bool[Verts.Count];
        foreach (var f in Faces) foreach (var v in f.V) used[v] = true;
        var map = new int[Verts.Count];
        var nv = new List<Vector3>(Verts.Count);
        for (int i = 0; i < Verts.Count; i++)
        {
            if (used[i]) { map[i] = nv.Count; nv.Add(Verts[i]); }
            else map[i] = -1;
        }
        Verts = nv;
        var done = new HashSet<Face>(ReferenceEqualityComparer.Instance);
        foreach (var f in Faces) { if (!done.Add(f)) continue; for (int i = 0; i < f.V.Length; i++) f.V[i] = map[f.V[i]]; }
        return map;
    }

    /// <summary>Drops consecutive duplicate indices and faces with fewer than 3 distinct vertices.</summary>
    public void CleanFaces()
    {
        var keep = new List<Face>(Faces.Count);
        foreach (var f in Faces)
        {
            // fast path: triangles and quads without repeated corners (the usual case) stay as they are
            var fv = f.V;
            if (fv.Length == 3 && fv[0] != fv[1] && fv[1] != fv[2] && fv[0] != fv[2]) { keep.Add(f); continue; }
            if (fv.Length == 4 && fv[0] != fv[1] && fv[0] != fv[2] && fv[0] != fv[3] && fv[1] != fv[2] && fv[1] != fv[3] && fv[2] != fv[3]) { keep.Add(f); continue; }
            var v = new List<int>(f.V.Length); var uv = new List<Vector2>(); var col = new List<Vector4>();
            for (int i = 0; i < f.V.Length; i++)
            {
                if (v.Count > 0 && v[^1] == f.V[i]) continue;
                v.Add(f.V[i]); uv.Add(f.UV[i]); col.Add(f.Col[i]);
            }
            while (v.Count > 1 && v[0] == v[^1]) { v.RemoveAt(v.Count - 1); uv.RemoveAt(uv.Count - 1); col.RemoveAt(col.Count - 1); }
            if (v.Distinct().Count() < 3) continue;
            // a corner met twice (a weld pinched the polygon): split it into its loops, each a valid polygon
            bool usedOriginal = false;
            foreach (var (lv, luv, lcol) in SplitPinched(v, uv, col))
            {
                if (lv.Count < 3) continue;
                // the first loop reuses the face object, every other loop gets its own (a face object listed twice
                // would be renumbered twice later)
                var nf = usedOriginal ? f.Clone() : f; usedOriginal = true;
                nf.V = lv.ToArray(); nf.UV = luv.ToArray(); nf.Col = lcol.ToArray(); nf.Col1 = null; nf.UV1 = null;
                keep.Add(nf);
            }
        }
        Faces = keep;
    }

    static IEnumerable<(List<int>, List<Vector2>, List<Vector4>)> SplitPinched(List<int> v, List<Vector2> uv, List<Vector4> col)
    {
        for (int i = 0; i < v.Count; i++)
            for (int j = i + 1; j < v.Count; j++)
                if (v[i] == v[j])
                {
                    // loop i..j-1 and the rest (j..end + 0..i-1)
                    var a = (v.GetRange(i, j - i), uv.GetRange(i, j - i), col.GetRange(i, j - i));
                    var bv = v.Skip(j).Concat(v.Take(i)).ToList(); var buv = uv.Skip(j).Concat(uv.Take(i)).ToList(); var bc = col.Skip(j).Concat(col.Take(i)).ToList();
                    foreach (var x in SplitPinched(a.Item1, a.Item2, a.Item3)) yield return x;
                    foreach (var x in SplitPinched(bv, buv, bc)) yield return x;
                    yield break;
                }
        yield return (v, uv, col);
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Verts.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in Verts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        return (mn, mx);
    }

    public int TriangleCount
    {
        get { int n = 0; foreach (var f in Faces) n += f.V.Length - 2; return n; }
    }

    /// <summary>
    /// True when every edge is used by exactly two faces in opposite directions (a closed,
    /// consistently oriented solid). That is what GTA MLO rooms and collision want.
    /// </summary>
    public bool IsClosedManifold(out string problem)
    {
        var dir = new Dictionary<(int, int), int>();
        foreach (var f in Faces)
            for (int i = 0; i < f.V.Length; i++)
            {
                var k = (f.V[i], f.V[(i + 1) % f.V.Length]);
                dir[k] = dir.TryGetValue(k, out var c) ? c + 1 : 1;
            }
        foreach (var kv in dir)
        {
            if (kv.Value > 1) { problem = $"edge {kv.Key} used twice in the same direction"; return false; }
            if (!dir.ContainsKey((kv.Key.Item2, kv.Key.Item1))) { problem = $"open edge {kv.Key}"; return false; }
        }
        problem = null;
        return true;
    }

    /// <summary>Throws if any face references a missing vertex or is degenerate.</summary>
    public void Validate()
    {
        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var f = Faces[fi];
            if (f.V.Length < 3) throw new InvalidOperationException($"face {fi} has {f.V.Length} vertices");
            if (f.UV.Length != f.V.Length || f.Col.Length != f.V.Length) throw new InvalidOperationException($"face {fi} attribute count mismatch");
            if (f.Col1 != null && f.Col1.Length != f.V.Length) f.Col1 = null; // an edit that did not carry colour 1: back to layer 0
            if (f.UV1 != null && f.UV1.Length != f.V.Length) f.UV1 = null;
            foreach (var v in f.V) if (v < 0 || v >= Verts.Count) throw new InvalidOperationException($"face {fi} references vertex {v}");
            if (RepeatsVertex(f.V)) throw new InvalidOperationException($"face {fi} repeats a vertex");
        }
    }

    /// <summary>True when a polygon lists the same point twice (no allocation for usual polygons).</summary>
    static bool RepeatsVertex(int[] v)
    {
        int n = v.Length;
        if (n <= 16)
        {
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) if (v[i] == v[j]) return true;
            return false;
        }
        return new HashSet<int>(v).Count != n;
    }

    /// <summary>Appends another mesh (already in this mesh's space). Returns the index of its first face.</summary>
    public int Append(PolyMesh other)
    {
        int vo = Verts.Count, fo = Faces.Count;
        Verts.AddRange(other.Verts);
        foreach (var f in other.Faces)
        {
            var c = f.Clone();
            for (int i = 0; i < c.V.Length; i++) c.V[i] += vo;
            Faces.Add(c);
        }
        return fo;
    }

    public void Transform(Matrix4x4 m)
    {
        for (int i = 0; i < Verts.Count; i++) Verts[i] = Vector3.Transform(Verts[i], m);
        // A mirroring transform flips orientation: keep faces outward.
        if (m.GetDeterminant() < 0) foreach (var f in Faces) f.Flip();
    }

    /// <summary>Sets every corner of every face to one material / colour.</summary>
    public void SetAllMaterial(int matId) { foreach (var f in Faces) f.MatId = matId; }

    public void SetAllColour(Vector4 c) { foreach (var f in Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = c; }
}
