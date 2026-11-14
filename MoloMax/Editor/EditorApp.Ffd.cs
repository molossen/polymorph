using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Editing the control points of an FFD modifier (3ds Max FFD "Control Points" sub-object, Blender lattice edit mode):
/// the cage is drawn on the object, a click takes a point (Shift adds / removes), a drag on empty space box-selects,
/// dragging a selected point moves all the selected ones (X / Y / Z lock the move on that axis, A selects all).
/// Esc, Enter or a right click leave.
/// </summary>
public sealed partial class EditorApp
{
    SceneObject ffdObj; FfdModifier ffdMod;
    readonly HashSet<int> ffdSel = new();
    int ffdHover = -1, ffdAxis = -1;
    bool ffdDragging; Vector2? ffdBox;
    Vector3 ffdGrab; Vector3[] ffdStart;

    public void BeginFfdEdit(SceneObject o, FfdModifier mod)
    {
        if (o == null || mod == null) return;
        if (ModalActive) CancelModal();
        if (Level != SubObject.Object) SetLevel(SubObject.Object);
        if (!mod.HasBox) { mod.Fit(Core.Modifiers.ModifierStack.Evaluate(o.Mesh, o.Modifiers.TakeWhile(m => m != mod).ToList(), false, false), false); o.Touch(); }
        ffdObj = o; ffdMod = mod; ffdSel.Clear(); ffdHover = -1; ffdAxis = -1; ffdDragging = false; ffdBox = null;
        StartModalCommon(ModalKind.FfdPoints, Viewports[ActiveViewport], Vector3.Zero);
        FfdHeader();
        SetStatus("FFD points: click a point (Shift adds), drag on empty space to box-select, drag a point to move. X / Y / Z lock an axis, A all. Esc to leave.");
    }

    void FfdHeader() => modal.Header = $"{ffdMod?.Name}: {ffdSel.Count} point(s) selected" + (ffdAxis >= 0 ? $"   locked on {"XYZ"[ffdAxis]}" : "") + "   X Y Z lock   A all   Esc leave";

    Vector3 FfdWorld(int idx) => Vector3.Transform(ffdMod.Point(idx), ffdObj.World);

    bool FfdScreen(Viewport vp, Vector3 w, out Vector2 s)
    {
        bool ok = vp.Camera.Project(w, vp.W, vp.H, out var l, out float depth);
        s = new Vector2(vp.X + l.X, vp.Y + l.Y);
        return ok && depth > 0;
    }

    /// <summary>The cage point under the mouse (10 px); points that fall on the same spot: the one nearest the camera.</summary>
    int FfdPick(Viewport vp, Vector2 mouse)
    {
        var eye = vp.Camera.Eye;
        var cand = new List<(int I, float D, float Z)>();
        for (int i = 0; i < ffdMod.Count; i++)
        {
            var w = FfdWorld(i);
            if (!FfdScreen(vp, w, out var s)) continue;
            float d = Vector2.Distance(s, mouse);
            if (d < 10) cand.Add((i, d, Vector3.Distance(w, eye)));
        }
        if (cand.Count == 0) return -1;
        float dmin = cand.Min(c => c.D);
        return cand.Where(c => c.D <= dmin + 2).OrderBy(c => c.Z).First().I;
    }

