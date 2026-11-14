using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>Editable Poly operations. All of them keep faces wound outward and attributes per corner.</summary>
public static partial class MeshOps
{
    /// <summary>long keys (packed edges / grid cells) hash badly by default (hi ^ lo): mix the bits.</summary>
    internal sealed class LongKey : IEqualityComparer<long>
    {
        public static readonly LongKey I = new();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long x) { ulong h = (ulong)x * 0x9E3779B97F4A7C15UL; return (int)(h >> 32) ^ (int)h; }
    }

    // ---------------------------------------------------------------- welding

    /// <summary>
    /// Merges vertices closer than <paramref name="tol"/>. When <paramref name="only"/> is given,
    /// only those vertices are candidates (3ds Max "Weld selected"). Clusters land on their average.
    /// Returns how many vertices were removed.
    /// </summary>
    public static int WeldByDistance(PolyMesh m, float tol, ISet<int> only = null)
    {
        int before = m.Verts.Count, n = before;
        tol = MathF.Max(tol, 1e-7f);
        // cells much bigger than the tolerance: neighbours are only looked at when a point sits within tol of a cell border
        float cell = tol * 64f, tol2 = tol * tol;
        var head = new Dictionary<long, int>(Math.Min(n, 1 << 20), LongKey.I);
        var next = new int[n];
        var rep = new int[n];
        for (int i = 0; i < n; i++) rep[i] = i;
        static long Pack(long x, long y, long z) => ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        var verts = m.Verts;
        for (int i = 0; i < n; i++)
        {
            if (only != null && !only.Contains(i)) continue;
            var p = verts[i];
            float fx = p.X / cell, fy = p.Y / cell, fz = p.Z / cell;
            long kx = (long)MathF.Floor(fx), ky = (long)MathF.Floor(fy), kz = (long)MathF.Floor(fz);
            float t = tol / cell;
            int x0 = fx - kx < t ? -1 : 0, x1 = kx + 1 - fx < t ? 1 : 0;
            int y0 = fy - ky < t ? -1 : 0, y1 = ky + 1 - fy < t ? 1 : 0;
            int z0 = fz - kz < t ? -1 : 0, z1 = kz + 1 - fz < t ? 1 : 0;
            int found = -1;
            for (long dx = x0; dx <= x1 && found < 0; dx++)
                for (long dy = y0; dy <= y1 && found < 0; dy++)
                    for (long dz = z0; dz <= z1 && found < 0; dz++)
                        if (head.TryGetValue(Pack(kx + dx, ky + dy, kz + dz), out var j))
                            for (; j >= 0; j = next[j])
                                if (Vector3.DistanceSquared(verts[j], p) <= tol2) { found = j; break; }
            if (found >= 0) { rep[i] = found; continue; }
            long key = Pack(kx, ky, kz);
            next[i] = head.TryGetValue(key, out var h) ? h : -1;
            head[key] = i;
        }
        // average each cluster onto its representative
        var sum = new Vector3[n]; var cnt = new int[n];
        bool any = false;
        for (int i = 0; i < n; i++) if (rep[i] != i) { any = true; int r = rep[i]; if (cnt[r] == 0) { sum[r] = verts[r]; cnt[r] = 1; } sum[r] += verts[i]; cnt[r]++; }
        if (!any) return 0;
        for (int i = 0; i < n; i++) if (cnt[i] > 1) verts[i] = sum[i] / cnt[i];

        foreach (var f in m.Faces) for (int i = 0; i < f.V.Length; i++) f.V[i] = rep[f.V[i]];
        m.CleanFaces();
        m.RemoveUnusedVertices();
        return before - m.Verts.Count;
    }

    /// <summary>
    /// Splits a mesh into pieces of at most <paramref name="budget"/> vertices (keep some margin for UV / smoothing seams),
    /// cutting along the longest axis of the bounding box again and again, like splitting an exterior into chunks.
    /// </summary>
    public static List<PolyMesh> SplitByVertexBudget(PolyMesh m, int budget)
    {
        var res = new List<PolyMesh>();
        void Rec(List<int> faces)
        {
            var uniq = new HashSet<int>(); foreach (var f in faces) foreach (var v in m.Faces[f].V) uniq.Add(v);
            if (uniq.Count <= budget || faces.Count < 2) { res.Add(Extract(m, faces)); return; }
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            var centres = new Dictionary<int, Vector3>(faces.Count);
            foreach (var f in faces) { var c = m.FaceCenter(f); centres[f] = c; mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c); }
            var size = mx - mn;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float Comp(Vector3 v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            faces.Sort((a, b) => Comp(centres[a]).CompareTo(Comp(centres[b])));
            int half = faces.Count / 2;
            Rec(faces.GetRange(0, half)); Rec(faces.GetRange(half, faces.Count - half));
        }
        Rec(Enumerable.Range(0, m.Faces.Count).ToList());
        return res;
    }

    /// <summary>New mesh with only these faces (vertices re-indexed, attributes kept).</summary>
    public static PolyMesh Extract(PolyMesh m, IEnumerable<int> faces)
    {
        var map = new Dictionary<int, int>();
        var r = new PolyMesh();
        foreach (var fi in faces)
        {
            var f = m.Faces[fi].Clone();
            for (int i = 0; i < f.V.Length; i++)
            {
                if (!map.TryGetValue(f.V[i], out var nv)) { nv = r.Verts.Count; r.Verts.Add(m.Verts[f.V[i]]); map[f.V[i]] = nv; }
                f.V[i] = nv;
            }
            r.Faces.Add(f);
        }
        return r;
    }

    /// <summary>Collapses each connected group of selected vertices into one vertex at its centre.</summary>
    public static void Collapse(PolyMesh m, ISet<int> verts)
    {
        if (verts == null || verts.Count < 2) return;
        var parent = new Dictionary<int, int>();
        foreach (var v in verts) parent[v] = v;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in m.BuildEdgeMap().Keys)
            if (verts.Contains(e.A) && verts.Contains(e.B)) parent[Find(e.A)] = Find(e.B);
        var groups = verts.GroupBy(Find);
        var rep = Enumerable.Range(0, m.Verts.Count).ToArray();
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count < 2) continue;
            var c = Vector3.Zero; foreach (var v in list) c += m.Verts[v];
            m.Verts[list[0]] = c / list.Count;
            foreach (var v in list) rep[v] = list[0];
        }
        foreach (var f in m.Faces) for (int i = 0; i < f.V.Length; i++) f.V[i] = rep[f.V[i]];
        m.CleanFaces();
        m.RemoveUnusedVertices();
    }

    /// <summary>3ds Max Target Weld: vertex <paramref name="src"/> is merged into <paramref name="dst"/> (which keeps its
    /// place). Faces that end up with fewer than 3 corners go away. Returns the index of the merged vertex afterwards, or -1.</summary>
    public static int TargetWeld(PolyMesh m, int src, int dst)
    {
        if (src == dst || src < 0 || dst < 0 || src >= m.Verts.Count || dst >= m.Verts.Count) return -1;
        var keepPos = m.Verts[dst];
        foreach (var f in m.Faces) for (int i = 0; i < f.V.Length; i++) if (f.V[i] == src) f.V[i] = dst;
        m.CleanFaces();
        // the merged vertex is found again by its position (indices shift when unused vertices go)
        m.RemoveUnusedVertices();
        for (int i = 0; i < m.Verts.Count; i++) if (m.Verts[i] == keepPos) return i;
        return -1;
    }

    // ---------------------------------------------------------------- deleting / detaching

    public static void DeleteFaces(PolyMesh m, ISet<int> faces, bool removeIsolatedVerts = true)
    {
        if (faces == null || faces.Count == 0) return;
        var keep = new List<Face>(m.Faces.Count);
        for (int i = 0; i < m.Faces.Count; i++) if (!faces.Contains(i)) keep.Add(m.Faces[i]);
        m.Faces = keep;
        if (removeIsolatedVerts) m.RemoveUnusedVertices();
    }

    public static void DeleteVertices(PolyMesh m, ISet<int> verts)
    {
        if (verts == null || verts.Count == 0) return;
        var faces = new HashSet<int>();
        for (int i = 0; i < m.Faces.Count; i++) if (m.Faces[i].V.Any(verts.Contains)) faces.Add(i);
        DeleteFaces(m, faces, false);
        m.RemoveUnusedVertices();
    }

    public static void FlipFaces(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (var f in faces) m.Faces[f].Flip();
    }

    public static void FlipAll(PolyMesh m) { foreach (var f in m.Faces) f.Flip(); }

    /// <summary>Moves the faces into a new mesh (3ds Max Detach). The source keeps the rest.</summary>
    public static PolyMesh Detach(PolyMesh m, ISet<int> faces)
    {
        var res = new PolyMesh();
        var map = new Dictionary<int, int>();
        foreach (var fi in faces.OrderBy(x => x))
        {
            var c = m.Faces[fi].Clone();
            for (int i = 0; i < c.V.Length; i++)
            {
                if (!map.TryGetValue(c.V[i], out var nv)) { nv = res.AddVertex(m.Verts[c.V[i]]); map[c.V[i]] = nv; }
                c.V[i] = nv;
            }
            res.Faces.Add(c);
        }
        DeleteFaces(m, faces);
        return res;
    }

    // ---------------------------------------------------------------- attributes

    public static void SetMaterial(PolyMesh m, IEnumerable<int> faces, int matId)
    {
        foreach (var f in faces) m.Faces[f].MatId = matId;
    }

    /// <summary>Paints the corners of the given faces.</summary>
    public static void SetFaceColour(PolyMesh m, IEnumerable<int> faces, Vector4 c)
    {
        foreach (var fi in faces) { var f = m.Faces[fi]; for (int i = 0; i < f.Col.Length; i++) f.Col[i] = c; }
    }

    /// <summary>Paints every corner that uses one of the vertices.</summary>
    public static void SetVertexColour(PolyMesh m, ISet<int> verts, Vector4 c)
    {
        foreach (var f in m.Faces) for (int i = 0; i < f.V.Length; i++) if (verts.Contains(f.V[i])) f.Col[i] = c;
    }

    public static void SetSmoothing(PolyMesh m, IEnumerable<int> faces, uint bits, bool on)
    {
        foreach (var fi in faces) { var f = m.Faces[fi]; f.Smooth = on ? f.Smooth | bits : f.Smooth & ~bits; }
    }

    public static void ClearSmoothing(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (var fi in faces) m.Faces[fi].Smooth = 0;
    }

    /// <summary>
    /// 3ds Max Auto Smooth: faces meeting at less than <paramref name="angleDeg"/> share a group,
    /// sharper edges stay hard. Groups are coloured greedily so neighbours across a hard edge never share a bit.
    /// </summary>
    public static void AutoSmooth(PolyMesh m, ISet<int> faces, float angleDeg)
    {
        int fc = m.Faces.Count;
        var sel = new bool[fc];
        if (faces == null) Array.Fill(sel, true); else foreach (var f in faces) if (f >= 0 && f < fc) sel[f] = true;
        float cosT = MathF.Cos(angleDeg * MathF.PI / 180f);
        var normals = new Vector3[fc];
        System.Threading.Tasks.Parallel.For(0, fc, i => normals[i] = m.FaceNormal(m.Faces[i]));
        // pair faces across edges through the vertex -> faces table (each vertex touches only a few faces)
        var (vs, vi) = m.BuildVertexFacesPacked();
        var softB = new System.Collections.Concurrent.ConcurrentBag<List<(int, int)>>(); var hardB = new System.Collections.Concurrent.ConcurrentBag<List<(int, int)>>();
        System.Threading.Tasks.Parallel.For(0, fc, () => (new List<(int, int)>(), new List<(int, int)>()), (fi, _, acc) =>
        {
            if (!sel[fi]) return acc;
            var v = m.Faces[fi].V;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                int other = -1, count = 1;
                for (int k = vs[a]; k < vs[a + 1]; k++)
                {
                    int g = vi[k]; if (g == fi) continue;
                    var gv = m.Faces[g].V; bool hasB = false;
                    for (int q = 0; q < gv.Length; q++) if (gv[q] == b) { hasB = true; break; }
                    if (hasB) { count++; other = g; }
                }
                if (count != 2 || other < fi || !sel[other]) continue; // each pair once, manifold edges only
                if (Vector3.Dot(normals[fi], normals[other]) >= cosT) acc.Item1.Add((fi, other)); else acc.Item2.Add((fi, other));
            }
            return acc;
        }, acc => { softB.Add(acc.Item1); hardB.Add(acc.Item2); });
        var soft = softB.SelectMany(x => x).ToList(); var hard = hardB.SelectMany(x => x).ToList();
        // regions = connected components over soft edges (union-find)
        var parent = new int[fc];
        for (int i = 0; i < fc; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (a, b) in soft) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
        var regionOf = new int[fc]; var regionIndex = new Dictionary<int, int>();
        for (int f = 0; f < fc; f++)
        {
            if (!sel[f]) continue;
            int r = Find(f);
            if (!regionIndex.TryGetValue(r, out var ri)) regionIndex[r] = ri = regionIndex.Count;
            regionOf[f] = ri;
        }
        int rc = regionIndex.Count;
        var nb = new List<int>[rc];
        foreach (var (a, b) in hard)
        {
            int ra = regionOf[a], rb = regionOf[b];
            if (ra == rb) continue;
            (nb[ra] ??= new List<int>(4)).Add(rb);
            (nb[rb] ??= new List<int>(4)).Add(ra);
        }
        var regionBit = new uint[rc];
        for (int r = 0; r < rc; r++)
        {
            uint used = 0;
            if (nb[r] != null) foreach (var x in nb[r]) used |= regionBit[x];
            int bit = 0; while (bit < 31 && (used & (1u << bit)) != 0) bit++;
            regionBit[r] = 1u << bit;
        }
        for (int f = 0; f < fc; f++) if (sel[f]) m.Faces[f].Smooth = regionBit[regionOf[f]];
    }

    /// <summary>
    /// Box mapping at real-world scale: each face is projected from its dominant axis,
    /// <paramref name="tile"/> metres per texture repeat (what GTA walls want).
    /// </summary>
    public static void BoxMap(PolyMesh m, ISet<int> faces, float tile, Matrix4x4? space = null)
    {
        tile = MathF.Max(tile, 1e-4f);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (faces != null && !faces.Contains(fi)) continue;
            var f = m.Faces[fi];
            var n = m.FaceNormal(f);
            if (space.HasValue) n = Vector3.Normalize(Vector3.TransformNormal(n, space.Value));
            var an = Vector3.Abs(n);
            for (int i = 0; i < f.V.Length; i++)
            {
                var p = m.Verts[f.V[i]];
                if (space.HasValue) p = Vector3.Transform(p, space.Value);
                Vector2 uv;
                if (an.Z >= an.X && an.Z >= an.Y) uv = new Vector2(n.Z >= 0 ? p.X : -p.X, -p.Y);
                else if (an.X >= an.Y) uv = new Vector2(n.X >= 0 ? p.Y : -p.Y, -p.Z);
                else uv = new Vector2(n.Y >= 0 ? -p.X : p.X, -p.Z);
                f.UV[i] = uv / tile;
            }
        }
    }

    /// <summary>Planar projection along a direction (3ds Max UVW Map planar), size in metres.</summary>
    public static void PlanarMap(PolyMesh m, ISet<int> faces, Vector3 axis, float width, float height)
    {
        axis = Vector3.Normalize(axis);
        Triangulator.Basis(axis, out var u, out var v);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (faces != null && !faces.Contains(fi)) continue;
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                var p = m.Verts[f.V[i]];
                f.UV[i] = new Vector2(Vector3.Dot(p, u) / width, -Vector3.Dot(p, v) / height);
            }
        }
    }
}
