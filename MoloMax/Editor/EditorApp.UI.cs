using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

public sealed partial class EditorApp
{
    const ImGuiWindowFlags Fixed = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                                 | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

    bool materialEditorOpen, shortcutsOpen, aboutOpen;
    int selectedMaterial;
    int renameId = -1; string renameBuf = ""; bool renameFocus;
    string explorerFilter = "";
    float extrudeH = 0.5f, insetA = 0.1f, bevelH = 0.3f, bevelO = -0.05f, weldT = 0.01f, autoSmoothA = 30f, boxTile = 1f;
    bool byPolygon, detachAsClone, boxMapWorld = true, removeWithVerts = true;
    int attachIndex;
    Vector3 customColour = new(1, 1, 1);
    readonly List<string> checkResults = new();
    CreateKind lastCreateKind = CreateKind.Box;

    static readonly string[] snapSteps = { "0.01", "0.05", "0.1", "0.25", "0.5", "1", "5" };

    // ---------------------------------------------------------------- menu bar

    void DrawMainMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        menuH = ImGui.GetWindowSize().Y;
        // brand mark at the left of the menu bar
        {
            var p0 = ImGui.GetCursorScreenPos(); float lh = menuH - 4;
            Ui.DrawLogo(ImGui.GetWindowDrawList(), new Vector2(p0.X - lh * 0.12f, p0.Y + 1), lh, dots: false);
            ImGui.Dummy(new Vector2(lh * 0.72f, lh));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Polymorph {AppInfo.Version} - {AppInfo.Tagline}");
            if (ImGui.IsItemClicked()) aboutOpen = true;
        }
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New", "Ctrl+N")) NewScene();
            if (ImGui.MenuItem("Open...", "Ctrl+O")) OpenDialog();
            RecentMenu();
            if (ImGui.MenuItem("Save", "Ctrl+S")) Save(false);
            if (ImGui.MenuItem("Save As...", "Ctrl+Shift+S")) Save(true);
            ImGui.Separator();
            if (ImGui.MenuItem("Import model (GLB, glTF, OBJ)...", "Ctrl+Shift+O")) ImportDialog();
            if (ImGui.MenuItem("Export models (.ydr / .yft)...")) OpenModelExport();
            if (ImGui.MenuItem("Export OBJ (all)...")) ExportObj(false);
            if (ImGui.MenuItem("Export OBJ (selected)...", null, false, Selected.Count > 0)) ExportObj(true);
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) form.BeginInvoke(new Action(form.Close));
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.BeginMenu("Look (theme)"))
            {
                if (ImGui.MenuItem("Standard", null, Settings.Mode == UiMode.Blender)) SetMode(UiMode.Blender);
                if (ImGui.MenuItem("Classic (4 views)", null, Settings.Mode == UiMode.Max)) SetMode(UiMode.Max);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(Undo.CanUndo ? $"Undo {Undo.UndoName}" : "Undo", "Ctrl+Z", false, Undo.CanUndo)) DoUndo();
            if (ImGui.MenuItem(Undo.CanRedo ? $"Redo {Undo.RedoName}" : "Redo", K("Ctrl+Shift+Z", "Ctrl+Shift+Z", "Ctrl+Y"), false, Undo.CanRedo)) DoRedo();
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", K("X", "Del"))) DeleteSelection();
            if (ImGui.MenuItem(K("Duplicate", "Duplicate", "Clone"), K("Shift+D", "Ctrl+D", "Ctrl+V"))) { if (Settings.Keymap == Keymap.Blender) DuplicateAndGrab(); else CloneSelection(false); }
            if (ImGui.MenuItem("Instance", K("Alt+D", "", "Ctrl+V"), false, Selected.Count > 0)) CloneSelection(true);
            if (ImGui.MenuItem("Make Unique", null, false, SelObjects().Any(o => o.InstanceKey != null))) MakeUniqueSelection();
            if (ImGui.MenuItem("Join", "Ctrl+J")) JoinSelected();
            ImGui.Separator();
            if (ImGui.MenuItem("Select All", K("A", "Ctrl+A"))) SelectAll();
            if (ImGui.MenuItem("Select None", K("Alt+A", "Ctrl+Shift+A", "Ctrl+D"))) SelectNone();
            if (ImGui.MenuItem("Select Invert", K("Ctrl+I", "Ctrl+I", "Ctrl+I"))) SelectInvert();
            if (Settings.Keymap == Keymap.Max && ImGui.MenuItem("Select by Name...", "H")) { OpenPopup("selectbyname"); selectByNameFilter = ""; }
            ImGui.Separator();
            if (ImGui.MenuItem("Hide Selection", K("H", "Ctrl+H", ""))) HideSelection();
            if (ImGui.MenuItem("Hide Unselected", K("Shift+H", "Shift+H", ""))) HideUnselected();
            if (ImGui.MenuItem("Unhide All", K("Alt+H", "Alt+H", ""))) UnhideAll();
            if (ImGui.MenuItem("Isolate Selection", K("Numpad /", "Shift+I", "Alt+Q"))) IsolateSelection();
            ImGui.Separator();
            if (ImGui.MenuItem("Search Commands...", SearchKey)) OpenSearch();
            if (ImGui.MenuItem("Preferences...", "Ctrl+,")) prefsOpen = true;
            ImGui.EndMenu();
        }
        if (addons.Count > 0 && ImGui.BeginMenu("Add-ons"))
        {
            foreach (var a in addons.Where(a => a.Registered))
                foreach (var name in a.Commands)
                    if (ImGui.MenuItem(name)) Commands().FirstOrDefault(c => c.Name == name)?.Run();
            ImGui.Separator();
            if (ImGui.MenuItem("Manage add-ons...")) { prefsOpen = true; prefsSection = 5; }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Tools"))
        {
            if (ImGui.MenuItem("Material Editor", "M", materialEditorOpen)) materialEditorOpen = !materialEditorOpen;
            if (ImGui.MenuItem("Sculpt Mode", null, SculptMode, Current != null)) ToggleSculptMode();
            if (ImGui.MenuItem("Run Python Script...")) RunScriptDialog();
            if (ImGui.MenuItem("Python & MCP (Claude)...")) { prefsOpen = true; prefsSection = 8; }
            ImGui.Separator();
            if (ImGui.MenuItem("Apply Rotation & Scale", K("Ctrl+A", ""))) ResetXForm();
            if (ImGui.MenuItem("Origin to Geometry")) CenterPivot();
            if (ImGui.MenuItem("Origin to Bottom")) PivotToBase();
            if (ImGui.MenuItem("Origin to 3D Cursor")) OriginToCursor();
            if (ImGui.BeginMenu("Mirror"))
            {
                if (ImGui.MenuItem("X")) MirrorObjects(0);
                if (ImGui.MenuItem("Y")) MirrorObjects(1);
                if (ImGui.MenuItem("Z")) MirrorObjects(2);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Check Scene for GTA")) { RunGtaChecks(); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Create"))
        {
            Ui.TextDisabled("Added at the 3D cursor (Shift+A)");
            foreach (CreateKind k in Enum.GetValues<CreateKind>())
                if (k != CreateKind.None && ImGui.MenuItem(KindLabel(k))) { AddPrimitive(k); lastCreateKind = k; }
            ImGui.Separator();
            if (ImGui.MenuItem("Line (shape)")) BeginLine();
            ImGui.Separator();
            if (ImGui.MenuItem("Light (point)")) AddLight(GtaLightKind.Point);
            if (ImGui.MenuItem("Light (spot)")) AddLight(GtaLightKind.Spot);
            ImGui.Separator();
            if (ImGui.MenuItem("Drawable from selection", null, false, Selected.Count > 0)) CreateDrawable();
            if (ImGui.MenuItem("Collision from selection", null, false, Selected.Count > 0)) CollisionFromSelection();
            ImGui.Separator();
            if (ImGui.MenuItem("Import model...", "Ctrl+Shift+O")) ImportDialog();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("GTA"))
        {
            if (ImGui.MenuItem("GTA panel", "F10", HubOpen)) HubOpen = !HubOpen;
            if (ImGui.MenuItem("Game files browser (GTA V)...")) OpenGameBrowser();
            if (ImGui.MenuItem("Asset Browser")) OpenAssetBrowser();
            if (ImGui.MenuItem("Import MLO (.ytyp)...")) ImportMloDialog();
            if (ImGui.MenuItem("MLO Room Builder (floor plan)...")) OpenRoomBuilder();
            ImGui.Separator();
            if (ImGui.MenuItem("Export models (.ydr / .yft)...")) OpenModelExport();
            if (ImGui.MenuItem("Export FiveM resource...")) OpenHub(1);
            if (ImGui.MenuItem("MLO builder")) OpenHub(2);
            if (ImGui.MenuItem("Object properties")) OpenHub(3);
            if (ImGui.MenuItem("Collision")) OpenHub(4);
            if (ImGui.MenuItem("Checks")) { OpenHub(5); RunGtaChecks(); }
            ImGui.Separator();
            if (ImGui.MenuItem("Tripo (AI 3D models)")) OpenHub(7);
            if (ImGui.MenuItem("Molo Materials (GTA texture library)")) OpenLibrary(LibraryAction.Browse);
            if (ImGui.MenuItem("Molo Paint")) { propTab = PropTab.Paint; ShowPanel = true; }
            if (ImGui.MenuItem("Molo Tools")) OpenHub(8);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Views"))
        {
            if (ImGui.MenuItem("Maximize Viewport Toggle", K("Ctrl+Alt+Q", "", "Alt+W"), Maximized)) Maximized = !Maximized;
            if (ImGui.BeginMenu("Look (theme)"))
            {
                if (ImGui.MenuItem("Standard", null, Settings.Mode == UiMode.Blender)) SetMode(UiMode.Blender);
                if (ImGui.MenuItem("Classic (4 views)", null, Settings.Mode == UiMode.Max)) SetMode(UiMode.Max);
                ImGui.EndMenu();
            }
            var vp = Viewports[ActiveViewport];
            ImGui.Separator();
            foreach (var k in new[] { ViewKind.Perspective, ViewKind.Top, ViewKind.Bottom, ViewKind.Front, ViewKind.Back, ViewKind.Left, ViewKind.Right })
                if (ImGui.MenuItem(k.ToString(), null, vp.Camera.Kind == k)) vp.Camera.SetView(k);
            ImGui.Separator();
            ShadingItems(vp);
            ImGui.Separator();
            if (ImGui.MenuItem(K("Frame Selected", "Frame Selected", "Zoom Extents Selected"), K("Numpad .", "F", "Z"))) FrameSelection();
            if (ImGui.MenuItem(K("Frame All", "Frame All", "Zoom Extents All"), K("Home", "A", "Ctrl+Shift+Z"))) FrameAll();
            if (ImGui.MenuItem("Show Grid", K("", "", "G"), ShowGrid)) ShowGrid = !ShowGrid;
            if (ImGui.MenuItem("Statistics", null, ShowStats)) ShowStats = !ShowStats;
            if (ImGui.MenuItem("Backface Cull", null, BackfaceCull)) BackfaceCull = !BackfaceCull;
            if (ImGui.MenuItem("Face Orientation", null, FaceOrientation)) FaceOrientation = !FaceOrientation;
            if (ImGui.MenuItem("Scene Explorer", K("T", "Ctrl+["), ShowExplorer)) ShowExplorer = !ShowExplorer;
            if (ImGui.MenuItem(Theme.IsBlender ? "Properties" : "Command Panel", K("N", "Ctrl+]"), ShowPanel)) ShowPanel = !ShowPanel;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Rendering"))
        {
            ImGui.SetNextItemWidth(180); ImGui.SliderFloat("Time of day", ref Hour, 0, 24, "%.1f h");
            ImGui.Checkbox("Interior lighting (no sun)", ref InteriorLight);
            ImGui.SetNextItemWidth(180); ImGui.SliderFloat("Interior ambient", ref InteriorLevel, 0, 1.5f);
            ImGui.SetNextItemWidth(180); ImGui.SliderFloat("Exposure", ref Exposure, 0.3f, 3f);
            ImGui.SetNextItemWidth(180); ImGui.SliderFloat("Modelling light", ref AmbientBoost, 0, 0.6f);
            ImGui.Separator();
            PhotoRenderMenuItems();
            ImGui.EndMenu();
        }
        AddonsMenu();
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Keyboard Shortcuts")) shortcutsOpen = true;
            if (ImGui.MenuItem("About Polymorph")) aboutOpen = true;
            if (ImGui.MenuItem("Check for updates...")) CheckUpdates(byHand: true);
            ImGui.Separator();
            if (ImGui.MenuItem("Video tutorials (website)")) OpenUrl("https://molossen.github.io/polymorph/#tutorials");
            if (ImGui.MenuItem("Report a bug (GitHub)")) OpenUrl("https://github.com/molossen/polymorph/issues");
            if (ImGui.MenuItem("Molo Modding Discord")) OpenUrl("https://discord.com/invite/molomodding");
            if (ImGui.MenuItem("Get Tripo (AI 3D models)")) OpenUrl(TripoLink);
            if (ImGui.MenuItem("Licence")) { var lic = Path.Combine(AppContext.BaseDirectory, "LICENSE"); if (File.Exists(lic)) { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + lic + "\""); } catch { } } else OpenUrl("https://github.com/molossen/polymorph/blob/main/LICENSE"); }
            ImGui.EndMenu();
        }
        ModeTabs();
        UpdateMenuButton();
        ImGui.EndMainMenuBar();
    }

    void ShadingItems(Viewport vp)
    {
        foreach (ShadingMode m in Enum.GetValues<ShadingMode>())
        {
            var label = m switch { ShadingMode.Realistic => "Realistic (GTA shader)", ShadingMode.Shaded => "Shaded", ShadingMode.VertexColour => "Vertex Color (GTA bake)", ShadingMode.Normals => "Normals", ShadingMode.UVChecker => "UV Checker", _ => "Flat Color" };
            if (ImGui.MenuItem(label, null, vp.Shading == m && !vp.WireframeOnly)) { vp.Shading = m; vp.WireframeOnly = false; }
        }
        if (ImGui.MenuItem("Wireframe", K("Shift+Z", "Alt+W", "F3"), vp.WireframeOnly)) vp.WireframeOnly = !vp.WireframeOnly;
        if (ImGui.MenuItem("Edged Faces", "F4", vp.EdgedFaces)) vp.EdgedFaces = !vp.EdgedFaces;
        ImGui.Separator();
        if (ImGui.MenuItem("Photo render (path traced)...", "F12")) { if (vp.Shading != ShadingMode.Realistic) vp.Shading = ShadingMode.Realistic; photoOpen = true; }
        Theme.Tip("A real photo of this view: light bouncing, soft shadows, glass. Rendered in the background.");
    }

    static string KindLabel(CreateKind k) => k switch
    {
        CreateKind.Room => "MLO Room",
        CreateKind.CollisionBox => "Collision Box",
        CreateKind.Tufted => "Tufted Cushion",
        _ => k.ToString(),
    };

    // ---------------------------------------------------------------- main toolbar

    void DrawToolbar()
    {
        ImGui.SetNextWindowPos(new Vector2(0, menuH));
        ImGui.SetNextWindowSize(new Vector2(gpu.Width, toolbarH));
        ImGui.Begin("##toolbar", Fixed | ImGuiWindowFlags.NoScrollbar);
        var sz = new Vector2(0, 26);
        if (ImGui.Button("Undo", sz)) DoUndo(); Theme.Tip("Ctrl+Z");
        ImGui.SameLine(); if (ImGui.Button("Redo", sz)) DoRedo(); Theme.Tip("Ctrl+Y");
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        // sub-object levels, always visible (3ds Max ribbon style)
        foreach (var (lvl, name, keyB, keyI) in new[] { (SubObject.Object, "Object", "Tab", "Q"), (SubObject.Vertex, "Vertex", "1", "1"), (SubObject.Edge, "Edge", "2", "2"),
                     (SubObject.Polygon, "Polygon", "3", "3"), (SubObject.Element, "Element", "4", "4") })
        {
            if (MapMode && !MapEditable(Current) && !(Current != null && entOf.ContainsKey(Current.Id))) continue; // MAP mode: mesh editing on what is modeled here (or a game model, copied)
            if (Theme.Toggle(name, Level == lvl, sz)) { if (lvl == SubObject.Object) SetLevel(SubObject.Object); else if (Level != lvl) SetLevel(lvl); }
            Theme.Tip(lvl == SubObject.Object ? "Leave edit mode" : name + " (" + K(keyB + " in edit mode", keyI) + ")" + (lvl == SubObject.Element ? ": click selects the whole connected piece" : ""));
            ImGui.SameLine();
        }
        if (MapMode) { ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.38f, 0.82f, 0.38f, 1)); ImGui.AlignTextToFramePadding(); Ui.Text("MAP"); ImGui.PopStyleColor(); ImGui.SameLine(); }
        Ui.TextDisabled("|"); ImGui.SameLine();
        if (Theme.Toggle("Select", Tool == Tool.Select, sz)) Tool = Tool.Select; Theme.Tip(K("Select tool", "Select tool (Q)"));
        ImGui.SameLine(); if (Theme.Toggle("Move", Tool == Tool.Move, sz)) Tool = Tool.Move; Theme.Tip(K("Move tool with gizmo (or G anytime)", "Move tool (W)"));
        ImGui.SameLine(); if (Theme.Toggle("Rotate", Tool == Tool.Rotate, sz)) Tool = Tool.Rotate; Theme.Tip(K("Rotate tool with gizmo (or R anytime)", "Rotate tool (E)"));
        ImGui.SameLine(); if (Theme.Toggle("Scale", Tool == Tool.Scale, sz)) Tool = Tool.Scale; Theme.Tip(K("Scale tool with gizmo (or S anytime)", "Scale tool (R)"));
        ImGui.SameLine(0, 2); if (Theme.Toggle("Box##scalecage", ScaleCage, new Vector2(0, sz.Y))) { ScaleCage = !ScaleCage; if (ScaleCage) Tool = Tool.Scale; }
        Theme.Tip("Scale box: with the Scale tool, a box with handles around the selection. Drag a side, an edge or a corner: it stretches from the opposite side, only that way. Shift keeps the proportions, snapping rounds the size to the grid.");
        ImGui.SameLine(); ImGui.SetNextItemWidth(72);
        int coord = LocalCoords ? 1 : 0;
        if (ImGui.Combo("##coord", ref coord, new[] { "World", "Local" }, 2)) LocalCoords = coord == 1;
        Theme.Tip("Reference Coordinate System");
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        SnapButton(sz.Y);
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        if (Theme.Toggle("Material Editor", materialEditorOpen, sz)) materialEditorOpen = !materialEditorOpen; Theme.Tip("M");
        ImGui.SameLine(); if (Theme.Toggle(Maximized ? "1 View" : "4 Views", !Maximized, sz)) Maximized = !Maximized; Theme.Tip("One view / four views (" + K("Ctrl+Alt+Q", "menu View", "Alt+W") + ")");
        ImGui.SameLine(); if (Theme.Toggle("Edged Faces", Viewports[ActiveViewport].EdgedFaces, sz)) Viewports[ActiveViewport].EdgedFaces ^= true; Theme.Tip("F4");
        ImGui.SameLine(); if (Theme.Toggle("Backface Cull", BackfaceCull, sz)) BackfaceCull = !BackfaceCull;
        Theme.Tip("Hide polygons seen from behind, like the game does. MLO rooms are always culled.");
        ImGui.SameLine(); if (Theme.Toggle("Face Orientation", FaceOrientation, sz)) FaceOrientation = !FaceOrientation;
        Theme.Tip("Front of the polygons in blue, back (flipped normals) in red. Colours in Preferences > Interface.");
        ImGui.SameLine(); if (Theme.Toggle("Visible only", SelectVisibleOnly, sz)) SelectVisibleOnly = !SelectVisibleOnly;
        ImGui.SameLine(); if (ImGui.Button("Ped")) AddPedReference();
        Theme.Tip("Add a GTA ped (1.818 m) to check the scale. Never exported. Auto scale in the Tool tab.");
        Theme.Tip("Alt+Z. Off (default): edit-mode clicks and boxes select through the mesh. On: only the vertices, edges and polygons you can see.");
        ImGui.SameLine(); if (Theme.Toggle("Opening", modal.Kind == ModalKind.Opening)) { if (modal.Kind == ModalKind.Opening) CancelModal(); else StartOpeningTool(); } TutRect("tb_opening");
        Theme.Tip("Wall Opening: drag a rectangle on the selected wall (door, window, passage), pull its sides, Enter cuts.");
        ImGui.SameLine(); if (Theme.Toggle("Sculpt", SculptMode)) ToggleSculptMode(); TutRect("tb_sculpt");
        Theme.Tip("Sculpt the selected mesh: draw, smooth, inflate, grab, pinch, flatten... (brushes in the panel, Esc leaves)");
        ImGui.SameLine(); if (Theme.Toggle("Paint", paintMode)) TogglePaintMode(); TutRect("tb_paint");
        Theme.Tip("Molo Paint: paint the selected model in layers like Substance (smart materials, masks, bake, GTA textures)");
        ImGui.SameLine(); if (Theme.Toggle("Timeline", TimelineOpen)) ToggleTimeline(); TutRect("tb_timeline");
        Theme.Tip("Animation: timeline, keyframes (I), auto key, camera keys, GTA .ycd import / export");
        ImGui.SameLine(); if (Theme.Toggle("Assets", assetsOpen)) { if (assetsOpen) assetsOpen = false; else OpenAssetBrowser(); }
        Theme.Tip("Asset Browser: your models and the game props as thumbnails; double-click or drag into the view");
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        ImGui.AlignTextToFramePadding(); Ui.Text(InteriorLight ? "Interior" : "Sun");
        ImGui.SameLine(); ImGui.SetNextItemWidth(gpu.Width < 2100 ? 100 : 140);
        if (!InteriorLight) ImGui.SliderFloat("##tod", ref Hour, 0, 24, "%.1f h"); else ImGui.SliderFloat("##int", ref InteriorLevel, 0, 1.5f, "amb %.2f");
        ImGui.SameLine(); ImGui.Checkbox("Interior", ref InteriorLight);
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.83f, 0.69f, 0.29f, 1) * new Vector4(0.75f, 0.75f, 0.75f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.83f, 0.69f, 0.29f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.05f, 1));
        if (ImGui.Button(" GTA ", sz)) HubOpen = !HubOpen;
        ImGui.PopStyleColor(3);
        Theme.Tip("GTA panel (F10): export the FiveM resource, MLO builder, collision, checks.");
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        // the last button always fits the window: shorter label, then just the key, when the toolbar is full
        float room = ImGui.GetContentRegionAvail().X - 4;
        string sl = room >= 110 ? "Search  " + SearchKey : room >= 70 ? "Search" : SearchKey;
        float sw = MathF.Max(32, MathF.Min(110, room));
        if (room >= 28 && ImGui.Button(sl + "##tbsearch", new Vector2(sw, sz.Y))) OpenSearch();
        Theme.Tip("Find any command by name (" + SearchKey + ")");
        ImGui.End();
    }

    // ---------------------------------------------------------------- scene explorer (left)

    void DrawSceneExplorer()
    {
        if (!ShowExplorer) return;
        float y = menuH + toolbarH;
        ImGui.SetNextWindowPos(new Vector2(0, y));
        ImGui.SetNextWindowSize(new Vector2(explorerW, gpu.Height - y - statusH));
        ImGui.Begin("Scene Explorer###explorer", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);
        DrawOutlinerContents();
        ImGui.End();
    }

    // ---------------------------------------------------------------- status bar (bottom)

    void DrawStatusBar()
    {
        ImGui.SetNextWindowPos(new Vector2(0, gpu.Height - statusH));
        ImGui.SetNextWindowSize(new Vector2(gpu.Width, statusH));
        ImGui.Begin("##status", Fixed | ImGuiWindowFlags.NoScrollbar);
        var cur = Current;
        string selText = Level != SubObject.Object && cur != null
            ? $"{SubSel(cur).Count(Level)} {Level}{(SubSel(cur).Count(Level) == 1 ? "" : "s")} Selected"
            : Selected.Count == 0 ? "None Selected" : Selected.Count == 1 ? "1 Object Selected" : $"{Selected.Count} Objects Selected";
        ImGui.AlignTextToFramePadding();
        Ui.Text(selText.PadRight(26));
        ImGui.SameLine(200);
        if (cur != null && Level == SubObject.Object) TransformTypeIn(cur);
        else if (cur != null && cur.Mesh != null && SelectedPoints(cur).Count > 0) SubTypeIn(cur);
        else Ui.TextDisabled("X:          Y:          Z:");
        ImGui.SameLine(); Ui.TextDisabled($"   Grid = {GridStep:0.###} m{(Snap ? "  [Snap]" : "")}");
        var msg = status;
        Ui.TextColored(ImGui.GetTime() - statusTime < 6 ? Theme.Text : Theme.TextDim, msg);
        var fps = $"{ImGui.GetIO().Framerate:0} fps";
        ImGui.SameLine(gpu.Width - 70); Ui.TextDisabled(fps);
        ImGui.End();
    }

    void TransformTypeIn(SceneObject o)
    {
        Vector3 v; string fmt; float speed;
        if (Tool == Tool.Rotate) { v = QuatToEulerDeg(o.Rotation); fmt = "%.1f°"; speed = 0.5f; }
        else if (Tool == Tool.Scale) { v = o.Scale * 100; fmt = "%.1f%%"; speed = 0.5f; }
        else { v = o.Position; fmt = "%.3f m"; speed = 0.01f; }
        var before = v;
        bool changed = false;
        string[] ax = { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            ImGui.SameLine(); Ui.Text(ax[i] + ":"); ImGui.SameLine(); ImGui.SetNextItemWidth(92);
            float c = i == 0 ? v.X : i == 1 ? v.Y : v.Z;
            if (ImGui.DragFloat("##ti" + i, ref c, speed, 0, 0, fmt))
            {
                if (i == 0) v.X = c; else if (i == 1) v.Y = c; else v.Z = c;
                changed = true;
            }
            if (ImGui.IsItemActivated()) Undo.Record("Transform Type-In", Scene);
        }
        if (!changed) return;
        if (Tool == Tool.Rotate) o.Rotation = EulerDegToQuat(v);
        else if (Tool == Tool.Scale) o.Scale = v / 100f;
        else o.Position = v;
        Dirty = true;
    }

    // ---- viewport lens (Blender's View panel: Focal Length on a 36 mm sensor, fitted on the longer side; Clip Start / End)
    static float FocalFromFov(float vfov, float aspect)
    {
        float t = MathF.Tan(vfov / 2) * (aspect >= 1 ? aspect : 1);
        return 18f / MathF.Max(t, 1e-4f);
    }

    /// <summary>Once a frame: the lens settings on every perspective viewport (MAP mode keeps its own adaptive near plane).</summary>
    void ApplyViewLens()
    {
        foreach (var v in Viewports)
        {
            var c = v.Camera;
            if (Settings.ViewFocalMm > 0)
            {
                float aspect = MathF.Max(v.W, 1) / MathF.Max(v.H, 1);
                float half = MathF.Atan(18f / Settings.ViewFocalMm);
                c.Fov = 2 * (aspect >= 1 ? MathF.Atan(MathF.Tan(half) / aspect) : half);
            }
            if (!MapMode) { c.Near = Settings.ViewClipStart; c.Far = Settings.ViewClipEnd; }
        }
    }

    // ---- edit mode type-in (3ds Max "offset" mode): type a value, Enter, the selection moves / turns / scales by it
    Vector3 subTypeIn;
    Tool subTypeInTool = (Tool)(-1);

    void SubTypeIn(SceneObject o)
    {
        if (subTypeInTool != Tool) { subTypeInTool = Tool; subTypeIn = Tool == Tool.Scale ? new Vector3(100) : Vector3.Zero; }
        string fmt = Tool == Tool.Rotate ? "%.1f°" : Tool == Tool.Scale ? "%.1f%%" : "%.3f m";
        string[] ax = { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            ImGui.SameLine(); Ui.Text(ax[i] + ":"); ImGui.SameLine(); ImGui.SetNextItemWidth(92);
            float c = i == 0 ? subTypeIn.X : i == 1 ? subTypeIn.Y : subTypeIn.Z;
            // (EnterReturnsTrue is not allowed on number fields: the value is applied when the field is left after an edit)
            if (ImGui.InputFloat("##sti" + i, ref c, 0, 0, fmt)) { if (i == 0) subTypeIn.X = c; else if (i == 1) subTypeIn.Y = c; else subTypeIn.Z = c; }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var rest = Tool == Tool.Scale ? new Vector3(100) : Vector3.Zero;
                var d = rest;
                if (i == 0) d.X = c; else if (i == 1) d.Y = c; else d.Z = c;
                subTypeIn = rest; // the field goes back to 0 (100 %): the next value is again an offset
                if (d != rest) ApplySubOffset(o, d);
            }
            Theme.Tip(Tool == Tool.Rotate ? "Type an angle and press Enter: the selection turns by it around its centre (World / Local axes)"
                : Tool == Tool.Scale ? "Type a percentage and press Enter: the selection is scaled by it along this axis"
                : "Type a distance and press Enter: the selection moves by it");
        }
    }

    void ApplySubOffset(SceneObject o, Vector3 d)
    {
        if (!SelectionPivot(out var pivot, out var orient)) return;
        var pts = SelectedPoints(o).ToArray();
        var ax = new[] { Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, orient)), Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, orient)), Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, orient)) };
        Matrix4x4 m;
        if (Tool == Tool.Rotate)
        {
            var r = d * (MathF.PI / 180f);
            var q = Quaternion.CreateFromAxisAngle(ax[2], r.Z) * Quaternion.CreateFromAxisAngle(ax[1], r.Y) * Quaternion.CreateFromAxisAngle(ax[0], r.X);
            m = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(pivot);
        }
        else if (Tool == Tool.Scale)
        {
            var s = d / 100f; Matrix4x4.Invert(orient, out var inv);
            m = Matrix4x4.CreateTranslation(-pivot) * inv * Matrix4x4.CreateScale(s) * orient * Matrix4x4.CreateTranslation(pivot);
        }
        else m = Matrix4x4.CreateTranslation(ax[0] * d.X + ax[1] * d.Y + ax[2] * d.Z);
        var label = Tool == Tool.Rotate ? $"Rotate {d.X:0.#}/{d.Y:0.#}/{d.Z:0.#}°" : Tool == Tool.Scale ? "Scale" : "Move";
        DoMesh(label, (oo, mesh, s) =>
        {
            Matrix4x4.Invert(oo.World, out var invW);
            var full = oo.World * m * invW;
            foreach (var v in pts) if (v < mesh.Verts.Count) mesh.Verts[v] = Vector3.Transform(mesh.Verts[v], full);
        });
        SetStatus(label + " applied");
    }

    static Vector3 QuatToEulerDeg(Quaternion q)
    {
        // XYZ order (3ds Max default)
        var m = Matrix4x4.CreateFromQuaternion(q);
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        float x, z;
        if (MathF.Abs(m.M13) < 0.9999f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = MathF.Atan2(-m.M32, m.M22); z = 0; }
        return new Vector3(x, y, z) * (180f / MathF.PI);
    }

    static Quaternion EulerDegToQuat(Vector3 d)
    {
        var r = d * (MathF.PI / 180f);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X));
    }

    // ---------------------------------------------------------------- viewport labels  [+] [Perspective] [Realistic]

    void DrawViewportLabels()
    {
        for (int i = 0; i < Viewports.Length; i++)
        {
            if (Maximized && i != ActiveViewport) continue;
            var vp = Viewports[i];
            ImGui.SetNextWindowPos(new Vector2(vp.X + 4, vp.Y + 3));
            ImGui.SetNextWindowBgAlpha(0);
            ImGui.Begin($"##vplabel{i}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                       | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.12f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
            if (ImGui.SmallButton("[+]")) ImGui.OpenPopup("plus");
            ImGui.SameLine(0, 2);
            if (ImGui.SmallButton($"[{vp.Camera.Label}]")) ImGui.OpenPopup("view");
            ImGui.SameLine(0, 2);
            if (ImGui.SmallButton($"[{vp.ShadingLabel}]")) ImGui.OpenPopup("shade");
            ImGui.PopStyleVar(2); ImGui.PopStyleColor(2);
            if (ImGui.BeginPopup("plus"))
            {
                if (ImGui.MenuItem("Maximize Viewport", "Alt+W", Maximized)) { Maximized = !Maximized; ActiveViewport = i; }
                if (ImGui.MenuItem("Show Grid", "G", ShowGrid)) ShowGrid = !ShowGrid;
                if (ImGui.MenuItem("Statistics", "7", ShowStats)) ShowStats = !ShowStats;
                ImGui.Separator();
                Ui.TextDisabled("View (all perspective viewports)");
                float aspect = MathF.Max(vp.W, 1) / MathF.Max(vp.H, 1);
                float focal = Settings.ViewFocalMm > 0 ? Settings.ViewFocalMm : FocalFromFov(vp.Camera.Fov, aspect);
                ImGui.SetNextItemWidth(170);
                if (ImGui.DragFloat("Focal Length", ref focal, 0.25f, 4f, 300f, "%.0f mm")) { Settings.ViewFocalMm = Math.Clamp(focal, 4f, 300f); ApplyViewLens(); }
                if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
                float cs = Settings.ViewClipStart, ce = Settings.ViewClipEnd;
                ImGui.SetNextItemWidth(170);
                if (ImGui.DragFloat("Clip Start", ref cs, 0.001f, 0.001f, 100f, "%.3f m", ImGuiSliderFlags.Logarithmic)) { Settings.ViewClipStart = Math.Clamp(cs, 0.001f, MathF.Max(0.002f, ce * 0.5f)); ApplyViewLens(); }
                if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
                ImGui.SetNextItemWidth(170);
                if (ImGui.DragFloat("End", ref ce, 10f, 1f, 100000f, "%.0f m", ImGuiSliderFlags.Logarithmic)) { Settings.ViewClipEnd = Math.Clamp(ce, Settings.ViewClipStart * 2, 100000f); ApplyViewLens(); }
                if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
                if (ImGui.SmallButton("Reset lens")) { Settings.ViewFocalMm = 0; Settings.ViewClipStart = 0.05f; Settings.ViewClipEnd = 20000f; foreach (var v in Viewports) v.Camera.Fov = 45f * MathF.PI / 180f; ApplyViewLens(); Settings.Save(); }
                ImGui.EndPopup();
            }
            if (ImGui.BeginPopup("view"))
            {
                foreach (var k in new[] { ViewKind.Perspective, ViewKind.Top, ViewKind.Bottom, ViewKind.Front, ViewKind.Back, ViewKind.Left, ViewKind.Right })
                    if (ImGui.MenuItem(k.ToString(), null, vp.Camera.Kind == k)) vp.Camera.SetView(k);
                ImGui.EndPopup();
            }
            if (ImGui.BeginPopup("shade")) { ShadingItems(vp); ImGui.EndPopup(); }
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) ActiveViewport = i;
            ImGui.End();
        }
    }

    // ---------------------------------------------------------------- quad menu, dialogs

    void DrawDialogs()
    {
        if (shortcutsOpen) { ImGui.OpenPopup("Keyboard Shortcuts"); shortcutsOpen = false; }
        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Keyboard Shortcuts", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var keys = ShortcutTable();
            Ui.Text("Keymap: " + KeymapNames[(int)Settings.Keymap]);
            for (int i = 0; i < KeymapNames.Length; i++)
            {
                if (i == (int)Settings.Keymap) continue;
                ImGui.SameLine();
                if (ImGui.SmallButton(KeymapNames[i])) SetKeymap((Keymap)i);
            }
            if (ImGui.BeginTable("keys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            {
                foreach (var (k, d) in keys) { ImGui.TableNextRow(); ImGui.TableNextColumn(); Ui.Text(k); ImGui.TableNextColumn(); Ui.TextDisabled(d); }
                ImGui.EndTable();
            }
            if (ImGui.Button("Close", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        DrawUpdateWindow();
        DrawPyEditor();
        DrawPhotoRender();
        if (aboutOpen) { ImGui.OpenPopup("About Polymorph"); aboutOpen = false; }
        bool aopen = true;
        if (ImGui.BeginPopupModal("About Polymorph", ref aopen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var lp = ImGui.GetCursorScreenPos();
            Ui.DrawLogo(ImGui.GetWindowDrawList(), lp, 96);
            ImGui.SetCursorScreenPos(lp + new Vector2(100, 22));
            ImGui.PushFont(Ui.BrandFont); ImGui.TextUnformatted("Polymorph"); ImGui.PopFont();
            ImGui.SetCursorScreenPos(lp + new Vector2(102, 56));
            Ui.Label("3D modelling for GTA V & FiveM", Theme.TextDim);
            ImGui.SetCursorScreenPos(lp + new Vector2(0, 104));
            Ui.Label($"v{AppInfo.Version} · by Molo Modding", Theme.TextDim);
            ImGui.Spacing();
            Ui.TextDisabled("Editable Poly, GTA basic shader (port of CodeWalker's BasicPS), MLO building blocks.");
            Ui.TextDisabled("CodeWalker by dexyfex (MIT), RAGE Tools fork, Sollumz shader data (MIT), Dear ImGui / ImGui.NET (MIT), SharpDX (MIT).");
            Ui.TextDisabled("Full notices: THIRD-PARTY-NOTICES.txt next to Polymorph.exe.");
            if (ImGui.Button("Close##2", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}
