using System.Diagnostics;
using System.Numerics;
using System.Text;
using MoloMax.Core.Gta.Check;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.Core.Paint;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>
/// `MoloMax.exe --selftest &lt;folder&gt; [gta folder] [extra .ydr] [.ytyp]`: drives the real editor frame by frame
/// (create, mesh tools, booleans, UVs, GIMS hierarchy, lights, LODs, GTA check, room builder, export, send to server,
/// save / open, recovery, imports, game files, 1M-vertex timing), takes screenshots of the real window and writes
/// report.txt, then quits. Settings and autosave live in the test folder (the user's own are not touched).
/// </summary>
public sealed partial class EditorApp
{
    public static string SelfTestDir;
    public static string[] SelfTestArgs = Array.Empty<string>();
    public static volatile int SelfTestHeartbeat;

    sealed class StTest { public string Name; public Func<IEnumerable<object>> Body; public List<string> Lines = new(); public bool Failed, Done; public long Ms; }
    List<StTest> stTests;
    int stIndex = -1;
    IEnumerator<object> stCur;
    Func<bool> stWait; double stWaitUntil; int stFrames;
    string stShot;
    int stShotN;
    readonly Stopwatch stClock = new();
    readonly List<float> stDts = new();
    StTest St => stTests[stIndex];

    void Expect(bool ok, string what) { St.Lines.Add((ok ? "  ok   " : "  FAIL ") + what); if (!ok) St.Failed = true; }
    void Note(string s) => St.Lines.Add("       " + s);
    static object Frames(int n) => n;
    static object Shot(string name) => "shot:" + name;
    static object Until(Func<bool> c, double seconds = 180) => (c, seconds);
    /// <summary>Like Until, but giving up is not a failure (films: wait for the streaming to settle, at most so long).</summary>
    static object Settle(Func<bool> c, double seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        return ((Func<bool>)(() => c() || DateTime.Now > end), seconds + 30);
    }
    string StPath(params string[] p) => Path.Combine(new[] { SelfTestDir }.Concat(p).ToArray());

    /// <summary>Per frame (inside the UI frame): advances the script.</summary>
    void SelfTestTick(float dt)
    {
        if (SelfTestDir == null) return;
        SelfTestHeartbeat++;
        stDts.Add(dt);
        if (stTests == null) { stTests = SelfTests(); stFrames = 20; }  // let the window settle
        if (stShot != null) return;                                       // captured after this frame renders
        if (stFrames > 0) { stFrames--; return; }
        if (stWait != null)
        {
            bool done; try { done = stWait(); } catch (Exception e) { done = true; Expect(false, "wait threw " + e.Message); }
            if (!done && ImGui_Time() < stWaitUntil) return;
            if (!done) { Expect(false, "timed out"); EndTest(); }
            stWait = null;
            if (stCur == null) return;
        }
        while (true)
        {
            if (stCur == null)
            {
                if (++stIndex >= stTests.Count) { FinishSelfTest(); return; }
                St.Lines.Clear(); stClock.Restart();
                try { stCur = St.Body().GetEnumerator(); } catch (Exception e) { Expect(false, "start: " + e.Message); EndTest(); continue; }
            }
            bool more;
            try { more = stCur.MoveNext(); }
            catch (Exception e) { Expect(false, $"exception: {e.GetType().Name}: {e.Message}"); Note(e.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? ""); EndTest(); continue; }
            if (!more) { EndTest(); continue; }
            switch (stCur.Current)
            {
                case int n: stFrames = n; return;
                case string s when s.StartsWith("shot:"): stShot = s[5..]; return;
                case ValueTuple<Func<bool>, double> w: stWait = w.Item1; stWaitUntil = ImGui_Time() + w.Item2; return;
                default: stFrames = 1; return;
            }
        }
    }

    static double ImGui_Time() => ImGuiNET.ImGui.GetTime();

    void EndTest()
    {
        St.Done = true; St.Ms = stClock.ElapsedMilliseconds; stCur = null;
        WriteSelfTestReport(false);
    }

    /// <summary>After the frame is rendered, before Present: the screenshot.</summary>
    void SelfTestAfterRender()
    {
        if (SelfTestDir == null) return;
        RecordFrame();
        if (stShot == null) return;
        var name = $"{++stShotN:00}_{stShot}.png";
        try { gpu.Capture(StPath("shots", name)); Note("screenshot " + name); }
        catch (Exception e) { Expect(false, "screenshot failed: " + e.Message); }
        stShot = null; stFrames = 1;
    }

    void WriteSelfTestReport(bool final)
    {
        var sb = new StringBuilder();
        int pass = stTests.Count(t => t.Done && !t.Failed), fail = stTests.Count(t => t.Done && t.Failed);
        sb.AppendLine($"Polymorph self test  {DateTime.Now:yyyy-MM-dd HH:mm}  GPU {gpu.Width}x{gpu.Height} MSAA {gpu.SampleCount}");
        foreach (var t in stTests.Where(t => t.Done || t == stTests.ElementAtOrDefault(stIndex)))
        {
            sb.AppendLine($"{(t.Done ? (t.Failed ? "FAIL" : "PASS") : "....")} {t.Name}  ({t.Ms} ms)");
            foreach (var l in t.Lines) sb.AppendLine(l);
        }
        sb.AppendLine(final ? $"\n{pass} passed, {fail} failed" : $"\n(running) {pass} passed, {fail} failed");
        try { File.WriteAllText(StPath("report.txt"), sb.ToString()); } catch { }
    }

    void FinishSelfTest()
    {
        WriteSelfTestReport(true);
        File.WriteAllText(StPath("done.txt"), "done");
        SelfTestDir = null;
        Dirty = false;
        form.BeginInvoke(new Action(() => form.Close()));
    }

    // ---------------------------------------------------------------- the tests

    SceneObject Obj(string name) => Scene.Objects.LastOrDefault(o => o.Name == name);
    void SelectOnly(params SceneObject[] objs) { Selected.Clear(); foreach (var o in objs) Selected.Add(o.Id); ActiveId = objs.Last().Id; SelectionChanged(); }
    SceneObject Prim(CreateKind k, Vector3 at) { Cursor3D = at; AddPrimitive(k); adjustOpen = false; var o = Scene.Find(lastAdd?.Item2 ?? -1) ?? Scene.Objects.Last(); return o; }
    void Fresh()
    {
        if (Current is { ModEdit: not null } me) EndModifierEdit(me);
        if (MapMode) SetMapMode(false); // a MAP test before leaves MODEL mode to the next one
        if (Settings.Mode != UiMode.Blender) SetMode(UiMode.Blender); // a Classic test before leaves Standard to the next one
        AnimPlaying = false; AutoKey = false; keySel.Clear(); animFrame = 0; ycdWindow = false; ShowPanel = true;
        Level = SubObject.Object; Dirty = false; NewScene(); Cursor3D = Vector3.Zero; importReportOpen = false;
    }

