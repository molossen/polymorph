using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public enum ModalKind { None, Translate, Rotate, Scale, Extrude, Inset, LoopCut, Wall, Chamfer, Knife, Opening, Connect, Line, QuickSlice, TargetWeld, FfdPoints, WeightPaint }

/// <summary>
/// Blender-style modal tools: G / R / S with X Y Z constraints (twice = local, Shift = plane),
/// typed values, Ctrl to toggle snapping, LMB / Enter to confirm and RMB / Esc to cancel; plus
/// interactive Extrude, Inset and Loop Cut, and wall drawing.
/// </summary>
public sealed partial class EditorApp
{
    sealed class ModalState
    {
        public ModalKind Kind;
        public Viewport Vp;
        public Vector2 MouseStart;
        public Vector3 Pivot;
        public Vector2 PivotScreen;
        public int Axis = -1;
        public bool Local, Plane;
        public string Num = "";
        public Vector3[] Global = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        public Vector3[] LocalAxes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        public float AngleAcc, LastAngle;
        // parametric tools (extrude / inset)
        public PolyMesh BaseMesh;
        public PolySelection BaseSel;
        public SceneObject Obj;
        public Vector3 Normal;
        public float LastValue = float.NaN;
        public float D0;
        // extrude drag: the mesh after the extrude, the points it moved and the new sides (later values only move them)
        public PolyMesh XMesh; public List<(int V, Vector3 N)> XMoved; public int[] XRing, XVerts; public float XApplied;
        /// <summary>The mesh was a closed solid when the tool started (the result must stay closed).</summary>
        public bool BaseClosed; public bool Broke;
        // loop cut preview
        public EdgeKey? Hover;
        public List<(Vector3 A, Vector3 B)> Preview = new();
        public string Header = "";
        public Vector3[] Axes => Local ? LocalAxes : Global;
    }

    readonly ModalState modal = new();
    public bool ModalActive => modal.Kind != ModalKind.None;

    // ---------------------------------------------------------------- start

    public void BeginModal(ModalKind kind)
    {
        if (modal.Kind != ModalKind.None || transforming) return;
        var vp = Hovered() ?? Viewports[ActiveViewport];
        var cur = Current;
        switch (kind)
        {
            case ModalKind.Translate:
            case ModalKind.Rotate:
            case ModalKind.Scale:
                {
                    if (!SelectionPivot(out var pivot, out _)) { SetStatus("Nothing selected."); return; }
                    Undo.Record(kind == ModalKind.Translate ? "Move" : kind == ModalKind.Rotate ? "Rotate" : "Scale", Scene);
                    StartModalCommon(kind, vp, pivot);
                    CaptureOriginals();
                    if (kind == ModalKind.Translate) CaptureSnapFrom(vp, pivot);
                    var r = cur != null ? cur.Rotation : Quaternion.Identity;
                    modal.LocalAxes = new[] { Vector3.Transform(Vector3.UnitX, r), Vector3.Transform(Vector3.UnitY, r), Vector3.Transform(Vector3.UnitZ, r) };
                    break;
                }
            case ModalKind.Extrude:
            case ModalKind.Inset:
                {
                    if (cur == null || !(Level is SubObject.Polygon or SubObject.Element) || SubSel(cur).Faces.Count == 0)
                    {
                        SetStatus(kind == ModalKind.Extrude ? "Extrude works on faces: press 3 (face mode) and select faces." : "Inset works on faces: press 3 (face mode) and select faces.");
                        return;
                    }
                    if (!SelectionPivot(out var pivot, out _)) return;
                    Undo.Record(kind == ModalKind.Extrude ? "Extrude" : "Inset", Scene);
                    StartModalCommon(kind, vp, pivot);
                    modal.Obj = cur; modal.BaseMesh = cur.Mesh.Clone(); modal.BaseSel = SubSel(cur).Clone();
                    var n = Vector3.Zero;
                    foreach (var f in modal.BaseSel.Faces) n += cur.Mesh.FaceNormal(f) * MathF.Max(cur.Mesh.FaceArea(cur.Mesh.Faces[f]), 1e-6f);
                    Matrix4x4.Invert(cur.World, out var inv);
                    var wn = Vector3.TransformNormal(n, Matrix4x4.Transpose(inv));
                    modal.Normal = wn.LengthSquared() > 1e-12f ? Vector3.Normalize(wn) : Vector3.UnitZ;
                    modal.D0 = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    ApplyParametric(0);
                    break;
                }
            case ModalKind.LoopCut:
                if (cur == null) { SetStatus("Select one object for Loop Cut."); return; }
                StartModalCommon(kind, vp, Vector3.Zero);
                loopPreviewKey = "";
                break;
            case ModalKind.Chamfer:
                {
                    if (cur == null || Level is not (SubObject.Edge or SubObject.Border) || SubSel(cur).Edges.Count == 0)
                    { SetStatus("Chamfer works on edges: press 2 (edge mode) and select edges."); return; }
                    if (!SelectionPivot(out var pivot, out _)) return;
                    Undo.Record("Chamfer", Scene);
                    StartModalCommon(kind, vp, pivot);
                    modal.Obj = cur; modal.BaseMesh = cur.Mesh.Clone(); modal.BaseSel = SubSel(cur).Clone();
                    modal.BaseClosed = cur.Mesh.Faces.Count <= 200000 && cur.Mesh.IsClosedManifold(out _); modal.Broke = false;
                    modal.D0 = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    break;
                }
            case ModalKind.Knife:
                if (cur == null) { SetStatus("Select one object to cut."); return; }
                StartModalCommon(kind, vp, Vector3.Zero);
                knifeA = null;
                KnifeHeader();
                SetStatus("Knife: click two points to cut along the line. Shift = straight (45° steps), options in the panel. Esc to cancel.");
                break;
            case ModalKind.QuickSlice:
                if (cur == null) { SetStatus("Select one object to slice."); return; }
                StartModalCommon(kind, vp, Vector3.Zero);
                knifeA = null;
                modal.Header = "QuickSlice: click two points (Shift = 45°)";
                SetStatus("QuickSlice: click two points, the whole object (or the selected polygons) is sliced along the line. Right click / Esc to stop.");
                break;
            case ModalKind.TargetWeld:
                if (cur?.Mesh == null) { SetStatus("Select one object to weld."); return; }
                if (Level != SubObject.Vertex) SetLevel(SubObject.Vertex);
                StartModalCommon(kind, vp, Vector3.Zero);
                twSrc = -1;
                modal.Header = "Target Weld: click a vertex, then the vertex to weld it onto";
                SetStatus("Target Weld: click a vertex, then the one it goes onto (that one stays put). Right click / Esc to stop.");
                break;
            case ModalKind.Wall:
                StartModalCommon(kind, vp, Vector3.Zero);
                wallPts.Clear();
                break;
        }
    }

