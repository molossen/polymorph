using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Polygon reduction like 3ds Max ProOptimizer / Blender Decimate (collapse): quadric error edge collapses
/// (Garland-Heckbert), each vertex merged onto a neighbour so that UVs, vertex colours, materials and smoothing stay
/// exact. Vertices on open borders, UV seams, colour seams and material borders are kept by default, which keeps the
/// silhouette and the textures of a GTA model for its LOD.
/// </summary>
public static class Decimate
{
    public sealed class Options
    {
        /// <summary>Vertices to keep, 0..1 of the original (3ds Max "Vertex %").</summary>
        public float Ratio = 0.5f;
        public bool KeepBorders = true, KeepUvSeams = true, KeepMaterialBorders = true;
        /// <summary>Stops before a collapse would bend a face more than this (degrees) – keeps flat walls flat.</summary>
        public float MaxNormalChange = 60;
    }

    sealed class Tri { public int A, B, C; public Vector2 UA, UB, UC; public Vector4 CA, CB, CC; public Vector4? DA, DB, DC; public int Mat; public uint Smooth; public bool Dead; }

    public static PolyMesh Run(PolyMesh src, Options o)
    {
        int nv = src.Verts.Count;
        var res = new PolyMesh();
        if (nv == 0 || o.Ratio >= 0.999f) return src;
        // ---- triangles with their corner attributes
        var tris = new List<Tri>(src.Faces.Count * 2);
        foreach (var f in src.Faces)
        {
            foreach (var (a, b, c) in Triangulator.Triangulate(src, f))
                tris.Add(new Tri
                {
                    A = f.V[a], B = f.V[b], C = f.V[c], UA = f.UV[a], UB = f.UV[b], UC = f.UV[c],
                    CA = f.Col[a], CB = f.Col[b], CC = f.Col[c],
                    DA = f.Col1?[a], DB = f.Col1?[b], DC = f.Col1?[c], Mat = f.MatId, Smooth = f.Smooth,
                });
        }
        var swT = System.Diagnostics.Stopwatch.StartNew(); void T(string n) { if (Environment.GetEnvironmentVariable("DECIMATE_PROFILE") != null) Console.WriteLine($"  {n}: {swT.ElapsedMilliseconds} ms"); }
        T("triangulate");
        var pos = src.Verts.ToArray();
        var vt = new List<int>[nv];
        for (int i = 0; i < nv; i++) vt[i] = new List<int>(6);
        for (int t = 0; t < tris.Count; t++) { vt[tris[t].A].Add(t); vt[tris[t].B].Add(t); vt[tris[t].C].Add(t); }

        // ---- locked vertices: borders, seams, material borders
        var locked = new bool[nv];
        var edgeCount = new Dictionary<long, int>(tris.Count * 2, MeshOps.LongKey.I);
        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        foreach (var t in tris) { Inc(t.A, t.B); Inc(t.B, t.C); Inc(t.C, t.A); }
        void Inc(int a, int b) { var k = Key(a, b); edgeCount[k] = edgeCount.TryGetValue(k, out var c) ? c + 1 : 1; }
        foreach (var (k, c) in edgeCount)
            if (c != 2 && o.KeepBorders) { locked[(int)(k >> 32)] = true; locked[(int)(k & 0xFFFFFFFF)] = true; }
            else if (c > 2) { locked[(int)(k >> 32)] = true; locked[(int)(k & 0xFFFFFFFF)] = true; }
        for (int v = 0; v < nv; v++)
        {
            if (locked[v] || vt[v].Count == 0) continue;
            Vector2? uv = null; Vector4? col = null; int mat = int.MinValue; uint sm = uint.MaxValue; bool seam = false;
            foreach (var ti in vt[v])
            {
                var t = tris[ti]; var (u, cc) = Corner(t, v);
                if (uv == null) { uv = u; col = cc; mat = t.Mat; sm = t.Smooth; continue; }
                if (o.KeepUvSeams && (Vector2.DistanceSquared(uv.Value, u) > 1e-10f || Vector4.DistanceSquared(col.Value, cc) > 1e-8f)) seam = true;
                if (o.KeepMaterialBorders && (t.Mat != mat || t.Smooth != sm)) seam = true;
            }
            locked[v] = seam;
        }

        T("locks");
        // ---- quadrics
        var q = new Quadric[nv];
        foreach (var t in tris)
        {
            var n = Vector3.Cross(pos[t.B] - pos[t.A], pos[t.C] - pos[t.A]);
            float area = n.Length(); if (area < 1e-12f) continue;
            n /= area;
            var k = Quadric.Plane(n, -Vector3.Dot(n, pos[t.A])) * area;
            q[t.A] += k; q[t.B] += k; q[t.C] += k;
        }

        // ---- candidate half-edge collapses u -> v
        var heap = new PriorityQueue<(int U, int V, int Stamp), double>();
        var stamp = new int[nv];
        void Push(int u)
        {
            if (locked[u] || vt[u].Count == 0) return;
            var seen = new HashSet<int>();
            foreach (var ti in vt[u])
            {
                var t = tris[ti];
                foreach (var w in new[] { t.A, t.B, t.C })
                    if (w != u && seen.Add(w)) heap.Enqueue((u, w, stamp[u]), (q[u] + q[w]).Eval(pos[w]) + Vector3.DistanceSquared(pos[u], pos[w]) * 1e-6);
            }
        }
        for (int v = 0; v < nv; v++) Push(v);

        T("push");
        int alive = vt.Count(l => l.Count > 0), target = Math.Max(4, (int)(alive * Math.Clamp(o.Ratio, 0, 1)));
        float cosMax = MathF.Cos(o.MaxNormalChange * MathF.PI / 180);
        var neighU = new HashSet<int>(); var neighV = new HashSet<int>();
        while (alive > target && heap.TryDequeue(out var c, out _))
        {
            int u = c.U, v = c.V;
            if (c.Stamp != stamp[u] || vt[u].Count == 0 || vt[v].Count == 0 || locked[u]) continue;
            // the edge must still exist
            bool shared = false; int sharedCount = 0;
            foreach (var ti in vt[u]) { var t = tris[ti]; if (t.A == v || t.B == v || t.C == v) { shared = true; sharedCount++; } }
            if (!shared) continue;
            // link condition: u and v share exactly the vertices of their shared triangles (no pinching)
            neighU.Clear(); neighV.Clear();
            foreach (var ti in vt[u]) { var t = tris[ti]; neighU.Add(t.A); neighU.Add(t.B); neighU.Add(t.C); }
            foreach (var ti in vt[v]) { var t = tris[ti]; neighV.Add(t.A); neighV.Add(t.B); neighV.Add(t.C); }
            neighU.IntersectWith(neighV); neighU.Remove(u); neighU.Remove(v);
            if (neighU.Count != sharedCount) continue;
            // no flipped or folded triangle
            bool bad = false;
            foreach (var ti in vt[u])
            {
                var t = tris[ti];
                if (t.A == v || t.B == v || t.C == v) continue;
                var p0 = pos[t.A]; var p1 = pos[t.B]; var p2 = pos[t.C];
                var n0 = Vector3.Cross(p1 - p0, p2 - p0);
                if (t.A == u) p0 = pos[v]; else if (t.B == u) p1 = pos[v]; else p2 = pos[v];
                var n1 = Vector3.Cross(p1 - p0, p2 - p0);
                float l0 = n0.Length(), l1 = n1.Length();
                if (l1 < 1e-12f || (l0 > 1e-12f && Vector3.Dot(n0, n1) / (l0 * l1) < cosMax)) { bad = true; break; }
            }
            if (bad) { continue; }
            // the UV / colour of v in u's chart (u is not on a seam, so any triangle with both gives it)
            Vector2 uvV = default; Vector4 colV = default; Vector4? col1V = null;
            foreach (var ti in vt[u]) { var t = tris[ti]; if (t.A == v || t.B == v || t.C == v) { (uvV, colV) = Corner(t, v); col1V = Corner1(t, v); break; } }
            // collapse
            foreach (var ti in vt[u].ToArray())
            {
                var t = tris[ti];
                if (t.A == v || t.B == v || t.C == v)
                {
                    t.Dead = true;
                    foreach (var w in new[] { t.A, t.B, t.C }) vt[w].Remove(ti);
                    continue;
                }
                if (t.A == u) { t.A = v; t.UA = uvV; t.CA = colV; t.DA = col1V ?? t.DA; }
                else if (t.B == u) { t.B = v; t.UB = uvV; t.CB = colV; t.DB = col1V ?? t.DB; }
                else { t.C = v; t.UC = uvV; t.CC = colV; t.DC = col1V ?? t.DC; }
                vt[v].Add(ti);
            }
            vt[u].Clear();
            q[v] += q[u];
            alive--;
            // neighbours' costs changed
            stamp[v]++; Push(v);
            foreach (var ti in vt[v]) { var t = tris[ti]; foreach (var w in new[] { t.A, t.B, t.C }) if (w != v) { stamp[w]++; Push(w); } }
        }

        T("collapse");
        // ---- rebuild
        var map = new int[nv]; Array.Fill(map, -1);
        foreach (var t in tris)
        {
            if (t.Dead) continue;
            int I(int x) { if (map[x] < 0) { map[x] = res.Verts.Count; res.Verts.Add(pos[x]); } return map[x]; }
            var f = Face.FromArrays(new[] { I(t.A), I(t.B), I(t.C) }, new[] { t.UA, t.UB, t.UC }, new[] { t.CA, t.CB, t.CC },
                t.DA.HasValue ? new[] { t.DA.Value, t.DB ?? t.DA.Value, t.DC ?? t.DA.Value } : null, t.Mat, t.Smooth);
            res.Faces.Add(f);
        }
        return res;
    }

