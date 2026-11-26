using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>What the clean-up is allowed to do.</summary>
public sealed class CleanOptions
{
    /// <summary>Neighbour faces within this angle (degrees) of the area they join become one polygon. 0 = only perfectly flat areas.</summary>
    public float Angle = 0.5f;
    /// <summary>Never merge across a UV seam or a vertex colour change (the texture stays exactly where it was).</summary>
    public bool KeepUvSeams = true;
    /// <summary>Never merge across a smoothing group change (shading stays the same).</summary>
    public bool KeepSmoothing = true;
    /// <summary>Points sitting in the middle of a straight edge (nothing else attached) are removed.</summary>
    public bool StraightPoints = true;
    /// <summary>Points closer than this are joined first (duplicates, cracks). 0 = off.</summary>
    public float Weld = 0.0001f;
    /// <summary>Flat areas rebuilt as straight rows of quads (only on closed meshes, only when the volume stays the same).</summary>
    public bool Quads;
}

public sealed record CleanReport(int FacesBefore, int FacesAfter, int VertsBefore, int VertsAfter, int TrisBefore, int TrisAfter)
{
    public override string ToString() => $"{FacesBefore:N0} -> {FacesAfter:N0} faces, {VertsBefore:N0} -> {VertsAfter:N0} points, {TrisBefore:N0} -> {TrisAfter:N0} triangles in game";
}

/// <summary>
/// Clean-up without changing the shape: duplicate points joined, flat areas cut in many faces made one polygon again,
/// points in the middle of straight edges removed. Material borders, UV seams, vertex colours and smoothing groups
/// are kept, so the model looks exactly the same (in the editor and in the game), with fewer edges.
/// </summary>
public static class MeshClean
{
    public static PolyMesh Run(PolyMesh src, CleanOptions o, out CleanReport report)
    {
        // never worse than before: a closed solid must stay closed and the polygons must stay valid; if a step breaks
        // that (welding two copies lying on top of each other, a polygon pinched to 2 points), it is done without it
        bool closed = src.IsClosedManifold(out _);
        var m = RunOnce(src, o, out report);
        if (Sound(m, closed)) return m;
        if (o.Weld > 0)
        {
            var o2 = new CleanOptions { Angle = o.Angle, KeepUvSeams = o.KeepUvSeams, KeepSmoothing = o.KeepSmoothing, StraightPoints = o.StraightPoints, Weld = 0, Quads = o.Quads };
            m = RunOnce(src, o2, out report);
            if (Sound(m, closed)) return m;
        }
        var c = src.Clone();
        int t = Tris(c);
        report = new CleanReport(c.Faces.Count, c.Faces.Count, c.Verts.Count, c.Verts.Count, t, t);
        return c;
    }

    static bool Sound(PolyMesh m, bool mustBeClosed)
    {
        try { m.Validate(); } catch { return false; }
        return !mustBeClosed || m.IsClosedManifold(out _);
    }

    static PolyMesh RunOnce(PolyMesh src, CleanOptions o, out CleanReport report)
    {
        var m = src.Clone();
        int f0 = m.Faces.Count, v0 = m.Verts.Count, t0 = Tris(m);
        if (o.Weld > 0) MeshOps.WeldByDistance(m, o.Weld);
        m.CleanFaces();
        MergeAreas(m, o);
        if (o.StraightPoints) for (int pass = 0; pass < 4 && DissolveStraight(m, o); pass++) { }
        if (o.Quads && m.IsClosedManifold(out _))
        {
            var q = CsgClean.Clean(m);
            if (!ReferenceEquals(q, m) && (!o.KeepUvSeams || SameUvLayout(m, q))) m = q;
        }
        m.RemoveUnusedVertices();
        report = new CleanReport(f0, m.Faces.Count, v0, m.Verts.Count, t0, Tris(m));
        return m;
    }

    static int Tris(PolyMesh m) { int t = 0; foreach (var f in m.Faces) t += Math.Max(0, f.V.Length - 2); return t; }

    static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    // ------------------------------------------------------------------ flat areas as one polygon

