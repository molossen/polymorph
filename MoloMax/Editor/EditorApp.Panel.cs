using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>The Command Panel (right): Create, Modify and Utilities, laid out as 3ds Max rollouts.</summary>
public sealed partial class EditorApp
{
    const ImGuiTreeNodeFlags Open = ImGuiTreeNodeFlags.DefaultOpen;

    /// <summary>Brings the Modify tab to the front on the next frame.</summary>
    public bool showModifyTab;

    void DrawCommandPanel()
    {
        if (!ShowPanel) return;
        float y = menuH + toolbarH;
        ImGui.SetNextWindowPos(new Vector2(gpu.Width - panelW, y));
        ImGui.SetNextWindowSize(new Vector2(panelW, gpu.Height - y - statusH));
        ImGui.Begin("##command", Fixed);
        if (ImGui.BeginTabBar("##cmdtabs"))
        {
            bool b = Theme.IsBlender;
            if (ImGui.BeginTabItem(b ? "Add###create" : "Create###create")) { CreateTab(); ImGui.EndTabItem(); }
            bool openMod = showModifyTab; showModifyTab = false; bool keep = true; string modLabel = b ? "Edit###modify" : "Modify###modify";
            if (openMod ? ImGui.BeginTabItem(modLabel, ref keep, ImGuiTabItemFlags.SetSelected) : ImGui.BeginTabItem(modLabel)) { ModifyTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(b ? "Tool###utilities" : "Utilities###utilities")) { UtilitiesTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Tripo###tripo")) { ImGui.BeginChild("##tripotab"); HubTripo(); ImGui.EndChild(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    bool KindButton(CreateKind k, float w)
    {
        bool active = k == CreateKind.Wall && modal.Kind == ModalKind.Wall;
        bool r = Theme.Toggle(KindLabel(k), active, new Vector2(w, 24));
        Theme.Tip(k == CreateKind.Wall ? "Draw a wall point by point" : "Add at the 3D cursor (Shift+A in the viewport)");
        if (r) { lastCreateKind = k; AddPrimitive(k); }
        return r;
    }

    // ---------------------------------------------------------------- Create

    void CreateTab()
    {
        float w = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (Ui.Header("Standard Primitives", Open))
        {
            KindButton(CreateKind.Box, w); ImGui.SameLine(); KindButton(CreateKind.Cone, w);
            KindButton(CreateKind.Sphere, w); ImGui.SameLine(); KindButton(CreateKind.Cylinder, w);
            KindButton(CreateKind.Tube, w); ImGui.SameLine(); KindButton(CreateKind.Plane, w);
            KindButton(CreateKind.Tufted, w);
            Theme.Tip("Capitonné cushion block: benches, padded headboards, sofa seats");
        }
        if (Ui.Header("Shapes", Open))
        {
            bool on = modal.Kind == ModalKind.Line;
            if (Theme.Toggle("Line", on, new Vector2(w, 24))) { if (on) ConfirmModal(); else BeginLine(); }
            TutRect("create_line");
            Theme.Tip("Draw a line point by point, snapped on vertices / edges / faces. Shift = straight. Then make it a door frame, a beam or an extrusion.");
            ImGui.SameLine();
            int nr = (int)NextShapeRender;
            ImGui.SetNextItemWidth(w);
            if (ImGui.Combo("##next_shape_render", ref nr, ShapeRenderNames, ShapeRenderNames.Length)) NextShapeRender = (MoloMax.Core.Mesh.ShapeRender)nr;
            Theme.Tip("What the next line becomes (changeable afterwards in its panel)");
        }
        if (Ui.Header("GTA / MLO", Open))
        {
            KindButton(CreateKind.Room, w); ImGui.SameLine(); KindButton(CreateKind.Wall, w);
            KindButton(CreateKind.Stairs, w); ImGui.SameLine(); KindButton(CreateKind.Door, w);
            KindButton(CreateKind.Portal, w); ImGui.SameLine(); KindButton(CreateKind.CollisionBox, w);
            Ui.TextWrapped("Rooms are closed boxes with normals facing inward. Leave a small gap between rooms and join them with a floor.");
        }
        if (Ui.Header("Import", Open))
        {
            if (ImGui.Button("Import model...", new Vector2(-1, 26))) ImportDialog();
            Ui.TextDisabled("GTA: .ydr .yft .ydd .ybn (and CodeWalker .xml). Also GLB, glTF, OBJ. Or drop the file on the window.");
        }
        var k = lastCreateKind;
        if (Ui.Header($"Parameters - {KindLabel(k)}", Open))
        {
            ParamsFor(k);
            Ui.TextDisabled("Used for the next one. Just after adding, the panel at the bottom left of the viewport changes it live (F9).");
        }
        if (Ui.Header("3D Cursor", Open))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.DragFloat3("##cursor", ref Cursor3D, 0.05f, 0, 0, "%.2f");
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            if (ImGui.Button("To Origin", new Vector2(w3, 0))) Cursor3D = Vector3.Zero; ImGui.SameLine();
            if (ImGui.Button("To Selected", new Vector2(w3, 0))) CursorToSelected(); ImGui.SameLine();
            if (ImGui.Button("Sel. to Cursor", new Vector2(w3, 0))) SelectionToCursor();
            Ui.TextDisabled("Shift+right-click in the viewport places it on a surface.");
        }
    }

    bool F(string label, ref float v, float speed = 0.05f, float min = 0.001f, float max = 10000f)
    {
        ImGui.SetNextItemWidth(120); return ImGui.DragFloat(label, ref v, speed, min, max, "%.3f");
    }
    bool I(string label, ref int v, int min = 1, int max = 200)
    {
        ImGui.SetNextItemWidth(120); return ImGui.DragInt(label, ref v, 0.2f, min, max);
    }

    /// <summary>Parameter fields of a primitive. Returns true when one changed.</summary>
    bool ParamsFor(CreateKind k)
    {
        var P = Params; bool c = false;
        switch (k)
        {
            case CreateKind.Box:
            case CreateKind.CollisionBox:
                c |= F("Width (X)", ref P.Width); c |= F("Length (Y)", ref P.Length); c |= F("Height", ref P.Height);
                if (k == CreateKind.Box) { c |= I("Width Segs", ref P.WSegs); c |= I("Length Segs", ref P.LSegs); c |= I("Height Segs", ref P.HSegs); }
                break;
            case CreateKind.Plane: c |= F("Width (X)", ref P.Width); c |= F("Length (Y)", ref P.Length); c |= I("Width Segs", ref P.WSegs); c |= I("Length Segs", ref P.LSegs); break;
            case CreateKind.Sphere: c |= F("Radius", ref P.Radius); c |= I("Segments", ref P.Segments, 4, 128); c |= ImGui.Checkbox("Smooth", ref P.Smooth); break;
            case CreateKind.Cylinder: c |= F("Radius", ref P.Radius); c |= F("Height", ref P.Height); c |= I("Sides", ref P.Sides, 3, 128); c |= I("Height Segs", ref P.HSegs); c |= ImGui.Checkbox("Smooth##2", ref P.Smooth); break;
            case CreateKind.Cone: c |= F("Radius 1", ref P.Radius); c |= F("Radius 2", ref P.Radius2, 0.05f, 0f); c |= F("Height", ref P.Height); c |= I("Sides", ref P.Sides, 3, 128); c |= ImGui.Checkbox("Smooth##3", ref P.Smooth); break;
            case CreateKind.Tube: c |= F("Radius 1", ref P.Radius); c |= F("Radius 2", ref P.Radius2); c |= F("Height", ref P.Height); c |= I("Sides", ref P.Sides, 3, 128); c |= ImGui.Checkbox("Smooth##4", ref P.Smooth); break;
            case CreateKind.Room: c |= F("Width (X)", ref P.RoomWidth); c |= F("Length (Y)", ref P.RoomLength); c |= F("Height", ref P.RoomHeight); break;
            case CreateKind.Wall: c |= F("Height", ref P.WallHeight); c |= F("Thickness", ref P.WallThickness, 0.01f); break;
            case CreateKind.Stairs: c |= F("Width", ref P.StairWidth); c |= F("Total Rise", ref P.StairRise); c |= F("Run", ref P.StairRun); c |= I("Steps", ref P.Steps, 1, 100); Ui.TextDisabled($"Step {P.StairRise / Math.Max(P.Steps, 1):0.000} m high, {P.StairRun / Math.Max(P.Steps, 1):0.000} m deep"); break;
            case CreateKind.Door: c |= F("Width", ref P.DoorWidth); c |= F("Height", ref P.DoorHeight); c |= F("Thickness", ref P.DoorThickness, 0.005f); break;
            case CreateKind.Portal: c |= F("Width", ref P.PortalWidth); c |= F("Height", ref P.PortalHeight); break;
            case CreateKind.Tufted:
                c |= F("Width (X)", ref P.TuftWidth); c |= F("Length (Y)", ref P.TuftLength); c |= F("Height", ref P.TuftHeight);
                c |= I("Cells X", ref P.TuftX, 1, 40); c |= I("Cells Y", ref P.TuftY, 1, 40); c |= I("Cells Z", ref P.TuftZ, 1, 40);
                c |= F("Puff", ref P.TuftPuff, 0.002f, 0f); c |= I("Segments / cell", ref P.TuftSegs, 2, 16); c |= ImGui.Checkbox("Closed bottom", ref P.TuftBottom);
                Ui.TextDisabled($"Cells {P.TuftWidth / Math.Max(P.TuftX, 1) * 100:0} x {P.TuftLength / Math.Max(P.TuftY, 1) * 100:0} cm, {(P.TuftX * P.TuftY + 2 * (P.TuftX + P.TuftY) * P.TuftZ) * P.TuftSegs * P.TuftSegs * 2:N0} triangles");
                break;
        }
        return c;
    }

    // ---------------------------------------------------------------- Modify

    void ModifyTab()
    {
        var o = Current;
        if (o == null)
        {
            if (Selected.Count > 1)
            {
                Ui.Text($"{Selected.Count} objects selected");
                if (ImGui.Button("Attach all into the first", new Vector2(-1, 24)))
                {
                    var objs = SelObjects().ToList();
                    for (int i = 1; i < objs.Count; i++) AttachObject(objs[0], objs[i]);
                }
                MaterialRollout(null);
                ColourRollout();
                UvRollout();
            }
            else Ui.TextDisabled("Select an object to edit it.");
            return;
        }

        // name + wire colour, like the top of the Max modify panel
        string name = o.Name;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 30);
        if (ImGui.InputText("##name", ref name, 63, ImGuiInputTextFlags.EnterReturnsTrue) && name.Trim().Length > 0) { var n = name.Trim(); Do("Rename", () => o.Name = n); }
        ImGui.SameLine();
        var wc = o.WireColour;
        if (ImGui.ColorEdit3("##wire", ref wc, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel)) o.WireColour = wc;

        // modifier stack: Editable Poly with its sub-object levels
        // sized to show the 6 rows without a scrollbar; this list is the only sub-object switcher
        float stackH = ImGui.GetTextLineHeightWithSpacing() * 6 + ImGui.GetStyle().WindowPadding.Y * 2 + 2;
        ImGui.BeginChild("##stack", new Vector2(0, stackH), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar);
        if (ImGui.Selectable("Editable Poly", Level == SubObject.Object)) SetLevel(SubObject.Object);
        var levels = new[] { SubObject.Vertex, SubObject.Edge, SubObject.Border, SubObject.Polygon, SubObject.Element };
        for (int i = 0; i < levels.Length; i++)
        {
            if (ImGui.Selectable("      " + levels[i], Level == levels[i])) SetLevel(Level == levels[i] ? SubObject.Object : levels[i]);
            string key = Settings.Keymap == Keymap.Max ? (i + 1).ToString()
                : levels[i] switch { SubObject.Vertex => "1", SubObject.Edge => "2", SubObject.Polygon => "3", _ => "" };
            if (key.Length > 0) { ImGui.SameLine(ImGui.GetContentRegionAvail().X - 8); Ui.TextDisabled(key); }
        }
        ImGui.EndChild();

        if (Ui.Header("GTA Object", Open))
        {
            int t = (int)o.GtaType;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##gtatype", ref t, new[] { "Drawable (.ydr)", "Collision (.ybn)", "MLO Room", "Portal" }, 4)) SetGtaType(o, (GtaObjectType)t);
            int nv = o.Mesh.Verts.Count;
            Ui.TextColored(Theme.TextDim, $"{nv:N0} vertices   {o.Mesh.Faces.Count:N0} polys");
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            if (ImGui.Button("Reset XForm", new Vector2(w3, 0))) ResetXForm(); ImGui.SameLine();
            if (ImGui.Button("Center Pivot", new Vector2(w3, 0))) CenterPivot(); ImGui.SameLine();
            if (ImGui.Button("Pivot Base", new Vector2(w3, 0))) PivotToBase();
        }
        InstancePanel(o);

        if (Ui.Header("Selection", Open))
        {
            if (Level == SubObject.Object)
                Ui.TextDisabled("Pick a sub-object level in the stack above.");
            else
            {
                float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
                if (ImGui.Button("Grow", new Vector2(w2, 0))) Grow(); ImGui.SameLine();
                if (ImGui.Button("Shrink", new Vector2(w2, 0))) Shrink();
                if (Level == SubObject.Edge)
                {
                    if (ImGui.Button("Ring", new Vector2(w2, 0))) Ring(); ImGui.SameLine();
                    if (ImGui.Button("Loop", new Vector2(w2, 0))) Loop();
                }
                Ui.TextDisabled($"{SubSel(o).Count(Level)} {Level}(s) selected");
            }
        }

        if (Ui.Header("Edit Geometry", Open))
        {
            var others = Scene.Objects.Where(x => x != o).ToList();
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (others.Count > 0)
            {
                attachIndex = Math.Clamp(attachIndex, 0, others.Count - 1);
                ImGui.SetNextItemWidth(w2);
                ImGui.Combo("##attach", ref attachIndex, others.Select(x => x.Name).ToArray(), others.Count);
                ImGui.SameLine(); if (ImGui.Button("Attach", new Vector2(w2, 0))) AttachObject(o, others[attachIndex]);
            }
            if (ImGui.Button("Cap Holes", new Vector2(w2, 0))) Cap(); ImGui.SameLine();
            if (ImGui.Button("Tessellate", new Vector2(w2, 0))) Tessellate();
            if (ImGui.Button("Collapse", new Vector2(w2, 0))) Collapse(); ImGui.SameLine();
            if (ImGui.Button("Flip", new Vector2(w2, 0))) FlipFaces();
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##weldt", ref weldT, 0.001f, 0.0001f, 10f, "%.4f m"); ImGui.SameLine();
            if (ImGui.Button("Weld", new Vector2(w2, 0))) Weld(weldT);
            Theme.Tip("Merge vertices closer than the threshold (selected vertices, or the whole object if none).");
            if (Level != SubObject.Object) MakePlanarUi();
        }

        if (Level == SubObject.Vertex && Ui.Header("Edit Vertices", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (ImGui.Button("Remove", new Vector2(w2, 0))) DeleteSelection(); ImGui.SameLine();
            if (ImGui.Button("Collapse##v", new Vector2(w2, 0))) Collapse();
        }
        if ((Level == SubObject.Edge || Level == SubObject.Border) && Ui.Header(Level == SubObject.Edge ? "Edit Edges" : "Edit Borders", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (Level == SubObject.Edge)
            {
                if (ImGui.Button("Connect", new Vector2(w2, 0))) ConnectEdges(); ImGui.SameLine();
                if (ImGui.Button("Remove##e", new Vector2(w2, 0))) RemoveEdges(removeWithVerts);
                ImGui.Checkbox("Remove leaves no stray vertices", ref removeWithVerts);
                Ui.TextDisabled("Select a ring (Ring) then Connect to add a loop.");
            }
            else if (ImGui.Button("Cap", new Vector2(-1, 0))) Cap();
        }
        if ((Level == SubObject.Polygon || Level == SubObject.Element) && Ui.Header("Edit Polygons", Open))
        {
            ImGui.Checkbox("By Polygon (otherwise Group)", ref byPolygon);
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##exh", ref extrudeH, 0.01f, -100, 100, "%.3f m"); ImGui.SameLine();
            if (ImGui.Button("Extrude", new Vector2(w2, 0))) ExtrudeFaces(extrudeH, byPolygon);
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##ins", ref insetA, 0.005f, -100, 100, "%.3f m"); ImGui.SameLine();
            if (ImGui.Button("Inset", new Vector2(w2, 0))) InsetFaces(insetA, byPolygon);
            ImGui.SetNextItemWidth(w2 / 2 - 2); ImGui.DragFloat("##bvh", ref bevelH, 0.01f, -100, 100, "h %.2f"); ImGui.SameLine();
            ImGui.SetNextItemWidth(w2 / 2 - 2); ImGui.DragFloat("##bvo", ref bevelO, 0.005f, -100, 100, "o %.2f"); ImGui.SameLine();
            if (ImGui.Button("Bevel", new Vector2(w2, 0))) BevelFaces(bevelH, bevelO, byPolygon);
            if (ImGui.Button("Flip##p", new Vector2(w2, 0))) FlipFaces(); ImGui.SameLine();
            if (ImGui.Button("Tessellate##p", new Vector2(w2, 0))) Tessellate();
            if (ImGui.Button("Detach", new Vector2(w2, 0))) DetachFaces(detachAsClone); ImGui.SameLine();
            ImGui.Checkbox("as Clone", ref detachAsClone);
        }

        MaterialRollout(o);
        if (Level is SubObject.Polygon or SubObject.Element or SubObject.Object) SmoothingRollout(o);
        ColourRollout();
        UvRollout();
    }

    void MaterialRollout(SceneObject o)
    {
        if (!Ui.Header("Polygon: Material IDs", Open)) return;
        SyncMaterialToFaces(o);
        var names = Scene.Materials.Select(m => $"{m.Name}  ({m.Shader})").ToArray();
        int idx = Math.Max(0, Scene.Materials.FindIndex(m => m.Id == selectedMaterial));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##mat", ref idx, names, names.Length)) selectedMaterial = Scene.Materials[idx].Id;
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Assign", new Vector2(w2, 0))) AssignMaterial(Scene.Materials[idx].Id);
        Theme.Tip("Selected polygons, or whole selected objects at top level.");
        ImGui.SameLine();
        if (ImGui.Button("Select ID", new Vector2(w2, 0)) && o != null) SelectByMaterial(Scene.Materials[idx].Id);
        if (ImGui.Button("Open Material Editor (M)", new Vector2(-1, 0))) materialEditorOpen = true;
    }

    void SmoothingRollout(SceneObject o)
    {
        if (!Ui.Header("Polygon: Smoothing Groups")) return;
        var faces = FacesInScope(o).ToList();
        float bw = (ImGui.GetContentRegionAvail().X - 7 * 2) / 8;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));
        for (int b = 0; b < 32; b++)
        {
            if (b % 8 != 0) ImGui.SameLine();
            uint bit = 1u << b;
            bool all = faces.Count > 0 && faces.All(f => (o.Mesh.Faces[f].Smooth & bit) != 0);
            bool any = faces.Any(f => (o.Mesh.Faces[f].Smooth & bit) != 0);
            ImGui.PushID(b);
            if (Theme.Toggle(any && !all ? $"({b + 1})" : (b + 1).ToString(), all, new Vector2(bw, 20))) ToggleSmoothing(b);
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##asa", ref autoSmoothA, 0.5f, 0, 180, "%.0f°"); ImGui.SameLine();
        if (ImGui.Button("Auto Smooth", new Vector2(w2, 0))) AutoSmooth(autoSmoothA);
        if (ImGui.Button("Clear All", new Vector2(-1, 0))) ClearSmoothing();
    }

    void ColourRollout()
    {
        if (!Ui.Header("GTA Vertex Colour", Open)) return;
        Ui.TextDisabled("R = natural (sky) light, G = artificial (interior) light");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ColourButton("Green", new Vector4(0, 1, 0, 1), w2)) PaintVertexColour(new Vector4(0, 1, 0, 1)); Theme.Tip("Everything that is not emissive (interiors)");
        ImGui.SameLine();
        if (ColourButton("Dark Green", new Vector4(0, 0.5f, 0, 1), w2)) PaintVertexColour(new Vector4(0, 0.5f, 0, 1)); Theme.Tip("Darker interior corners");
        if (ColourButton("White", new Vector4(1, 1, 1, 1), w2)) PaintVertexColour(new Vector4(1, 1, 1, 1)); Theme.Tip("Emissive: signs, adverts, screens");
        ImGui.SameLine();
        if (ColourButton("Red", new Vector4(1, 0, 0, 1), w2)) PaintVertexColour(new Vector4(1, 0, 0, 1)); Theme.Tip("Exterior props such as trees (otherwise they render black)");
        ImGui.ColorEdit3("##custom", ref customColour, ImGuiColorEditFlags.NoInputs); ImGui.SameLine();
        if (ImGui.Button("Paint custom", new Vector2(-1, 0))) PaintVertexColour(new Vector4(customColour, 1));
    }

    static bool ColourButton(string label, Vector4 c, float w)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        bool r = ImGui.Button("     " + label, new Vector2(w, 0));
        dl.AddRectFilled(p + new Vector2(5, 5), p + new Vector2(17, 17), ImGui.ColorConvertFloat4ToU32(c));
        dl.AddRect(p + new Vector2(5, 5), p + new Vector2(17, 17), 0xFF202020);
        return r;
    }

