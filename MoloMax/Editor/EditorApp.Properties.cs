using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// The Properties editor, laid out like Blender's: a column of tabs on the left
/// (Tool, Scene, Object, Modifiers, Data, Material, GTA, Tripo) and the tab's panels on the right.
/// </summary>
public sealed partial class EditorApp
{
    enum PropTab { Map, Tool, Scene, Object, Modifiers, Data, Material, Light, Paint, Gta, Weapon, Vehicle, Tripo, Addons }
    PropTab propTab = PropTab.Object;
    SubObject propLastLevel = SubObject.Object; PropTab? propTabBeforeEdit;
    int materialSlot;

    static readonly (PropTab Tab, string Tip, uint Colour)[] PropTabs =
    {
        (PropTab.Map, "Map: GTA world, .ymap files, place props, save the map resource", 0xFF60D060),
        (PropTab.Tool, "Tool: add objects, 3D cursor, snapping", 0xFFB0B0B0),
        (PropTab.Scene, "Scene: lighting, import options, preferences, checks", 0xFFB0B0B0),
        (PropTab.Object, "Object: name, transform, origin, visibility", 0xFF3D9BF5),   // orange (ABGR)
        (PropTab.Modifiers, "Modifiers", 0xFFE0A050),                                   // blue wrench
        (PropTab.Data, "Edit: modeling tools (extrude, bevel, make planar...), UV, vertex colour, normals", 0xFF60C060), // green triangle
        (PropTab.Material, "Material: slots and GTA shader", 0xFF6060E0),                  // red sphere
        (PropTab.Light, "Light: scene lights, light settings, sun, sky, shadows, exposure", 0xFF50D8F8),
        (PropTab.Paint, "Molo Paint: texture painting, smart materials, GTA library", 0xFF40A0F0),
        (PropTab.Gta, "GTA: object type, role, collision, animation", 0xFF40C8F0),
        (PropTab.Weapon, "Weapon Forge: any model becomes a GTA weapon (game weapon as donor)", 0xFF4080F0),
        (PropTab.Vehicle, "Vehicle Forge: any car model becomes a FiveM vehicle (game car as donor)", 0xFF40D0A0),
        (PropTab.Tripo, "Tripo: AI 3D models", 0xFFE070C0),
        (PropTab.Addons, "Add-ons: panels of your add-ons", 0xFFC0C0C0),
    };

