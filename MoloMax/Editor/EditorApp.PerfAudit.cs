using System.Diagnostics;
using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>
/// "PERF FULL": measures the processor time per frame and per action on heavy scenes (one 490k-polygon mesh like a
/// GTA room, 3000 objects), so optimisations are judged on numbers. Each line of the report: median / 95% of the
/// frame time without the wait for the screen, the slowest sections, and the time of single actions.
/// </summary>
public sealed partial class EditorApp
{
    readonly List<string> perfLines = new();

    IEnumerable<object> PerfMeasure(string label, int frames, Action<int> perFrame = null)
    {
        var ms = new List<double>(); ResetWorstFrame();
        foreach (var k in FrameProfile.Keys.ToList()) FrameProfile[k] = 0;
        for (int i = 0; i < frames; i++)
        {
            perFrame?.Invoke(i);
            yield return Frames(1);
            if (i >= 3) ms.Add(LastFrameCpuMs);
        }
        ms.Sort();
        double med = ms.Count > 0 ? ms[ms.Count / 2] : 0, p95 = ms.Count > 0 ? ms[(int)(ms.Count * 0.95)] : 0;
        string line = $"PERF {label}: median {med:0.0} ms, 95% {p95:0.0} ms | {ProfileText(5)} | {WorstFrameText()}";
        perfLines.Add(line); Note(line);
    }

    void Lv(SubObject l) { if (Level != l) SetLevel(l); } // SetLevel on the current level goes back to object mode

    double PerfTime(string label, Action a)
    {
        var sw = Stopwatch.StartNew();
        try { a(); } catch (Exception e) { Note($"PERF {label}: threw {e.Message}"); }
        double t = sw.Elapsed.TotalMilliseconds;
        string line = $"PERF {label}: {t:0} ms"; perfLines.Add(line); Note(line);
        return t;
    }