    void UvRollout()
    {
        if (!Ui.Header("UVW Map")) return;
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##tile", ref boxTile, 0.05f, 0.05f, 100, "%.2f m / tile"); ImGui.SameLine();
        if (ImGui.Button("Box Map", new Vector2(w2, 0))) BoxMap(boxTile, boxMapWorld);
        ImGui.Checkbox("World space (textures line up across objects)", ref boxMapWorld);
    }

    // ---------------------------------------------------------------- Utilities

    void UtilitiesTab()
    {
        if (ImGui.Button("Preferences (keymap, import, library, add-ons)...", new Vector2(-1, 24))) prefsOpen = true;
        if (Ui.Header("GTA Lighting", Open))
        {
            ImGui.Checkbox("Interior (no sun)", ref InteriorLight);
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Time of day", ref Hour, 0, 24, "%.1f h");
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Interior amb.", ref InteriorLevel, 0, 1.5f);
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Exposure", ref Exposure, 0.3f, 3f);
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Modelling light", ref AmbientBoost, 0, 0.6f);
            Ui.TextWrapped("Realistic shading uses the GTA basic shader: vertex colour R/G pick sky or interior ambient, emissive adds its colour on top.");
        }
        if (Ui.Header("Grid & Snaps", Open))
        {
            ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Grid step", ref GridStep, 0.005f, 0.001f, 100, "%.3f m");
            ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Angle snap", ref AngleSnap, 0.5f, 0.5f, 90, "%.1f°");
            ImGui.Checkbox("Snaps on (S)", ref Snap);
        }
        if (Ui.Header("Scene", Open))
        {
            var (objs, polys, tris, verts) = Scene.Stats();
            Ui.TextDisabled($"{objs} objects   {polys:N0} polys   {tris:N0} tris   {verts:N0} verts");
            if (ImGui.Button("Check Scene for GTA", new Vector2(-1, 26))) RunGtaChecks();
            foreach (var r in checkResults) Ui.TextWrapped(r);
        }
        if (Ui.Header("Export", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (ImGui.Button("OBJ (all)", new Vector2(w2, 0))) ExportObj(false); ImGui.SameLine();
            if (ImGui.Button("OBJ (selected)", new Vector2(w2, 0))) ExportObj(true);
            Ui.TextWrapped("Y-up, vertex colours kept, shader names written in the .mtl for Sollumz.");
        }
        if (Ui.Header("Textures"))
        {
            if (ImGui.Button("Add texture folder...", new Vector2(-1, 0)))
            {
                using var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder searched for texture names" };
                if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) { renderer.Textures.SearchFolders.Add(d.SelectedPath); renderer.Textures.ClearResolveCache(); }
                Input.ReleaseAllKeys();
            }
            TextureFolderList("##paneltexfolders");
            foreach (var e in renderer.Textures.Errors.TakeLast(6)) Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), e);
        }
    }

    /// <summary>Checks the rules that make an MLO or prop load cleanly in GTA / FiveM.</summary>
    void RunGtaChecks()
    {
        checkIssues = MoloMax.Core.Gta.Check.GtaChecker.Run(Scene);
        checkResults.Clear();
        foreach (var i in checkIssues) checkResults.Add("• " + i.Message);
        checkOpen = true;
        int err = checkIssues.Count(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Error), warn = checkIssues.Count(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Warning);
        SetStatus(err + warn == 0 ? "GTA check: ready for GTA." : $"GTA check: {err} error(s), {warn} warning(s).");
    }

    static float Volume(PolyMesh m)
    {
        float v = 0;
        foreach (var f in m.Faces) foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                v += Vector3.Dot(m.Verts[f.V[a]], Vector3.Cross(m.Verts[f.V[b]], m.Verts[f.V[c]])) / 6f;
        return v;
    }

    // ---------------------------------------------------------------- material editor

    bool showGameMaterials;
    /// <summary>Material being dragged from the list (-1: none); dropped when the button is released over a viewport.</summary>
    int matDragId = -1;

    /// <summary>Ends a material drag: released over a viewport (not over a window), the material goes on what is under the mouse.</summary>
    void MaterialDropCheck()
    {
        if (matDragId < 0) return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        int id = matDragId; matDragId = -1;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) return; // dropped back on a panel
        DropMaterial(Hovered(), Input.Mouse, id);
    }

    bool matSelOnly = true; string matSelKey; long matUsedKey; HashSet<int> matUsed;

    /// <summary>Material ids used by the selected faces (Polygon / Element mode) or by the selected objects; null when nothing is selected.</summary>
    int matListShown = -1;

    HashSet<int> MaterialsOfSelection()
    {
        var objs = SelObjects().Where(o => o.Mesh != null).ToList();
        if (objs.Count == 0) return null;
        long key = Level.GetHashCode() * 31L + objs.Count;
        foreach (var o in objs) key = key * 1000003L + o.Id * 7919L + o.Version + (o == Current ? SetSig(SubSel(o).Faces) : 0);
        if (key == matUsedKey && matUsed != null) return matUsed;
        var res = new HashSet<int>();
        foreach (var o in objs)
        {
            var m = o.Evaluated() ?? o.Mesh;
            if (Level is SubObject.Polygon or SubObject.Element && o == Current && SubSel(o).Faces.Count > 0)
            { foreach (var fi in SubSel(o).Faces) if (fi < o.Mesh.Faces.Count) res.Add(o.Mesh.Faces[fi].MatId); continue; }
            foreach (var f in m.Faces) res.Add(f.MatId);
        }
        matUsedKey = key; matUsed = res;
        return res;
    }

    void DrawMaterialEditor()
    {
        if (!materialEditorOpen) return;
        ImGui.SetNextWindowSize(new Vector2(640, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(explorerW + 40, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Material Editor - GTA Shaders", ref materialEditorOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }

        // a collision selected: what counts is its collision material and RoomID, not a shader
        if (Current is { } colSel && colSel.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup)
        {
            var parts = colSel.GtaType == GtaObjectType.Collision ? new List<SceneObject> { colSel } : Scene.Objects.Where(x => x.ParentId == colSel.Id && x.GtaType == GtaObjectType.Collision).ToList();
            if (parts.Count > 0)
            {
                Ui.Text(colSel.GtaType == GtaObjectType.Collision ? "Collision object" : $"Collision group, {parts.Count} part(s)");
                foreach (var x in parts.Take(12))
                    Ui.TextDisabled($"{x.Name}:  material {MoloMax.Core.Gta.Export.CollisionMaterials.NameOf(x.Gta.CollisionMaterial)},  RoomID {(x.Gta.RoomIdOverride < 0 ? "auto" : x.Gta.RoomIdOverride.ToString())}");
                if (parts.Count > 12) Ui.TextDisabled($"... and {parts.Count - 12} more");
                if (ImGui.Button("Collision settings (material, RoomID, flags)")) HubGo(HubPage.Collision);
                ImGui.Separator();
            }
        }
        int gameMats = 0; foreach (var m in Scene.Materials) if (m.FromGame) gameMats++;
        // materials of the selection (the selected faces in Polygon mode): shown alone, the first one picked
        var used = MaterialsOfSelection();
        long selKeyL = Selected.Count * 1000003L + (long)Level; foreach (var id in Selected) selKeyL += id * 0x9E3779B1L;
        string selKey = selKeyL.ToString();
        if (selKey != matSelKey)
        {
            matSelKey = selKey;
            if (used != null && used.Count > 0 && !used.Contains(selectedMaterial)) selectedMaterial = Scene.Materials.First(m => used.Contains(m.Id)).Id;
        }
        bool filter = matSelOnly && used != null && used.Count > 0;
        ImGui.BeginGroup();
        float hw = 98;
        if (Theme.Toggle("Selection##matf", matSelOnly, new Vector2(hw, 0))) matSelOnly = true;
        Theme.Tip("Only the materials of the selected objects (or selected faces)");
        ImGui.SameLine(0, 4);
        if (Theme.Toggle("All##matf", !matSelOnly, new Vector2(hw, 0))) matSelOnly = false;
        if (gameMats > 0) { ImGui.Checkbox($"Game materials ({gameMats:N0})##gm", ref showGameMaterials); Theme.Tip("Materials of the game models shown in MAP mode (read from the game, not yours). Hidden unless you want to look at them."); }
        ImGui.BeginChild("##matlist", new Vector2(200, gameMats > 0 ? -56 : -30), ImGuiChildFlags.Borders);
        if (matSelOnly && (used == null || used.Count == 0)) Ui.TextDisabled("Nothing selected:\nall materials");
        // the list goes to the picked material when it changed from the viewport (like the Scene Explorer)
        bool scrollToMat = selectedMaterial != matListShown; matListShown = selectedMaterial;
        foreach (var m in Scene.Materials)
        {
            if (filter && !used.Contains(m.Id)) continue;
            if (m.FromGame && !showGameMaterials && m.Id != selectedMaterial) continue;
            var c = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(c + new Vector2(0, 3), c + new Vector2(10, 13), ImGui.ColorConvertFloat4ToU32(new Vector4(m.ViewportColour, 1)));
            ImGui.Dummy(new Vector2(12, 0)); ImGui.SameLine();
            if (ImGui.Selectable($"{m.Name}##m{m.Id}", selectedMaterial == m.Id)) { selectedMaterial = m.Id; matListShown = m.Id; }
            if (scrollToMat && m.Id == selectedMaterial && !ImGui.IsItemVisible()) ImGui.SetScrollHereY(0.5f);
            TutRect("mat:" + m.Id);
            // drag it onto an object in the viewport (or a face in Polygon mode) to apply it
            if (ImGui.BeginDragDropSource())
            {
                ImGui.SetDragDropPayload("MOLO_MATERIAL", IntPtr.Zero, 0);
                matDragId = m.Id;
                ImGui.Text(m.Name);
                Ui.TextDisabled(Level is SubObject.Polygon or SubObject.Element ? "Drop on a face" : "Drop on an object");
                ImGui.EndDragDropSource();
            }
            Theme.Tip("Drag onto an object in the viewport (onto a face in Polygon mode) to apply it");
        }
        ImGui.EndChild();
        ImGui.EndGroup();
        ImGui.SameLine();
        var mat = Scene.Materials.FirstOrDefault(x => x.Id == selectedMaterial) ?? Scene.Materials[0];
        selectedMaterial = mat.Id;
        ImGui.BeginChild("##matprops", new Vector2(0, -30));
        string mname = mat.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##mname", ref mname, 63)) mat.Name = mname;
        var shaders = GtaShaders.All;
        int si = Math.Max(0, shaders.ToList().FindIndex(s => s.Name == mat.Shader));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##shader", ref si, shaders.Select(s => $"{s.Name}.sps  -  {s.Label}").ToArray(), shaders.Count)) { Undo.Record("Shader", Scene); mat.Shader = shaders[si].Name; Dirty = true; }
        var p = mat.Preset;
        Ui.TextDisabled(p.Notes);
        Ui.TextDisabled($"Render bucket {p.RenderBucket}{(p.AlphaBlended ? ", alpha blended" : ", opaque with alpha test 0.33")}{(p.Emissive ? ", emissive" : "")}");
        ImGui.Separator();
        TexSlot("Diffuse", ref mat.DiffuseTexture, true);
        TexSlot("Normal (bump)", ref mat.BumpTexture, p.Bump);
        TexSlot("Specular", ref mat.SpecTexture, p.Spec);
        TexSlot("Detail", ref mat.DetailTexture, p.Detail);
        if (mat.IsTerrain) TerrainLayerSlots(mat);
        ImGui.Separator();
        var vc = mat.ViewportColour;
        if (ImGui.ColorEdit3("Viewport colour", ref vc)) mat.ViewportColour = vc;
        if (p.Bump) { ImGui.SetNextItemWidth(160); ImGui.SliderFloat("bumpiness", ref mat.Bumpiness, 0, 4); }
        if (p.Spec || p.Glass)
        {
            ImGui.SetNextItemWidth(160); ImGui.SliderFloat("specularIntensityMult", ref mat.SpecularIntensity, 0, 2);
            ImGui.SetNextItemWidth(160); ImGui.SliderFloat("specularFalloffMult", ref mat.SpecularFalloff, 1, 512);
            ImGui.SetNextItemWidth(160); ImGui.SliderFloat("specularFresnel", ref mat.SpecularFresnel, 0, 1);
            var mask = mat.SpecMapIntMask; ImGui.SetNextItemWidth(160);
            if (ImGui.DragFloat3("specMapIntMask", ref mask, 0.01f, 0, 1)) mat.SpecMapIntMask = mask;
        }
        if (p.Emissive) { ImGui.SetNextItemWidth(160); ImGui.SliderFloat("emissiveMultiplier", ref mat.EmissiveMultiplier, 0, 16); }
        if (p.Detail) { var ds = mat.DetailSettings; ImGui.SetNextItemWidth(220); if (ImGui.DragFloat4("detailSettings", ref ds, 0.01f)) mat.DetailSettings = ds; }
        ImGui.EndChild();

        bool newMat = ImGui.Button("New"); TutRect("mat_new");
        if (newMat) { Undo.Record("New Material", Scene); selectedMaterial = Scene.AddMaterial("material_" + Scene.NextMaterialId, new Vector3(0.7f)).Id; }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate")) { Undo.Record("Duplicate Material", Scene); var c = mat.Clone(); c.Id = Scene.NextMaterialId++; c.Name += "_copy"; Scene.Materials.Add(c); selectedMaterial = c.Id; }
        ImGui.SameLine();
        bool canDelete = mat.Id != 0 && Scene.Materials.Count > 1;
        if (ImGui.Button("Delete") && canDelete)
        {
            Do("Delete Material", () =>
            {
                foreach (var o in Scene.Objects) { foreach (var f in o.Mesh.Faces) if (f.MatId == mat.Id) f.MatId = 0; o.Touch(); }
                Scene.Materials.Remove(mat);
            });
            selectedMaterial = 0;
        }
        ImGui.SameLine();
        EyedropperButton("eyedrop_me", ImGui.GetFrameHeight());
        ImGui.SameLine();
        PasteMaterialButton("paste_me", ImGui.GetFrameHeight());
        ImGui.SameLine();
        if (Theme.Toggle("Assign Material to Selection", true, new Vector2(-1, 0))) AssignMaterial(mat.Id);
        ImGui.End();
    }

    /// <summary>Terrain shaders: the 4 layer textures and their normal maps (the view blends them with vertex colour 1).</summary>
    void TerrainLayerSlots(GtaMaterial mat)
    {
        mat.EnsureLayers();
        Ui.TextDisabled("4 ground textures, blended by painting (Paint tab > Terrain layers)");
        for (int i = 0; i < 4; i++)
        {
            ImGui.PushID("tl" + i);
            if (ImGui.SmallButton($"Pick layer {i + 1} from Molo Materials")) { libLayerMat = mat; libLayerIndex = i; OpenLibrary(LibraryAction.TerrainLayer); }
            Theme.Tip("Choose a GTA material: its texture and normal map fill this layer.");
            TexSlot($"Layer {i + 1}", ref mat.Layers[i], true);
            TexSlot($"  normal", ref mat.LayerBumps[i], true);
            ImGui.PopID();
        }
    }

    void TexSlot(string label, ref string path, bool enabled)
    {
        ImGui.PushID(label);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.AlignTextToFramePadding(); Ui.Text(label); ImGui.SameLine(86);
        // thumbnail of the texture (hover: bigger)
        {
            float th = ImGui.GetFrameHeight();
            var tp = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(th, th));
            var dl = ImGui.GetWindowDrawList();
            var full = path != null ? renderer.Textures.Resolve(path) : null;
            var srv = full != null ? renderer.Textures.Thumb(full) : null;
            if (srv != null) dl.AddImage(imgui.IdOf(srv), tp, tp + new Vector2(th, th));
            else dl.AddRectFilled(tp, tp + new Vector2(th, th), 0xFF2A2A2A, 2);
            dl.AddRect(tp, tp + new Vector2(th, th), 0xFF505050, 2);
            if (srv != null && ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Image(imgui.IdOf(srv), new Vector2(192, 192));
                Ui.TextDisabled(Path.GetFileName(full));
                ImGui.EndTooltip();
            }
            ImGui.SameLine(0, 4);
        }
        string v = path ?? "";
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 62);
        if (ImGui.InputTextWithHint("##p", "none", ref v, 260)) path = v.Length > 0 ? v : null;
        ImGui.SameLine();
        if (ImGui.Button("...")) { var f = PickTexture(); if (f != null) { path = f; renderer.Textures.Reload(f); Dirty = true; } }
        ImGui.SameLine();
        if (ImGui.Button("x")) path = null;
        if (!enabled) ImGui.EndDisabled();
        if (enabled && path != null && renderer.Textures.Resolve(path) == null) { ImGui.SameLine(); Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), "!"); Theme.Tip("File not found. Use a full path or add a texture folder in Utilities."); }
        ImGui.PopID();
    }
}