    static (Vector2, Vector4) Corner(Tri t, int v) => v == t.A ? (t.UA, t.CA) : v == t.B ? (t.UB, t.CB) : (t.UC, t.CC);
    static Vector4? Corner1(Tri t, int v) => v == t.A ? t.DA : v == t.B ? t.DB : t.DC;

    struct Quadric
    {
        double a, b, c, d, e, f, g, h, i, j; // symmetric 4x4: [a b c d; b e f g; c f h i; d g i j]
        public static Quadric Plane(Vector3 n, float off)
        {
            double x = n.X, y = n.Y, z = n.Z, w = off;
            return new Quadric { a = x * x, b = x * y, c = x * z, d = x * w, e = y * y, f = y * z, g = y * w, h = z * z, i = z * w, j = w * w };
        }
        public static Quadric operator +(Quadric p, Quadric r) => new() { a = p.a + r.a, b = p.b + r.b, c = p.c + r.c, d = p.d + r.d, e = p.e + r.e, f = p.f + r.f, g = p.g + r.g, h = p.h + r.h, i = p.i + r.i, j = p.j + r.j };
        public static Quadric operator *(Quadric p, float s) => new() { a = p.a * s, b = p.b * s, c = p.c * s, d = p.d * s, e = p.e * s, f = p.f * s, g = p.g * s, h = p.h * s, i = p.i * s, j = p.j * s };
        public double Eval(Vector3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            return a * x * x + 2 * b * x * y + 2 * c * x * z + 2 * d * x + e * y * y + 2 * f * y * z + 2 * g * y + h * z * z + 2 * i * z + j;
        }
    }
}
