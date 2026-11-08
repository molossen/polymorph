using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Tools;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Modifiers;

/// <summary>Shape of an opening inside its rectangle.</summary>
public enum OpeningShape { Rectangle, Arch, Circle, Gothic }

/// <summary>A hole traced on a wall: door, window, passage (rectangle, arch or round).</summary>
public sealed class OpeningRect
{
    /// <summary>Rectangle, arch (round top, as wide as the opening) or circle / oval filling the rectangle.</summary>
    public OpeningShape Shape;
    /// <summary>Segments on the curved part (arch top or whole circle): more = smoother, more faces.</summary>
    public int Segments = 16;
    /// <summary>A point on the wall surface, in the object's space.</summary>
    public Vector3 Origin;
    /// <summary>Outward normal of that surface (object space).</summary>
    public Vector3 Normal = Vector3.UnitY;
    /// <summary>The rectangle on the wall, in metres from <see cref="Origin"/> along the wall's right / up axes.</summary>
    public float Left, Right, Bottom, Top;
    /// <summary>How deep it goes: 0 = right through the wall (thickness found by itself).</summary>
    public float Depth;

    public float Width => Right - Left;
    public const int MinSegments = 3, MaxSegments = 96;

    /// <summary>The outline in the wall's (right, up) axes, counter-clockwise seen from the front.</summary>
    public List<Vector2> Outline()
    {
        float l = Left, r = Right, b = Bottom, t = Top, cx = (l + r) * 0.5f;
        int n = Math.Clamp(Segments, MinSegments, MaxSegments);
        var pts = new List<Vector2>();
        switch (Shape)
        {
            case OpeningShape.Arch:
            {
                float rx = (r - l) * 0.5f, ry = MathF.Min(rx, t - b), spring = t - ry;
                pts.Add(new(l, b)); pts.Add(new(r, b));
                if (spring > b + 1e-4f) pts.Add(new(r, spring));
                for (int k = 1; k < n; k++) { float a = MathF.PI * k / n; pts.Add(new(cx + rx * MathF.Cos(a), spring + ry * MathF.Sin(a))); }
                if (spring > b + 1e-4f) pts.Add(new(l, spring));
                break;
            }
            case OpeningShape.Gothic:
            {
                // pointed (church) arch: two arcs of radius = width, each centred on the opposite springing point
                float w = r - l, archH = w * 0.8660254f;
                float sc = MathF.Min(1f, (t - b) / archH), spring = t - archH * sc;
                int h = Math.Max(2, n / 2);
                pts.Add(new(l, b)); pts.Add(new(r, b));
                if (spring > b + 1e-4f) pts.Add(new(r, spring));
                for (int k = 1; k < h; k++) { float a = MathF.PI / 3f * k / h; pts.Add(new(l + w * MathF.Cos(a), spring + w * MathF.Sin(a) * sc)); }
                pts.Add(new(cx, spring + archH * sc));
                for (int k = 1; k < h; k++) { float a = MathF.PI * 2f / 3f + MathF.PI / 3f * k / h; pts.Add(new(r + w * MathF.Cos(a), spring + w * MathF.Sin(a) * sc)); }
                if (spring > b + 1e-4f) pts.Add(new(l, spring));
                break;
            }
            case OpeningShape.Circle:
            {
                float rx = (r - l) * 0.5f, ry = (t - b) * 0.5f, cy = (b + t) * 0.5f;
                n = Math.Max(n, 6);
                // start at the bottom so a round window sits straight (a flat-free, symmetric outline)
                for (int k = 0; k < n; k++) { float a = -MathF.PI * 0.5f + 2 * MathF.PI * k / n; pts.Add(new(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a))); }
                break;
            }
            default:
                pts.Add(new(l, b)); pts.Add(new(r, b)); pts.Add(new(r, t)); pts.Add(new(l, t));
                break;
        }
        return pts;
    }
    public float Height => Top - Bottom;
    public OpeningRect Copy() => (OpeningRect)MemberwiseClone();

    /// <summary>The wall's axes: up = world up on the wall, right = along the wall (the same ones the doorway tool uses).</summary>
    public (Vector3 Right, Vector3 Up, Vector3 N) Axes()
    {
        var n = Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(Normal) : Vector3.UnitY;
        var up = Vector3.UnitZ - n * n.Z;
        if (up.LengthSquared() < 0.01f) up = Vector3.UnitY - n * n.Y; // floor / ceiling: a hatch
        up = Vector3.Normalize(up);
        return (Vector3.Normalize(Vector3.Cross(up, n)), up, n);
    }

    /// <summary>A rectangle point in object space.</summary>
    public Vector3 At(float x, float y) { var (r, u, _) = Axes(); return Origin + r * x + u * y; }
}

