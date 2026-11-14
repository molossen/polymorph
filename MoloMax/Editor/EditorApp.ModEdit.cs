using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Edit Poly and Unwrap UVW as modifiers: entering one swaps the object's mesh for the modifier's working mesh,
/// so every edit tool (extrude, bevel, inset, connect, cut, weld, chamfer, loops / rings, the UV editor) works inside the stack.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>The modifier picked in the stack, per object (Tab edits it when it is an Edit Poly / Unwrap UVW).</summary>
    readonly Dictionary<int, int> activeModifier = new();

    int ActiveModifierIndex(SceneObject o) => o != null && activeModifier.TryGetValue(o.Id, out var i) && i < o.FullStack.Count ? i : -1;

    /// <summary>Starts editing the editable modifier <paramref name="index"/> at a sub-object level.</summary>
    public void EditModifier(SceneObject o, int index, SubObject level)
    {
        if (o == null || index < 0 || index >= o.FullStack.Count || o.FullStack[index] is not IEditableModifier) return;
        if (ModalActive) CancelModal();
        if (o.ModEdit == null || o.ModEdit.Index != index)
        {
            var name = o.FullStack[index].Name;
            Do("Edit " + name, () => { o.BeginModifierEdit(index); subSel.Remove(o.Id); });
        }
        activeModifier[o.Id] = index;
        if (!Selected.Contains(o.Id) || ActiveId != o.Id) { Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged(); }
        if (Level == SubObject.Object || Level != level) { Level = level; lastEditLevel = level; }
        SetStatus($"Editing {o.FullStack[index].Name}: the tools work on this step of the stack. Tab or Done when finished.");
    }

    /// <summary>Ends a modifier edit (the modifier keeps the result).</summary>
    public void EndModifierEdit(SceneObject o, bool toObjectMode = true)
    {
        if (o?.ModEdit == null) return;
        if (ModalActive) CancelModal();
        o.CommitModifierEdit();
        subSel.Remove(o.Id);
        if (toObjectMode && Current == o) Level = SubObject.Object;
    }

    /// <summary>Called every frame: a modifier edit ends when its object leaves edit mode or stops being the edited object.</summary>
    void ModEditGuard()
    {
        foreach (var o in Scene.Objects)
            if (o.ModEdit != null && (o != Current || Level == SubObject.Object)) { o.CommitModifierEdit(); subSel.Remove(o.Id); }
    }

    // ------------------------------------------------------------------------------- panels

    static readonly SubObject[] PolyLevels = { SubObject.Vertex, SubObject.Edge, SubObject.Border, SubObject.Polygon, SubObject.Element };

    void EditPolyFields(SceneObject o, int index, EditPolyModifier ep)
    {
        bool editing = o.ModEdit != null && o.ModEdit.Index == index && Current == o && Level != SubObject.Object;
        float bw = (ImGui.GetContentRegionAvail().X - 8) / 5;
        for (int k = 0; k < PolyLevels.Length; k++)
        {
            if (k > 0) ImGui.SameLine(0, 2);
            var lv = PolyLevels[k];
            string lbl = lv switch { SubObject.Vertex => "Vertex", SubObject.Edge => "Edge", SubObject.Border => "Border", SubObject.Polygon => "Poly", _ => "Element" };
            if (Theme.Toggle(lbl + "##eplv", editing && Level == lv, new Vector2(bw, 0)))
            {
                if (editing && Level == lv) EndModifierEdit(o);
                else EditModifier(o, index, lv);
            }
        }
        if (editing)
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            Ui.Label("Edit Geometry", Theme.TextDim);
            if (ImGui.Button("Extrude##ep", new Vector2(w2, 0))) BeginModal(ModalKind.Extrude); ImGui.SameLine(0, 4);
            if (ImGui.Button("Bevel##ep", new Vector2(w2, 0))) BevelFaces(0.1f, -0.05f, false);
            if (ImGui.Button("Inset##ep", new Vector2(w2, 0))) BeginModal(ModalKind.Inset); ImGui.SameLine(0, 4);
            if (ImGui.Button("Chamfer##ep", new Vector2(w2, 0))) BeginModal(ModalKind.Chamfer);
            if (ImGui.Button("Connect##ep", new Vector2(w2, 0))) ConnectEdges(); ImGui.SameLine(0, 4);
            if (ImGui.Button("Cut##ep", new Vector2(w2, 0))) BeginModal(ModalKind.Knife);
            if (ImGui.Button("QuickSlice##ep", new Vector2(-1, 0))) BeginModal(ModalKind.QuickSlice);
            Theme.Tip("Two clicks: slices the whole object (or the selected polygons) along the line, like a quick slice.");
            if (ImGui.Button("Weld##ep", new Vector2(w2, 0))) Weld(0.01f); ImGui.SameLine(0, 4);
            if (ImGui.Button("Collapse##ep", new Vector2(w2, 0))) Collapse();
            if (ImGui.Button("Target Weld##ep", new Vector2(-1, 0))) BeginModal(ModalKind.TargetWeld);
            Theme.Tip("Click a vertex, then the vertex to weld it onto (that one stays put).");
            MakePlanarUi();
            Ui.Label("Selection", Theme.TextDim);
            if (ImGui.Button("Loop##ep", new Vector2(w2, 0))) Loop(); ImGui.SameLine(0, 4);
            if (ImGui.Button("Ring##ep", new Vector2(w2, 0))) Ring();
            var s = SubSel(o);
            Ui.TextDisabled($"{o.Mesh.Verts.Count:N0} verts · {o.Mesh.Faces.Count:N0} polys · {s.Count(Level)} selected");
            if (ImGui.Button("Done##ep", new Vector2(-1, 0))) EndModifierEdit(o);
            Theme.Tip("Ends the edit (Tab does the same). The result stays in this modifier.");
        }
        else
        {
            Ui.TextDisabled(ep.HasEdit ? $"Holds an edit: {ep.Result.FN.Length:N0} polys. Pick a level to go on editing." : "No edit yet: pick a level (or select this modifier and press Tab).");
            if (ep.HasEdit && ImGui.Button("Reset edits##ep", new Vector2(-1, 0)))
                Do("Reset Edit Poly", () => { ep.Result = null; ep.Origin = null; ep.BasePos = null; o.Touch(); });
        }
        if (ep.Warning != null) Ui.TextColored(new Vector4(1f, 0.6f, 0.25f, 1), ep.Warning);
    }

    void UnwrapFields(SceneObject o, int index, UnwrapUvwModifier uw)
    {
        bool editing = o.ModEdit != null && o.ModEdit.Index == index && Current == o && Level != SubObject.Object;
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (!editing)
        {
            if (ImGui.Button("Open UV Editor##uw", new Vector2(-1, 0))) { EditModifier(o, index, SubObject.Polygon); OpenUvEditor(); }
            Theme.Tip("Edit the UVs of this step: select faces, move / scale islands, Peel, Relax, Stitch.");
        }
        else
        {
            if (Theme.Toggle("Faces##uwl", Level == SubObject.Polygon, new Vector2(w2, 0))) SetLevelDirect(SubObject.Polygon); ImGui.SameLine(0, 4);
            if (Theme.Toggle("Edges (seams)##uwl", Level == SubObject.Edge, new Vector2(w2, 0))) SetLevelDirect(SubObject.Edge);
            if (ImGui.Button("UV Editor##uw2", new Vector2(w2, 0))) OpenUvEditor(); ImGui.SameLine(0, 4);
            if (ImGui.Button("Done##uw", new Vector2(w2, 0))) { EndModifierEdit(o); uvOpen = false; }
        }
        Ui.Label("Unwrap", Theme.TextDim);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##uwang", ref uw.AngleLimit, 10, 89, "Seam angle %.0f°")) Dirty = true;
        if (ImGui.Button("Auto Unwrap##uw", new Vector2(-1, 26)))
            UnwrapOp(o, index, uw, "Auto Unwrap", (m, faces, mod) =>
            {
                var (n, seams) = UvUnfold.AutoUnwrap(m, mod.AngleLimit, faces);
                // seams outside the selection are kept
                var keep = mod.SeamSet().Where(e => !faces.Any(f => m.Faces[f].V.Contains(e.A) && m.Faces[f].V.Contains(e.B)));
                mod.SetSeams(keep.Concat(seams).Distinct());
                UvTools.SetDensity(m, faces, mod.Density, mod.TextureSize);
                UvTools.Pack(m, faces);
                SetStatus($"Auto Unwrap: {n} charts, {seams.Count} seams.");
            });
        Theme.Tip("Molo Auto UV chain: automatic seams (sharp edges, materials, tubes and closed shapes cut), unfold, same texel density, pack.");
        if (ImGui.Button("Peel##uw", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Peel", (m, faces, mod) =>
            {
                var seams = mod.SeamSet(); if (seams.Count == 0) seams = UvUnfold.AutoSeams(m, mod.AngleLimit, faces);
                int n = UvUnfold.Unwrap(m, seams, faces);
                SetStatus($"Peel: {n} chart(s) unfolded along the seams.");
            });
        Theme.Tip("Unfolds the faces along the seams (least squares conformal), then packs.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Smart Project##uw", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Smart Project", (m, faces, mod) => { UvUnwrap.SmartProject(m, mod.AngleLimit, 0.004f, faces); });
        if (ImGui.Button("Relax##uw", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Relax", (m, faces, mod) => UvUnfold.Relax(m, faces, 80));
        Theme.Tip("Evens out stretched UVs (edge lengths pulled toward the 3D lengths).");
        ImGui.SameLine(0, 4);
        ImGui.BeginDisabled(!editing);
        if (ImGui.Button("Stitch##uw", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Stitch", (m, faces, mod) => { int n = UvUnfold.Stitch(m, faces); SetStatus(n > 0 ? $"Stitched {n} island(s) onto the biggest one." : "Stitch: select faces in two islands that share an edge."); });
        ImGui.EndDisabled();
        Theme.Tip("Select faces of two islands that touch in 3D: the smaller island moves onto the bigger one and their edge is welded.");
        if (ImGui.Button("Pack##uw", new Vector2(-1, 0)))
            UnwrapOp(o, index, uw, "Pack", (m, faces, mod) => UvTools.Pack(m, faces));
        Ui.Label("Texel density", Theme.TextDim);
        ImGui.SetNextItemWidth(w2);
        if (ImGui.DragFloat("##uwden", ref uw.Density, 2, 16, 4096, "%.0f px/m")) Dirty = true;
        ImGui.SameLine(0, 4); ImGui.SetNextItemWidth(-1);
        int ts = Array.IndexOf(new[] { 256, 512, 1024, 2048, 4096 }, uw.TextureSize); if (ts < 0) ts = 2;
        if (ImGui.Combo("##uwts", ref ts, new[] { "256", "512", "1024", "2048", "4096" }, 5)) { uw.TextureSize = new[] { 256, 512, 1024, 2048, 4096 }[ts]; Dirty = true; }
        if (ImGui.Button("Set density##uw", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Set Texel Density", (m, faces, mod) => UvTools.SetDensity(m, faces, mod.Density, mod.TextureSize));
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Measure##uw", new Vector2(w2, 0)))
        {
            var input = editing ? o.Mesh : uw.BeginEdit(ModifierStack.Evaluate(o.ModEdit?.Base ?? o.Mesh, o.FullStack.Take(index).ToList(), false, false));
            uw.Density = MathF.Round(UvTools.TexelDensity(input, Enumerable.Range(0, input.Faces.Count), uw.TextureSize));
        }
        Ui.Label($"Seams ({uw.Seams?.Length / 2 ?? 0})", Theme.TextDim);
        ImGui.BeginDisabled(!editing || Level != SubObject.Edge);
        if (ImGui.Button("Mark selected##uws", new Vector2(w2, 0))) Do("Mark Seams", () => { uw.SetSeams(uw.SeamSet().Union(SubSel(o).Edges)); });
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Clear selected##uws", new Vector2(w2, 0))) Do("Clear Seams", () => { var s = uw.SeamSet(); s.ExceptWith(SubSel(o).Edges); uw.SetSeams(s); });
        ImGui.EndDisabled();
        if (ImGui.Button("Seams from islands##uws", new Vector2(w2, 0)))
            UnwrapOp(o, index, uw, "Seams from UV Islands", (m, faces, mod) => mod.SetSeams(UvUnfold.SeamsFromUv(m)));
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Clear all##uws", new Vector2(w2, 0))) Do("Clear Seams", () => uw.Seams = null);
        if (uw.Warning != null) Ui.TextColored(new Vector4(1f, 0.6f, 0.25f, 1), uw.Warning);
    }

    /// <summary>
    /// Runs a UV tool for this Unwrap UVW: on the working mesh (selected faces, or all) while it is being edited,
    /// otherwise on its output for the current input, then stores the result.
    /// </summary>
    void UnwrapOp(SceneObject o, int index, UnwrapUvwModifier uw, string name, Action<PolyMesh, ISet<int>, UnwrapUvwModifier> op)
    {
        Do(name, () =>
        {
            if (o.ModEdit != null && o.ModEdit.Index == index)
            {
                var sel = SubSel(o).Faces;
                var faces = Level == SubObject.Polygon && sel.Count > 0 ? (ISet<int>)sel.ToHashSet() : Enumerable.Range(0, o.Mesh.Faces.Count).ToHashSet();
                op(o.Mesh, faces, uw);
                o.Touch();
                return;
            }
            if (o.ModEdit != null) EndModifierEdit(o, false);
            var input = ModifierStack.Evaluate(o.Mesh, o.Modifiers.Take(index).ToList(), false, false);
            var work = uw.BeginEdit(input);
            op(work, Enumerable.Range(0, work.Faces.Count).ToHashSet(), uw);
            uw.StoreUv(work);
            o.Touch();
        });
    }

    /// <summary>Seams of the Unwrap UVW being edited, drawn on the mesh (red, like a modeller's seam display).</summary>
    void DrawModifierSeams(SceneObject o)
    {
        if (o?.EditedModifier is not UnwrapUvwModifier uw || uw.Seams == null) return;
        var w = o.World; var col = new Vector4(1f, 0.25f, 0.2f, 1);
        foreach (var e in uw.SeamSet())
            if (e.A < o.Mesh.Verts.Count && e.B < o.Mesh.Verts.Count) lines.Line(Vector3.Transform(o.Mesh.Verts[e.A], w), Vector3.Transform(o.Mesh.Verts[e.B], w), col);
    }
}
