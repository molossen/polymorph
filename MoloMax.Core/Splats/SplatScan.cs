using System.IO.Compression;
using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Splats;

/// <summary>
/// Measuring tools over a splat capture, in world space (the capture object's transform applied): pictures from any
/// camera with a block-out drawn over it, plan cuts at a height, floor / ceiling heights in a box, rays that stop on the
/// first wall, and the big flat planes of the capture. They only read the capture: modelling the block-out is left to the
/// person (or to Claude through MCP) who looks at these results.
/// </summary>
public sealed class SplatScan
{
    public readonly int Count;
    public readonly Vector3[] P;      // world positions
    public readonly uint[] Col;       // packed RGBA
    public readonly float[] Size;     // world radius of each blob (about one sigma)
    public readonly Vector3 Min, Max; // box of 98% of the points
    readonly float cell;
    readonly Dictionary<(int, int, int), List<int>> grid = new();

    public SplatScan(SplatCloud c, Matrix4x4 world, float minOpacity = 0.25f)
    {
        float sc = MathF.Pow(MathF.Abs(world.GetDeterminant()), 1f / 3f);
        var pos = new List<Vector3>(c.Count); var col = new List<uint>(c.Count); var size = new List<float>(c.Count);
        for (int i = 0; i < c.Count; i++)
        {
            float a = c.Rgba[i * 4 + 3];
            if (a < minOpacity) continue;
            var p = Vector3.Transform(new Vector3(c.Pos[i * 3], c.Pos[i * 3 + 1], c.Pos[i * 3 + 2]), world);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            pos.Add(p);
            byte r = (byte)Math.Clamp(c.Rgba[i * 4] * 255, 0, 255), g = (byte)Math.Clamp(c.Rgba[i * 4 + 1] * 255, 0, 255), b = (byte)Math.Clamp(c.Rgba[i * 4 + 2] * 255, 0, 255);
            col.Add(r | (uint)g << 8 | (uint)b << 16 | (uint)(byte)Math.Clamp(a * 255, 0, 255) << 24);
            float tr = (c.Cov[i * 6] + c.Cov[i * 6 + 3] + c.Cov[i * 6 + 5]) / 3f;
            size.Add(MathF.Sqrt(MathF.Max(tr, 0)) * sc);
        }
        P = pos.ToArray(); Col = col.ToArray(); Size = size.ToArray(); Count = P.Length;
        if (Count == 0) { Min = Max = Vector3.Zero; cell = 1; return; }
        float Pct(Func<Vector3, float> f, float q) { var a = P.Select(f).ToArray(); Array.Sort(a); return a[Math.Clamp((int)(q * (a.Length - 1)), 0, a.Length - 1)]; }
        Min = new Vector3(Pct(v => v.X, 0.01f), Pct(v => v.Y, 0.01f), Pct(v => v.Z, 0.01f));
        Max = new Vector3(Pct(v => v.X, 0.99f), Pct(v => v.Y, 0.99f), Pct(v => v.Z, 0.99f));
        var ext = Max - Min;
        cell = MathF.Max(0.02f, MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) / 160f);
        for (int i = 0; i < Count; i++)
        {
            var k = Key(P[i]);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(8);
            l.Add(i);
        }
    }

    (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    /// <summary>Points within <paramref name="r"/> of <paramref name="c"/>.</summary>
    public int CountNear(Vector3 c, float r)
    {
        int n = 0; float r2 = r * r; int k = (int)MathF.Ceiling(r / cell);
        var (x, y, z) = Key(c);
        for (int a = -k; a <= k; a++) for (int b = -k; b <= k; b++) for (int d = -k; d <= k; d++)
            if (grid.TryGetValue((x + a, y + b, z + d), out var l))
                foreach (var i in l) if (Vector3.DistanceSquared(P[i], c) <= r2) n++;
        return n;
    }

    // ------------------------------------------------------------------ measures

    public sealed class Measure
    {
        public int Points;
        public float? Floor, Ceiling;
        public Vector3 Min, Max;
        /// <summary>Heights (bottom of each 5 cm band) where the capture is dense: floors, tables, shelves, ceilings.</summary>
        public List<(float Z, int Count)> Layers = new();
    }

    /// <summary>What is inside a box: the floor and ceiling heights (densest bands at the bottom and top) and the dense
    /// horizontal layers in between.</summary>
    public Measure MeasureBox(Vector3 a, Vector3 b)
    {
        var mn = Vector3.Min(a, b); var mx = Vector3.Max(a, b);
        var m = new Measure { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        const float band = 0.05f;
        var hist = new Dictionary<int, int>();
        for (int i = 0; i < Count; i++)
        {
            var p = P[i];
            if (p.X < mn.X || p.Y < mn.Y || p.Z < mn.Z || p.X > mx.X || p.Y > mx.Y || p.Z > mx.Z) continue;
            m.Points++; m.Min = Vector3.Min(m.Min, p); m.Max = Vector3.Max(m.Max, p);
            int k = (int)MathF.Floor(p.Z / band); hist[k] = hist.GetValueOrDefault(k) + 1;
        }
        if (m.Points == 0) return m;
        int span = hist.Keys.Max() - hist.Keys.Min() + 1; float mean = (float)m.Points / Math.Max(1, span);
        var dense = hist.Where(kv => kv.Value > mean * 2.5f && kv.Value >= 20).OrderBy(kv => kv.Key).ToList();
        // merge neighbouring bands, keep the band with most points of each run
        for (int i = 0; i < dense.Count; i++)
        {
            int j = i; var best = dense[i];
            while (j + 1 < dense.Count && dense[j + 1].Key == dense[j].Key + 1) { j++; if (dense[j].Value > best.Value) best = dense[j]; }
            m.Layers.Add((best.Key * band, best.Value)); i = j;
        }
        if (m.Layers.Count > 0) { m.Floor = m.Layers[0].Z; if (m.Layers.Count > 1) m.Ceiling = m.Layers[^1].Z + band; }
        return m;
    }

    /// <summary>First dense spot along a ray (a wall, a floor, furniture): distance and point, or null.</summary>
    public (float Dist, Vector3 Point, uint Colour)? Ray(Vector3 o, Vector3 dir, float maxDist, float radius = 0.06f, int needed = 6)
    {
        dir = Vector3.Normalize(dir);
        for (float t = radius; t <= maxDist; t += radius * 0.5f)
        {
            var c = o + dir * t;
            if (CountNear(c, radius) >= needed)
            {
                // colour of the nearest point there
                uint colour = 0; float bd = float.MaxValue; int k = (int)MathF.Ceiling(radius / cell); var (x, y, z) = Key(c);
                for (int a = -k; a <= k; a++) for (int b = -k; b <= k; b++) for (int d = -k; d <= k; d++)
                    if (grid.TryGetValue((x + a, y + b, z + d), out var l))
                        foreach (var i in l) { float dd = Vector3.DistanceSquared(P[i], c); if (dd < bd) { bd = dd; colour = Col[i]; } }
                return (t, c, colour);
            }
        }
        return null;
    }

    public sealed class Plane
    {
        public Vector3 Normal; public float D; public int Points; public Vector3 Min, Max;
        public string Kind; // floor/ceiling (horizontal), wall (vertical), slope
    }

    /// <summary>The largest flat planes of the capture (RANSAC on a sample): floors, ceilings, walls. Normals point up for
    /// horizontal ones. Useful to stand a capture upright and square before modelling.</summary>
    public List<Plane> Planes(int count = 8, int seed = 1)
    {
        var rnd = new Random(seed);
        int ns = Math.Min(Count, 120000);
        var idx = Enumerable.Range(0, Count).OrderBy(_ => rnd.Next()).Take(ns).ToList();
        var ext = Max - Min; float tol = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) * 0.006f;
        var res = new List<Plane>();
        var left = idx.Select(i => P[i]).ToList();
        for (int k = 0; k < count && left.Count > 500; k++)
        {
            Vector3 bn = default; float bd = 0; int bc = 0;
            for (int it = 0; it < 400; it++)
            {
                var a = left[rnd.Next(left.Count)]; var b = left[rnd.Next(left.Count)]; var c = left[rnd.Next(left.Count)];
                var n = Vector3.Cross(b - a, c - a); float l = n.Length(); if (l < 1e-9f) continue; n /= l;
                float d = Vector3.Dot(n, a); int cnt = 0;
                for (int i = 0; i < left.Count; i += 4) if (MathF.Abs(Vector3.Dot(n, left[i]) - d) < tol) cnt++;
                if (cnt > bc) { bc = cnt; bn = n; bd = d; }
            }
            if (bc == 0) break;
            // refit on the inliers (least squares through their centroid)
            var inl = left.Where(p => MathF.Abs(Vector3.Dot(bn, p) - bd) < tol).ToList();
            var cen = inl.Aggregate(Vector3.Zero, (s, p) => s + p) / inl.Count;
            bn = RefitNormal(inl, cen, bn); bd = Vector3.Dot(bn, cen);
            if (bn.Z < -0.5f) { bn = -bn; bd = -bd; }
            var pl = new Plane { Normal = bn, D = bd, Points = inl.Count, Min = inl.Aggregate(new Vector3(float.MaxValue), Vector3.Min), Max = inl.Aggregate(new Vector3(float.MinValue), Vector3.Max) };
            pl.Kind = MathF.Abs(bn.Z) > 0.9f ? "horizontal" : MathF.Abs(bn.Z) < 0.2f ? "vertical" : "slope";
            res.Add(pl);
            left = left.Where(p => MathF.Abs(Vector3.Dot(bn, p) - bd) >= tol * 1.5f).ToList();
        }
        return res;
    }

    static Vector3 RefitNormal(List<Vector3> pts, Vector3 c, Vector3 guess)
    {
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in pts) { var d = p - c; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        // smallest eigenvector by inverse iteration on the covariance (shifted)
        var v = guess;
        for (int it = 0; it < 30; it++)
        {
            // solve (C) x = v roughly with a few Jacobi-like steps: use power iteration on (trace*I - C)
            double tr = xx + yy + zz;
            var w = new Vector3((float)((tr - xx) * v.X - xy * v.Y - xz * v.Z), (float)(-xy * v.X + (tr - yy) * v.Y - yz * v.Z), (float)(-xz * v.X - yz * v.Y + (tr - zz) * v.Z));
            float l = w.Length(); if (l < 1e-12f) break; v = w / l;
        }
        return Vector3.Dot(v, guess) < 0 ? -v : v;
    }

    // ------------------------------------------------------------------ pictures

    public sealed class Overlay
    {
        public PolyMesh Mesh; public Matrix4x4 World; public Vector3 Colour = new(1f, 0.35f, 0.1f); public string Label;
    }

    /// <summary>Picture of the capture from a camera, with block-out meshes drawn over it (faces see-through, edges solid).
    /// Ortho: <paramref name="orthoHeight"/> metres seen top to bottom.</summary>
    public RgbaImage View(Vector3 eye, Vector3 target, float fovDeg, int w, int h, IEnumerable<Overlay> overlay = null, float faceAlpha = 0.35f,
        bool ortho = false, float orthoHeight = 10f, float near = 0.05f, Vector3? clipMin = null, Vector3? clipMax = null)
    {
        var cmn = clipMin ?? new Vector3(float.MinValue); var cmx = clipMax ?? new Vector3(float.MaxValue);
        var img = RgbaImage.Solid(w, h, 28, 30, 34);
        var up = MathF.Abs(Vector3.Normalize(target - eye).Z) > 0.98f ? Vector3.UnitY : Vector3.UnitZ;
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        float aspect = (float)w / h;
        var proj = ortho ? Matrix4x4.CreateOrthographic(orthoHeight * aspect, orthoHeight, near, 10000f)
                         : Matrix4x4.CreatePerspectiveFieldOfView(fovDeg * MathF.PI / 180f, aspect, near, 10000f);
        var vp = view * proj;
        float focal = ortho ? h / orthoHeight : h / (2f * MathF.Tan(fovDeg * MathF.PI / 360f));
        var depth = new float[w * h]; Array.Fill(depth, float.MaxValue);
        // splats: far to near, each a small round blob
        var order = new List<(float Z, int I, float X, float Y, float R)>(Count);
        for (int i = 0; i < Count; i++)
        {
            var pp = P[i];
            if (pp.X < cmn.X || pp.Y < cmn.Y || pp.Z < cmn.Z || pp.X > cmx.X || pp.Y > cmx.Y || pp.Z > cmx.Z) continue;
            var q = Vector4.Transform(new Vector4(pp, 1), vp);
            if (q.W <= near && !ortho) continue;
            float iw = ortho ? 1 : 1 / q.W;
            float sx = (q.X * iw * 0.5f + 0.5f) * w, sy = (1 - (q.Y * iw * 0.5f + 0.5f)) * h;
            if (sx < -20 || sy < -20 || sx > w + 20 || sy > h + 20) continue;
            float z = ortho ? -Vector3.Transform(P[i], view).Z : q.W;
            float r = Math.Clamp(Size[i] * 1.6f * focal / (ortho ? 1 : MathF.Max(z, near)), 0.7f, 14f);
            order.Add((z, i, sx, sy, r));
        }
        order.Sort((a, b) => b.Z.CompareTo(a.Z));
        var px = img.Pixels;
        foreach (var (z, i, sx, sy, r) in order)
        {
            uint c = Col[i]; float a0 = ((c >> 24) & 255) / 255f;
            int x0 = (int)MathF.Floor(sx - r), x1 = (int)MathF.Ceiling(sx + r), y0 = (int)MathF.Floor(sy - r), y1 = (int)MathF.Ceiling(sy + r);
            for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                {
                    float dx = (x + 0.5f - sx) / r, dy = (y + 0.5f - sy) / r, d2 = dx * dx + dy * dy;
                    if (d2 > 1) continue;
                    float a = a0 * MathF.Exp(-2.2f * d2);
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(px[o] + ((int)(c & 255) - px[o]) * a);
                    px[o + 1] = (byte)(px[o + 1] + ((int)((c >> 8) & 255) - px[o + 1]) * a);
                    px[o + 2] = (byte)(px[o + 2] + ((int)((c >> 16) & 255) - px[o + 2]) * a);
                    if (a > 0.35f) depth[y * w + x] = MathF.Min(depth[y * w + x], z);
                }
        }
        if (overlay != null) DrawOverlay(img, overlay, vp, ortho, faceAlpha);
        return img;
    }

    static void DrawOverlay(RgbaImage img, IEnumerable<Overlay> overlay, Matrix4x4 vp, bool ortho, float faceAlpha)
    {
        int w = img.Width, h = img.Height; var px = img.Pixels;
        foreach (var ov in overlay)
        {
            var m = ov.Mesh; if (m == null) continue;
            var sp = new Vector3[m.Verts.Count]; var ok = new bool[m.Verts.Count];
            for (int i = 0; i < m.Verts.Count; i++)
            {
                var q = Vector4.Transform(new Vector4(Vector3.Transform(m.Verts[i], ov.World), 1), vp);
                ok[i] = ortho || q.W > 0.05f; float iw = ortho ? 1 : 1 / q.W;
                sp[i] = new Vector3((q.X * iw * 0.5f + 0.5f) * w, (1 - (q.Y * iw * 0.5f + 0.5f)) * h, q.W);
            }
            byte cr = (byte)(ov.Colour.X * 255), cg = (byte)(ov.Colour.Y * 255), cb = (byte)(ov.Colour.Z * 255);
            // faces, see-through
            var cover = new bool[w * h];
            foreach (var f in m.Faces)
            {
                if (f.V.Any(v => !ok[v])) continue;
                for (int t = 1; t + 1 < f.V.Length; t++) FillTri(sp[f.V[0]], sp[f.V[t]], sp[f.V[t + 1]], w, h, cover);
            }
            for (int i = 0; i < w * h; i++) if (cover[i]) { int o = i * 4; px[o] = (byte)(px[o] + ((int)cr - px[o]) * faceAlpha); px[o + 1] = (byte)(px[o + 1] + ((int)cg - px[o + 1]) * faceAlpha); px[o + 2] = (byte)(px[o + 2] + ((int)cb - px[o + 2]) * faceAlpha); }
            // edges
            foreach (var f in m.Faces)
                for (int k = 0; k < f.V.Length; k++)
                {
                    int a = f.V[k], b = f.V[(k + 1) % f.V.Length];
                    if (ok[a] && ok[b]) Line(img, sp[a].X, sp[a].Y, sp[b].X, sp[b].Y, cr, cg, cb);
                }
        }
    }

    static void FillTri(Vector3 a, Vector3 b, Vector3 c, int w, int h, bool[] cover)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f || (long)(x1 - x0) * (y1 - y0) > 40_000_000) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = (b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px);
                float w1 = (c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px);
                float w2 = (a.X - px) * (b.Y - py) - (a.Y - py) * (b.X - px);
                if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0)) cover[y * w + x] = true;
            }
    }

    static void Line(RgbaImage img, float x0, float y0, float x1, float y1, byte r, byte g, byte b, int thick = 1)
    {
        int w = img.Width, h = img.Height;
        float dx = x1 - x0, dy = y1 - y0; int n = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
        if (n > 20000) return;
        for (int i = 0; i <= n; i++)
        {
            float t = n == 0 ? 0 : (float)i / n; int x = (int)(x0 + dx * t), y = (int)(y0 + dy * t);
            for (int a = -thick + 1; a < thick; a++) for (int c = -thick + 1; c < thick; c++)
            {
                int xx = x + a, yy = y + c; if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                int o = (yy * w + xx) * 4; img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b;
            }
        }
    }

    /// <summary>Plan cut, seen from above like an architect's plan: the capture's points between z-thick/2 and z+thick/2
    /// (dark where dense: walls), a grid every metre (brighter every 5 m, origin lines in colour) and the block-out cut at
    /// the same height (in orange). Returns the picture and the world rectangle it shows.</summary>
    public (RgbaImage Image, Vector2 Min, Vector2 Max) Section(float z, float thick, Vector2 min, Vector2 max, float pxPerM, IEnumerable<Overlay> overlay = null)
    {
        int w = Math.Clamp((int)((max.X - min.X) * pxPerM), 16, 4096), h = Math.Clamp((int)((max.Y - min.Y) * pxPerM), 16, 4096);
        var img = RgbaImage.Solid(w, h, 245, 245, 240);
        var dens = new float[w * h]; var colr = new Vector3[w * h];
        for (int i = 0; i < Count; i++)
        {
            var p = P[i]; if (MathF.Abs(p.Z - z) > thick / 2) continue;
            int x = (int)((p.X - min.X) * pxPerM), y = (int)((max.Y - p.Y) * pxPerM);
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            dens[y * w + x] += 1; uint c = Col[i]; colr[y * w + x] += new Vector3(c & 255, (c >> 8) & 255, (c >> 16) & 255);
        }
        float top = dens.Where(d => d > 0).DefaultIfEmpty(1).OrderBy(d => d).ElementAt((int)(dens.Count(d => d > 0) * 0.95f));
        var px = img.Pixels;
        // grid
        for (int gx = (int)MathF.Floor(min.X); gx <= max.X; gx++) { float x = (gx - min.X) * pxPerM; byte v = (byte)(gx == 0 ? 0 : gx % 5 == 0 ? 170 : 215); GridLine(img, x, true, gx == 0 ? (byte)220 : v, v, gx == 0 ? (byte)60 : v); }
        for (int gy = (int)MathF.Floor(min.Y); gy <= max.Y; gy++) { float y = (max.Y - gy) * pxPerM; byte v = (byte)(gy == 0 ? 0 : gy % 5 == 0 ? 170 : 215); GridLine(img, y, false, gy == 0 ? (byte)60 : v, gy == 0 ? (byte)160 : v, gy == 0 ? (byte)60 : v); }
        for (int i = 0; i < w * h; i++)
        {
            if (dens[i] <= 0) continue;
            float t = MathF.Min(1, dens[i] / top); var c = colr[i] / dens[i];
            // dark where dense, tinted with the capture's colour
            float k = 0.15f + 0.85f * t; int o = i * 4;
            px[o] = (byte)(px[o] * (1 - k) + c.X * 0.35f * k); px[o + 1] = (byte)(px[o + 1] * (1 - k) + c.Y * 0.35f * k); px[o + 2] = (byte)(px[o + 2] * (1 - k) + c.Z * 0.35f * k);
        }
        if (overlay != null)
            foreach (var ov in overlay)
            {
                var m = ov.Mesh; if (m == null) continue;
                var wv = m.Verts.Select(v => Vector3.Transform(v, ov.World)).ToArray();
                byte cr = (byte)(ov.Colour.X * 255), cg = (byte)(ov.Colour.Y * 255), cb = (byte)(ov.Colour.Z * 255);
                foreach (var f in m.Faces)
                {
                    // segment where the face crosses the cut plane
                    var pts = new List<Vector2>();
                    for (int k = 0; k < f.V.Length; k++)
                    {
                        var a = wv[f.V[k]]; var b = wv[f.V[(k + 1) % f.V.Length]];
                        if ((a.Z - z) * (b.Z - z) < 0 || (a.Z == z && b.Z != z)) { float t = (z - a.Z) / (b.Z - a.Z); var q = a + (b - a) * t; pts.Add(new Vector2(q.X, q.Y)); }
                    }
                    for (int k = 0; k + 1 < pts.Count; k += 2)
                        Line(img, (pts[k].X - min.X) * pxPerM, (max.Y - pts[k].Y) * pxPerM, (pts[k + 1].X - min.X) * pxPerM, (max.Y - pts[k + 1].Y) * pxPerM, cr, cg, cb, 2);
                }
            }
        return (img, min, max);
    }

    static void GridLine(RgbaImage img, float at, bool vertical, byte r, byte g, byte b)
    {
        int i = (int)at; int w = img.Width, h = img.Height;
        if (vertical) { if (i < 0 || i >= w) return; for (int y = 0; y < h; y++) { int o = (y * w + i) * 4; img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; } }
        else { if (i < 0 || i >= h) return; for (int x = 0; x < w; x++) { int o = (i * w + x) * 4; img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; } }
    }

    // ------------------------------------------------------------------ PNG

    public static void SavePng(RgbaImage img, string path)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)img.Width); WriteBE(ihdr, 4, (uint)img.Height); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            int stride = img.Width * 4;
            for (int y = 0; y < img.Height; y++) { z.WriteByte(0); z.Write(img.Pixels, y * stride, stride); }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFF); crc = Crc(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c);
    }

    static uint[] crcTable;
    static uint Crc(byte[] d, uint c)
    {
        if (crcTable == null)
        {
            var tbl = new uint[256];
            for (uint n = 0; n < 256; n++) { uint x = n; for (int k = 0; k < 8; k++) x = (x & 1) != 0 ? 0xEDB88320 ^ (x >> 1) : x >> 1; tbl[n] = x; }
            crcTable = tbl;
        }
        foreach (var b in d) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