    void FfdKeys()
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        if (P(ImGuiKey.X)) ffdAxis = ffdAxis == 0 ? -1 : 0;
        if (P(ImGuiKey.Y)) ffdAxis = ffdAxis == 1 ? -1 : 1;
        if (P(ImGuiKey.Z)) ffdAxis = ffdAxis == 2 ? -1 : 2;
        if (P(ImGuiKey.A)) { if (ffdSel.Count == ffdMod.Count) ffdSel.Clear(); else for (int i = 0; i < ffdMod.Count; i++) ffdSel.Add(i); }
        FfdHeader();
    }

    void FfdMouse(Viewport hv)
    {
        if (ffdObj == null || ffdMod == null || !Scene.Objects.Contains(ffdObj) || !ffdObj.Modifiers.Contains(ffdMod)) { modal.Kind = ModalKind.None; return; }
        if (Input.Pressed(MouseButtons.Right) && !ffdDragging) { modal.Kind = ModalKind.None; return; }
        var vp = ffdDragging ? modal.Vp : hv;
        if (vp == null) return;
        if (!ffdDragging && ffdBox == null) ffdHover = ImGui.GetIO().WantCaptureMouse ? -1 : FfdPick(vp, Input.Mouse);

        if (Input.Pressed(MouseButtons.Left) && !ImGui.GetIO().WantCaptureMouse)
        {
            bool shift = Input.Shift;
            if (ffdHover >= 0)
            {
                if (shift) { if (!ffdSel.Remove(ffdHover)) ffdSel.Add(ffdHover); }
                else if (!ffdSel.Contains(ffdHover)) { ffdSel.Clear(); ffdSel.Add(ffdHover); }
                if (ffdSel.Contains(ffdHover))
                {
                    // start moving the selected points on the plane facing the camera through the grabbed one
                    Undo.Record("Move FFD points", Scene);
                    modal.Vp = vp;
                    ffdDragging = true;
                    ffdGrab = FfdWorld(ffdHover);
                    ffdStart = new Vector3[ffdMod.Count];
                    for (int i = 0; i < ffdMod.Count; i++) ffdStart[i] = ffdMod.Offset(i);
                    modal.MouseStart = Input.Mouse;
                }
            }
            else { if (!shift) ffdSel.Clear(); ffdBox = Input.Mouse; modal.Vp = vp; }
            FfdHeader();
        }

        if (ffdDragging)
        {
            if (!Input.Down(MouseButtons.Left)) { ffdDragging = false; Dirty = true; FfdHeader(); return; }
            var cam = vp.Camera;
            var n = Vector3.Normalize(cam.Forward);
            Vector3 PlaneHit(Vector2 m)
            {
                var r = RayAt(vp, m); float dn = Vector3.Dot(r.Direction, n);
                if (MathF.Abs(dn) < 1e-6f) return ffdGrab;
                return r.Origin + r.Direction * (Vector3.Dot(ffdGrab - r.Origin, n) / dn);
            }
            var delta = PlaneHit(Input.Mouse) - PlaneHit(modal.MouseStart);
            if (ffdAxis >= 0)
            {
                // locked on a world axis: the mouse moves along that axis only
                var ax = ffdAxis == 0 ? Vector3.UnitX : ffdAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                var r = RayAt(vp, Input.Mouse); var r0 = RayAt(vp, modal.MouseStart);
                float t1 = Picking.RayLineParam(r, ffdGrab, ax), t0 = Picking.RayLineParam(r0, ffdGrab, ax);
                delta = ax * (t1 - t0);
            }
            if (SnapActive && GridStep > 0) delta = new Vector3(SnapValue(delta.X, GridStep), SnapValue(delta.Y, GridStep), SnapValue(delta.Z, GridStep));
            // world delta into the object's local space (the cage lives there)
            Matrix4x4.Invert(ffdObj.World, out var inv);
            var local = Vector3.TransformNormal(delta, inv);
            foreach (var i in ffdSel) ffdMod.SetOffset(i, ffdStart[i] + local);
            ffdObj.Touch();
            var d = delta;
            SetStatus($"Move {ffdSel.Count} FFD point(s)  {d.X:0.###}, {d.Y:0.###}, {d.Z:0.###} m");
            return;
        }

        if (ffdBox is { } b0)
        {
            if (Input.Down(MouseButtons.Left)) return;
            var b1 = Input.Mouse; ffdBox = null;
            var mn = Vector2.Min(b0, b1); var mx = Vector2.Max(b0, b1);
            if (Vector2.Distance(b0, b1) < 3) { FfdHeader(); return; }
            for (int i = 0; i < ffdMod.Count; i++)
                if (FfdScreen(vp, FfdWorld(i), out var s) && s.X >= mn.X && s.Y >= mn.Y && s.X <= mx.X && s.Y <= mx.Y) ffdSel.Add(i);
            FfdHeader();
        }
    }

    void FfdOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind != ModalKind.FfdPoints || ffdMod == null || ffdObj == null) return;
        var m = ffdMod;
        uint line = 0xC0FFB040, pt = 0xFF60D0FF, sel = 0xFF30A0FF, hot = ImGui.ColorConvertFloat4ToU32(Theme.Gold);
        void L(int a, int b)
        {
            if (FfdScreen(vp, FfdWorld(a), out var sa) && FfdScreen(vp, FfdWorld(b), out var sb)) dl.AddLine(sa, sb, line, 1.2f);
        }
        for (int k = 0; k < m.Nz; k++)
            for (int j = 0; j < m.Ny; j++)
                for (int i = 0; i < m.Nx; i++)
                {
                    int idx = m.Index(i, j, k);
                    if (i + 1 < m.Nx) L(idx, m.Index(i + 1, j, k));
                    if (j + 1 < m.Ny) L(idx, m.Index(i, j + 1, k));
                    if (k + 1 < m.Nz) L(idx, m.Index(i, j, k + 1));
                }
        for (int i = 0; i < m.Count; i++)
        {
            if (!FfdScreen(vp, FfdWorld(i), out var s)) continue;
            bool on = ffdSel.Contains(i);
            float r = i == ffdHover ? 6 : on ? 5 : 4;
            dl.AddRectFilled(s - new Vector2(r), s + new Vector2(r), i == ffdHover ? hot : on ? sel : pt);
            dl.AddRect(s - new Vector2(r + 1), s + new Vector2(r + 1), 0xFF202020);
        }
        if (ffdBox is { } b && modal.Vp == vp)
        {
            dl.AddRectFilled(Vector2.Min(b, Input.Mouse), Vector2.Max(b, Input.Mouse), 0x2030A0FF);
            dl.AddRect(Vector2.Min(b, Input.Mouse), Vector2.Max(b, Input.Mouse), 0xC030A0FF);
        }
    }

    /// <summary>Modifier panel of an FFD.</summary>
    bool FfdFields(FfdModifier m, Action<string> label)
    {
        bool c = false;
        var o = Current;
        label("Points");
        int[] res = { m.Nx, m.Ny, m.Nz };
        if (ImGui.DragInt3("##ffdres", ref res[0], 0.05f, 2, 8))
        {
            m.SetResolution(res[0], res[1], res[2]); c = true;
        }
        Theme.Tip("Control points per axis (2x2x2, 3x3x3, 4x4x4; each axis can differ). Changing it resets the points.");
        c |= ImGui.Checkbox("Only in volume", ref m.InsideOnly);
        Theme.Tip("On: only the points inside the cage move. Off: the whole mesh follows.");
        bool editing = modal.Kind == ModalKind.FfdPoints && ffdMod == m;
        if (Theme.Toggle(editing ? "Editing control points (Esc)##ffded" : "Edit control points##ffded", editing, new Vector2(-1, 0)))
        {
            if (editing) modal.Kind = ModalKind.None; else BeginFfdEdit(o, m);
        }
        Theme.Tip("Shows the cage on the object: click points, drag to move them (X / Y / Z lock an axis).");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Reset##ffd", new Vector2(w2, 0)) && o != null)
        {
            Undo.Record("Reset FFD", Scene);
            for (int i = 0; i < m.Count; i++) m.SetOffset(i, Vector3.Zero);
            c = true;
        }
        Theme.Tip("Puts every point back in place.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Conform##ffd", new Vector2(w2, 0)) && o != null)
        {
            Undo.Record("Conform FFD", Scene);
            int idx = o.Modifiers.IndexOf(m);
            m.Fit(Core.Modifiers.ModifierStack.Evaluate(o.Mesh, o.Modifiers.Take(Math.Max(0, idx)).ToList(), false, false), true);
            c = true;
        }
        Theme.Tip("Fits the cage to the object again (and resets the points).");
        if (c) o?.Touch();
        return c;
    }
}
