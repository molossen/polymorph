using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Scenes;

public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

public readonly record struct PickHit(SceneObject Object, int Face, float Distance, Vector3 Point);

public static class Picking
{
    /// <summary>Möller–Trumbore. Returns distance along the ray or -1.</summary>
    public static float RayTriangle(in Ray r, Vector3 a, Vector3 b, Vector3 c, bool twoSided = true)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(r.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (twoSided ? MathF.Abs(det) < 1e-10f : det < 1e-10f) return -1;
        float inv = 1f / det;
        var s = r.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(r.Direction, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 1e-6f ? t : -1;
    }

    public static bool RayBox(in Ray r, Vector3 mn, Vector3 mx, out float tmin)
    {
        tmin = 0; float tmax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? r.Origin.X : i == 1 ? r.Origin.Y : r.Origin.Z;
            float d = i == 0 ? r.Direction.X : i == 1 ? r.Direction.Y : r.Direction.Z;
            float lo = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, hi = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(d) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        return true;
    }

    /// <summary>Closest face of one object hit by a world ray (back faces count, like Max).</summary>
    /// <param name="cullBack">Skip triangles seen from behind (they are not drawn with backface culling: a click must not land on them).</param>
    const int ChunkSize = 1024;
    sealed class Chunks { public int Version; public int Faces, Verts; public Vector3[] Min, Max; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, Chunks> chunkCache = new();

    /// <summary>Local-space boxes of each run of 1024 polygons, cached per mesh version.</summary>
    static Chunks ChunkBoxes(SceneObject o, PolyMesh m)
    {
        int ver = ReferenceEquals(m, o.Mesh) ? o.Version : -1;
        if (ver >= 0 && chunkCache.TryGetValue(m, out var c) && c.Version == ver && c.Faces == m.Faces.Count && c.Verts == m.Verts.Count) return c;
        int fc = m.Faces.Count, n = (fc + ChunkSize - 1) / ChunkSize;
        c = new Chunks { Version = ver, Faces = fc, Verts = m.Verts.Count, Min = new Vector3[n], Max = new Vector3[n] };
        var V = m.Verts; var F = m.Faces;
        System.Threading.Tasks.Parallel.For(0, n, k =>
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = k * ChunkSize; i < Math.Min(fc, (k + 1) * ChunkSize); i++)
                foreach (var v in F[i].V) { var p = V[v]; mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var pad = (mx - mn) * 1e-4f + new Vector3(1e-4f);
            c.Min[k] = mn - pad; c.Max[k] = mx + pad;
        });
        if (ver >= 0) chunkCache.AddOrUpdate(m, c);
        return c;
    }

    public static PickHit? PickFace(SceneObject o, in Ray worldRay, bool evaluated = false, bool cullBack = false)
    {
        var m = evaluated && o.Modifiers is { Count: > 0 } ? o.Evaluated() : o.Mesh;
        if (m == o.Mesh)
        {
            var (mn, mx) = o.FastWorldBounds();
            var pad = new Vector3(0.01f);
            if (!RayBox(worldRay, mn - pad, mx + pad, out _)) return null;
        }
        // the ray goes into the object's own space (same t), so no vertex is transformed and nothing is allocated
        var w = o.World;
        if (!Matrix4x4.Invert(w, out var inv)) return null;
        var lr = new Ray(Vector3.Transform(worldRay.Origin, inv), Vector3.TransformNormal(worldRay.Direction, inv));
        float sign = w.GetDeterminant() < 0 ? -1f : 1f;
        var V = m.Verts; var F = m.Faces; int fc = F.Count;
        float Tri(in Ray r, int fi, Vector3 a, Vector3 b, Vector3 c)
        {
            if (cullBack && sign * Vector3.Dot(Vector3.Cross(b - a, c - a), r.Direction) >= 0) return -1;
            return RayTriangle(r, a, b, c);
        }
        (float T, int F) Scan(int from, int to)
        {
            float bt = float.MaxValue; int bf = -1;
            for (int fi = from; fi < to; fi++)
            {
                var f = F[fi].V;
                if (f.Length == 3) { float tt = Tri(lr, fi, V[f[0]], V[f[1]], V[f[2]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; } }
                else if (f.Length == 4)
                {
                    float tt = Tri(lr, fi, V[f[0]], V[f[1]], V[f[2]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; }
                    tt = Tri(lr, fi, V[f[0]], V[f[2]], V[f[3]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; }
                }
                else if (f.Length > 4)
                    foreach (var (a, b, c) in Triangulator.Triangulate(m, F[fi]))
                    { float tt = Tri(lr, fi, V[f[a]], V[f[b]], V[f[c]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; } }
            }
            return (bt, bf);
        }
        (float T, int F) best;
        if (fc < 20000) best = Scan(0, fc);
        else
        {
            // big mesh: boxes of 1024 polygons (kept while the mesh doesn't change) → only the boxes the ray crosses are tested
            var cb = ChunkBoxes(o, m);
            var hitChunks = new List<int>();
            for (int k = 0; k < cb.Min.Length; k++) if (RayBox(lr, cb.Min[k], cb.Max[k], out _)) hitChunks.Add(k);
            var res = new (float, int)[hitChunks.Count];
            System.Threading.Tasks.Parallel.For(0, hitChunks.Count, j => { int k = hitChunks[j]; res[j] = Scan(k * ChunkSize, Math.Min(fc, (k + 1) * ChunkSize)); });
            best = (float.MaxValue, -1);
            foreach (var r in res) if (r.Item2 >= 0 && r.Item1 < best.T) best = r;
        }
        if (best.F < 0) return null;
        return new PickHit(o, best.F, best.T, worldRay.Origin + worldRay.Direction * best.T);
    }

    /// <summary>
    /// Closest face hit, testing the ray in the object's own space (no vertex is transformed) and skipping objects whose box
    /// starts farther than the best hit so far. For scenes of thousands of objects (map mode).
    /// </summary>
    public static PickHit? PickNearest(IEnumerable<SceneObject> objects, in Ray worldRay, Func<SceneObject, bool> skip = null)
    {
        var cands = new List<(float T, SceneObject O)>();
        foreach (var o in objects)
        {
            if (o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0 || skip != null && skip(o)) continue;
            var (mn, mx) = o.FastWorldBounds();
            if (RayBox(worldRay, mn - new Vector3(0.01f), mx + new Vector3(0.01f), out float t)) cands.Add((t, o));
        }
        cands.Sort((a, b) => a.T.CompareTo(b.T));
        PickHit? best = null;
        foreach (var (t0, o) in cands)
        {
            if (best.HasValue && t0 > best.Value.Distance) break;
            if (!Matrix4x4.Invert(o.World, out var inv)) continue;
            var lr = new Ray(Vector3.Transform(worldRay.Origin, inv), Vector3.TransformNormal(worldRay.Direction, inv)); // same t as the world ray
            var m = o.Mesh; var V = m.Verts;
            float bt = best?.Distance ?? float.MaxValue; int bf = -1;
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi].V;
                if (f.Length == 3) { float tt = RayTriangle(lr, V[f[0]], V[f[1]], V[f[2]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; } }
                else if (f.Length == 4)
                {
                    float tt = RayTriangle(lr, V[f[0]], V[f[1]], V[f[2]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; }
                    tt = RayTriangle(lr, V[f[0]], V[f[2]], V[f[3]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; }
                }
                else
                    foreach (var (a, b, c) in Triangulator.Triangulate(m, m.Faces[fi]))
                    { float tt = RayTriangle(lr, V[f[a]], V[f[b]], V[f[c]]); if (tt > 0 && tt < bt) { bt = tt; bf = fi; } }
            }
            if (bf >= 0) best = new PickHit(o, bf, bt, worldRay.Origin + worldRay.Direction * bt);
        }
        return best;
    }

    public static PickHit? PickObject(Scene s, in Ray worldRay, Func<SceneObject, bool> skip = null)
    {
        // plain meshes: the fast local-space test; objects with modifiers: their evaluated mesh
        var best = PickNearest(s.Objects, worldRay, o => o.Frozen || o.Modifiers is { Count: > 0 } || o.Gta is { BoundsOnly: true } || (skip != null && skip(o)));
        foreach (var o in s.Objects)
        {
            if (o.Hidden || o.Frozen || o.Modifiers is not { Count: > 0 } || (skip != null && skip(o))) continue;
            var h = PickFace(o, worldRay, evaluated: true);
            if (h.HasValue && (!best.HasValue || h.Value.Distance < best.Value.Distance)) best = h;
        }
        return best;
    }

    /// <summary>Intersection with the horizontal plane Z = <paramref name="z"/>.</summary>
    public static bool RayGround(in Ray r, float z, out Vector3 point)
    {
        point = default;
        if (MathF.Abs(r.Direction.Z) < 1e-8f) return false;
        float t = (z - r.Origin.Z) / r.Direction.Z;
        if (t <= 0) return false;
        point = r.Origin + r.Direction * t;
        return true;
    }

    /// <summary>Closest point between a ray and an infinite line (for axis-constrained gizmo drags).</summary>
    public static float RayLineParam(in Ray r, Vector3 linePoint, Vector3 lineDir)
    {
        var w0 = linePoint - r.Origin;
        float a = Vector3.Dot(lineDir, lineDir), b = Vector3.Dot(lineDir, r.Direction), c = Vector3.Dot(r.Direction, r.Direction);
        float d = Vector3.Dot(lineDir, w0), e = Vector3.Dot(r.Direction, w0);
        float den = a * c - b * b;
        if (MathF.Abs(den) < 1e-10f) return 0;
        return (b * e - c * d) / den;
    }

    public static bool RayPlane(in Ray r, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
    {
        point = default;
        float den = Vector3.Dot(planeNormal, r.Direction);
        if (MathF.Abs(den) < 1e-8f) return false;
        float t = Vector3.Dot(planePoint - r.Origin, planeNormal) / den;
        if (t < 0) return false;
        point = r.Origin + r.Direction * t;
        return true;
    }
}