    List<StTest> SelfTests()
    {
        Directory.CreateDirectory(StPath("shots"));
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        var extraYdr = SelfTestArgs.ElementAtOrDefault(1);
        var ytyp = SelfTestArgs.ElementAtOrDefault(2);
        var list = new List<StTest>();
        void T(string name, Func<IEnumerable<object>> body) => list.Add(new StTest { Name = name, Body = body });
        if (gtaFolder == "--render" && extraYdr != null) { T("render " + Path.GetFileName(extraYdr), () => Render(extraYdr)); return list; }

        T("startup + crash recovery prompt", Recovery);
        T("primitives + viewport", PrimitivesTest);
        T("chamfer (Ctrl+B)", Chamfer);
        T("bridge", BridgeTest);
        T("boolean subtract + undo / redo", BooleanTest);
        T("knife / slice", Knife);
        T("smart UV + UV editor", Uv);
        T("selection: edit-mode picking keeps working with X-Ray", XRayTest);
        T("vertex selection goes through the mesh by default, Visible only limits it", SelectThroughTest);
        T("SMALL BOX: a small box inside a big object or face still selects it", SmallBoxTest);
        T("GIMS: create Drawable, collision, light, LODs", Gims);
        T("instances: Alt+D / Clone Options, shared edits, Drawable hierarchy, make unique, undo", InstancesTest);
        T("Edit Poly + Unwrap UVW modifiers inside the stack", ModifierEditTest);
        T("scripting API (HTTP) + Python package + MCP server", ApiTest);
        T("sculpt mode: brushes clean a noisy generated mesh", SculptTest);
        T("real mouse + keyboard: click select, Tab into Edit Poly, sculpt stroke, Shift smooth, undo", RealInputTest);
        T("snaps menu, Shift clone, isolate, eye on selection, walk camera", SnapsCloneIsolateTest);
        T("edit mode Shift + move copies faces / pulls edges, view cube drag", EditShiftCloneTest);
        T("Light tab: scene lights, temperature, sun shadows, face orientation, material click, hub", LightsNormalsTest);
        T("GTA check + Fix all", Checker);
        T("MLO room builder", RoomBuilderTest);
        T("room builder: plan picture, size references, materials, skirting + dirt", PlanPhotoTest);
        T("room builder: live 3D while moving / resizing, one undo", PlanLiveTest);
        T("extrude arrow + Adjust Last Operation, cube projection with material", ExtrudeCubeProjTest);
        T("FiveM export (F10)", ExportTest);
        T("IN-GAME content: textured props + MLO resources for the test server", InGameContentTest);
        T("send to my FiveM server + bridge", SendTest);
        T("GTA scale: ped button + Auto scale from the name", ScaleTest);
        T("save / open / .bak / recent", SaveOpen);
        T("glb export + re-import", GlbRoundTrip);
        if (extraYdr != null && File.Exists(extraYdr)) T("background import of a real game .ydr", () => ImportReal(extraYdr));
        if (ytyp != null && File.Exists(ytyp)) T("MLO import from a real .ytyp", () => MloReal(ytyp));
        if (gtaFolder != null && Directory.Exists(gtaFolder))
        {
            T("game files browser (real install)", () => GameBrowserTest(gtaFolder));
            T("MAP mode: stream Legion Square, place a bench, edit + save .ymap", () => MapModeTest(gtaFolder));
            T("LIBRARY BUILD: texture library from my own GTA V (every interior)", () => LibraryBuildTest(gtaFolder));
            T("MAP modeling: build on the map, MLO rooms, edit a game model in place", () => MapBuildTest(gtaFolder));
            T("FORGE: weapon and vehicle from a model, game donors", () => ForgeTest(gtaFolder));
            if (ytyp != null && File.Exists(ytyp)) T("MLO import with its models taken from the game", () => MloReal(ytyp));
        }
        T("Molo Paint: two materials = two texture sets, tools tab", PaintStart);
        T("Molo Paint: symmetry stroke, tip, fill, clone, mask painting", PaintStrokes);
        T("Molo Paint: folders, smart masks, filters, my smart material", PaintLayersTest);
        T("Molo Paint: starter alphas, relief, eyedropper, radial / scatter, new masks and filters", PaintPackTest);
        T("Molo Paint: high-poly bake (normal, AO, colour)", PaintBakeTest);
        T("Molo Paint: vertex colours, emissive, export (DDS + Sollumz), save / reopen", PaintExportTest);
        T("performance: 1M vertices", Perf);
        T("preferences + hub windows", Windows);
        UserFlowTests(T);
        Tutorials(T);
        var only = Environment.GetEnvironmentVariable("POLYMORPH_ST_ONLY");
        // the tutorial videos are recorded only when asked for ("TUTO" in the filter)
        if (string.IsNullOrEmpty(only) || !only.Contains("TUTO", StringComparison.OrdinalIgnoreCase)) list.RemoveAll(t => t.Name.StartsWith("TUTO"));
        if (!string.IsNullOrEmpty(only))
        {
            list = list.Where(t => only.Split('|', ';').Any(o => t.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
            list.Insert(0, new StTest { Name = "close the recovery prompt", Body = () => SkipRecovery() });
        }
        return list;
    }

    IEnumerable<object> Recovery()
    {
        // the runner left a fake "did not close properly" session with an autosave: the prompt must show
        Expect(recoveryOpen, "recovery prompt shown at startup");
        yield return Shot("recovery_prompt");
        if (recoveryOpen)
        {
            OpenFile(recovery.Autosave); CurrentFile = recovery.Original; Dirty = true; recoveryOpen = false;
            Expect(Scene.Objects.Count > 0, $"autosave restored ({Scene.Objects.Count} objects)");
        }
        yield return Frames(5);
    }

    IEnumerable<object> PrimitivesTest()
    {
        Fresh();
        var b = Prim(CreateKind.Box, new Vector3(0, 0, 0));
        var s = Prim(CreateKind.Sphere, new Vector3(4, 0, 0));
        var c = Prim(CreateKind.Cylinder, new Vector3(-4, 0, 0));
        Prim(CreateKind.Room, new Vector3(0, 8, 0));
        Expect(Scene.Objects.Count == 4, $"4 objects ({Scene.Objects.Count})");
        Expect(b.Mesh.IsClosedManifold(out _) && s.Mesh.IsClosedManifold(out _) && c.Mesh.IsClosedManifold(out _), "box / sphere / cylinder closed");
        FrameAll();
        yield return Frames(3);
        yield return Shot("primitives");
        Maximized = true; ActiveViewport = 3; FrameAll(Viewports[3]);
        yield return Frames(3);
        yield return Shot("perspective_max");
    }

    IEnumerable<object> Chamfer()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(b); SetLevel(SubObject.Edge);
        var edges = b.Mesh.AllEdges().ToList();
        foreach (var e in edges) SubSel(b).Edges.Add(e);
        yield return Frames(2);
        yield return Shot("edges_selected");
        int f0 = b.Mesh.Faces.Count;
        DoMesh("Chamfer", (o, m, s) => { MeshOps.ChamferEdges(m, s.Edges.ToList(), 0.1f); s.Edges.Clear(); });
        Expect(b.Mesh.Faces.Count == 26, $"chamfer all edges of a box: {f0} -> {b.Mesh.Faces.Count} faces (26 expected)");
        Expect(b.Mesh.IsClosedManifold(out var why), "still closed " + why);
        FrameAll();
        yield return Frames(2);
        yield return Shot("chamfer");
        // the modal tool itself (Ctrl+B): starts and cancels without breaking the mesh
        var e1 = b.Mesh.AllEdges().First(); SubSel(b).Edges.Add(e1);
        BeginModal(ModalKind.Chamfer);
        bool started = ModalActive; string st0 = status;
        yield return Frames(3);
        Expect(ModalActive, $"Ctrl+B modal started (right after: {started}, level {Level}, edges {SubSel(b).Edges.Count}, status '{st0}' / '{status}')");
        CancelModal();
        Expect(!ModalActive && b.Mesh.Faces.Count == 26, "modal cancelled, mesh unchanged");
        SetLevel(SubObject.Object);
    }

    IEnumerable<object> BridgeTest()
    {
        Fresh();
        var two = Primitives_Two();
        var o = AddObject("two_boxes", two, Vector3.Zero);
        SelectOnly(o); SetLevel(SubObject.Polygon);
        int a = Enumerable.Range(0, two.Faces.Count).First(i => MathF.Abs(two.FaceCenter(i).Z - 1) < 1e-3f && two.FaceNormal(i).Z > 0.9f);
        int b = Enumerable.Range(0, two.Faces.Count).First(i => MathF.Abs(two.FaceCenter(i).Z - 3) < 1e-3f && two.FaceNormal(i).Z < -0.9f);
        SubSel(o).Faces.Add(a); SubSel(o).Faces.Add(b);
        yield return Frames(2);
        yield return Shot("bridge_before");
        Bridge();
        Expect(o.Mesh.IsClosedManifold(out var why) && o.Mesh.Faces.Count == 14, $"bridged into one closed piece: {o.Mesh.Faces.Count} faces {why}");
        FrameAll();
        yield return Frames(2);
        yield return Shot("bridge_after");
        SetLevel(SubObject.Object);
    }

    static PolyMesh Primitives_Two()
    {
        var two = Core.Mesh.Primitives.Box(1, 1, 1); var other = Core.Mesh.Primitives.Box(1, 1, 1);
        other.Transform(Matrix4x4.CreateTranslation(0, 0, 3)); two.Append(other);
        return two;
    }

    IEnumerable<object> BooleanTest()
    {
        Fresh();
        var cutter = Prim(CreateKind.Sphere, new Vector3(1, 1, 1.4f));
        var box = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(cutter, box); // box active (last)
        int f0 = box.Mesh.Faces.Count; long v0 = box.Version;
        Boolean(Csg.Op.Subtract);
        var after = Scene.Find(box.Id);
        Expect(after.Mesh.Faces.Count > f0 && Scene.Find(cutter.Id).Hidden, $"subtract: {f0} -> {after.Mesh.Faces.Count} faces, cutter hidden");
        Expect(after.Mesh.IsClosedManifold(out var why), "result closed " + why);
        FrameAll();
        yield return Frames(2);
        yield return Shot("boolean_subtract");
        DoUndo();
        Expect(Scene.Find(box.Id).Mesh.Faces.Count == f0 && !Scene.Find(cutter.Id).Hidden, "undo restores the box and shows the cutter");
        DoRedo();
        Expect(Scene.Find(box.Id).Mesh.Faces.Count == after.Mesh.Faces.Count, "redo");
        yield return Frames(2);
    }

    IEnumerable<object> Knife()
    {
        Fresh();
        var c = Prim(CreateKind.Cylinder, Vector3.Zero);
        SelectOnly(c);
        int f0 = c.Mesh.Faces.Count;
        var (mn, mx) = c.Mesh.Bounds();
        DoMesh("Slice", (o, m, s) => MeshOps.Slice(m, (mn + mx) / 2, Vector3.UnitZ));
        Expect(c.Mesh.Faces.Count > f0 && c.Mesh.IsClosedManifold(out _), $"slice through the middle: {f0} -> {c.Mesh.Faces.Count} faces, closed");
        BeginModal(ModalKind.Knife);
        yield return Frames(3);
        Expect(ModalActive, "K knife modal started");
        yield return Shot("knife_modal");
        CancelModal();
        yield return Frames(2);
    }

    IEnumerable<object> Uv()
    {
        Fresh();
        var s = Prim(CreateKind.Sphere, Vector3.Zero);
        SelectOnly(s);
        SmartUv();
        var (lo, hi) = UvTools.Bounds(s.Mesh, Enumerable.Range(0, s.Mesh.Faces.Count));
        Expect(lo.X >= -1e-3f && lo.Y >= -1e-3f && hi.X <= 1.001f && hi.Y <= 1.001f, $"smart UV islands inside 0..1 ({lo} .. {hi})");
        Note($"{UvTools.Islands(s.Mesh).Count} islands, texel density {UvTools.TexelDensity(s.Mesh, Enumerable.Range(0, s.Mesh.Faces.Count)):0} px/m");
        OpenUvEditor();
        yield return Frames(4);
        Expect(uvOpen && Level == SubObject.Polygon, "UV editor open (face mode)");
        yield return Shot("uv_editor");
        uvOpen = false; SetLevel(SubObject.Object);
    }

    IEnumerable<object> XRayTest()
    {
        Fresh();
        var back = Prim(CreateKind.Box, new Vector3(0, 6, 0));
        var front = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(front, back); SetLevel(SubObject.Polygon);
        XRay = true;
        yield return Frames(3);
        yield return Shot("xray_edit_mode");
        XRay = false; SetLevel(SubObject.Object);
        Expect(true, "X-Ray toggled in edit mode without errors");
    }

    IEnumerable<object> SmallBoxTest()
    {
        Fresh();
        var plane = Prim(CreateKind.Box, Vector3.Zero);
        plane.Scale = new Vector3(20, 20, 1);
        Selected.Clear(); SelectionChanged();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.WireframeOnly = false;
        vp.Camera.Frame(new Vector3(-3, -3, -1), new Vector3(3, 3, 2));
        yield return Frames(3);
        var c = ScreenOf(vp, new Vector3(0, 0, plane.WorldBounds().Max.Z));
        int inside = plane.Mesh.Verts.Count(v => { var sp = ScreenOf(vp, Vector3.Transform(v, plane.World)); return MathF.Abs(sp.X - c.X) < 15 && MathF.Abs(sp.Y - c.Y) < 15; });
        BoxSelect(vp, c - new Vector2(15), c + new Vector2(15));
        Expect(inside == 0 && Selected.Contains(plane.Id), $"object mode: a 30 px box in the middle of a big box (no vertex in it: {inside}) selects it");
        SelectOnly(plane); SetLevel(SubObject.Polygon); SubSel(plane).Clear();
        BoxSelect(vp, c - new Vector2(15), c + new Vector2(15));
        Expect(SubSel(plane).Faces.Count == 1, $"polygon level: a small box on the big top face selects it ({SubSel(plane).Faces.Count})");
        SetLevel(SubObject.Object);
    }

    /// <summary>Default (like 3ds Max): a box over a cube in vertex mode takes the hidden back corners too; "Visible only" keeps
    /// only the corners you can see. A click on a vertex behind the mesh picks it.</summary>
    IEnumerable<object> SelectThroughTest()
    {
        Fresh();
        var cube = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(cube); SetLevel(SubObject.Vertex);
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.WireframeOnly = false;
        vp.Camera.Frame(new Vector3(-2, -2, -1), new Vector3(2, 2, 3));
        yield return Frames(3);
        SelectVisibleOnly = false;
        var corners = cube.Mesh.Verts.Select(v => ScreenOf(vp, Vector3.Transform(v, cube.World))).ToList();
        var mn = corners.Aggregate(Vector2.Min) - new Vector2(10); var mx = corners.Aggregate(Vector2.Max) + new Vector2(10);
        SubSel(cube).Clear();
        BoxSelect(vp, mn, mx);
        int all = SubSel(cube).Verts.Count;
        Expect(all == cube.Mesh.Verts.Count, $"box over the cube, default: every corner selected, hidden ones too ({all}/{cube.Mesh.Verts.Count})");
        SubSel(cube).Clear(); SelectVisibleOnly = true;
        BoxSelect(vp, mn, mx);
        int vis = SubSel(cube).Verts.Count;
        Expect(vis > 0 && vis < all, $"Visible only: only the corners you can see ({vis})");
        SelectVisibleOnly = false;
        // a room (inward faces) seen from outside: the near wall is not drawn, a click goes to the far wall you see
        SetLevel(SubObject.Object);
        var room = Prim(CreateKind.Room, new Vector3(8, 0, 0));
        SelectOnly(room); SetLevel(SubObject.Polygon);
        var (rmn, rmx) = room.WorldBounds();
        vp.Camera.Frame(rmn - new Vector3(1), rmx + new Vector3(1)); yield return Frames(3);
        var rc = (rmn + rmx) * 0.5f;
        SubSel(room).Clear();
        UI.ImGuiBackend.SyntheticInput = true;
        var pt = ScreenOf(vp, rc);
        PostMouse(0x200, pt, 0); yield return Frames(2); PostMouse(0x201, pt, 1); yield return Frames(2); PostMouse(0x202, pt, 0); yield return Frames(3);
        UI.ImGuiBackend.SyntheticInput = false;
        int picked = SubSel(room).Faces.Count == 1 ? SubSel(room).Faces.First() : -1;
        bool facing = picked >= 0 && Vector3.Dot(Vector3.TransformNormal(room.Mesh.FaceNormal(picked), room.World), vp.Camera.Eye - Vector3.Transform(room.Mesh.FaceCenter(picked), room.World)) > 0;
        Expect(facing, $"click on a room from outside picks the wall you see, not the hidden near wall (face {picked}, facing the camera {facing})");
        Maximized = false; SetLevel(SubObject.Object);
    }

    IEnumerable<object> Gims()
    {
        Fresh();
        var mesh = Prim(CreateKind.Box, Vector3.Zero); mesh.Name = "mm_crate";
        SelectOnly(mesh);
        CreateDrawable();
        var node = Current;
        Expect(node?.GtaType == GtaObjectType.Model && Scene.Parent(mesh) == node, "Drawable node with the mesh under it");
        SelectOnly(mesh);
        CollisionFromSelection();
        var col = Scene.Descendants(node).FirstOrDefault(o => o.GtaType == GtaObjectType.Collision);
        Expect(col != null && Scene.Parent(col)?.GtaType == GtaObjectType.CollisionGroup && Scene.Parent(Scene.Parent(col)) == node, "collision in the Drawable's collision group (Drawable Col)");
        SelectOnly(node);
        AddLight(GtaLightKind.Spot);
        Expect(Current?.GtaType == GtaObjectType.Light && Scene.Parent(Current) == node, "spot light under the Drawable");
        MakeLodMeshes(mesh);
        int lods = Scene.Children(node).Count(o => o.Gta.LodLevel > 0);
        Expect(lods == 3, $"3 LOD meshes ({lods})");
        propTab = PropTab.Gta; SelectOnly(node); outlinerMode = 1;
        yield return Frames(3);
        yield return Shot("gims_outliner");
        // moving the parent moves the children
        var p0 = mesh.Position;
        Do("move", () => { node.Position += new Vector3(2, 0, 0); });
        foreach (var d in Scene.Descendants(node)) d.Position += new Vector3(2, 0, 0); // what the gizmo does
        Expect(Vector3.Distance(Scene.Find(mesh.Id).Position, p0 + new Vector3(2, 0, 0)) < 1e-4f, "children follow the Drawable");
        outlinerMode = 0;
    }

    IEnumerable<object> InstancesTest()
    {
        Fresh();
        var chair = Prim(CreateKind.Box, Vector3.Zero); chair.Name = "mm_chair";
        SelectOnly(chair);
        CloneSelection(true); var a = Current; a.Position = new Vector3(2.5f, 0, 0);
        CloneSelection(true); var b = Current; b.Position = new Vector3(5, 0, 0); b.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f);
        Expect(a != chair && b != a && ReferenceEquals(chair.Mesh, a.Mesh) && ReferenceEquals(a.Mesh, b.Mesh) && Scene.InstanceCount(chair) == 3, "two instances share the original's mesh (3 in the group)");
        // an edit on one instance shows on all
        Do("edit instance", () => { var m = a.Mesh.Clone(); for (int i = 0; i < m.Verts.Count; i++) if (m.Verts[i].Z > 0.5f) m.Verts[i] += new Vector3(0, 0, 0.6f); a.Mesh = m; a.Touch(); });
        yield return Frames(2);
        float h(SceneObject o) => o.Mesh.Bounds().Max.Z;
        Expect(ReferenceEquals(chair.Mesh, a.Mesh) && ReferenceEquals(b.Mesh, a.Mesh) && MathF.Abs(h(chair) - h(a)) < 1e-5f && chair.Version == a.Version, $"edit reached every instance (height {h(chair):0.00} m)");
        // pivot change keeps every instance in place
        var bb = b.WorldBounds();
        SelectOnly(a); PivotToBase(); CenterPivot();
        var bb2 = Scene.Find(b.Id).WorldBounds();
        Expect(Vector3.Distance(bb.Min, bb2.Min) < 1e-3f && Vector3.Distance(bb.Max, bb2.Max) < 1e-3f, "pivot change on one instance: the others don't move");
        // a Drawable with its collision, instanced as a whole
        var mesh = Prim(CreateKind.Box, new Vector3(0, 4, 0)); mesh.Name = "mm_bench";
        SelectOnly(mesh); CreateDrawable(); var node = Current;
        SelectOnly(mesh); CollisionFromSelection();
        SelectOnly(node); CloneSelection(true); var node2 = Current; node2.Position += new Vector3(3, 0, 0);
        var kids1 = Scene.Descendants(node).ToList(); var kids2 = Scene.Descendants(node2).ToList();
        Expect(node2 != node && node2.GtaType == GtaObjectType.Model && kids2.Count == kids1.Count && kids2.Any(k => k.GtaType == GtaObjectType.CollisionGroup)
            && kids2.Where(k => k.GtaType == GtaObjectType.Drawable).All(k => kids1.Any(k1 => ReferenceEquals(k1.Mesh, k.Mesh))), $"Drawable instanced with its hierarchy ({kids2.Count} children, meshes shared)");
        SelectOnly(a); propTab = PropTab.Object;
        yield return Frames(3);
        yield return Shot("instances");
        // undo keeps the group on one mesh
        DoUndo();
        yield return Frames(2);
        var group = Scene.Objects.Where(o => o.Name.StartsWith("mm_chair")).ToList();
        Expect(group.Count == 3 && group.All(o => ReferenceEquals(o.Mesh, group[0].Mesh)), "undo: the chairs still share one mesh");
        // make unique
        SelectOnly(group[2]); MakeUniqueSelection();
        yield return Frames(1);
        Expect(group[2].InstanceKey == null && !ReferenceEquals(group[2].Mesh, group[0].Mesh) && Scene.InstanceCount(group[0]) == 2, "Make Unique: own mesh, the other two stay instances");
        // save / reopen writes the mesh once
        var json = MoloMax.Core.Scenes.SceneIO.ToJson(Scene);
        var back = MoloMax.Core.Scenes.SceneIO.FromJson(json);
        var bc = back.Objects.Where(o => o.Name.StartsWith("mm_chair") && o.InstanceKey != null).ToList();
        Expect(bc.Count == 2 && ReferenceEquals(bc[0].Mesh, bc[1].Mesh), "save / reopen: instances still share their mesh");
        // Clone Options popup (3ds Max Ctrl+V)
        OpenPopup("cloneopts");
        yield return Frames(2);
        yield return Shot("clone_options");
        closeCloneOpts = true;
        yield return Frames(2);
    }

