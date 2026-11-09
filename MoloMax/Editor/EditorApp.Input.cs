using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Viewport mouse handling: navigation (per keymap), click / box / loop selection, the 3D cursor,
/// gizmo dragging and the right-click context menu. Modal tools live in EditorApp.Modal.cs and
/// keyboard shortcuts in EditorApp.Keymap.cs.
/// </summary>
public sealed partial class EditorApp
{
    enum NavMode { None, Pan, Orbit, Zoom, Fly }
    NavMode nav = NavMode.None;
    Viewport navViewport;
    System.Windows.Forms.MouseButtons navButton;

    bool lmbTracking, boxSelecting;
    Vector2 boxStart;
    Viewport boxViewport;

    bool transforming;
    Viewport xfViewport;
    Vector2 xfMouseStart;
    float xfParamStart;
    Vector3 xfPlaneStart, xfPivot;
    Vector3[] xfAxes;
    readonly Dictionary<int, (Vector3 Pos, Quaternion Rot, Vector3 Scale)> xfObjects = new();
    readonly Dictionary<int, Vector3> xfVerts = new(); // vertex index → world position at drag start
    bool rmbTracking; Vector2 rmbStart;
    Vector3 xfPivotLive;

    /// <summary>Blender's 3D cursor: where Shift+A adds things.</summary>
    public Vector3 Cursor3D;

    Viewport Hovered()
    {
        for (int i = 0; i < Viewports.Length; i++)
        {
            if (Maximized && i != ActiveViewport) continue;
            if (Viewports[i].Contains(Input.Mouse)) return Viewports[i];
        }
        return null;
    }

    // ---------------------------------------------------------------- pivot / gizmo placement

