using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>Self tests of the snaps menu, Shift clone, isolate, eye on a selection, walk camera, hub, lights, normals views.</summary>
public sealed partial class EditorApp
{
    string grabKind; Vector3? grabPoint;

    IEnumerable<object> SnapsCloneIsolateTest()
    {
        Fresh();
        var a = Prim(CreateKind.Box, new Vector3(0, 0, 0));
        var b = Prim(CreateKind.Sphere, new Vector3(5, 0, 0));
        var c = Prim(CreateKind.Cylinder, new Vector3(-5, 0, 0));
        yield return Frames(2);

        // snaps: grid points are absolute (the pivot lands on the grid), only along the dragged axis
        var vps = Viewports[ActiveViewport];
        SnapGrid = true; Snap = true; GridStep = 0.5f; snapFrom = null;
        SnapVertexT = SnapEdge = SnapFace = SnapNearest = false; // grid alone (the mouse may be over an object)
        var wax = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var d = ApplySnaps(vps, new Vector3(0.6f, 0.2f, 0), new Vector3(0.3f, 0.1f, 0), wax, 0, false);
        Expect(Vector3.Distance(d, new Vector3(0.7f, 0, 0)) < 1e-4f, $"grid snap: pivot at x 0.3 dragged on X lands on the grid point 1.0, Y untouched (delta {d})");
        d = ApplySnaps(vps, new Vector3(0.7f, 1.2f, -0.2f), Vector3.Zero, wax, -1, false);
        Expect(Vector3.Distance(d, new Vector3(0.5f, 1f, 0f)) < 1e-4f, $"grid snap rounds a free move to grid points ({d})");
        // vertex snap with the real mouse: grab box A by a corner, drop that corner on a corner of box B
        UI.ImGuiBackend.SyntheticInput = true;
        var ba = Prim(CreateKind.Box, new Vector3(-1, -6, 0)); ba.Name = "snap_a";
        var bb = Prim(CreateKind.Box, new Vector3(3, -4, 0)); bb.Name = "snap_b";
        foreach (var v in Viewports) v.Camera.Frame(new Vector3(-3, -8, 0), new Vector3(5, -2, 2));
        Maximized = true; ActiveViewport = 3; vps = Viewports[3];
        yield return Frames(3);
        Vector3 W(SceneObject o, int i) => Vector3.Transform(o.Mesh.Verts[i], o.World);
        int ia = Enumerable.Range(0, ba.Mesh.Verts.Count).OrderByDescending(i => W(ba, i).X + W(ba, i).Z - W(ba, i).Y * 0.1f).First();
        int ib = Enumerable.Range(0, bb.Mesh.Verts.Count).OrderBy(i => W(bb, i).X - W(bb, i).Z + W(bb, i).Y * 0.1f).First();
        var cornerA = W(ba, ia); var cornerB = W(bb, ib); var startA = ba.Position;
        SnapGrid = false; SnapVertexT = true; SnapEdge = false; SnapFace = false; SnapNearest = false;
        SelectOnly(ba);
        PostMouse(0x200, ScreenOf(vps, cornerA), 0); yield return Frames(3);
        BeginModal(ModalKind.Translate);
        bool fromCorner = snapFrom is { } sf && Vector3.Distance(sf, cornerA) < 1e-3f;
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vps, cornerA), ScreenOf(vps, cornerB), i / 10f), 0); yield return Frames(1); }
        yield return Frames(2);
        yield return Shot("snap_vertex_drag");
        ConfirmModal();
        var landed = W(ba, ia);
        Expect(fromCorner && Vector3.Distance(landed, cornerB) < 1e-3f, $"vertex snap: the grabbed corner of A lands exactly on B's corner (from corner {fromCorner}, gap {Vector3.Distance(landed, cornerB):0.0000} m)");
        // with an axis constraint the snap only moves along that axis
        ba.Position = startA; ba.Touch();
        yield return Frames(2);
        var posBefore = ba.Position;
        PostMouse(0x200, ScreenOf(vps, cornerA), 0); yield return Frames(3);
        SnapAxisConstraint = true;
        BeginModal(ModalKind.Translate); modal.Axis = 0; modal.Plane = false;
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vps, cornerA), ScreenOf(vps, cornerB), i / 10f), 0); yield return Frames(1); }
        yield return Frames(2);
        ConfirmModal();
        var moved = ba.Position - posBefore;
        Expect(MathF.Abs(moved.Y) < 1e-4f && MathF.Abs(moved.Z) < 1e-4f && MathF.Abs(W(ba, ia).X - cornerB.X) < 1e-3f, $"X constraint (axis constraints on): snapped to B's corner on X only (moved {moved.X:0.000}, {moved.Y:0.000}, {moved.Z:0.000})");
        // Move tool, snap on, no gizmo: the corner under the mouse is shown, a left drag grabs it there and drops it on B's corner
        ba.Position = startA; ba.Touch();
        SnapEdge = true; SnapFace = true; Tool = Tool.Move;
        yield return Frames(2);
        PostMouse(0x200, ScreenOf(vps, cornerA), 0); yield return Frames(3);
        bool hoverShown = snapHover is { } shv && Vector3.Distance(shv, cornerA) < 1e-3f;
        yield return Shot("snap_hover_point");
        PostMouse(0x201, ScreenOf(vps, cornerA), 1); yield return Frames(2);
        bool clickSafe = !transforming; // a press alone does not grab: a plain click still selects
        bool grabbed = false;
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vps, cornerA), ScreenOf(vps, cornerB), i / 10f), 1); yield return Frames(1); grabbed |= transforming; }
        yield return Frames(2);
        yield return Shot("snap_drag_target_dots");
        PostMouse(0x202, ScreenOf(vps, cornerB), 0); yield return Frames(2);
        var landed2 = W(ba, ia);
        Expect(hoverShown && clickSafe && grabbed && Vector3.Distance(landed2, cornerB) < 1e-3f, $"drag a corner without the gizmo: point shown {hoverShown}, press alone does not grab {clickSafe}, grabbed on drag {grabbed}, lands on B's corner (gap {Vector3.Distance(landed2, cornerB):0.0000} m)");
        // same for an edge and a face: grab the middle of A's top edge / the middle of A's top face and drop it on B
        IEnumerable<object> Grab(Vector3 from, Vector3 to, string shot)
        {
            PostMouse(0x200, ScreenOf(vps, from), 0); yield return Frames(3);
            grabKind = snapHoverKind; grabPoint = snapHover;
            yield return Shot(shot + "_hover");
            PostMouse(0x201, ScreenOf(vps, from), 1); yield return Frames(2);
            for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vps, from), ScreenOf(vps, to), i / 10f), 1); yield return Frames(1); }
            yield return Frames(2);
            yield return Shot(shot + "_drop");
            PostMouse(0x202, ScreenOf(vps, to), 0); yield return Frames(2);
        }
        int[] TopEdge(SceneObject o)
        {
            float top = Enumerable.Range(0, o.Mesh.Verts.Count).Max(i => W(o, i).Z);
            foreach (var f in o.Mesh.Faces) for (int c = 0; c < f.V.Length; c++) { int x = f.V[c], y = f.V[(c + 1) % f.V.Length]; if (W(o, x).Z > top - 1e-3f && W(o, y).Z > top - 1e-3f) return new[] { x, y }; }
            return null;
        }
        Vector3 TopCenter(SceneObject o) { var all = Enumerable.Range(0, o.Mesh.Verts.Count).Select(i => W(o, i)).ToList(); float mz = all.Max(v => v.Z); var top = all.Where(v => v.Z > mz - 1e-3f).ToList(); return top.Aggregate(Vector3.Zero, (q, v) => q + v) / top.Count; }
        ba.Position = startA; ba.Touch(); yield return Frames(2);
        var te = TopEdge(ba); var edgeMid = (W(ba, te[0]) + W(ba, te[1])) * 0.5f;
        var posE = ba.Position;
        foreach (var x in Grab(edgeMid, cornerB, "snap_edge")) yield return x;
        var gotE = grabPoint is { } gpe ? gpe + (ba.Position - posE) : Vector3.Zero;
        Expect(grabKind == "edge" && Vector3.Distance(gotE, cornerB) < 1e-3f, $"drag by an edge: {grabKind} under the mouse, the grabbed edge point lands on B's corner (gap {Vector3.Distance(gotE, cornerB):0.0000} m)");
        ba.Position = startA; ba.Touch(); yield return Frames(2);
        var faceA = TopCenter(ba); var faceB = TopCenter(bb) + new Vector3(0, -0.3f, 0); // the front half of B's top: clear of the sphere behind it
        var posF = ba.Position;
        foreach (var x in Grab(faceA, faceB, "snap_face")) yield return x;
        var (amn, amx) = ba.WorldBounds(); var (bmn, bmx) = bb.WorldBounds();
        Expect(grabKind == "face" && MathF.Abs(amn.Z - bmx.Z) < 2e-3f, $"drag by a face onto B's top: {grabKind} under the mouse, A sits on B's top, touching, not inside (A bottom {amn.Z:0.000}, B top {bmx.Z:0.000})");
        // onto a side face: against it, never inside
        Vector3 SideCenter(SceneObject o, out Vector3 nrm)
        {
            var eyeC = vps.Camera.Eye; Vector3 best = default; nrm = default; float bestD = -1;
            foreach (var f in o.Mesh.Faces)
            {
                var pts = f.V.Select(i => W(o, i)).ToArray(); var c = pts.Aggregate(Vector3.Zero, (q, v) => q + v) / pts.Length;
                var nn = Vector3.Normalize(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]));
                if (MathF.Abs(nn.Z) > 0.1f) continue;
                float facing = Vector3.Dot(nn, Vector3.Normalize(eyeC - c));
                if (facing > bestD) { bestD = facing; best = c; nrm = nn; }
            }
            return best;
        }
        ba.Position = startA; ba.Touch(); yield return Frames(2);
        var sideB = SideCenter(bb, out var sideN) + new Vector3(0, 0, -0.2f);
        foreach (var x in Grab(TopCenter(ba), sideB, "snap_side")) yield return x;
        (amn, amx) = ba.WorldBounds();
        float overlapX = MathF.Min(amx.X, bmx.X) - MathF.Max(amn.X, bmn.X), overlapY = MathF.Min(amx.Y, bmx.Y) - MathF.Max(amn.Y, bmn.Y), overlapZ = MathF.Min(amx.Z, bmx.Z) - MathF.Max(amn.Z, bmn.Z);
        float pen = MathF.Min(overlapX, MathF.Min(overlapY, overlapZ));
        Expect(grabKind == "face" && MathF.Abs(pen) < 2e-3f && MathF.Abs(ba.Position.Z - startA.Z) < 1e-3f, $"drag onto B's side face: A ends against it, touching, not inside (overlap {pen:0.0000} m), at its height (z {ba.Position.Z:0.000})");
        // gizmo arrow + face snap: the move stays on the arrow and stops against the face it meets
        var sideCenter = SideCenter(bb, out var sn);
        int axI = MathF.Abs(sn.X) > MathF.Abs(sn.Y) ? 0 : 1;
        var bc = (bmn + bmx) * 0.5f;
        var startPos = new Vector3(bc.X, bc.Y, 0); if (axI == 0) startPos.X += MathF.Sign(sn.X) * 4; else startPos.Y += MathF.Sign(sn.Y) * 4;
        ba.Position = startPos; ba.Touch(); yield return Frames(2);
        SelectOnly(ba);
        PostMouse(0x200, ScreenOf(vps, ba.Position + new Vector3(0, 0, 1)), 0); yield return Frames(2);
        BeginModal(ModalKind.Translate); modal.Axis = axI; modal.Plane = false;
        var aim = sideCenter + new Vector3(0, 0, -0.3f);
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vps, ba.Position + new Vector3(0, 0, 1)), ScreenOf(vps, aim), i / 10f), 0); yield return Frames(1); }
        yield return Frames(2);
        yield return Shot("snap_axis_against_face");
        ConfirmModal();
        (amn, amx) = ba.WorldBounds();
        var moved2 = ba.Position - startPos;
        float gap = axI == 0 ? (sn.X > 0 ? amn.X - bmx.X : bmn.X - amx.X) : (sn.Y > 0 ? amn.Y - bmx.Y : bmn.Y - amx.Y);
        float offAxis = axI == 0 ? MathF.Abs(moved2.Y) + MathF.Abs(moved2.Z) : MathF.Abs(moved2.X) + MathF.Abs(moved2.Z);
        Expect(MathF.Abs(gap) < 2e-3f && offAxis < 1e-3f, $"{(axI == 0 ? "X" : "Y")} arrow onto a face: stays on the arrow (off-axis {offAxis:0.000} m) and stops against the face (gap {gap:0.0000} m)");
        Tool = Tool.Select;
        UI.ImGuiBackend.SyntheticInput = false;
        Maximized = false;
        SnapVertexT = SnapEdge = SnapFace = true; SnapNearest = false; SnapGrid = true; Snap = false;

        // Shift+drag clone: 3 copies along the move offset, then cancel removes the clones
        SelectOnly(a);
        int before = Scene.Objects.Count;
        CloneSelection(false);
        foreach (var r in SelObjects()) r.Position += new Vector3(0, 3, 0);
        shiftCloneDelta = new Vector3(0, 3, 0); shiftCloneCount = 3; shiftCloneMode = 0;
        FinishShiftClone();
        Expect(Scene.Objects.Count == before + 3, $"Shift clone x3 makes 3 copies ({Scene.Objects.Count - before})");
        var last = Current;
        Expect(last != null && MathF.Abs(last.Position.Y - 9) < 1e-3f, $"copies follow the move offset (last at y={last?.Position.Y})");
        SelectOnly(a);
        CloneSelection(true);
        CancelShiftClone();
        Expect(Scene.Objects.Count == before + 3 && Selected.Contains(a.Id), "cancelled clone removed, original selected again");
        yield return Frames(2);

        // isolate / end isolate keep the eyes as they were
        c.Hidden = true;
        SelectOnly(b);
        IsolateSelection(forceOn: true);
        Expect(!b.Hidden && a.Hidden, "isolate: only the selection stays visible");
        yield return Frames(2);
        yield return Shot("isolate");
        EndIsolate();
        Expect(!a.Hidden && !b.Hidden && c.Hidden, "end isolate: every eye back as it was (hidden one stays hidden)");
        c.Hidden = false;

        // eye on a selected row hides the whole selection
        SelectOnly(a, b, c);
        bool hide = !a.Hidden;
        foreach (var t in RowTargets(a)) t.Hidden = hide;
        Expect(a.Hidden && b.Hidden && c.Hidden, "eye on a selected row hides every selected object");
        foreach (var t in RowTargets(a)) t.Hidden = false;
        yield return Frames(2);

        // game-style camera with the real keys and mouse: arrows move (forward / side step), right button held = mouse look,
        // ZQSD / WASD move while it is held (no shortcut runs), a right click without moving still opens the menu
        Maximized = true;
        var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Camera.Pitch = 0.3f;
        UI.ImGuiBackend.SyntheticInput = true;
        var mid = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.5f);
        PostMouse(0x200, mid, 0); yield return Frames(2);
        var eye0 = vp.Camera.Eye; float yaw0 = vp.Camera.Yaw;
        PostKey(System.Windows.Forms.Keys.Up, true); yield return Frames(3);
        yield return Frames(27); PostKey(System.Windows.Forms.Keys.Up, false); yield return Frames(20);
        var eye1 = vp.Camera.Eye;
        Expect(Vector3.Distance(eye0, eye1) > 0.5f && MathF.Abs(eye1.Z - eye0.Z) < 1e-3f, $"Up arrow moves forward on the ground ({Vector3.Distance(eye0, eye1):0.00} m, height {eye1.Z - eye0.Z:0.000})");
        PostKey(System.Windows.Forms.Keys.Left, true); yield return Frames(30); PostKey(System.Windows.Forms.Keys.Left, false); yield return Frames(20);
        var eye2 = vp.Camera.Eye; var sideDir = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(new Vector3(vp.Camera.Forward.X, vp.Camera.Forward.Y, 0)), Vector3.UnitZ));
        Expect(MathF.Abs(vp.Camera.Yaw - yaw0) < 1e-4f && Vector3.Dot(eye2 - eye1, -sideDir) > 0.5f, $"Left arrow side-steps left, the view does not turn ({Vector3.Dot(eye2 - eye1, -sideDir):0.00} m)");
        // right button held + mouse: look around, the camera stays in place
        yield return Frames(30); eye2 = vp.Camera.Eye; // the glide after the side step has stopped
        PostMouse(0x204, mid, 2); yield return Frames(2);
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, mid + new Vector2(i * 12, -i * 3), 2); yield return Frames(1); }
        var eye3 = vp.Camera.Eye;
        bool flyOn = nav == NavMode.Fly;
        // the mouse went right: the view turns right (forward rotates clockwise seen from above)
        var f0 = new Vector2(MathF.Cos(yaw0), MathF.Sin(yaw0)); var f1 = new Vector2(MathF.Cos(vp.Camera.Yaw), MathF.Sin(vp.Camera.Yaw));
        Expect(f0.X * f1.Y - f0.Y * f1.X < 0, $"mouse to the right turns the view to the right (yaw {yaw0:0.00} -> {vp.Camera.Yaw:0.00})");
        Expect(flyOn && MathF.Abs(vp.Camera.Yaw - yaw0) > 0.2f && Vector3.Distance(eye3, eye2) < 0.05f, $"right button + mouse looks around from the same place (fly {flyOn}, turned {(vp.Camera.Yaw - yaw0) * 180 / MathF.PI:0} deg, eye moved {Vector3.Distance(eye3, eye2):0.000} m)");
        var tool0 = Tool;
        PostKey(System.Windows.Forms.Keys.W, true); yield return Frames(30); PostKey(System.Windows.Forms.Keys.W, false);
        PostKey(System.Windows.Forms.Keys.Z, true); yield return Frames(20); PostKey(System.Windows.Forms.Keys.Z, false); yield return Frames(20);
        var eye4 = vp.Camera.Eye;
        Expect(Vector3.Dot(eye4 - eye3, vp.Camera.Forward) > 0.5f && Tool == tool0, $"W / Z with the right button held fly forward ({Vector3.Dot(eye4 - eye3, vp.Camera.Forward):0.00} m), no shortcut ran (tool {Tool})");
        yield return Shot("fly_camera");
        int menus0 = contextMenuOpens;
        PostMouse(0x205, mid + new Vector2(120, -30), 0); yield return Frames(3);
        Expect(nav == NavMode.None && contextMenuOpens == menus0, "releasing the right button after looking: no context menu");
        PostMouse(0x200, mid, 0); yield return Frames(2);
        PostMouse(0x204, mid, 2); yield return Frames(2); PostMouse(0x205, mid, 0); yield return Frames(3);
        bool menu = contextMenuOpens > menus0;
        Expect(menu, "a right click without moving still opens the context menu");
        if (menu) { PostKey(System.Windows.Forms.Keys.Escape, true); yield return Frames(1); PostKey(System.Windows.Forms.Keys.Escape, false); yield return Frames(2); }
        UI.ImGuiBackend.SyntheticInput = false;
        yield return Frames(2);
        FrameAll();
        Maximized = false;
    }

    /// <summary>Edit mode: Shift + drag on the gizmo copies the selected faces (or pulls a strip out of edges) and moves the
    /// copy, with the real mouse and keyboard. Then the view cube: a held drag turns the view, a click picks a face.</summary>
    IEnumerable<object> EditShiftCloneTest()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero); b.Name = "shift_box";
        yield return Frames(2);
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false;
        vp.Camera.Frame(new Vector3(-3, -3, -1), new Vector3(3, 3, 5));
        // a click on empty space first: closes any menu a previous test left open (it would take the first click)
        UI.ImGuiBackend.SyntheticInput = true;
        var empty = new Vector2(vp.X + 30, vp.Y + vp.H - 30);
        PostMouse(0x200, empty, 0); yield return Frames(2); PostMouse(0x201, empty, 1); yield return Frames(2); PostMouse(0x202, empty, 0); yield return Frames(3);
        SelectOnly(b); Tool = Tool.Move;
        Level = SubObject.Polygon;
        float topZ = b.Mesh.Verts.Max(v => v.Z);
        int top = Enumerable.Range(0, b.Mesh.Faces.Count).First(i => b.Mesh.Faces[i].V.All(v => MathF.Abs(b.Mesh.Verts[v].Z - topZ) < 1e-4f));
        var sel = SubSel(b); sel.Clear(); sel.Faces.Add(top);
        yield return Frames(3);
        UI.ImGuiBackend.SyntheticInput = true;
        // a point on the gizmo's Z arrow
        Vector2 Grip(GizmoHandle want)
        {
            var lay = GizmoLayout(vp); SelectionPivot(out var pv, out _); var p0 = ScreenOf(vp, pv);
            for (int r = 8; r < 160; r += 2)
                for (int a = 0; a < 16; a++)
                {
                    var q = p0 + new Vector2(MathF.Cos(a * MathF.Tau / 16), MathF.Sin(a * MathF.Tau / 16)) * r;
                    if (gizmo.HitTest(vp, lay, Tool, q) == want) return q;
                }
            return p0;
        }
        var g = Grip(GizmoHandle.Z);
        int faces0 = b.Mesh.Faces.Count, verts0 = b.Mesh.Verts.Count;
        PostMouse(0x200, g, 0); yield return Frames(3);
        PostKey(System.Windows.Forms.Keys.ShiftKey, true); yield return Frames(2);
        PostMouse(0x201, g, 1); yield return Frames(2);
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, g - new Vector2(0, i * 8), 1); yield return Frames(1); }
        PostMouse(0x202, g - new Vector2(0, 80), 0); yield return Frames(2);
        PostKey(System.Windows.Forms.Keys.ShiftKey, false); yield return Frames(2);
        var m = b.Mesh; var nsel = SubSel(b);
        int newFace = nsel.Faces.Count == 1 ? nsel.Faces.First() : -1;
        float newZ = newFace >= 0 ? m.Faces[newFace].V.Average(v => m.Verts[v].Z) : 0;
        bool origStays = m.Faces[top].V.All(v => MathF.Abs(m.Verts[v].Z - topZ) < 1e-4f);
        yield return Shot("edit_shift_clone_face");
        Expect(m.Faces.Count == faces0 + 1 && m.Verts.Count == verts0 + 4 && newFace != top && newZ > topZ + 0.05f && origStays,
            $"Shift + drag the Z arrow on a face: a copy moves up ({faces0} -> {m.Faces.Count} faces, copy at z {newZ:0.00} above {topZ:0.00}), the original stays");
        DoUndo(); DoUndo();
        var bAfter = Obj("shift_box"); // undo rebuilds the scene: read the object again
        Expect(bAfter != null && bAfter.Mesh.Faces.Count == faces0, $"two undos: move, then copy ({bAfter?.Mesh.Faces.Count} faces)");
        // edges: Shift + drag pulls a strip of faces out of the top border edges of an open plane
        Fresh();
        var pl = Prim(CreateKind.Plane, Vector3.Zero); pl.Name = "shift_plane";
        yield return Frames(2);
        SelectOnly(pl); Tool = Tool.Move; Level = SubObject.Edge;
        var pm = pl.Mesh; var border = MoloMax.Core.Mesh.MeshOps.BorderEdges(pm);
        float maxY = pm.Verts.Max(v => v.Y);
        var esel = SubSel(pl); esel.Clear();
        foreach (var e in border) if (MathF.Abs(pm.Verts[e.A].Y - maxY) < 1e-4f && MathF.Abs(pm.Verts[e.B].Y - maxY) < 1e-4f) esel.Edges.Add(e);
        int nEdges = esel.Edges.Count, pf0 = pm.Faces.Count;
        vp.Camera.Frame(pm.Bounds().Min - new Vector3(1), pm.Bounds().Max + new Vector3(1, 3, 1));
        yield return Frames(3);
        var gy = Grip(GizmoHandle.Y);
        SelectionPivot(out var pv0, out _);
        var yDir = ScreenOf(vp, pv0 + Vector3.UnitY) - ScreenOf(vp, pv0); yDir = Vector2.Normalize(yDir);
        PostMouse(0x200, gy, 0); yield return Frames(3);
        PostKey(System.Windows.Forms.Keys.ShiftKey, true); yield return Frames(2);
        PostMouse(0x201, gy, 1); yield return Frames(2);
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, gy + yDir * (i * 8), 1); yield return Frames(1); }
        PostMouse(0x202, gy + yDir * 80, 0); yield return Frames(2);
        PostKey(System.Windows.Forms.Keys.ShiftKey, false); yield return Frames(2);
        pm = pl.Mesh;
        float newMaxY = pm.Verts.Max(v => v.Y);
        yield return Shot("edit_shift_clone_edges");
        Expect(pm.Faces.Count == pf0 + nEdges && newMaxY > maxY + 0.05f && SubSel(pl).Edges.Count == nEdges,
            $"Shift + drag {nEdges} border edges: a strip of {pm.Faces.Count - pf0} new faces pulled out (edge at y {newMaxY:0.00}, was {maxY:0.00})");
        // a material dragged from the Material Editor and dropped: Polygon mode, on the face under the mouse
        var newMat = Scene.AddMaterial("drop_test", new Vector3(0.9f, 0.2f, 0.2f));
        Level = SubObject.Polygon; SubSel(pl).Clear();
        var fc = pl.Mesh.Faces[0].V.Select(v => Vector3.Transform(pl.Mesh.Verts[v], pl.World)).Aggregate(Vector3.Zero, (q, v) => q + v) / pl.Mesh.Faces[0].V.Length;
        bool dropped = DropMaterial(vp, ScreenOf(vp, fc), newMat.Id);
        int withMat = pl.Mesh.Faces.Count(f => f.MatId == newMat.Id);
        Expect(dropped && withMat == 1 && pl.Mesh.Faces[0].MatId == newMat.Id, $"material dropped on a face in Polygon mode: only that face takes it ({withMat} face)");
        Level = SubObject.Object;
        var bx = Prim(CreateKind.Box, new Vector3(0, -4, 0)); bx.Name = "drop_box";
        vp.Camera.Frame(new Vector3(-3, -7, -1), new Vector3(3, 3, 3));
        yield return Frames(3);
        var (bxn, bxx) = bx.WorldBounds();
        dropped = DropMaterial(vp, ScreenOf(vp, (bxn + bxx) * 0.5f), newMat.Id);
        Expect(dropped && bx.Mesh.Faces.All(f => f.MatId == newMat.Id) && pl.Mesh.Faces.Count(f => f.MatId == newMat.Id) == 1, $"material dropped on an object: every face of that object takes it, nothing else changes");
        Expect(Scene.Objects.Where(o => o != bx && o != pl).All(o => o.Mesh.Faces.All(f => f.MatId != newMat.Id)), "creating a material applies it to nothing by itself");
        // view cube: hold + drag turns the view, a click picks a face
        var cam = vp.Camera; float yaw0 = cam.Yaw;
        var cube = new Vector2(vp.X + vp.W - 22f * 2.1f - 8, vp.Y + 22f * 2.1f + 8);
        PostMouse(0x200, cube, 0); yield return Frames(2);
        PostMouse(0x201, cube, 1); yield return Frames(2);
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, cube + new Vector2(i * 10, 0), 1); yield return Frames(1); }
        PostMouse(0x202, cube + new Vector2(100, 0), 0); yield return Frames(3);
        float turned = (cam.Yaw - yaw0) * 180 / MathF.PI;
        Expect(MathF.Abs(turned) > 10 && cam.Kind is ViewKind.Perspective or ViewKind.User, $"view cube held and dragged: the view turns ({turned:0} deg), no face view jumped to");
        var face = MoloMax.UI.ViewCube.HitTest(vp, cube, out var kind);
        PostMouse(0x200, cube, 0); yield return Frames(2);
        PostMouse(0x201, cube, 1); yield return Frames(2); PostMouse(0x202, cube, 0); yield return Frames(3);
        Expect(face && cam.Kind == kind, $"view cube click: jumps to {kind} ({cam.Kind})");
        UI.ImGuiBackend.SyntheticInput = false;
        Maximized = false; Tool = Tool.Select;
        yield return Frames(2);
    }

    IEnumerable<object> LightsNormalsTest()
    {
        Fresh();
        var floor = Prim(CreateKind.Box, new Vector3(0, 0, 0));
        floor.Scale = new Vector3(8, 8, 0.2f);
        var box = Prim(CreateKind.Box, new Vector3(0, 0, 0.2f));
        AddLight(GtaLightKind.Point);
        var lamp = Current;
        lamp.Position = new Vector3(2, 1, 2.5f);
        lamp.Gta.Light.UseTemperature = true; lamp.Gta.Light.Temperature = 2700; lamp.Gta.Light.Intensity = 30; lamp.Gta.Light.Range = 8;
        AddLight(GtaLightKind.Spot);
        var spot = Current; spot.Position = new Vector3(-2, -1, 3f);
        AddLight(GtaLightKind.Capsule);
        var tube = Current; tube.Position = new Vector3(0, 3, 2f);
        Expect(GtaLight.Kelvin(2700).Z < 0.6f && GtaLight.Kelvin(9000).Z > 0.95f, "temperature: warm is orange, cold is blue");

        Maximized = true; Viewports[ActiveViewport].Shading = ShadingMode.Realistic; Hour = 15; InteriorLight = false;
        propTab = PropTab.Light; ShowPanel = true;
        Selected.Clear(); Selected.Add(lamp.Id); ActiveId = lamp.Id; SelectionChanged();
        FrameAll();
        yield return Frames(4);
        Expect(ViewLightCount == 3, $"3 scene lights sent to the viewport ({ViewLightCount})");
        Expect(ShadowsRendered, "sun shadow map rendered");
        yield return Shot("light_tab_day");
        Hour = 23;
        yield return Frames(3);
        yield return Shot("lights_night");
        // night-only light switches off by day
        lamp.Gta.Light.TimeFlags = NightHours(); Hour = 13;
        yield return Frames(2);
        Expect(ViewLightCount == 2, $"night-only light off at 13h ({ViewLightCount} lit)");
        lamp.Gta.Light.TimeFlags = 0xFFFFFF;
        // sun shadows alone (scene lights off), low afternoon sun
        bool vl = Settings.ViewportLights; Settings.ViewportLights = false; Hour = 17.5f;
        yield return Frames(3);
        yield return Shot("sun_shadows");
        Settings.ViewportLights = vl; Hour = 13;
        Settings.Filmic = true;
        yield return Frames(2);
        yield return Shot("filmic");
        Settings.Filmic = false;

        // normals: flip some faces of the box, face orientation shows them in red
        SelectOnly(box);
        SetLevel(SubObject.Polygon);
        SubSel(box).Faces = new HashSet<int> { 0, 1 };
        FlipFaces();
        yield return Frames(2);
        yield return Shot("selected_flipped_dark");
        SetLevel(SubObject.Object);
        FaceOrientation = true;
        yield return Frames(3);
        yield return Shot("face_orientation");
        FaceOrientation = false;

        // a click on a material slot selects its faces
        SelectOnly(box); SetLevel(SubObject.Polygon); SubSel(box).Faces.Clear();
        int mat = box.Mesh.Faces[0].MatId;
        SelectByMaterial(mat);
        Expect(SubSel(box).Faces.Count == box.Mesh.Faces.Count(f => f.MatId == mat), "material click selects its faces");
        SetLevel(SubObject.Object);

        // smart hub home
        HubOpen = true; hubPage = HubPage.Home;
        yield return Frames(3);
        yield return Shot("hub_home");
        HubOpen = false;
        Maximized = false;
    }

    IEnumerable<object> PlanPhotoTest()
    {
        Fresh();
        // a plan picture: 12 x 8 m, two rooms, one door between them
        var file = StPath("plan_picture.png");
        using (var bmp = new System.Drawing.Bitmap(600, 400))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(245, 245, 240));
            var br = System.Drawing.Brushes.Black;
            g.FillRectangle(br, 40, 40, 520, 10); g.FillRectangle(br, 40, 350, 520, 10); g.FillRectangle(br, 40, 40, 10, 320); g.FillRectangle(br, 550, 40, 10, 320);
            g.FillRectangle(br, 345, 40, 10, 140); g.FillRectangle(br, 345, 230, 10, 130);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        Scene.Plan = new RoomPlanData { Photo = file, PhotoWidth = 12 };
        planOpen = true; planTool = 5;
        yield return Frames(3);
        ReadPlanPhoto(Scene.Plan);
        Expect(Scene.Plan.Rooms.Count == 2 && Scene.Plan.Openings.Count == 1, $"picture read: {Scene.Plan.Rooms.Count} rooms, {Scene.Plan.Openings.Count} door ({planPhotoNote})");
        // size references
        Scene.Plan.Props.Add(new PlanProp { Kind = "ped", X = 3, Y = 4 });
        Scene.Plan.Props.Add(new PlanProp { Kind = "desk", X = 9, Y = 5, Rotation = 90 });
        planTool = 4;
        yield return Frames(3);
        yield return Shot("plan_from_picture");
        // materials + finishing
        var floor = Scene.AddMaterial("test_floor_tiles", new Vector3(0.6f, 0.55f, 0.5f));
        var walls = Scene.AddMaterial("test_wall_plaster", new Vector3(0.85f, 0.85f, 0.8f));
        RoomMat[0] = floor.Id; RoomMat[1] = walls.Id;
        FinishSkirting = FinishFloorDirt = FinishCeilingDirt = FinishCornerWear = true;
        BuildPlan();
        var rooms = Scene.Objects.Where(o => o.GtaType == GtaObjectType.MloRoom).ToList();
        Expect(rooms.Count == 2, $"2 rooms built ({rooms.Count})");
        Expect(Scene.Objects.Count(o => o.Name.Contains("skirting")) == 2, "smart skirting in each room");
        var frames = Scene.Objects.Where(o => o.Name.Contains("doorframe")).ToList();
        Expect(frames.Count == 2 && frames[0].Mesh.Verts.Count == frames[1].Mesh.Verts.Count && frames.All(f => f.Mesh.Bounds().Min.Z > 0.05f || f.Mesh.Faces.Count > 0),
            $"door frame on both sides of the inner door, same profile on both ({frames.Count} frames, {string.Join("/", frames.Select(f => f.Mesh.Verts.Count))} vertices)");
        // connected rooms: a lining through the gap meets both casings
        var links = Scene.Objects.Where(o => o.Name.StartsWith("link_") && !o.Name.Contains("_lining")).ToList();
        var linings = Scene.Objects.Where(o => o.Name.Contains("_lining")).ToList();
        bool spans = linings.Count == 1 && links.Count == 1 && frames.Count == 2 && LiningSpans(linings[0], frames);
        Expect(linings.Count == links.Count && links.Count == 1 && spans,
            $"connected rooms: one door lining joins the two door frames ({linings.Count} lining, {links.Count} passage, reaches both casings: {spans})");
        FinishJoinDoors = false; BuildPlan();
        Expect(!Scene.Objects.Any(o => o.Name.Contains("_lining")), "lining off when the rooms are not connected");
        FinishJoinDoors = true; BuildPlan();
        frames = Scene.Objects.Where(o => o.Name.Contains("doorframe")).ToList();
        var styles = Enumerable.Range(1, 5).Select(i => CasingProfile(i, true)).Select(t => TrimSweep.Profile(t).Count).Distinct().Count();
        Expect(styles >= 4, $"door / window frame styles are different profiles ({styles} distinct of 5)");
        Expect(Scene.Objects.Count(o => o.Name.Contains("floor_dirt")) == 2 && Scene.Objects.Any(o => o.Name.Contains("corner_wear")), "dirt and corner wear decals");
        Expect(rooms.All(r => r.Mesh.Faces.Any(f => f.MatId == floor.Id) && r.Mesh.Faces.Any(f => f.MatId == walls.Id)), "floor and wall materials applied");
        Expect(Scene.Objects.Count(o => o.Name.StartsWith("ref_") && o.Gta.Role == GtaRole.Ignore) == 2, "size references built, not exported");
        RoomMat[0] = RoomMat[1] = -1;
        planOpen = false;
        Maximized = true; Viewports[ActiveViewport].Shading = ShadingMode.Realistic;
        FrameAll();
        yield return Frames(3);
        yield return Shot("plan_built_finished");
        Maximized = false;
    }

    /// <summary>Does the lining box touch the casing of each side (world boxes overlapping)?</summary>
    static bool LiningSpans(SceneObject lining, List<SceneObject> frames)
    {
        var (ln, lx) = lining.WorldBounds();
        return frames.All(f => { var (fn, fx) = f.WorldBounds(); return ln.X <= fx.X + 0.005f && lx.X >= fn.X - 0.005f && ln.Y <= fx.Y + 0.005f && lx.Y >= fn.Y - 0.005f && ln.Z <= fx.Z && lx.Z >= fn.Z; });
    }

    IEnumerable<object> PlanLiveTest()
    {
        Fresh();
        Scene.Plan = new RoomPlanData();
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "a", X0 = 0, Y0 = 0, X1 = 4, Y1 = 4 });
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "b", X0 = 4, Y0 = 0, X1 = 8, Y1 = 4 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 8, Y = 2 }); // street door of b
        planOpen = true; planTool = 3; PlanLive = true;
        BuildPlan();
        yield return Frames(3);
        var b0 = Scene.Objects.First(o => o.Name == "b").Position;
        // move room b by 2 m: the viewport follows without pressing Build
        var rb = Scene.Plan.Rooms[1]; rb.X0 += 2; rb.X1 += 2; Scene.Plan.Openings[0].X += 2;
        yield return Frames(4);
        var b1 = Scene.Objects.First(o => o.Name == "b").Position;
        Expect(MathF.Abs(b1.X - b0.X - 2) < 0.01f, $"live: room moved in 3D as the plan changed ({b0.X:0.0} -> {b1.X:0.0})");
        // resize room a
        Scene.Plan.Rooms[0].Y1 = 6;
        yield return Frames(4);
        var a = Scene.Objects.First(o => o.Name == "a");
        var (mn, mx) = a.Mesh.Bounds();
        Expect(mx.Y - mn.Y > 5.5f, $"live: resized room rebuilt ({mx.Y - mn.Y:0.0} m deep)");
        yield return Shot("plan_live");
        // one undo puts the resize back (plan and 3D)
        yield return Frames(20);
        DoUndo();
        yield return Frames(3);
        Expect(MathF.Abs(Scene.Plan.Rooms[0].Y1 - 4) < 0.01f, $"undo restores the plan (room a depth back to {Scene.Plan.Rooms[0].Y1})");
        // the finishing options and materials rebuild live too (no Build button)
        yield return Frames(20);
        bool keepSk = FinishSkirting;
        FinishSkirting = !keepSk;
        yield return Frames(6);
        FinishSkirting = false;
        yield return Frames(6);
        int noSk = Scene.Objects.Count(o => o.Folder == PlanFolder);
        FinishSkirting = true;
        yield return Frames(6);
        int withSk = Scene.Objects.Count(o => o.Folder == PlanFolder);
        Expect(noSk < withSk, $"live: switching the skirting boards on / off rebuilds at once ({noSk} objects without, {withSk} with)");
        var tile = Scene.AddMaterial("plan_floor_test", new Vector3(0.4f, 0.3f, 0.2f));
        int keepFloor = RoomMat[0]; RoomMat[0] = tile.Id;
        yield return Frames(6);
        var ra = Scene.Objects.First(o => o.Name == "a");
        Expect(ra.Mesh.Faces.Any(f => f.MatId == tile.Id), "live: a floor material picked in the builder shows on the room at once");
        RoomMat[0] = keepFloor; FinishSkirting = keepSk;
        yield return Frames(6);
        planOpen = false;
    }

    IEnumerable<object> ExtrudeCubeProjTest()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(b);
        SetLevel(SubObject.Polygon);
        bool eaWas = Settings.ExtrudeArrow; Settings.ExtrudeArrow = true; // off by default, this test turns it on
        int top = Enumerable.Range(0, b.Mesh.Faces.Count).First(f => b.Mesh.FaceNormal(f).Z > 0.9f);
        SubSel(b).Faces = new HashSet<int> { top };
        Maximized = true; Viewports[ActiveViewport].Shading = ShadingMode.Realistic; FrameAll();
        yield return Frames(3);
        Expect(ExtrudeHandle(Viewports[ActiveViewport], out var ha, out var hb, out _, out _), $"extrude arrow shown on the selected face ({Vector2.Distance(ha, hb):0} px)");
        yield return Shot("extrude_arrow");
        float z0 = b.Mesh.Bounds().Max.Z;
        BeginModal(ModalKind.Extrude);
        ApplyParametric(0.5f);
        ConfirmModal(); // (frames in between would let the mouse drive the modal)
        Expect(lastMeshOp != null && MathF.Abs(b.Mesh.Bounds().Max.Z - z0 - 0.5f) < 0.01f, $"extruded 0.5 m, Adjust Last Operation ready");
        // the panel redoes it with another value
        lastMeshOp.Value = 1.25f; lastMeshOp.Repeat = 3;
        RunMeshOp(lastMeshOp.Kind, lastMeshOp.Obj, lastMeshOp.BaseMesh, lastMeshOp.BaseSel, lastMeshOp.Value, lastMeshOp.Repeat);
        lastMeshOp.Version = b.Version;
        Expect(MathF.Abs(b.Mesh.Bounds().Max.Z - z0 - 3.75f) < 0.02f, $"panel: offset 1.25 x 3 steps ({b.Mesh.Bounds().Max.Z - z0:0.00} m)");
        yield return Frames(2);
        yield return Shot("extrude_adjust_panel");
        // cube projection with a material
        SetLevel(SubObject.Object); SelectOnly(b);
        var mat = Scene.AddMaterial("test_brick", new Vector3(0.7f, 0.35f, 0.25f));
        StartCubeProjection();
        cubeProj.MaterialId = mat.Id; cubeProj.SizeU = cubeProj.SizeV = 0.5f; cubeProj.Rotation = 90;
        ApplyCubeProjection();
        Expect(b.Mesh.Faces.All(f => f.MatId == mat.Id), "cube projection: material on every face");
        var uvs = b.Mesh.Faces.SelectMany(f => f.UV).ToList();
        float span = uvs.Max(u => u.X) - uvs.Min(u => u.X);
        Expect(span > 1.5f, $"cube projection: 0.5 m tiles repeat on a 1 m box ({span:0.0} tiles)");
        yield return Frames(2);
        yield return Shot("cube_projection_panel");
        cubeProj = null;
        Maximized = false; Settings.ExtrudeArrow = eaWas;
    }

    /// <summary>A checker texture (DDS with mips) for the in-game test content.</summary>
    string TestTexture(string name, Vector3 a, Vector3 b)
    {
        var img = new MoloMax.Core.Gta.Export.RgbaImage(512, 512);
        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 512; x++)
            {
                bool on = ((x / 64) + (y / 64)) % 2 == 0;
                var c = on ? a : b; int i = (y * 512 + x) * 4;
                img.Pixels[i] = (byte)(c.X * 255); img.Pixels[i + 1] = (byte)(c.Y * 255); img.Pixels[i + 2] = (byte)(c.Z * 255); img.Pixels[i + 3] = 255;
            }
        return SaveGenerated(name, img);
    }

    /// <summary>
    /// Content for the in-game check on the FiveM test server: a textured prop group with collision at Legion Square,
    /// a two-room MLO with portals in the Sandy Shores desert. Written to selftest/ingame (zz_poly_prop, zz_poly_mlo).
    /// </summary>
    IEnumerable<object> InGameContentTest()
    {
        var root = StPath("ingame"); Directory.CreateDirectory(root);
        // 1) props: a textured crate with collision and a row of bollards (instances)
        Fresh();
        var tex = Scene.AddMaterial("zz_poly_checker", new Vector3(1));
        tex.DiffuseTexture = TestTexture("zz_poly_checker", new Vector3(0.95f, 0.42f, 0.11f), new Vector3(0.12f, 0.71f, 0.64f));
        var crate = Prim(CreateKind.Box, Vector3.Zero); crate.Name = "zz_poly_crate"; crate.Scale = new Vector3(1.5f); crate.ResetXForm(); crate.Mesh.SetAllMaterial(tex.Id); crate.Touch();
        MeshOps.BoxMap(crate.Mesh, null, 1.5f);
        SelectOnly(crate); CollisionFromSelection();
        var bol = Prim(CreateKind.Cylinder, new Vector3(3, 0, 0)); bol.Name = "zz_poly_bollard"; bol.Mesh.SetAllMaterial(tex.Id); MeshOps.BoxMap(bol.Mesh, null, 0.5f); bol.Touch();
        bol.Gta.Role = GtaRole.Prop;
        SelectOnly(bol);
        for (int i = 1; i < 5; i++) { var c = Scene.AddInstance(bol); c.Position = bol.Position + new Vector3(0, i * 1.5f, 0); }
        Scene.GtaOrigin = new Vector3(197f, -931f, 29.7f);
        Scene.Gta.OutputFolder = root; Scene.Gta.ResourceName = "zz_poly_prop";
        StartExport();
        yield return Until(() => exportTask == null || exportTask.IsCompleted, 120);
        yield return Frames(3);
        var rep = lastReport ?? exportTask?.Result;
        Expect(rep != null && rep.Ok, "prop resource exported " + (rep == null ? "" : string.Join(" ", rep.Errors)));
        if (rep != null) Note(rep.ToString().Replace("\n", " | "));
        HubOpen = false;
        // 2) a two-room MLO from the room builder, textured walls, a street door and an inner door
        Fresh();
        var wall = Scene.AddMaterial("zz_poly_wall", new Vector3(1));
        wall.DiffuseTexture = TestTexture("zz_poly_wall", new Vector3(0.85f, 0.83f, 0.78f), new Vector3(0.62f, 0.6f, 0.56f));
        var floor = Scene.AddMaterial("zz_poly_floor", new Vector3(1));
        floor.DiffuseTexture = TestTexture("zz_poly_floor", new Vector3(0.35f, 0.3f, 0.26f), new Vector3(0.5f, 0.45f, 0.4f));
        Scene.Plan = new RoomPlanData();
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "hall", X0 = 0, Y0 = 0, X1 = 6, Y1 = 4 });
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "office", X0 = 6, Y0 = 0, X1 = 10, Y1 = 4 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 6, Y = 2 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 0, Y = 2 });
        RoomMat[0] = floor.Id; RoomMat[1] = wall.Id;
        FinishSkirting = true;
        BuildPlan();
        RoomMat[0] = RoomMat[1] = -1;
        Scene.GtaOrigin = new Vector3(1560f, 3180f, 39.55f);
        Scene.Gta.OutputFolder = root; Scene.Gta.ResourceName = "zz_poly_mlo";
        var issues = MoloMax.Core.Gta.Check.GtaChecker.Run(Scene);
        Note("GTA check before export: " + string.Join(" | ", issues.Where(i => i.Level != MoloMax.Core.Gta.Check.CheckLevel.Info).Select(i => i.Level + ": " + i.Message)));
        StartExport();
        yield return Until(() => exportTask == null || exportTask.IsCompleted, 120);
        yield return Frames(3);
        rep = lastReport ?? exportTask?.Result;
        Expect(rep != null && rep.Ok, "MLO resource exported " + (rep == null ? "" : string.Join(" ", rep.Errors)));
        if (rep != null) Note(rep.ToString().Replace("\n", " | "));
        // issue #2: the interior collision is named after the MLO (its physics dictionary) and declared in _manifest.ymf
        if (rep != null)
        {
            Expect(rep.Files.Any(f => f.EndsWith("zz_poly_mlo.ybn", StringComparison.OrdinalIgnoreCase)) && rep.Files.Any(f => f.EndsWith("_manifest.ymf", StringComparison.OrdinalIgnoreCase)),
                "MLO collision named after the archetype + _manifest.ymf: " + string.Join(", ", rep.Files.Where(f => f.EndsWith(".ybn") || f.EndsWith(".ymf"))));
            var miloYmap = Path.Combine(root, "zz_poly_mlo", rep.Files.FirstOrDefault(f => f.EndsWith("_milo_.ymap")) ?? "none");
            Expect(File.Exists(miloYmap), "the _milo_ ymap is written");
        }
        HubOpen = false;
    }

    /// <summary>Weapon and vehicle forges: a simple gun fitted on the carbine and written as an add-on weapon, a toy car
    /// whose wheels are found, fitted on a game car and written as a FiveM vehicle; the files are read back.</summary>
    IEnumerable<object> ScaleTest()
    {
        Fresh();
        var stool = Scene.Add("stool", Core.Mesh.Primitives.Box(0.4f, 0.4f, 2.0f), new Vector3(1, 2, 0.5f)); stool.Name = "Tabouret_Bar";
        var chair = Scene.Add("chair", Core.Mesh.Primitives.Box(0.5f, 0.5f, 0.3f), new Vector3(-1, 0, 0)); chair.Name = "office_chair";
        var crate = Scene.Add("crate", Core.Mesh.Primitives.Box(1, 1, 1), new Vector3(3, 0, 0)); crate.Name = "crate";
        SelectOnly(stool);
        var ped = AddPedReference();
        var (pmn, pmx) = ped?.WorldBounds() ?? default;
        Expect(ped != null && MathF.Abs(pmx.Z - pmn.Z - Core.Tools.GtaScale.PedHeight) < 0.01f && ped.Gta.Role == GtaRole.Ignore && pmn.X > stool.WorldBounds().Max.X,
            $"ped added beside the stool: {pmx.Z - pmn.Z:0.000} m tall, never exported");
        SelectOnly(stool, chair, crate);
        var baseZ = stool.WorldBounds().Min.Z; var baseXY = (stool.WorldBounds().Min + stool.WorldBounds().Max) * 0.5f;
        int n = AutoScale();
        var (smn, smx) = stool.WorldBounds(); var (cmn, cmx) = chair.WorldBounds(); var (kmn, kmx) = crate.WorldBounds();
        Expect(n == 2 && MathF.Abs(smx.Z - smn.Z - 0.75f) < 0.005f && MathF.Abs(cmx.Z - cmn.Z - 0.90f) < 0.005f && MathF.Abs(kmx.Z - kmn.Z - 1f) < 0.005f,
            $"auto scale: stool {smx.Z - smn.Z:0.000} m, chair {cmx.Z - cmn.Z:0.000} m, unknown crate untouched ({kmx.Z - kmn.Z:0.000} m)");
        var c = (smn + smx) * 0.5f;
        Expect(MathF.Abs(smn.Z - baseZ) < 0.002f && MathF.Abs(c.X - baseXY.X) < 0.002f && MathF.Abs(c.Y - baseXY.Y) < 0.002f, "scaled around its base: still on the floor, same spot");
        DoUndo(); stool = Obj("Tabouret_Bar"); crate = Obj("crate");
        Expect(stool != null && MathF.Abs(stool.WorldBounds().Max.Z - stool.WorldBounds().Min.Z - 2f) < 0.005f, "undo puts the sizes back");
        SelectOnly(crate); AutoScale(Core.Tools.GtaScale.Kinds.First(k => k.Id == "desk"));
        Expect(MathF.Abs(crate.WorldBounds().Max.Z - crate.WorldBounds().Min.Z - 0.80f) < 0.005f, "kind picked by hand: crate as a desk = 0.80 m");
        yield return Frames(3);
        yield return Shot("gta_scale");
    }

    IEnumerable<object> ForgeTest(string gtaFolder)
    {
        Fresh();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        if (game == null) { Expect(false, "game files " + gameError); yield break; }
        var outDir = StPath("forge_out"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true); forgeOut = outDir;
        // ---- weapon: a receiver box and a long thin barrel, along Y (the forge turns it)
        var recv = Scene.Add("gun_body", Core.Mesh.Primitives.Box(0.08f, 0.45f, 0.14f), new Vector3(0, 0, 1)); recv.Name = "gun_body";
        var barrel = Scene.Add("gun_barrel", Core.Mesh.Primitives.Cylinder(0.015f, 0.35f, 10), new Vector3(0, 0.22f, 1.1f)); barrel.Name = "gun_barrel";
        barrel.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2); barrel.Touch();
        var grip = Scene.Add("gun_grip", Core.Mesh.Primitives.Box(0.05f, 0.06f, 0.14f), new Vector3(0, -0.12f, 0.88f)); grip.Name = "gun_grip";
        propTab = PropTab.Weapon; ShowPanel = true; weaponDonor = 7; weaponName = "molo_rifle"; weaponReplace = false;
        LoadWeaponDonor();
        Expect(weaponBones != null && weaponBones.Count > 3 && weaponBones.Any(b => b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase)), $"donor carbine rifle: {weaponBones?.Count} bones ({string.Join(", ", weaponBones?.Take(6).Select(b => b.Name) ?? Array.Empty<string>())})");
        var gun = new List<SceneObject> { Obj("gun_body"), Obj("gun_barrel"), Obj("gun_grip") };
        SelectOnly(gun.ToArray());
        FitWeapon(ForgeSelection());
        var donorMuzzle = weaponBones.First(b => b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase)).Position;
        Expect(weaponMuzzle.HasValue && Vector3.Distance(weaponMuzzle.Value, donorMuzzle) < 0.08f, $"gun fitted: its barrel tip on the donor's muzzle ({weaponMuzzle?.X:0.000}, {weaponMuzzle?.Y:0.000}, {weaponMuzzle?.Z:0.000} vs {donorMuzzle.X:0.000}, {donorMuzzle.Y:0.000}, {donorMuzzle.Z:0.000})");
        yield return Frames(4);
        yield return Shot("forge_weapon");
        ExportWeapon(ForgeSelection());
        var wdir = forgeReport?.Folder;
        var ydr = wdir != null ? Path.Combine(wdir, "stream", "w_molo_rifle.ydr") : null;
        var wbones = ydr != null && File.Exists(ydr) ? Core.Gta.Forge.ForgeCommon.YdrBones(File.ReadAllBytes(ydr)) : null;
        var wm = wbones?.FirstOrDefault(b => b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase));
        Expect(forgeReport != null && forgeReport.Ok && wbones != null && wbones.Count == weaponBones.Count && wm != null && Vector3.Distance(wm.Position, weaponMuzzle.Value) < 0.005f,
            $"add-on weapon written: w_molo_rifle.ydr with the donor's {wbones?.Count} bones, muzzle moved to the new barrel" + (forgeReport != null ? " " + string.Join(" | ", forgeReport.Errors.Concat(forgeReport.Warnings)) : ""));
        string Txt(string f) => wdir != null && File.Exists(Path.Combine(wdir, f)) ? File.ReadAllText(Path.Combine(wdir, f)) : "";
        Expect(Txt("meta/weapons.meta").Contains("WEAPON_MOLO_RIFLE") && Txt("meta/weapons.meta").Contains("w_molo_rifle") && Txt("meta/weaponarchetypes.meta").Contains("w_molo_rifle")
               && Txt("meta/weaponanimations.meta").Contains("WEAPON_MOLO_RIFLE") && Txt("fxmanifest.lua").Contains("WEAPONINFO_FILE"),
            "weapon metas cloned from the carbine (weapons, archetypes, animations) + fxmanifest + /give command");
        // ---- vehicle: a body and four round wheels, along Y
        Fresh();
        Scene.Add("car_body", Core.Mesh.Primitives.Box(1.8f, 4.2f, 0.8f), new Vector3(0, 0, 0.45f)).Name = "car_body";
        foreach (var (nm, x, y) in new[] { ("w1", 0.75f, 1.35f), ("w2", -0.75f, 1.35f), ("w3", 0.75f, -1.35f), ("w4", -0.75f, -1.35f) })
        {
            var w = Scene.Add(nm, Core.Mesh.Primitives.Cylinder(0.35f, 0.25f, 16), new Vector3(x - 0.125f, y, 0.35f)); w.Name = nm;
            w.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2); w.Touch();
        }
        propTab = PropTab.Vehicle; vehicleDonorName = "sultan2"; vehicleName = "molo_testcar"; vehicleLabel = "Molo test car"; vehicleFrontPositive = true; vehicleDonorWheels = true;
        LoadVehicleDonor();
        Expect(vehicleDonor != null && vehicleDonor.Wheels.Count >= 4, $"donor sultan2: {vehicleDonor?.Bones.Count} bones, wheels {string.Join(", ", vehicleDonor?.Wheels.Keys ?? Enumerable.Empty<string>())}");
        if (vehicleDonor == null) yield break;
        SelectOnly(Scene.Objects.Where(o => o.Folder != ForgeFolder && o.Mesh.Faces.Count > 0).ToArray());
        FindVehicleWheels(ForgeSelection());
        Expect(vehicleWheels != null && vehicleWheels.Count == 4 && vehicleWheels["wheel_lf"].X < 0 && vehicleWheels["wheel_lf"].Y > 0 && vehicleWheels["wheel_rr"].X > 0 && vehicleWheels["wheel_rr"].Y < 0,
            "the four wheels found and named (lf front left, rr rear right)");
        FitVehicle(ForgeSelection());
        float err = vehicleWheels == null ? 99 : vehicleWheels.Max(w => vehicleDonor.Wheels.TryGetValue(w.Key, out var dw) ? Vector2.Distance(new Vector2(w.Value.X, w.Value.Y), new Vector2(dw.X, dw.Y)) : 99);
        Expect(err < 0.05f, $"car fitted: its wheels on the donor's wheels (worst {err:0.000} m)");
        yield return Frames(4);
        yield return Shot("forge_vehicle");
        ExportVehicle(ForgeSelection());
        var vdir = forgeReport?.Folder;
        var yft = vdir != null ? Path.Combine(vdir, "stream", "molo_testcar.yft") : null;
        var info = yft != null && File.Exists(yft) ? Core.Gta.Forge.ForgeCommon.YftInfo(File.ReadAllBytes(yft)) : default;
        Expect(forgeReport != null && forgeReport.Ok && info.Bones != null && info.Bones.Count == vehicleDonor.Bones.Count && info.Models >= 1,
            $"vehicle written: molo_testcar.yft on the sultan2 skeleton ({info.Bones?.Count} bones, {info.Models} body model(s), {info.Children} physics children)" + (forgeReport != null ? " " + string.Join(" | ", forgeReport.Errors.Concat(forgeReport.Warnings)) : ""));
        string VT(string f) => vdir != null && File.Exists(Path.Combine(vdir, f)) ? File.ReadAllText(Path.Combine(vdir, f)) : "";
        Expect(VT("data/vehicles.meta").Contains("molo_testcar") && VT("data/handling.meta").Contains("MOLO_TESTCAR") && VT("fxmanifest.lua").Contains("VEHICLE_METADATA_FILE") && File.Exists(Path.Combine(vdir ?? "", "stream", "molo_testcar.ytd")),
            "vehicle metas cloned from sultan2 (vehicles, handling, carvariations), donor textures as molo_testcar.ytd, fxmanifest + /spawn command");
        Scene.Objects.RemoveAll(o => o.Folder == ForgeFolder);
        propTab = PropTab.Object;
    }

    /// <summary>Modeling right on the map: a box made and edited in MAP mode (undo included), MLO rooms built on the ground,
    /// a game model opened in place, and all of it written with the map resource.</summary>
    IEnumerable<object> MapBuildTest(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index " + gameError); SetMapMode(false); yield break; }
        var legion = new Vector3(195, -934, 30);
        GoTo(legion); var cam = Viewports[ActiveViewport].Camera; cam.Distance = 70; cam.Pitch = 0.6f;
        LoadAreaAt(legion);
        yield return Until(() => mapLoadQueue.Count == 0 && mapLoadTask == null && entOf.Count > 100 && archSlots.Values.Count(a => a.State == 2) > 60, 240);
        yield return Until(() => archLoading == 0, 60);
        // 1. a box on the ground, edited like in MODEL mode
        AddPrimitive(CreateKind.Box);
        var box = Current;
        Expect(box != null && MapEditable(box) && box.Folder == MapBuildFolder && Vector2.Distance(new Vector2(box.Position.X, box.Position.Y), new Vector2(legion.X, legion.Y)) < 80,
            $"box made on the map at {box?.Position.X:0.0}, {box?.Position.Y:0.0}, {box?.Position.Z:0.0} (in '{box?.Folder}')");
        SetLevel(SubObject.Polygon);
        Expect(Level == SubObject.Polygon, "edit mode works in MAP mode on what is modeled here");
        var v0 = box.Mesh.Verts[0];
        Do("Pull a vertex", () => { box.Mesh.Verts[0] += new Vector3(0, 0, 1); box.Touch(); });
        DoUndo(); box = Scene.Find(box.Id);
        Expect(Vector3.Distance(box.Mesh.Verts[0], v0) < 1e-5f, "undo restores the edited mesh in MAP mode");
        DoRedo(); box = Scene.Find(box.Id);
        Expect(Vector3.Distance(box.Mesh.Verts[0], v0 + new Vector3(0, 0, 1)) < 1e-5f, "redo too");
        SetLevel(SubObject.Object);
        // 2. MLO rooms right on the map
        Scene.Plan = new MoloMax.Core.Tools.RoomPlanData();
        OpenPlanOnMap();
        var org = new Vector3(Scene.Plan.OriginX, Scene.Plan.OriginY, Scene.Plan.OriginZ);
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "a", X0 = 0, Y0 = 0, X1 = 5, Y1 = 4 });
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "b", X0 = 5, Y0 = 0, X1 = 9, Y1 = 4 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 5, Y = 2 });
        BuildPlan(); planOpen = false;
        var rooms = Scene.Objects.Where(o => o.Folder == PlanFolder && o.GtaType == GtaObjectType.MloRoom).ToList();
        Expect(rooms.Count == 2 && rooms.All(r => Vector3.Distance(r.Position, org) < 12) && Vector2.Distance(new Vector2(org.X, org.Y), new Vector2(legion.X, legion.Y)) < 80,
            $"MLO rooms built on the ground of the map ({rooms.Count} rooms around {org.X:0}, {org.Y:0}, {org.Z:0.0})");
        // 3. a game model opened in place
        var ent = Scene.Objects.Where(o => entOf.TryGetValue(o.Id, out var de) && de.Doc.FromGame && !de.Ent.IsMlo && archSlots.TryGetValue(de.Ent.ArchetypeHash, out var sl) && sl.State == 2 && o.Mesh.Faces.Count > 20)
                               .OrderBy(o => Vector3.DistanceSquared(o.Position, legion)).FirstOrDefault();
        Expect(ent != null, "a game model near the square");
        if (ent != null)
        {
            var (doc, me) = entOf[ent.Id]; int n0 = doc.Entities.Count; var shared = ent.Mesh;
            SelectOnly(ent);
            SetLevel(SubObject.Polygon);
            Expect(Level == SubObject.Polygon && MapEditable(ent) && !ReferenceEquals(ent.Mesh, shared) && doc.Entities.Count == n0 - 1,
                $"game model {ent.Name} opened in place: own mesh copy, entity left its .ymap ({n0} -> {doc.Entities.Count})");
            Do("Edit", () => { ent.Mesh.Verts[0] += new Vector3(0, 0, 0.5f); ent.Touch(); });
            Expect(!ReferenceEquals(shared, ent.Mesh) && Vector3.Distance(shared.Verts[0], ent.Mesh.Verts[0]) > 0.4f, "the other places using that model keep the original");
            SetLevel(SubObject.Object);
            yield return Frames(5);
            yield return Shot("map_build");
            // 4. saved with the map
            var outDir = StPath("map_build_out");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            int saved = SaveMaps(outDir);
            var build = Path.Combine(outDir, "stream", "build");
            var files = Directory.Exists(build) ? Directory.GetFiles(build, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList() : new List<string>();
            Note("build files: " + string.Join(", ", files.Take(20)) + (mapBuildReport != null && mapBuildReport.Warnings.Count > 0 ? "  warnings: " + string.Join(" | ", mapBuildReport.Warnings.Take(4)) : ""));
            Expect(files.Count(f => f.EndsWith(".ydr")) >= 2 && files.Any(f => f.EndsWith(".ytyp")) && files.Any(f => f.Contains("_milo_") && f.EndsWith(".ymap")) && files.Any(f => f.EndsWith("_exterior.ymap")),
                $"saved with the map: {files.Count(f => f.EndsWith(".ydr"))} models, ytyp, MLO placement, exterior entities in stream/build ({saved} files)" + (mapBuildReport != null && mapBuildReport.Errors.Count > 0 ? " errors: " + string.Join(" | ", mapBuildReport.Errors) : ""));
            var back = MoloMax.Core.Gta.Map.MapDocument.Load(File.ReadAllBytes(Path.Combine(outDir, "stream", doc.Name + ".ymap")), doc.Name + ".ymap");
            Expect(back.Entities.Count == n0 - 1, $"the edited model's original entity is gone from {doc.Name}.ymap ({n0} -> {back.Entities.Count})");
            var ext = Directory.GetFiles(build, "*_exterior.ymap", SearchOption.AllDirectories).FirstOrDefault();
            if (ext != null)
            {
                var ey = MoloMax.Core.Gta.Map.MapDocument.Load(File.ReadAllBytes(ext), Path.GetFileName(ext));
                Expect(ey.Entities.Count > 0 && ey.Entities.All(e => Vector2.Distance(new Vector2(e.Position.X, e.Position.Y), new Vector2(legion.X, legion.Y)) < 150),
                    $"the build is placed on the map where it was made ({ey.Entities.Count} exterior entities around Legion Square: {string.Join(", ", ey.Entities.Take(4).Select(e => $"{e.Archetype} at {e.Position.X:0}, {e.Position.Y:0}, {e.Position.Z:0}"))})");
            }
        }
        SetMapMode(false);
    }

    IEnumerable<object> MapModeTest(string gtaFolder)
    {
        Fresh();
        var modelCount = Scene.Objects.Count;
        Prim(CreateKind.Box, Vector3.Zero).Name = "model_scene_box";
        SetMapMode(true); ClearMapBuild();
        bool keepStream = MapStream; MapStream = false; // the click tests first; the HD around the camera is tested at the end
        Expect(MapMode && Scene.Objects.Count == 0, "MAP tab: an empty world scene, the model scene kept aside");
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        Expect(game != null, "game files loaded " + gameError);
        if (game == null) { SetMapMode(false); yield break; }
        var t0 = DateTime.Now;
        yield return Until(() => world != null, 600);
        Expect(world != null, $"map index: {world?.Ymaps.Count:N0} .ymap in {(DateTime.Now - t0).TotalSeconds:0} s ({worldStatus})");
        if (world == null) { SetMapMode(false); yield break; }
        GoTo(new Vector3(195, -934, 30));
        var cam = Viewports[ActiveViewport].Camera; cam.Distance = 90;
        // the whole map first (far models), nothing editable loaded yet
        var tw = DateTime.Now;
        yield return Until(() => backdrop.Count > 300 && archSlots.Values.Count(a => a.State == 2) > 30, 240);
        Expect(backdrop.Count > 300 && entOf.Count == 0, $"whole map shown with far models: {backdrop.Count:N0} far models from {backdropMaps}/{backdropTotal} maps in {(DateTime.Now - tw).TotalSeconds:0} s, no editable map loaded ({entOf.Count})");
        // overview of the whole map from high above (like CodeWalker): wait until everything seen from there is read
        var keepTarget = cam.Target; float keepPitch = cam.Pitch;
        cam.Target = new Vector3(-300, 900, 0); cam.Distance = 6500; cam.Pitch = 1.05f;
        var tv = DateTime.Now; int up0v = renderer.Uploads;
        int FarWaiting() => backdrop.Count(o => !FarSteppedAside(o, cam.Eye) && FarLodVisible(o, cam.Eye) && mapSlotOf.TryGetValue(o.InstanceKey, out var sl) && sl.State <= 1);
        yield return Until(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal && archLoading == 0 && FarWaiting() == 0, 420);
        int shown = backdrop.Count(o => FarLodVisible(o, cam.Eye));
        var lv = new int[6]; foreach (var o in backdrop) if (FarLodVisible(o, cam.Eye) && farInfo.TryGetValue(o.Id, out var fi)) lv[fi.Level]++;
        Expect(FarWaiting() == 0, $"whole map overview read in {(DateTime.Now - tv).TotalSeconds:0} s: {shown:N0} far models at their distance (LOD {lv[1]}, SLOD1 {lv[2]}, SLOD2 {lv[3]}, SLOD3 {lv[4]}), "
            + $"{archSlots.Values.Count(a => a.State == 2):N0} models ready, {archSlots.Values.Count(a => a.State == 3):N0} not found, {ArchParallel} read at once");
        foreach (var grp in archSlots.Values.Where(a => a.State == 3).GroupBy(a => (a.Error ?? "?").Split(':')[0]).OrderByDescending(gq => gq.Count()).Take(6))
            Note($"not found x{grp.Count()}: {grp.First().Name ?? grp.First().Hash.ToString("X8")} - {grp.First().Error}");
        yield return Frames(20);
        yield return Shot("map_overview");
        {
            int so = stDts.Count; ResetWorstFrame();
            for (int i = 0; i < 40; i++) { cam.Orbit(3, 0); yield return Frames(1); }
            renderer.StatDraws = 0; renderer.StatTris = 0; yield return Frames(1);
            Note($"overview: one frame = {renderer.StatDraws:N0} GPU draws, {renderer.StatTris:N0} triangles; LOD links: {farInfo.Values.Count(f => f.HasParent):N0} with parent, {farInfo.Values.Count(f => f.HasKids):N0} with children, {farLinks.Count:N0} parents not found");
            var od = stDts.Skip(so).Where(x => x > 0).OrderBy(x => x).ToList();
            Note($"overview: {MapVisible:N0} drawn, {MapDrawCalls} draw calls, frame median {(od.Count > 0 ? od[od.Count / 2] * 1000 : 0):0.0} ms; profile {ProfileText()}; {WorstFrameText()}");
        }
        // levels of detail: one checkbox each, switching one off hides that level and lets its neighbours show at every distance
        {
            int Shown(int lvl) => backdrop.Count(o => farInfo.TryGetValue(o.Id, out var f) && f.Level == lvl && FarLodVisible(o, cam.Eye));
            int s1 = Shown(2), s2before = Shown(3);
            SetMapLevel(2, false); yield return Frames(2);
            int s1off = Shown(2), s2after = Shown(3);
            SetMapLevel(2, true); yield return Frames(2);
            Expect(s1 > 100 && s1off == 0 && s2after >= s2before && Shown(2) == s1, $"LOD levels switch per level: SLOD1 {s1} shown -> {s1off} when off (SLOD2 {s2before} -> {s2after}), back to {Shown(2)}");
            int minBefore = backdropLevel;
            var shownBefore = new[] { Shown(2), Shown(3), Shown(4) };
            int readyBefore = backdrop.Count(o => o.Mesh != null && FarLodVisible(o, cam.Eye) && mapSlotOf.TryGetValue(o.InstanceKey, out var sl) && sl.State == 2);
            SetMapLevel(1, true); yield return Frames(3);
            Expect(backdropLevel == 1 && minBefore == 2, $"switching LOD on reads the LOD maps (finest level read {minBefore} -> {backdropLevel})");
            yield return Until(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal, 120);
            int withLod = backdrop.Count;
            SetMapLevel(1, false); yield return Frames(3);
            Expect(backdropLevel == 2 && backdrop.Count < withLod && !farInfo.Values.Any(f => f.Level < 2), $"switching LOD off lets its models go ({withLod:N0} -> {backdrop.Count:N0} far models)");
            var shownAfter = new[] { Shown(2), Shown(3), Shown(4) };
            int readyAfter = backdrop.Count(o => o.Mesh != null && FarLodVisible(o, cam.Eye) && mapSlotOf.TryGetValue(o.InstanceKey, out var sl) && sl.State == 2);
            int placeholders = backdrop.Count(o => FarLodVisible(o, cam.Eye) && mapSlotOf.TryGetValue(o.InstanceKey, out var sl) && sl.State == 2 && !ReferenceEquals(o.Mesh, sl.Mesh));
            Expect(shownAfter.SequenceEqual(shownBefore) && placeholders == 0, $"after LOD on / off the same SLOD models show (SLOD1/2/3 {string.Join("/", shownBefore)} -> {string.Join("/", shownAfter)}), ready {readyBefore} -> {readyAfter}, {placeholders} still on a placeholder");
        }
        cam.Pitch = 0.5f; cam.Distance = 1800; cam.Target = new Vector3(100, -600, 0);
        yield return Until(() => archLoading == 0 && FarWaiting() == 0, 120);
        yield return Frames(20);
        yield return Shot("map_city_far");
        cam.Target = keepTarget; cam.Pitch = keepPitch;
        cam.Distance = 900; yield return Frames(30);
        yield return Shot("map_whole_world");
        cam.Distance = 90;
        // a click on the far world loads that area for editing
        int st1 = stDts.Count;
        for (int i = 0; i < 60; i++) { cam.Orbit(6, 0); yield return Frames(1); }
        var dw = stDts.Skip(st1).Where(x => x > 0).OrderBy(x => x).ToList();
        Note($"whole map frame time: median {(dw.Count > 0 ? dw[dw.Count / 2] * 1000 : 0):0.0} ms ({MapVisible:N0} visible, {MapDrawCalls} draw calls)");
        Note("frame profile (ms): " + ProfileText());
        int queued = LoadAreaAt(new Vector3(195, -934, 30));
        yield return Until(() => mapLoadQueue.Count == 0 && mapLoadTask == null && entOf.Count > 100 && archSlots.Values.Count(a => a.State == 2) > 60, 240);
        Expect(queued > 0 && entOf.Count > 100, $"click on Legion Square: {queued} map(s) loaded for editing, {entOf.Count} entities, {backdropHidden.Count} far models stepped aside");
        yield return Frames(5);
        yield return Until(() => renderer.Textures.Pending == 0, 15);
        yield return Shot("map_legion_square");
        // smoothness: frame time with the streamed world on screen (the camera turning around the square)
        int st0 = stDts.Count; int up0 = renderer.Uploads; ResetWorstFrame();
        for (int i = 0; i < 120; i++) { cam.Orbit(6, 0); yield return Frames(1); }
        var dts = stDts.Skip(st0).Where(x => x > 0).OrderBy(x => x).ToList();
        float med = dts.Count > 0 ? dts[dts.Count / 2] * 1000 : 0, p95 = dts.Count > 0 ? dts[(int)(dts.Count * 0.95)] * 1000 : 0;
        Note($"map frame time: median {med:0.0} ms, 95% {p95:0.0} ms  ({MapVisible:N0} visible, {MapDrawCalls} draw calls, {entOf.Count:N0} entities)");
        Note("frame profile (ms): " + ProfileText() + $"  ·  GPU uploads during the turn: {renderer.Uploads - up0}");
        Note(WorstFrameText());
        Expect(med < 30, $"map mode stays smooth (median {med:0.0} ms per frame)");
        // place a bench on the surface in the middle of the view
        var vp = Viewports[ActiveViewport];
        StartPlacement("prop_bench_01a");
        yield return Until(() => archSlots.TryGetValue(MoloMax.Core.Gta.Map.MapDocument.Hash("prop_bench_01a"), out var sl) && sl.State >= 2, 60);
        // median of single picks: one per frame while the mouse moves (models still loading in the background add noise)
        var picks = new List<double>();
        for (int i = 0; i < 21; i++)
        {
            var swp = System.Diagnostics.Stopwatch.StartNew();
            MapPick(vp, new Vector2(vp.X + vp.W * (0.3f + i * 0.02f), vp.Y + vp.H * 0.6f), placeGhost, out _, out _);
            picks.Add(swp.Elapsed.TotalMilliseconds);
        }
        picks.Sort(); double pickMs = picks[picks.Count / 2];
        Expect(pickMs < 8, $"surface under the mouse found in {pickMs:0.00} ms (placing stays smooth)");
        bool hit = MapPick(vp, new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2), placeGhost, out var p, out var n);
        PlaceGhostAt(p, n);
        int placed = ConfirmPlacement();
        EndPlacement();
        var bench = Scene.Find(placed);
        Expect(hit && bench != null && bench.Gta.MapArchetype == "prop_bench_01a", $"bench placed on the surface at {p.X:0.0}, {p.Y:0.0}, {p.Z:0.0} (normal z {n.Z:0.00})");
        yield return Frames(3);
        yield return Shot("map_bench_placed");
        // move one game entity, delete another, save
        var gameObjs = Scene.Objects.Where(o => entOf.TryGetValue(o.Id, out var de) && de.Doc.FromGame && !de.Ent.IsMlo).ToList();
        var moved = gameObjs.First(); var movedDoc = entOf[moved.Id].Doc;
        var del = gameObjs.First(o => o != moved && entOf[o.Id].Doc == movedDoc);
        int before = movedDoc.Entities.Count;
        var mp0 = moved.Position;
        Do("Move", () => moved.Position += new Vector3(2, 0, 0));
        SelectOnly(del); DeleteSelection();
        var outDir = StPath("map_out");
        int saved = SaveMaps(outDir);
        Expect(saved >= 2 && File.Exists(Path.Combine(outDir, "fxmanifest.lua")), $"{saved} .ymap saved + fxmanifest (this_is_a_map)");
        var back = MoloMax.Core.Gta.Map.MapDocument.Load(File.ReadAllBytes(Path.Combine(outDir, "stream", movedDoc.Name + ".ymap")), movedDoc.Name + ".ymap");
        Expect(back.Entities.Count == before - 1, $"edited game map: {before} -> {back.Entities.Count} entities (one deleted)");
        Expect(back.Entities.Any(e => Vector3.Distance(e.Position, mp0 + new Vector3(2, 0, 0)) < 0.01f), "moved entity saved at its new place");
        var mine = MoloMax.Core.Gta.Map.MapDocument.Load(File.ReadAllBytes(Path.Combine(outDir, "stream", ActiveDoc().Name + ".ymap")), ActiveDoc().Name);
        Expect(mine.Entities.Any(e => e.ArchetypeHash == MoloMax.Core.Gta.Map.MapDocument.Hash("prop_bench_01a")), "new map holds the placed bench");
        // a game interior (MLO) near the square: open it, move one object, delete one, save its .ytyp
        var legion = new Vector3(195, -934, 30);
        var iy = world.Ymaps.Where(y => y.Interior && !mapDocs.ContainsKey(y.Name)).OrderBy(y => Vector3.DistanceSquared((y.Min + y.Max) * 0.5f, legion)).FirstOrDefault();
        if (iy == null) Expect(false, "an interior map near Legion Square");
        else
        {
            mapLoadQueue.Enqueue(iy);
            yield return Until(() => mapDocs.ContainsKey(iy.Name), 60);
            var mloObj = Scene.Objects.FirstOrDefault(o => entOf.TryGetValue(o.Id, out var de) && de.Ent.IsMlo && de.Doc.Name == iy.Name);
            bool opened = mloObj != null && EditInterior(mloObj);
            var oi = opened ? interiors.Values.First(i => i.InstanceId == mloObj.Id) : null;
            Expect(opened && oi.Doc.Entities.Count > 3, $"interior {(oi?.Doc.ArchetypeName ?? mloObj?.Name ?? "?")} of {iy.Name} opened: {oi?.Doc.Entities.Count} objects in {(oi?.Doc.Rooms.Count ?? 1) - 1} rooms ({status})");
            if (oi != null)
            {
                yield return Until(() => oi.ObjOf.Values.Select(id => Scene.Find(id)).Count(o => o != null && mapSlotOf.TryGetValue(o.InstanceKey, out var sl) && sl.State >= 2) > oi.Doc.Entities.Count / 2, 90);
                var objs = oi.ObjOf.Values.Select(id => Scene.Find(id)).Where(o => o != null).ToList();
                var (bmn, bmx) = (objs.Select(o => o.Position).Aggregate(Vector3.Min), objs.Select(o => o.Position).Aggregate(Vector3.Max));
                cam.Target = (bmn + bmx) * 0.5f; cam.Distance = MathF.Max(12, (bmx - bmn).Length() * 0.9f); cam.Pitch = 0.6f;
                foreach (var o in Scene.Objects) if (entOf.ContainsKey(o.Id) && !intEntOf.ContainsKey(o.Id)) o.Hidden = true; // the interior alone on the picture
                yield return Frames(20);
                yield return Shot("map_interior");
                foreach (var o in Scene.Objects) if (entOf.ContainsKey(o.Id)) o.Hidden = false;
                var im = objs.First(o => !o.Name.Contains("shell")); var imEnt = intEntOf[im.Id].Ent;
                var id2 = objs.First(o => o != im && !o.Name.Contains("shell"));
                var lp0 = imEnt.Position;
                Do("Move", () => im.Position += new Vector3(1, 0, 0));
                SelectOnly(id2); DeleteSelection();
                int n0 = oi.Doc.Entities.Count;
                var outI = StPath("map_out_interior");
                int savedI = SaveMaps(outI);
                var ypath = Path.Combine(outI, "stream", oi.Doc.Name + ".ytyp");
                var backI = File.Exists(ypath) ? MoloMax.Core.Gta.Map.MloDocument.Load(File.ReadAllBytes(ypath), oi.Doc.Name + ".ytyp", oi.Doc.ArchetypeHash) : null;
                Expect(backI != null && backI.Entities.Count == n0 - 1, $"interior .ytyp saved ({oi.Doc.Name}.ytyp): {n0} -> {backI?.Entities.Count} objects");
                var want = lp0 + Vector3.Transform(new Vector3(1, 0, 0), Quaternion.Conjugate(oi.Rot));
                Expect(backI != null && backI.Entities.Any(e => Vector3.Distance(e.Position, want) < 0.01f), "moved interior object saved at its new place (interior space)");
            }
        }
        // HD around the camera: zoomed in, the real models of the area load by themselves; zoomed out, they go
        {
            foreach (var o in Scene.Objects) o.Hidden = false;
            MapStream = true; SetMapLevel(0, true); SetMapLevel(5, true);
            var pier = new Vector3(-1640, -1060, 13);
            int Streamed() => mapDocs.Values.Count(d => d.FromGame && !mapPinned.Contains(d.Name) && d.Name != mapActive);
            int HdNear() => Scene.Objects.Count(o => !o.Hidden && entOf.TryGetValue(o.Id, out var de) && de.Ent.IsHd && Vector3.Distance(o.Position, pier) < MapStreamRadius + 60);
            GoTo(pier); cam.Distance = 120; cam.Pitch = 0.45f;
            var th = DateTime.Now;
            yield return Until(() => mapStreamQueue.Count == 0 && mapLoadTask == null && Streamed() > 0 && HdNear() > 50, 120);
            int hd = HdNear(), maps = Streamed();
            Expect(hd > 50, $"zoomed in on Del Perro pier: the HD around the camera loads by itself ({maps} map(s), {hd:N0} HD models in {(DateTime.Now - th).TotalSeconds:0} s)");
            yield return Until(() => archLoading == 0, 60);
            yield return Frames(20);
            yield return Shot("map_hd_auto");
            yield return Until(() => renderer.Textures.Pending == 0 && archLoading == 0, 30);
            yield return Frames(60);
            {
                int s0 = stDts.Count; ResetWorstFrame();
                for (int i = 0; i < 90; i++) { cam.Orbit(4, 0); yield return Frames(1); }
                renderer.StatDraws = 0; renderer.StatTris = 0; yield return Frames(1);
                var hdt = stDts.Skip(s0).Where(x => x > 0).OrderBy(x => x).ToList();
                float hmed = hdt.Count > 0 ? hdt[hdt.Count / 2] * 1000 : 0, h95 = hdt.Count > 0 ? hdt[(int)(hdt.Count * 0.95)] * 1000 : 0;
                Note($"HD around the camera: {Scene.Objects.Count:N0} scene objects, {MapVisible:N0} drawn, {MapDrawCalls} draw calls, {renderer.StatTris:N0} triangles; profile {ProfileText(14)}; {WorstFrameText()}");
                Expect(hmed < 30, $"HD around the camera stays smooth: median {hmed:0.0} ms, 95% {h95:0.0} ms per frame");
            }
            cam.Distance = 1500;
            yield return Until(() => Streamed() == 0, 30);
            Expect(Streamed() == 0, $"zoomed out: the streamed HD maps go ({maps} -> {Streamed()}), the changed ones stay ({mapDocs.Count} open)");
            MapStream = keepStream;
        }
        SetMapMode(false);
        Expect(!MapMode && Obj("model_scene_box") != null, "back to MODEL: the model scene is untouched");
    }
}
