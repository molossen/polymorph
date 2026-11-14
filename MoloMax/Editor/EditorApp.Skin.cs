using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Bones, the Skin modifier and the weight brush (3ds Max Skin + Paint Weights / Blender Weight Paint):
/// Create > Bone adds a bone (a chain when a bone is selected), the Skin panel binds the mesh to the bones with automatic
/// weights, "Paint weights" shows the weights of the chosen bone in colour (blue 0 ... red 1) and paints them with the
/// mouse: left button adds, Ctrl removes, Shift smooths. Moving or turning a bone then bends the mesh.
/// </summary>
public sealed partial class EditorApp
{
    SceneObject wpObj; SkinModifier wpMod;
    int wpBone; float wpRadius = 0.25f, wpStrength = 0.35f; int wpMode;
    bool wpStroke; Vector3 wpHit; bool wpHitOk;
    List<int>[] wpNeighbours; int wpNbVersion = -1;
    readonly Dictionary<int, int> skinPoseHash = new();

    /// <summary>Create > Bone: at the 3D cursor pointing up, or, when a bone is selected, at its tip continuing it (a chain).</summary>
    public SceneObject AddBone()
    {
        var parent = Selected.Count == 1 && Scene.Find(Selected.First()) is { IsBone: true } p ? p : null;
        float len = parent?.BoneLength ?? 0.3f;
        var pos = parent != null ? Vector3.Transform(new Vector3(0, 0, parent.BoneLength), parent.World) : Cursor3D;
        var rot = parent?.Rotation ?? Quaternion.Identity;
        var o = AddObject("Bone", BoneMesh(len), pos);
        if (o == null) return null;
        Do("Bone settings", () =>
        {
            o.IsBone = true; o.BoneLength = len; o.Rotation = rot; o.Gta.Role = GtaRole.Ignore;
            o.WireColour = new Vector3(0.9f, 0.75f, 0.3f);
            if (parent != null) o.ParentId = parent.Id;
            o.Touch();
        });
        SelectOnly(o);
        SetStatus(parent != null ? "Bone added at the tip of the selected one (a chain). Add more, then put a Skin modifier on the mesh." : "Bone added. With it selected, Create > Bone again continues the chain.");
        return o;
    }

    /// <summary>The usual bone look: a thin double pyramid from the pivot (0) to the tip (+Z, length).</summary>
    static PolyMesh BoneMesh(float len)
    {
        var m = new PolyMesh();
        float w = len * 0.1f, h = len * 0.15f;
        int b = m.AddVertex(Vector3.Zero), t = m.AddVertex(new Vector3(0, 0, len));
        int a0 = m.AddVertex(new Vector3(-w, -w, h)), a1 = m.AddVertex(new Vector3(w, -w, h)), a2 = m.AddVertex(new Vector3(w, w, h)), a3 = m.AddVertex(new Vector3(-w, w, h));
        int[] ring = { a0, a1, a2, a3 };
        for (int i = 0; i < 4; i++)
        {
            int p = ring[i], q = ring[(i + 1) % 4];
            m.AddFace(new[] { b, q, p });
            m.AddFace(new[] { t, p, q });
        }
        MeshOps.RecalcNormals(m, null, false);
        return m;
    }

    List<SceneObject> SceneBones() => Scene.Objects.Where(x => x.IsBone && !x.Hidden).ToList();

    /// <summary>Every frame: the skinned objects whose bones moved are re-evaluated (their own version did not change).</summary>
    void SkinTick()
    {
        SkinModifier.WorldOf ??= id => Scene.Find(id)?.World;
        foreach (var o in Scene.Objects)
        {
            if (o.Modifiers == null || o.Modifiers.Count == 0) continue;
            foreach (var mod in o.Modifiers)
            {
                if (mod is not SkinModifier sk || sk.BoneIds.Count == 0) continue;
                var h = new HashCode(); h.Add(o.Position); h.Add(o.Rotation); h.Add(o.Scale);
                foreach (var id in sk.BoneIds) if (Scene.Find(id) is { } b) { h.Add(b.Position); h.Add(b.Rotation); h.Add(b.Scale); }
                int hv = h.ToHashCode();
                if (!skinPoseHash.TryGetValue(o.Id, out var old) || old != hv) { if (skinPoseHash.ContainsKey(o.Id)) o.Touch(); skinPoseHash[o.Id] = hv; }
            }
        }
    }