    (int, int, long) pivotKey; Vector3 pivotLocal;
    (int Obj, long Topo, SubObject Level, long Sig, int Frame, int Count) ptsKey; HashSet<int> ptsCache = new(); long ptsSig;
    /// <summary>Changes when the polygons change (not when points only move): mesh object, counts, a few polygon sizes.</summary>
    static long TopoSig(PolyMesh m)
    {
        long h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m) * 1000003L + m.Faces.Count * 7919L + m.Verts.Count;
        int n = m.Faces.Count;
        for (int i = 0; i < Math.Min(n, 8); i++) { var fv = m.Faces[i].V; h = h * 31 + fv.Length + (fv.Length > 0 ? fv[0] * 17L : 0); }
        for (int i = Math.Max(8, n - 8); i < n; i++) { var fv = m.Faces[i].V; h = h * 31 + fv.Length + (fv.Length > 0 ? fv[0] * 17L : 0); }
        return h;
    }

    /// <summary>The points of the edit-mode selection (vertices of the selected edges / faces), worked out once per frame and kept while nothing changes.</summary>
    HashSet<int> SelectedPoints(SceneObject cur)
    {
        int frame = ImGui.GetFrameCount();
        long topo = TopoSig(cur.Mesh);
        var s = SubSel(cur);
        // same frame: only the size of the set is compared (a selection changed by code within the frame still shows)
        int count = Level switch { SubObject.Vertex => s.Verts.Count, SubObject.Edge or SubObject.Border => s.Edges.Count, _ => s.Faces.Count };
        if (ptsKey.Obj == cur.Id && ptsKey.Frame == frame && ptsKey.Level == Level && ptsKey.Topo == topo && ptsKey.Count == count) return ptsCache;
        long sig = Level switch { SubObject.Vertex => SetSig(s.Verts), SubObject.Edge or SubObject.Border => SetSig(s.Edges), _ => SetSig(s.Faces) } ^ (long)Level << 56;
        if (ptsKey.Obj == cur.Id && ptsKey.Topo == topo && ptsKey.Level == Level && ptsKey.Sig == sig) { ptsKey.Frame = frame; ptsKey.Count = count; return ptsCache; }
        ptsCache = s.VerticesOf(cur.Mesh, Level);
        ptsKey = (cur.Id, topo, Level, sig, frame, count); ptsSig = sig;
        return ptsCache;
    }

    /// <summary>Median of the selection (objects' origins, or the selected vertices in edit mode).</summary>
    bool SelectionPivot(out Vector3 pivot, out Matrix4x4 orient)
    {
        pivot = default; orient = Matrix4x4.Identity;
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            var verts = SelectedPoints(cur);
            if (verts.Count == 0) return false;
            // cached: the gizmo, the hover and every viewport ask for it several times per frame
            if (pivotKey == (cur.Id, cur.Version, ptsSig)) pivot = Vector3.Transform(pivotLocal, cur.World);
            else
            {
                var c = Vector3.Zero; int k = 0; var vs = cur.Mesh.Verts;
                foreach (var v in verts) if (v < vs.Count) { c += vs[v]; k++; }
                if (k == 0) return false;
                pivotLocal = c / k; pivotKey = (cur.Id, cur.Version, ptsSig);
                pivot = Vector3.Transform(pivotLocal, cur.World);
            }
            if (LocalCoords) orient = Matrix4x4.CreateFromQuaternion(cur.Rotation);
            return true;
        }
        var objs = Selected.Select(Scene.Find).Where(o => o != null && !o.Hidden).ToList();
        if (objs.Count == 0) return false;
        if (objs.Count == 1) { pivot = objs[0].Position; if (LocalCoords) orient = Matrix4x4.CreateFromQuaternion(objs[0].Rotation); return true; }
        var acc = Vector3.Zero; foreach (var o in objs) acc += o.Position;
        pivot = acc / objs.Count;
        if (LocalCoords && cur != null) orient = Matrix4x4.CreateFromQuaternion(cur.Rotation);
        return true;
    }

    Gizmo.Layout GizmoLayout(Viewport vp)
    {
        if (Tool == Tool.Select || modal.Kind != ModalKind.None || !SelectionPivot(out var p, out var o)) return default;
        if (transforming) p = xfPivotLive;
        return gizmo.Compute(vp, p, o);
    }

    // ---------------------------------------------------------------- per-frame input

    void HandleViewportInput()
    {
        var io = ImGui.GetIO();
        bool modalOn = modal.Kind != ModalKind.None;
        bool busy = nav != NavMode.None || transforming || lmbTracking || rmbTracking || modalOn;
        bool uiMouse = io.WantCaptureMouse && !busy;
        var hv = Hovered();
        bool blender = Settings.Keymap == Keymap.Blender, max = Settings.Keymap == Keymap.Max;
        if (!Input.Ctrl) ctrlLatch = false;

        // ---- middle button on the gizmo moves / rotates / scales the selection (like the left button); elsewhere it navigates
        if (nav == NavMode.None && !transforming && !modalOn && hv != null && !uiMouse && Input.Pressed(MouseButtons.Middle) && !Input.Alt && !Input.Ctrl
            && Tool != Tool.Select && Selected.Count > 0 && !SculptMode && !paintMode)
        {
            var h = gizmo.HitTest(hv, GizmoLayout(hv), Tool, Input.Mouse);
            if ((h == GizmoHandle.None || Level != SubObject.Object) && snapHover is { } mgrab && Tool == Tool.Move)
            {
                // snap on, the middle button on a point of the selection: carry that point onto a vertex / edge / face
                ActiveViewport = Array.IndexOf(Viewports, hv);
                shiftClone = false;
                BeginTransform(hv, GizmoHandle.View);
                snapFrom = mgrab;
                xfButton = MouseButtons.Middle;
            }
            else if (h == GizmoHandle.None && Tool == Tool.Scale)
            {
                // Scale tool, middle button anywhere: like S in Blender, the size follows the mouse's distance to the centre
                ActiveViewport = Array.IndexOf(Viewports, hv);
                shiftClone = false;
                BeginTransform(hv, GizmoHandle.View);
                xfButton = MouseButtons.Middle;
                xfRadialScale = true;
                if (SelectionPivot(out var pv, out _)) xfPivotScreen = ScreenOf(hv, pv);
            }
            else if (h == GizmoHandle.None && Tool == Tool.Move && MiddleOnSelection(hv))
            {
                // Move tool, middle button on the selection itself (not on the gizmo): it follows the mouse in the view plane,
                // like 3ds Max; middle button on empty space still navigates
                ActiveViewport = Array.IndexOf(Viewports, hv);
                shiftClone = false;
                if (Input.Shift && Level == SubObject.Object) { CloneSelection(); shiftClone = lastCloneMap.Count > 0; }
                else if (Input.Shift && Level != SubObject.Object) SubShiftClone();
                BeginTransform(hv, GizmoHandle.View);
                xfButton = MouseButtons.Middle;
            }
            else if (h != GizmoHandle.None)
            {
                ActiveViewport = Array.IndexOf(Viewports, hv);
                shiftClone = false;
                if (Input.Shift && Level == SubObject.Object) { CloneSelection(); shiftClone = lastCloneMap.Count > 0; }
                else if (Input.Shift && Level != SubObject.Object) SubShiftClone(); // edit mode: the copy of the faces / edges moves
                gizmo.Hover = h;
                BeginTransform(hv, h);
                xfButton = MouseButtons.Middle;
            }
        }

        // ---- navigation
        if (nav == NavMode.None && !transforming && hv != null && !uiMouse)
        {
            if (blender)
            {
                // Blender: MMB orbit, Shift+MMB pan, Ctrl+MMB zoom
                if (Input.Pressed(MouseButtons.Middle))
                    StartNav(hv, Input.Shift ? NavMode.Pan : Input.Ctrl ? NavMode.Zoom : NavMode.Orbit, MouseButtons.Middle);
            }
            else if (max)
            {
                // 3ds Max: Alt+MMB orbit, Ctrl+Alt+MMB zoom, MMB pan
                if (Input.Pressed(MouseButtons.Middle))
                    StartNav(hv, Input.Alt ? (Input.Ctrl ? NavMode.Zoom : NavMode.Orbit) : NavMode.Pan, MouseButtons.Middle);
            }
            else
            {
                // Industry Compatible: Alt+LMB orbit, Alt+MMB pan, Alt+RMB zoom (plain MMB does nothing)
                if (Input.Alt && Input.Pressed(MouseButtons.Left) && !modalOn) { StartNav(hv, NavMode.Orbit, MouseButtons.Left); navDouble = Input.DoubleClick; }
                else if (Input.Alt && Input.Pressed(MouseButtons.Middle)) StartNav(hv, NavMode.Pan, MouseButtons.Middle);
                else if (Input.Alt && Input.Pressed(MouseButtons.Right) && !modalOn) StartNav(hv, NavMode.Zoom, MouseButtons.Right);
            }
        }
        if (nav != NavMode.None)
        {
            var d = Input.MouseDelta; var cam = navViewport.Camera;
            if (nav == NavMode.Pan) cam.Pan(d.X, d.Y, navViewport.H);
            else if (nav == NavMode.Fly)
            {
                FlyLook(cam, d);
                // the wheel sets the fly speed
                if (MathF.Abs(Input.Wheel) > 0) { FlySpeedFactor = Math.Clamp(FlySpeedFactor * MathF.Pow(1.25f, Math.Sign(Input.Wheel)), 0.1f, 20f); flySpeedShownAt = ImGui.GetTime(); }
            }
            else if (nav == NavMode.Orbit) cam.Orbit(d.X, d.Y);
            else cam.Zoom((d.X - d.Y) * 0.03f);
            navTravel += d.Length();
            if (!Input.Down(navButton))
            {
                // Industry: Alt+double-click without dragging = edge ring (mesh.edgering_select)
                if (navButton == MouseButtons.Left && navDouble && navTravel < 4 && Level != SubObject.Object)
                    LoopSelect(navViewport, Input.Mouse, ring: true);
                nav = NavMode.None;
            }
            return; // a navigation drag owns the mouse
        }
        // MAP mode: a prop follows the mouse until it is dropped (takes the wheel to turn it)
        if (MapMode && MapPlacementInput(hv, uiMouse)) return;
        if (hv != null && !uiMouse && MathF.Abs(Input.Wheel) > 0 && modal.Kind is not (ModalKind.Chamfer or ModalKind.LoopCut)) { EndWalk(hv.Camera); hv.Camera.Zoom(Input.Wheel); }

        // ---- modal tools take the buttons
        if (modalOn) { ModalMouse(hv); return; }

        // ---- material pipette: the next click on a face takes its material
        if (EyedropperInput(hv, uiMouse)) return;

        // ---- Molo Paint strokes and decal placement take the left button
        if (DecalPlaceInput(hv, uiMouse) || SculptInput(hv, uiMouse) || PaintInput(hv, uiMouse)) return;

        // ---- MAP mode: a click on the far world loads that area (unless the gizmo is under the mouse)
        if (MapMode && !busy && hv != null && !uiMouse && Input.Pressed(MouseButtons.Left) && !Input.Alt
            && (Tool == Tool.Select || gizmo.HitTest(hv, GizmoLayout(hv), Tool, Input.Mouse) == GizmoHandle.None) && !ViewCube.HitTest(hv, Input.Mouse, out _) && MapClick(hv)) return;

        // ---- extrude arrow on selected polygons (takes the click before the transform gizmo)
        if (!busy && ExtrudeHandleInput(hv, uiMouse)) return;

        // ---- gizmo hover
        if (!busy && hv != null && !uiMouse && Tool != Tool.Select)
            gizmo.Hover = gizmo.HitTest(hv, GizmoLayout(hv), Tool, Input.Mouse);
        else if (!transforming) gizmo.Hover = GizmoHandle.None;
        // the scale box: its handles take the click when the gizmo is not under the mouse
        cageHover = !busy && hv != null && !uiMouse && gizmo.Hover == GizmoHandle.None ? CageHit(hv, Input.Mouse) : cageDrag ? cageHover : -1;
        UpdateSnapHover(!busy && !uiMouse ? hv : null);

        // ---- view cube held: drag turns the view (the cube: free orbit, the ring: around the vertical), a click picks a face
        if (cubeDrag)
        {
            var d = Input.MouseDelta; cubeMoved += d.Length();
            var cam = cubeViewport.Camera;
            // like grabbing the cube: the view turns with the mouse
            if (cubeMoved > 3) { if (cubeOnFace) cam.Orbit(d.X, d.Y); else cam.Orbit(d.X, 0); }
            if (!Input.Down(MouseButtons.Left))
            {
                cubeDrag = false;
                if (cubeMoved <= 3 && cubeOnFace) { cam.SetView(cubeKind); if (cubeKind == ViewKind.Perspective) FrameSelection(cubeViewport); }
            }
            return;
        }

        // ---- left button
        if (Input.Pressed(MouseButtons.Left) && hv != null && !uiMouse)
        {
            ActiveViewport = Array.IndexOf(Viewports, hv);
            if (ViewCube.InArea(hv, Input.Mouse))
            {
                // the view cube: a click picks a face (on release), a press held and dragged turns the view
                cubeDrag = true; cubeMoved = 0; cubeViewport = hv;
                cubeOnFace = ViewCube.HitTest(hv, Input.Mouse, out cubeKind);
            }
            else if (snapHover is { } vgrab && Tool == Tool.Move && Level != SubObject.Object && Selected.Count > 0)
            {
                // edit mode, snap on, the mouse on a selected vertex (even where the gizmo sits): a drag carries that vertex
                pendingGrab = vgrab; pendingGrabVp = hv; pendingGrabStart = Input.Mouse; lmbDouble = Input.DoubleClick;
            }
            else if (cageHover >= 0 && Tool == Tool.Scale)
            {
                // a handle of the scale box: stretches the selection from the opposite side
                shiftClone = false;
                BeginCage(hv, cageHover);
                xfButton = MouseButtons.Left;
            }
            else if (Tool != Tool.Select && gizmo.Hover != GizmoHandle.None)
            {
                // Shift+drag on the gizmo clones the selection and moves the clones (every keymap); Clone Options opens on release
                shiftClone = false;
                if (Input.Shift && Level == SubObject.Object && Selected.Count > 0) { CloneSelection(); shiftClone = lastCloneMap.Count > 0; }
                else if (Input.Shift && Level != SubObject.Object) SubShiftClone(); // edit mode: the copy of the faces / edges moves
                BeginTransform(hv, gizmo.Hover);
                xfButton = MouseButtons.Left;
            }
            else if (snapHover is { } grab && Tool == Tool.Move && Selected.Count > 0)
            {
                // snap on, a point of the selection under the mouse: a drag grabs it there and carries it onto a vertex / edge / face;
                // a plain click still selects (the grab starts only once the mouse moves)
                pendingGrab = grab; pendingGrabVp = hv; pendingGrabStart = Input.Mouse; lmbDouble = Input.DoubleClick;
            }
            else { lmbTracking = true; lmbDouble = Input.DoubleClick; boxStart = Input.Mouse; boxViewport = hv; boxSelecting = false; }
        }
        if (pendingGrab is { } pg)
        {
            if (!Input.Down(MouseButtons.Left)) { pendingGrab = null; ClickSelect(pendingGrabVp, pendingGrabStart); }
            else if (Vector2.Distance(Input.Mouse, pendingGrabStart) > 5)
            {
                pendingGrab = null;
                shiftClone = false;
                if (Input.Shift && Level == SubObject.Object) { CloneSelection(); shiftClone = lastCloneMap.Count > 0; }
                else if (Input.Shift && Level != SubObject.Object) SubShiftClone();
                BeginTransform(pendingGrabVp, GizmoHandle.View);
                snapFrom = pg;
                xfButton = MouseButtons.Left;
            }
        }
        bool wasTransforming = transforming;
        if (transforming)
        {
            UpdateTransform();
            if (Input.Released(xfButton) || !Input.Down(xfButton)) EndTransform();
            if (Input.Pressed(MouseButtons.Right)) CancelTransform();
        }
        if (lmbTracking)
        {
            // a click that moves a little (mouse / pen jitter) stays a click: the box starts past 6 px
            if (!boxSelecting && Vector2.Distance(Input.Mouse, boxStart) > 6) boxSelecting = true;
            if (Input.Released(MouseButtons.Left) || !Input.Down(MouseButtons.Left))
            {
                var bd = Input.Mouse - boxStart;
                if (boxSelecting && (MathF.Abs(bd.X) >= 6 || MathF.Abs(bd.Y) >= 6)) BoxSelect(boxViewport, boxStart, Input.Mouse);
                else
                {
                    // Blender: Alt+click loop (Ctrl+Alt ring); Industry and 3ds Max: double-click loop
                    // faces: a quick second click is a second face (Ctrl / Shift adds it), never a face loop that runs over
                    // the whole mesh (a stair is one strip): whole parts are the Element level, Alt+click still loops in Standard keys
                    bool loop = blender ? Input.Alt : lmbDouble && Level is not (SubObject.Polygon or SubObject.Element) && !Input.Ctrl && !Input.Shift;
                    if (loop && Level != SubObject.Object) LoopSelect(boxViewport, boxStart, ring: blender && Input.Ctrl);
                    else ClickSelect(boxViewport, boxStart);
                }
                lmbTracking = false; boxSelecting = false;
            }
        }

        // ---- right button: Shift+RMB places the 3D cursor, a plain click opens the context menu
        if (Input.Pressed(MouseButtons.Right) && hv != null && !uiMouse && !wasTransforming)
        {
            ActiveViewport = Array.IndexOf(Viewports, hv);
            if (Input.Shift && !max) PlaceCursor(hv, Input.Mouse);
            else { rmbTracking = true; rmbStart = Input.Mouse; }
        }
        // right button held and moved: game-style look (fly); a click without moving still opens the menu
        // (not in the flat Top / Front / Left views: a small slip of the mouse during a right-click turned them into a
        // perspective camera far back; and 10 px so a shaky click still opens the menu)
        if (rmbTracking && Input.Down(MouseButtons.Right) && Vector2.Distance(rmbStart, Input.Mouse) >= 10 && hv != null && !hv.Camera.Ortho)
        {
            rmbTracking = false;
            StartNav(hv, NavMode.Fly, MouseButtons.Right);
            return;
        }
        if (rmbTracking && (Input.Released(MouseButtons.Right) || !Input.Down(MouseButtons.Right)))
        {
            rmbTracking = false;
            if (Vector2.Distance(rmbStart, Input.Mouse) < 10 || hv is { Camera.Ortho: true }) { quadCenter = Input.Mouse; OpenPopup("context"); }
        }
    }

    bool lmbDouble, navDouble;
    Vector3? pendingGrab; Viewport pendingGrabVp; Vector2 pendingGrabStart;
    bool cubeDrag, cubeOnFace; float cubeMoved; Viewport cubeViewport; ViewKind cubeKind;
    float navTravel;

    void StartNav(Viewport vp, NavMode mode, MouseButtons button)
    {
        if (mode != NavMode.Fly) EndWalk(vp.Camera); // orbit / pan / zoom: the walk mode leaves
        nav = mode; navViewport = vp; navButton = button; navTravel = 0; navDouble = false;
        ActiveViewport = Array.IndexOf(Viewports, vp);
    }

    public void FrameSelection(Viewport vp = null)
    {
        vp ??= Viewports[ActiveViewport];
        var objs = Selected.Count > 0 ? Selected.Select(Scene.Find).Where(o => o != null) : Scene.Objects.Where(o => !o.Hidden);
        // a Drawable node has no geometry of its own: frame its game meshes
        objs = objs.SelectMany(o => o.GtaType == GtaObjectType.Model ? Scene.Descendants(o).Prepend(o) : new[] { o }).Where(o => o.Mesh.Verts.Count > 0).Distinct().ToList();
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue); bool any = false;
        var cur = Current;
        if (cur != null && Level != SubObject.Object)
        {
            var verts = SubSel(cur).VerticesOf(cur.Mesh, Level);
            var w = cur.World;
            foreach (var v in verts) { var p = Vector3.Transform(cur.Mesh.Verts[v], w); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); any = true; }
        }
        if (!any)
            foreach (var o in objs) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); any = true; }
        if (!any) { mn = new Vector3(-5, -5, 0); mx = new Vector3(5, 5, 3); }
        vp.Camera.Frame(mn, mx);
    }

    public void FrameAll(Viewport vp = null)
    {
        vp ??= Viewports[ActiveViewport];
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue); bool any = false;
        foreach (var o in Scene.Objects.Where(o => !o.Hidden)) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); any = true; }
        if (!any) { mn = new Vector3(-5, -5, 0); mx = new Vector3(5, 5, 3); }
        vp.Camera.Frame(mn, mx);
    }

    // ---------------------------------------------------------------- 3D cursor

    /// <summary>Puts the 3D cursor on the surface under the mouse, or on the ground grid.</summary>
    void PlaceCursor(Viewport vp, Vector2 screen)
    {
        var ray = RayAt(vp, screen);
        var hit = Picking.PickObject(Scene, ray);
        Vector3 p;
        if (hit.HasValue) p = hit.Value.Point;
        else if (!Picking.RayGround(ray, 0, out p) && !Picking.RayPlane(ray, vp.Camera.Target, -vp.Camera.Forward, out p)) return;
        if (Snap) p = new Vector3(SnapValue(p.X, GridStep), SnapValue(p.Y, GridStep), SnapValue(p.Z, GridStep));
        Cursor3D = p;
        SetStatus($"3D cursor at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}");
    }

    public void CursorToSelected() { if (SelectionPivot(out var p, out _)) Cursor3D = p; }

    public void SelectionToCursor()
    {
        if (!SelectionPivot(out var pivot, out _)) return;
        var delta = Cursor3D - pivot;
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            DoMesh("Selection to Cursor", (o, m, s) =>
            {
                Matrix4x4.Invert(o.World, out var inv);
                var w = o.World;
                foreach (var v in s.VerticesOf(m, Level)) m.Verts[v] = Vector3.Transform(Vector3.Transform(m.Verts[v], w) + delta, inv);
            });
            return;
        }
        Do("Selection to Cursor", () => { foreach (var o in SelObjects()) o.Position += delta; });
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Blender / Industry: Shift toggles (click) or extends (box), Ctrl removes. 3ds Max: Ctrl adds (toggles on click), Alt removes.</summary>
    void ApplySelection<T>(HashSet<T> target, ICollection<T> hits, bool isClick)
    {
        bool max = Settings.Keymap == Keymap.Max;
        bool shift = max ? Input.Ctrl && !Input.Alt : Input.Shift;
        bool ctrl = max ? Input.Alt && !Input.Ctrl : Input.Ctrl && !Input.Alt && !isClick; // Blender/Industry: Ctrl+click selects, Ctrl+box removes
        if (ctrl) { foreach (var h in hits) target.Remove(h); return; }
        if (shift)
        {
            if (isClick && hits.Count > 0 && hits.All(target.Contains)) foreach (var h in hits) target.Remove(h);
            else foreach (var h in hits) target.Add(h);
            return;
        }
        target.Clear();
        foreach (var h in hits) target.Add(h);
    }

    // ---------------------------------------------------------------- visibility for edit-mode selection

    /// <summary>Edit-mode selection only takes what you can see ("Visible only", Alt+Z). Off (default, like 3ds Max): clicks and
    /// boxes select through the mesh, hidden vertices / edges / faces included.</summary>
    public bool SelectVisibleOnly;
    /// <summary>Selection goes through the mesh (the default).</summary>
    public bool XRay { get => !SelectVisibleOnly; set => SelectVisibleOnly = !value; }

    sealed class Occluder
    {
        public Vector3[] Wv; public SceneObject Obj;
        List<(int A, int B, int C)> tris;
        /// <summary>Triangles in world space, built only when needed (box select with "Visible only").</summary>
        public List<(int A, int B, int C)> Tris
        {
            get
            {
                if (tris != null) return tris;
                tris = new();
                var m = Obj.Mesh;
                foreach (var f in m.Faces)
                    foreach (var (a, b, c) in Triangulator.Triangulate(m, f)) tris.Add((f.V[a], f.V[b], f.V[c]));
                return tris;
            }
        }
        public Occluder(SceneObject o)
        {
            var m = o.Mesh; var w = o.World; Obj = o;
            Wv = new Vector3[m.Verts.Count];
            if (Wv.Length < 20000) for (int i = 0; i < Wv.Length; i++) Wv[i] = Vector3.Transform(m.Verts[i], w);
            else System.Threading.Tasks.Parallel.For(0, (Wv.Length + 8191) / 8192, b =>
            {
                int e = Math.Min(Wv.Length, (b + 1) * 8192);
                for (int i = b * 8192; i < e; i++) Wv[i] = Vector3.Transform(m.Verts[i], w);
            });
        }
    }

    /// <summary>Depth (along the view) of the first surface of this mesh under the mouse, only the sides that are drawn (a
    /// room's walls face inwards: seen from outside, the near wall is not drawn and must not hide the far one's edges);
    /// null when the mouse is off the mesh. One pick for the whole click instead of one ray per candidate against every triangle.</summary>
    float? FrontDepth(Viewport vp, SceneObject o, Vector2 local)
    {
        var ray = vp.Camera.ScreenRay(local.X, local.Y, vp.W, vp.H);
        var hit = Picking.PickFace(o, ray, cullBack: CulledFor(o));
        return hit is { } h ? ViewDepth(vp, h.Point) : null;
    }

    static float ViewDepth(Viewport vp, Vector3 wp)
    {
        var eye = vp.Camera.ScreenRay(vp.W * 0.5f, vp.H * 0.5f, vp.W, vp.H).Origin;
        return Vector3.Dot(wp - eye, Vector3.Normalize(vp.Camera.Forward));
    }

    /// <summary>The point is not behind the surface met under the mouse (with a little tolerance for points on that surface).</summary>
    static bool NotBehind(Viewport vp, Vector3 wp, float? front)
        => front is not { } f || ViewDepth(vp, wp) <= f + MathF.Max(0.02f, MathF.Abs(f) * 0.01f);

    /// <summary>True when nothing of the mesh is in front of this world point, seen from the viewport camera.</summary>
    bool PointVisible(Viewport vp, Occluder oc, Vector3 wp)
    {
        if (XRay || vp.WireframeOnly) return true;
        if (!vp.Camera.Project(wp, vp.W, vp.H, out var sp, out _)) return false;
        var ray = vp.Camera.ScreenRay(sp.X, sp.Y, vp.W, vp.H);
        var dir = Vector3.Normalize(ray.Direction);
        var r = new Ray(ray.Origin, dir);
        float target = Vector3.Dot(wp - r.Origin, dir);
        float eps = MathF.Max(1e-4f, target * 2e-3f);
        foreach (var (a, b, c) in oc.Tris)
        {
            float t = Picking.RayTriangle(r, oc.Wv[a], oc.Wv[b], oc.Wv[c]);
            if (t > 0 && t < target - eps) return false;
        }
        return true;
    }

    EdgeKey? EdgeUnder(Viewport vp, SceneObject o, Vector2 local, float maxPx = 9f, IEnumerable<EdgeKey> candidates = null, bool visibleOnly = false)
    {
        var m = o.Mesh; var w = o.World;
        var near = new List<(float D, EdgeKey E, Vector2 A, Vector2 B)>();
        var sc = ScreenVerts(vp, o); var P = sc.P; var Ok = sc.Ok; int nv = P.Length;
        var cam = vp.Camera; var eye = cam.Eye; var fwd = Vector3.Normalize(cam.Forward);
        foreach (var e in candidates ?? Edges(o))
        {
            if (e.A >= nv || e.B >= nv) continue;
            Vector2 a, b;
            if (Ok[e.A] && Ok[e.B]) { a = P[e.A]; b = P[e.B]; }
            else if (Ok[e.A] || Ok[e.B])
            {
                // one end behind the camera (inside a room, close up): the edge is cut where it enters the view, so it can
                // still be clicked (before, such edges were skipped and many walls' edges could not be taken)
                if (cam.Ortho) continue;
                var pa = Vector3.Transform(m.Verts[e.A], w); var pb = Vector3.Transform(m.Verts[e.B], w);
                float da = Vector3.Dot(pa - eye, fwd), db = Vector3.Dot(pb - eye, fwd), dn = cam.Near * 2;
                if (MathF.Abs(db - da) < 1e-6f) continue;
                var pc = Vector3.Lerp(pa, pb, Math.Clamp((dn - da) / (db - da), 0, 1));
                if (!cam.Project(pc, vp.W, vp.H, out var cs, out _)) continue;
                if (Ok[e.A]) { a = P[e.A]; b = cs; } else { a = cs; b = P[e.B]; }
            }
            else continue;
            // quick reject: the mouse is far outside the edge's box
            if (local.X < MathF.Min(a.X, b.X) - maxPx || local.X > MathF.Max(a.X, b.X) + maxPx || local.Y < MathF.Min(a.Y, b.Y) - maxPx || local.Y > MathF.Max(a.Y, b.Y) + maxPx) continue;
            float d = SegDist(local, a, b);
            if (d < maxPx) near.Add((d, e, a, b));
        }
        if (near.Count == 0) return null;
        near.Sort((x, y) => x.D.CompareTo(y.D));
        if (!visibleOnly || vp.WireframeOnly) return near[0].E;
        // the point of each edge nearest to the mouse ray, in world space
        var mray = cam.ScreenRay(local.X, local.Y, vp.W, vp.H);
        Vector3 Wp(EdgeKey e, Vector2 a, Vector2 b)
        {
            var pa = Vector3.Transform(m.Verts[e.A], w); var pb = Vector3.Transform(m.Verts[e.B], w);
            var dir = pb - pa; float len = dir.Length();
            if (len < 1e-6f) return pa;
            float t = Picking.RayLineParam(mray, pa, dir / len);
            return pa + dir / len * Math.Clamp(t, 0, len);
        }
        var front = FrontDepth(vp, o, local);
        if (XRay)
        {
            // selection goes through the mesh, but on a dense mesh an edge hidden behind often lands a pixel nearer the
            // mouse than the one you see: among the edges as near as the nearest (+3 px), the one you can see wins
            foreach (var (d, e, a, b) in near)
            {
                if (d > near[0].D + 3f) break;
                if (NotBehind(vp, Wp(e, a, b), front)) return e;
            }
            return near[0].E;
        }
        foreach (var (_, e, a, b) in near)
            if (NotBehind(vp, Wp(e, a, b), front)) return e;
        return null;
    }

    PickHit? clickHit;

    void ClickSelect(Viewport vp, Vector2 screen)
    {
        clickHit = null;
        var local = vp.Local(screen);
        var ray = vp.Camera.ScreenRay(local.X, local.Y, vp.W, vp.H);
        var cur = Current;
        if (Level != SubObject.Object && cur != null && cur.ModEdit == null && !Input.Shift && !Input.Ctrl && !Input.Alt)
        {
            // clicking another mesh in a sub-object level goes on editing that mesh, same level
            // (a GTA room is cut in many meshes: the face under the mouse is often not the one being edited)
            var other = PickVisibleFace(ray); // only what is drawn: the hidden back of a one-sided wall doesn't take the click
            if (other is { } oh && oh.Object == cur && Level is SubObject.Polygon or SubObject.Element) clickHit = oh; // reused below (no second pass over the mesh)
            if (other.HasValue && other.Value.Object != cur && other.Value.Object.Mesh != null && !other.Value.Object.Frozen)
            {
                var mine = Picking.PickFace(cur, ray, cullBack: CulledFor(cur));
                bool vertsNear = false;
                // an edge / vertex of the mesh being edited under the mouse keeps the click on it, even when another mesh's
                // face lies on top (pieces of a room overlap on their borders): the edit doesn't jump to the other mesh
                if (Level is SubObject.Vertex or SubObject.Edge or SubObject.Border)
                {
                    // ... as long as that edge / vertex is not hidden behind the other mesh's face
                    float tol = MathF.Max(0.02f, other.Value.Distance * 0.01f);
                    var cw = cur.World;
                    if (Level == SubObject.Vertex)
                    {
                        int vi = NearestVisibleVertex(vp, cur, local);
                        vertsNear = vi >= 0 && Vector3.Distance(ray.Origin, Vector3.Transform(cur.Mesh.Verts[vi], cw)) <= other.Value.Distance + tol;
                    }
                    else if (EdgeUnder(vp, cur, local, 9f, Level == SubObject.Border ? MeshOps.BorderEdges(cur.Mesh) : null, visibleOnly: true) is { } ek)
                    {
                        var pa = Vector3.Transform(cur.Mesh.Verts[ek.A], cw); var pb = Vector3.Transform(cur.Mesh.Verts[ek.B], cw);
                        var dir = pb - pa; float len = dir.Length();
                        var pt = len < 1e-6f ? pa : pa + dir / len * Math.Clamp(Picking.RayLineParam(ray, pa, dir / len), 0, len);
                        vertsNear = Vector3.Dot(pt - ray.Origin, Vector3.Normalize(ray.Direction)) <= other.Value.Distance + tol;
                    }
                }
                if (!vertsNear && (!mine.HasValue || other.Value.Distance < mine.Value.Distance - 1e-3f))
                {
                    var lv = Level;
                    Selected.Clear(); Selected.Add(other.Value.Object.Id); ActiveId = other.Value.Object.Id;
                    Level = lv; cur = other.Value.Object;
                    SetStatus($"Editing {cur.Name}");
                }
            }
        }
        if (Level != SubObject.Object && cur != null)
        {
            var s = SubSel(cur); var m = cur.Mesh; var w = cur.World;
            switch (Level)
            {
                case SubObject.Vertex:
                    {
                        int best = NearestVisibleVertex(vp, cur, local);
                        ApplySelection(s.Verts, best >= 0 ? new[] { best } : Array.Empty<int>(), true);
                        break;
                    }
                case SubObject.Edge:
                case SubObject.Border:
                    {
                        var e = EdgeUnder(vp, cur, local, 9f, Level == SubObject.Border ? MeshOps.BorderEdges(m) : null, visibleOnly: true);
                        ICollection<EdgeKey> hit = Array.Empty<EdgeKey>();
                        if (e.HasValue) hit = Level == SubObject.Border ? MeshOps.BorderLoop(m, e.Value) : new[] { e.Value };
                        ApplySelection(s.Edges, hit, true);
                        break;
                    }
                default:
                    {
                        var h = clickHit is { } ch && ch.Object == cur && cur.Modifiers is not { Count: > 0 } ? ch : Picking.PickFace(cur, ray, cullBack: CulledFor(cur));
                        clickHit = null;
                        // a piece with its faces turned the other way (a camera, a lamp...) is drawn as edges only: a click on it must
                        // still take it, not the wall behind. Its hidden back face is taken when it is in front of a different piece.
                        if (CulledFor(cur) && cur.Modifiers is not { Count: > 0 })
                        {
                            var hb = Picking.PickFace(cur, ray, cullBack: false);
                            if (hb is { } b && (!h.HasValue || b.Distance < h.Value.Distance - 1e-3f && !MeshSelect.Elements(m, new[] { h.Value.Face }, cur.Version).Contains(b.Face)))
                            { h = b; SetStatus("This face is turned inside out (flipped normals): Face Orientation shows it in red, Recalculate Outside fixes it"); }
                        }
                        ICollection<int> hit = Array.Empty<int>();
                        if (h.HasValue) hit = Level == SubObject.Element ? MeshSelect.Elements(m, new[] { h.Value.Face }, cur.Version) : new[] { h.Value.Face };
                        ApplySelection(s.Faces, hit, true);
                        break;
                    }
            }
            return;
        }
        var pick = Picking.PickObject(Scene, ray);
        // lights and Drawable nodes have no faces: they are picked by their marker on screen
        SceneObject empty = null; float bestPx = 12f;
        foreach (var e in Scene.Objects)
        {
            if (e.Hidden || e.Frozen || e.GtaType is not (GtaObjectType.Light or GtaObjectType.Model)) continue;
            if (!vp.Camera.Project(e.Position, vp.W, vp.H, out var sp, out _)) continue;
            float d = Vector2.Distance(sp, local);
            if (d < bestPx) { bestPx = d; empty = e; }
        }
        if (empty != null) pick = new PickHit(empty, -1, 0, empty.Position);
        else if (PickShape(vp, local) is { } line) pick = new PickHit(line, -1, 0, line.Position);
        if (pick.HasValue)
        {
            int id = pick.Value.Object.Id;
            bool wasSelected = Selected.Contains(id);
            ApplySelection(Selected, new[] { id }, true);
            // Blender: Shift+click on a selected but inactive object makes it active instead of deselecting
            if (Settings.Keymap != Keymap.Max && Input.Shift && wasSelected && ActiveId != id) { Selected.Add(id); ActiveId = id; }
            else if (Selected.Contains(id)) ActiveId = id;
        }
        else if (!Input.Shift && !Input.Ctrl && !Input.Alt) Selected.Clear();
        SelectionChanged();
    }

    /// <summary>Nearest vertex of o on screen (12 px) that is not hidden behind the mesh, or -1.</summary>
    int NearestVisibleVertex(Viewport vp, SceneObject o, Vector2 local)
    {
        var near = new List<(float D, int I)>();
        var sc = ScreenVerts(vp, o);
        for (int i = 0; i < sc.P.Length; i++)
        {
            if (!sc.Ok[i]) continue;
            float d = Vector2.Distance(sc.P[i], local);
            if (d <= 12f) near.Add((d, i));
        }
        near.Sort((x, y) => x.D.CompareTo(y.D));
        if (near.Count == 0) return -1;
        if (XRay || vp.WireframeOnly) return near[0].I;
        var front = FrontDepth(vp, o, local);
        foreach (var (_, i) in near)
            if (NotBehind(vp, Vector3.Transform(o.Mesh.Verts[i], o.World), front)) return i;
        return -1;
    }

    /// <summary>Alt+click (Blender) / double-click (Industry): edge loop, Ctrl+Alt+click: ring; face loop at polygon level.</summary>
    void LoopSelect(Viewport vp, Vector2 screen, bool ring)
    {
        var cur = Current; if (cur == null) return;
        var local = vp.Local(screen); var m = cur.Mesh; var s = SubSel(cur);
        EdgeKey? e;
        if (Level is SubObject.Polygon or SubObject.Element)
        {
            // the edge of the clicked face nearest the mouse decides the direction of the face loop
            var h = Picking.PickFace(cur, RayAt(vp, screen), cullBack: CulledFor(cur));
            if (!h.HasValue) return;
            e = EdgeUnder(vp, cur, local, 10000f, m.FaceEdges(m.Faces[h.Value.Face]));
            if (!e.HasValue) return;
            var ringEdge = MeshOps.EdgeRing(m, e.Value).Count > 1 ? e.Value : e.Value;
            ApplySelection(s.Faces, MeshOps.FaceLoop(m, ringEdge).ToList(), false);
            return;
        }
        e = EdgeUnder(vp, cur, local, 12f);
        if (!e.HasValue) return;
        var edges = ring ? MeshOps.EdgeRing(m, e.Value) : MeshOps.EdgeLoop(m, e.Value);
        if (Level == SubObject.Vertex) ApplySelection(s.Verts, edges.SelectMany(x => new[] { x.A, x.B }).Distinct().ToList(), false);
        else ApplySelection(s.Edges, edges.ToList(), false);
    }

    /// <summary>Blender L: select the linked part under the mouse.</summary>
    public void SelectLinkedUnderMouse()
    {
        var cur = Current; var vp = Hovered();
        if (cur == null || vp == null || Level == SubObject.Object) return;
        var h = Picking.PickFace(cur, RayAt(vp, Input.Mouse));
        if (!h.HasValue) return;
        var faces = MeshSelect.Elements(cur.Mesh, new[] { h.Value.Face });
        var s = SubSel(cur);
        var tmp = new PolySelection { Faces = faces };
        if (Level == SubObject.Vertex) s.Verts.UnionWith(tmp.VerticesOf(cur.Mesh, SubObject.Polygon));
        else if (Level is SubObject.Edge or SubObject.Border) { tmp.Convert(cur.Mesh, SubObject.Polygon, SubObject.Edge); s.Edges.UnionWith(tmp.Edges); }
        else s.Faces.UnionWith(faces);
    }

    /// <summary>Ctrl+L: grow the current selection to whole connected parts.</summary>
    public void SelectLinked()
    {
        var cur = Current; if (cur == null || Level == SubObject.Object) return;
        var s = SubSel(cur); var m = cur.Mesh;
        var verts = s.VerticesOf(m, Level);
        var seed = Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].V.Any(verts.Contains));
        var faces = MeshSelect.Elements(m, seed);
        var tmp = new PolySelection { Faces = faces };
        if (Level == SubObject.Vertex) s.Verts = tmp.VerticesOf(m, SubObject.Polygon);
        else if (Level is SubObject.Edge or SubObject.Border) { tmp.Convert(m, SubObject.Polygon, SubObject.Edge); s.Edges = tmp.Edges; }
        else s.Faces = faces;
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l = ab.LengthSquared();
        float t = l > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / l, 0, 1) : 0;
        return Vector2.Distance(p, a + ab * t);
    }

    void BoxSelect(Viewport vp, Vector2 s0, Vector2 s1)
    {
        var a = vp.Local(Vector2.Min(s0, s1)); var b = vp.Local(Vector2.Max(s0, s1));
        bool In(Vector3 wp) => vp.Camera.Project(wp, vp.W, vp.H, out var sp, out _) && sp.X >= a.X && sp.X <= b.X && sp.Y >= a.Y && sp.Y <= b.Y;
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            var s = SubSel(cur); var m = cur.Mesh; var w = cur.World;
            var inside = new bool[m.Verts.Count];
            var oc = XRay || vp.WireframeOnly ? null : new Occluder(cur);
            for (int i = 0; i < inside.Length; i++) inside[i] = In(Vector3.Transform(m.Verts[i], w)) && (oc == null || PointVisible(vp, oc, oc.Wv[i]));
            bool FaceIn(int f) { var c = Vector3.Transform(m.FaceCenter(f), w); return In(c) && (oc == null || PointVisible(vp, oc, c)); }
            switch (Level)
            {
                case SubObject.Vertex: ApplySelection(s.Verts, Enumerable.Range(0, inside.Length).Where(i => inside[i]).ToList(), false); break;
                case SubObject.Edge: ApplySelection(s.Edges, Edges(cur).Where(e => inside[e.A] && inside[e.B]).ToList(), false); break;
                case SubObject.Border: ApplySelection(s.Edges, MeshOps.BorderEdges(m).Where(e => inside[e.A] && inside[e.B]).ToList(), false); break;
                case SubObject.Polygon: ApplySelection(s.Faces, Enumerable.Range(0, m.Faces.Count).Where(FaceIn).Union(BoxRayFaces(vp, cur, s0, s1)).ToList(), false); break;
                case SubObject.Element:
                    ApplySelection(s.Faces, MeshSelect.Elements(m, Enumerable.Range(0, m.Faces.Count).Where(FaceIn).Union(BoxRayFaces(vp, cur, s0, s1))).ToList(), false); break;
            }
            return;
        }
        var hits = new List<int>();
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Frozen) continue;
            var w = o.World;
            if (o.Mesh.Verts.Any(v => In(Vector3.Transform(v, w)))) hits.Add(o.Id);
            else if (o.GtaType is GtaObjectType.Light or GtaObjectType.Model && In(o.Position)) hits.Add(o.Id);
        }
        // crossing (3ds Max): a box drawn inside a big object, with none of its vertices in it, still picks
        // what the box touches on screen
        foreach (var p in BoxSamples(s0, s1))
        {
            var lp = vp.Local(p);
            var hit = Picking.PickObject(Scene, vp.Camera.ScreenRay(lp.X, lp.Y, vp.W, vp.H));
            if (hit.HasValue && !hit.Value.Object.Hidden && !hit.Value.Object.Frozen && !hits.Contains(hit.Value.Object.Id)) hits.Add(hit.Value.Object.Id);
        }
        ApplySelection(Selected, hits, false);
        if (hits.Count > 0 && Selected.Contains(hits[0]) && !Selected.Contains(ActiveId)) ActiveId = hits[0];
        SelectionChanged();
    }

    /// <summary>Screen points spread over a selection box (about every 6 px, at most 32 x 32).</summary>
    static IEnumerable<Vector2> BoxSamples(Vector2 s0, Vector2 s1)
    {
        var a = Vector2.Min(s0, s1); var b = Vector2.Max(s0, s1);
        int nx = Math.Clamp((int)((b.X - a.X) / 6) + 1, 2, 32), ny = Math.Clamp((int)((b.Y - a.Y) / 6) + 1, 2, 32);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                yield return new Vector2(a.X + (b.X - a.X) * i / (nx - 1), a.Y + (b.Y - a.Y) * j / (ny - 1));
    }

    /// <summary>The visible faces of o under a selection box (so a small box on a big face selects it).</summary>
    IEnumerable<int> BoxRayFaces(Viewport vp, SceneObject o, Vector2 s0, Vector2 s1)
    {
        var set = new HashSet<int>();
        foreach (var p in BoxSamples(s0, s1))
        {
            var lp = vp.Local(p);
            var h = Picking.PickFace(o, vp.Camera.ScreenRay(lp.X, lp.Y, vp.W, vp.H), cullBack: CulledFor(o));
            if (h.HasValue) set.Add(h.Value.Face);
        }
        return set;
    }

    /// <summary>Back faces of this object are not drawn (Backface Cull, MLO rooms always): picking skips them too.</summary>
    /// <summary>The nearest face the viewport actually draws (back faces skipped where they are culled).</summary>
    PickHit? PickVisibleFace(in Ray ray)
    {
        PickHit? best = null;
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Frozen || o.Mesh == null || o.Mesh.Faces.Count == 0 || o.Gta is { BoundsOnly: true }) continue;
            var h = Picking.PickFace(o, ray, evaluated: o.Modifiers is { Count: > 0 }, cullBack: CulledFor(o));
            if (h.HasValue && (!best.HasValue || h.Value.Distance < best.Value.Distance)) best = h;
        }
        return best;
    }

    /// <summary>The mouse is over a selected object (object mode) or over the object being edited (edit mode).</summary>
    bool MiddleOnSelection(Viewport hv)
    {
        // edit mode, edges or points: close to a selected one on screen is enough (an edge on the outline has no face under it)
        if (Level is SubObject.Vertex or SubObject.Edge or SubObject.Border && Current is { Mesh: not null } c && MiddleNearSelected(hv, c, 14f)) return true;
        var hit = PickVisibleFace(RayAt(hv, Input.Mouse));
        if (hit is not { } hh) return false;
        var o = hh.Object;
        if (Level != SubObject.Object) return o == Current;
        if (Selected.Contains(o.Id)) return true;
        // a game mesh under a selected Drawable node moves with it
        for (var p = Scene.Parent(o); p != null; p = Scene.Parent(p)) if (Selected.Contains(p.Id)) return true;
        return false;
    }

    bool MiddleNearSelected(Viewport hv, SceneObject c, float px)
    {
        var m = c.Mesh; var w = c.World; var sel = SubSel(c); var mouse = Input.Mouse;
        Vector2? Scr(int v)
        {
            if (v < 0 || v >= m.Verts.Count) return null;
            if (!hv.Camera.Project(Vector3.Transform(m.Verts[v], w), hv.W, hv.H, out var sp, out _)) return null;
            return new Vector2(hv.X + sp.X, hv.Y + sp.Y);
        }
        if (Level == SubObject.Vertex)
        {
            int n = 0;
            foreach (var v in sel.Verts) { if (++n > 20000) break; if (Scr(v) is { } p && Vector2.Distance(p, mouse) <= px) return true; }
            return false;
        }
        int k = 0;
        foreach (var e in sel.Edges)
        {
            if (++k > 20000) break;
            if (Scr(e.A) is not { } a || Scr(e.B) is not { } b) continue;
            var ab = b - a; float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(mouse - a, ab) / ab.LengthSquared(), 0, 1);
            if (Vector2.Distance(a + ab * t, mouse) <= px) return true;
        }
        return false;
    }

    bool CulledFor(SceneObject o) => !FaceOrientation && o.GtaType != GtaObjectType.Portal && (BackfaceCull || o.GtaType == GtaObjectType.MloRoom);

    void SelectionChanged()
    {
        if (!Selected.Contains(ActiveId)) ActiveId = Selected.Count > 0 ? Selected.First() : -1;
        if (Level != SubObject.Object && Current == null) Level = SubObject.Object;
    }

    // ---------------------------------------------------------------- gizmo transforms (Move / Rotate / Scale tools)

    MouseButtons xfButton = MouseButtons.Left;
    bool xfRadialScale; Vector2 xfPivotScreen;

    void BeginTransform(Viewport vp, GizmoHandle h)
    {
        xfButton = MouseButtons.Left; xfRadialScale = false;
        if (!SelectionPivot(out var pivot, out var orient)) return;
        Undo.Record(Tool == Tool.Move ? "Move" : Tool == Tool.Rotate ? "Rotate" : "Scale", Scene);
        lastAdd = null; ctrlLatch = false;
        transforming = true; xfViewport = vp; gizmo.Active = h; xfMouseStart = Input.Mouse; xfPivot = pivot; xfPivotLive = pivot;
        xfAxes = new[]
        {
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, orient)),
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, orient)),
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, orient)),
        };
        CaptureOriginals();
        if (Tool == Tool.Move) CaptureSnapFrom(vp, pivot);
        var ray = RayAt(vp, Input.Mouse);
        int ax = AxisIndex(h);
        if (Tool == Tool.Move && ax >= 0) xfParamStart = Picking.RayLineParam(ray, pivot, xfAxes[ax]);
        else Picking.RayPlane(ray, pivot, PlaneNormal(vp, h), out xfPlaneStart);
    }

    /// <summary>Remembers object transforms or world vertex positions before a transform.</summary>
    void CaptureOriginals()
    {
        xfObjects.Clear(); xfVerts.Clear(); lastSnap = null;
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            var w = cur.World;
            foreach (var v in SubSel(cur).VerticesOf(cur.Mesh, Level)) xfVerts[v] = Vector3.Transform(cur.Mesh.Verts[v], w);
            CaptureUvMaps(cur);
        }
        else
        {
            // children follow their parent (3ds Max / GIMS hierarchy)
            foreach (var id in Selected.ToList())
            {
                var o = Scene.Find(id); if (o == null) continue;
                xfObjects[id] = (o.Position, o.Rotation, o.Scale);
                foreach (var c in Scene.Descendants(o)) xfObjects.TryAdd(c.Id, (c.Position, c.Rotation, c.Scale));
            }
        }
    }

    static int AxisIndex(GizmoHandle h) => h switch { GizmoHandle.X => 0, GizmoHandle.Y => 1, GizmoHandle.Z => 2, _ => -1 };

    Vector3 PlaneNormal(Viewport vp, GizmoHandle h) => h switch
    {
        GizmoHandle.XY => xfAxes[2],
        GizmoHandle.YZ => xfAxes[0],
        GizmoHandle.ZX => xfAxes[1],
        _ => -vp.Camera.Forward,
    };

    static Ray RayAt(Viewport vp, Vector2 screen)
    {
        var l = vp.Local(screen);
        return vp.Camera.ScreenRay(l.X, l.Y, vp.W, vp.H);
    }

    float SnapValue(float v, float step) => step > 0 ? MathF.Round(v / step) * step : v;
    bool ctrlLatch;
    /// <summary>Snapping in effect. Ctrl flips it only inside a G / R / S (or extrude...) modal move, like Blender: holding
    /// Ctrl anywhere else (Ctrl+click to add to the selection, Ctrl+arrows...) never turns the snap on.</summary>
    bool SnapActive => Snap ^ (ModalActive && Input.Ctrl && !ctrlLatch && Settings.Keymap != Keymap.Max);

    void UpdateTransform()
    {
        if (cageDrag) { UpdateCage(); return; }
        var vp = xfViewport; var h = gizmo.Active; var ray = RayAt(vp, Input.Mouse);
        int ax = AxisIndex(h);
        if (Tool == Tool.Move)
        {
            Vector3 delta;
            int planeAx = h switch { GizmoHandle.XY => 2, GizmoHandle.YZ => 0, GizmoHandle.ZX => 1, _ => -1 };
            if (ax >= 0) delta = xfAxes[ax] * (Picking.RayLineParam(ray, xfPivot, xfAxes[ax]) - xfParamStart);
            else
            {
                if (!Picking.RayPlane(ray, xfPivot, PlaneNormal(vp, h), out var p)) return;
                delta = p - xfPlaneStart;
            }
            delta = ApplySnaps(vp, delta, xfPivot, xfAxes, ax >= 0 ? ax : planeAx, ax < 0 && planeAx >= 0);
            xfPivotLive = xfPivot + delta;
            ApplyLive(p => p + delta, (pos, rot, sc) => (pos + delta, rot, sc));
            SetStatus($"Move  X {delta.X:0.000}  Y {delta.Y:0.000}  Z {delta.Z:0.000} m");
        }
        else if (Tool == Tool.Rotate)
        {
            var dm = Input.Mouse - xfMouseStart;
            float deg = (dm.X - dm.Y) * 0.5f;
            if (SnapActive && SnapGrid) deg = SnapValue(deg, AngleSnap);
            var axis = ax >= 0 ? xfAxes[ax] : -vp.Camera.Forward;
            ApplyRotation(axis, deg * MathF.PI / 180f, xfPivot);
            SetStatus($"Rotate {deg:0.0}°");
        }
        else if (Tool == Tool.Scale)
        {
            var dm = Input.Mouse - xfMouseStart;
            float f = MathF.Max(0.001f, 1f + (dm.X - dm.Y) * 0.008f);
            if (xfRadialScale) f = MathF.Max(0.001f, Vector2.Distance(Input.Mouse, xfPivotScreen) / MathF.Max(4f, Vector2.Distance(xfMouseStart, xfPivotScreen)));
            if (SnapActive) f = MathF.Max(0.1f, MathF.Round(f * 10) / 10);
            var fv = ax switch { 0 => new Vector3(f, 1, 1), 1 => new Vector3(1, f, 1), 2 => new Vector3(1, 1, f), _ => new Vector3(f) };
            ApplyScale(fv, xfPivot, xfAxes);
            SetStatus($"Scale {f * 100:0.0}%");
        }
    }

    void ApplyRotation(Vector3 axis, float radians, Vector3 pivot)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
        ApplyLive(p => pivot + Vector3.Transform(p - pivot, q),
                  (pos, rot, sc) => (pivot + Vector3.Transform(pos - pivot, q), Quaternion.Normalize(q * rot), sc));
    }

    void ApplyScale(Vector3 fv, Vector3 pivot, Vector3[] axes)
    {
        ApplyLive(p =>
        {
            var d = p - pivot;
            var l = new Vector3(Vector3.Dot(d, axes[0]), Vector3.Dot(d, axes[1]), Vector3.Dot(d, axes[2])) * fv;
            return pivot + axes[0] * l.X + axes[1] * l.Y + axes[2] * l.Z;
        },
        (pos, rot, sc) =>
        {
            var d = pos - pivot;
            var l = new Vector3(Vector3.Dot(d, axes[0]), Vector3.Dot(d, axes[1]), Vector3.Dot(d, axes[2])) * fv;
            var np = Selected.Count > 1 ? pivot + axes[0] * l.X + axes[1] * l.Y + axes[2] * l.Z : pos;
            return (np, rot, sc * fv);
        });
    }

    void ApplyLive(Func<Vector3, Vector3> vertexFn, Func<Vector3, Quaternion, Vector3, (Vector3, Quaternion, Vector3)> objectFn)
    {
        var cur = Current;
        if (xfVerts.Count > 0 && cur != null)
        {
            Matrix4x4.Invert(cur.World, out var inv);
            foreach (var kv in xfVerts) if (kv.Key < cur.Mesh.Verts.Count) cur.Mesh.Verts[kv.Key] = Vector3.Transform(vertexFn(kv.Value), inv);
            ApplyUvMaps(cur);
            int from = cur.Version;
            cur.Touch();
            // only points moved (no new faces, no UV change): the viewport rewrites their positions instead of rebuilding the mesh
            if (uvMaps.Count == 0)
            {
                var moved = xfVerts.Keys.ToArray();
                renderer.HintPointsMoved(cur.Id, from, cur.Version, moved);
                wires.HintPointsMoved(cur.Id, from, cur.Version, moved, cur.Mesh.Verts);
                xfPointsOnly = true;
            }
        }
        else
        {
            foreach (var kv in xfObjects)
            {
                var o = Scene.Find(kv.Key); if (o == null) continue;
                (o.Position, o.Rotation, o.Scale) = objectFn(kv.Value.Pos, kv.Value.Rot, kv.Value.Scale);
            }
        }
    }

    void RestoreOriginals()
    {
        ApplyLive(p => p, (pos, rot, sc) => (pos, rot, sc));
    }

    /// <summary>The drag only rewrote point positions on the GPU: shading (normals) is rebuilt once at the end.</summary>
    bool xfPointsOnly;
    void FinishPointsOnly() { if (xfPointsOnly) { xfPointsOnly = false; Current?.Touch(); } }

    void EndTransform()
    {
        transforming = false; gizmo.Active = GizmoHandle.None; Dirty = true; EndCage();
        FinishPointsOnly();
        AutoKeyAfterTransform();
        if (shiftClone)
        {
            shiftClone = false;
            shiftCloneDelta = Tool == Tool.Move ? xfPivotLive - xfPivot : Vector3.Zero;
            shiftCloneCount = 1; shiftCloneMode = lastCloneInstance ? 1 : 0;
            OpenPopup("Clone Options##shift");
        }
    }
    bool shiftClone; Vector3 shiftCloneDelta; int shiftCloneCount = 1, shiftCloneMode;

    void CancelTransform()
    {
        RestoreOriginals();
        FinishPointsOnly();
        Undo.DropLast();
        transforming = false; gizmo.Active = GizmoHandle.None; EndCage();
        SetStatus("Transform cancelled");
    }
}
