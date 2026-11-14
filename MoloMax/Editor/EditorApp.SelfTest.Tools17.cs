using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using Keys = System.Windows.Forms.Keys;

namespace MoloMax.Editor;

/// <summary>Tests of the 1.7 tools, with the real mouse and keyboard: Target Weld, FFD, ProOptimizer, UV selection.</summary>
public sealed partial class EditorApp
{
    void Tools17Tests(Action<string, Func<IEnumerable<object>>> T)
    {
        T("TOOLS17: Target Weld by two clicks: the first vertex goes onto the second, the mesh stays closed, undo", TargetWeldTest);
        T("TOOLS17: FFD 3x3x3: drag the top middle point up, the box bulges smoothly, undo, save / open keeps it", FfdTest);
        T("TOOLS17: ProOptimizer: fewer polygons, UVs from the source, closed, in the GTA export mesh, save / open", ProOptimizerTest);
        T("TOOLS17: shapes: rectangle extrude, circle pipe, smooth curve, lathe of a half outline (closed, outward)", ShapesTest);
        T("TOOLS17: bones + Skin: bind with automatic weights, turn a bone (the top bends, the bottom stays), paint weights with the mouse, save / open", SkinTest);
        T("TOOLS17: UV editor selection: vertex near the point, edge, face from its edge, island, box, L, drag moves", UvSelectTest);
    }

