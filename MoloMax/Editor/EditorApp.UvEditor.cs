using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// UV editor: the UVs of the current object in 2D over its texture. Selection is the same polygon selection as the
/// 3D view (faces or whole islands). Drag to move, buttons to rotate / flip / scale / fit / pack, texel density
/// shown and settable in px per metre.
/// </summary>
public sealed partial class EditorApp
{
    bool uvOpen, uvChecker;
    /// <summary>Selection mode like Blender's UV editor: 0 vertex, 1 edge, 2 face, 3 island (keys 1 2 3 4 on the canvas).</summary>
    int uvMode = 3;
    bool uvIsland { get => uvMode == 3; set => uvMode = value ? 3 : 2; }
    /// <summary>Selected UV corners in vertex / edge mode (face index &lt;&lt; 8 | corner). Face / island modes use the polygon selection.</summary>
    readonly HashSet<long> uvCorn = new();
    static long CK(int f, int c) => ((long)f << 8) | (uint)c;
    // hover (pre-selection): kind 0 vertex, 1 edge, 2 face; face and corner (edge = corner .. corner+1)
    (int Kind, int F, int C) uvHot = (-1, -1, -1);
    (int Obj, int Ver, PolyMesh M) uvVfKey; List<int>[] uvVf;
    Vector2? uvWinAt; // films: where the window stands
    Vector2 uvPan = new(20, 20); float uvZoom = 400;
    bool uvDragging, uvBoxing; Vector2 uvDragStart, uvLastMouse;
    float uvTargetDensity = 256; int uvTexSize = 1024;
    // transform tools: 0 move, 1 rotate, 2 scale; a running transform keeps the UVs it started from (no drift)
    int uvTool; float uvAngle = 45;
    bool uvHover; // the mouse is on the UV canvas: its keys (G R S W E, arrows) are not the 3D view's
    int uvXfMode = -1, uvXfAxis = -1; bool uvXfModal; Vector2 uvXfMouse0, uvXfPivot;
    Dictionary<int, Vector2[]> uvXfStart;

    public void OpenUvEditor() { uvOpen = true; if (Current != null && Level != SubObject.Polygon && Level != SubObject.Element) SetLevel(SubObject.Polygon); }