    void StartModalCommon(ModalKind kind, Viewport vp, Vector3 pivot)
    {
        modal.Kind = kind; modal.Vp = vp; modal.MouseStart = Input.Mouse; modal.Pivot = pivot;
        modal.Axis = -1; modal.Local = false; modal.Plane = false; modal.Num = ""; modal.AngleAcc = 0; modal.LastValue = float.NaN; modal.XMesh = null;
        modal.Hover = null; modal.Preview.Clear(); modal.Header = "";
        ctrlLatch = Input.Ctrl; // Ctrl held from the shortcut (Ctrl+E…) must not flip snapping
        vp.Camera.Project(pivot, vp.W, vp.H, out var ps, out _);
        modal.PivotScreen = vp.ToScreen(ps);
        modal.LastAngle = MathF.Atan2(Input.Mouse.Y - modal.PivotScreen.Y, Input.Mouse.X - modal.PivotScreen.X);
        ActiveViewport = Array.IndexOf(Viewports, vp);
        lmbTracking = false; boxSelecting = false; rmbTracking = false;
    }

    // ---------------------------------------------------------------- keys while a modal tool runs

    void ModalKeys()
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool shift = ImGui.GetIO().KeyShift;
        if (P(ImGuiKey.Escape)) { CancelModal(); return; }
        if (P(ImGuiKey.Enter) || P(ImGuiKey.KeypadEnter)) { ConfirmModal(); return; }
        if (modal.Kind == ModalKind.Opening) { OpeningKeys(); return; }
        if (modal.Kind == ModalKind.FfdPoints) { FfdKeys(); return; }
        if (modal.Kind == ModalKind.WeightPaint) { WeightPaintKeys(); return; }