/// <summary>
/// Wall Opening: rectangular doors and windows traced straight on the wall, each side movable, no cutter object and no
/// boolean to set up. Thick walls are cut right through with clean straight faces (and the reveal inside); single
/// surfaces (MLO room boxes) get the hole in the face.
/// </summary>
public sealed class WallOpeningModifier : Modifier
{
    public List<OpeningRect> Openings = new();

    public WallOpeningModifier() { Name = "Wall Opening"; }
    public override string TypeName => "Wall Opening";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override Modifier Clone()
    {
        var c = (WallOpeningModifier)base.Clone();
        c.Openings = Openings.Select(o => o.Copy()).ToList();
        return c;
    }

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        foreach (var op in Openings) m = Cut(m, op);
        return m;
    }

    /// <summary>One opening cut into the mesh (the mesh comes back unchanged when it can't be cut).
    /// On a big mesh (a whole GTA room) only the faces around the opening go through the boolean: the rest is kept as it is,
    /// so a 200k-polygon room is cut in milliseconds instead of a second at every edit.</summary>
    public static PolyMesh Cut(PolyMesh m, OpeningRect op)
    {
        if (op.Width < 0.01f || op.Height < 0.01f || m.Faces.Count == 0) return m;
        if (m.Faces.Count > 2000 && LocalRegion(m, op) is { } region)
        {
            if (region.Count == 0) return m; // no wall where the opening is
            if (region.Count < m.Faces.Count * 0.7f) return CutLocal(m, op, region);
        }
        return CutAll(m, op);
    }

    /// <summary>Faces whose box meets the opening pushed through a wall up to 3 m thick (or its own depth).</summary>
    static List<int> LocalRegion(PolyMesh m, OpeningRect op)
    {
        var (right, up, n) = op.Axes();
        float back = (op.Depth > 0 ? op.Depth : 3f) + 0.1f, front = 0.1f;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in op.Outline())
            foreach (var d in new[] { front, -back })
            {
                var w = op.Origin + right * p.X + up * p.Y + n * d;
                mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
            }
        mn -= new Vector3(0.05f); mx += new Vector3(0.05f);
        var res = new List<int>();
        var verts = m.Verts;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi].V;
            var fmn = verts[f[0]]; var fmx = fmn;
            for (int i = 1; i < f.Length; i++) { fmn = Vector3.Min(fmn, verts[f[i]]); fmx = Vector3.Max(fmx, verts[f[i]]); }
            if (fmx.X < mn.X || fmn.X > mx.X || fmx.Y < mn.Y || fmn.Y > mx.Y || fmx.Z < mn.Z || fmn.Z > mx.Z) continue;
            res.Add(fi);
        }
        return res;
    }

    /// <summary>The boolean on the faces around the opening only, then put back with the untouched rest.</summary>
    static PolyMesh CutLocal(PolyMesh m, OpeningRect op, List<int> region)
    {
        var sub = MeshOps.Extract(m, region);
        var cutSub = CutAll(sub, op);
        if (ReferenceEquals(cutSub, sub)) return m; // nothing cut
        var inRegion = new HashSet<int>(region);
        var res = new PolyMesh();
        res.Verts.AddRange(m.Verts);
        var seam = new HashSet<int>();
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (inRegion.Contains(fi)) { foreach (var v in m.Faces[fi].V) seam.Add(v); continue; }
            res.Faces.Add(m.Faces[fi]);
        }
        int off = res.Verts.Count;
        res.Verts.AddRange(cutSub.Verts);
        for (int i = 0; i < cutSub.Verts.Count; i++) seam.Add(off + i);
        foreach (var f in cutSub.Faces)
        {
            var c = f.Clone();
            for (int i = 0; i < c.V.Length; i++) c.V[i] += off;
            res.Faces.Add(c);
        }
        // the cut piece is joined back to the rest (and to itself) where the vertices meet
        MeshOps.WeldByDistance(res, 1e-4f, seam);
        res.RemoveUnusedVertices();
        return res;
    }

    static PolyMesh CutAll(PolyMesh m, OpeningRect op)
    {
        var (right, up, n) = op.Axes();
        var centre = op.At((op.Left + op.Right) * 0.5f, (op.Bottom + op.Top) * 0.5f);
        float depth = op.Depth > 0 ? op.Depth : Thickness(m, centre, n);
        if (depth <= 0) return Surface(m, op);
        // the outline pushed through the wall, from just in front of it to just behind it, cut out (clean faces by the boolean's own cleaning)
        const float margin = 0.02f;
        float len = depth + 2 * margin;
        var cutter = Prism(op.Outline(), len);
        // the reveal takes the material of the wall it goes through
        int wallMat = -1; float bestD = float.MaxValue;
        foreach (var f in m.Faces)
        {
            if (Vector3.Dot(m.FaceNormal(f), n) < 0.99f) continue;
            float d = MathF.Abs(Vector3.Dot(m.Verts[f.V[0]] - centre, n));
            if (d < bestD) { bestD = d; wallMat = f.MatId; }
        }
        if (wallMat >= 0) foreach (var f in cutter.Faces) f.MatId = wallMat;
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0, -n.X, -n.Y, -n.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        basis.Translation = op.Origin + n * margin;
        try
        {
            var r = Csg.Apply(m, Matrix4x4.Identity, cutter, basis, Csg.Op.Subtract);
            return r.Faces.Count > 0 ? r : m;
        }
        catch { return m; }
    }

    /// <summary>A closed prism: the outline (x, z) pushed along +Y from 0 to <paramref name="len"/>.</summary>
    public static PolyMesh Prism(List<Vector2> outline, float len)
    {
        var m = new PolyMesh(); int n = outline.Count;
        for (int i = 0; i < n; i++) m.AddVertex(new Vector3(outline[i].X, 0, outline[i].Y));
        for (int i = 0; i < n; i++) m.AddVertex(new Vector3(outline[i].X, len, outline[i].Y));
        // outline is counter-clockwise seen from -Y (the front): the front cap faces -Y as listed
        var front = m.AddFace(Enumerable.Range(0, n).ToArray()); front.UV = outline.ToArray();
        var back = m.AddFace(Enumerable.Range(0, n).Reverse().Select(i => i + n).ToArray()); back.UV = Enumerable.Range(0, n).Reverse().Select(i => outline[i]).ToArray();
        float u = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n; float seg = Vector2.Distance(outline[i], outline[j]);
            var f = m.AddFace(new[] { j, i, i + n, j + n });
            f.UV = new[] { new Vector2(u + seg, 0), new Vector2(u, 0), new Vector2(u, len), new Vector2(u + seg, len) };
            u += seg;
        }
        if (CsgClean.Volume(m) < 0) foreach (var f in m.Faces) { Array.Reverse(f.V); Array.Reverse(f.UV); }
        return m;
    }

    /// <summary>Distance through the wall: from the surface, the first face behind it that faces the other way.</summary>
    public static float Thickness(PolyMesh m, Vector3 onSurface, Vector3 n)
    {
        var ray = new Ray(onSurface - n * 1e-3f, -n);
        float best = float.MaxValue;
        foreach (var f in m.Faces)
        {
            var fn = m.FaceNormal(f);
            if (Vector3.Dot(fn, n) > -0.5f) continue; // only the back of the wall
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                float d = Picking.RayTriangle(ray, m.Verts[f.V[a]], m.Verts[f.V[b]], m.Verts[f.V[c]]);
                if (d > 1e-4f && d < best) best = d;
            }
        }
        return best < float.MaxValue ? best + 1e-3f : 0;
    }

    /// <summary>Single surface (MLO room box): the hole goes in the face under the rectangle.</summary>
    static PolyMesh Surface(PolyMesh m, OpeningRect op)
    {
        if (op.Shape != OpeningShape.Rectangle) return SurfaceShape(m, op);
        var (right, up, n) = op.Axes();
        var c = op.At((op.Left + op.Right) * 0.5f, (op.Bottom + op.Top) * 0.5f);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            var fn = m.FaceNormal(f);
            if (Vector3.Dot(fn, n) < 0.999f || MathF.Abs(Vector3.Dot(m.Verts[f.V[0]] - c, fn)) > 1e-3f) continue;
            var o = m.FaceCenter(f);
            var pr = f.V.Select(v => new Vector2(Vector3.Dot(m.Verts[v] - o, right), Vector3.Dot(m.Verts[v] - o, up))).ToArray();
            float r0 = pr.Min(p => p.X), r1 = pr.Max(p => p.X), u0 = pr.Min(p => p.Y);
            var cc = new Vector2(Vector3.Dot(c - o, right), Vector3.Dot(c - o, up));
            if (cc.X < r0 || cc.X > r1 || cc.Y < u0) continue;
            var copy = m.Clone();
            float offset = cc.X; // Doorway.Cut measures from FaceCenter
            float sill = Vector3.Dot(op.At(0, op.Bottom) - o, up) - u0;
            var res = Doorway.Cut(copy, fi, op.Width, op.Height, offset, MathF.Max(0, sill));
            return res.Ok ? copy : m;
        }
        return m;
    }

    /// <summary>Single surface, any shape: every face on the wall plane loses the part inside the outline
    /// (each face cut into convex pieces around the hole, then pieces of the same face merged back).</summary>
    static PolyMesh SurfaceShape(PolyMesh m0, OpeningRect op)
    {
        var (right, up, n) = op.Axes();
        var hole = op.Outline();
        float hx0 = hole.Min(p => p.X), hx1 = hole.Max(p => p.X), hy0 = hole.Min(p => p.Y), hy1 = hole.Max(p => p.Y);
        Vector2 To2(Vector3 p) => new(Vector3.Dot(p - op.Origin, right), Vector3.Dot(p - op.Origin, up));
        var m = m0.Clone();
        var added = new HashSet<Face>(ReferenceEqualityComparer.Instance);
        var keep = new List<Face>(); bool any = false;
        foreach (var f in m.Faces)
        {
            var fn = m.FaceNormal(f);
            if (Vector3.Dot(fn, n) < 0.999f || MathF.Abs(Vector3.Dot(m.Verts[f.V[0]] - op.Origin, n)) > 1e-3f) { keep.Add(f); continue; }
            var p2 = f.V.Select(v => To2(m.Verts[v])).ToList();
            if (p2.Max(p => p.X) <= hx0 || p2.Min(p => p.X) >= hx1 || p2.Max(p => p.Y) <= hy0 || p2.Min(p => p.Y) >= hy1) { keep.Add(f); continue; }
            var uvAt = AffineUv(m, f, To2);
            var polys = new List<List<Vector2>>();
            if (IsConvex(p2)) polys.Add(p2);
            else foreach (var (a, b, c) in Triangulator.Triangulate(m, f)) polys.Add(new List<Vector2> { p2[a], p2[b], p2[c] });
            var pieces = new List<List<Vector2>>();
            foreach (var poly in polys)
            {
                var rest = poly;
                for (int i = 0; i < hole.Count && rest.Count >= 3; i++)
                {
                    var a = hole[i]; var b = hole[(i + 1) % hole.Count];
                    var outside = Clip(rest, a, b, false);
                    if (Area(outside) > 1e-7f) pieces.Add(outside);
                    rest = Clip(rest, a, b, true);
                }
                if (Area(rest) < 1e-7f) continue; // whole polygon outside... handled below
            }
            any = true;
            foreach (var pc in pieces)
            {
                var idx = pc.Select(q => m.AddVertex(op.Origin + right * q.X + up * q.Y)).ToArray();
                var nf = new Face(idx, f.MatId, f.Smooth) { UV = pc.Select(uvAt).ToArray(), Col = Enumerable.Repeat(f.Col[0], pc.Count).ToArray() };
                if (f.Col1 != null) nf.Col1 = Enumerable.Repeat(f.Col1[0], pc.Count).ToArray();
                keep.Add(nf); added.Add(nf);
            }
        }
        if (!any) return m0;
        m.Faces = keep;
        MeshOps.WeldByDistance(m, 1e-5f);
        Csg.FixTJunctions(m, 1e-5f);
        Csg.MergeCoplanar(m, f => added.Contains(f));
        CsgClean.DissolveCollinear(m);
        m.RemoveUnusedVertices();
        return m;
    }

    static List<Vector2> Clip(List<Vector2> poly, Vector2 a, Vector2 b, bool keepLeft)
    {
        float Side(Vector2 p) { float s = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X); return keepLeft ? s : -s; }
        var res = new List<Vector2>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count];
            float sp = Side(p), sq = Side(q);
            if (sp >= 0) res.Add(p);
            if ((sp > 0 && sq < 0) || (sp < 0 && sq > 0)) res.Add(p + (q - p) * (sp / (sp - sq)));
        }
        // drop repeated points
        var clean = new List<Vector2>();
        foreach (var p in res) if (clean.Count == 0 || Vector2.DistanceSquared(clean[^1], p) > 1e-12f) clean.Add(p);
        if (clean.Count > 1 && Vector2.DistanceSquared(clean[0], clean[^1]) <= 1e-12f) clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    static float Area(List<Vector2> p)
    {
        if (p.Count < 3) return 0;
        float a = 0; for (int i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a * 0.5f;
    }

    static bool IsConvex(List<Vector2> p)
    {
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count]; var c = p[(i + 2) % p.Count];
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < -1e-9f) return false;
        }
        return Area(p) > 0;
    }

    /// <summary>The face's UVs as an affine map of the wall plane (exact for flat UVs).</summary>
    static Func<Vector2, Vector2> AffineUv(PolyMesh m, Face f, Func<Vector3, Vector2> to2)
    {
        var p = f.V.Select(v => to2(m.Verts[v])).ToArray();
        for (int i = 1; i + 1 < p.Length; i++)
        {
            var e1 = p[i] - p[0]; var e2 = p[i + 1] - p[0];
            float det = e1.X * e2.Y - e1.Y * e2.X;
            if (MathF.Abs(det) < 1e-9f) continue;
            Vector2 u0 = f.UV[0], du1 = f.UV[i] - u0, du2 = f.UV[i + 1] - u0;
            return q =>
            {
                var d = q - p[0];
                float s = (d.X * e2.Y - d.Y * e2.X) / det, t = (e1.X * d.Y - e1.Y * d.X) / det;
                return u0 + du1 * s + du2 * t;
            };
        }
        return _ => f.UV[0];
    }
}