    void DrawUvEditor()
    {
        uvHover = false;
        if (!uvOpen) return;
        if (uvWinAt is Vector2 at) { ImGui.SetNextWindowSize(new Vector2(600, 600), ImGuiCond.Always); ImGui.SetNextWindowPos(at, ImGuiCond.Always); }
        else
        {
            ImGui.SetNextWindowSize(new Vector2(720, 640), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(60, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        }
        if (!ImGui.Begin("UV Editor###uved", ref uvOpen, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { ImGui.End(); return; }
        var o = Current;
        if (o == null || o.Mesh.Faces.Count == 0) { Ui.TextDisabled("Select one object with polygons."); ImGui.End(); return; }
        var m = o.Mesh; var sel = SubSel(o).Faces;

        // ---- toolbar
        string[] modeNames = { "Vertex", "Edge", "Face", "Island" };
        string[] modeTips = { "Vertex select (1): click near a UV point", "Edge select (2): click near a UV edge", "Face select (3): click a face", "Island select (4): click a piece, the whole island comes" };
        for (int mi = 0; mi < 4; mi++)
        {
            if (mi > 0) ImGui.SameLine(0, 2);
            if (Theme.Toggle(modeNames[mi] + "##uvm", uvMode == mi, new Vector2(mi == 3 ? 58 : 54, 0))) SetUvMode(mi, m, sel);
            Theme.Tip(modeTips[mi]);
        }
        ImGui.SameLine();
        uvCorn.RemoveWhere(k => (int)(k >> 8) >= m.Faces.Count || (int)(k & 0xFF) >= m.Faces[(int)(k >> 8)].V.Length);
        var selCorners = SelCorners(m, sel);
        IEnumerable<int> Target() => uvMode < 2 ? (uvCorn.Count > 0 ? uvCorn.Select(k => (int)(k >> 8)).Distinct().ToList() : Enumerable.Range(0, m.Faces.Count)) : sel.Count > 0 ? sel.ToList() : Enumerable.Range(0, m.Faces.Count);
        List<long> TargetCorners() => selCorners.Count > 0 ? selCorners : AllCorners(m);
        void Op(string name, Action a) { Do(name, () => { a(); o.Touch(); }); }
        var (bmn, bmx) = CornerBounds(m, TargetCorners());
        var pivot = (bmn + bmx) * 0.5f;
        void XfOp(string name, Matrix3x2 x) => Op(name, () => CornerTransform(m, TargetCorners(), UvTools.Around(pivot, x)));
        if (ImGui.Button("Rot 90")) XfOp("UV rotate", Matrix3x2.CreateRotation(MathF.PI / 2)); ImGui.SameLine();
        if (ImGui.Button("Flip U")) XfOp("UV flip", Matrix3x2.CreateScale(-1, 1)); ImGui.SameLine();
        if (ImGui.Button("Flip V")) XfOp("UV flip", Matrix3x2.CreateScale(1, -1)); ImGui.SameLine();
        if (ImGui.Button("x2")) XfOp("UV scale", Matrix3x2.CreateScale(2)); ImGui.SameLine();
        if (ImGui.Button("/2")) XfOp("UV scale", Matrix3x2.CreateScale(0.5f)); ImGui.SameLine();
        if (ImGui.Button("Fit 0-1")) Op("UV fit", () => UvTools.Fit01(m, Target())); ImGui.SameLine();
        if (ImGui.Button("Pack")) Op("UV pack", () => UvTools.Pack(m, Target())); Theme.Tip("Packs the islands into 0..1 (selection, or everything)."); ImGui.SameLine();
        if (ImGui.Button("Unwrap")) SmartUv(); Theme.Tip("Smart UV Project on the selection (or the whole object).");
        if (Theme.Toggle("Move", uvTool == 0, new Vector2(52, 0))) uvTool = 0; Theme.Tip("Move tool (W): drag the arrows. G: move with the mouse"); ImGui.SameLine(0, 2);
        if (Theme.Toggle("Rotate", uvTool == 1, new Vector2(58, 0))) uvTool = 1; Theme.Tip("Rotate tool (E): drag the circle. R: rotate with the mouse (Ctrl: 15° steps)"); ImGui.SameLine(0, 2);
        if (Theme.Toggle("Scale", uvTool == 2, new Vector2(52, 0))) uvTool = 2; Theme.Tip("Scale tool: drag an arrow (one axis) or the centre (both). S: scale with the mouse"); ImGui.SameLine();
        ImGui.SetNextItemWidth(70); ImGui.DragFloat("##uvang", ref uvAngle, 0.5f, -360, 360, "%.1f°"); ImGui.SameLine(0, 2);
        if (ImGui.Button("Rotate by")) XfOp("UV rotate", Matrix3x2.CreateRotation(uvAngle * MathF.PI / 180f));
        Theme.Tip("Turns the selection by this angle around its centre");

        float dens = UvTools.TexelDensity(m, Target(), uvTexSize, o.World);
        ImGui.AlignTextToFramePadding(); Ui.Text($"Texel density: {dens:0} px/m");
        ImGui.SameLine(); ImGui.SetNextItemWidth(70);
        int ts = Array.IndexOf(new[] { 256, 512, 1024, 2048 }, uvTexSize); if (ts < 0) ts = 2;
        if (ImGui.Combo("##uvts", ref ts, new[] { "256", "512", "1024", "2048" }, 4)) uvTexSize = new[] { 256, 512, 1024, 2048 }[ts];
        Theme.Tip("Texture size used for the density (GTA: 1024).");
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.DragFloat("##uvtd", ref uvTargetDensity, 1, 16, 4096, "%.0f px/m");
        ImGui.SameLine(); if (ImGui.Button("Set density")) Op("UV texel density", () => UvTools.SetDensity(m, Target(), uvTargetDensity, uvTexSize, o.World));
        Theme.Tip("Scales each island so every part of the model gets the same sharpness. 256 px/m is a good GTA interior value.");
        ImGui.SameLine(); ImGui.Checkbox("Checker", ref uvChecker);

        // ---- canvas
        var c0 = ImGui.GetCursorScreenPos(); var size = ImGui.GetContentRegionAvail();
        uvC0 = c0; uvCanvasSize = size;
        if (size.X < 50 || size.Y < 50) { ImGui.End(); return; }
        ImGui.InvisibleButton("##uvcanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool hov = ImGui.IsItemHovered(); bool active = ImGui.IsItemActive(); uvHover = hov || uvXfMode >= 0;
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(c0, c0 + size, true);
        dl.AddRectFilled(c0, c0 + size, 0xFF2A2A2A);
        Vector2 ToS(Vector2 uv) => c0 + uvPan + uv * uvZoom;
        Vector2 ToUv(Vector2 s) => (s - c0 - uvPan) / uvZoom;
        var io = ImGui.GetIO();
        if (hov && io.MouseWheel != 0)
        {
            var before = ToUv(io.MousePos);
            uvZoom = Math.Clamp(uvZoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 20, 20000);
            uvPan += (ToS(before) - io.MousePos) * -1;
        }
        if (active && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))) uvPan += io.MouseDelta;
        bool keys = (hov || UvCanvasUnderMouse) && !io.WantTextInput; // keys: also right after a key press (ImGui stops the hover until the mouse moves)
        if (keys && ImGui.IsKeyPressed(ImGuiKey.F, false)) { var (a, b) = CornerBounds(m, TargetCorners()); var sz = MathF.Max(b.X - a.X, b.Y - a.Y); if (sz > 1e-5f) { uvZoom = MathF.Min(size.X, size.Y) * 0.85f / sz; uvPan = size * 0.5f - (a + b) * 0.5f * uvZoom; } }

        // texture (diffuse of the first selected face's material, else the object's first)
        int matId = sel.Count > 0 ? m.Faces[sel.First()].MatId : m.Faces[0].MatId;
        var mat = Scene.Material(matId);
        var srv = !uvChecker && mat.DiffuseTexture != null ? renderer.Textures.Get(mat.DiffuseTexture) : null;
        var t0 = ToS(Vector2.Zero); var t1 = ToS(Vector2.One);
        if (srv != null) dl.AddImage(imgui.IdOf(srv), t0, t1);
        else
        {
            int n = 8; float cell = (t1.X - t0.X) / n;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                    dl.AddRectFilled(t0 + new Vector2(x, y) * cell, t0 + new Vector2(x + 1, y + 1) * cell, (x + y) % 2 == 0 ? 0xFF505050u : 0xFF3C3C3Cu);
        }
        dl.AddRect(t0, t1, 0xFFB0B0B0);
        // repeat tiles faintly (GTA textures tile)
        for (int k = -1; k <= 1; k++) for (int j = -1; j <= 1; j++) if (k != 0 || j != 0) dl.AddRect(ToS(new Vector2(k, j)), ToS(new Vector2(k + 1, j + 1)), 0x30FFFFFF);

        // faces, then the selection of the current mode, then what the mouse is over (pre-selection, like Blender)
        uint wire = 0xFFE0E0E0, selFill = 0x553030FF, selWire = 0xFF4040FF, hot = 0xFF30D0FF, selPt = 0xFF2090FF;
        bool faceModes = uvMode >= 2;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi]; bool s = faceModes && sel.Contains(fi);
            if (s)
                foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                    dl.AddTriangleFilled(ToS(f.UV[a]), ToS(f.UV[b]), ToS(f.UV[c]), selFill);
            for (int i = 0; i < f.V.Length; i++) dl.AddLine(ToS(f.UV[i]), ToS(f.UV[(i + 1) % f.V.Length]), s ? selWire : wire, 1f);
        }
        if (uvMode == 0) foreach (var k in uvCorn) { var f = m.Faces[(int)(k >> 8)]; var p = ToS(f.UV[(int)(k & 0xFF)]); dl.AddRectFilled(p - new Vector2(3.5f), p + new Vector2(3.5f), selPt); }
        if (uvMode == 1)
            foreach (var k in uvCorn)
            {
                int fi = (int)(k >> 8), ci = (int)(k & 0xFF); var f = m.Faces[fi]; int cn = (ci + 1) % f.V.Length;
                if (uvCorn.Contains(CK(fi, cn))) dl.AddLine(ToS(f.UV[ci]), ToS(f.UV[cn]), selPt, 3f);
            }
        if (hov && uvXfMode < 0 && !uvBoxing && !uvDragging)
        {
            uvHot = UvHit(m, io.MousePos, ToS);
            if (uvHot.Kind >= 0) DrawUvHot(dl, m, uvHot, ToS, hot);
        }
        else if (!uvDragging) uvHot = (-1, -1, -1);

        // ---- transform tools: gizmo on the selection, G / R / S like Blender, arrow keys nudge
        bool gizmoTook = false;
        if (selCorners.Count > 0)
        {
            var (smn, smx) = CornerBounds(m, selCorners); var sp = (smn + smx) * 0.5f; var ps = ToS(sp);
            float L = 70; var aU = ps + new Vector2(L, 0); var aV = ps + new Vector2(0, L);
            int hoverAxis = -1; var mp = io.MousePos;
            float DistSeg(Vector2 a, Vector2 b, Vector2 q) { var ab = b - a; float t = Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0, 1); return Vector2.Distance(q, a + ab * t); }
            if (uvXfMode < 0 && hov)
            {
                if (uvTool == 1) { float r = Vector2.Distance(mp, ps); if (MathF.Abs(r - L) < 7) hoverAxis = 3; }
                else if (Vector2.Distance(mp, ps) < 9) hoverAxis = 2;
                else if (DistSeg(ps, aU, mp) < 6) hoverAxis = 0;
                else if (DistSeg(ps, aV, mp) < 6) hoverAxis = 1;
            }
            uint cU = hoverAxis == 0 || uvXfAxis == 0 ? 0xFF40FFFFu : 0xFF4040FFu, cV = hoverAxis == 1 || uvXfAxis == 1 ? 0xFF40FFFFu : 0xFF40D040u;
            if (uvTool == 1) dl.AddCircle(ps, L, hoverAxis == 3 || uvXfMode == 1 ? 0xFF40FFFFu : 0xFFE0A040u, 64, 2.5f);
            else
            {
                dl.AddLine(ps, aU, cU, 2.5f); dl.AddLine(ps, aV, cV, 2.5f);
                if (uvTool == 0) { dl.AddTriangleFilled(aU + new Vector2(10, 0), aU + new Vector2(0, -5), aU + new Vector2(0, 5), cU); dl.AddTriangleFilled(aV + new Vector2(0, 10), aV + new Vector2(-5, 0), aV + new Vector2(5, 0), cV); }
                else { dl.AddRectFilled(aU - new Vector2(5), aU + new Vector2(5), cU); dl.AddRectFilled(aV - new Vector2(5), aV + new Vector2(5), cV); }
                dl.AddText(aU + new Vector2(12, -8), cU, "U"); dl.AddText(aV + new Vector2(8, 4), cV, "V");
            }
            dl.AddRect(ps - new Vector2(7), ps + new Vector2(7), hoverAxis == 2 ? 0xFF40FFFFu : 0xFFFFFFFFu, 0, ImDrawFlags.None, 2);

            void Begin(int mode, int axis, bool modal)
            {
                uvXfMode = mode; uvXfAxis = axis; uvXfModal = modal; uvXfMouse0 = mp; uvXfPivot = sp;
                uvXfCorners = selCorners.ToList();
                uvXfStart = uvXfCorners.Select(k => (int)(k >> 8)).Distinct().ToDictionary(fi => fi, fi => (Vector2[])m.Faces[fi].UV.Clone());
                Undo.Record(mode == 0 ? "UV move" : mode == 1 ? "UV rotate" : "UV scale", Scene);
            }
            if (uvXfMode < 0 && hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hoverAxis >= 0)
            { Begin(uvTool, hoverAxis == 3 ? -1 : hoverAxis == 2 ? -1 : hoverAxis, false); gizmoTook = true; }
            if (uvXfMode < 0 && keys)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.G, false)) Begin(0, -1, true);
                else if (ImGui.IsKeyPressed(ImGuiKey.R, false)) Begin(1, -1, true);
                else if (ImGui.IsKeyPressed(ImGuiKey.S, false)) Begin(2, -1, true);
                else if (ImGui.IsKeyPressed(ImGuiKey.W, false)) uvTool = 0;
                else if (ImGui.IsKeyPressed(ImGuiKey.E, false)) uvTool = 1;
                // arrow keys: one texel (Shift: ten)
                var nd = Vector2.Zero; float px = (io.KeyShift ? 10f : 1f) / uvTexSize;
                if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) nd.X -= px; if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) nd.X += px;
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) nd.Y -= px; if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) nd.Y += px;
                if (nd != Vector2.Zero) Op("UV nudge", () => CornerTransform(m, selCorners, Matrix3x2.CreateTranslation(nd)));
            }
            if (uvXfMode >= 0)
            {
                // modal axis lock like Blender: X / Y while moving or scaling
                if (uvXfModal && ImGui.IsKeyPressed(ImGuiKey.X, false)) uvXfAxis = uvXfAxis == 0 ? -1 : 0;
                if (uvXfModal && ImGui.IsKeyPressed(ImGuiKey.Y, false)) uvXfAxis = uvXfAxis == 1 ? -1 : 1;
                foreach (var kv in uvXfStart) if (kv.Key < m.Faces.Count) Array.Copy(kv.Value, m.Faces[kv.Key].UV, Math.Min(kv.Value.Length, m.Faces[kv.Key].UV.Length));
                var pivS = ToS(uvXfPivot); Matrix3x2 xf;
                if (uvXfMode == 0)
                {
                    var d = (mp - uvXfMouse0) / uvZoom;
                    if (uvXfAxis == 0) d.Y = 0; else if (uvXfAxis == 1) d.X = 0;
                    if (io.KeyCtrl) d = new Vector2(MathF.Round(d.X * uvTexSize / 8) * 8 / uvTexSize, MathF.Round(d.Y * uvTexSize / 8) * 8 / uvTexSize);
                    xf = Matrix3x2.CreateTranslation(d);
                    SetStatus($"UV move {d.X * uvTexSize:0} px, {d.Y * uvTexSize:0} px");
                }
                else if (uvXfMode == 1)
                {
                    var a0 = uvXfMouse0 - pivS; var a1 = mp - pivS;
                    float ang = MathF.Atan2(a1.Y, a1.X) - MathF.Atan2(a0.Y, a0.X);
                    if (io.KeyCtrl) ang = MathF.Round(ang / (MathF.PI / 12)) * (MathF.PI / 12);
                    xf = UvTools.Around(uvXfPivot, Matrix3x2.CreateRotation(ang));
                    SetStatus($"UV rotate {ang * 180 / MathF.PI:0.0}°");
                }
                else
                {
                    float f = Vector2.Distance(mp, pivS) / MathF.Max(4f, Vector2.Distance(uvXfMouse0, pivS));
                    if (!uvXfModal && uvXfAxis >= 0) { var dm = mp - uvXfMouse0; f = MathF.Max(0.01f, 1 + (uvXfAxis == 0 ? dm.X : dm.Y) / 70f); }
                    if (io.KeyCtrl) f = MathF.Max(0.1f, MathF.Round(f * 10) / 10);
                    var sc = uvXfAxis == 0 ? new Vector2(f, 1) : uvXfAxis == 1 ? new Vector2(1, f) : new Vector2(f);
                    xf = UvTools.Around(uvXfPivot, Matrix3x2.CreateScale(sc));
                    SetStatus($"UV scale {f * 100:0}%");
                }
                CornerTransform(m, uvXfCorners, xf); o.Touch(); Dirty = true;
                bool confirm = uvXfModal ? ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsKeyPressed(ImGuiKey.Enter) : !ImGui.IsMouseDown(ImGuiMouseButton.Left);
                bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape) || (uvXfModal && ImGui.IsMouseClicked(ImGuiMouseButton.Right));
                if (cancel)
                {
                    foreach (var kv in uvXfStart) if (kv.Key < m.Faces.Count) Array.Copy(kv.Value, m.Faces[kv.Key].UV, Math.Min(kv.Value.Length, m.Faces[kv.Key].UV.Length));
                    Undo.DropLast(); o.Touch(); SetStatus("UV transform cancelled");
                }
                if (confirm || cancel) { uvXfMode = -1; uvXfAxis = -1; uvXfStart = null; gizmoTook = true; }
                else gizmoTook = true;
            }
        }

        // ---- interaction: a click takes what is highlighted (nearest point / edge / face, not only inside a face),
        // Shift adds or removes, Ctrl removes, a drag on a selected thing moves it, a drag on empty space box-selects,
        // L picks the island under the mouse, A all, Alt+A nothing, 1 2 3 4 the selection mode
        if (keys && uvXfMode < 0)
        {
            for (int mi = 0; mi < 4; mi++) if (ImGui.IsKeyPressed(ImGuiKey._1 + mi, false)) SetUvMode(mi, m, sel);
            if (ImGui.IsKeyPressed(ImGuiKey.A, false))
            {
                if (io.KeyAlt) { sel.Clear(); uvCorn.Clear(); }
                else if (uvMode < 2) foreach (var k in AllCorners(m)) uvCorn.Add(k);
                else for (int fi = 0; fi < m.Faces.Count; fi++) sel.Add(fi);
            }
            if (ImGui.IsKeyPressed(ImGuiKey.L, false) && uvHot.F < 0) uvHot = UvHit(m, io.MousePos, ToS);
            if (ImGui.IsKeyPressed(ImGuiKey.L, false) && uvHot.F >= 0)
            {
                var isl = UvIslands(m).First(i => i.Contains(uvHot.F));
                if (uvMode < 2) foreach (var fi in isl) for (int c = 0; c < m.Faces[fi].V.Length; c++) { if (io.KeyShift) uvCorn.Remove(CK(fi, c)); else uvCorn.Add(CK(fi, c)); }
                else foreach (var fi in isl) { if (io.KeyShift) sel.Remove(fi); else sel.Add(fi); }
            }
        }
        if (!gizmoTook && hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var hit = UvHit(m, io.MousePos, ToS);
            uvLastMouse = io.MousePos; uvDragStart = io.MousePos;
            bool add = io.KeyShift, remove = io.KeyCtrl;
            if (hit.Kind >= 0)
            {
                var pick = PickOf(m, hit);
                bool already = uvMode < 2 ? pick.Corners.All(uvCorn.Contains) : pick.Faces.All(sel.Contains);
                if (!add && !remove && already) { uvDragging = true; Undo.Record("UV move", Scene); uvDragCorners = SelCorners(m, sel); }
                else
                {
                    if (!add && !remove) { sel.Clear(); uvCorn.Clear(); }
                    bool take = !remove && !(add && already);
                    if (uvMode < 2) foreach (var k in pick.Corners) { if (take) uvCorn.Add(k); else uvCorn.Remove(k); }
                    else foreach (var fi in pick.Faces) { if (take) sel.Add(fi); else sel.Remove(fi); }
                    uvDragging = !add && !remove;
                    if (uvDragging) { Undo.Record("UV move", Scene); uvDragCorners = SelCorners(m, sel); }
                }
            }
            else uvBoxing = true;
        }
        if (uvDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var d = (io.MousePos - uvLastMouse) / uvZoom;
                if (io.KeyCtrl) d = Vector2.Zero; // hold Ctrl to stop
                if (d != Vector2.Zero && uvDragCorners != null) { CornerTransform(m, uvDragCorners, Matrix3x2.CreateTranslation(d)); o.Touch(); Dirty = true; }
                uvLastMouse = io.MousePos;
            }
            else { uvDragging = false; uvDragCorners = null; }
        }
        if (uvBoxing)
        {
            dl.AddRectFilled(Vector2.Min(uvDragStart, io.MousePos), Vector2.Max(uvDragStart, io.MousePos), 0x18FFFFFF);
            dl.AddRect(Vector2.Min(uvDragStart, io.MousePos), Vector2.Max(uvDragStart, io.MousePos), 0xFFFFFFFF);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                uvBoxing = false;
                var a = ToUv(Vector2.Min(uvDragStart, io.MousePos)); var b = ToUv(Vector2.Max(uvDragStart, io.MousePos));
                if (!io.KeyShift && !io.KeyCtrl) { sel.Clear(); uvCorn.Clear(); }
                bool In(Vector2 p) => p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y;
                if ((b - a).Length() * uvZoom > 3)
                    for (int fi = 0; fi < m.Faces.Count; fi++)
                    {
                        var f = m.Faces[fi]; int n = f.V.Length;
                        if (uvMode == 0)
                        {
                            for (int c = 0; c < n; c++) if (In(f.UV[c])) { if (io.KeyCtrl) uvCorn.Remove(CK(fi, c)); else uvCorn.Add(CK(fi, c)); }
                        }
                        else if (uvMode == 1)
                        {
                            for (int c = 0; c < n; c++)
                                if (In(f.UV[c]) && In(f.UV[(c + 1) % n]))
                                    foreach (var k in new[] { CK(fi, c), CK(fi, (c + 1) % n) }) { if (io.KeyCtrl) uvCorn.Remove(k); else uvCorn.Add(k); }
                        }
                        else
                        {
                            var cc = Vector2.Zero; foreach (var uv in f.UV) cc += uv; cc /= n;
                            if (In(cc)) { if (io.KeyCtrl) sel.Remove(fi); else sel.Add(fi); }
                        }
                    }
                if (uvMode == 3)
                {
                    // island mode: a box that touches an island takes it whole
                    var whole = new HashSet<int>();
                    foreach (var isl in UvIslands(m)) if (isl.Any(sel.Contains)) foreach (var fi in isl) whole.Add(fi);
                    if (!io.KeyCtrl) sel.UnionWith(whole);
                }
            }
        }
        dl.PopClipRect();
        var mu = ToUv(io.MousePos);
        dl.AddText(c0 + new Vector2(8, size.Y - 20), 0xFFB0B0B0, hov ? $"u {mu.X:0.000}  v {mu.Y:0.000}    1-4 vertex/edge/face/island, click the highlight (Shift add, Ctrl remove), L island, A all, F frame, G / R / S, wheel zoom, right / middle pan" : uvMode < 2 ? $"{uvCorn.Count} UV corner(s) selected" : $"{sel.Count} face(s) selected");
        ImGui.End();
    }

    List<long> uvXfCorners, uvDragCorners;
    /// <summary>Top-left of the UV canvas on screen (tests aim at UV points with it).</summary>
    Vector2 uvC0, uvCanvasSize;
    /// <summary>The mouse is on the UV canvas (last frame's place): its keys (1-4, A, L, G R S...) are the UV editor's, not the 3D view's.</summary>
    bool UvCanvasUnderMouse { get { var mp = ImGui.GetIO().MousePos; return uvOpen && uvCanvasSize.X > 0 && mp.X >= uvC0.X && mp.Y >= uvC0.Y && mp.X < uvC0.X + uvCanvasSize.X && mp.Y < uvC0.Y + uvCanvasSize.Y; } }
    Vector2 UvToScreen(Vector2 uv) => uvC0 + uvPan + uv * uvZoom;
    (int Ver, PolyMesh M, int Hash) uvIslKey; List<List<int>> uvIsl;
    /// <summary>Islands of the current mesh, kept while the UVs don't change (hover would rebuild them every frame).</summary>
    List<List<int>> UvIslands(PolyMesh m)
    {
        int hash = m.Faces.Count; // UVs moved by drags do not bump the version: hash a sample of them
        for (int i = 0; i < m.Faces.Count; i += Math.Max(1, m.Faces.Count / 256)) hash = HashCode.Combine(hash, m.Faces[i].UV.Length > 0 ? m.Faces[i].UV[0] : default);
        var ver = Current?.Version ?? 0;
        if (uvIsl == null || uvIslKey.Ver != ver || !ReferenceEquals(uvIslKey.M, m) || uvIslKey.Hash != hash) { uvIsl = UvTools.Islands(m); uvIslKey = (ver, m, hash); }
        return uvIsl;
    }

    void SetUvMode(int mode, PolyMesh m, HashSet<int> sel)
    {
        if (mode == uvMode) return;
        // keep what was selected: faces -> their corners, corners -> the faces they close
        if (mode < 2 && uvMode >= 2) { uvCorn.Clear(); foreach (var fi in sel) if (fi < m.Faces.Count) for (int c = 0; c < m.Faces[fi].V.Length; c++) uvCorn.Add(CK(fi, c)); }
        else if (mode >= 2 && uvMode < 2)
        {
            sel.Clear();
            for (int fi = 0; fi < m.Faces.Count; fi++) { bool all = true; for (int c = 0; c < m.Faces[fi].V.Length && all; c++) all = uvCorn.Contains(CK(fi, c)); if (all && m.Faces[fi].V.Length > 0) sel.Add(fi); }
        }
        if (mode == 3) { var whole = new HashSet<int>(); foreach (var isl in UvIslands(m)) if (isl.Any(sel.Contains)) foreach (var fi in isl) whole.Add(fi); sel.UnionWith(whole); }
        uvMode = mode;
    }

    List<long> SelCorners(PolyMesh m, HashSet<int> sel)
    {
        if (uvMode < 2) return uvCorn.ToList();
        var r = new List<long>();
        foreach (var fi in sel) if (fi < m.Faces.Count) for (int c = 0; c < m.Faces[fi].V.Length; c++) r.Add(CK(fi, c));
        return r;
    }
    static List<long> AllCorners(PolyMesh m)
    {
        var r = new List<long>();
        for (int fi = 0; fi < m.Faces.Count; fi++) for (int c = 0; c < m.Faces[fi].V.Length; c++) r.Add(CK(fi, c));
        return r;
    }
    static void CornerTransform(PolyMesh m, IEnumerable<long> corners, Matrix3x2 t)
    {
        foreach (var k in corners)
        {
            int fi = (int)(k >> 8), c = (int)(k & 0xFF);
            if (fi < m.Faces.Count && c < m.Faces[fi].UV.Length) m.Faces[fi].UV[c] = Vector2.Transform(m.Faces[fi].UV[c], t);
        }
    }
    static (Vector2, Vector2) CornerBounds(PolyMesh m, IEnumerable<long> corners)
    {
        var mn = new Vector2(float.MaxValue); var mx = new Vector2(float.MinValue); bool any = false;
        foreach (var k in corners)
        {
            int fi = (int)(k >> 8), c = (int)(k & 0xFF);
            if (fi >= m.Faces.Count || c >= m.Faces[fi].UV.Length) continue;
            var p = m.Faces[fi].UV[c]; mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p); any = true;
        }
        return any ? (mn, mx) : (Vector2.Zero, Vector2.One);
    }

    List<int>[] UvVertexFaces(SceneObject o, PolyMesh m)
    {
        if (uvVf == null || uvVfKey.Obj != o.Id || uvVfKey.Ver != o.Version || !ReferenceEquals(uvVfKey.M, m)) { uvVf = m.BuildVertexFaces(); uvVfKey = (o.Id, o.Version, m); }
        return uvVf;
    }

    /// <summary>What the mouse is over, for the current mode: the nearest UV point (12 px) / edge (10 px), or the face under
    /// it (else the face of the nearest edge within 10 px). Small or stacked faces stay easy to take.</summary>
    (int Kind, int F, int C) UvHit(PolyMesh m, Vector2 mouse, Func<Vector2, Vector2> toS)
    {
        float best = uvMode == 0 ? 12 : 10; (int, int, int) r = (-1, -1, -1);
        float bestFace = 10; int nearFace = -1;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var uv = m.Faces[fi].UV; int n = uv.Length;
            for (int c = 0; c < n; c++)
            {
                var a = toS(uv[c]);
                if (uvMode == 0)
                {
                    float d = Vector2.Distance(a, mouse);
                    if (d < best) { best = d; r = (0, fi, c); }
                    continue;
                }
                var b = toS(uv[(c + 1) % n]);
                var ab = b - a; float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(mouse - a, ab) / ab.LengthSquared(), 0, 1);
                float de = Vector2.Distance(mouse, a + ab * t);
                if (uvMode == 1 && de < best) { best = de; r = (1, fi, c); }
                if (uvMode >= 2 && de < bestFace) { bestFace = de; nearFace = fi; }
            }
        }
        if (uvMode >= 2)
        {
            // inside a face (the smallest one when faces are stacked), else the face of the nearest edge
            var p = (mouse - toS(Vector2.Zero)) / MathF.Max(1e-6f, (toS(Vector2.One) - toS(Vector2.Zero)).X);
            int inside = FaceAtUvSmallest(m, p);
            int f = inside >= 0 ? inside : nearFace;
            return f >= 0 ? (2, f, -1) : (-1, -1, -1);
        }
        return r;
    }

    /// <summary>The corners / faces a hit takes. A UV point takes every corner of that vertex at the same UV place (the
    /// points that look like one, Blender's "shared location"); an edge takes both its ends in every face sharing it.</summary>
    (List<long> Corners, List<int> Faces) PickOf(PolyMesh m, (int Kind, int F, int C) hit)
    {
        var o = Current; var corners = new List<long>(); var faces = new List<int>();
        if (hit.Kind == 2)
        {
            if (uvMode == 3) faces.AddRange(UvIslands(m).First(i => i.Contains(hit.F))); else faces.Add(hit.F);
            return (corners, faces);
        }
        var vf = UvVertexFaces(o, m);
        void Shared(int fi, int c)
        {
            var f = m.Faces[fi]; int v = f.V[c]; var uv = f.UV[c];
            if (v >= vf.Length) { corners.Add(CK(fi, c)); return; }
            foreach (var f2 in vf[v])
            {
                var g = m.Faces[f2];
                for (int c2 = 0; c2 < g.V.Length; c2++) if (g.V[c2] == v && Vector2.DistanceSquared(g.UV[c2], uv) < 1e-10f) corners.Add(CK(f2, c2));
            }
        }
        Shared(hit.F, hit.C);
        if (hit.Kind == 1) Shared(hit.F, (hit.C + 1) % m.Faces[hit.F].V.Length);
        return (corners.Distinct().ToList(), faces);
    }

    void DrawUvHot(ImDrawListPtr dl, PolyMesh m, (int Kind, int F, int C) h, Func<Vector2, Vector2> toS, uint col)
    {
        var f = m.Faces[h.F]; int n = f.V.Length;
        if (h.Kind == 0) { var p = toS(f.UV[h.C]); dl.AddCircle(p, 7, col, 16, 2f); dl.AddCircleFilled(p, 3, col); }
        else if (h.Kind == 1) dl.AddLine(toS(f.UV[h.C]), toS(f.UV[(h.C + 1) % n]), col, 3.5f);
        else
        {
            var faces = uvMode == 3 ? UvIslands(m).First(i => i.Contains(h.F)) : new List<int> { h.F };
            foreach (var fi in faces)
            {
                var g = m.Faces[fi];
                for (int i = 0; i < g.V.Length; i++) dl.AddLine(toS(g.UV[i]), toS(g.UV[(i + 1) % g.V.Length]), col, 2f);
            }
        }
    }

    /// <summary>The smallest face containing the UV point (stacked / overlapping UVs: the small one on top is the one you aim at).</summary>
    static int FaceAtUvSmallest(PolyMesh m, Vector2 p)
    {
        int best = -1; float bestArea = float.MaxValue;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var uv = m.Faces[fi].UV; bool inside = false;
            for (int i = 0, j = uv.Length - 1; i < uv.Length; j = i++)
                if ((uv[i].Y > p.Y) != (uv[j].Y > p.Y) && p.X < (uv[j].X - uv[i].X) * (p.Y - uv[i].Y) / (uv[j].Y - uv[i].Y + 1e-12f) + uv[i].X) inside = !inside;
            if (!inside) continue;
            float area = 0; for (int i = 0, j = uv.Length - 1; i < uv.Length; j = i++) area += uv[j].X * uv[i].Y - uv[i].X * uv[j].Y;
            area = MathF.Abs(area);
            if (area < bestArea) { bestArea = area; best = fi; }
        }
        return best;
    }

    static int FaceAtUv(PolyMesh m, Vector2 p)
    {
        for (int fi = m.Faces.Count - 1; fi >= 0; fi--)
        {
            var uv = m.Faces[fi].UV; bool inside = false;
            for (int i = 0, j = uv.Length - 1; i < uv.Length; j = i++)
                if ((uv[i].Y > p.Y) != (uv[j].Y > p.Y) && p.X < (uv[j].X - uv[i].X) * (p.Y - uv[i].Y) / (uv[j].Y - uv[i].Y + 1e-12f) + uv[i].X) inside = !inside;
            if (inside) return fi;
        }
        return -1;
    }
}