        if (modal.Kind == ModalKind.Line) { LineKeys(); return; }
        if (modal.Kind == ModalKind.Wall)
        {
            if (P(ImGuiKey.Backspace) && wallPts.Count > 0) wallPts.RemoveAt(wallPts.Count - 1);
            return;
        }
        if (modal.Kind == ModalKind.LoopCut)
        {
            // number of loops: mouse wheel, Page Up / Down, + / -; Tab turns the cut the other way
            int d = 0;
            if (MathF.Abs(Input.Wheel) > 0 && !ImGui.GetIO().WantCaptureMouse) d = Math.Sign(Input.Wheel);
            if (P(ImGuiKey.PageUp) || P(ImGuiKey.KeypadAdd) || P(ImGuiKey.Equal)) d = 1;
            if (P(ImGuiKey.PageDown) || P(ImGuiKey.KeypadSubtract) || P(ImGuiKey.Minus)) d = -1;
            if (d != 0) loopCuts = Math.Clamp(loopCuts + d, 1, 32);
            if (P(ImGuiKey.Tab)) loopCross = !loopCross;
            return;
        }
        if (modal.Kind == ModalKind.Knife)
        {
            // A = straight cut on the world axes, V = only the faces you see, 1-4 = angle step
            if (P(ImGuiKey.A)) knifeWorld = !knifeWorld;
            if (P(ImGuiKey.V)) knifeVisibleOnly = !knifeVisibleOnly;
            if (P(ImGuiKey._1)) knifeSnap = 0; if (P(ImGuiKey._2)) knifeSnap = 15; if (P(ImGuiKey._3)) knifeSnap = 45; if (P(ImGuiKey._4)) knifeSnap = 90;
            KnifeHeader();
            return;
        }
        if (modal.Kind == ModalKind.Chamfer)
        {
            // sides of the bevel: mouse wheel (Blender), Page Up / Down, + / -
            int d = 0;
            if (MathF.Abs(Input.Wheel) > 0) d = Math.Sign(Input.Wheel);
            if (P(ImGuiKey.PageUp) || P(ImGuiKey.KeypadAdd) || P(ImGuiKey.Equal)) d = 1;
            if (P(ImGuiKey.PageDown) || P(ImGuiKey.KeypadSubtract)) d = -1;
            if (d != 0)
            {
                chamferSegments = Math.Clamp(chamferSegments + d, 1, 32);
                if (!float.IsNaN(modal.LastValue)) { float v = modal.LastValue; modal.LastValue = float.NaN; ApplyParametric(v); }
            }
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale)
        {
            if (P(ImGuiKey.X)) CycleAxis(0, shift);
            if (P(ImGuiKey.Y)) CycleAxis(1, shift);
            if (P(ImGuiKey.Z)) CycleAxis(2, shift);
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale or ModalKind.Extrude or ModalKind.Inset or ModalKind.Chamfer)
        {
            // typed value: characters (works with any layout, numpad included) …
            bool gotDigit = false;
            foreach (var c in Input.Chars)
            {
                if (char.IsDigit(c)) { modal.Num += c; gotDigit = true; }
                else if ((c == '.' || c == ',') && !modal.Num.Contains('.')) modal.Num += '.';
                else if (c == '-') modal.Num = modal.Num.StartsWith('-') ? modal.Num[1..] : "-" + modal.Num;
            }
            // … plus the top-row keys pressed without Shift on AZERTY (they type & é " ' …)
            if (!gotDigit && Input.Chars.Count == 0)
                for (int d = 0; d <= 9; d++)
                    if (P(ImGuiKey._0 + d) && !shift) modal.Num += (char)('0' + d);
            if (P(ImGuiKey.Backspace) && modal.Num.Length > 0) modal.Num = modal.Num[..^1];
        }
    }

    void CycleAxis(int axis, bool plane)
    {
        // Blender: first press global, second press local, third press off
        if (modal.Axis != axis || modal.Plane != plane) { modal.Axis = axis; modal.Local = false; modal.Plane = plane; }
        else if (!modal.Local) modal.Local = true;
        else { modal.Axis = -1; modal.Local = false; modal.Plane = false; }
    }

    bool NumValue(out float v)
    {
        v = 0;
        if (modal.Num.Length == 0 || modal.Num == "-" || modal.Num == ".") return false;
        return float.TryParse(modal.Num, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // ---------------------------------------------------------------- mouse while a modal tool runs

    void ModalMouse(Viewport hv)
    {
        if (modal.Kind == ModalKind.Wall) { WallMouse(hv); return; }
        if (modal.Kind == ModalKind.Line) { LineMouse(hv); return; }
        if (modal.Kind == ModalKind.Opening) { OpeningMouse(hv); return; }
        if (modal.Kind == ModalKind.LoopCut) { LoopCutMouse(hv); return; }
        if (modal.Kind == ModalKind.Knife) { KnifeMouse(hv); return; }
        if (modal.Kind == ModalKind.QuickSlice) { QuickSliceMouse(hv); return; }
        if (modal.Kind == ModalKind.TargetWeld) { TargetWeldMouse(hv); return; }
        if (modal.Kind == ModalKind.FfdPoints) { FfdMouse(hv); return; }
        if (modal.Kind == ModalKind.WeightPaint) { WeightPaintMouse(hv); return; }
        UpdateModal();
        // started by dragging the extrude arrow: releasing the button confirms (Blender's tool gizmo)
        if (extrudeDragging && modal.Kind == ModalKind.Extrude && !Input.Down(MouseButtons.Left)) { extrudeDragging = false; ConfirmModal(); }
        else if (Input.Pressed(MouseButtons.Left)) { extrudeDragging = false; ConfirmModal(); }
        else if (Input.Pressed(MouseButtons.Right)) { extrudeDragging = false; CancelModal(); }
    }

    /// <summary>Screen pixels moved along a world direction through the pivot, as metres.</summary>
    float ScreenAlong(Vector3 dir, Vector2 mouseDelta)
    {
        var vp = modal.Vp;
        vp.Camera.Project(modal.Pivot, vp.W, vp.H, out var a, out _);
        vp.Camera.Project(modal.Pivot + dir, vp.W, vp.H, out var b, out _);
        var sd = b - a; float l2 = sd.LengthSquared();
        if (l2 < 1e-4f) return 0;
        return Vector2.Dot(mouseDelta, sd) / l2;
    }

    void UpdateModal()
    {
        var vp = modal.Vp; var d = Input.Mouse - modal.MouseStart;
        bool hasNum = NumValue(out float num);
        string axisName = modal.Axis < 0 ? "" : (modal.Plane ? "plane without " : "along ") + "XYZ"[modal.Axis] + (modal.Local ? " (local)" : " (global)");
        switch (modal.Kind)
        {
            case ModalKind.Translate:
                {
                    var axes = modal.Axes;
                    Vector3 delta;
                    if (hasNum)
                    {
                        int ax = modal.Axis < 0 ? 0 : modal.Plane ? (modal.Axis + 1) % 3 : modal.Axis;
                        delta = axes[ax] * num;
                    }
                    else if (modal.Axis >= 0 && !modal.Plane) delta = axes[modal.Axis] * ScreenAlong(axes[modal.Axis], d);
                    else if (modal.Axis >= 0 && modal.Plane)
                    {
                        var n = axes[modal.Axis];
                        if (!Picking.RayPlane(RayAt(vp, Input.Mouse), modal.Pivot, n, out var p1) || !Picking.RayPlane(RayAt(vp, modal.MouseStart), modal.Pivot, n, out var p0)) return;
                        delta = p1 - p0;
                    }
                    else
                    {
                        float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                        delta = (vp.Camera.Right * d.X - vp.Camera.CamUp * d.Y) * px;
                    }
                    if (!hasNum) delta = ApplySnaps(vp, delta, modal.Pivot, axes, modal.Axis, modal.Plane); // vertex / edge / face under the mouse, else grid points
                    ApplyLive(p => p + delta, (pos, rot, sc) => (pos + delta, rot, sc));
                    modal.Header = $"Move  D: {delta.X:0.000}  {delta.Y:0.000}  {delta.Z:0.000} m  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Rotate:
                {
                    var toViewer = -vp.Camera.Forward;
                    var axis = modal.Axis >= 0 ? modal.Axes[modal.Axis] : toViewer;
                    float a = MathF.Atan2(Input.Mouse.Y - modal.PivotScreen.Y, Input.Mouse.X - modal.PivotScreen.X);
                    float da = a - modal.LastAngle;
                    if (da > MathF.PI) da -= MathF.Tau; else if (da < -MathF.PI) da += MathF.Tau;
                    modal.AngleAcc += da; modal.LastAngle = a;
                    // screen angles grow clockwise (y down); clockwise on screen is negative about an axis facing the viewer
                    float deg = -modal.AngleAcc * 180f / MathF.PI * (Vector3.Dot(axis, toViewer) >= 0 ? 1 : -1);
                    if (hasNum) deg = num;
                    else if (SnapActive) deg = SnapValue(deg, AngleSnap);
                    ApplyRotation(axis, deg * MathF.PI / 180f, modal.Pivot);
                    modal.Header = $"Rotate  {deg:0.0}°  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Scale:
                {
                    float d0 = MathF.Max(Vector2.Distance(modal.MouseStart, modal.PivotScreen), 30f);
                    float f = Vector2.Distance(Input.Mouse, modal.PivotScreen) / d0;
                    if (hasNum) f = num;
                    else if (SnapActive) f = SnapValue(f, 0.1f);
                    Vector3 fv;
                    if (modal.Axis < 0) fv = new Vector3(f);
                    else if (modal.Plane) { fv = new Vector3(f); if (modal.Axis == 0) fv.X = 1; else if (modal.Axis == 1) fv.Y = 1; else fv.Z = 1; }
                    else { fv = Vector3.One; if (modal.Axis == 0) fv.X = f; else if (modal.Axis == 1) fv.Y = f; else fv.Z = f; }
                    ApplyScale(fv, modal.Pivot, modal.Axes);
                    modal.Header = $"Scale  {f:0.000}  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Extrude:
                {
                    float h = hasNum ? num : ScreenAlong(modal.Normal, d);
                    if (SnapActive && !hasNum) h = SnapValue(h, GridStep);
                    ApplyParametric(h);
                    modal.Header = $"Extrude  {h:0.000} m along normal  {NumShown()}";
                    break;
                }
            case ModalKind.Chamfer:
                {
                    float dist = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                    float amount = MathF.Max(0, (dist - modal.D0) * px * 0.5f);
                    if (hasNum) amount = num;
                    else if (SnapActive) amount = SnapValue(amount, GridStep * 0.1f);
                    ApplyParametric(amount);
                    modal.Header = modal.Broke ? "Chamfer: these edges can't be chamfered together without holes (try fewer edges)"
                        : $"Chamfer  {amount:0.000} m  {chamferSegments} segment{(chamferSegments > 1 ? "s" : "")} (wheel / Page Up-Down)  {NumShown()}";
                    break;
                }
            case ModalKind.Inset:
                {
                    float dist = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                    // move towards the centre to thicken (Blender); starting on the centre, move away instead
                    float amount = modal.D0 >= 30 ? (modal.D0 - dist) * px : (dist - modal.D0) * px;
                    amount = MathF.Max(0, amount);
                    if (hasNum) amount = num;
                    else if (SnapActive) amount = SnapValue(amount, GridStep * 0.1f);
                    ApplyParametric(amount);
                    modal.Header = $"Inset  thickness {amount:0.000} m  {NumShown()}";
                    break;
                }
        }
        SetStatus(modal.Header + "    LMB/Enter confirm · RMB/Esc cancel · X Y Z axis (twice = local, Shift = plane) · type a value · Ctrl snap");
    }

    string NumShown() => modal.Num.Length > 0 ? $"[{modal.Num}]" : "";

    /// <summary>Re-runs extrude / inset from the original mesh with a new value.</summary>
    void ApplyParametric(float value)
    {
        if (MathF.Abs(value - modal.LastValue) < 1e-6f) return;
        modal.LastValue = value;
        var o = modal.Obj;
        float s = (MathF.Abs(o.Scale.X) + MathF.Abs(o.Scale.Y) + MathF.Abs(o.Scale.Z)) / 3f;
        float local = value / MathF.Max(s, 1e-4f);
        if (modal.Kind == ModalKind.Extrude && modal.XMesh != null && ReferenceEquals(o.Mesh, modal.XMesh))
        {
            // same faces, another height: the extruded points slide along their direction, the side UVs follow
            float d = local - modal.XApplied;
            foreach (var (v, n) in modal.XMoved) o.Mesh.Verts[v] += n * d;
            MeshOps.SideUVMetric(o.Mesh, modal.XRing);
            modal.XApplied = local;
            int from = o.Version; o.Touch();
            renderer.HintPointsMoved(o.Id, from, o.Version, modal.XVerts, modal.XRing);
            wires.HintPointsMoved(o.Id, from, o.Version, modal.XVerts, o.Mesh.Verts);
            xfPointsOnly = true;
            return;
        }
        o.Mesh = modal.BaseMesh.Clone();
        var sel = modal.BaseSel.Clone();
        subSel[o.Id] = sel;
        modal.XMesh = null;
        if (modal.Kind == ModalKind.Extrude)
        {
            var moved = new List<(int V, Vector3 N)>();
            var ring = MeshOps.ExtrudeFaces(o.Mesh, sel.Faces, local, false, moved);
            modal.XMesh = o.Mesh; modal.XMoved = moved; modal.XRing = ring.ToArray(); modal.XVerts = moved.Select(x => x.V).ToArray(); modal.XApplied = local;
        }
        else if (modal.Kind == ModalKind.Chamfer)
        {
            if (local > 1e-5f) MeshOps.ChamferEdges(o.Mesh, sel.Edges.ToList(), local, chamferSegments);
            sel.Edges.Clear();
            // an odd corner the chamfer can't close: the mesh stays as it was rather than getting holes
            modal.Broke = modal.BaseClosed && local > 1e-5f && !o.Mesh.IsClosedManifold(out _);
            if (modal.Broke) { o.Mesh = modal.BaseMesh.Clone(); subSel[o.Id] = modal.BaseSel.Clone(); }
        }
        else MeshOps.InsetFaces(o.Mesh, sel.Faces, local);
        o.Touch();
    }

    // ---------------------------------------------------------------- knife (straight cut through the view)

    Vector2? knifeA;

    void KnifeMouse(Viewport hv)
    {
        if (Input.Pressed(MouseButtons.Right)) { CancelModal(); return; }
        if (hv == null || !Input.Pressed(MouseButtons.Left) || ImGui.GetIO().WantCaptureMouse) return;
        if (knifeA == null) { knifeA = Input.Mouse; return; }
        var cur = Current; var a = knifeA.Value; var b = KnifeEnd();
        knifeA = null;
        if (cur == null || Vector2.Distance(a, b) < 3) return;
        // plane through both view rays
        var ra = RayAt(hv, a); var rb = RayAt(hv, b);
        var pa = ra.Origin + ra.Direction * 10; var pb = rb.Origin + rb.Direction * 10;
        var n = Vector3.Cross(pb - pa, hv.Camera.Ortho ? hv.Camera.Forward : (pa - hv.Camera.Eye));
        if (n.LengthSquared() < 1e-10f) return;
        Matrix4x4.Invert(cur.World, out var inv);
        if (knifeWorld)
        {
            // straight: the cut plane turned onto the nearest world axis, through the point clicked on the mesh
            var wn = Vector3.Normalize(n);
            var ax = MathF.Abs(wn.X) >= MathF.Abs(wn.Y) && MathF.Abs(wn.X) >= MathF.Abs(wn.Z) ? Vector3.UnitX : MathF.Abs(wn.Y) >= MathF.Abs(wn.Z) ? Vector3.UnitY : Vector3.UnitZ;
            n = ax;
            // the first point of the line that lands on the mesh (the clicks themselves are often outside it)
            for (int i = 0; i <= 32; i++)
                if (Picking.PickFace(cur, RayAt(hv, Vector2.Lerp(a, b, i / 32f)), cullBack: false) is { } hit) { pa = hit.Point; break; }
        }
        var lp = Vector3.Transform(pa, inv); var ln = Vector3.Normalize(Vector3.TransformNormal(n, Matrix4x4.Transpose(cur.World)));
        var faces = Level is SubObject.Polygon or SubObject.Element && SubSel(cur).Faces.Count > 0 ? new HashSet<int>(SubSel(cur).Faces) : null;
        // only faces the line actually crosses on screen (a knife, not an infinite slice)
        var keep = new HashSet<int>();
        var w = cur.World; var m = cur.Mesh;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (faces != null && !faces.Contains(fi)) continue;
            var f = m.Faces[fi]; bool hit = false, allOn = true;
            var poly2 = new Vector2[f.V.Length];
            for (int i = 0; i < f.V.Length; i++)
                if (!hv.Camera.Project(Vector3.Transform(m.Verts[f.V[i]], w), hv.W, hv.H, out poly2[i], out _)) allOn = false;
            if (!allOn) continue;
            if (knifeVisibleOnly && !FacesCamera(hv, cur, fi)) continue;
            var la = hv.Local(a); var lb = hv.Local(b);
            for (int i = 0; i < poly2.Length && !hit; i++) hit = SegmentsCross(la, lb, poly2[i], poly2[(i + 1) % poly2.Length]);
            // a click inside a face also counts (the line may start or end inside a big face, like Blender's knife)
            if (!hit && (InsidePolygon(la, poly2) || InsidePolygon(lb, poly2)) && FacesCamera(hv, cur, fi)) hit = true;
            if (hit) keep.Add(fi);
        }
        if (keep.Count == 0) { SetStatus("The line does not cross the mesh."); return; }
        HashSet<EdgeKey> cut = null;
        DoMesh("Knife", (o, mm, s) => { cut = MeshOps.Slice(mm, lp, ln, keep); });
        if (cut != null && Level is SubObject.Edge or SubObject.Border) SubSel(cur).Edges = cut;
        SetStatus($"Knife: {cut?.Count ?? 0} new edge(s). Click two more points, or Esc / right click to stop.");
    }

    /// <summary>3ds Max QuickSlice: two clicks, the object (or its selected polygons) is cut by the plane through that
    /// line and the view, across the whole mesh, not only where the line is. Stays on for more slices.</summary>
    void QuickSliceMouse(Viewport hv)
    {
        if (Input.Pressed(MouseButtons.Right)) { CancelModal(); return; }
        if (hv == null || !Input.Pressed(MouseButtons.Left) || ImGui.GetIO().WantCaptureMouse) return;
        if (knifeA == null) { knifeA = Input.Mouse; return; }
        var cur = Current; var a = knifeA.Value; var b = KnifeEnd();
        knifeA = null;
        if (cur == null || Vector2.Distance(a, b) < 3) return;
        var ra = RayAt(hv, a); var rb = RayAt(hv, b);
        var pa = ra.Origin + ra.Direction * 10; var pb = rb.Origin + rb.Direction * 10;
        var n = Vector3.Cross(pb - pa, hv.Camera.Ortho ? hv.Camera.Forward : (pa - hv.Camera.Eye));
        if (n.LengthSquared() < 1e-10f) return;
        Matrix4x4.Invert(cur.World, out var inv);
        var lp = Vector3.Transform(pa, inv); var ln = Vector3.Normalize(Vector3.TransformNormal(n, Matrix4x4.Transpose(cur.World)));
        var faces = Level is SubObject.Polygon or SubObject.Element && SubSel(cur).Faces.Count > 0 ? new HashSet<int>(SubSel(cur).Faces) : null;
        HashSet<EdgeKey> cut = null;
        DoMesh("QuickSlice", (o, mm, s) => { cut = MeshOps.Slice(mm, lp, ln, faces); });
        if (cut == null || cut.Count == 0) { SetStatus("QuickSlice: the line does not cross the mesh."); return; }
        if (Level is SubObject.Edge or SubObject.Border) SubSel(cur).Edges = cut;
        SetStatus($"QuickSlice: {cut.Count} new edge(s){(faces != null ? " in the selected polygons" : "")}. Click two more points, or Esc / right click to stop.");
    }

    /// <summary>Target Weld: the first click takes a vertex, the second merges it into the vertex clicked. Stays on.</summary>
    int twSrc = -1, twHover = -1;
    void TargetWeldMouse(Viewport hv)
    {
        var cur = Current;
        if (Input.Pressed(MouseButtons.Right)) { if (twSrc >= 0) { twSrc = -1; return; } CancelModal(); return; }
        twHover = -1;
        if (hv == null || cur?.Mesh == null || ImGui.GetIO().WantCaptureMouse) return;
        twHover = NearestVisibleVertex(hv, cur, hv.Local(Input.Mouse));
        if (twHover == twSrc) twHover = -1;
        if (!Input.Pressed(MouseButtons.Left)) return;
        if (twSrc < 0) { twSrc = twHover; if (twSrc >= 0) SetStatus("Target Weld: now click the vertex to weld it onto."); return; }
        if (twHover < 0) { twSrc = -1; SetStatus("Target Weld: no vertex there. Click a vertex to start again."); return; }
        int src = twSrc, dst = twHover, merged = -1;
        DoMesh("Target Weld", (o, mm, s) => { merged = MeshOps.TargetWeld(mm, src, dst); s.Clear(); if (merged >= 0) s.Verts.Add(merged); });
        twSrc = -1;
        SetStatus(merged >= 0 ? "Welded. Click another vertex, or Esc / right click to stop." : "Target Weld: could not weld those two.");
    }

    static bool InsidePolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The face is turned towards the camera (a click inside it is on its visible side, not through the mesh).</summary>
    static bool FacesCamera(Viewport hv, SceneObject o, int fi)
    {
        var w = o.World;
        var c = Vector3.Transform(o.Mesh.FaceCenter(fi), w);
        var n = Vector3.Normalize(Vector3.TransformNormal(o.Mesh.FaceNormal(fi), w));
        var toCam = hv.Camera.Ortho ? -hv.Camera.Forward : hv.Camera.Eye - c;
        return Vector3.Dot(n, toCam) > 0;
    }

    static bool SegmentsCross(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        static float Cr(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        float d1 = Cr(q1, q2, p1), d2 = Cr(q1, q2, p2), d3 = Cr(p1, p2, q1), d4 = Cr(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>Knife options: angle step on screen (0 = free), straight on the world axes, only the faces turned to you.</summary>
    int knifeSnap; bool knifeWorld, knifeVisibleOnly;

    /// <summary>The second point of the knife line: the mouse, turned to the angle step (Shift = 45° when the step is off).</summary>
    Vector2 KnifeEnd()
    {
        var b = Input.Mouse;
        if (knifeA is not { } a) return b;
        int step = knifeSnap > 0 ? knifeSnap : ImGui.GetIO().KeyShift ? 45 : 0;
        if (step == 0) return b;
        var d = b - a; float len = d.Length(); if (len < 1) return b;
        float ang = MathF.Atan2(d.Y, d.X), st = step * MathF.PI / 180f;
        ang = MathF.Round(ang / st) * st;
        return a + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * len;
    }

    void KnifeHeader()
    {
        var o = new List<string>();
        if (knifeSnap > 0) o.Add($"{knifeSnap}° steps"); if (knifeWorld) o.Add("straight on axes"); if (knifeVisibleOnly) o.Add("visible faces");
        modal.Header = "Knife: click two points" + (o.Count > 0 ? "  (" + string.Join(", ", o) + ")" : "");
    }

    void KnifePanel()
    {
        if (modal.Kind != ModalKind.Knife) return;
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + vp.W - 250, vp.Y + 70), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##knifepanel", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        Ui.TextColored(Theme.Gold, "Knife");
        ImGui.TextDisabled("Angle step (1-4, Shift = 45°)");
        int[] steps = { 0, 15, 45, 90 };
        for (int i = 0; i < steps.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4);
            bool on = knifeSnap == steps[i];
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ToggleOn);
            if (ImGui.Button((steps[i] == 0 ? "Free" : steps[i] + "°") + "##ks", new Vector2(50, 0))) knifeSnap = steps[i];
            if (on) ImGui.PopStyleColor();
        }
        ImGui.Checkbox("Straight on world axes (A)", ref knifeWorld);
        Theme.Tip("The cut is turned onto the nearest axis (X, Y or Z) through the point you clicked: perfectly level or plumb lines");
        ImGui.Checkbox("Visible faces only (V)", ref knifeVisibleOnly);
        Theme.Tip("Off: the cut goes through the whole mesh, the back too. On: only the faces turned towards you");
        ImGui.End();
        KnifeHeader();
    }

    // ---------------------------------------------------------------- loop cut

    /// <summary>Loop cut options: how many loops, and the cut turned the other way (across the hovered edge).</summary>
    int loopCuts = 1; bool loopCross; string loopPreviewKey = "";

    void LoopCutPanel()
    {
        if (modal.Kind != ModalKind.LoopCut) return;
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + vp.W - 250, vp.Y + 70), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##loopcutpanel", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        Ui.TextColored(Theme.Gold, "Loop Cut");
        ImGui.SetNextItemWidth(160); ImGui.SliderInt("Cuts", ref loopCuts, 1, 32);
        Theme.Tip("Number of loops, evenly spaced (mouse wheel, + / -)");
        if (ImGui.Button(loopCross ? "Direction: across (Tab)" : "Direction: along (Tab)", new Vector2(200, 0))) loopCross = !loopCross;
        Theme.Tip("Turns the cut the other way on the same faces");
        ImGui.End();
    }

    void LoopCutMouse(Viewport hv)
    {
        var cur = Current;
        if (cur == null) { CancelModal(); return; }
        bool overUi = ImGui.GetIO().WantCaptureMouse;
        if (hv == null) { modal.Hover = null; modal.Preview.Clear(); loopPreviewKey = ""; }
        else if (!overUi) // over the panel: the last loop stays shown
        {
            var e = EdgeUnder(hv, cur, hv.Local(Input.Mouse), 14f);
            if (e.HasValue && loopCross) e = MeshOps.CrossEdge(cur.Mesh, e.Value) ?? e;
            modal.Hover = e;
        }
        string key = modal.Hover is { } hk ? $"{hk.A}/{hk.B}/{loopCuts}/{cur.Version}" : "";
        if (key != loopPreviewKey)
        {
            loopPreviewKey = key; modal.Preview.Clear();
            if (modal.Hover is { } he)
            {
                var w = cur.World;
                foreach (var (a, b) in MeshOps.LoopCutPreview(cur.Mesh, he, loopCuts)) modal.Preview.Add((Vector3.Transform(a, w), Vector3.Transform(b, w)));
            }
        }
        modal.Header = modal.Hover.HasValue ? $"Loop Cut: click to cut ({loopCuts} loop{(loopCuts > 1 ? "s" : "")})" : "Loop Cut: hover an edge";
        SetStatus(modal.Header + "    LMB cut · wheel / + - loops · Tab direction · RMB/Esc cancel");
        if (Input.Pressed(MouseButtons.Left) && modal.Hover.HasValue && !overUi)
        {
            var edge = modal.Hover.Value; int cuts = loopCuts;
            modal.Kind = ModalKind.None;
            DoMesh("Loop Cut", (o, m, s) =>
            {
                var added = MeshOps.LoopCut(m, edge, cuts);
                Level = SubObject.Edge;
                s.Edges = added;
                SetStatus($"Loop cut: {added.Count} new edges");
            });
            if (Level == SubObject.Object) Level = SubObject.Edge;
        }
        else if (Input.Pressed(MouseButtons.Right)) CancelModal();
    }

    // ---------------------------------------------------------------- confirm / cancel

    void ConfirmModal()
    {
        var k = modal.Kind;
        if (k == ModalKind.Wall) { FinishWall(false); return; }
        if (k == ModalKind.Line) { FinishLine(false); return; }
        if (k == ModalKind.Opening) { ConfirmOpening(); return; }
        if (k == ModalKind.LoopCut) { if (modal.Hover.HasValue) { } CancelModal(); return; }
        if (k is ModalKind.Knife or ModalKind.QuickSlice or ModalKind.TargetWeld or ModalKind.FfdPoints or ModalKind.WeightPaint) { modal.Kind = ModalKind.None; twSrc = -1; ffdDragging = false; wpStroke = false; return; }
        if (k is ModalKind.Extrude or ModalKind.Inset or ModalKind.Chamfer) RememberMeshOp();
        modal.Kind = ModalKind.None;
        FinishPointsOnly();
        Dirty = true;
        lastAdd = null;
        SetStatus(modal.Header);
    }

    void CancelModal()
    {
        var k = modal.Kind;
        modal.Kind = ModalKind.None;
        twSrc = -1; ffdDragging = false; ffdBox = null;
        switch (k)
        {
            case ModalKind.Translate:
            case ModalKind.Rotate:
            case ModalKind.Scale:
                RestoreOriginals();
                FinishPointsOnly();
                Undo.DropLast();
                break;
            case ModalKind.Extrude:
            case ModalKind.Inset:
            case ModalKind.Chamfer:
                modal.Obj.Mesh = modal.BaseMesh;
                subSel[modal.Obj.Id] = modal.BaseSel;
                modal.Obj.Touch();
                Undo.DropLast();
                break;
            case ModalKind.Wall:
                wallPts.Clear();
                break;
            case ModalKind.Line:
                linePts.Clear(); snapMarker = null;
                break;
            case ModalKind.Opening:
                CancelOpening();
                break;
        }
        SetStatus("Cancelled");
    }

    // ---------------------------------------------------------------- drawing

    void ModalPreviewLines()
    {
        if (modal.Kind == ModalKind.LoopCut)
        {
            var y = new Vector4(1f, 0.85f, 0.1f, 1);
            foreach (var (a, b) in modal.Preview) lines.Line(a, b, y);
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale && modal.Axis >= 0)
        {
            var c = modal.Axis == 0 ? new Vector4(0.95f, 0.25f, 0.3f, 1) : modal.Axis == 1 ? new Vector4(0.45f, 0.85f, 0.2f, 1) : new Vector4(0.3f, 0.55f, 1f, 1);
            var axes = modal.Axes;
            if (modal.Plane)
            {
                var a1 = axes[(modal.Axis + 1) % 3] * 2000; var a2 = axes[(modal.Axis + 2) % 3] * 2000;
                lines.Line(modal.Pivot - a1, modal.Pivot + a1, c * 0.8f);
                lines.Line(modal.Pivot - a2, modal.Pivot + a2, c * 0.8f);
            }
            else lines.Line(modal.Pivot - axes[modal.Axis] * 2000, modal.Pivot + axes[modal.Axis] * 2000, c);
        }
        if (modal.Kind == ModalKind.Extrude)
            lines.Line(modal.Pivot - modal.Normal * 2000, modal.Pivot + modal.Normal * 2000, new Vector4(0.3f, 0.75f, 1f, 1));
        WallPreviewLines();
        LinePreviewLines();
        OpeningLines();
    }

    void ModalOverlay(ImDrawListPtr dl, Viewport vp)
    {
        ExtrudeHandleOverlay(dl, vp);
        OpeningOverlay(dl, vp);
        if (modal.Kind == ModalKind.Opening && vp == Viewports[ActiveViewport]) OpeningPanel();
        if (vp == Viewports[ActiveViewport]) { LoopCutPanel(); KnifePanel(); }
        if (modal.Kind == ModalKind.None || vp != Viewports[ActiveViewport]) return;
        // header strip at the top of the viewport, like Blender's tool header
        var text = modal.Header;
        if (string.IsNullOrEmpty(text)) return;
        var size = ImGui.CalcTextSize(text);
        var p = new Vector2(vp.X + (vp.W - size.X) / 2, vp.Y + 6);
        dl.AddRectFilled(p - new Vector2(8, 3), p + size + new Vector2(8, 3), 0xD0202020, 2);
        dl.AddText(p, ImGui.ColorConvertFloat4ToU32(Theme.Gold), text);
        if (modal.Kind is ModalKind.Rotate or ModalKind.Scale or ModalKind.Inset or ModalKind.Chamfer)
            dl.AddLine(modal.PivotScreen, Input.Mouse, 0x80FFFFFF, 1f);
        if (modal.Kind == ModalKind.Knife && knifeA.HasValue) dl.AddLine(knifeA.Value, KnifeEnd(), 0xFF40E0FF, 2f);
        if (modal.Kind == ModalKind.QuickSlice && knifeA.HasValue)
        {
            // the slice goes right through: the line is drawn across the whole view, the two clicks marked
            var a = knifeA.Value; var b = KnifeEnd(); var d = b - a;
            if (d.LengthSquared() > 1)
            {
                d = Vector2.Normalize(d) * (vp.W + vp.H) * 2;
                dl.PushClipRect(new Vector2(vp.X, vp.Y), new Vector2(vp.X + vp.W, vp.Y + vp.H), true);
                dl.AddLine(a - d, b + d, 0xC040E0FF, 1.5f);
                dl.PopClipRect();
            }
            dl.AddCircleFilled(a, 4, 0xFF40E0FF); dl.AddCircleFilled(b, 4, 0xFF40E0FF);
        }
        if (modal.Kind == ModalKind.TargetWeld && Current is { Mesh: not null } tw)
        {
            var sc = ScreenVerts(vp, tw);
            Vector2 S(int i) => new(vp.X + sc.P[i].X, vp.Y + sc.P[i].Y);
            if (twSrc >= 0 && twSrc < sc.P.Length && sc.Ok[twSrc])
            {
                var a = S(twSrc); var b = twHover >= 0 && twHover < sc.P.Length ? S(twHover) : Input.Mouse;
                dl.AddLine(a, b, 0xFF40E0FF, 2f);
                dl.AddCircleFilled(a, 5, 0xFF40E0FF);
            }
            if (twHover >= 0 && twHover < sc.P.Length && sc.Ok[twHover]) dl.AddCircle(S(twHover), 8, ImGui.ColorConvertFloat4ToU32(Theme.Gold), 16, 2f);
        }
        FfdOverlay(dl, vp);
        WeightPaintOverlay(dl, vp);
        WallOverlay(dl, vp);
        LineOverlay(dl, vp);
    }
}
