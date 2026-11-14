using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Search every MoloMax command, like Blender's menu search: F3 with the Blender keymap, Tab with Industry
/// Compatible (as in Blender). Type a few letters, arrows to choose, Enter to run. Add-on commands show up too.
/// </summary>
public sealed partial class EditorApp
{
    sealed record Command(string Name, string Category, Action Run, Func<bool> Enabled = null, Func<string> Key = null, string Help = null);

    List<Command> commands;
    readonly List<string> recentCommands = new();
    bool searchOpen, searchFocus;
    string searchText = "";
    int searchIndex;

    string SearchKey => BlenderKeymap ? "F3" : "X";

    void OpenSearch() { searchOpen = true; searchFocus = true; searchText = ""; searchIndex = 0; }

    List<Command> Commands()
    {
        if (commands != null) return commands;
        bool HasObj() => Current != null;
        bool Edit() => Level != SubObject.Object && Current != null;
        bool Obj() => Level == SubObject.Object;
        var c = new List<Command>
        {
            // file
            new("New Scene", "File", NewScene, Key: () => "Ctrl+N"),
            new("Open...", "File", OpenDialog, Key: () => "Ctrl+O"),
            new("Save", "File", () => Save(false), Key: () => "Ctrl+S"),
            new("Save As...", "File", () => Save(true), Key: () => "Ctrl+Shift+S"),
            new("Import Model (GTA ydr / yft / ybn, GLB, OBJ)...", "File", ImportDialog, Key: () => "Ctrl+Shift+O"),
            new("Export OBJ (all)", "File", () => ExportObj(false)),
            new("Export OBJ (selected)", "File", () => ExportObj(true)),
            new("Export FiveM Resource", "GTA", () => { OpenHub(1); }, Help: "GTA panel > Export"),
            new("Preferences", "Edit", () => prefsOpen = true, Key: () => "Ctrl+,"),
            new("Undo", "Edit", DoUndo, Key: () => "Ctrl+Z"),
            new("Redo", "Edit", DoRedo, Key: () => "Ctrl+Shift+Z"),
            new("Keyboard Shortcuts", "Help", () => shortcutsOpen = true),
            // select
            new("Select All", "Select", SelectAll, Key: () => K("A", "Ctrl+A")),
            new("Select None", "Select", SelectNone, Key: () => K("Alt+A", "Ctrl+Shift+A")),
            new("Select Invert", "Select", SelectInvert, Key: () => "Ctrl+I"),
            new("Select More", "Select", Grow, Edit, () => K("Ctrl+Numpad +", "Ctrl+Up")),
            new("Select Less", "Select", Shrink, Edit, () => K("Ctrl+Numpad -", "Down")),
            new("Select Linked", "Select", SelectLinked, Edit, () => "Ctrl+L"),
            new("Select Edge Loops", "Select", Loop, Edit),
            new("Select Edge Rings", "Select", Ring, Edit),
            new("Make Planar (best-fit plane)", "Edit Poly", () => MakePlanar(-1), Edit),
            new("Preserve UVs (on / off)", "Edit Poly", () => { Settings.PreserveUVs = !Settings.PreserveUVs; Settings.Save(); SetStatus("Preserve UVs " + (Settings.PreserveUVs ? "on" : "off")); }, Edit),
            new("Make Planar X", "Edit Poly", () => MakePlanar(0), Edit),
            new("Make Planar Y", "Edit Poly", () => MakePlanar(1), Edit),
            new("Make Planar Z (level the vertices)", "Edit Poly", () => MakePlanar(2), Edit),
            // object
            new("Duplicate", "Object", () => { if (BlenderKeymap) DuplicateAndGrab(); else CloneSelection(false); }, () => Selected.Count > 0, () => K("Shift+D", "Ctrl+D")),
            new("Instance (linked duplicate)", "Object", () => CloneSelection(true), () => Selected.Count > 0, () => K("Alt+D", "")),
            new("Sculpt Mode", "Object", ToggleSculptMode, () => Current != null, () => ""),
            new("Timeline (animation)", "Animation", ToggleTimeline),
            new("Insert Keyframe (location, rotation, scale)", "Animation", () => InsertKeyframe()),
            new("Key camera", "Animation", KeyCamera),
            new("Import animation (.ycd)", "Animation", ImportYcdDialog),
            new("Export animation (.ycd)", "Animation", ExportYcdDialog),
            new("Run Python Script", "Tools", RunScriptDialog, () => true, () => ""),
            new("Make Unique", "Object", MakeUniqueSelection, () => SelObjects().Any(o => o.InstanceKey != null), () => ""),
            new("Select Instances", "Object", () => SelectInstances(), () => Current?.InstanceKey != null, () => ""),
            new("Delete", "Object", DeleteSelection, () => Selected.Count > 0, () => K("X", "Del")),
            new("Join", "Object", JoinSelected, () => Selected.Count > 1, () => K("Ctrl+J", "")),
            new("Wall Opening: cut a door / window (trace it on the wall)", "Object", StartOpeningTool, () => Current != null),
            new("Clean Up: fewer edges, same shape (flat areas merged, straight points removed)", "Object", CleanUpSelected, () => Current != null),
            new("Hide Selected", "Object", HideSelection, Key: () => K("H", "Ctrl+H")),
            new("Hide Unselected", "Object", HideUnselected, Key: () => "Shift+H"),
            new("Reveal Hidden", "Object", UnhideAll, Key: () => "Alt+H"),
            new("Local View (isolate)", "View", () => IsolateSelection(), Key: () => K("Numpad /", "Shift+I")),
            new("Apply Rotation & Scale", "Object", ResetXForm, HasObj, () => K("Ctrl+A", "")),
            new("Origin to Geometry", "Object", CenterPivot, HasObj),
            new("Origin to Base", "Object", PivotToBase, HasObj),
            new("Origin to 3D Cursor", "Object", OriginToCursor, HasObj),
            new("Toggle Edit Mode", "Mode", ToggleEditMode, HasObj, () => K("Tab", "1 / 4")),
            new("Vertex Select", "Mode", () => SetLevelDirect(SubObject.Vertex), HasObj, () => "1"),
            new("Edge Select", "Mode", () => SetLevelDirect(SubObject.Edge), HasObj, () => "2"),
            new("Poly Select", "Mode", () => SetLevelDirect(SubObject.Polygon), HasObj, () => "3"),
            new("Element Select", "Mode", () => SetLevelDirect(SubObject.Element), HasObj, () => K("4", "")),
            // mesh
            new("Extrude", "Mesh", () => BeginModal(ModalKind.Extrude), Edit, () => K("E", "Ctrl+E")),
            new("Inset Faces", "Mesh", () => BeginModal(ModalKind.Inset), Edit, () => "I"),
            new("Loop Cut", "Mesh", () => BeginModal(ModalKind.LoopCut), Edit, () => K("Ctrl+R", "Alt+C")),
            new("Fill / Make Face", "Mesh", MakeFace, Edit, () => K("F", "")),
            new("Fill Holes", "Mesh", Cap, Edit),
            new("Subdivide", "Mesh", Tessellate, Edit),
            new("Flip Normals", "Mesh", FlipFaces, HasObj),
            new("Recalculate Normals Outside", "Mesh", () => RecalcNormals(false), HasObj, () => K("Shift+N", "")),
            new("Recalculate Normals Inside", "Mesh", () => RecalcNormals(true), HasObj, () => K("Ctrl+Shift+N", "")),
            new("Separate Selection", "Mesh", () => DetachFaces(false), Edit, () => K("P", "")),
            new("Connect", "Mesh", ConnectEdges, Edit),
            new("Dissolve", "Mesh", MaxRemove, Edit, () => K("Ctrl+X", "Ctrl+Del")),
            new("Merge by Distance", "Mesh", () => Weld(weldT), HasObj),
            new("Smooth by Angle", "Mesh", () => AutoSmooth(autoSmoothA), HasObj),
            new("Shade Flat", "Mesh", ClearSmoothing, HasObj),
            new("Smart UV Project (unwrap)", "UV", SmartUv, HasObj),
            new("UV Editor (move islands, pack, texel density)", "UV", OpenUvEditor, HasObj),
            new("Chamfer / Bevel edges", "Mesh", () => BeginModal(ModalKind.Chamfer), HasObj, () => "Ctrl+B"),
            new("Knife (cut along a line)", "Mesh", () => BeginModal(ModalKind.Knife), HasObj, () => K("K", "Shift+K")),
            new("QuickSlice (slice the whole mesh along a line)", "Mesh", () => BeginModal(ModalKind.QuickSlice), HasObj, () => ""),
            new("Target Weld (click a vertex, then the one to weld it onto)", "Mesh", () => BeginModal(ModalKind.TargetWeld), HasObj, () => ""),
            new("Bridge (2 faces or 2 edge loops)", "Mesh", Bridge, HasObj),
            new("Boolean Union", "Object", () => Boolean(MoloMax.Core.Mesh.Csg.Op.Union), () => Selected.Count > 1),
            new("Boolean Subtract (cut the others out of the active)", "Object", () => Boolean(MoloMax.Core.Mesh.Csg.Op.Subtract), () => Selected.Count > 1),
            new("Boolean Intersect", "Object", () => Boolean(MoloMax.Core.Mesh.Csg.Op.Intersect), () => Selected.Count > 1),
            new("Snap to vertex (on / off)", "View", () => { SnapVertexT = !SnapVertexT; if (SnapVertexT) Snap = true; }),
            new("Snap to edge (on / off)", "View", () => { SnapEdge = !SnapEdge; if (SnapEdge) Snap = true; }),
            new("Snap to face (on / off)", "View", () => { SnapFace = !SnapFace; if (SnapFace) Snap = true; }),
            new("Create Drawable (GIMS: game meshes under one model)", "GTA", CreateDrawable, () => Selected.Count > 0),
            new("Collision from selection (copy under the Drawable)", "GTA", CollisionFromSelection, () => Selected.Count > 0),
            new("Quick Import / Export menu", "File", () => OpenPopup("quickio")),
            new("Game files browser (GTA V models, native buildings)", "GTA", OpenGameBrowser),
            new("Asset Browser (thumbnails of my models and game props)", "GTA", OpenAssetBrowser),
            new("Import MLO from .ytyp (rooms, portals, entities)", "GTA", ImportMloDialog),
            new("Open a stream folder in MAP (ymap, ytyp, ydr... like CodeWalker)", "MAP", OpenStreamFolderDialog),
            new("MLO Room Builder (draw the floor plan)", "GTA", OpenRoomBuilder),
            new("Send to my FiveM server (export + restart)", "GTA", SendToServer),
            new("Install the FiveM bridge resource", "GTA", InstallBridge),
            new("Add GTA light (point)", "GTA", () => AddLight(MoloMax.Core.Scenes.GtaLightKind.Point)),
            new("Add GTA light (spot)", "GTA", () => AddLight(MoloMax.Core.Scenes.GtaLightKind.Spot)),
            new("Export Selected as GTA .ydr", "File", () => QuickExport(".ydr"), () => Selected.Count > 0),
            new("Export Selected as GTA .yft (physical)", "File", () => QuickExport(".yft"), () => Selected.Count > 0),
            new("Export Selected as .glb", "File", () => QuickExport(".glb"), () => Selected.Count > 0),
            new("Export Scene as .glb", "File", () => QuickExport(".glb", all: true)),
            new("Cube Projection (box map)", "UV", () => BoxMap(boxTile, boxMapWorld), HasObj),
            // view
            new("Frame Selected", "View", () => FrameSelection(), Key: () => K("Numpad .", "F")),
            new("Frame All", "View", () => FrameAll(), Key: () => K("Home", "A")),
            new("Top View", "View", () => Viewports[ActiveViewport].Camera.SetView(ViewKind.Top), Key: () => K("Numpad 7", "F3")),
            new("Front View", "View", () => Viewports[ActiveViewport].Camera.SetView(ViewKind.Front), Key: () => K("Numpad 1", "F1")),
            new("Right View", "View", () => Viewports[ActiveViewport].Camera.SetView(ViewKind.Right), Key: () => K("Numpad 3", "F2")),
            new("Perspective / Orthographic", "View", () => ToggleOrtho(Viewports[ActiveViewport].Camera), Key: () => K("Numpad 5", "")),
            new("Toggle Wireframe", "View", () => Viewports[ActiveViewport].WireframeOnly ^= true, Key: () => K("Shift+Z", "Alt+W", "F3")),
            new("Toggle Quad View", "View", () => Maximized = !Maximized, Key: () => K("Ctrl+Alt+Q", "")),
            new("Toggle Sidebar (Properties)", "View", () => ShowPanel = !ShowPanel, Key: () => K("N", "Ctrl+]")),
            new("Toggle Outliner", "View", () => ShowExplorer = !ShowExplorer, Key: () => K("T", "Ctrl+[")),
            // tools
            new("GTA Panel", "GTA", () => HubOpen = !HubOpen, Key: () => "F10"),
            new("MLO Builder (rooms, portals)", "GTA", () => OpenHub(2)),
            new("GTA Object Properties", "GTA", () => { propTab = PropTab.Gta; ShowPanel = true; }),
            new("Check Scene for GTA", "GTA", RunGtaChecks),
            new("Animation (YCD)", "GTA", () => OpenHub((int)HubPage.Anim)),
            new("Molo Tools", "GTA", () => OpenHub((int)HubPage.Tools)),
            new("Tripo: AI 3D model", "Tripo", () => { propTab = PropTab.Tripo; ShowPanel = true; }),
            new("Molo Paint", "Paint", () => { propTab = PropTab.Paint; ShowPanel = true; }),
            new("Molo Paint: layers, masks, filters", "Paint", () => { propTab = PropTab.Paint; paintTab = 1; ShowPanel = true; }),
            new("Molo Paint: bake high-poly to low-poly", "Paint", () => { propTab = PropTab.Paint; paintTab = 2; ShowPanel = true; }),
            new("Molo Paint: export textures (DDS, PNG, TGA, Sollumz)", "Paint", () => { propTab = PropTab.Paint; paintTab = 3; ShowPanel = true; }),
            new("Molo Paint: vertex colour brush", "Paint", () => { propTab = PropTab.Paint; paintTab = 0; ShowPanel = true; SetPaintTool(PaintTool.VertexColour); }),
            new("Molo Paint: fill tool", "Paint", () => { propTab = PropTab.Paint; paintTab = 0; ShowPanel = true; SetPaintTool(PaintTool.Fill); }),
            new("Molo Paint: clone tool", "Paint", () => { propTab = PropTab.Paint; paintTab = 0; ShowPanel = true; SetPaintTool(PaintTool.Clone); }),
            new("Molo Materials (GTA texture library)", "Material", () => OpenLibrary(LibraryAction.Browse)),
            new("Material Editor", "Material", () => materialEditorOpen = true),
        };
        foreach (CreateKind k in Enum.GetValues<CreateKind>())
            if (k != CreateKind.None) { var kk = k; c.Add(new($"Add {KindLabel(kk)}", "Add", () => AddPrimitive(kk), Key: () => K("Shift+A", ""))); }
        foreach (var (name, cat, make) in Modifier.Catalog)
        {
            var mk = make;
            c.Add(new($"Add Modifier: {name}", "Modifiers", () =>
            {
                var o = Current; if (o == null) return;
                var mod = mk();
                Do("Add " + mod.TypeName, () => { mod.Name = ModifierStack.UniqueName(o.Modifiers, mod.Name); o.Modifiers.Add(mod); o.Touch(); });
                propTab = PropTab.Modifiers; ShowPanel = true;
            }, HasObj));
        }
        commands = c;
        return c;
    }