    /// <summary>Binds the Skin modifier to the scene's bones (or the selected ones) in their current pose, automatic weights.</summary>
    public void BindSkin(SceneObject o, SkinModifier sk, bool selectedOnly = false)
    {
        if (o == null || sk == null) return;
        var bones = (selectedOnly ? Selected.Select(Scene.Find).Where(b => b is { IsBone: true }) : SceneBones()).ToList();
        if (bones.Count == 0) { SetStatus("No bone: Create > Bone first (a chain: add one, keep it selected, add again)."); return; }
        int idx = o.Modifiers.IndexOf(sk);
        var input = ModifierStack.Evaluate(o.Mesh, o.Modifiers.Take(Math.Max(0, idx)).ToList(), false, false);
        Do("Bind skin", () =>
        {
            sk.OwnerId = o.Id;
            sk.Bind(input, o.World, bones.Select(b => (b.Id, b.World, b.BoneLength)).ToList());
            o.Touch();
        });
        skinPoseHash.Remove(o.Id);
        SetStatus($"Skin: bound to {bones.Count} bone(s), weights made from the distance to each bone. Paint them to fix the joints.");
    }

    bool SkinFields(SkinModifier m, Action<string> label)
    {
        bool c = false;
        var o = Current;
        int n = o?.Mesh.Verts.Count ?? 0;
        Ui.TextDisabled(m.BoneIds.Count == 0 ? "Not bound yet." : $"{m.BoneIds.Count} bone(s), {m.VertexCount:N0} points weighted");
        if (o != null && m.BoneIds.Count > 0 && o.Modifiers.IndexOf(m) is var ix && ix >= 0
            && ModifierStack.Evaluate(o.Mesh, o.Modifiers.Take(ix).ToList(), false, false).Verts.Count != m.VertexCount)
            Ui.TextDisabled("The mesh changed since: bind again.");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Bind (all bones)##sk", new Vector2(w2, 0)) && o != null) { BindSkin(o, m); c = true; }
        Theme.Tip("Binds the mesh to every bone of the scene in the pose they are in now, with automatic weights (nearest bones).");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Bind (selected)##sk", new Vector2(w2, 0)) && o != null) { BindSkin(o, m, true); c = true; }
        Theme.Tip("Same with the bones selected in the scene only.");
        if (m.BoneIds.Count > 0)
        {
            bool painting = modal.Kind == ModalKind.WeightPaint && wpMod == m;
            if (Theme.Toggle(painting ? "Painting weights (Esc)##skp" : "Paint weights##skp", painting, new Vector2(-1, 0)))
            {
                if (painting) modal.Kind = ModalKind.None; else BeginWeightPaint(o, m);
            }
            Theme.Tip("Shows the weights of the chosen bone (blue 0 ... red 1) and paints them: left adds, Ctrl removes, Shift smooths.");
            label("Bone");
            var names = m.BoneIds.Select(id => Scene.Find(id)?.Name ?? $"#{id}").ToArray();
            wpBone = Math.Clamp(wpBone, 0, Math.Max(0, names.Length - 1));
            ImGui.Combo("##skbone", ref wpBone, names, names.Length);
            label("Radius"); ImGui.DragFloat("##skr", ref wpRadius, 0.005f, 0.01f, 10, "%.3f m");
            label("Strength"); ImGui.SliderFloat("##sks", ref wpStrength, 0.01f, 1);
            label("Brush");
            float w3 = (ImGui.GetContentRegionAvail().X - 4) / 3;
            string[] mn = { "Add", "Remove", "Smooth" };
            for (int i = 0; i < 3; i++) { if (i > 0) ImGui.SameLine(0, 2); if (Theme.Toggle(mn[i] + "##skm", wpMode == i, new Vector2(w3, 0))) wpMode = i; }
        }
        return c;
    }

    public void BeginWeightPaint(SceneObject o, SkinModifier m)
    {
        if (o == null || m == null || m.BoneIds.Count == 0) return;
        if (ModalActive) CancelModal();
        if (Level != SubObject.Object) SetLevel(SubObject.Object);
        wpObj = o; wpMod = m; wpStroke = false;
        StartModalCommon(ModalKind.WeightPaint, Viewports[ActiveViewport], Vector3.Zero);
        WeightPaintHeader();
        SetStatus("Paint weights: left adds the bone, Ctrl removes, Shift smooths. [ ] radius. Esc to leave.");
    }

    void WeightPaintHeader()
    {
        string bone = wpMod != null && wpBone < wpMod.BoneIds.Count ? Scene.Find(wpMod.BoneIds[wpBone])?.Name : "?";
        modal.Header = $"Paint weights: {bone}   radius {wpRadius:0.00} m   {(Input.Ctrl ? "remove" : Input.Shift ? "smooth" : new[] { "add", "remove", "smooth" }[wpMode])}   [ ] radius   Esc leave";
    }