    IEnumerable<object> PerfFull()
    {
        perfLines.Clear();
        Fresh(); if (MapMode) SetMapMode(false); paintMode = false; SculptMode = false; propTab = PropTab.Object;
        UI.ImGuiBackend.SyntheticInput = true;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        Maximized = true;

        // ---- 1. one heavy mesh (a big GTA room): 490k polygons
        SceneObject big = null;
        PerfTime("create a 490k-polygon mesh", () => Do("perf big", () => { big = Scene.Add("big", Primitives.Plane(60, 60, 700, 700), Vector3.Zero); }));
        FrameAll(); yield return Frames(15);
        var center = ScreenOf(vp, Vector3.Zero);
        foreach (var x in PerfMeasure("object mode, 490k polys, camera turning", 90, i => cam.Orbit(3, 0))) yield return x;
        SelectOnly(big);
        foreach (var x in PerfMeasure("object mode, 490k polys selected", 60, i => cam.Orbit(3, 0))) yield return x;
        Lv(SubObject.Vertex); yield return Frames(5);
        foreach (var x in PerfMeasure("vertex mode, nothing selected, mouse moving on the mesh", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        SubSel(big).Verts.UnionWith(Enumerable.Range(0, 50000));
        foreach (var x in PerfMeasure("vertex mode, 50k vertices selected, mouse moving", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        Lv(SubObject.Edge); yield return Frames(5);
        foreach (var x in PerfMeasure("edge mode, mouse moving", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        Lv(SubObject.Polygon); SubSel(big).Faces = new HashSet<int>(Enumerable.Range(0, 50000)); yield return Frames(5);
        foreach (var x in PerfMeasure("polygon mode, 50k faces selected, mouse moving", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        propTab = PropTab.Material; ShowPanel = true; yield return Frames(3);
        foreach (var x in PerfMeasure("polygon mode, Material tab open", 40, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, 0), 0))) yield return x;
        materialEditorOpen = true; yield return Frames(3);
        foreach (var x in PerfMeasure("polygon mode, Material Editor open", 40, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, 0), 0))) yield return x;
        materialEditorOpen = false; propTab = PropTab.Object;

        // moving 50k faces with the gizmo
        Tool = Tool.Move; yield return Frames(3);
        var g = GizmoGrip(vp, GizmoHandle.X);
        foreach (var x in MouseTo(g, 6)) yield return x;
        PostMouse(0x201, g, 1); yield return Frames(2);
        foreach (var x in PerfMeasure("dragging 50k selected faces", 40, i => PostMouse(0x200, g + new Vector2(i * 2, 0), 1))) yield return x;
        PostMouse(0x202, g + new Vector2(80, 0), 0); yield return Frames(4);
        Note($"drag moved the faces: {(Undo.CanUndo ? "yes" : "no")}");

        // interactive extrude (E) on the 50k faces: the height follows the mouse
        Lv(SubObject.Polygon); SubSel(big).Faces = new HashSet<int>(Enumerable.Range(0, 50000)); yield return Frames(3);
        PostMouse(0x200, center, 0); yield return Frames(2);
        BeginModal(ModalKind.Extrude); yield return Frames(2);
        Note($"extrude modal on: {modal.Kind}, faces {big.Mesh.Faces.Count:N0}");
        foreach (var x in PerfMeasure("extrude (E) on 50k faces, mouse moving", 40, i => PostMouse(0x200, center + new Vector2(0, -i * 2), 0))) yield return x;
        Note($"extrude height reached {modal.LastValue:0.000} m, faces {big.Mesh.Faces.Count:N0}");
        yield return Shot("perf_extrude_modal");
        ConfirmModal(); yield return Frames(5);
        DoUndo(); yield return Frames(5);

        // single actions
        PerfTime("click select a face (polygon mode)", () => ClickSelect(vp, center));
        Lv(SubObject.Element);
        PerfTime("click select an element (490k faces)", () => ClickSelect(vp, center));
        Lv(SubObject.Polygon); SubSel(big).Faces = new HashSet<int>(Enumerable.Range(0, 50000));
        PerfTime("select all faces", () => SelectAll());
        SubSel(big).Faces = new HashSet<int>(Enumerable.Range(0, 50000));
        PerfTime("extrude 50k faces", () => ExtrudeFaces(0.2f, false));
        yield return Frames(5);
        PerfTime("undo the extrude", () => DoUndo());
        yield return Frames(5);
        PerfTime("redo", () => DoRedo());
        yield return Frames(5);
        var tmp = Path.Combine(SelfTestDir, "perf_big.mmx");
        PerfTime("save the scene (490k polys)", () => SceneIO.Save(Scene, tmp));
        PerfTime("open the scene again", () => SceneIO.Load(tmp));
        Lv(SubObject.Object);

        // ---- 2. many objects (a map / MLO full of props)
        Fresh(); Maximized = true;
        PerfTime("create 3000 objects", () => Do("perf many", () =>
        {
            for (int i = 0; i < 3000; i++) Scene.Add("prop" + i, Primitives.Box(0.5f, 0.5f, 0.5f, 2, 2, 2), new Vector3(i % 60 * 1.2f, i / 60 * 1.2f, 0));
        }));
        FrameAll(); yield return Frames(15);
        foreach (var x in PerfMeasure("3000 objects, camera turning", 90, i => cam.Orbit(3, 0))) yield return x;
        PerfTime("select all 3000", () => SelectAll());
        yield return Frames(3);
        foreach (var x in PerfMeasure("3000 objects all selected", 60, i => cam.Orbit(3, 0))) yield return x;
        var mid = ScreenOf(vp, new Vector3(36, 30, 0));
        PerfTime("click select one of 3000", () => ClickSelect(vp, mid));
        Tool = Tool.Move; SelectAll(); yield return Frames(3);
        var g2 = GizmoGrip(vp, GizmoHandle.X);
        foreach (var x in MouseTo(g2, 6)) yield return x;
        PostMouse(0x201, g2, 1); yield return Frames(2);
        foreach (var x in PerfMeasure("dragging 3000 objects", 40, i => PostMouse(0x200, g2 + new Vector2(i * 2, 0), 1))) yield return x;
        PostMouse(0x202, g2 + new Vector2(80, 0), 0); yield return Frames(4);
        PerfTime("undo the move of 3000", () => DoUndo());
        yield return Frames(3);

        UI.ImGuiBackend.SyntheticInput = false;
        File.WriteAllLines(Path.Combine(SelfTestDir, "perf.txt"), perfLines);
        Expect(perfLines.Count > 10, $"{perfLines.Count} measures written to perf.txt");
    }

    /// <summary>"PERF USER": the same measures on a real scene (POLYMORPH_PERF_FILE, a copy of the user's file): the active
    /// object (or the heaviest visible one) in edit mode, with its modifiers.</summary>
    IEnumerable<object> PerfUser()
    {
        perfLines.Clear();
        var file = Environment.GetEnvironmentVariable("POLYMORPH_PERF_FILE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Note("POLYMORPH_PERF_FILE not set: skipped"); yield break; }
        UI.ImGuiBackend.SyntheticInput = true;
        PerfTime("open the scene", () => OpenFile(file));
        yield return Frames(20);
        var o = Current is { Mesh: not null, Hidden: false } c ? c
            : Scene.Objects.Where(x => !x.Hidden && x.Mesh != null).OrderByDescending(x => x.Mesh.Faces.Count).FirstOrDefault();
        if (o == null) { Note("no visible mesh"); yield break; }
        Note($"object {o.Name}: {o.Mesh.Verts.Count:N0} verts, {o.Mesh.Faces.Count:N0} polys, modifiers: {string.Join(", ", o.Modifiers?.Select(m => m.TypeName) ?? Array.Empty<string>())}");
        Note($"scene: {Scene.Objects.Count} objects, {Scene.Objects.Count(x => !x.Hidden)} visible, {Scene.Objects.Sum(x => (long)(x.Mesh?.Faces.Count ?? 0)):N0} polys in all");
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
        if (o.Modifiers is { Count: > 0 })
            PerfTime("modifiers evaluated once", () => global::MoloMax.Core.Modifiers.ModifierStack.Evaluate(o.Mesh, o.Modifiers, false, false));
        SelectOnly(o); FrameSelection(); yield return Frames(10);
        var center = new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2);
        foreach (var x in PerfMeasure("object mode, camera turning", 60, i => cam.Orbit(2, 0))) yield return x;
        Lv(SubObject.Polygon); yield return Frames(5);
        foreach (var x in PerfMeasure("polygon mode, idle", 40)) yield return x;
        foreach (var x in PerfMeasure("polygon mode, mouse moving", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        foreach (var x in PerfMeasure("polygon mode, camera turning", 60, i => cam.Orbit(2, 0))) yield return x;
        PerfTime("click select a face", () => ClickSelect(vp, center));
        yield return Frames(5);
        Note($"faces selected: {SubSel(o).Faces.Count}");
        foreach (var x in PerfMeasure("polygon mode, 1 face selected, mouse moving", 40, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, 0), 0))) yield return x;
        // moving the selected face with the gizmo (each drag step re-evaluates the modifiers)
        Tool = Tool.Move; yield return Frames(3);
        if (SubSel(o).Faces.Count > 0)
        {
            var g = GizmoGrip(vp, GizmoHandle.Z);
            foreach (var x in MouseTo(g, 6)) yield return x;
            PostMouse(0x201, g, 1); yield return Frames(2);
            foreach (var x in PerfMeasure("dragging one face", 30, i => PostMouse(0x200, g + new Vector2(0, -i), 1))) yield return x;
            PostMouse(0x202, g + new Vector2(0, -30), 0); yield return Frames(4);
            PerfTime("undo the move", () => DoUndo());
            yield return Frames(5);
        }
        Lv(SubObject.Edge); yield return Frames(5);
        foreach (var x in PerfMeasure("edge mode, mouse moving", 60, i => PostMouse(0x200, center + new Vector2(i % 20 * 3, i % 7 * 3), 0))) yield return x;
        int ok = 0;
        double worst = 0;
        for (int i = 0; i < 10; i++)
        {
            var at = center + new Vector2(-150 + i * 33, (i % 3 - 1) * 60);
            var t = PerfTime($"click select an edge #{i}", () => ClickSelect(vp, at));
            worst = Math.Max(worst, t);
            if (SubSel(o).Edges.Count > 0) ok++;
            yield return Frames(2);
        }
        Note($"edge clicks that took an edge: {ok}/10, slowest {worst:0} ms");
        yield return Shot("perf_user_edges");
        Lv(SubObject.Object);
        UI.ImGuiBackend.SyntheticInput = false;
        File.WriteAllLines(Path.Combine(SelfTestDir, "perf_user.txt"), perfLines);
        Expect(perfLines.Count > 5, $"{perfLines.Count} measures written to perf_user.txt");
    }
}
