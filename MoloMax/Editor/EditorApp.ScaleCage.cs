using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Scale box (the Scale Cage of Blender): with the Scale tool, a box is drawn around the selection with a handle on each
/// side, edge and corner. Dragging a handle stretches the selection from the opposite side, only along the axes of that
/// handle (a side: one axis, an edge: two, a corner: three). Shift keeps the proportions, snapping rounds the size to the grid.
/// Works on objects and on vertices / edges / polygons in edit mode.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>Show the scale box with the Scale tool (toolbar "Box").</summary>
    public bool ScaleCage = true;

    struct CageBox
    {
        public Vector3 Centre; public Vector3[] Axes; public Vector3 Half;
        public Vector3 Point(Vector3 d) => Centre + Axes[0] * (d.X * Half.X) + Axes[1] * (d.Y * Half.Y) + Axes[2] * (d.Z * Half.Z);
    }

    static readonly Vector3[] CageDirs = BuildCageDirs();
    static Vector3[] BuildCageDirs()
    {
        var l = new List<Vector3>();
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            if (x != 0 || y != 0 || z != 0) l.Add(new Vector3(x, y, z));
        // sides first (they win a tie), then edges, then corners
        return l.OrderBy(d => MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z)).ToArray();
    }

    int cageHover = -1;
    bool cageDrag;
    Vector3 cageDir, cageAnchor, cageN, cageHalf0;
    Vector3[] cageAxes;
    float cageLen, cageT0;
    readonly Dictionary<int, (int Version, Vector3 Min, Vector3 Max)> cageBoundsCache = new();

    bool CageOn => ScaleCage && Tool == Tool.Scale && !ModalActive && !SculptMode && !paintMode && Selected.Count > 0;

    (Vector3 Min, Vector3 Max) CageMeshBounds(SceneObject o)
    {
        if (cageBoundsCache.TryGetValue(o.Id, out var c) && c.Version == o.Version) return (c.Min, c.Max);
        var b = o.Mesh.Verts.Count > 0 ? o.Mesh.Bounds() : (Vector3.Zero, Vector3.Zero);
        cageBoundsCache[o.Id] = (o.Version, b.Item1, b.Item2);
        return b;
    }

    /// <summary>The box around the selection, in the object's own axes (one object or edit mode) or the world's.</summary>
    bool SelectionCage(out CageBox box)
    {
        box = default;
        var pts = new List<Vector3>();
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var cur = Current;
        if (Level != SubObject.Object)
        {
            if (cur?.Mesh == null) return false;
            var w = cur.World;
            foreach (var v in SubSel(cur).VerticesOf(cur.Mesh, Level))
                if (v < cur.Mesh.Verts.Count) pts.Add(Vector3.Transform(cur.Mesh.Verts[v], w));
            if (pts.Count < 2) return false;
            axes = RotAxes(cur.Rotation);
        }
        else
        {
            var objs = Selected.Select(Scene.Find).Where(o => o != null && !o.Hidden).ToList();
            if (objs.Count == 0) return false;
            if (objs.Count == 1) axes = RotAxes(objs[0].Rotation);
            foreach (var o in objs)
            {
                if (o.Mesh == null || o.Mesh.Verts.Count == 0) { pts.Add(o.Position); continue; }
                var (mn, mx) = CageMeshBounds(o);
                var w = o.World;
                for (int i = 0; i < 8; i++)
                    pts.Add(Vector3.Transform(new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z), w));
            }
        }
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in pts)
        {
            var l = new Vector3(Vector3.Dot(p, axes[0]), Vector3.Dot(p, axes[1]), Vector3.Dot(p, axes[2]));
            lo = Vector3.Min(lo, l); hi = Vector3.Max(hi, l);
        }
        var mid = (lo + hi) / 2;
        box.Axes = axes;
        box.Centre = axes[0] * mid.X + axes[1] * mid.Y + axes[2] * mid.Z;
        box.Half = (hi - lo) / 2;
        return true;
    }

    static Vector3[] RotAxes(Quaternion q) => new[]
    {
        Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q)),
        Vector3.Normalize(Vector3.Transform(Vector3.UnitY, q)),
        Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q)),
    };

    /// <summary>A handle is usable when the box has a size along every axis it moves.</summary>
    static bool CageUsable(CageBox b, Vector3 d)
        => (d.X == 0 || b.Half.X > 1e-5f) && (d.Y == 0 || b.Half.Y > 1e-5f) && (d.Z == 0 || b.Half.Z > 1e-5f);

    bool CageScreen(Viewport vp, Vector3 world, out Vector2 s)
    {
        bool ok = vp.Camera.Project(world, vp.W, vp.H, out var l, out float depth);
        s = new Vector2(vp.X + l.X, vp.Y + l.Y);
        return ok && depth > 0;
    }

    /// <summary>The handle under the mouse (index in <see cref="CageDirs"/>), or -1.</summary>
    int CageHit(Viewport vp, Vector2 mouse)
    {
        if (!CageOn || !SelectionCage(out var b)) return -1;
        int best = -1; float bd = 9f;
        for (int i = 0; i < CageDirs.Length; i++)
        {
            if (!CageUsable(b, CageDirs[i]) || !CageScreen(vp, b.Point(CageDirs[i]), out var s)) continue;
            float d = Vector2.Distance(s, mouse);
            if (d < bd - 0.5f) { bd = d; best = i; }
        }
        return best;
    }

    void BeginCage(Viewport vp, int handle)
    {
        if (!SelectionCage(out var b)) return;
        BeginTransform(vp, GizmoHandle.View);
        if (!transforming) return;
        cageDrag = true;
        cageDir = CageDirs[handle];
        cageAxes = b.Axes; cageHalf0 = b.Half;
        cageAnchor = b.Point(-cageDir);
        var h = b.Point(cageDir);
        cageLen = Vector3.Distance(h, cageAnchor);
        cageN = cageLen > 1e-6f ? (h - cageAnchor) / cageLen : b.Axes[0];
        cageT0 = Picking.RayLineParam(RayAt(vp, Input.Mouse), cageAnchor, cageN);
        xfPivot = xfPivotLive = cageAnchor;
    }

    void UpdateCage()
    {
        var vp = xfViewport;
        float t = Picking.RayLineParam(RayAt(vp, Input.Mouse), cageAnchor, cageN);
        float f = MathF.Max(0.001f, (cageLen + (t - cageT0)) / MathF.Max(cageLen, 1e-6f));
        bool uniform = Input.Shift;
        var fv = new Vector3(
            uniform || cageDir.X != 0 ? f : 1,
            uniform || cageDir.Y != 0 ? f : 1,
            uniform || cageDir.Z != 0 ? f : 1);
        if (SnapActive && GridStep > 0)
        {
            // snapping: the new size is a whole number of grid steps
            float S(float k, float half)
            {
                float size = half * 2;
                return k == 1 || size < 1e-6f ? k : MathF.Max(GridStep, SnapValue(size * k, GridStep)) / size;
            }
            fv = new Vector3(S(fv.X, cageHalf0.X), S(fv.Y, cageHalf0.Y), S(fv.Z, cageHalf0.Z));
        }
        var A = cageAnchor; var ax = cageAxes;
        Vector3 Map(Vector3 p)
        {
            var d = p - A;
            return A + ax[0] * (Vector3.Dot(d, ax[0]) * fv.X) + ax[1] * (Vector3.Dot(d, ax[1]) * fv.Y) + ax[2] * (Vector3.Dot(d, ax[2]) * fv.Z);
        }
        ApplyLive(Map, (pos, rot, sc) =>
        {
            // the object's own axes stretched by the box (exact when they line up with the box)
            var r = RotAxes(rot);
            var k = new Vector3(Stretch(r[0]), Stretch(r[1]), Stretch(r[2]));
            return (Map(pos), rot, sc * k);
        });
        float Stretch(Vector3 a) => (ax[0] * (Vector3.Dot(a, ax[0]) * fv.X) + ax[1] * (Vector3.Dot(a, ax[1]) * fv.Y) + ax[2] * (Vector3.Dot(a, ax[2]) * fv.Z)).Length();
        var size2 = cageHalf0 * 2 * fv;
        SetStatus($"Scale box  {Fmt(size2.X)} x {Fmt(size2.Y)} x {Fmt(size2.Z)} m  ({Pct(fv)})   Shift: keep proportions");
    }

    static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Pct(Vector3 fv)
    {
        var parts = new List<string>();
        if (fv.X != 1) parts.Add("X " + (fv.X * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%");
        if (fv.Y != 1) parts.Add("Y " + (fv.Y * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%");
        if (fv.Z != 1) parts.Add("Z " + (fv.Z * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%");
        return parts.Count == 0 ? "100%" : string.Join(", ", parts);
    }

    void EndCage() => cageDrag = false;

    void DrawCage(ImDrawListPtr dl, Viewport vp)
    {
        if (!CageOn || !SelectionCage(out var b)) return;
        uint line = 0xC0F0B060, handle = 0xFFF0B060, hot = ImGui.ColorConvertFloat4ToU32(Theme.Gold);
        // the 12 edges of the box
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
            foreach (int bit in new[] { 1, 2, 4 })
            {
                if ((i & bit) != 0) continue;
                var c2 = c; if (bit == 1) c2.X = 1; else if (bit == 2) c2.Y = 1; else c2.Z = 1;
                if (CageScreen(vp, b.Point(c), out var s0) && CageScreen(vp, b.Point(c2), out var s1)) dl.AddLine(s0, s1, line, 1.2f);
            }
        }
        int active = cageDrag ? Array.IndexOf(CageDirs, cageDir) : cageHover;
        for (int i = 0; i < CageDirs.Length; i++)
        {
            var d = CageDirs[i];
            if (!CageUsable(b, d) || !CageScreen(vp, b.Point(d), out var s)) continue;
            int n = (int)(MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z));
            float r = n == 1 ? 5f : n == 2 ? 3.5f : 4.5f;
            uint col = i == active ? hot : handle;
            if (i == active) r += 1.5f;
            if (n == 1) dl.AddCircleFilled(s, r, col);
            else dl.AddRectFilled(s - new Vector2(r), s + new Vector2(r), col);
            dl.AddRect(s - new Vector2(r + 1), s + new Vector2(r + 1), 0xFF202020, 0, ImDrawFlags.None, 1f);
        }
        // the size of the box next to the handle in use
        if (active >= 0 && CageScreen(vp, b.Point(CageDirs[active]), out var ts))
        {
            var sz = b.Half * 2;
            string txt = $"{Fmt(sz.X)} x {Fmt(sz.Y)} x {Fmt(sz.Z)} m";
            var p = ts + new Vector2(12, -22);
            var tw = ImGui.CalcTextSize(txt);
            dl.AddRectFilled(p - new Vector2(4, 2), p + tw + new Vector2(4, 2), 0xD0101010, 3);
            dl.AddText(p, 0xFFFFFFFF, txt);
        }
    }
}