    /// <summary>The points the weights are painted on, where they are shown (the deformed mesh, same points as the skin's input).</summary>
    List<Vector3> WeightPoints(out PolyMesh shown)
    {
        shown = wpObj.Evaluated();
        if (shown.Verts.Count != wpMod.VertexCount)
        {
            int ix = wpObj.Modifiers.IndexOf(wpMod);
            shown = ModifierStack.Evaluate(wpObj.Mesh, wpObj.Modifiers.Take(ix + 1).ToList(), false, false);
        }
        return shown.Verts;
    }

    void WeightPaintKeys()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) wpRadius = MathF.Max(0.01f, wpRadius / 1.15f);
        if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) wpRadius *= 1.15f;
        WeightPaintHeader();
    }

    void WeightPaintMouse(Viewport hv)
    {
        if (wpObj == null || wpMod == null || !Scene.Objects.Contains(wpObj) || !wpObj.Modifiers.Contains(wpMod)) { modal.Kind = ModalKind.None; return; }
        if (Input.Pressed(MouseButtons.Right)) { modal.Kind = ModalKind.None; return; }
        wpHitOk = false;
        if (hv == null || ImGui.GetIO().WantCaptureMouse) { if (!Input.Down(MouseButtons.Left)) wpStroke = false; return; }
        var pts = WeightPoints(out var shown);
        var ray = RayAt(hv, Input.Mouse);
        Matrix4x4.Invert(wpObj.World, out var inv);
        if (Picking.PickFace(wpObj, ray, evaluated: true) is { } hit) { wpHit = Vector3.Transform(hit.Point, inv); wpHitOk = true; }
        if (Input.Pressed(MouseButtons.Left) && wpHitOk) { Undo.Record("Paint weights", Scene); wpStroke = true; }
        if (!Input.Down(MouseButtons.Left)) wpStroke = false;
        if (wpStroke && wpHitOk)
        {
            if (wpNeighbours == null || wpNbVersion != wpObj.Version || wpNeighbours.Length != pts.Count)
            {
                var vf = shown.BuildVertexFaces();
                wpNeighbours = new List<int>[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    var s = new HashSet<int>();
                    if (i < vf.Length) foreach (var f in vf[i]) foreach (var v in shown.Faces[f].V) if (v != i) s.Add(v);
                    wpNeighbours[i] = s.ToList();
                }
                wpNbVersion = wpObj.Version;
            }
            int mode = Input.Ctrl ? 1 : Input.Shift ? 2 : wpMode;
            // radius in world metres, the points are in object space
            float scale = MathF.Max(1e-6f, (wpObj.Scale.X + wpObj.Scale.Y + wpObj.Scale.Z) / 3);
            int n = wpMod.Paint(pts.ToList(), wpHit, wpRadius / scale, wpBone, wpStrength * 0.5f, mode, wpNeighbours);
            if (n > 0) { wpObj.Touch(); wpNbVersion = wpObj.Version; Dirty = true; }
        }
        WeightPaintHeader();
    }

    /// <summary>The weights of the chosen bone on every point (blue 0, green, yellow, red 1) and the brush circle.</summary>
    void WeightPaintOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind != ModalKind.WeightPaint || wpObj == null || wpMod == null) return;
        var pts = WeightPoints(out _);
        var w = wpObj.World;
        var wvp = w * vp.Camera.ViewProj(vp.W / MathF.Max(vp.H, 1));
        static uint Heat(float t)
        {
            t = Math.Clamp(t, 0, 1);
            float r = Math.Clamp(t * 2 - 0.5f, 0, 1), g = t < 0.5f ? t * 2 : 2 - t * 2, b = Math.Clamp(1 - t * 2, 0, 1);
            return 0xFF000000u | ((uint)(b * 255) << 16) | ((uint)(MathF.Max(g, 0.15f) * 255) << 8) | (uint)(r * 255);
        }
        int step = Math.Max(1, pts.Count / 40000);
        for (int i = 0; i < pts.Count && i < wpMod.VertexCount; i += step)
        {
            if (!Camera.Project(wvp, pts[i], vp.W, vp.H, out var s)) continue;
            var p = new Vector2(vp.X + s.X, vp.Y + s.Y);
            dl.AddRectFilled(p - new Vector2(2.5f), p + new Vector2(2.5f), Heat(wpMod.Weight(i, wpBone)));
        }
        if (wpHitOk)
        {
            var c = Vector3.Transform(wpHit, w);
            var cam = vp.Camera; var right = Vector3.Normalize(Vector3.Cross(cam.Forward, Vector3.UnitZ)); if (float.IsNaN(right.X)) right = Vector3.UnitX;
            if (cam.Project(c, vp.W, vp.H, out var sc, out _) && cam.Project(c + right * wpRadius, vp.W, vp.H, out var se, out _))
                dl.AddCircle(new Vector2(vp.X + sc.X, vp.Y + sc.Y), MathF.Max(3, Vector2.Distance(sc, se)), 0xFFFFFFFF, 48, 1.5f);
        }
    }
}