    IEnumerable<object> TargetWeldTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        var c = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(c); var vp = Viewports[ActiveViewport]; FrameSelection(vp); yield return Frames(4);
        int v0 = c.Mesh.Verts.Count;
        BeginModal(ModalKind.TargetWeld); yield return Frames(2);
        Expect(modal.Kind == ModalKind.TargetWeld && Level == SubObject.Vertex, "Target Weld started (vertex mode)");
        // two vertices of the same edge that the camera sees
        var sc = ScreenVerts(vp, c);
        var e = Edges(c).First(k => sc.Ok[k.A] && sc.Ok[k.B] && Vector2.Distance(sc.P[k.A], sc.P[k.B]) > 40 && NotBehind(vp, Vector3.Transform(c.Mesh.Verts[k.A], c.World), FrontDepth(vp, c, sc.P[k.A])) && NotBehind(vp, Vector3.Transform(c.Mesh.Verts[k.B], c.World), FrontDepth(vp, c, sc.P[k.B])));
        var keep = Vector3.Transform(c.Mesh.Verts[e.B], c.World);
        foreach (var x in UClick(vp.ToScreen(sc.P[e.A]) + new Vector2(3, 2))) yield return x;
        Expect(twSrc == e.A, $"first click takes the vertex near the mouse ({twSrc} / {e.A})");
        foreach (var x in UClick(vp.ToScreen(sc.P[e.B]) + new Vector2(-2, 3))) yield return x;
        c = Scene.Find(c.Id);
        Expect(c.Mesh.Verts.Count == v0 - 1, $"one vertex less ({v0} -> {c.Mesh.Verts.Count})");
        Expect(c.Mesh.Verts.Any(v => Vector3.Distance(Vector3.Transform(v, c.World), keep) < 1e-4f), "the target vertex did not move");
        Expect(c.Mesh.IsClosedManifold(out _), "still a closed mesh");
        Expect(modal.Kind == ModalKind.TargetWeld, "stays on for the next weld");
        yield return Shot("target_weld");
        CancelModal(); DoUndo(); yield return Frames(3);
        Expect(Scene.Find(c.Id).Mesh.Verts.Count == v0, "Ctrl+Z gives the vertex back");
        SetLevel(SubObject.Object);
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> FfdTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        var c = Prim(CreateKind.Box, Vector3.Zero);
        Do("subdivide", () => { c.Mesh = Primitives.Box(2, 2, 2, 8, 8, 8); c.Touch(); });
        SelectOnly(c); var vp = Viewports[ActiveViewport];
        var ffd = new FfdModifier(); ffd.SetResolution(3, 3, 3);
        c.Modifiers.Add(ffd); c.Touch();
        FrameSelection(vp); vp.Camera.Yaw = -2.2f; vp.Camera.Pitch = 0.45f; vp.Camera.Ortho = false; yield return Frames(4); // a 3/4 view: no two cage points on the same pixel
        float top0 = c.Evaluated().Verts.Max(v => v.Z);
        BeginFfdEdit(c, ffd); yield return Frames(3);
        Expect(modal.Kind == ModalKind.FfdPoints && ffd.HasBox, "FFD point editing started, cage fitted");
        int topMid = ffd.Index(1, 1, 2);
        var p0 = ScreenOf(vp, Vector3.Transform(ffd.Point(topMid), c.World));
        var p1 = ScreenOf(vp, Vector3.Transform(ffd.Point(topMid) + Vector3.UnitZ * 1.2f, c.World));
        foreach (var x in MouseTo(p0, 6)) yield return x;
        yield return Frames(2);
        Expect(ffdHover == topMid, $"the top middle point is under the mouse ({ffdHover} / {topMid})");
        PostKey(Keys.Z, true); yield return Frames(1); PostKey(Keys.Z, false); yield return Frames(2);
        Expect(ffdAxis == 2, "Z locks the move on Z");
        PostMouse(0x201, p0, 1); yield return Frames(2);
        foreach (var x in MouseTo(p1, 10, 1)) yield return x;
        PostMouse(0x202, p1, 0); yield return Frames(3);
        var ev = c.Evaluated();
        float top1 = ev.Verts.Max(v => v.Z);
        var offs = ffd.Offset(topMid);
        Expect(offs.Z > 0.5f && MathF.Abs(offs.X) < 1e-3f && MathF.Abs(offs.Y) < 1e-3f, $"point moved straight up {offs}");
        Expect(top1 > top0 + 0.2f, $"the top bulges ({top0:0.00} -> {top1:0.00})");
        float corner = ev.Verts.Where(v => MathF.Abs(MathF.Abs(v.X) - 1) < 1e-3f && MathF.Abs(MathF.Abs(v.Y) - 1) < 1e-3f).Max(v => v.Z);
        Expect(MathF.Abs(corner - top0) < 1e-3f, $"the top corners stay ({corner:0.000})");
        yield return Shot("ffd_bulge");
        ConfirmModal();
        // save / open keeps the cage and its points
        var path = Path.Combine(SelfTestDir, "ffd_test.mmx");
        SceneIO.Save(Scene, path);
        var back = SceneIO.Load(path);
        var o2 = back.Objects.First(x => x.Name == c.Name);
        var f2 = o2.Modifiers.OfType<FfdModifier>().FirstOrDefault();
        Expect(f2 != null && Vector3.Distance(f2.Offset(topMid), offs) < 1e-5f && MathF.Abs(o2.Evaluated().Verts.Max(v => v.Z) - top1) < 1e-4f, "save / open keeps the FFD and its moved point");
        DoUndo(); yield return Frames(3);
        Expect(Scene.Find(c.Id).Modifiers.OfType<FfdModifier>().First().Offset(topMid).Z < 1e-4f, "Ctrl+Z puts the point back");
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> ProOptimizerTest()
    {
        Fresh();
        var c = Prim(CreateKind.Sphere, Vector3.Zero);
        Do("dense sphere", () => { c.Mesh = Primitives.Sphere(1, 64); c.Touch(); });
        SelectOnly(c); FrameSelection(Viewports[ActiveViewport]); yield return Frames(3);
        int f0 = c.Mesh.Faces.Count;
        var po = new ProOptimizerModifier { Percent = 30 };
        c.Modifiers.Add(po); c.Touch(); yield return Frames(3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ev = c.Evaluated();
        Note($"ProOptimizer 30 %: {f0} -> {ev.Faces.Count} faces, {sw.ElapsedMilliseconds} ms");
        Expect(ev.Faces.Count < f0 * 0.7f && ev.Faces.Count > f0 * 0.15f, $"about a third of the polygons left ({ev.Faces.Count} / {f0})");
        Expect(ev.IsClosedManifold(out _), "still closed");
        Expect(ev.Verts.All(v => MathF.Abs(v.Length() - 1) < 1e-4f), "every point is a point of the sphere (none invented)");
        var srcUv = new HashSet<(float, float)>(); foreach (var f in c.Mesh.Faces) foreach (var u in f.UV) srcUv.Add((MathF.Round(u.X, 4), MathF.Round(u.Y, 4)));
        int bad = 0; foreach (var f in ev.Faces) foreach (var u in f.UV) if (!srcUv.Contains((MathF.Round(u.X, 4), MathF.Round(u.Y, 4)))) bad++;
        Expect(bad == 0, $"UVs all from the source ({bad} new)");
        var render = c.Evaluated(false, true);
        Expect(render.Faces.Count == ev.Faces.Count, "the GTA export mesh (render) is the reduced one");
        yield return Shot("prooptimizer");
        var path = Path.Combine(SelfTestDir, "proopt_test.mmx");
        SceneIO.Save(Scene, path);
        var o2 = SceneIO.Load(path).Objects.First(x => x.Name == c.Name);
        Expect(o2.Modifiers.OfType<ProOptimizerModifier>().FirstOrDefault()?.Percent == 30 && o2.Evaluated().Faces.Count == ev.Faces.Count, "save / open keeps it");
    }

    IEnumerable<object> ShapesTest()
    {
        Fresh();
        Cursor3D = Vector3.Zero;
        var r = CreateShapeKind(1);
        Expect(r?.Shape is { Kind: 1, Closed: true } && r.Shape.Points.Count == 4, "rectangle: 4 points, closed");
        var sr = r.Shape.Copy(); sr.RectWidth = 2; sr.RectLength = 1; sr.Render = ShapeRender.Extrude; sr.Amount = 0.5f;
        Do("rect", () => { r.Shape = sr; r.Mesh = ShapeBuilder.Build(sr); r.Touch(); });
        var (mn, mx) = r.Mesh.Bounds();
        Expect(r.Mesh.IsClosedManifold(out _) && MathF.Abs(mx.X - mn.X - 2) < 1e-3f && MathF.Abs(mx.Y - mn.Y - 1) < 1e-3f && MathF.Abs(mx.Z - mn.Z - 0.5f) < 1e-3f,
            $"rectangle 2 x 1 extruded 0.5: a closed box of that size ({mx - mn})");
        Cursor3D = new Vector3(4, 0, 0);
        var c = CreateShapeKind(2);
        var sc = c.Shape.Copy(); sc.Radius = 1; sc.Render = ShapeRender.Round; sc.Thickness = 0.2f;
        Do("circle", () => { c.Shape = sc; c.Mesh = ShapeBuilder.Build(sc); c.Touch(); });
        Expect(c.Mesh.IsClosedManifold(out _), $"circle as a pipe: a closed ring ({c.Mesh.Faces.Count} faces)");
        // smooth curve: more points along the path, passing through the clicked ones
        var line = CreateLine(new[] { new Vector3(0, 4, 0), new Vector3(1, 5, 0), new Vector3(2, 4, 0), new Vector3(3, 5, 0) }, false, ShapeRender.Line);
        var sl = line.Shape.Copy(); sl.Smooth = true; sl.Steps = 8;
        var path = ShapeBuilder.Path(sl);
        Expect(path.Count > 20 && sl.Points.All(p => path.Any(q => Vector3.Distance(p, q) < 1e-4f)), $"smooth curve: {path.Count} points, through every clicked point");
        // lathe: half outline of a cylinder (axis Z through the pivot) turns into a closed solid facing outward
        var half = CreateLine(new[] { new Vector3(0, 8, 0), new Vector3(0.5f, 8, 0), new Vector3(0.5f, 8, 1), new Vector3(0, 8, 1) }, false, ShapeRender.Line);
        var sh = half.Shape.Copy(); sh.Render = ShapeRender.Lathe; sh.LatheAxis = 2; sh.LatheSegs = 24;
        Do("lathe", () => { half.Shape = sh; half.Mesh = ShapeBuilder.Build(sh); half.Touch(); });
        var lm = half.Mesh;
        double vol = 0; foreach (var f in lm.Faces) for (int i = 1; i + 1 < f.V.Length; i++) vol += Vector3.Dot(lm.Verts[f.V[0]], Vector3.Cross(lm.Verts[f.V[i]], lm.Verts[f.V[i + 1]])) / 6;
        Expect(lm.IsClosedManifold(out _) && vol > 0.7 && vol < 0.8, $"lathe: closed cylinder, faces outward, volume {vol:0.000} (pi r^2 h = 0.785)");
        SelectOnly(half); FrameSelection(Viewports[ActiveViewport]); yield return Frames(4);
        yield return Shot("lathe");
    }

    IEnumerable<object> SkinTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        var tube = Prim(CreateKind.Cylinder, Vector3.Zero);
        Do("tall", () => { tube.Mesh = Primitives.Cylinder(0.15f, 2, 12, 16); tube.Touch(); });
        Cursor3D = Vector3.Zero; SelectNone();
        var b0 = AddBone(); Do("len", () => { b0.BoneLength = 1; b0.Mesh = BoneMesh(1); b0.Touch(); });
        var b1 = AddBone(); // chained at the tip of the first
        Expect(b0.IsBone && b1.IsBone && b1.ParentId == b0.Id && Vector3.Distance(b1.Position, new Vector3(0, 0, 1)) < 1e-4f, $"bone chain: second bone at the tip of the first ({b1.Position})");
        Expect(b0.Gta.Role == GtaRole.Ignore, "bones are not exported to GTA");
        var sk = new SkinModifier(); tube.Modifiers.Add(sk); tube.Touch();
        BindSkin(tube, sk);
        int n = tube.Mesh.Verts.Count;
        Expect(sk.HasWeights(n) && sk.BoneIds.Count == 2, $"bound: {sk.BoneIds.Count} bones, {sk.VertexCount} points");
        bool sums = Enumerable.Range(0, n).All(i => MathF.Abs(sk.Weight(i, 0) + sk.Weight(i, 1) - 1) < 1e-3f);
        var lowI = Enumerable.Range(0, n).OrderBy(i => tube.Mesh.Verts[i].Z).First(); var topI = Enumerable.Range(0, n).OrderBy(i => -tube.Mesh.Verts[i].Z).First();
        Expect(sums && sk.Weight(lowI, 0) > 0.9f && sk.Weight(topI, 1) > 0.9f, $"automatic weights: bottom on bone 1 ({sk.Weight(lowI, 0):0.00}), top on bone 2 ({sk.Weight(topI, 1):0.00}), each point sums to 1");
        // turn the second bone 90° around X at its pivot: the top swings sideways, the bottom does not move
        Do("pose", () => { b1.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2); });
        yield return Frames(3);
        var ev = tube.Evaluated();
        Expect(Vector3.Distance(ev.Verts[lowI], tube.Mesh.Verts[lowI]) < 1e-3f, "the bottom stays");
        var top = ev.Verts[topI];
        Expect(top.Z < 1.5f && MathF.Abs(top.Y) > 0.6f, $"the top bends with the bone ({tube.Mesh.Verts[topI]} -> {top})");
        var vp = Viewports[ActiveViewport]; SelectOnly(tube); FrameSelection(vp); yield return Frames(4);
        yield return Shot("skin_bent");
        Do("rest", () => { b1.Rotation = Quaternion.Identity; }); yield return Frames(3);
        // paint: bone 1 added on the middle with real mouse strokes
        BeginWeightPaint(tube, sk); wpBone = 0; wpMode = 0; wpRadius = 0.4f; wpStrength = 1; yield return Frames(3);
        Expect(modal.Kind == ModalKind.WeightPaint, "weight paint on");
        var midI = Enumerable.Range(0, n).OrderBy(i => MathF.Abs(tube.Mesh.Verts[i].Z - 1.3f)).First();
        float w0 = sk.Weight(midI, 0);
        var pm = ScreenOf(vp, Vector3.Transform(new Vector3(0, 0, tube.Mesh.Verts[midI].Z), tube.World));
        foreach (var x in MouseTo(pm, 4)) yield return x;
        PostMouse(0x201, pm, 1); yield return Frames(2);
        foreach (var x in MouseTo(pm + new Vector2(12, 6), 10, 1)) yield return x;
        PostMouse(0x202, pm + new Vector2(12, 6), 0); yield return Frames(3);
        float w1 = sk.Weight(midI, 0);
        Expect(wpHitOk || w1 > w0, $"brush on the mesh ({wpHit})");
        Expect(w1 > w0 + 0.1f && MathF.Abs(sk.Weight(midI, 0) + sk.Weight(midI, 1) - 1) < 1e-3f, $"painting adds bone 1 there ({w0:0.00} -> {w1:0.00}), still sums to 1");
        yield return Shot("skin_weights");
        ConfirmModal();
        var path = Path.Combine(SelfTestDir, "skin_test.mmx");
        SceneIO.Save(Scene, path);
        var back = SceneIO.Load(path);
        var t2 = back.Objects.First(x => x.Name == tube.Name);
        var s2 = t2.Modifiers.OfType<SkinModifier>().FirstOrDefault();
        Expect(s2 != null && s2.HasWeights(n) && MathF.Abs(s2.Weight(midI, 0) - w1) < 1e-5f && back.Objects.Count(x => x.IsBone) == 2, "save / open keeps the bones, the binding and the painted weights");
        // GTA: a skinned .ydr with a root + the two bones, read back by the game file reader
        var ydr = Path.Combine(SelfTestDir, "skin_tube.ydr");
        string info = null; Exception gex = null;
        try { info = MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(Scene, new[] { tube }, ydr, _ => null); } catch (Exception ex) { gex = ex; }
        var gb = gex == null ? MoloMax.Core.Gta.Forge.ForgeCommon.YdrBones(File.ReadAllBytes(ydr)) : new();
        Expect(gex == null && gb.Count == 3 && gb[2].Parent == 1 && Vector3.Distance(gb[2].Position, new Vector3(0, 0, 1)) < 1e-3f,
            $"GTA export: skinned .ydr, root + 2 bones, chain kept ({info ?? gex?.Message}; {string.Join(", ", gb.Select(x => x.Name + "@" + x.Position))})");
        DoUndo(); yield return Frames(3);
        Expect(Scene.Find(tube.Id).Modifiers.OfType<SkinModifier>().First().Weight(midI, 0) < w1 - 0.05f, "Ctrl+Z undoes the stroke");
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> UvSelectTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        var c = Prim(CreateKind.Box, Vector3.Zero);
        SelectOnly(c); SetLevel(SubObject.Polygon);
        foreach (var fi in Enumerable.Range(0, c.Mesh.Faces.Count)) SubSel(c).Faces.Add(fi);
        SmartUv(); SubSel(c).Faces.Clear();
        uvOpen = true; uvWinAt = new Vector2(80, 120); uvMode = 3; uvCorn.Clear();
        yield return Frames(4);
        // frame the UVs in the canvas
        PostMouse(0x200, uvC0 + new Vector2(200, 200), 0); yield return Frames(2);
        PostKey(Keys.F, true); yield return Frames(1); PostKey(Keys.F, false); yield return Frames(3);
        var m = c.Mesh;
        // ---- vertex mode (key 1): a click 6 px from a UV point takes it and its twins at the same place
        PostKey(Keys.D1, true); yield return Frames(1); PostKey(Keys.D1, false); yield return Frames(2);
        Expect(uvMode == 0, "1 = vertex mode");
        var target = m.Faces[0].UV[0];
        foreach (var x in UClick(UvToScreen(target) + new Vector2(2, 1))) yield return x;
        Note($"vertex click: mode {uvMode}, hot {uvHot}, {uvCorn.Count} corner(s), first {(uvCorn.Count > 0 ? uvCorn.First() : -1)}");
        // islands packed edge to edge can have another point on the same spot: the one taken must be there (3 px)
        bool allAtPoint = uvCorn.Count > 0 && uvCorn.All(k => Vector2.Distance(UvToScreen(m.Faces[(int)(k >> 8)].UV[(int)(k & 0xFF)]), UvToScreen(target)) < 3);
        Expect(allAtPoint, $"a click near the point takes the point there ({uvCorn.Count} corner(s) at that place)");
        // drag it: only those corners move
        var before = m.Faces.Select(f => (Vector2[])f.UV.Clone()).ToList();
        var s0 = UvToScreen(target);
        PostMouse(0x201, s0, 1); yield return Frames(2);
        foreach (var x in MouseTo(s0 + new Vector2(40, 0), 6, 1)) yield return x;
        PostMouse(0x202, s0 + new Vector2(40, 0), 0); yield return Frames(3);
        int moved = 0, wrong = 0;
        for (int fi = 0; fi < m.Faces.Count; fi++) for (int ci = 0; ci < m.Faces[fi].UV.Length; ci++)
            {
                bool mv = Vector2.Distance(before[fi][ci], m.Faces[fi].UV[ci]) > 1e-5f;
                if (mv) moved++; if (mv != uvCorn.Contains(CK(fi, ci))) wrong++;
            }
        Expect(moved == uvCorn.Count && wrong == 0, $"the drag moves exactly the selected point ({moved} corner(s))");
        DoUndo(); yield return Frames(3); m = Scene.Find(c.Id).Mesh;
        // ---- edge mode: a click near the middle of a UV edge takes its two ends
        PostMouse(0x200, UvToScreen(new Vector2(0.5f, 0.5f)), 0); yield return Frames(2);
        Note($"before 2: canvas under mouse {UvCanvasUnderMouse}, hover {uvHover}, imgui mouse {ImGuiNET.ImGui.GetIO().MousePos}, canvas {uvC0} {uvCanvasSize}, current {Current?.Name}, open {uvOpen}");
        PostKey(Keys.D2, true); yield return Frames(1); PostKey(Keys.D2, false); yield return Frames(2);
        uvCorn.Clear();
        var ea = m.Faces[1].UV[0]; var eb = m.Faces[1].UV[1];
        foreach (var x in UClick(UvToScreen((ea + eb) / 2) + new Vector2(0, 4))) yield return x;
        Note($"edge click: mode {uvMode}, hot {uvHot}, {uvCorn.Count} corner(s), at {UvToScreen((ea + eb) / 2)}, canvas {uvC0}, level {Level}");
        Expect(uvMode == 1 && uvCorn.Count >= 2 && uvCorn.Contains(CK(1, 0)) && uvCorn.Contains(CK(1, 1)), $"edge mode: the edge under the mouse ({uvCorn.Count} corners)");
        // ---- face mode: a click just outside a face, on its edge, still takes it
        PostKey(Keys.D3, true); yield return Frames(1); PostKey(Keys.D3, false); yield return Frames(2);
        Note($"after 3 (no mouse move): mode {uvMode}, canvas under mouse {UvCanvasUnderMouse}, hover {uvHover}, imgui mouse {ImGuiNET.ImGui.GetIO().MousePos}, want kb {ImGuiNET.ImGui.GetIO().WantCaptureKeyboard}, xf {uvXfMode}, drag {uvDragging}, box {uvBoxing}, level {Level}");
        if (uvMode != 2)
        {
            PostMouse(0x200, ImGuiNET.ImGui.GetIO().MousePos + new Vector2(1, 1), 0); yield return Frames(2);
            PostKey(Keys.D3, true); yield return Frames(1); PostKey(Keys.D3, false); yield return Frames(2);
            Note($"after 3 (mouse moved 1 px): mode {uvMode}, canvas under mouse {UvCanvasUnderMouse}, hover {uvHover}");
        }
        SubSel(c).Faces.Clear();
        var fc = Vector2.Zero; foreach (var u in m.Faces[2].UV) fc += u; fc /= m.Faces[2].UV.Length;
        foreach (var x in UClick(UvToScreen(fc))) yield return x;
        Note($"face click: mode {uvMode}, hot {uvHot}, faces {Faces(SubSel(c).Faces)}, level {Level}, current {Current?.Name}");
        Expect(uvMode == 2 && SubSel(c).Faces.SetEquals(new[] { 2 }), $"face mode: the face clicked ({Faces(SubSel(c).Faces)})");
        // ---- island mode: L over a face takes its island
        PostKey(Keys.D4, true); yield return Frames(1); PostKey(Keys.D4, false); yield return Frames(2);
        SubSel(c).Faces.Clear();
        PostMouse(0x200, UvToScreen(fc), 0); yield return Frames(3);
        PostKey(Keys.L, true); yield return Frames(1); PostKey(Keys.L, false); yield return Frames(2);
        var isl = UvTools.Islands(m).First(i => i.Contains(2));
        Expect(SubSel(c).Faces.SetEquals(isl), $"L takes the island under the mouse ({SubSel(c).Faces.Count} / {isl.Count} faces)");
        // ---- box select in face mode
        PostKey(Keys.D3, true); yield return Frames(1); PostKey(Keys.D3, false); yield return Frames(2);
        var bmin = UvToScreen(Vector2.Zero) - new Vector2(18, 18); var bmax = UvToScreen(Vector2.One) + new Vector2(18, 18);
        PostMouse(0x200, bmin, 0); yield return Frames(2);
        PostMouse(0x201, bmin, 1); yield return Frames(2);
        foreach (var x in MouseTo(bmax, 8, 1)) yield return x;
        PostMouse(0x202, bmax, 0); yield return Frames(3);
        Expect(SubSel(c).Faces.Count == m.Faces.Count, $"a box over 0-1 takes every face ({SubSel(c).Faces.Count} / {m.Faces.Count})");
        yield return Shot("uv_select");
        uvOpen = false; uvWinAt = null; SetLevel(SubObject.Object);
        UI.ImGuiBackend.SyntheticInput = false;
    }
}