    static void MergeAreas(PolyMesh m, CleanOptions o)
    {
        int nf = m.Faces.Count;
        var normal = new Vector3[nf];
        for (int i = 0; i < nf; i++) normal[i] = m.FaceNormal(i);
        // directed edge -> (face, corner)
        var dir = new Dictionary<long, (int F, int C)>(nf * 4, MeshOps.LongKey.I);
        var count = new Dictionary<long, int>(nf * 4, MeshOps.LongKey.I);
        for (int fi = 0; fi < nf; fi++)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                dir[Key(a, b)] = (fi, i);
                long u = a < b ? Key(a, b) : Key(b, a);
                count[u] = count.GetValueOrDefault(u) + 1;
            }
        }
        float cosA = MathF.Cos(Math.Clamp(o.Angle, 0, 89) * MathF.PI / 180f);
        if (o.Angle <= 0) cosA = 0.99999f;
        bool Joinable(int fa, int ca, int fb, int cb)
        {
            var A = m.Faces[fa]; var B = m.Faces[fb];
            if (A.MatId != B.MatId) return false;
            if (o.KeepSmoothing && A.Smooth != B.Smooth) return false;
            if (o.KeepUvSeams)
            {
                // edge a->b in A at corner ca, b->a in B at corner cb
                int na = A.V.Length, nb = B.V.Length;
                int a1 = (ca + 1) % na, b1 = (cb + 1) % nb;
                if (Vector2.DistanceSquared(A.UV[ca], B.UV[b1]) > 1e-8f || Vector2.DistanceSquared(A.UV[a1], B.UV[cb]) > 1e-8f) return false;
                if (Vector4.DistanceSquared(A.Col[ca], B.Col[b1]) > 1e-6f || Vector4.DistanceSquared(A.Col[a1], B.Col[cb]) > 1e-6f) return false;
                if (A.Col1 != null || B.Col1 != null)
                {
                    if (A.Col1 == null || B.Col1 == null) return false;
                    if (Vector4.DistanceSquared(A.Col1[ca], B.Col1[b1]) > 1e-6f || Vector4.DistanceSquared(A.Col1[a1], B.Col1[cb]) > 1e-6f) return false;
                }
                if (A.UV1 != null || B.UV1 != null)
                {
                    if (A.UV1 == null || B.UV1 == null) return false;
                    if (Vector2.DistanceSquared(A.UV1[ca], B.UV1[b1]) > 1e-8f || Vector2.DistanceSquared(A.UV1[a1], B.UV1[cb]) > 1e-8f) return false;
                }
            }
            return true;
        }
        // grow areas from a seed: every face of the area stays within the angle of the seed (no slow drift round a curve)
        var area = new int[nf]; Array.Fill(area, -1);
        var groups = new List<List<int>>();
        var stack = new Stack<int>();
        for (int s = 0; s < nf; s++)
        {
            if (area[s] >= 0) continue;
            int gid = groups.Count; var g = new List<int> { s }; groups.Add(g); area[s] = gid;
            var n0 = normal[s]; float d0 = Vector3.Dot(n0, m.Verts[m.Faces[s].V[0]]);
            stack.Push(s);
            while (stack.Count > 0)
            {
                int fi = stack.Pop(); var f = m.Faces[fi];
                for (int i = 0; i < f.V.Length; i++)
                {
                    int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                    if (count[a < b ? Key(a, b) : Key(b, a)] != 2 || !dir.TryGetValue(Key(b, a), out var nb)) continue;
                    int fj = nb.F;
                    if (area[fj] >= 0 || Vector3.Dot(normal[fj], n0) < cosA) continue;
                    if (o.Angle <= 0 && m.Faces[fj].V.Any(v => MathF.Abs(Vector3.Dot(n0, m.Verts[v]) - d0) > 1e-4f)) continue;
                    if (!Joinable(fi, i, fj, nb.C)) continue;
                    area[fj] = gid; g.Add(fj); stack.Push(fj);
                }
            }
        }
        var outFaces = new List<Face>(nf);
        foreach (var g in groups)
        {
            if (g.Count == 1) { outFaces.Add(m.Faces[g[0]]); continue; }
            var merged = Outline(m, g, dir);
            if (merged == null) { foreach (var fi in g) outFaces.Add(m.Faces[fi]); continue; }
            outFaces.Add(merged);
        }
        m.Faces = outFaces;
    }

    /// <summary>The area's border as one polygon, or null when it has holes or touches itself (then it stays as it was).</summary>
    static Face Outline(PolyMesh m, List<int> g, Dictionary<long, (int F, int C)> dir)
    {
        var inG = new HashSet<int>(g);
        var next = new Dictionary<int, (int To, int F, int C)>();
        int edges = 0;
        foreach (var fi in g)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                if (dir.TryGetValue(Key(b, a), out var tw) && inG.Contains(tw.F)) continue; // inside the area
                if (next.ContainsKey(a)) return null; // the border passes twice through a point
                next[a] = (b, fi, i); edges++;
            }
        }
        if (edges < 3) return null;
        int start = next.Keys.First(), cur = start, guard = 0;
        var v = new List<int>(); var uv = new List<Vector2>(); var col = new List<Vector4>(); var col1 = new List<Vector4>(); var uv1 = new List<Vector2>();
        var f0 = m.Faces[g[0]]; bool hasCol1 = g.All(fi => m.Faces[fi].Col1 != null), hasUv1 = g.All(fi => m.Faces[fi].UV1 != null);
        do
        {
            if (!next.TryGetValue(cur, out var e) || guard++ > edges) return null;
            var f = m.Faces[e.F];
            v.Add(cur); uv.Add(f.UV[e.C]); col.Add(f.Col[e.C]); if (hasCol1) col1.Add(f.Col1[e.C]); if (hasUv1) uv1.Add(f.UV1[e.C]);
            cur = e.To;
        } while (cur != start);
        if (v.Count != edges) return null; // several loops: a hole in the area
        return new Face(v.ToArray(), f0.MatId, f0.Smooth) { UV = uv.ToArray(), Col = col.ToArray(), Col1 = hasCol1 ? col1.ToArray() : null, UV1 = hasUv1 ? uv1.ToArray() : null };
    }

    // ------------------------------------------------------------------ points in the middle of straight edges

    static bool DissolveStraight(PolyMesh m, CleanOptions o)
    {
        var vf = m.BuildVertexFaces();
        var drop = new HashSet<int>();
        for (int v = 0; v < vf.Length; v++)
        {
            var faces = vf[v];
            if (faces.Count == 0 || faces.Count > 2) continue;
            (int, int)? pair = null; bool ok = true;
            foreach (var fi in faces)
            {
                var f = m.Faces[fi]; int n = f.V.Length; if (n <= 3) { ok = false; break; }
                int i = Array.IndexOf(f.V, v); int ia = (i + n - 1) % n, ib = (i + 1) % n;
                int a = f.V[ia], b = f.V[ib];
                var p = a < b ? (a, b) : (b, a);
                if (pair == null) pair = p; else if (pair.Value != p) { ok = false; break; }
                var pa = m.Verts[a]; var d = m.Verts[b] - pa; float len2 = d.LengthSquared();
                if (len2 < 1e-12f) { ok = false; break; }
                float t = Vector3.Dot(m.Verts[v] - pa, d) / len2;
                if (t <= 0 || t >= 1 || Vector3.DistanceSquared(pa + d * t, m.Verts[v]) > 1e-10f * MathF.Max(1, len2)) { ok = false; break; }
                // the texture and colours along the edge must not bend at that point
                if (Vector2.DistanceSquared(Vector2.Lerp(f.UV[ia], f.UV[ib], t), f.UV[i]) > 1e-8f) { ok = false; break; }
                if (Vector4.DistanceSquared(Vector4.Lerp(f.Col[ia], f.Col[ib], t), f.Col[i]) > 1e-5f) { ok = false; break; }
            }
            if (!ok || pair == null) continue;
            drop.Add(v);
        }
        if (drop.Count == 0) return false;
        foreach (var f in m.Faces)
        {
            if (!f.V.Any(drop.Contains)) continue;
            var keep = Enumerable.Range(0, f.V.Length).Where(i => !drop.Contains(f.V[i])).ToArray();
            f.V = keep.Select(i => f.V[i]).ToArray(); f.UV = keep.Select(i => f.UV[i]).ToArray(); f.Col = keep.Select(i => f.Col[i]).ToArray();
            if (f.Col1 != null) f.Col1 = keep.Select(i => f.Col1[i]).ToArray();
            if (f.UV1 != null) f.UV1 = keep.Select(i => f.UV1[i]).ToArray();
        }
        return true;
    }

    /// <summary>True when every corner of <paramref name="b"/> has the UV the original surface had at that point
    /// (a rebuilt area only kept if its texture didn't move).</summary>
    static bool SameUvLayout(PolyMesh a, PolyMesh b)
    {
        var uvAt = new Dictionary<(long, long, long), List<Vector2>>();
        (long, long, long) K(Vector3 p) => ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));
        foreach (var f in a.Faces) for (int i = 0; i < f.V.Length; i++) { var k = K(a.Verts[f.V[i]]); if (!uvAt.TryGetValue(k, out var l)) uvAt[k] = l = new(); l.Add(f.UV[i]); }
        foreach (var f in b.Faces)
            for (int i = 0; i < f.V.Length; i++)
                if (uvAt.TryGetValue(K(b.Verts[f.V[i]]), out var l) && !l.Any(u => Vector2.DistanceSquared(u, f.UV[i]) < 1e-6f)) return false;
        return true;
    }
}
