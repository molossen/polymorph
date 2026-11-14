using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Line tool (Create > Shapes > Line in the classic modeller): click points one by one, each snapping to the vertices,
/// edges and faces under the mouse (also of the selected objects), Shift keeps the segment straight along X / Y / Z.
/// Click the first point to close, right click / Enter to finish open, Backspace removes the last point, Esc cancels.
/// The line stays a shape: its panel gives it a section (rectangular, round), a door frame casing or an extrusion,
/// rebuilt live until "Convert to Editable Poly".
/// </summary>
public partial class EditorApp
{
    readonly List<Vector3> linePts = new();
    Vector3 lineCursor; bool lineCursorOk; string lineSnapKind;
    Vector3? linePlaneP, linePlaneN;
    /// <summary>What the next line becomes (Create panel).</summary>
    public ShapeRender NextShapeRender = ShapeRender.Line;

    public void BeginLine()
    {
        if (modal.Kind != ModalKind.None || transforming) return;
        if (Level != SubObject.Object) Level = SubObject.Object;
        var vp = Hovered() ?? Viewports[ActiveViewport];
        StartModalCommon(ModalKind.Line, vp, Vector3.Zero);
        linePts.Clear(); linePlaneP = null; linePlaneN = null; lineCursorOk = false;
        xfObjects.Clear(); xfVerts.Clear(); // nothing moves: every object (the selected ones too) is a snap target
        modal.Header = "Line: click the first point (snaps to vertices, edges, faces)";
        SetStatus("Line: click points (snap to vertex / edge / face, Alt = free) · Shift = straight · click the first point to close · RMB / Enter finish · Backspace removes the last point · Esc cancels");
    }

    /// <summary>Line point under the mouse: a snapped vertex / edge / face, one of the line's own points, the plane of
    /// the first surface clicked, else the construction grid. Shift keeps it straight from the previous point.</summary>
    bool LinePoint(Viewport vp, out Vector3 p)
    {
        p = default; lineSnapKind = null;
        var local = vp.Local(Input.Mouse);
        bool got = false;
        // the line's own points first (closing, going back to a corner)
        float best = 10f;
        foreach (var q in linePts)
            if (vp.Camera.Project(q, vp.W, vp.H, out var sq, out _) && Vector2.Distance(sq, local) < best) { best = Vector2.Distance(sq, local); p = q; got = true; lineSnapKind = "vertex"; snapMarker = q; snapMarkerKind = "vertex"; }
        if (!got && !Input.Alt)
        {
            bool sv = SnapVertexT, se = SnapEdge, sf = SnapFace, sn = SnapNearest;
            if (!(sv || se || sf || sn)) { SnapVertexT = true; SnapEdge = true; SnapFace = true; } // the line always snaps
            try { got = SnapPoint(vp, out p); }
            finally { SnapVertexT = sv; SnapEdge = se; SnapFace = sf; SnapNearest = sn; }
            if (got) lineSnapKind = snapMarkerKind;
            // other lines: their segments and points (they have no faces to snap on)
            if (ShapeSnap(vp, local, ref best, out var sp, out var kind) && (!got || lineSnapKind == "face")) { p = sp; got = true; lineSnapKind = kind; snapMarker = sp; snapMarkerKind = kind; }
            if (got && lineSnapKind == "face" && linePts.Count == 0 && snapFaceNormal is { } fn) { linePlaneP = p; linePlaneN = fn; }
        }
        if (!got)
        {
            snapMarker = null;
            var ray = RayAt(vp, Input.Mouse);
            if (linePlaneP is { } pp && linePlaneN is { } pn && Picking.RayPlane(ray, pp, pn, out var onPlane)) { p = onPlane; got = true; }
            else if (GridPoint(vp, Input.Mouse, out var g)) { p = g; got = true; }
        }
        if (got && Input.Shift && linePts.Count > 0)
        {
            var last = linePts[^1]; var d = p - last; var a = Vector3.Abs(d);
            p = a.X >= a.Y && a.X >= a.Z ? last + new Vector3(d.X, 0, 0) : a.Y >= a.Z ? last + new Vector3(0, d.Y, 0) : last + new Vector3(0, 0, d.Z);
        }
        return got;
    }