    /// <summary>Commands added by add-ons join the list.</summary>
    void AddAddonCommand(string name, string category, Action run, string help)
    {
        Commands().Add(new Command(name, string.IsNullOrEmpty(category) ? "Add-ons" : category, run, Help: help));
    }

    void DrawSearch()
    {
        if (!searchOpen) return;
        var all = Commands();
        var words = searchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<Command> found;
        if (words.Length == 0)
            found = recentCommands.Select(n => all.FirstOrDefault(x => x.Name == n)).Where(x => x != null).Concat(all.Where(x => !recentCommands.Contains(x.Name))).ToList();
        else
            found = all.Where(x => words.All(w => (x.Name + " " + x.Category).ToLowerInvariant().Contains(w)))
                       .OrderBy(x => x.Name.ToLowerInvariant().StartsWith(words[0]) ? 0 : 1).ThenBy(x => x.Name.Length).ToList();
        searchIndex = Math.Clamp(searchIndex, 0, Math.Max(0, found.Count - 1));

        var size = new Vector2(520, 420);
        ImGui.SetNextWindowPos(new Vector2((gpu.Width - size.X) / 2, menuH + toolbarH + 40));
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##search", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove);
        if (searchFocus) { ImGui.SetKeyboardFocusHere(); searchFocus = false; }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##q", "Search commands...", ref searchText, 100)) searchIndex = 0;
        bool run = false;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) searchIndex = Math.Min(found.Count - 1, searchIndex + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) searchIndex = Math.Max(0, searchIndex - 1);
        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) run = true;
        bool close = ImGui.IsKeyPressed(ImGuiKey.Escape, false) || (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Left));
        ImGui.BeginChild("##results");
        for (int i = 0; i < found.Count && i < 200; i++)
        {
            var cmd = found[i];
            bool enabled = cmd.Enabled?.Invoke() ?? true;
            if (!enabled) ImGui.BeginDisabled();
            if (ImGui.Selectable($"{cmd.Name}##{i}", i == searchIndex)) { searchIndex = i; run = true; }
            if (i == searchIndex && ImGui.IsKeyPressed(ImGuiKey.DownArrow) || i == searchIndex && ImGui.IsKeyPressed(ImGuiKey.UpArrow)) ImGui.SetScrollHereY();
            var key = cmd.Key?.Invoke();
            ImGui.SameLine(330); Ui.TextDisabled(cmd.Category);
            if (!string.IsNullOrEmpty(key)) { ImGui.SameLine(430); Ui.TextDisabled(key); }
            if (!enabled) ImGui.EndDisabled();
        }
        if (found.Count == 0) Ui.TextDisabled("No command found.");
        ImGui.EndChild();
        ImGui.End();
        if (run && found.Count > 0)
        {
            var cmd = found[searchIndex];
            if (cmd.Enabled?.Invoke() ?? true)
            {
                searchOpen = false;
                recentCommands.Remove(cmd.Name); recentCommands.Insert(0, cmd.Name);
                if (recentCommands.Count > 12) recentCommands.RemoveAt(recentCommands.Count - 1);
                try { cmd.Run(); } catch (Exception e) { SetStatus($"{cmd.Name}: {e.Message}"); }
            }
            else SetStatus($"{cmd.Name} is not available here (select an object / go to Edit Mode).");
        }
        else if (close) searchOpen = false;
    }
}