    IEnumerable<object> ModifierEditTest()
    {
        Fresh();
        var o = Prim(CreateKind.Box, Vector3.Zero); o.Name = "mm_crate";
        SelectOnly(o);
        var ep = new MoloMax.Core.Modifiers.EditPolyModifier(); var uw = new MoloMax.Core.Modifiers.UnwrapUvwModifier();
        Do("add", () => { o.Modifiers.Add(ep); o.Modifiers.Add(new MoloMax.Core.Modifiers.SmoothByAngleModifier()); o.Modifiers.Add(uw); o.Touch(); });
        propTab = PropTab.Modifiers;
        activeModifier[o.Id] = 0;
        ToggleEditMode(); // Tab on the picked Edit Poly edits that step
        Expect(o.ModEdit != null && o.ModEdit.Index == 0 && Level != SubObject.Object && o.Modifiers.Count == 2, "Tab on a picked Edit Poly: its step is being edited, the stack after it stays live");
        SetLevelDirect(SubObject.Polygon);
        int top = Enumerable.Range(0, o.Mesh.Faces.Count).First(f => o.Mesh.FaceNormal(f).Z > 0.9f);
        SubSel(o).Faces.Clear(); SubSel(o).Faces.Add(top);
        ExtrudeFaces(0.4f, false);
        yield return Frames(3);
        yield return Shot("edit_poly_modifier");
        ToggleEditMode(); // Tab again: leave
        yield return Frames(2);
        var ev = o.Evaluated();
        Expect(o.ModEdit == null && o.Mesh.Faces.Count == 6 && ev.Faces.Count == 10 && ep.HasEdit, $"edit kept in the modifier (base 6 polys, result {ev.Faces.Count})");
        // Unwrap UVW: open its UV editor, Auto Unwrap, done
        EditModifier(o, 2, SubObject.Polygon); OpenUvEditor();
        UnwrapOp(o, 2, uw, "Auto Unwrap", (m, faces, mod) => { var (n, seams) = UvUnfold.AutoUnwrap(m, mod.AngleLimit, faces); mod.SetSeams(seams); UvTools.SetDensity(m, faces, mod.Density, mod.TextureSize); UvTools.Pack(m, faces); });
        SetLevelDirect(SubObject.Edge);
        yield return Frames(4);
        yield return Shot("unwrap_uvw_modifier");
        EndModifierEdit(o); uvOpen = false;
        yield return Frames(2);
        var ev2 = o.Evaluated();
        int islands = UvTools.Islands(ev2).Count;
        Expect(uw.HasUv && uw.SeamSet().Count > 0 && islands >= 6 && ev2.Faces.All(f => f.UV.All(u => u.X >= -1e-3f && u.Y >= -1e-3f && u.X <= 1.001f && u.Y <= 1.001f)), $"Unwrap UVW kept the UVs ({islands} islands, {uw.SeamSet().Count} seams) on top of the Edit Poly result");
        // the Edit Poly below moves: the unwrap still fits (same topology)
        EditModifier(o, 0, SubObject.Vertex);
        Do("move", () => { for (int i = 0; i < o.Mesh.Verts.Count; i++) if (o.Mesh.Verts[i].Z > 1.2f) o.Mesh.Verts[i] += new Vector3(0, 0, 0.3f); o.Touch(); });
        EndModifierEdit(o);
        yield return Frames(1);
        Expect(uw.Warning == null && o.Evaluated().Bounds().Max.Z > 1.6f, "moving vertices in the Edit Poly keeps the unwrap above it valid");
        // save / reopen
        var back = MoloMax.Core.Scenes.SceneIO.FromJson(MoloMax.Core.Scenes.SceneIO.ToJson(Scene));
        var bo = back.Objects.First(x => x.Name == "mm_crate");
        Expect(bo.Modifiers.Count == 3 && bo.Evaluated().Faces.Count == 10, "save / reopen keeps both modifiers and their data");
    }

