using System.Numerics;
using MoloMax.Core.Tools;

namespace MoloMax.Core.Mesh;

/// <summary>How a line (3ds Max spline) shows: the bare line, a renderable section swept along it (rectangle, round),
/// a door / window casing profile swept along it, or an extrusion of the shape.</summary>
public enum ShapeRender { Line, Rectangular, Round, DoorFrame, Extrude, Sweep, Lathe }

/// <summary>
/// A line drawn point by point (3ds Max Create > Shapes > Line): its points in object space, open or closed, and how it
/// becomes a mesh. The object's mesh is rebuilt from it until "Convert to Editable Poly".
/// </summary>
public sealed class ShapeData
{
    public List<Vector3> Points = new();
    public bool Closed;
    public ShapeRender Render = ShapeRender.Line;
    /// <summary>Rectangular: width across the line (in the shape's plane) and depth out of the plane, metres.</summary>
    public float Width = 0.1f, Depth = 0.05f;
    /// <summary>Round: diameter and sides.</summary>
    public float Thickness = 0.05f; public int Sides = 8;
    /// <summary>Door frame: casing style (0 square, 1 stepped, 2 chamfer, 3 round), width (away from the opening), depth (off the wall).</summary>
    public int FrameStyle = 1; public float FrameWidth = 0.12f, FrameDepth = 0.035f;
    /// <summary>Extrude: amount along the shape's plane normal (a closed shape gets caps).</summary>
    public float Amount = 0.2f;
    /// <summary>Puts the section on the other side of the line / the other face of the plane.</summary>
    public bool Flip;
    /// <summary>The side the section stands out to (door frame / beam / extrude), set from the view when it is drawn:
    /// the plane normal is turned toward it, so a casing drawn on a wall sticks out of the wall, toward the viewer.</summary>
    public Vector3 Facing;
    /// <summary>Rounds every corner of the line with this radius (metres, 0 = sharp); FilletSegs = 1 cuts a chamfer.</summary>
    public float Fillet; public int FilletSegs = 4;
    /// <summary>Sweep: the section drawn as another line (x across this line, y out of its plane, metres; its first
    /// point rides on the line), and its scale.</summary>
    public List<Vector2> Profile = new(); public float ProfileScale = 1f; public string ProfileName;
    /// <summary>3ds Max shape type: 0 line (its own points), 1 rectangle, 2 circle, 3 arc. Rectangle / circle / arc keep
    /// their sizes editable and make their points from them (in the shape's plane: X right, Y up of that plane).</summary>
    public int Kind;
    public float RectWidth = 1, RectLength = 1, Radius = 0.5f, ArcFrom = 0, ArcTo = 180;
    /// <summary>Smooth: the line passes through its points as a curve (3ds Max "Smooth" vertices), Steps points per segment.</summary>
    public bool Smooth; public int Steps = 6;
    /// <summary>Lathe: turns the line around an axis through the shape's origin (0 X, 1 Y, 2 Z of the object).</summary>
    public int LatheAxis = 2, LatheSegs = 16; public float LatheDegrees = 360;

    public ShapeData Copy()
    {
        var c = (ShapeData)MemberwiseClone();
        c.Points = new List<Vector3>(Points);
        c.Profile = new List<Vector2>(Profile ?? new());
        return c;
    }
}