    /// <summary>Snap on the points and segments of the line objects (shapes drawn as bare lines).</summary>
    bool ShapeSnap(Viewport vp, Vector2 local, ref float best, out Vector3 pos, out string kind)
    {
        pos = default; kind = null; bool found = false; float bestE = MathF.Min(best, 8f);
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Shape == null || o.Mesh.Faces.Count > 0) continue;
            var pts = ShapeWorldPoints(o);
            int n = pts.Count; if (n == 0) continue;
            var sp = new Vector2[n]; var ok = new bool[n];
            for (int i = 0; i < n; i++) ok[i] = vp.Camera.Project(pts[i], vp.W, vp.H, out sp[i], out _);
            for (int i = 0; i < n; i++)
                if (ok[i] && Vector2.Distance(sp[i], local) < best) { best = Vector2.Distance(sp[i], local); pos = pts[i]; kind = "vertex"; found = true; }
            if (found) continue;
            int segs = o.Shape.Closed && n > 2 ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n; if (!ok[i] || !ok[j]) continue;
                var ab = sp[j] - sp[i]; float l2 = ab.LengthSquared(); if (l2 < 1e-6f) continue;
                float t = Math.Clamp(Vector2.Dot(local - sp[i], ab) / l2, 0, 1);
                float d = Vector2.Distance(sp[i] + ab * t, local);
                if (d < bestE) { bestE = d; pos = Vector3.Lerp(pts[i], pts[j], t); kind = "edge"; found = true; }
            }
        }
        return found;
    }

    /// <summary>The shape's points in world space. A bare line keeps its points in the mesh vertices, so moving them in
    /// vertex mode reshapes the line (they are read back here).</summary>
    List<Vector3> ShapeWorldPoints(SceneObject o)
    {
        var s = o.Shape; var w = o.World;
        if (s.Render == ShapeRender.Line && o.Mesh.Faces.Count == 0 && o.Mesh.Verts.Count == s.Points.Count)
            for (int i = 0; i < s.Points.Count; i++) s.Points[i] = o.Mesh.Verts[i];
        return s.Points.Select(q => Vector3.Transform(q, w)).ToList();
    }

    void LineMouse(Viewport hv)
    {
        if (hv != null) lineCursorOk = LinePoint(hv, out lineCursor) || lineCursorOk;
        float len = LineLength();
        string snap = lineSnapKind != null ? $"   snap: {lineSnapKind}" : "";
        modal.Header = linePts.Count == 0 ? $"Line: click the first point{snap}" : $"Line: {linePts.Count} point(s), {len:0.000} m (segment {Vector3.Distance(linePts[^1], lineCursor):0.000} m){snap}   Shift straight  Alt free  RMB finish";
        if (Input.Pressed(MouseButtons.Left) && hv != null && !ImGui.GetIO().WantCaptureMouse && lineCursorOk)
        {
            if (linePts.Count > 2 && Vector3.Distance(linePts[0], lineCursor) < 1e-4f) { FinishLine(true); return; }
            bool same = linePts.Count > 0 && Vector3.Distance(linePts[^1], lineCursor) < 1e-4f;
            if (Input.DoubleClick && same && linePts.Count > 1) { FinishLine(false); return; }
            if (!same) linePts.Add(lineCursor);
        }
        else if (Input.Pressed(MouseButtons.Right) && hv != null && !ImGui.GetIO().WantCaptureMouse)
        {
            if (linePts.Count > 1) FinishLine(false); else CancelModal();
        }
    }

    float LineLength()
    {
        float l = 0;
        for (int i = 1; i < linePts.Count; i++) l += Vector3.Distance(linePts[i - 1], linePts[i]);
        if (linePts.Count > 0) l += Vector3.Distance(linePts[^1], lineCursor);
        return l;
    }

    /// <summary>Creates the line object from points in world space (also used by scripts and tests).</summary>
    public SceneObject CreateLine(IReadOnlyList<Vector3> worldPts, bool closed, ShapeRender render)
    {
        if (worldPts.Count < 2) { SetStatus("A line needs two points."); return null; }
        var origin = worldPts[0];
        var s = new ShapeData { Closed = closed && worldPts.Count > 2, Render = render };
        foreach (var q in worldPts) s.Points.Add(q - origin);
        // the section stands out toward the viewer (a casing drawn on a wall sticks out of it); a face clicked first wins
        var vp = Viewports[ActiveViewport];
        s.Facing = linePlaneN ?? (vp.Camera.Eye - origin is var e && e.LengthSquared() > 1e-8f ? Vector3.Normalize(e) : Vector3.UnitZ);
        var created = AddObject("Line", ShapeBuilder.Build(s), origin);
        if (created != null) { created.Shape = s; created.Touch(); }
        SetStatus($"Line: {worldPts.Count} points{(s.Closed ? ", closed" : "")}. Its panel (Properties) turns it into a door frame, a beam or an extrusion.");
        return created;
    }

    void FinishLine(bool closed)
    {
        var pts = new List<Vector3>(linePts);
        linePts.Clear();
        modal.Kind = ModalKind.None;
        snapMarker = null;
        if (pts.Count < 2) { SetStatus("A line needs two points."); return; }
        try { CreateLine(pts, closed, NextShapeRender); }
        catch (Exception e) { SetStatus("Line failed: " + e.Message); }
        linePlaneP = null; linePlaneN = null;
    }

    void LineKeys()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, false) && linePts.Count > 0) linePts.RemoveAt(linePts.Count - 1);
        if (ImGui.IsKeyPressed(ImGuiKey.C, false) && linePts.Count > 2) FinishLine(true); // C closes, like the classic modeller
    }

    void LinePreviewLines()
    {
        if (modal.Kind != ModalKind.Line) return;
        var col = new Vector4(1f, 0.85f, 0.3f, 1);
        for (int i = 1; i < linePts.Count; i++) lines.Line(linePts[i - 1], linePts[i], col);
        if (linePts.Count > 0 && lineCursorOk) lines.Line(linePts[^1], lineCursor, col * 0.8f);
    }

    void LineOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind != ModalKind.Line) return;
        uint gold = ImGui.ColorConvertFloat4ToU32(Theme.Gold);
        foreach (var q in linePts)
            if (vp.Camera.Project(q, vp.W, vp.H, out var s, out _)) { var c = vp.ToScreen(s); dl.AddRect(c - new Vector2(3), c + new Vector2(3), gold); }
        if (lineCursorOk && snapMarker == null && vp.Camera.Project(lineCursor, vp.W, vp.H, out var sc, out _))
        {
            var c = vp.ToScreen(sc);
            dl.AddLine(c - new Vector2(8, 0), c + new Vector2(8, 0), gold, 1.5f);
            dl.AddLine(c - new Vector2(0, 8), c + new Vector2(0, 8), gold, 1.5f);
        }
    }

    /// <summary>Every line object is drawn as its polyline (a bare line has no faces); white when selected.</summary>
    void ShapeLines()
    {
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Shape == null) continue;
            bool sel = Selected.Contains(o.Id);
            if (o.Shape.Render != ShapeRender.Line && !sel) continue;
            ShapeWorldPoints(o); // reads moved points back
            var w = o.World;
            var pts = ShapeBuilder.Path(o.Shape).Select(q => Vector3.Transform(q, w)).ToList();
            var col = sel ? new Vector4(1, 1, 1, 1) : new Vector4(0.35f, 0.75f, 1f, 1);
            if (o.Shape.Render != ShapeRender.Line) col = new Vector4(1f, 0.85f, 0.3f, 1);
            for (int i = 1; i < pts.Count; i++) lines.Line(pts[i - 1], pts[i], col);
            if (o.Shape.Closed && pts.Count > 2) lines.Line(pts[^1], pts[0], col);
        }
    }

    /// <summary>A bare line object under the mouse (8 px from one of its segments), for clicking it.</summary>
    SceneObject PickShape(Viewport vp, Vector2 local)
    {
        SceneObject hit = null; float best = 8f;
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Frozen || o.Shape == null || o.Mesh.Faces.Count > 0) continue;
            var pts = ShapeWorldPoints(o); int n = pts.Count;
            var sp = new Vector2[n]; var ok = new bool[n];
            for (int i = 0; i < n; i++) ok[i] = vp.Camera.Project(pts[i], vp.W, vp.H, out sp[i], out _);
            int segs = o.Shape.Closed && n > 2 ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n; if (!ok[i] || !ok[j]) continue;
                var ab = sp[j] - sp[i]; float l2 = ab.LengthSquared();
                float t = l2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(local - sp[i], ab) / l2, 0, 1);
                float d = Vector2.Distance(sp[i] + ab * t, local);
                if (d < best) { best = d; hit = o; }
            }
        }
        return hit;
    }

    static readonly string[] ShapeRenderNames = { "Line only", "Rectangular (beam)", "Round (pipe)", "Door frame (casing)", "Extrude", "Sweep (your profile)", "Lathe (turn around an axis)" };
    static readonly string[] ShapeKindNames = { "Line", "Rectangle", "Circle", "Arc" };

    /// <summary>3ds Max Create > Shapes > Rectangle / Circle / Arc: a shape with editable sizes at the 3D cursor, flat on
    /// the ground. Its panel renders it like a line (beam, pipe, extrude, lathe...).</summary>
    public SceneObject CreateShapeKind(int kind)
    {
        var s = new ShapeData { Kind = kind, Render = ShapeRender.Line, Facing = Vector3.UnitZ };
        ShapeBuilder.Generate(s);
        var created = AddObject(ShapeKindNames[kind], ShapeBuilder.Build(s), Cursor3D);
        if (created != null) { created.Shape = s; created.Touch(); }
        SetStatus($"{ShapeKindNames[kind]} added. Its sizes, render (extrude, lathe, pipe...) and curve are in its panel.");
        return created;
    }
    static readonly string[] LineFrameStyles = { "Square", "Stepped", "Chamfer", "Round" };

    /// <summary>Properties of a line: closed, how it renders and its sizes; the mesh is rebuilt on every change.</summary>
    void ShapePanel(SceneObject o)
    {
        if (!Ui.Header("Line", Open)) return;
        var s = o.Shape.Copy();
        bool ch = false;
        int r = (int)s.Render;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##shape_render", ref r, ShapeRenderNames, ShapeRenderNames.Length)) { s.Render = (ShapeRender)r; ch = true; }
        TutRect("shape_render");
        bool closed = s.Closed;
        if (ImGui.Checkbox("Closed##shape_closed", ref closed)) { s.Closed = closed; ch = true; }
        ImGui.SameLine();
        bool flip = s.Flip;
        if (ImGui.Checkbox("Flip side##shape_flip", ref flip)) { s.Flip = flip; ch = true; }
        Theme.Tip("Puts the section on the other side of the line (door frame: inside / outside of the opening; extrude: the other way)");
        bool F(string label, ref float v, float speed = 0.002f, float min = 0.001f)
        {
            ImGui.SetNextItemWidth(-1);
            bool c = ImGui.DragFloat(label, ref v, speed, min, 50f, label.StartsWith("##") ? "%.3f m" : label + " %.3f m");
            return c;
        }
        // the shape itself: rectangle / circle / arc keep their sizes (3ds Max parameters) until turned into a line
        if (s.Kind != 0)
        {
            Ui.TextDisabled(ShapeKindNames[Math.Clamp(s.Kind, 0, 3)]);
            if (s.Kind == 1) { ch |= F("Width##shape_rw", ref s.RectWidth, 0.01f); ch |= F("Length##shape_rl", ref s.RectLength, 0.01f); }
            else
            {
                ch |= F("Radius##shape_rad", ref s.Radius, 0.005f);
                int sd = s.Sides; ImGui.SetNextItemWidth(-1); if (ImGui.SliderInt("##shape_csides", ref sd, 2, 32, "smoothness %d")) { s.Sides = sd; ch = true; }
                if (s.Kind == 3)
                {
                    ImGui.SetNextItemWidth(-1); ch |= ImGui.DragFloat("##shape_af", ref s.ArcFrom, 0.5f, -360, 360, "from %.0f°");
                    ImGui.SetNextItemWidth(-1); ch |= ImGui.DragFloat("##shape_at", ref s.ArcTo, 0.5f, -360, 360, "to %.0f°");
                }
            }
            if (ImGui.Button("Make it a line (edit its points)##shape_toline", new Vector2(-1, 0))) { ShapeBuilder.Generate(s); s.Kind = 0; ch = true; }
            Theme.Tip("Like 3ds Max Convert to Editable Spline: the sizes go, its points can be moved one by one in vertex mode.");
        }
        bool smooth = s.Smooth;
        if (ImGui.Checkbox("Smooth curve##shape_smooth", ref smooth)) { s.Smooth = smooth; ch = true; }
        Theme.Tip("The line passes through its points as a smooth curve (3ds Max smooth vertices).");
        if (s.Smooth) { ImGui.SameLine(); int st = s.Steps; ImGui.SetNextItemWidth(-1); if (ImGui.SliderInt("##shape_steps", ref st, 1, 24, "steps %d")) { s.Steps = st; ch = true; } }
        switch (s.Render)
        {
            case ShapeRender.Rectangular:
                Ui.TextDisabled("Width (in the plane of the line) / depth (out of it)");
                ch |= F("##shape_w", ref s.Width); ch |= F("##shape_d", ref s.Depth);
                break;
            case ShapeRender.Round:
                Ui.TextDisabled("Thickness / sides");
                ch |= F("##shape_t", ref s.Thickness);
                { int sd = s.Sides; ImGui.SetNextItemWidth(-1); if (ImGui.SliderInt("##shape_sides", ref sd, 3, 32)) { s.Sides = sd; ch = true; } }
                break;
            case ShapeRender.DoorFrame:
                {
                    int fs = s.FrameStyle;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.Combo("##shape_fstyle", ref fs, LineFrameStyles, LineFrameStyles.Length)) { s.FrameStyle = fs; ch = true; }
                    Ui.TextDisabled("Width (away from the opening) / depth (off the wall)");
                    ch |= F("##shape_fw", ref s.FrameWidth); ch |= F("##shape_fd", ref s.FrameDepth);
                    Ui.TextDisabled("Draw the line along the opening: up, across, down (open), snapped on its corners.");
                    break;
                }
            case ShapeRender.Extrude:
                Ui.TextDisabled("Amount (along the plane of the line)");
                ch |= F("##shape_amt", ref s.Amount, 0.005f);
                break;
            case ShapeRender.Lathe:
                {
                    Ui.TextDisabled("Axis (through the line's pivot) / segments / degrees");
                    float bw = (ImGui.GetContentRegionAvail().X - 4) / 3;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        if (ax > 0) ImGui.SameLine(0, 2);
                        if (Theme.Toggle("XYZ"[ax] + "##lathe_ax", s.LatheAxis == ax, new Vector2(bw, 0))) { s.LatheAxis = ax; ch = true; }
                    }
                    int sg = s.LatheSegs; ImGui.SetNextItemWidth(-1); if (ImGui.SliderInt("##lathe_segs", ref sg, 3, 64, "%d segments")) { s.LatheSegs = sg; ch = true; }
                    ImGui.SetNextItemWidth(-1); ch |= ImGui.DragFloat("##lathe_deg", ref s.LatheDegrees, 1, 1, 360, "%.0f°");
                    Ui.TextDisabled("Draw half the outline of a vase, a column, a lamp: it turns around the axis.");
                    break;
                }
            case ShapeRender.Sweep:
                {
                    Ui.TextDisabled("Profile: another line, drawn as the section (its first point rides on this line)");
                    var others = Scene.Objects.Where(q => q.Shape != null && q != o && q.Shape.Points.Count >= 3).ToList();
                    string cur = s.Profile is { Count: >= 3 } ? (s.ProfileName ?? "picked") : "none (a 10 cm square)";
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##shape_prof", cur))
                    {
                        if (others.Count == 0) Ui.TextDisabled("Draw the profile first: another Line, closed or open");
                        foreach (var q in others)
                            if (ImGui.Selectable(q.Name + "##shape_prof_" + q.Id))
                            {
                                ShapeWorldPoints(q);
                                var qw = q.World;
                                s.Profile = ShapeBuilder.ProfileFrom(ShapeBuilder.Path(q.Shape).Select(v => Vector3.Transform(v, qw)).ToList());
                                s.ProfileName = q.Name; ch = true;
                            }
                        ImGui.EndCombo();
                    }
                    TutRect("shape_profile");
                    Ui.TextDisabled("Profile scale");
                    ImGui.SetNextItemWidth(-1);
                    ch |= ImGui.DragFloat("##shape_pscale", ref s.ProfileScale, 0.01f, 0.01f, 100f, "x %.2f");
                    break;
                }
        }
        if (s.Points.Count >= 3)
        {
            Ui.TextDisabled("Round the corners (radius / segments, 1 = chamfer)");
            float half = (ImGui.GetContentRegionAvail().X - 4) / 2;
            ImGui.SetNextItemWidth(half);
            ch |= ImGui.DragFloat("##shape_fillet", ref s.Fillet, 0.002f, 0f, 20f, s.Fillet <= 0 ? "sharp" : "%.3f m");
            TutRect("shape_fillet");
            ImGui.SameLine();
            int fsg = s.FilletSegs; ImGui.SetNextItemWidth(half);
            if (ImGui.SliderInt("##shape_fsegs", ref fsg, 1, 16)) { s.FilletSegs = fsg; ch = true; }
        }
        float len = 0; for (int i = 1; i < s.Points.Count; i++) len += Vector3.Distance(s.Points[i - 1], s.Points[i]);
        if (s.Closed && s.Points.Count > 2) len += Vector3.Distance(s.Points[^1], s.Points[0]);
        Ui.TextDisabled($"{s.Points.Count} points, length {len:0.000} m");
        if (ch)
        {
            var c = s;
            Do("Line", () => { o.Shape = c; o.Mesh = ShapeBuilder.Build(c); o.Touch(); });
        }
        if (ImGui.Button("Convert to Editable Poly##shape_conv", new Vector2(-1, 0)))
        {
            if (o.Shape.Render == ShapeRender.Line) SetStatus("Pick a render (door frame, beam, extrude...) first: a bare line has no faces.");
            else Do("Convert to Editable Poly", () => { o.Shape = null; o.Touch(); });
        }
        Theme.Tip("Keeps the mesh and forgets the line: edit it like any other mesh");
    }
}