    IEnumerable<object> ApiTest()
    {
        Fresh();
        Expect(Api.Running && Api.Port > 0, $"API server running on 127.0.0.1:{Api.Port}");
        // raw HTTP like any client
        string Post(string method, string json, string token)
        {
            using var h = new System.Net.Http.HttpClient();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{Api.Port}/rpc") { Content = new System.Net.Http.StringContent("{\"method\":\"" + method + "\",\"params\":" + json + "}") };
            if (token != null) req.Headers.Add("X-Polymorph-Token", token);
            var r = h.Send(req); return ((int)r.StatusCode) + " " + r.Content.ReadAsStringAsync().Result;
        }
        string a = null, b = null, c = null;
        var t = System.Threading.Tasks.Task.Run(() =>
        {
            a = Post("app.info", "{}", null);
            b = Post("object.create", "{\"kind\":\"box\",\"name\":\"api_box\",\"position\":[1,2,0],\"params\":{\"Width\":3}}", Api.Token);
            c = Post("modifier.add", "{\"name\":\"api_box\",\"type\":\"Mirror\",\"props\":{\"AxisX\":true,\"Merge\":true}}", Api.Token);
        });
        yield return Until(() => t.IsCompleted, 30);
        Note($"no token: {a?[..Math.Min(a.Length, 60)]}");
        Expect(a != null && a.StartsWith("401"), "a call without the token is refused (a web page can't drive the app)");
        var box = Scene.Objects.FirstOrDefault(o => o.Name == "api_box");
        Expect(b != null && b.StartsWith("200") && box != null && MathF.Abs(box.Mesh.Bounds().Max.X - box.Mesh.Bounds().Min.X - 3) < 1e-3f && box.Position == new Vector3(1, 2, 0), "object.create: 3 m box at (1, 2, 0)");
        Expect(c != null && c.StartsWith("200") && box?.Modifiers.Count == 1, "modifier.add: Mirror on the box");
        // the Python package and the MCP server, run with the real Python
        var py = FindPython();
        if (py == null) { Note("no Python on this PC: Python / MCP part skipped"); yield break; }
        var script = Path.Combine(PythonFolder, "tests", "test_api.py");
        var output = new System.Text.StringBuilder(); int exit = -1;
        var run = System.Threading.Tasks.Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(py, (py == "py" ? "-3 " : "") + "-u \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["POLYMORPH_PORT"] = Api.Port.ToString(); psi.Environment["POLYMORPH_TOKEN"] = Api.Token; psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var p = System.Diagnostics.Process.Start(psi);
            output.Append(p.StandardOutput.ReadToEnd()); output.Append(p.StandardError.ReadToEnd());
            p.WaitForExit(120000); exit = p.ExitCode;
        });
        yield return Until(() => run.IsCompleted, 150);
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Note(line.TrimEnd());
        Expect(exit == 0, $"Python package + MCP server end to end (exit code {exit})");
        SelectOnly(Scene.Objects.First(o => o.Name.StartsWith("py_bollard")));
        foreach (var vp in Viewports) FrameSelection(vp);
        yield return Frames(3);
        yield return Shot("python_api_bollards");
    }

    IEnumerable<object> SculptTest()
    {
        Fresh();
        Params.Radius = 1; Params.Segments = 48;
        var o = Prim(CreateKind.Sphere, new Vector3(0, 0, 1)); o.Name = "tripo_rock";
        var rnd = new Random(7);
        var ctr = o.Mesh.Verts.Aggregate(Vector3.Zero, (a, b) => a + b) / o.Mesh.Verts.Count; // the sphere's pivot is at its base
        Do("noise", () => { for (int i = 0; i < o.Mesh.Verts.Count; i++) o.Mesh.Verts[i] = ctr + (o.Mesh.Verts[i] - ctr) * (1 + ((float)rnd.NextDouble() - 0.5f) * 0.08f); o.Touch(); });
        float Noise() { var r = o.Mesh.Verts.Select(v => (v - ctr).Length()).ToList(); float m = r.Average(); return MathF.Sqrt(r.Average(x => (x - m) * (x - m))); }
        float n0 = Noise();
        SelectOnly(o);
        ToggleSculptMode();
        Expect(SculptMode && sculptSession != null, "sculpt mode on, brush sized from the mesh");
        Sculpt.Brush = SculptBrush.Relax;
        SculptWhole(o, false); // Smooth all (Relax all only evens the spacing, it keeps bumps on purpose)
        Sculpt.Brush = SculptBrush.Smooth; Sculpt.SymX = true;
        Do("dabs", () => { var st = CurrentSculptSettings(o); for (int i = 0; i < 20; i++) sculptSession.Dab(ctr + new Vector3(0.7f, 0, 0.7f), Vector3.Normalize(new Vector3(1, 0, 1)), st); o.Touch(); });
        float n1 = Noise();
        foreach (var vp in Viewports) FrameSelection(vp);
        sculptHover = (Vector3.Transform(ctr + new Vector3(0.7f, 0, 0.7f), o.World), Vector3.Normalize(new Vector3(1, 0, 1)), Sculpt.Radius, true);
        yield return Frames(3);
        yield return Shot("sculpt_mode");
        Note($"radius spread {n0:0.0000} -> {n1:0.0000}");
        Expect(n1 < n0 * 0.8f, "smooth all + smooth strokes calm the noise");
        DoUndo(); DoUndo();
        yield return Frames(1);
        o = Scene.Find(o.Id);
        Expect(MathF.Abs(Noise() - n0) < 1e-4f, "undo brings the original surface back");
        ToggleSculptMode();
        Expect(!SculptMode, "sculpt mode off");
    }

    // ---- real window messages (what a mouse / keyboard sends), so the whole input path is tested
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    void PostMouse(int msg, Vector2 p, int buttons) => PostMessage(form.Handle, msg, (IntPtr)buttons, (IntPtr)(((int)p.Y << 16) | ((int)p.X & 0xFFFF)));
    void PostKey(System.Windows.Forms.Keys k, bool down)
    {
        // arrows, Page Up / Down, Home / End, Insert / Delete come with the "extended" bit like a real keyboard (without it they read as the numpad)
        int vk = (int)k; int ext = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E ? 1 << 24 : 0;
        PostMessage(form.Handle, down ? 0x100 : 0x101, (IntPtr)vk, (IntPtr)((down ? 1 : unchecked((int)0xC0000001)) | ext));
    }
    Vector2 ScreenOf(Viewport vp, Vector3 world) { vp.Camera.Project(world, vp.W, vp.H, out var s, out _); return new Vector2(vp.X + s.X, vp.Y + s.Y); }

    IEnumerable<object> RealInputTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true; // button / modifier state from the posted messages
        var vp = Viewports[ActiveViewport];
        var box = Prim(CreateKind.Box, new Vector3(-2.5f, 0, 0)); box.Name = "click_box";
        Params.Radius = 1; Params.Segments = 32;
        var ball = Prim(CreateKind.Sphere, new Vector3(1.5f, 0, 0)); ball.Name = "sculpt_ball";
        Selected.Clear(); SelectionChanged();
        foreach (var v in Viewports) v.Camera.Frame(new Vector3(-4, -2, 0), new Vector3(3, 2, 2));
        yield return Frames(3);
        // 1. a left click on the box selects it
        var pb = ScreenOf(vp, new Vector3(-2.5f, 0, 1f));
        PostMouse(0x200, pb, 0); yield return Frames(2);
        PostMouse(0x201, pb, 1); yield return Frames(2);
        PostMouse(0x202, pb, 0); yield return Frames(3);
        Expect(Selected.Count == 1 && Selected.Contains(box.Id), $"click on the box selects it (selected: {string.Join(",", Selected.Select(i => Scene.Find(i)?.Name))})");
        // 2. Tab with an Edit Poly picked: edits that step; Tab again ends it
        Do("add edit poly", () => { box.Modifiers.Add(new MoloMax.Core.Modifiers.EditPolyModifier()); box.Touch(); });
        activeModifier[box.Id] = 0;
        PostKey(System.Windows.Forms.Keys.Tab, true); yield return Frames(2); PostKey(System.Windows.Forms.Keys.Tab, false); yield return Frames(2);
        yield return Frames(2);
        bool inEdit = box.ModEdit != null && Level != SubObject.Object;
        string st1 = $"after Tab: modedit={box.ModEdit != null} level={Level} focus={ImGuiNET.ImGui.GetIO().WantCaptureKeyboard} sel={Selected.Count}";
        PostKey(System.Windows.Forms.Keys.Tab, true); yield return Frames(2); PostKey(System.Windows.Forms.Keys.Tab, false); yield return Frames(4);
        Expect(inEdit && box.ModEdit == null && Level == SubObject.Object, $"Tab enters the picked Edit Poly, Tab again leaves it (edit kept in the modifier)  [{st1}; after 2nd: modedit={box.ModEdit != null} level={Level}]");
        // 3. sculpt: a Draw stroke across the ball with the real mouse
        SelectOnly(ball);
        ToggleSculptMode();
        Sculpt.Brush = SculptBrush.Draw; Sculpt.Strength = 1f; Sculpt.SymX = false; Sculpt.Radius = 0.45f;
        var before = ball.Mesh.Verts.ToArray();
        var centreW = Vector3.Transform(ball.Mesh.Verts.Aggregate(Vector3.Zero, (a, b) => a + b) / ball.Mesh.Verts.Count, ball.World);
        var front = centreW - vp.Camera.Forward * 0.98f; // a point on the side of the ball facing the camera
        var a0 = ScreenOf(vp, front + new Vector3(0, 0, 0.35f)); var a1 = ScreenOf(vp, front - new Vector3(0, 0, 0.35f));
        PostMouse(0x200, a0, 0); yield return Frames(2);
        Expect(sculptHover.Hit, "the brush circle follows the mouse on the ball");
        PostMouse(0x201, a0, 1); yield return Frames(2);
        for (int i = 1; i <= 12; i++) { PostMouse(0x200, Vector2.Lerp(a0, a1, i / 12f), 1); yield return Frames(1); }
        PostMouse(0x202, a1, 0); yield return Frames(3);
        int movedOut = 0; var c = ball.Mesh.Verts.Aggregate(Vector3.Zero, (q, w) => q + w) / ball.Mesh.Verts.Count;
        for (int i = 0; i < before.Length && i < ball.Mesh.Verts.Count; i++) if ((ball.Mesh.Verts[i] - c).Length() > (before[i] - c).Length() + 0.005f) movedOut++;
        Note($"{movedOut} vertices raised by the stroke");
        yield return Shot("real_input_sculpt");
        Expect(movedOut > 10, "a real mouse stroke with Draw raises the surface");
        // 4. Shift held: the same stroke smooths instead
        var raised = ball.Mesh.Verts.ToArray();
        string undoBefore = Undo.UndoName;
        PostKey(System.Windows.Forms.Keys.ShiftKey, true); yield return Frames(1);
        Note($"after Shift down: Input.Shift={Input.Shift} sculpting={sculpting} hover={sculptHover.Hit}");
        PostMouse(0x200, a0, 4); yield return Frames(1);
        PostMouse(0x201, a0, 1 | 4); yield return Frames(2);
        Note($"after press: sculpting={sculpting} hover={sculptHover.Hit} undo '{undoBefore}' -> '{Undo.UndoName}' brush={CurrentSculptSettings(ball).Brush}");
        for (int i = 1; i <= 12; i++) { PostMouse(0x200, Vector2.Lerp(a0, a1, i / 12f), 1 | 4); yield return Frames(1); }
        PostMouse(0x202, a1, 4); yield return Frames(2);
        PostKey(System.Windows.Forms.Keys.ShiftKey, false); yield return Frames(2);
        float Bump(Vector3[] vs) => vs.Max(v => (v - c).Length());
        Note($"highest point {Bump(raised):0.000} -> {Bump(ball.Mesh.Verts.ToArray()):0.000} m from the centre");
        Expect(Bump(ball.Mesh.Verts.ToArray()) < Bump(raised) - 0.001f, "Shift + stroke smooths the bump");
        // 5. Ctrl+Z (window key messages) undoes the smooth stroke, then the draw stroke
        for (int k = 0; k < 2; k++)
        {
            PostKey(System.Windows.Forms.Keys.ControlKey, true); yield return Frames(1);
            PostKey(System.Windows.Forms.Keys.Z, true); yield return Frames(2); PostKey(System.Windows.Forms.Keys.Z, false);
            PostKey(System.Windows.Forms.Keys.ControlKey, false); yield return Frames(2);
        }
        var ballNow = Scene.Find(ball.Id);
        float diff = ballNow == null ? 99 : before.Zip(ballNow.Mesh.Verts).Max(p => Vector3.Distance(p.First, p.Second));
        Expect(diff < 1e-4f, $"Ctrl+Z twice restores the ball (max change {diff:0.00000} m)");
        if (SculptMode) ToggleSculptMode();
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> SkipRecovery() { recoveryOpen = false; yield return Frames(5); }

    IEnumerable<object> Checker()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero);
        b.Mesh = Core.Mesh.Primitives.Plane(10, 10, 300, 300); b.Touch(); // 90k verts, no colours: over the budget for one model
        var issues = GtaChecker.Run(Scene);
        Note(string.Join(" | ", issues.Select(i => i.Level + ": " + i.Message).Take(6)));
        Expect(issues.Count > 0, $"{issues.Count} issue(s) found on a raw 90k-vertex plane");
        RunGtaChecks();
        yield return Frames(3);
        yield return Shot("gta_check");
        Do("Fix all", () => GtaChecker.FixAll(Scene));
        var after = GtaChecker.Run(Scene);
        Note(string.Join(" | ", after.Select(i => i.Level + ": " + i.Message).Take(6)));
        Expect(after.Count(i => i.Level == CheckLevel.Error) == 0, $"no errors left after Fix all ({after.Count} notes)");
        RunGtaChecks();
        yield return Frames(3);
        yield return Shot("gta_check_fixed");
        checkOpen = false;
    }

    IEnumerable<object> RoomBuilderTest()
    {
        Fresh();
        Scene.Plan = new RoomPlanData();
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "hall", X0 = 0, Y0 = 0, X1 = 6, Y1 = 4 });
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "office", X0 = 6, Y0 = 0, X1 = 10, Y1 = 4 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 6, Y = 2 });                 // inner door
        Scene.Plan.Openings.Add(new PlanOpening { X = 0, Y = 2 });                 // street door
        Scene.Plan.Openings.Add(new PlanOpening { X = 8, Y = 4, Sill = 0.9f, Height = 1.2f }); // window
        planOpen = true;
        yield return Frames(4);
        yield return Shot("room_builder");
        BuildPlan();
        int rooms = Scene.Objects.Count(o => o.GtaType == GtaObjectType.MloRoom);
        Expect(rooms == 2, $"2 MLO rooms built ({rooms}), {Scene.Objects.Count} objects");
        planOpen = false;
        yield return Frames(3);
        yield return Shot("room_builder_built");
    }

    IEnumerable<object> ExportTest()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero); b.Name = "mm_test_crate";
        SelectOnly(b); CollisionFromSelection();
        Scene.Gta.OutputFolder = StPath("export");
        Scene.Gta.ResourceName = "molomax_selftest";
        OpenHub(1);
        StartExport();
        yield return Until(() => exportTask == null || exportTask.IsCompleted, 120);
        yield return Frames(3);
        var rep = lastReport ?? exportTask?.Result;
        Expect(rep != null && rep.Ok, "export ok " + (rep == null ? "(no report)" : string.Join(" ", rep.Errors)));
        if (rep != null) { Note(rep.ToString().Replace("\n", " | ")); Expect(rep.Files.Any(f => f.EndsWith(".ydr")) && File.Exists(Path.Combine(rep.ResourceFolder, "fxmanifest.lua")), "ydr + fxmanifest written"); }
        yield return Shot("export_report");
        HubOpen = false;
    }

    IEnumerable<object> SendTest()
    {
        var server = StPath("server_resources"); Directory.CreateDirectory(server);
        Settings.ServerResourcesFolder = server;
        InstallBridge();
        Expect(File.Exists(Path.Combine(server, "molomax_bridge", "server.lua")) && bridgeCfg?.Contains("ensure molomax_bridge") == true, "bridge resource installed, server.cfg lines ready");
        yield return Frames(3);
        yield return Shot("bridge_help");
        bridgeOpen = false;
        if (Scene.Objects.Count == 0) Prim(CreateKind.Box, Vector3.Zero);
        SendToServer();
        yield return Until(() => sendTask == null, 60);
        yield return Frames(2);
        var copied = Directory.GetDirectories(server).Where(d => !d.EndsWith("molomax_bridge")).ToList();
        Expect(copied.Count == 1 && Directory.GetFiles(copied[0], "*.ydr", SearchOption.AllDirectories).Length > 0, "resource copied into the server: " + string.Join(", ", copied.Select(Path.GetFileName)));
        Note("status: " + sendStatus);
        yield return Shot("sent");
    }

    IEnumerable<object> SaveOpen()
    {
        Fresh();
        Prim(CreateKind.Box, Vector3.Zero); Prim(CreateKind.Sphere, new Vector3(3, 0, 0));
        var p = StPath("scene", "test.mmx"); Directory.CreateDirectory(Path.GetDirectoryName(p));
        CurrentFile = p;
        Expect(Save(false) && File.Exists(p), "saved");
        Expect(Save(false) && File.Exists(p + ".bak") || Directory.GetFiles(Path.GetDirectoryName(p), "*.bak").Length > 0, "second save keeps a .bak");
        int n = Scene.Objects.Count;
        Fresh();
        OpenFile(p);
        Expect(Scene.Objects.Count == n, $"reopened {Scene.Objects.Count}/{n} objects");
        Expect(Settings.RecentFiles?.Contains(p) == true, "in recent files");
        yield return Frames(2);
    }

    IEnumerable<object> GlbRoundTrip()
    {
        Fresh();
        var b = Prim(CreateKind.Sphere, Vector3.Zero);
        var glb = StPath("scene", "sphere.glb");
        Core.IO.GlbExporter.Export(Scene, new[] { b }, glb, TexturePng);
        Expect(File.Exists(glb), "glb written");
        int faces = b.Mesh.Faces.Count;
        ImportFilesAsync(new[] { glb }, new Vector3(4, 0, 0));
        yield return Until(() => pendingImports.Count == 0, 60);
        var back = Scene.Objects.LastOrDefault(o => o.Id != b.Id && o.Mesh.Faces.Count > 0);
        Expect(back != null && back.Mesh.Verts.Count > 0, $"re-imported ({back?.Mesh.Faces.Count} faces, original {faces})");
        yield return Frames(2);
    }

    IEnumerable<object> ImportReal(string ydr)
    {
        Fresh();
        var sw = Stopwatch.StartNew();
        ImportFilesAsync(new[] { ydr });
        int frames = 0;
        yield return Until(() => { frames++; return pendingImports.Count == 0; }, 120);
        Note($"{Path.GetFileName(ydr)} imported in {sw.ElapsedMilliseconds} ms while the UI kept drawing ({frames} frames)");
        var node = Scene.Objects.FirstOrDefault(o => o.GtaType == GtaObjectType.Model);
        Expect(node != null && Scene.Children(node).Any(), "arrives as a Drawable with its meshes under it");
        Expect(!importReportOpen || importReportTitle?.StartsWith("Import failed") != true, "no import error");
        FrameAll();
        yield return Frames(4);
        yield return Shot("real_ydr");
    }

    IEnumerable<object> MloReal(string ytyp)
    {
        Fresh();
        ImportMlo(ytyp);
        yield return Until(() => pendingImports.Count == 0, 180);
        int rooms = Scene.Objects.Count(o => o.GtaType == GtaObjectType.MloRoom), portals = Scene.Objects.Count(o => o.GtaType == GtaObjectType.Portal);
        Expect(rooms > 0, $"{Path.GetFileName(ytyp)}: {rooms} rooms, {portals} portals, {Scene.Objects.Count} objects");
        Note(string.Join(" | ", importReport.Take(3)));
        string B(IEnumerable<SceneObject> objs)
        {
            var l = objs.Where(o => o.Mesh.Verts.Count > 0).ToList(); if (l.Count == 0) return "none";
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var o in l) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
            return $"{l.Count} [{mn.X:0.0} {mn.Y:0.0} {mn.Z:0.0}]..[{mx.X:0.0} {mx.Y:0.0} {mx.Z:0.0}]";
        }
        Note("rooms " + B(Scene.Objects.Where(o => o.GtaType == GtaObjectType.MloRoom)) + "  portals " + B(Scene.Objects.Where(o => o.GtaType == GtaObjectType.Portal)));
        Note("meshes " + B(Scene.Objects.Where(o => o.GtaType == GtaObjectType.Drawable)) + "  collision " + B(Scene.Objects.Where(o => o.GtaType == GtaObjectType.Collision)));
        if (game != null) Expect(importReport.Any(l => l.Contains("taken from the game files")), "models extracted from the game");
        FrameAll();
        yield return Frames(4);
        yield return Shot("real_mlo");
    }

    IEnumerable<object> GameBrowserTest(string folder)
    {
        Fresh();
        Settings.GtaFolder = folder;
        OpenGameBrowser();
        yield return Frames(3);
        yield return Shot("game_browser_start");
        var sw = Stopwatch.StartNew();
        StartGameLoad(folder);
        yield return Until(() => game != null || gameError != null, 600);
        Expect(game != null, $"game files loaded in {sw.Elapsed.TotalSeconds:0.0} s: {gameStatus} {gameError}");
        if (game == null) yield break;
        sw.Restart();
        StartGameLoad(folder); // second time with the saved key
        var first = game; game = null;
        yield return Until(() => game != null || gameError != null, 600);
        Expect(game != null, $"reloaded with the saved key in {sw.Elapsed.TotalSeconds:0.0} s");
        game ??= first;
        gameSearch = "prop_bench_01a";
        yield return Frames(4);
        yield return Shot("game_browser_search");
        var a = game.Search("prop_bench_01a").FirstOrDefault();
        Expect(a != null, "prop_bench_01a found");
        if (a == null) yield break;
        GameImport(a, null);
        yield return Until(() => pendingImports.Count == 0, 120);
        Expect(Scene.Objects.Any(o => o.GtaType == GtaObjectType.Model && o.Name.StartsWith("prop_bench")), "imported from the game as a Drawable");
        gameOpen = false;
        FrameAll();
        yield return Frames(4);
        yield return Shot("game_import");
    }

    /// <summary>`--selftest dir --render scene.mmx`: opens a scene and takes a few beauty shots of it.</summary>
    IEnumerable<object> Render(string file)
    {
        recoveryOpen = false;
        Fresh();
        OpenFile(file);
        Expect(Scene.Objects.Count > 0, $"{Scene.Objects.Count} objects, {Scene.Objects.Sum(o => o.TriangleCount):N0} triangles, painted: {Scene.Objects.Count(o => o.Paint != null)}");
        Selected.Clear(); SelectionChanged();
        Maximized = true; ActiveViewport = 3; InteriorLight = false; Hour = 13;
        var vp = Viewports[3];
        (float Yaw, float Pitch, float Zoom, string Name)[] views = { (-MathF.PI / 2 - 0.38f, 0.36f, 0.75f, "front"), (-MathF.PI / 2 - 0.85f, 0.55f, 0.65f, "three_quarter"), (-MathF.PI / 2 - 0.2f, 0.3f, 0.4f, "close") };
        foreach (var v in views)
        {
            FrameAll(vp);
            vp.Camera.Yaw = v.Yaw; vp.Camera.Pitch = v.Pitch; vp.Camera.Distance *= v.Zoom;
            yield return Frames(6);
            yield return Shot("render_" + v.Name);
        }
        foreach (var o in Scene.Objects.Where(o => o.Paint != null)) foreach (var ts in o.Paint.Sets) ts.SetView(PaintView.Final);
        // the selection outline and the collision panel
        var mesh = Scene.Objects.FirstOrDefault(o => o.GtaType == GtaObjectType.Drawable && o.Mesh.Faces.Count > 0);
        if (mesh != null)
        {
            FrameAll(vp); vp.Camera.Yaw = views[1].Yaw; vp.Camera.Pitch = views[1].Pitch; vp.Camera.Distance *= 0.7f;
            SelectOnly(mesh); propTab = PropTab.Object;
            yield return Frames(4);
            yield return Shot("selected_outline");
        }
        var col = Scene.Objects.FirstOrDefault(o => o.GtaType == GtaObjectType.Collision);
        if (col != null)
        {
            SelectOnly(col); propTab = PropTab.Gta;
            yield return Frames(4);
            yield return Shot("collision_panel");
        }
    }

    SceneObject paintObj;

    IEnumerable<object> PaintStart()
    {
        Fresh();
        var b = Prim(CreateKind.Box, Vector3.Zero); b.Name = "mm_paint_crate";
        var m2 = Scene.AddMaterial("crate_metal", new Vector3(0.5f));
        Do("second material", () => { for (int i = 0; i < 3; i++) b.Mesh.Faces[i].MatId = m2.Id; b.Touch(); });
        SelectOnly(b);
        propTab = PropTab.Paint; paintTab = 0; paintRes = 1024;
        DoMesh("Smart UV Project", (oo, m, s) => UvUnwrap.SmartProject(m));
        StartPaint(b);
        paintObj = b;
        Expect(b.Paint?.Sets.Count == 2, $"two texture sets ({b.Paint?.Sets.Count}): {string.Join(", ", b.Paint?.Sets.Select(x => x.Name) ?? Array.Empty<string>())}");
        Expect(b.Paint.Sets.All(x => x.Map.Count > 1000) && b.Paint.Sets[0].Map.FaceOf.All(f => b.Mesh.Faces[f].MatId == b.Paint.Sets[0].MatId), "each set only paints its material's faces");
        FrameAll();
        yield return Frames(4);
        yield return Shot("paint_tools_tab");
    }

    IEnumerable<object> PaintStrokes()
    {
        var o = paintObj; SelectOnly(o);
        var doc = o.Paint; doc.Active = 1; var ts = doc.Cur;
        SetPaintTool(PaintTool.Brush);
        brush.ColorValue = new Vector3(0.8f, 0.1f, 0.05f); brush.Radius = 0.25f; brush.Strength = 1; brush.Hardness = 0.8f; brush.SymX = true;
        // a print on a face of this set, mirrored in X
        int t0 = Enumerable.Range(0, ts.Map.Count).First(t => ts.Map.Pos[t].X > 0.3f && MathF.Abs(ts.Map.Nrm[t].X) < 0.5f);
        var p = ts.Map.Pos[t0]; var n = ts.Map.Nrm[t0];
        PrepareStroke(o, ts);
        StampAll(o, ts, p, n, brush.Radius, p);
        int mirror = ts.Map.Nearest(p * new Vector3(-1, 1, 1), 0.05f);
        Expect(ts.CColor[t0].X > 0.7f && mirror >= 0 && ts.CColor[mirror].X > 0.7f, "stroke and its X mirror are painted");
        brush.SymX = false;
        // brush tip
        brush.TipName = "Splatter"; brush.Tip = BrushTips.Get("Splatter"); brush.ColorValue = new Vector3(0.1f, 0.1f, 0.1f);
        int t1 = Enumerable.Range(0, ts.Map.Count).First(t => ts.Map.Nrm[t].Z > 0.9f || ts.Map.Nrm[t].Z < -0.9f || MathF.Abs(ts.Map.Nrm[t].Y) > 0.9f);
        StampAll(o, ts, ts.Map.Pos[t1], ts.Map.Nrm[t1], 0.3f, ts.Map.Pos[t1]);
        brush.TipName = "Round"; brush.Tip = null;
        // fill a face
        SetPaintTool(PaintTool.Fill); fillScope = FillScope.Face; brush.ColorValue = new Vector3(0.1f, 0.3f, 0.8f);
        int face = ts.Map.FaceOf[ts.Map.Count / 2];
        PaintFillClick(o, ts, face);
        Expect(Enumerable.Range(0, ts.Map.Count).Where(t => ts.Map.FaceOf[t] == face).All(t => ts.CColor[t].Z > 0.7f), "fill paints the whole face");
        // clone
        SetPaintTool(PaintTool.Clone);
        cloneSource = p; PrepareStroke(o, ts);
        int t2 = Enumerable.Range(0, ts.Map.Count).First(t => ts.Map.FaceOf[t] == face && Vector3.Distance(ts.Map.Pos[t], p) > 0.4f);
        brush.CloneOffset = p - ts.Map.Pos[t2]; brush.CloneSourceSet = true;
        var srcCol = ts.CColor[ts.Map.Nearest(p, 0.05f)];
        StampAll(o, ts, ts.Map.Pos[t2], ts.Map.Nrm[t2], 0.1f, ts.Map.Pos[t2]);
        Expect(Vector3.Distance(ts.CColor[t2], srcCol) < 0.1f && Vector3.Distance(srcCol, new Vector3(0.1f, 0.3f, 0.8f)) > 0.2f, $"clone copied the source ({srcCol} -> {ts.CColor[t2]})");
        // mask painting hides the paint layer under the eraser
        SetPaintTool(PaintTool.Eraser); brush.MaskMode = true;
        PrepareStroke(o, ts);
        StampAll(o, ts, ts.Map.Pos[t2], ts.Map.Nrm[t2], 0.08f, ts.Map.Pos[t2]);
        Expect(ts.Current.MaskPaint != null && ts.CColor[t2].Z < 0.5f || ts.CColor[t2].X < 0.6f, "mask painting hides the layer there");
        brush.MaskMode = false; SetPaintTool(PaintTool.Brush);
        Expect(PaintUndo(), "Ctrl+Z undoes the last stroke");
        yield return Frames(3);
        yield return Shot("paint_strokes");
        shelfTab = 3;
        yield return Frames(3);
        yield return Shot("paint_shelf_alphas");
        shelfTab = 2;
        yield return Frames(3);
        yield return Shot("paint_shelf_smart");
    }

    IEnumerable<object> PaintLayersTest()
    {
        var o = paintObj; var ts = o.Paint.Cur;
        paintTab = 1;
        AddLayer(ts, new PaintLayer { Name = "Dirt", Kind = LayerKind.Folder });
        var folder = ts.Current;
        AddLayer(ts, new PaintLayer { Name = "Grime", Kind = LayerKind.Fill, Color = new Vector3(0.15f, 0.12f, 0.08f), Folder = folder.Id });
        var grime = ts.Current;
        SmartMasks.All.First(m => m.Name == "Dirt in corners").Apply(grime);
        grime.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 3 });
        grime.Filters.Add(new PaintFilter { Kind = FilterKind.HueSaturation, OnMask = false, Saturation = 0.5f });
        foreach (var l in SmartMaterials.Make("Yellow industrial paint", 5)) { l.Folder = -1; AddLayer(ts, l); }
        ts.Refresh(true);
        Expect(ts.Layers.Count(l => l.Folder == folder.Id) == 1 && ts.Layers.Any(l => l.Name.Contains("Chipped")), $"{ts.Layers.Count} layers, folder with its layer, GTA smart material added");
        folder.Visible = false; ts.Refresh(); var hidden = (Vector3[])ts.CColor.Clone();
        folder.Visible = true; ts.Refresh();
        Expect(true, "folder toggled");
        SmartLibrary.Folder = StPath("smart");
        SmartLibrary.Save("selftest yellow", ts.Layers);
        Expect(SmartLibrary.Names().Contains("selftest yellow"), "my smart material saved and listed in the shelf");
        ts.SelectedLayer = ts.Layers.IndexOf(grime);
        yield return Frames(4);
        yield return Shot("paint_layers_tab");
    }

    IEnumerable<object> PaintPackTest()
    {
        var o = paintObj; SelectOnly(o); var ts = o.Paint.Cur;
        EnsurePaintPack();
        Expect(packAlphas.Count >= 80 && reliefs.Count >= 80, $"starter pack found: {packAlphas.Count} alphas, {reliefs.Count} reliefs");
        // an image alpha as the brush tip
        PickTip(packAlphas.First(a => a.Key.EndsWith("Scratches")).Key);
        Expect(brush.Tip is { Width: 256 } && brush.Tip.Pixels.Where((b, i) => i % 4 == 3).Any(x => x > 200), "image alpha loaded as a tip");
        SetPaintTool(PaintTool.Brush); brush.ColorValue = new Vector3(0.9f, 0.85f, 0.1f); brush.Strength = 1;
        int t0 = Enumerable.Range(0, ts.Map.Count).First(t => MathF.Abs(ts.Map.Nrm[t].Z) < 0.5f);
        PrepareStroke(o, ts);
        StampAll(o, ts, ts.Map.Pos[t0], ts.Map.Nrm[t0], 0.4f, ts.Map.Pos[t0]);
        PickTip("Round");
        // radial symmetry: 4 prints turned around Z; scatter moves the print inside the brush
        brush.Radial = 4;
        var copies = Mirrors(new Vector3(1, 0, 0.5f), Vector3.UnitX, new Vector3(1, 0, 0.5f)).ToList();
        Expect(copies.Count == 4 && copies.Any(c => Vector3.Distance(c.P, new Vector3(0, 1, 0.5f)) < 1e-3f), $"radial symmetry gives 4 prints ({copies.Count})");
        brush.Radial = 1;
        // eyedropper
        int te = ts.Map.Count / 3;
        paintPick = true; PaintEyedrop(ts, ts.Map.Pos[te], 0.02f);
        Expect(!paintPick && Vector3.Distance(brush.ColorValue, ts.CColor[te]) < 0.05f, "eyedropper picks the colour under the click");
        // the new mask generators cover part of the surface
        foreach (var g in new[] { MaskGen.Dirt, MaskGen.Speckles, MaskGen.Pores })
        {
            var l = new PaintLayer { Name = g.ToString(), Kind = LayerKind.Fill, Mask = g, MaskNoiseScale = 4, Seed = 3 };
            var m = ts.MaskOf(l); float mean = m.Average();
            Expect(mean > 0.005f && mean < 0.95f, $"{g} mask covers part of the model ({mean:0.000})");
        }
        // the new filters
        var fl = new PaintLayer { Name = "filters", Kind = LayerKind.Fill, Mask = MaskGen.Noise, MaskNoiseScale = 6 };
        foreach (var k in new[] { FilterKind.DirectionalBlur, FilterKind.Highpass, FilterKind.Outline, FilterKind.Clamp, FilterKind.Threshold })
            fl.Filters.Add(new PaintFilter { Kind = k, Radius = 4, Black = k == FilterKind.Threshold ? 0.5f : 0.1f, White = 0.9f, Angle = 30 });
        var fm = ts.MaskOf(fl);
        Expect(fm.All(v => v == 0 || v == 1) && fm.Any(v => v == 1), "directional blur, highpass, outline, clamp and threshold run (threshold leaves 0 / 1)");
        // relief layer from the pack: the height changes
        var rel = reliefs.First(r => r.Name == "Brick");
        AddReliefLayer(rel.Name, rel.Label, rel.Path);
        ts.Refresh();
        float hmin = ts.CHeight.Min(), hmax = ts.CHeight.Max();
        Expect(hmax - hmin > 0.2f && ts.Current.Name.Contains("Relief"), $"relief layer gives height ({hmin:0.00}..{hmax:0.00})");
        shelfTab = 3; alphaSearch = "";
        yield return Frames(6);
        yield return Shot("paint_shelf_alphas_pack");
        shelfTab = 4;
        yield return Frames(30);
        yield return Shot("paint_shelf_relief");
        shelfTab = 2;
    }

    IEnumerable<object> PaintBakeTest()
    {
        var o = paintObj; var ts = o.Paint.Cur;
        Do("high-poly", () => { var hs = Scene.Add("crate_high", Core.Mesh.Primitives.Sphere(1.4f, 64), o.Position + new Vector3(0, 0, 1)); hs.Name = "crate_high"; foreach (var f in hs.Mesh.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new Vector4(0.9f, 0.6f, 0.1f, 1); });
        bakeHighId = Scene.Objects.Last(x => x.Name == "crate_high").Id;
        bakeSettings.MaxDistance = 0.6f; bakeSettings.AoRays = 12; bakeColourLayer = true;
        SelectOnly(o); paintTab = 2;
        StartBake(o, ts);
        yield return Until(() => bakeTask == null, 120);
        Note(bakeInfo ?? "");
        Expect(ts.HasBakedMaps && ts.BakedNormal != null && ts.Layers.Any(l => l.Name.StartsWith("Baked colour")), "bake applied: baked normal, maps and colour layer");
        Expect(Scene.Objects.First(x => x.Id == bakeHighId).Hidden, "high-poly hidden after the bake");
        yield return Frames(4);
        yield return Shot("paint_bake_tab");
    }

    IEnumerable<object> PaintExportTest()
    {
        var o = paintObj; var ts = o.Paint.Cur;
        SetPaintTool(PaintTool.VertexColour); vertexColour = new Vector4(1, 0, 0, 1);
        Do("Vertex colour stroke", () => { });
        int tv = 0; var pv = ts.Map.Pos[tv]; var nv = ts.Map.Nrm[tv];
        VertexStamp(o, pv, nv, 0.6f);
        Expect(o.Mesh.Faces.Any(f => f.Col.Any(c => c.X > 0.9f && c.Y < 0.1f)), "vertex colour stroke painted red corners");
        yield return Frames(4);
        Expect(Viewports[ActiveViewport].Shading == ShadingMode.VertexColour, "the view shows vertex colours with the tool");
        yield return Shot("paint_vertex_colours");
        SetPaintTool(PaintTool.Brush);
        yield return Frames(2);
        // emissive square on this set
        brush.Emissive = true; brush.EmissiveValue = 1; brush.ColorValue = new Vector3(1, 1, 0.8f);
        int face = ts.Map.FaceOf[ts.Map.Count / 3];
        fillScope = FillScope.Face; PaintFillClick(o, ts, face);
        brush.Emissive = false;
        Expect(ts.HasEmissive, "emissive painted");
        ApplyEmissiveFaces(o, ts);
        Expect(Scene.Materials.Any(m => m.Shader == "emissive" && m.Name.EndsWith("_emissive")) && o.Mesh.Faces[face].Col.All(c => c == Vector4.One), "emissive face -> emissive shader, white vertex colours");
        ts.ExportFormat = PaintExportFormat.Sollumz; ts.ExportSize = 1024;
        paintTab = 3;
        var p = StPath("scene", "paint.mmx"); Directory.CreateDirectory(Path.GetDirectoryName(p));
        CurrentFile = p;
        Expect(BakePaint(o), "textures saved");
        var dir = PaintDir; var bn = PaintBase(o, ts);
        Expect(File.Exists(Path.Combine(dir, bn + ".dds")) && File.Exists(Path.Combine(dir, bn + "_e.dds")) && File.Exists(Path.Combine(dir, "sollumz", bn, bn + ".png")), $"DDS (+ _e) and the Sollumz PNG folder written ({bn})");
        Note(string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Take(14)));
        yield return Frames(4);
        yield return Shot("paint_export_tab");
        Expect(Save(false), "scene saved");
        int sets = o.Paint.Sets.Count, layers = o.Paint.Sets[1].Layers.Count;
        Fresh(); OpenFile(p);
        var back = Scene.Objects.FirstOrDefault(x => x.Name == o.Name);
        Expect(back?.Paint?.Sets.Count == sets && back.Paint.Sets[1].Layers.Count == layers && back.Paint.Sets[1].HasBakedMaps, $"reopened: {back?.Paint?.Sets.Count} sets, {back?.Paint?.Sets[1].Layers.Count} layers, baked maps kept");
        paintMode = false;
        yield return Frames(3);
    }

    IEnumerable<object> Perf()
    {
        Fresh();
        Do("perf scene", () => { for (int i = 0; i < 20; i++) Scene.Add("plane" + i, Core.Mesh.Primitives.Plane(10, 10, 223, 223), new Vector3(i % 5 * 11, i / 5 * 11, 0)); });
        var st = Scene.Stats();
        Note($"{st.Verts:N0} verts, {st.Polys:N0} polys");
        FrameAll();
        yield return Frames(10); // GPU upload
        int start = stDts.Count;
        yield return Frames(120);
        var d = stDts.Skip(start).Where(x => x > 0).OrderBy(x => x).ToList();
        float med = d.Count > 0 ? d[d.Count / 2] * 1000 : 0, worst = d.Count > 0 ? d[^1] * 1000 : 0;
        Note($"frame time median {med:0.0} ms, worst {worst:0.0} ms (vsync on)");
        Expect(med < 40, "median frame under 40 ms with 1M vertices");
        // edit + undo on the big scene
        var sw = Stopwatch.StartNew();
        var o = Scene.Objects[0]; SelectOnly(o);
        Do("move", () => { o.Position += Vector3.UnitZ; o.Touch(); });
        long tEdit = sw.ElapsedMilliseconds; sw.Restart();
        DoUndo();
        long tUndo = sw.ElapsedMilliseconds;
        Expect(tEdit < 300 && tUndo < 500, $"edit {tEdit} ms, undo {tUndo} ms (one 50k-vertex object, scene of 1M)");
        SetLevel(SubObject.Polygon);
        start = stDts.Count;
        yield return Frames(30);
        d = stDts.Skip(start).Where(x => x > 0).OrderBy(x => x).ToList();
        Note($"edit mode on a 50k-vertex object: median {(d.Count > 0 ? d[d.Count / 2] * 1000 : 0):0.0} ms");
        yield return Shot("perf_1m");
        SetLevel(SubObject.Object);
    }

    IEnumerable<object> Windows()
    {
        prefsOpen = true; prefsSection = 7;
        yield return Frames(3);
        yield return Shot("prefs_gta_fivem");
        prefsOpen = false;
        OpenHub(0);
        yield return Frames(3);
        yield return Shot("hub_home");
        HubOpen = false;
        Expect(true, "windows drawn");
    }
}