public static class ShapeBuilder
{
    /// <summary>Normal of the plane the points lie in (Newell); a straight line takes a horizontal side, so a vertical
    /// line gets a normal along Y or X and a horizontal one stands up (Z).</summary>
    public static Vector3 PlaneNormal(IReadOnlyList<Vector3> p, bool closed)
    {
        var n = Vector3.Zero;
        int cnt = p.Count;
        for (int i = 0; i < cnt; i++) { var a = p[i]; var b = p[(i + 1) % cnt]; n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        if (n.LengthSquared() > 1e-10f) return Vector3.Normalize(n);
        // collinear: any normal perpendicular to the line, preferably Z (a line on the floor) else horizontal
        var d = cnt >= 2 ? p[^1] - p[0] : Vector3.UnitX;
        if (d.LengthSquared() < 1e-12f) return Vector3.UnitZ;
        d = Vector3.Normalize(d);
        if (MathF.Abs(d.Z) < 0.9f) return Vector3.UnitZ;
        var side = Vector3.Normalize(Vector3.Cross(d, Vector3.UnitX));
        return side;
    }

    /// <summary>The mesh of a shape (object space). The bare line keeps only its points (they still snap).</summary>
    public static PolyMesh Build(ShapeData s)
    {
        Generate(s);
        var raw = Clean(s.Points, s.Closed);
        var m = new PolyMesh();
        if (raw.Count < 2 || s.Render == ShapeRender.Line) { foreach (var p in raw) m.AddVertex(p); return m; }
        bool closed = s.Closed && raw.Count >= 3;
        var pts = Path(s);
        var n = PlaneNormal(pts, closed);
        if (s.Facing.LengthSquared() > 1e-8f && Vector3.Dot(n, s.Facing) < 0) n = -n;
        if (s.Flip && s.Render is ShapeRender.Extrude) n = -n;
        switch (s.Render)
        {
            case ShapeRender.Rectangular:
                {
                    float w = MathF.Max(s.Width, 1e-4f) / 2, d = MathF.Max(s.Depth, 1e-4f) / 2;
                    Sweep(m, pts, closed, n, new List<Vector2> { new(-w, -d), new(w, -d), new(w, d), new(-w, d) }, outward: false, s.Flip);
                    break;
                }
            case ShapeRender.Round:
                {
                    int k = Math.Clamp(s.Sides, 3, 64); float r = MathF.Max(s.Thickness, 1e-4f) / 2;
                    var prof = Enumerable.Range(0, k).Select(i => new Vector2(MathF.Cos(MathF.Tau * i / k) * r, MathF.Sin(MathF.Tau * i / k) * r)).ToList();
                    Sweep(m, pts, closed, n, prof, outward: false, s.Flip);
                    break;
                }
            case ShapeRender.DoorFrame:
                {
                    var ts = CasingSettings(s);
                    // the trim profile (out, up): "up" goes away from the opening (across the line, outward), "out" off the wall
                    var prof = TrimSweep.Profile(ts).Select(q => new Vector2(q.Y, q.X)).ToList();
                    Sweep(m, pts, closed, n, prof, outward: true, s.Flip);
                    break;
                }
            case ShapeRender.Extrude: Extrude(m, pts, closed, n * s.Amount); break;
            case ShapeRender.Lathe: Lathe(m, pts, closed, s); break;
            case ShapeRender.Sweep:
                {
                    var prof = (s.Profile ?? new()).Select(q => q * s.ProfileScale).ToList();
                    if (prof.Count < 3) { float w = 0.05f; prof = new List<Vector2> { new(-w, -w), new(w, -w), new(w, w), new(-w, w) }; }
                    Sweep(m, pts, closed, n, prof, outward: true, s.Flip);
                    break;
                }
        }
        MeshOps.RecalcNormals(m, null, false);
        foreach (var f in m.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new Vector4(0, 1, 0, 1); // GTA green
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }

    /// <summary>Rectangle / circle / arc: their points from their sizes (lines keep theirs).</summary>
    public static void Generate(ShapeData s)
    {
        if (s.Kind == 0) return;
        var p = new List<Vector3>();
        if (s.Kind == 1)
        {
            float w = MathF.Max(s.RectWidth, 1e-4f) / 2, l = MathF.Max(s.RectLength, 1e-4f) / 2;
            p.AddRange(new[] { new Vector3(-w, -l, 0), new Vector3(w, -l, 0), new Vector3(w, l, 0), new Vector3(-w, l, 0) });
            s.Closed = true;
        }
        else
        {
            float r = MathF.Max(s.Radius, 1e-4f);
            bool arc = s.Kind == 3;
            float a0 = arc ? s.ArcFrom : 0, a1 = arc ? s.ArcTo : 360;
            int segs = Math.Clamp((int)MathF.Ceiling(MathF.Abs(a1 - a0) / 360f * Math.Max(8, s.Sides * 4)), 3, 256);
            int n = arc ? segs + 1 : segs;
            for (int i = 0; i < n; i++)
            {
                float a = (a0 + (a1 - a0) * i / segs) * MathF.PI / 180;
                p.Add(new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0));
            }
            s.Closed = !arc;
        }
        s.Points = p;
    }

    /// <summary>Catmull-Rom curve through the points (3ds Max smooth vertices), steps points per segment.</summary>
    static List<Vector3> SmoothCurve(List<Vector3> p, bool closed, int steps)
    {
        int n = p.Count; steps = Math.Clamp(steps, 1, 64);
        if (n < 3) return p;
        var r = new List<Vector3>();
        int segs = closed ? n : n - 1;
        Vector3 P(int i) => closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        for (int i = 0; i < segs; i++)
        {
            var p0 = P(i - 1); var p1 = P(i); var p2 = P(i + 1); var p3 = P(i + 2);
            for (int k = 0; k < steps; k++)
            {
                float t = k / (float)steps, t2 = t * t, t3 = t2 * t;
                r.Add(0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
            }
        }
        if (!closed) r.Add(p[^1]);
        return r;
    }

    /// <summary>3ds Max Lathe: the line turned around the axis (through the shape's origin), closed caps where it touches the axis.</summary>
    static void Lathe(PolyMesh m, List<Vector3> pts, bool closed, ShapeData s)
    {
        var axis = s.LatheAxis == 0 ? Vector3.UnitX : s.LatheAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        int segs = Math.Clamp(s.LatheSegs, 3, 128);
        float deg = Math.Clamp(s.LatheDegrees, 1, 360);
        bool full = deg >= 359.999f;
        int rings = full ? segs : segs + 1;
        int np = pts.Count;
        var idx = new int[rings, np];
        for (int j = 0; j < np; j++)
        {
            var p = pts[j];
            float along = Vector3.Dot(p, axis); var radial = p - axis * along;
            bool onAxis = radial.Length() < 1e-5f;
            int shared = onAxis ? m.AddVertex(axis * along) : -1;
            for (int k = 0; k < rings; k++)
            {
                if (onAxis) { idx[k, j] = shared; continue; }
                var q = Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180 * k / segs);
                idx[k, j] = m.AddVertex(Vector3.Transform(p, q));
            }
        }
        int nseg = closed ? np : np - 1;
        for (int k = 0; k < segs; k++)
        {
            int k1 = full ? (k + 1) % rings : k + 1;
            for (int j = 0; j < nseg; j++)
            {
                int j1 = (j + 1) % np;
                var quad = new[] { idx[k, j], idx[k, j1], idx[k1, j1], idx[k1, j] };
                var f = quad.Distinct().ToArray();
                if (f.Length >= 3) m.AddFace(f);
            }
        }
        // faces outward: a full lathe encloses a volume, its sign tells the winding
        if (m.Faces.Count > 0)
        {
            double vol = 0;
            foreach (var f in m.Faces)
                for (int i = 1; i + 1 < f.V.Length; i++)
                    vol += Vector3.Dot(m.Verts[f.V[0]], Vector3.Cross(m.Verts[f.V[i]], m.Verts[f.V[i + 1]]));
            if ((vol < 0) != s.Flip) foreach (var f in m.Faces) f.Flip();
        }
    }

    /// <summary>The line as it is built: cleaned, with its corners rounded (or chamfered) by Fillet.</summary>
    public static List<Vector3> Path(ShapeData s)
    {
        Generate(s);
        var pts = Clean(s.Points, s.Closed);
        bool closed = s.Closed && pts.Count >= 3;
        if (s.Smooth && pts.Count >= 3) pts = SmoothCurve(pts, closed, s.Steps);
        if (s.Fillet <= 1e-5f || pts.Count < 3) return pts;
        int np = pts.Count, segs = Math.Clamp(s.FilletSegs, 1, 32);
        var r = new List<Vector3>();
        for (int i = 0; i < np; i++)
        {
            bool end = !closed && (i == 0 || i == np - 1);
            var p = pts[i];
            if (end) { r.Add(p); continue; }
            var a = pts[(i - 1 + np) % np]; var b = pts[(i + 1) % np];
            float la = Vector3.Distance(a, p), lb = Vector3.Distance(b, p);
            var d1 = (a - p) / la; var d2 = (b - p) / lb;
            float cos = Math.Clamp(Vector3.Dot(d1, d2), -1, 1);
            float th = MathF.Acos(cos);
            if (th < 1e-3f || th > MathF.PI - 1e-3f) { r.Add(p); continue; } // folded back or straight: no corner to round
            float half = th / 2;
            // distance from the corner to the tangent points, at most half of each side (two fillets meet in the middle)
            float t = MathF.Min(s.Fillet / MathF.Tan(half), MathF.Min(la, lb) * 0.5f);
            float rr = t * MathF.Tan(half);
            var t1 = p + d1 * t; var t2 = p + d2 * t;
            var c = p + Vector3.Normalize(d1 + d2) * (rr / MathF.Sin(half));
            var u1 = t1 - c; var u2 = t2 - c;
            float phi = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(u1), Vector3.Normalize(u2)), -1, 1));
            for (int k = 0; k <= segs; k++)
            {
                float f = (float)k / segs;
                if (segs == 1 || phi < 1e-4f) { r.Add(Vector3.Lerp(t1, t2, f)); continue; }
                float sa = MathF.Sin((1 - f) * phi) / MathF.Sin(phi), sb = MathF.Sin(f * phi) / MathF.Sin(phi);
                r.Add(c + u1 * sa + u2 * sb);
            }
        }
        return Clean(r, closed);
    }

    /// <summary>A line drawn as a profile: its points in 2D in its own plane, relative to its first point
    /// (x along the horizontal of that plane, y up it).</summary>
    public static List<Vector2> ProfileFrom(IReadOnlyList<Vector3> world)
    {
        var r = new List<Vector2>();
        if (world.Count < 2) return r;
        var n = PlaneNormal(world, true);
        var u = Vector3.Cross(Vector3.UnitZ, n);
        u = u.LengthSquared() > 1e-6f ? Vector3.Normalize(u) : Vector3.UnitX;
        var v = Vector3.Normalize(Vector3.Cross(n, u));
        if (v.Z < -1e-4f || MathF.Abs(v.Z) <= 1e-4f && v.Y < 0) { v = -v; u = -u; }
        var o = world[0];
        foreach (var p in world) r.Add(new Vector2(Vector3.Dot(p - o, u), Vector3.Dot(p - o, v)));
        return r;
    }

    public static TrimSettings CasingSettings(ShapeData s)
    {
        var t = TrimSettings.Frame(TrimKind.DoorFrame);
        t.Height = MathF.Max(s.FrameWidth, 0.01f); t.Depth = MathF.Max(s.FrameDepth, 0.002f); t.CapEnds = true;
        switch (s.FrameStyle)
        {
            case 0: t.Inner = TrimEdge.Square; t.Outer = TrimEdge.Square; break;
            case 2: t.Inner = TrimEdge.Chamfer; t.Outer = TrimEdge.Chamfer; t.InnerAmount = 0.5f; t.OuterAmount = 0.5f; break;
            case 3: t.Inner = TrimEdge.Round; t.Outer = TrimEdge.Round; t.InnerAmount = 0.6f; t.OuterAmount = 0.6f; t.Segments = 4; break;
            default: break; // stepped (the frame preset)
        }
        return t;
    }

    static List<Vector3> Clean(List<Vector3> src, bool closed)
    {
        var r = new List<Vector3>();
        foreach (var p in src) if (r.Count == 0 || Vector3.Distance(r[^1], p) > 1e-5f) r.Add(p);
        if (closed && r.Count > 2 && Vector3.Distance(r[0], r[^1]) < 1e-5f) r.RemoveAt(r.Count - 1);
        return r;
    }

    /// <summary>
    /// Sweeps a closed 2D section (x across the line in the shape's plane, y along the plane normal) along the points, with
    /// mitred corners. <paramref name="outward"/>: x grows away from the middle of the shape (a casing grows away from its
    /// opening); else x is centred on the line.
    /// </summary>
    static void Sweep(PolyMesh m, List<Vector3> pts, bool closed, Vector3 n, List<Vector2> prof, bool outward, bool flip)
    {
        int np = pts.Count, ns = closed ? np : np - 1;
        var segSide = new Vector3[ns];
        for (int k = 0; k < ns; k++)
        {
            var d = Vector3.Normalize(pts[(k + 1) % np] - pts[k]);
            var sd = Vector3.Cross(n, d);
            segSide[k] = sd.LengthSquared() > 1e-10f ? Vector3.Normalize(sd) : Vector3.UnitX;
        }
        // which way is "away from the middle": vote over the segments
        float sign = 1;
        if (outward)
        {
            var c = Vector3.Zero; foreach (var p in pts) c += p; c /= np;
            float vote = 0;
            for (int k = 0; k < ns; k++) { var mid = (pts[k] + pts[(k + 1) % np]) * 0.5f; vote += Vector3.Dot(segSide[k], mid - c) * Vector3.Distance(pts[k], pts[(k + 1) % np]); }
            sign = vote >= 0 ? 1 : -1;
        }
        if (flip) sign = -sign;
        // a side per point: the mitre of the two segments around it, scaled so the section keeps its width
        var side = new Vector3[np];
        for (int i = 0; i < np; i++)
        {
            bool hasPrev = closed || i > 0, hasNext = closed || i < np - 1;
            Vector3 a = hasPrev ? segSide[(i - 1 + ns) % ns] : segSide[i], b = hasNext ? segSide[i % ns] : a;
            var mtr = a + b;
            if (mtr.LengthSquared() < 1e-8f) mtr = b;
            mtr = Vector3.Normalize(mtr);
            float cos = MathF.Max(Vector3.Dot(mtr, b), 0.25f); // limit very sharp corners
            side[i] = mtr / cos * sign;
        }
        int nq = prof.Count, rings = np;
        var idx = new int[rings, nq];
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < nq; j++)
                idx[i, j] = m.AddVertex(pts[i] + side[i] * prof[j].X + n * prof[j].Y);
        int last = closed ? rings : rings - 1;
        for (int i = 0; i < last; i++)
        {
            int i2 = (i + 1) % rings;
            for (int j = 0; j < nq; j++)
            {
                int j2 = (j + 1) % nq;
                m.AddFace(new[] { idx[i, j], idx[i2, j], idx[i2, j2], idx[i, j2] });
            }
        }
        if (!closed)
        {
            m.AddFace(Enumerable.Range(0, nq).Select(j => idx[0, j]).Reverse().ToArray());
            m.AddFace(Enumerable.Range(0, nq).Select(j => idx[rings - 1, j]).ToArray());
        }
    }

    /// <summary>Extrusion: a closed shape becomes a solid (two caps and the sides), an open one a strip (a wall).</summary>
    static void Extrude(PolyMesh m, List<Vector3> pts, bool closed, Vector3 dir)
    {
        int np = pts.Count;
        var bot = pts.Select(p => m.AddVertex(p)).ToArray();
        var top = pts.Select(p => m.AddVertex(p + dir)).ToArray();
        int last = closed ? np : np - 1;
        for (int i = 0; i < last; i++)
        {
            int i2 = (i + 1) % np;
            m.AddFace(new[] { bot[i], bot[i2], top[i2], top[i] });
        }
        if (closed)
        {
            m.AddFace(bot.Reverse().ToArray());
            m.AddFace(top.ToArray());
        }
    }
}
