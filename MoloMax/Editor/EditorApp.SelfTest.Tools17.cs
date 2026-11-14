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
        FrameSelection(vp); yield return Frames(4);
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
        foreach (var x in UClick(UvToScreen(target) + new Vector2(5, 4))) yield return x;
        bool allAtPoint = uvCorn.Count > 0 && uvCorn.All(k => Vector2.Distance(m.Faces[(int)(k >> 8)].UV[(int)(k & 0xFF)], target) < 1e-5f);
        Expect(allAtPoint && uvCorn.Contains(CK(0, 0)), $"a click near the point takes it ({uvCorn.Count} corner(s) at that place)");
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
        PostKey(Keys.D2, true); yield return Frames(1); PostKey(Keys.D2, false); yield return Frames(2);
        uvCorn.Clear();
        var ea = m.Faces[1].UV[0]; var eb = m.Faces[1].UV[1];
        foreach (var x in UClick(UvToScreen((ea + eb) / 2) + new Vector2(0, 5))) yield return x;
        Expect(uvMode == 1 && uvCorn.Count >= 2 && uvCorn.Contains(CK(1, 0)) && uvCorn.Contains(CK(1, 1)), $"edge mode: the edge under the mouse ({uvCorn.Count} corners)");
        // ---- face mode: a click just outside a face, on its edge, still takes it
        PostKey(Keys.D3, true); yield return Frames(1); PostKey(Keys.D3, false); yield return Frames(2);
        SubSel(c).Faces.Clear();
        var fc = Vector2.Zero; foreach (var u in m.Faces[2].UV) fc += u; fc /= m.Faces[2].UV.Length;
        foreach (var x in UClick(UvToScreen(fc))) yield return x;
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