    void DrawPropertiesEditor()
    {
        if (!ShowPanel) return;
        float y = menuH + toolbarH;
        ImGui.SetNextWindowPos(new Vector2(gpu.Width - panelW, y));
        ImGui.SetNextWindowSize(new Vector2(panelW, gpu.Height - y - statusH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.Begin("##properties", Fixed);
        ImGui.PopStyleVar();

        // tab column
        const float tabW = 30;
        ImGui.BeginChild("##ptabs", new Vector2(tabW, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(), ImGui.ColorConvertFloat4ToU32(Theme.PanelDark));
        ImGui.SetCursorPosY(6);
        foreach (var (tab, tip, col) in PropTabs)
        {
            if (tab == PropTab.Addons && !AnyAddonPanels) continue; // only when an add-on adds a panel
            if (MapMode ? tab is PropTab.Weapon or PropTab.Vehicle || !MapEditable(Current) && tab is PropTab.Modifiers or PropTab.Data or PropTab.Paint or PropTab.Tripo or PropTab.Tool or PropTab.Material : tab == PropTab.Map) continue;
            if (tab == PropTab.Object || tab == PropTab.Gta) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6); // groups, like Blender
            var p = ImGui.GetCursorScreenPos() + new Vector2(3, 0);
            ImGui.SetCursorScreenPos(p);
            bool on = propTab == tab;
            ImGui.PushID((int)tab);
            if (ImGui.InvisibleButton("##t", new Vector2(24, 24))) propTab = tab;
            bool hov = ImGui.IsItemHovered();
            ImGui.PopID();
            if (on || hov) dl.AddRectFilled(p, p + new Vector2(24, 24), ImGui.ColorConvertFloat4ToU32(on ? Theme.Panel : Theme.Button), 4);
            DrawTabIcon(dl, tab, p + new Vector2(12, 12), col);
            if (hov) ImGui.SetTooltip(tip);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X, p.Y + 27));
        }
        ImGui.Dummy(Vector2.Zero); // ImGui needs an item after moving the cursor
        ImGui.EndChild();
        ImGui.SameLine(0, 0);

        // entering edit mode brings the edit tools (Data tab) to the front; leaving it goes back to where you were
        if (Level != propLastLevel)
        {
            if (propLastLevel == SubObject.Object && Level != SubObject.Object && propTab is PropTab.Object or PropTab.Tool or PropTab.Scene or PropTab.Modifiers)
            { propTabBeforeEdit = propTab; propTab = PropTab.Data; }
            else if (Level == SubObject.Object && propTabBeforeEdit is { } back && propTab == PropTab.Data) propTab = back;
            if (Level == SubObject.Object) propTabBeforeEdit = null;
            propLastLevel = Level;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        ImGui.BeginChild("##pbody", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
        switch (propTab)
        {
            case PropTab.Map: MapProps(); break;
            case PropTab.Tool: ToolProps(); break;
            case PropTab.Scene: SceneProps(); break;
            case PropTab.Object: ObjectProps(); break;
            case PropTab.Modifiers: ModifierProps(); break;
            case PropTab.Data: DataProps(); break;
            case PropTab.Material: MaterialProps(); break;
            case PropTab.Light: LightTabProps(); break;
            case PropTab.Paint: PaintProps(); break;
            case PropTab.Gta: HubObject(); break;
            case PropTab.Weapon: WeaponProps(); break;
            case PropTab.Vehicle: VehicleProps(); break;
            case PropTab.Tripo: HubTripo(); break;
            case PropTab.Addons: AddonProps(); break;
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.End();
    }

    /// <summary>Small vector icons in Blender's colours.</summary>
    static void DrawTabIcon(ImDrawListPtr dl, PropTab tab, Vector2 c, uint col)
    {
        switch (tab)
        {
            case PropTab.Weapon: // pistol
                dl.AddRectFilled(c + new Vector2(-7, -4), c + new Vector2(7, 0), col, 1);
                dl.AddRectFilled(c + new Vector2(-6, 0), c + new Vector2(-2, 7), col, 1);
                dl.AddLine(c + new Vector2(-1, 0), c + new Vector2(1, 3), col, 1.5f);
                break;
            case PropTab.Vehicle: // car
                dl.AddRectFilled(c + new Vector2(-8, -1), c + new Vector2(8, 3), col, 2);
                dl.AddRectFilled(c + new Vector2(-4, -5), c + new Vector2(4, -1), col, 2);
                dl.AddCircleFilled(c + new Vector2(-4.5f, 4), 2.3f, col); dl.AddCircleFilled(c + new Vector2(4.5f, 4), 2.3f, col);
                break;
            case PropTab.Map: // globe
                dl.AddCircle(c, 7, col, 16, 1.6f);
                dl.AddLine(c - new Vector2(7, 0), c + new Vector2(7, 0), col, 1.2f);
                dl.AddBezierCubic(c - new Vector2(0, 7), c + new Vector2(-5, -3), c + new Vector2(-5, 3), c + new Vector2(0, 7), col, 1.2f);
                dl.AddBezierCubic(c - new Vector2(0, 7), c + new Vector2(5, -3), c + new Vector2(5, 3), c + new Vector2(0, 7), col, 1.2f);
                break;
            case PropTab.Tool: // screwdriver
                dl.AddLine(c + new Vector2(-6, 6), c + new Vector2(2, -2), col, 2.5f);
                dl.AddRectFilled(c + new Vector2(1, -7), c + new Vector2(7, -1), col, 1);
                break;
            case PropTab.Scene: // cone and sphere
                dl.AddTriangleFilled(c + new Vector2(-7, 6), c + new Vector2(-1, 6), c + new Vector2(-4, -6), col);
                dl.AddCircleFilled(c + new Vector2(4, 2), 4, col);
                break;
            case PropTab.Object: // orange square
                dl.AddRect(c - new Vector2(6, 6), c + new Vector2(6, 6), col, 2, ImDrawFlags.None, 2);
                dl.AddRectFilled(c - new Vector2(3, 3), c + new Vector2(3, 3), col, 1);
                break;
            case PropTab.Modifiers: // wrench
                dl.AddLine(c + new Vector2(-6, 6), c + new Vector2(2, -2), col, 3f);
                dl.AddCircle(c + new Vector2(3.5f, -3.5f), 4, col, 12, 2.5f);
                break;
            case PropTab.Data: // mesh triangle
                var a = c + new Vector2(0, -7); var b = c + new Vector2(-7, 6); var d = c + new Vector2(7, 6);
                dl.AddTriangle(a, b, d, col, 2);
                foreach (var v in new[] { a, b, d }) dl.AddCircleFilled(v, 2.2f, col);
                break;
            case PropTab.Material: // shaded sphere
                dl.AddCircleFilled(c, 7, col, 20);
                dl.AddCircleFilled(c + new Vector2(-2.5f, -2.5f), 2.5f, 0xC0FFFFFF, 12);
                break;
            case PropTab.Light: // bulb
                dl.AddCircleFilled(c + new Vector2(0, -2), 5, col, 16);
                dl.AddRectFilled(c + new Vector2(-2.5f, 3), c + new Vector2(2.5f, 7), col, 1);
                foreach (var a2 in new[] { -2.4f, -1.57f, -0.75f })
                    dl.AddLine(c + new Vector2(0, -2) + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * 7, c + new Vector2(0, -2) + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * 9, col, 1.5f);
                break;
            case PropTab.Paint: // brush
                dl.AddLine(c + new Vector2(5, -6), c + new Vector2(-1, 1), col, 2.5f);
                dl.AddCircleFilled(c + new Vector2(-3, 4), 3.5f, col);
                break;
            case PropTab.Gta:
                dl.AddText(c - new Vector2(4, 7), col, "G");
                break;
            case PropTab.Tripo:
                dl.AddText(c - new Vector2(7, 7), col, "AI");
                break;
            case PropTab.Addons: // puzzle piece
                dl.AddRectFilled(c - new Vector2(6, 4), c + new Vector2(5, 7), col, 1);
                dl.AddCircleFilled(c + new Vector2(0, -5), 3, col);
                dl.AddCircleFilled(c + new Vector2(6, 2), 3, col);
                break;
        }
    }

    // ------------------------------------------------------------------------------------- Tool

    void ToolProps()
    {
        CreateTab();
        ScalePanel();
        if (Ui.Header("Snapping", Open))
        {
            ImGui.Checkbox("Snap (" + "Shift+Tab" + ")", ref Snap);
            ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Increment", ref GridStep, 0.005f, 0.001f, 100, "%.3f m");
            ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Rotation", ref AngleSnap, 0.5f, 0.5f, 90, "%.1f°");
        }
    }

    // ------------------------------------------------------------------------------------- Scene

    void SceneProps() => UtilitiesTab();

    // ------------------------------------------------------------------------------------- Object

    void ObjectProps()
    {
        var o = Current;
        if (o == null) { Ui.TextDisabled(Selected.Count > 1 ? $"{Selected.Count} objects selected, none active." : "No active object."); return; }
        ObjectHeader(o, "Object");
        if (Selected.Count > 1 && Ui.Header("Boolean", Open))
        {
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            if (ImGui.Button("Union", new Vector2(w3, 0))) Boolean(MoloMax.Core.Mesh.Csg.Op.Union); TutRect("bool_union"); ImGui.SameLine();
            if (ImGui.Button("Subtract", new Vector2(w3, 0))) Boolean(MoloMax.Core.Mesh.Csg.Op.Subtract); TutRect("bool_sub"); ImGui.SameLine();
            if (ImGui.Button("Intersect", new Vector2(w3, 0))) Boolean(MoloMax.Core.Mesh.Csg.Op.Intersect); TutRect("bool_int");
            Ui.TextDisabled($"Applies to {o.Name} (active). The other selected objects are the cutters, hidden afterwards.");
        }
        if (o.Shape != null) ShapePanel(o);
        if (Ui.Header("Transform", Open))
        {
            var pos = o.Position;
            if (Vec3Row("Location", ref pos, 0.01f, "%.3f m")) { var v = pos; Do("Location", () => o.Position = v); }
            var rot = QuatToEulerDeg(o.Rotation);
            if (Vec3Row("Rotation", ref rot, 0.5f, "%.1f°")) { var v = rot; Do("Rotation", () => o.Rotation = EulerDegToQuat(v)); }
            var sc = o.Scale;
            if (Vec3Row("Scale", ref sc, 0.01f, "%.3f")) { var v = sc; Do("Scale", () => o.Scale = v); }
            Ui.TextDisabled("Mode: XYZ Euler");
        }
        if (IsSplat(o))
        {
            SplatPanel(o);
            if (Ui.Header("Visibility", Open)) { bool hs = o.Hidden; if (ImGui.Checkbox("Hide in Viewport", ref hs)) o.Hidden = hs; }
            return;
        }
        if (Ui.Header("Origin & Apply", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (ImGui.Button("Origin to Geometry", new Vector2(w2, 0))) CenterPivot(); ImGui.SameLine();
            if (ImGui.Button("Origin to Base", new Vector2(w2, 0))) PivotToBase();
            if (ImGui.Button("Origin to 3D Cursor", new Vector2(w2, 0))) OriginToCursor(); ImGui.SameLine();
            if (ImGui.Button("Apply Rot & Scale", new Vector2(w2, 0))) ResetXForm();
            Theme.Tip("Ctrl+A > Rotation & Scale: bakes them into the mesh (needed for collisions and rooms).");
        }
        if (Ui.Header("Visibility", Open))
        {
            bool h = o.Hidden; if (ImGui.Checkbox("Hide in Viewport##2", ref h)) o.Hidden = h;
            bool f = o.Frozen; if (ImGui.Checkbox("Disable Selection", ref f)) o.Frozen = f;
            var wc = o.WireColour; if (ImGui.ColorEdit3("Viewport Display Colour", ref wc, ImGuiColorEditFlags.NoInputs)) o.WireColour = wc;
        }
        InstancePanel(o);
    }

    /// <summary>Instancing: how many objects share this mesh, and the buttons to add / select / break instances.</summary>
    void InstancePanel(SceneObject o)
    {
        if (o.GtaType is GtaObjectType.Light) return;
        int n = Scene.InstanceCount(o);
        if (!Ui.Header(n > 1 ? $"Instances ({n})###inst" : "Instances###inst", n > 1 ? Open : 0)) return;
        if (n > 1) Ui.TextColored(ColInstance, $"{n} objects share this mesh: editing one edits all.\nGTA export: one model, {n} placements.");
        else Ui.TextDisabled("Unique object. An instance shares this mesh\n(GTA: same model placed again, no extra file).");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Add Instance", new Vector2(w2, 0))) CloneSelection(true);
        Theme.Tip(Settings.Keymap == Keymap.Max ? "Ctrl+V > Instance (Shift+drag uses the same choice)" : "Alt+D");
        ImGui.SameLine();
        if (ImGui.Button("Copy", new Vector2(w2, 0))) CloneSelection(false);
        ImGui.BeginDisabled(n < 2);
        if (ImGui.Button("Select Instances", new Vector2(w2, 0))) SelectInstances(o);
        ImGui.SameLine();
        if (ImGui.Button("Make Unique", new Vector2(w2, 0))) MakeUniqueSelection();
        Theme.Tip("This object gets its own copy of the mesh (Make Unique)");
        ImGui.EndDisabled();
    }

    /// <summary>The object name field at the top of Object / Modifiers / Data / Material, like Blender.</summary>
    void ObjectHeader(SceneObject o, string what)
    {
        string name = o.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##objname", ref name, 63, ImGuiInputTextFlags.EnterReturnsTrue) && name.Trim().Length > 0) { var n = name.Trim(); Do("Rename", () => o.Name = n); }
        ImGui.Spacing();
    }

    static bool Vec3Row(string label, ref Vector3 v, float speed, string fmt)
    {
        ImGui.PushID(label);
        Ui.TextDisabled(label);
        bool c = false;
        string[] ax = { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            float x = i == 0 ? v.X : i == 1 ? v.Y : v.Z;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat($"##{ax[i]}", ref x, speed, 0, 0, $"{ax[i]}  {fmt}"))
            {
                if (i == 0) v.X = x; else if (i == 1) v.Y = x; else v.Z = x;
                c = true;
            }
        }
        ImGui.PopID();
        return c;
    }

    // ------------------------------------------------------------------------------------- Modifiers

    void ModifierProps()
    {
        var o = Current;
        if (o == null) { Ui.TextDisabled("No active object."); return; }
        ObjectHeader(o, "Modifiers");
        if (ImGui.Button("Add Modifier  v", new Vector2(-1, 24))) ImGui.OpenPopup("addmod");
        if (ImGui.BeginPopup("addmod"))
        {
            foreach (ModifierCategory cat in Enum.GetValues<ModifierCategory>())
            {
                var items = Modifier.Catalog.Where(c => c.Cat == cat).ToList();
                if (items.Count == 0) continue;
                if (ImGui.BeginMenu(cat.ToString()))
                {
                    foreach (var (name, _, make) in items)
                        if (ImGui.MenuItem(name))
                        {
                            var mod = make();
                            Do("Add " + name, () => { EndModifierEdit(o, false); mod.Name = ModifierStack.UniqueName(o.Modifiers, mod.Name); o.Modifiers.Add(mod); o.Touch(); });
                            activeModifier[o.Id] = o.Modifiers.Count - 1;
                        }
                    ImGui.EndMenu();
                }
            }
            ImGui.EndPopup();
        }
        ImGui.Spacing();
        var stack = o.FullStack;
        if (stack.Count == 0) { Ui.TextDisabled("No modifiers. They change the mesh without touching it until you Apply them (Ctrl+A on the panel)."); return; }

        int moveFrom = -1, moveTo = -1, remove = -1, apply = -1, dup = -1;
        int activeMod = ActiveModifierIndex(o);
        for (int i = 0; i < stack.Count; i++)
        {
            var m = stack[i];
            ImGui.PushID(i);
            bool editedHere = o.ModEdit != null && o.ModEdit.Index == i;
            // header: [v] Name [edit] [eye] [cam] [menu] [x]
            var start = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(w, 24), ImGui.ColorConvertFloat4ToU32(editedHere ? Theme.ToggleOn * new Vector4(1, 1, 1, 0.55f) : Theme.Rollout), 4);
            if (i == activeMod) ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(w, 24), ImGui.ColorConvertFloat4ToU32(Theme.Gold), 4, 0, 1.5f);
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(start, start + new Vector2(w, 24)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) activeModifier[o.Id] = i;
            ImGui.SetCursorScreenPos(start + new Vector2(2, 2));
            if (ImGui.ArrowButton("##exp", m.Expanded ? ImGuiDir.Down : ImGuiDir.Right)) m.Expanded = !m.Expanded;
            ImGui.SameLine(0, 4);
            string nm = m.Name;
            ImGui.SetNextItemWidth(w - 22 - 4 * 24 - 24);
            if (ImGui.InputText("##mname", ref nm, 63)) m.Name = nm;
            ImGui.SameLine(0, 2);
            if (HeaderToggle("E", m.ShowInEditMode, "Edit Mode: show in edit mode")) { m.ShowInEditMode ^= true; o.Touch(); }
            ImGui.SameLine(0, 2);
            if (HeaderToggle("V", m.ShowViewport, "Realtime: show in viewport")) { m.ShowViewport ^= true; o.Touch(); }
            ImGui.SameLine(0, 2);
            if (HeaderToggle("R", m.ShowRender, "Render: use in the GTA export")) { m.ShowRender ^= true; o.Touch(); }
            ImGui.SameLine(0, 2);
            if (ImGui.Button("v##menu", new Vector2(20, 20))) ImGui.OpenPopup("modmenu");
            ImGui.SameLine(0, 2);
            if (ImGui.Button("x##del", new Vector2(20, 20))) remove = i;
            Theme.Tip("Delete (Ctrl+X)");
            if (ImGui.BeginPopup("modmenu"))
            {
                if (ImGui.MenuItem("Apply", "Ctrl+A")) apply = i;
                if (ImGui.MenuItem("Duplicate", "Shift+D")) dup = i;
                ImGui.Separator();
                if (ImGui.MenuItem("Move to First", null, false, i > 0)) { moveFrom = i; moveTo = 0; }
                if (ImGui.MenuItem("Move to Last", null, false, i < stack.Count - 1)) { moveFrom = i; moveTo = stack.Count - 1; }
                if (ImGui.MenuItem("Move Up", null, false, i > 0)) { moveFrom = i; moveTo = i - 1; }
                if (ImGui.MenuItem("Move Down", null, false, i < stack.Count - 1)) { moveFrom = i; moveTo = i + 1; }
                ImGui.EndPopup();
            }
            // shortcuts while hovering the panel, like Blender
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(start, start + new Vector2(w, 24)))
            {
                var io = ImGui.GetIO();
                if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false)) apply = i;
                if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.X, false)) remove = i;
                if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.D, false)) dup = i;
            }
            ImGui.SetCursorScreenPos(start + new Vector2(0, 27));
            ImGui.Dummy(Vector2.Zero);
            if (m.Expanded)
            {
                ImGui.Indent(6);
                if (m is EditPolyModifier epm) EditPolyFields(o, i, epm);
                else if (m is UnwrapUvwModifier uwm) UnwrapFields(o, i, uwm);
                else if (m is WallOpeningModifier wom) WallOpeningFields(o, wom);
                else if (ModifierFields(m)) o.Touch();
                ImGui.Unindent(6);
            }
            ImGui.Spacing();
            ImGui.PopID();
        }
        if ((remove >= 0 || dup >= 0 || moveFrom >= 0 || apply >= 0) && o.ModEdit != null) EndModifierEdit(o);
        if (remove >= 0) Do("Remove Modifier", () => { o.Modifiers.RemoveAt(remove); o.Touch(); });
        else if (dup >= 0) Do("Duplicate Modifier", () => { var c = o.Modifiers[dup].Clone(); c.Name = ModifierStack.UniqueName(o.Modifiers, c.Name); o.Modifiers.Insert(dup + 1, c); o.Touch(); });
        else if (moveFrom >= 0) Do("Move Modifier", () => { var m = o.Modifiers[moveFrom]; o.Modifiers.RemoveAt(moveFrom); o.Modifiers.Insert(moveTo, m); o.Touch(); });
        else if (apply >= 0) ApplyModifier(o, apply);
    }

    /// <summary>Blender "Apply": bakes the modifiers up to and including this one into the mesh (only the first one, as Blender warns).</summary>
    void ApplyModifier(SceneObject o, int index)
    {
        if (Level != SubObject.Object) { SetStatus("Apply: go back to Object Mode first (Tab)."); return; }
        if (index != 0) SetStatus("Applied modifier was not first, result may not be as expected.");
        Do("Apply " + o.Modifiers[index].Name, () =>
        {
            var m = o.Modifiers[index];
            var mesh = m.Apply(o.Mesh.Clone(), false);
            o.Mesh = mesh;
            o.Modifiers.RemoveAt(index);
            subSel.Remove(o.Id);
            o.Touch();
        });
    }

    static bool HeaderToggle(string label, bool on, string tip)
    {
        bool r = Theme.Toggle(label, on, new Vector2(20, 20));
        Theme.Tip(tip);
        return r;
    }

    /// <summary>The properties of one modifier, as in Blender's panels. Returns true when something changed.</summary>
    bool ModifierFields(Modifier mod)
    {
        bool c = false;
        float lw = 110;
        void L(string s) { ImGui.AlignTextToFramePadding(); Ui.TextDisabled(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }
        bool Axes(string label, ref bool x, ref bool y, ref bool z)
        {
            L(label);
            float bw = (ImGui.GetContentRegionAvail().X - 4) / 3; bool r = false;
            if (Theme.Toggle("X##" + label, x, new Vector2(bw, 0))) { x ^= true; r = true; } ImGui.SameLine(0, 2);
            if (Theme.Toggle("Y##" + label, y, new Vector2(bw, 0))) { y ^= true; r = true; } ImGui.SameLine(0, 2);
            if (Theme.Toggle("Z##" + label, z, new Vector2(bw, 0))) { z ^= true; r = true; }
            return r;
        }
        switch (mod)
        {
            case MirrorModifier m:
                c |= Axes("Axis", ref m.AxisX, ref m.AxisY, ref m.AxisZ);
                c |= Axes("Bisect", ref m.BisectX, ref m.BisectY, ref m.BisectZ);
                c |= Axes("Flip", ref m.FlipX, ref m.FlipY, ref m.FlipZ);
                c |= ImGui.Checkbox("Clipping", ref m.Clipping);
                c |= ImGui.Checkbox("Merge", ref m.Merge);
                if (m.Merge) { L("Distance"); c |= ImGui.DragFloat("##md", ref m.MergeDistance, 0.0005f, 0, 1, "%.4f m"); }
                if (ImGui.TreeNode("Data"))
                {
                    c |= ImGui.Checkbox("Mirror U", ref m.MirrorU); ImGui.SameLine(); c |= ImGui.Checkbox("Mirror V", ref m.MirrorV);
                    L("Offset U"); c |= ImGui.DragFloat("##ou", ref m.OffsetU, 0.01f, -1, 1);
                    L("Offset V"); c |= ImGui.DragFloat("##ov", ref m.OffsetV, 0.01f, -1, 1);
                    ImGui.TreePop();
                }
                break;
            case SymmetryModifier m:
                {
                    L("Mirror Axis");
                    float bw3 = (ImGui.GetContentRegionAvail().X - 4) / 3;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        if (ax > 0) ImGui.SameLine(0, 2);
                        if (Theme.Toggle(new[] { "X", "Y", "Z" }[ax] + "##symax", m.Axis == ax, new Vector2(bw3, 0))) { m.Axis = ax; c = true; }
                    }
                    c |= ImGui.Checkbox("Flip", ref m.Flip);
                    c |= ImGui.Checkbox("Slice Along Mirror", ref m.Slice);
                    c |= ImGui.Checkbox("Weld Seam", ref m.Weld);
                    if (m.Weld) { L("Threshold"); c |= ImGui.DragFloat("##symth", ref m.Threshold, 0.0005f, 0, 1, "%.4f m"); }
                    L("Mirror position"); c |= ImGui.DragFloat("##symoff", ref m.Offset, 0.005f, -1000, 1000, "%.3f m");
                    Theme.Tip("Moves the mirror plane along the axis (the mirror gizmo).");
                    break;
                }
            case ArrayModifier m:
                L("Count"); c |= ImGui.DragInt("##cnt", ref m.Count, 0.1f, 1, 1000);
                c |= ImGui.Checkbox("Relative Offset", ref m.UseRelativeOffset);
                if (m.UseRelativeOffset) { L("Factor"); c |= ImGui.DragFloat3("##rel", ref m.RelativeOffset, 0.01f); }
                c |= ImGui.Checkbox("Constant Offset", ref m.UseConstantOffset);
                if (m.UseConstantOffset) { L("Distance"); c |= ImGui.DragFloat3("##con", ref m.ConstantOffset, 0.01f); }
                c |= ImGui.Checkbox("Merge##2", ref m.MergeVertices);
                if (m.MergeVertices) { L("Distance"); c |= ImGui.DragFloat("##amd", ref m.MergeDistance, 0.001f, 0, 10, "%.3f m"); }
                break;
            case SolidifyModifier m:
                L("Thickness"); c |= ImGui.DragFloat("##th", ref m.Thickness, 0.001f, -10, 10, "%.3f m");
                L("Offset"); c |= ImGui.SliderFloat("##off", ref m.Offset, -1, 1, "%.2f");
                c |= ImGui.Checkbox("Even Thickness", ref m.EvenThickness);
                c |= ImGui.Checkbox("Fill Rim", ref m.FillRim);
                c |= ImGui.Checkbox("Flip Normals", ref m.FlipNormals);
                break;
            case SubdivisionModifier m:
                int mode = m.CatmullClark ? 0 : 1;
                float bw2 = (ImGui.GetContentRegionAvail().X - 2) / 2;
                if (Theme.Toggle("Catmull-Clark", mode == 0, new Vector2(bw2, 0))) { m.CatmullClark = true; c = true; } ImGui.SameLine(0, 2);
                if (Theme.Toggle("Simple", mode == 1, new Vector2(bw2, 0))) { m.CatmullClark = false; c = true; }
                L("Levels Viewport"); c |= ImGui.SliderInt("##lv", ref m.LevelsViewport, 0, 4);
                L("Render"); c |= ImGui.SliderInt("##lr", ref m.LevelsRender, 0, 4);
                Ui.TextDisabled("Render = level used in the GTA export. Mind the 65k vertex limit.");
                break;
            case WeldModifier m:
                L("Distance"); c |= ImGui.DragFloat("##wd", ref m.Distance, 0.0005f, 0, 10, "%.4f m");
                break;
            case CleanUpModifier m:
                L("Flat angle"); c |= ImGui.SliderFloat("##ca", ref m.Angle, 0, 10, "%.1f deg");
                Theme.Tip("Faces this close to flat become one polygon. 0 = only perfectly flat areas. Higher = fewer faces on slightly curved surfaces.");
                c |= ImGui.Checkbox("Keep UV seams and colours", ref m.KeepUvSeams);
                Theme.Tip("Never merge across a texture seam or a vertex colour change: the texture stays exactly in place.");
                c |= ImGui.Checkbox("Remove points on straight edges", ref m.StraightPoints);
                c |= ImGui.Checkbox("Straight quads on flat areas", ref m.Quads);
                Theme.Tip("Flat areas rebuilt as rows of quads (closed meshes only, kept only if the shape and texture stay the same).");
                if (m.LastReport != null) Ui.TextDisabled(m.LastReport.ToString());
                Ui.TextDisabled("Nothing is deleted: remove the modifier to get every edge back.");
                break;
            case TriangulateModifier m:
                L("Minimum Vertices"); c |= ImGui.SliderInt("##mv", ref m.MinVertices, 4, 12);
                break;
            case SmoothModifier m:
                c |= Axes("Axis", ref m.X, ref m.Y, ref m.Z);
                L("Factor"); c |= ImGui.DragFloat("##sf", ref m.Factor, 0.01f, -10, 10);
                L("Repeat"); c |= ImGui.DragInt("##sr", ref m.Repeat, 0.1f, 0, 200);
                break;
            case SmoothByAngleModifier m:
                L("Angle"); c |= ImGui.SliderFloat("##ang", ref m.Angle, 0, 180, "%.0f°");
                break;
            case ProOptimizerModifier m:
                L("Vertex %"); c |= ImGui.SliderFloat("##popct", ref m.Percent, 1, 100, "%.1f %%");
                Theme.Tip("How many vertices to keep. 50 % = about half the polygons. UVs, colours and materials stay exact.");
                c |= ImGui.Checkbox("Keep borders", ref m.KeepBorders);
                c |= ImGui.Checkbox("Keep UV seams", ref m.KeepUvSeams);
                c |= ImGui.Checkbox("Keep material borders", ref m.KeepMaterialBorders);
                if (Current is { } po) { var ev = po.Evaluated(); Ui.TextDisabled($"{po.Mesh.Faces.Count:N0} -> {ev.Faces.Count:N0} polygons"); }
                break;
            case SkinModifier m:
                c |= SkinFields(m, L);
                break;
            case FfdModifier m:
                c |= FfdFields(m, L);
                break;
            case CastModifier m:
                L("Shape"); c |= ImGui.Combo("##shape", ref m.Shape, new[] { "Sphere", "Cylinder", "Cuboid" }, 3);
                L("Factor"); c |= ImGui.DragFloat("##cf", ref m.Factor, 0.01f, -2, 2);
                L("Radius"); c |= ImGui.DragFloat("##cr", ref m.Radius, 0.01f, 0, 1000, m.Radius <= 0 ? "auto" : "%.3f m");
                break;
            case DisplaceNoiseModifier m:
                L("Strength"); c |= ImGui.DragFloat("##ds", ref m.Strength, 0.005f, -10, 10, "%.3f m");
                L("Midlevel"); c |= ImGui.SliderFloat("##dm", ref m.MidLevel, 0, 1);
                L("Noise Size"); c |= ImGui.DragFloat("##dz", ref m.Size, 0.01f, 0.01f, 100, "%.2f m");
                L("Seed"); c |= ImGui.DragInt("##dseed", ref m.Seed, 0.2f, 0, 9999);
                break;
        }
        if (c) Dirty = true;
        return c;
    }

    // ------------------------------------------------------------------------------------- Data (mesh)

    (int, int, PolyMesh) evTriKey; int evTris;

    void DataProps()
    {
        var o = Current;
        if (o == null) { Ui.TextDisabled("No active object."); return; }
        ObjectHeader(o, "Data");
        var ev = o.Evaluated();
        // counts cached per version: walking 500k polygons every frame cost ~10 ms
        Ui.TextDisabled($"Mesh: {o.Mesh.Verts.Count:N0} verts, {o.Mesh.Faces.Count:N0} faces, {o.TriangleCount:N0} tris");
        if (ev != o.Mesh)
        {
            if (evTriKey != (o.Id, o.Version, ev)) { evTris = ev.TriangleCount; evTriKey = (o.Id, o.Version, ev); }
            Ui.TextDisabled($"With modifiers: {ev.Verts.Count:N0} verts, {evTris:N0} tris");
        }

        if (Level != SubObject.Object)
        {
            if (Ui.Header("Select Mode", Open))
            {
                // like 3ds Max: Poly one face at a time, Element the whole connected piece
                float bw = (ImGui.GetContentRegionAvail().X - 6) / 4;
                var modes = new[] { ("Vertex", SubObject.Vertex, "1"), ("Edge", SubObject.Edge, "2"), ("Poly", SubObject.Polygon, "3"), ("Element", SubObject.Element, "4") };
                for (int i = 0; i < modes.Length; i++)
                {
                    if (i > 0) ImGui.SameLine(0, 2);
                    var (label, lv, key) = modes[i];
                    if (Theme.Toggle(label, Level == lv, new Vector2(bw, 0))) SetLevelDirect(lv);
                    Theme.Tip(lv switch
                    {
                        SubObject.Polygon => "Poly: faces one by one",
                        SubObject.Element => "Element: click a face, the whole connected piece is selected",
                        _ => label,
                    } + (BlenderKeymap ? $"  ({key})" : ""));
                }
                Ui.TextDisabled($"{SubSel(o).Count(Level)} selected");
            }
            EditMeshTools(o);
        }
        else Ui.TextDisabled("Tab: Edit Mode for the mesh tools.");

        if (Ui.Header("Normals", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##asa", ref autoSmoothA, 0.5f, 0, 180, "%.0f°"); ImGui.SameLine();
            if (ImGui.Button("Smooth by Angle", new Vector2(w2, 0))) AutoSmooth(autoSmoothA);
            if (ImGui.Button("Shade Flat", new Vector2(w2, 0))) ClearSmoothing(); ImGui.SameLine();
            if (ImGui.Button("Recalculate Outside", new Vector2(w2, 0))) RecalcNormals(false);
        }
        if (Ui.Header("UV Maps", Open))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (ImGui.Button("Smart UV Project", new Vector2(w2, 0))) SmartUv();
            ImGui.SameLine(); if (ImGui.Button("UV Editor##meshtools", new Vector2(w2, 0))) OpenUvEditor();
            Theme.Tip("Unwrap: cuts the mesh into flat islands (66° angle limit) packed in 0..1. Use it before painting or baking. Works on the selected faces in Poly mode, else the whole object.");
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##tile", ref boxTile, 0.05f, 0.05f, 100, "%.2f m / tile"); ImGui.SameLine();
            if (ImGui.Button("Cube Projection", new Vector2(w2, 0))) BoxMap(boxTile, boxMapWorld);
            ImGui.Checkbox("World space (textures line up across objects)", ref boxMapWorld);
        }
        ColourRollout();
    }

    /// <summary>Edit-mode mesh operators (Blender keeps them in the toolbar and the Mesh / Vertex / Edge / Face menus).</summary>
    void EditMeshTools(SceneObject o)
    {
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (Ui.Header("Mesh Tools", Open))
        {
            if (ImGui.Button("Extrude (E)", new Vector2(w2, 0))) BeginModal(ModalKind.Extrude); ImGui.SameLine();
            if (ImGui.Button("Inset (I)", new Vector2(w2, 0))) BeginModal(ModalKind.Inset);
            if (ImGui.Button("Loop Cut (Ctrl+R)", new Vector2(w2, 0))) BeginModal(ModalKind.LoopCut); ImGui.SameLine();
            if (ImGui.Button("Fill (F)", new Vector2(w2, 0))) MakeFace();
            if (ImGui.Button("Subdivide", new Vector2(w2, 0))) Tessellate(); ImGui.SameLine();
            if (ImGui.Button("Flip Normals##2", new Vector2(w2, 0))) FlipFaces();
            if (ImGui.Button("Separate (P)", new Vector2(w2, 0))) DetachFaces(false); ImGui.SameLine();
            if (ImGui.Button("Fill Holes", new Vector2(w2, 0))) Cap();
            if (ImGui.Button("Connect", new Vector2(w2, 0))) ConnectEdges(); ImGui.SameLine();
            if (ImGui.Button("Dissolve", new Vector2(w2, 0))) MaxRemove();
            if (ImGui.Button("Chamfer (Ctrl+B)", new Vector2(w2, 0))) BeginModal(ModalKind.Chamfer); ImGui.SameLine();
            if (ImGui.Button("Knife (" + K("K", "Shift+K") + ")", new Vector2(w2, 0))) BeginModal(ModalKind.Knife);
            if (ImGui.Button("QuickSlice", new Vector2(-1, 0))) BeginModal(ModalKind.QuickSlice);
            Theme.Tip("QuickSlice: two clicks slice the whole object (or the selected polygons) along the line. Shift = 45°.");
            if (ImGui.Button("Bridge", new Vector2(w2, 0))) Bridge(); ImGui.SameLine();
            if (ImGui.Button("UV Editor##uvmaps", new Vector2(w2, 0))) OpenUvEditor();
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##weldt", ref weldT, 0.001f, 0.0001f, 10f, "%.4f m"); ImGui.SameLine();
            if (ImGui.Button("Merge by Distance", new Vector2(w2, 0))) Weld(weldT);
            if (ImGui.Button("Select More", new Vector2(w2, 0))) Grow(); ImGui.SameLine();
            if (ImGui.Button("Select Less", new Vector2(w2, 0))) Shrink();
            if (Level == SubObject.Edge)
            {
                if (ImGui.Button("Edge Loops", new Vector2(w2, 0))) Loop(); ImGui.SameLine();
                if (ImGui.Button("Edge Rings", new Vector2(w2, 0))) Ring();
            }
        }
        if (Ui.Header("Align / Make Planar", Open)) MakePlanarUi();
        if ((Level == SubObject.Polygon || Level == SubObject.Element) && Ui.Header("Faces (exact values)"))
        {
            ImGui.Checkbox("Individual faces", ref byPolygon);
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##exh", ref extrudeH, 0.01f, -100, 100, "%.3f m"); ImGui.SameLine();
            if (ImGui.Button("Extrude##v", new Vector2(w2, 0))) ExtrudeFaces(extrudeH, byPolygon);
            ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##ins", ref insetA, 0.005f, -100, 100, "%.3f m"); ImGui.SameLine();
            if (ImGui.Button("Inset##v", new Vector2(w2, 0))) InsetFaces(insetA, byPolygon);
        }
    }

    // ------------------------------------------------------------------------------------- Material

    int slotListObj = -1, slotMatId = -1;
    readonly Dictionary<int, (int Version, PolyMesh Mesh, List<int> Ids)> usedMats = new();

    /// <summary>The material ids an object's polygons use, in first-use order (cached per mesh version: a 500k-polygon
    /// room was rescanned every frame by the Material and Paint tabs).</summary>
    List<int> UsedMaterials(SceneObject o)
    {
        if (usedMats.TryGetValue(o.Id, out var c) && c.Version == o.Version && ReferenceEquals(c.Mesh, o.Mesh)) return c.Ids;
        var seen = new HashSet<int>(); var list = new List<int>();
        foreach (var f in o.Mesh.Faces) if (seen.Add(f.MatId)) list.Add(f.MatId);
        if (usedMats.Count > 256) usedMats.Clear();
        usedMats[o.Id] = (o.Version, o.Mesh, list);
        return list;
    }

    void MaterialProps()
    {
        var o = Current;
        if (o == null) { Ui.TextDisabled("No active object."); return; }
        ObjectHeader(o, "Material");
        // slots: the materials this object uses, in first-use order
        var slots = new List<int>(UsedMaterials(o));
        if (slots.Count == 0) slots.Add(0);
        // the picked slot follows its material (the list order changes after an assign)
        if (slotMatId >= 0 && slots.IndexOf(slotMatId) is int keep && keep >= 0) materialSlot = keep;
        int slotBefore = materialSlot;
        if (SyncMaterialToFaces(o) is int fm && slots.IndexOf(fm) is int si && si >= 0) materialSlot = si;
        materialSlot = Math.Clamp(materialSlot, 0, slots.Count - 1);
        // the list scrolls to the material of the picked faces (like the Scene Explorer goes to the picked object)
        bool scrollToSlot = materialSlot != slotBefore || o.Id != slotListObj; slotListObj = o.Id;
        ImGui.BeginChild("##slots", new Vector2(-28, Math.Min(6, slots.Count) * ImGui.GetTextLineHeightWithSpacing() + 10), ImGuiChildFlags.Borders);
        for (int i = 0; i < slots.Count; i++)
        {
            var m = Scene.Material(slots[i]);
            var c = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddCircleFilled(c + new Vector2(6, 8), 5, ImGui.ColorConvertFloat4ToU32(new Vector4(m.ViewportColour, 1)));
            ImGui.Dummy(new Vector2(14, 0)); ImGui.SameLine();
            if (ImGui.Selectable($"{m.Name}##slot{i}", materialSlot == i, ImGuiSelectableFlags.AllowDoubleClick))
            {
                // a click only picks the material (then Assign puts it on the selected faces); a double-click selects its faces
                materialSlot = i; slotMatId = m.Id; selectedMaterial = m.Id;
                if (Level != SubObject.Object && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (Input.Ctrl) { Level = SubObject.Polygon; SubSel(o).Faces.UnionWith(MeshSelect.FacesByMaterial(o.Mesh, m.Id)); }
                    else SelectByMaterial(m.Id);
                }
            }
            if (ImGui.IsItemHovered() && Level != SubObject.Object) ImGui.SetTooltip("Click: pick it (Assign puts it on the selected faces) · Double-click: select its faces · Ctrl+double-click: add them");
            if (scrollToSlot && i == materialSlot && !ImGui.IsItemVisible()) ImGui.SetScrollHereY(0.5f);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginGroup();
        if (ImGui.Button("+", new Vector2(22, 22)))
        {
            Undo.Record("New Material", Scene);
            var nm = Scene.AddMaterial("material_" + Scene.NextMaterialId, new Vector3(0.7f));
            selectedMaterial = nm.Id;
            if (Level != SubObject.Object) AssignMaterial(nm.Id);
            SetStatus($"{nm.Name} created" + (Level != SubObject.Object ? " and assigned to the selected faces." : ". Assign it in Edit Mode, or pick it for the slot below."));
        }
        Theme.Tip("New material");
        if (ImGui.Button("L", new Vector2(22, 22))) OpenLibrary(LibraryAction.Assign);
        Theme.Tip("Molo Materials: pick a GTA material from your texture library");
        EyedropperButton("eyedrop_tab");
        PasteMaterialButton("paste_tab");
        ImGui.EndGroup();

        var slotMat = Scene.Material(slots[materialSlot]);
        slotMatId = slotMat.Id;
        // browse: replace the material of this slot on the object
        var names = Scene.Materials.Select(m => m.Name).ToArray();
        int bi = Math.Max(0, Scene.Materials.FindIndex(m => m.Id == slotMat.Id));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##browse", ref bi, names, names.Length) && Scene.Materials[bi].Id != slotMat.Id)
        {
            int from = slotMat.Id, to = Scene.Materials[bi].Id;
            DoMesh("Change Material", (oo, mm, s) => { foreach (var f in mm.Faces) if (f.MatId == from) f.MatId = to; });
        }
        if (Level != SubObject.Object)
        {
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            if (ImGui.Button("Assign", new Vector2(w3, 0)))
            {
                int nSel = SubSel(o).Faces.Count;
                if (Level is not (SubObject.Polygon or SubObject.Element) || nSel == 0) SetStatus("Select faces first (Polygon mode), then Assign.");
                else { AssignMaterial(slotMat.Id); SetStatus($"{slotMat.Name} put on {nSel} face(s)"); }
            }
            Theme.Tip("Puts this material on the selected faces");
            ImGui.SameLine();
            if (ImGui.Button("Select", new Vector2(w3, 0))) { SelectByMaterial(slotMat.Id); SetStatus($"{SubSel(o).Faces.Count} face(s) with {slotMat.Name} selected"); }
            Theme.Tip("Selects every face that has this material");
            ImGui.SameLine();
            if (ImGui.Button("Deselect", new Vector2(w3, 0))) { var s = SubSel(o); int n0 = s.Faces.Count; s.Faces.RemoveWhere(f => f < o.Mesh.Faces.Count && o.Mesh.Faces[f].MatId == slotMat.Id); SetStatus($"{n0 - s.Faces.Count} face(s) with {slotMat.Name} deselected"); }
            Theme.Tip("Removes the faces with this material from the selection");
        }
        ImGui.Separator();
        MaterialSettings(slotMat);
    }

    /// <summary>GTA shader settings of a material (shared by the Material tab and the material editor window).</summary>
    void MaterialSettings(GtaMaterial mat)
    {
        string mname = mat.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##mname_2", ref mname, 63)) mat.Name = mname;
        var shaders = GtaShaders.All;
        int si = Math.Max(0, shaders.ToList().FindIndex(s => s.Name == mat.Shader));
        Ui.TextDisabled("Shader");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##shader", ref si, shaders.Select(s => $"{s.Name}.sps  -  {s.Label}").ToArray(), shaders.Count)) { Undo.Record("Shader", Scene); mat.Shader = shaders[si].Name; Dirty = true; }
        var p = mat.Preset;
        Ui.TextWrapped(p.Notes);
        if (Ui.Header("Textures", Open))
        {
            if (mat.IsTerrain) TerrainLayerSlots(mat);
            else
            {
                TexSlot("Diffuse", ref mat.DiffuseTexture, true);
                TexSlot("Normal", ref mat.BumpTexture, p.Bump);
                TexSlot("Specular", ref mat.SpecTexture, p.Spec);
                TexSlot("Detail", ref mat.DetailTexture, p.Detail);
            }
        }
        if (Ui.Header("Parameters", Open))
        {
            var vc = mat.ViewportColour;
            if (ImGui.ColorEdit3("Viewport colour", ref vc, ImGuiColorEditFlags.NoInputs)) mat.ViewportColour = vc;
            if (p.Bump) { ImGui.SetNextItemWidth(-120); ImGui.SliderFloat("bumpiness", ref mat.Bumpiness, 0, 4); }
            if (p.Spec || p.Glass)
            {
                ImGui.SetNextItemWidth(-120); ImGui.SliderFloat("spec intensity", ref mat.SpecularIntensity, 0, 2);
                ImGui.SetNextItemWidth(-120); ImGui.SliderFloat("spec falloff", ref mat.SpecularFalloff, 1, 512);
                ImGui.SetNextItemWidth(-120); ImGui.SliderFloat("spec fresnel", ref mat.SpecularFresnel, 0, 1);
            }
            if (p.Emissive) { ImGui.SetNextItemWidth(-120); ImGui.SliderFloat("emissive mult", ref mat.EmissiveMultiplier, 0, 16); }
            if (p.Detail) { var ds = mat.DetailSettings; ImGui.SetNextItemWidth(-120); if (ImGui.DragFloat4("detail", ref ds, 0.01f)) mat.DetailSettings = ds; }
            Ui.TextDisabled($"Render bucket {p.RenderBucket}{(p.AlphaBlended ? ", alpha blended" : "")}{(p.Emissive ? ", emissive" : "")}");
        }
    }
}
