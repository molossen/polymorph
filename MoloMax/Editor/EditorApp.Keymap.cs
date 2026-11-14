using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Keyboard shortcuts for the two keymaps and the small menus they open
/// (Add, Delete, Merge, Normals, Apply, Snap, Shading, View and the right-click context menu).
/// </summary>
public sealed partial class EditorApp
{
    public AppSettings Settings = AppSettings.Load();
    string pendingPopup;
    public bool ShowExplorer = true, ShowPanel = true;

    void OpenPopup(string id) { pendingPopup = id; if (id == "context") contextMenuOpens++; }
    int contextMenuOpens;
    bool closeCloneOpts;

    /// <summary>Shortcut label for menus, depending on the keymap (the 3ds Max label falls back to the Industry one).</summary>
    string K(string blender, string industry, string max = null) => Settings.Keymap switch
    {
        Keymap.Blender => blender,
        Keymap.Max => max ?? industry,
        _ => industry,
    };

    bool BlenderKeymap => Settings.Keymap == Keymap.Blender;
    bool MaxKeymap => Settings.Keymap == Keymap.Max;

    /// <summary>The Classic keymap and the Welcome chooser stay in the code but are off: Classic is only a look (Edit > Look).</summary>
    public const bool MaxModeEnabled = false;
    static readonly string[] KeymapNames = MaxModeEnabled ? new[] { "Standard", "Industry Compatible", "Classic" } : new[] { "Standard", "Industry Compatible" };

    public void SetKeymap(Keymap k)
    {
        Settings.Keymap = k; Settings.KeymapChosen = true; Settings.Save();
        Tool = Tool.Select;
        SetStatus(k switch
        {
            Keymap.Blender => "Keymap: Standard (MMB orbit, Shift+MMB pan, Ctrl+MMB zoom, G R S, Tab, Shift+A)",
            Keymap.Max => "Keymap: Classic (Alt+MMB orbit, MMB pan, Q W E R, 1-5 sub-objects, right-click quad menu)",
            _ => "Keymap: Industry Compatible (Alt+LMB orbit, Alt+MMB pan, Alt+RMB zoom, Q W E R, 1 2 3 / 4)",
        });
    }

    /// <summary>Standard or Classic look: colours and viewport layout only, the keys do not change.</summary>
    public void SetMode(UiMode mode)
    {
        Settings.Mode = mode; Settings.ModeChosen = true; Settings.Save();
        Theme.Apply(mode);
        Maximized = mode == UiMode.Blender;
        SetStatus(mode == UiMode.Blender ? "Standard look: one big view." : "Classic look: classic colours and 4 views. Your keys stay the same.");
    }

    // ---------------------------------------------------------------- shortcuts

    void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput) return; // Standard shortcuts work before first-run mode choice.
        if (FlyKeysActive) return; // right button held: the letters move the camera (game-style)
        if (ModalActive) { ModalKeys(); return; }
        if (transforming) { if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) CancelTransform(); return; }
        bool ctrl = io.KeyCtrl, shift = io.KeyShift, alt = io.KeyAlt;
        if ((uvHover || UvCanvasUnderMouse) && !ctrl) return; // the UV editor uses the letters, digits and arrows itself
        if (PaintKeys(ctrl)) return;
        if (!SculptMode && !paintMode && AnimKeys(ctrl, io.KeyShift, io.KeyAlt)) return;
        // menu search: F3 (Standard), Space (Industry Compatible: Tab is the edit mode toggle there too); preferences: Ctrl+,
        // search every command: F3 (Blender keymap), X in the classic / industry keymaps (Space too in industry)
        if (!ctrl && !shift && !alt && (BlenderKeymap ? P(ImGuiKey.F3) : (P(ImGuiKey.X) || Settings.Keymap == Keymap.Industry && P(ImGuiKey.Space)))) { OpenSearch(); return; }
        if (ctrl && P(ImGuiKey.Comma)) { prefsOpen = true; return; }
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool edit = Level != SubObject.Object && Current != null;

        // ---- common to all keymaps (file, undo)
        if (ctrl && P(ImGuiKey.Z))
        {
            if (shift) { if (MaxKeymap) FrameAll(); else DoRedo(); } // Max: Ctrl+Shift+Z = Zoom Extents All
            else DoUndo();
            return;
        }
        if (ctrl && P(ImGuiKey.Y)) { DoRedo(); return; }
        if (ctrl && P(ImGuiKey.S)) { if (MapMode && !shift) MapQuickSave(); else if (MapMode && SelectedMapDoc() is { } smd) SaveMapDoc(smd, saveAs: true); else Save(shift); return; }
        if (ctrl && shift && P(ImGuiKey.O)) { ImportDialog(); return; }
        if (ctrl && P(ImGuiKey.O)) { OpenDialog(); return; }
        if (ctrl && P(ImGuiKey.N) && !shift) { NewScene(); return; }
        if (P(ImGuiKey.F10)) { HubOpen = !HubOpen; return; }
        if (MaxKeymap) { MaxKeys(ctrl, shift, alt, edit); return; }
        if (BlenderKeymap) BlenderKeys(ctrl, shift, alt, edit);
        else IndustryKeys(ctrl, shift, alt, edit);
    }

    /// <summary>
    /// Blender 5.2 default keymap (left-click select), taken from keymap_data/blender_default.py:
    /// Window, Screen, 3D View, Object Mode, Object Non-modal and Mesh keymaps.
    /// </summary>
    void BlenderKeys(bool ctrl, bool shift, bool alt, bool edit)
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool none = !ctrl && !shift && !alt;
        var vp = Viewports[ActiveViewport];

        // Screen / Window
        if (P(ImGuiKey.F9) && none) { adjustOpen = !adjustOpen; return; }                       // screen.redo_last
        if (P(ImGuiKey.F2) && none && Current != null) { renameId = Current.Id; renameBuf = Current.Name; renameFocus = true; return; } // rename active item
        if (ctrl && alt && P(ImGuiKey.Q)) { Maximized = !Maximized; return; }                  // screen.region_quadview
        if (ctrl && P(ImGuiKey.Space)) { bool show = !(ShowPanel || ShowExplorer); ShowPanel = ShowExplorer = show; return; } // screen.screen_full_area
        // Object Non-modal
        if (P(ImGuiKey.Tab) && none) { ToggleEditMode(); return; }                              // object.mode_set EDIT toggle
        // 3D View Generic
        if (P(ImGuiKey.N) && none) { ShowPanel = !ShowPanel; return; }
        if (P(ImGuiKey.T) && none) { ShowExplorer = !ShowExplorer; return; }
        // 3D View
        if (NumpadViews(ctrl)) return;
        if (P(ImGuiKey.Home) && !shift) { FrameAll(); return; }
        if (P(ImGuiKey.KeypadDecimal)) { FrameSelection(); return; }
        if (shift && !ctrl && P(ImGuiKey.C)) { Cursor3D = Vector3.Zero; FrameAll(); return; }   // view_all center=True
        if (P(ImGuiKey.GraveAccent) && none) { OpenPopup("view"); return; }                     // VIEW3D_MT_view_pie
        if (P(ImGuiKey.V) && none) { OpenPopup("quickio"); return; }                            // MoloMax: quick import / export
        if ((P(ImGuiKey.KeypadDivide) || P(ImGuiKey.Slash)) && !ctrl && !shift) { IsolateSelection(); return; } // view3d.localview
        if (shift && !ctrl && P(ImGuiKey.Tab)) { Snap = !Snap; SetStatus(Snap ? $"Snapping on ({GridStep} m, {AngleSnap}°)" : "Snapping off"); return; }
        if (shift && !ctrl && P(ImGuiKey.S)) { OpenPopup("snap"); return; }                     // VIEW3D_MT_snap_pie
        if (P(ImGuiKey.Z) && none) { OpenPopup("shading"); return; }                            // VIEW3D_MT_shading_pie
        if (P(ImGuiKey.Z) && alt && !ctrl && !shift) { SelectVisibleOnly = !SelectVisibleOnly; SetStatus(SelectVisibleOnly ? "Visible only: edit-mode selection takes only what you can see" : "Selection goes through the mesh (hidden vertices / faces too)"); return; } // toggle_xray
        if (P(ImGuiKey.Z) && shift && !ctrl && !alt) { vp.WireframeOnly = !vp.WireframeOnly; return; }
        if (P(ImGuiKey.W) && alt && !ctrl && !shift) { vp.WireframeOnly = !vp.WireframeOnly; SetStatus(vp.WireframeOnly ? "Wireframe" : "Shaded"); return; } // Alt+W, like the Industry keys // toggle_shading WIREFRAME
        // select
        if (P(ImGuiKey.A) && !ctrl && !shift) { if (alt) SelectNone(); else SelectAll(); return; }
        if (ctrl && !shift && P(ImGuiKey.I)) { SelectInvert(); return; }
        if (ctrl && !shift && P(ImGuiKey.KeypadAdd)) { Grow(); return; }
        if (ctrl && !shift && !alt && edit && P(ImGuiKey.UpArrow)) { Grow(); return; }       // Ctrl+arrows: the arrows alone move the camera
        if (ctrl && !shift && !alt && edit && P(ImGuiKey.DownArrow)) { Shrink(); return; }
        if (ctrl && !shift && P(ImGuiKey.KeypadSubtract)) { Shrink(); return; }
        // transform
        if (!ctrl && !shift && P(ImGuiKey.G)) { if (alt) { if (!edit) ClearTransform(0); } else BeginModal(ModalKind.Translate); return; }
        if (!ctrl && !shift && P(ImGuiKey.R)) { if (alt) { if (!edit) ClearTransform(1); } else BeginModal(ModalKind.Rotate); return; }
        if (!ctrl && !shift && P(ImGuiKey.S)) { if (alt) { if (!edit) ClearTransform(2); } else BeginModal(ModalKind.Scale); return; }
        // hide
        if (P(ImGuiKey.H) && !ctrl) { if (alt) UnhideAll(); else if (shift) HideUnselected(); else HideSelection(); return; }

        if (!edit)
        {
            // Object Mode
            if (shift && !ctrl && !alt && P(ImGuiKey.A)) { OpenPopup("add"); return; }         // VIEW3D_MT_add
            if (ctrl && !shift && P(ImGuiKey.A)) { OpenPopup("apply"); return; }               // VIEW3D_MT_object_apply
            if (shift && !alt && !ctrl && P(ImGuiKey.D)) { DuplicateAndGrab(); return; }       // duplicate_move
            if (alt && !shift && !ctrl && P(ImGuiKey.D)) { DuplicateAndGrab(true); return; }   // duplicate_move_linked (instance)
            if (P(ImGuiKey.X) && !ctrl && !alt) { DeleteSelection(); return; }                     // delete straight away (no menu)
            if (P(ImGuiKey.Delete) && !ctrl) { DeleteSelection(); return; }                    // object.delete no confirm
            if (ctrl && !shift && P(ImGuiKey.J)) { JoinSelected(); return; }                   // object.join
            return;
        }

        // Mesh (edit mode)
        if (none)
        {
            if (P(ImGuiKey._1)) { SetLevelDirect(SubObject.Vertex); return; }
            if (P(ImGuiKey._2)) { SetLevelDirect(SubObject.Edge); return; }
            if (P(ImGuiKey._3)) { SetLevelDirect(SubObject.Polygon); return; }
            if (P(ImGuiKey._4)) { SetLevelDirect(SubObject.Element); return; }                 // MoloMax extra (free in Blender's edit mode)
            if (P(ImGuiKey.E)) { BeginModal(ModalKind.Extrude); return; }                      // extrude_move_normal
            if (P(ImGuiKey.I)) { BeginModal(ModalKind.Inset); return; }                        // mesh.inset
            if (P(ImGuiKey.F)) { MakeFace(); return; }                                         // edge_face_add
            if (P(ImGuiKey.L)) { SelectLinkedUnderMouse(); return; }                           // select_linked_pick
            if (P(ImGuiKey.M)) { OpenPopup("merge"); return; }                                 // VIEW3D_MT_edit_mesh_merge
            if (P(ImGuiKey.P)) { DetachFaces(false); return; }                                 // mesh.separate
            if (P(ImGuiKey.U)) { OpenPopup("uv"); return; }                                    // VIEW3D_MT_uv_map
            if (P(ImGuiKey.X) || P(ImGuiKey.Delete)) { MaxDelete(); return; }                          // deletes what is selected at the current level, no menu
        }
        if (shift && !ctrl && !alt && P(ImGuiKey.A)) { OpenPopup("add"); return; }             // VIEW3D_MT_mesh_add
        if (shift && !ctrl && !alt && P(ImGuiKey.D)) { DuplicateAndGrab(); return; }           // mesh.duplicate_move
        if (ctrl && !shift && (P(ImGuiKey.X) || P(ImGuiKey.Delete))) { MaxRemove(); return; }  // mesh.dissolve_mode
        if (ctrl && !shift && !alt && P(ImGuiKey.B)) { BeginModal(ModalKind.Chamfer); return; } // mesh.bevel (edges)
        if (none && P(ImGuiKey.K)) { BeginModal(ModalKind.Knife); return; }                       // mesh.knife_tool
        if (ctrl && !shift && P(ImGuiKey.R)) { BeginModal(ModalKind.LoopCut); return; }        // loopcut_slide
        if (ctrl && !shift && P(ImGuiKey.L)) { SelectLinked(); return; }                       // select_linked
        if (alt && !ctrl && P(ImGuiKey.N)) { OpenPopup("normals"); return; }                   // VIEW3D_MT_edit_mesh_normals
        if (shift && !alt && P(ImGuiKey.N)) { RecalcNormals(ctrl); return; }                   // normals_make_consistent (Ctrl = inside)
    }

    /// <summary>
    /// Blender 5.2 "Industry Compatible" keymap, taken from keymap_data/industry_compatible_data.py:
    /// Alt+LMB/MMB/RMB navigation, Q W E R tools, 1-3 component modes and 4 object mode, F1-F3 views.
    /// </summary>
    void IndustryKeys(bool ctrl, bool shift, bool alt, bool edit)
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool none = !ctrl && !shift && !alt;
        var cam = Viewports[ActiveViewport].Camera;

        // Tab: object mode <-> edit mode (same as the Standard keymap)
        if (P(ImGuiKey.Tab) && none) { ToggleEditMode(); return; }
        // 3D View Generic: Ctrl+[ toolbar, Ctrl+] sidebar
        if (ctrl && P(ImGuiKey.LeftBracket)) { ShowExplorer = !ShowExplorer; return; }
        if (ctrl && P(ImGuiKey.RightBracket)) { ShowPanel = !ShowPanel; return; }
        // 3D View
        if ((P(ImGuiKey.Enter) || P(ImGuiKey.KeypadEnter)) && none && Current != null) { renameId = Current.Id; renameBuf = Current.Name; renameFocus = true; return; }
        if (P(ImGuiKey.F1) && !shift && !alt) { cam.SetView(ctrl ? ViewKind.Back : ViewKind.Front); return; }
        if (P(ImGuiKey.F2) && !shift && !alt) { cam.SetView(ctrl ? ViewKind.Left : ViewKind.Right); return; }
        if (P(ImGuiKey.F3) && !shift && !alt) { cam.SetView(ctrl ? ViewKind.Bottom : ViewKind.Top); return; }
        if (P(ImGuiKey.F4) && none) { cam.SetView(ViewKind.Perspective); return; }               // view_camera (no cameras here)
        if (P(ImGuiKey.F5) && none) { cam.Orbit(MathF.PI / 0.006f, 0); return; }                // orbit 180°
        if (P(ImGuiKey.A) && !ctrl && !alt) { FrameAll(); return; }                             // view_all (Shift = all regions)
        if (P(ImGuiKey.F) && none) { FrameSelection(); return; }                                // view_selected
        if (P(ImGuiKey.V) && none) { OpenPopup("quickio"); return; }                            // MoloMax: quick import / export
        if (P(ImGuiKey.GraveAccent) && none) { OpenPopup("view"); return; }                     // view menu moved off V
        if (shift && !ctrl && P(ImGuiKey.I)) { IsolateSelection(); return; }                    // localview
        if (shift && !ctrl && !alt && P(ImGuiKey.X)) { OpenPopup("snap"); return; }            // VIEW3D_MT_snap_pie
        // Object Non-modal: 4 = object mode; 1/2/3 enter the component mode from anywhere
        if (none)
        {
            if (P(ImGuiKey._1)) { SetLevelDirect(SubObject.Vertex); return; }
            if (P(ImGuiKey._2)) { SetLevelDirect(SubObject.Edge); return; }
            if (P(ImGuiKey._3)) { SetLevelDirect(SubObject.Polygon); return; }
            if (P(ImGuiKey._4)) { if (Level != SubObject.Object) { lastEditLevel = Level; Level = SubObject.Object; } return; }
            // tools
            if (P(ImGuiKey.Q)) { Tool = Tool.Select; return; }
            if (P(ImGuiKey.W)) { Tool = Tool.Move; return; }
            if (P(ImGuiKey.E)) { Tool = Tool.Rotate; return; }
            if (P(ImGuiKey.R)) { Tool = Tool.Scale; return; }
            if (P(ImGuiKey.T)) { Tool = Tool.Move; return; }                                    // transform tool
        }
        // select
        if (ctrl && !alt && P(ImGuiKey.A)) { if (shift) SelectNone(); else SelectAll(); return; }
        if (ctrl && !shift && !alt && P(ImGuiKey.I)) { SelectInvert(); return; }
        if (P(ImGuiKey.UpArrow) && ctrl && !shift && !alt && edit) { Grow(); return; }          // select_more (Ctrl+Up: the arrows alone move the camera)
        if (P(ImGuiKey.DownArrow) && ctrl && !shift && !alt && edit) { Shrink(); return; }      // select_less
        if (ctrl && !shift && P(ImGuiKey.L) && edit) { SelectLinked(); return; }
        // hide: Ctrl+H hide, Shift+H hide unselected, Alt+H reveal
        if (P(ImGuiKey.H))
        {
            if (alt && !ctrl && !shift) { UnhideAll(); return; }
            if (shift && !ctrl && !alt) { HideUnselected(); return; }
            if (ctrl && !shift && !alt) { HideSelection(); return; }
        }
        // duplicate / delete
        if (ctrl && !shift && !alt && P(ImGuiKey.D)) { if (edit) DuplicateAndGrab(); else CloneSelection(false); return; }
        if ((P(ImGuiKey.Delete) || P(ImGuiKey.Backspace)) && !alt)
        {
            if (!edit) { DeleteSelection(); return; }
            if (ctrl) MaxRemove(); else MaxDelete();                                            // dissolve / delete straight away
            return;
        }
        // Alt+W: wireframe on / off (any mode)
        if (alt && !ctrl && !shift && P(ImGuiKey.W)) { var wv = Viewports[ActiveViewport]; wv.WireframeOnly = !wv.WireframeOnly; SetStatus(wv.WireframeOnly ? "Wireframe" : "Shaded"); return; }
        if (!edit)
        {
            // Alt+E / Alt+R clear rotation / scale (location: menu Object > Clear)
            if (alt && !ctrl && !shift && P(ImGuiKey.E)) { ClearTransform(1); return; }
            if (alt && !ctrl && !shift && P(ImGuiKey.R)) { ClearTransform(2); return; }
            return;
        }
        // Mesh tools
        if (ctrl && !shift && !alt && P(ImGuiKey.E)) { BeginModal(ModalKind.Extrude); return; } // extrude_region tool
        if (P(ImGuiKey.I) && none) { BeginModal(ModalKind.Inset); return; }                     // inset_faces tool
        if (alt && !ctrl && !shift && P(ImGuiKey.C)) { BeginModal(ModalKind.LoopCut); return; } // loop_cut tool
        if (ctrl && !shift && !alt && P(ImGuiKey.B)) { BeginModal(ModalKind.Chamfer); return; } // bevel tool
        if (shift && !ctrl && !alt && P(ImGuiKey.K)) { BeginModal(ModalKind.Knife); return; }  // knife tool
    }

    /// <summary>3ds Max 2016 default keyboard shortcuts.</summary>
    void MaxKeys(bool ctrl, bool shift, bool alt, bool edit)
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        var vp = Viewports[ActiveViewport];
        if (NumpadViews(ctrl)) return;
        if (!ctrl && !alt && !shift)
        {
            if (P(ImGuiKey.Q)) { Tool = Tool.Select; return; }
            if (P(ImGuiKey.W)) { Tool = Tool.Move; return; }
            if (P(ImGuiKey.E)) { Tool = Tool.Rotate; return; }
            if (P(ImGuiKey.R)) { Tool = Tool.Scale; return; }
            // sub-object levels: pressing the current level again goes back to the object
            if (P(ImGuiKey._1)) { MaxLevel(SubObject.Vertex); return; }
            if (P(ImGuiKey._2)) { MaxLevel(SubObject.Edge); return; }
            if (P(ImGuiKey._3)) { MaxLevel(SubObject.Border); return; }
            if (P(ImGuiKey._4)) { MaxLevel(SubObject.Polygon); return; }
            if (P(ImGuiKey._5)) { MaxLevel(SubObject.Element); return; }
            if (P(ImGuiKey._6)) { if (Level != SubObject.Object) { lastEditLevel = Level; Level = SubObject.Object; } return; }
            if (P(ImGuiKey.Z)) { FrameSelection(); return; }
            if (P(ImGuiKey.T)) { vp.Camera.SetView(ViewKind.Top); return; }
            if (P(ImGuiKey.B)) { vp.Camera.SetView(ViewKind.Bottom); return; }
            if (P(ImGuiKey.F)) { vp.Camera.SetView(ViewKind.Front); return; }
            if (P(ImGuiKey.L)) { vp.Camera.SetView(ViewKind.Left); return; }
            if (P(ImGuiKey.P)) { vp.Camera.SetView(ViewKind.Perspective); return; }
            if (P(ImGuiKey.U)) { ToggleOrtho(vp.Camera); return; }
            if (P(ImGuiKey.G)) { ShowGrid = !ShowGrid; return; }
            if (P(ImGuiKey.S)) { Snap = !Snap; SetStatus(Snap ? $"Snaps on ({GridStep} m, {AngleSnap}°)" : "Snaps off"); return; }
            if (P(ImGuiKey.A)) { Snap = !Snap; SetStatus(Snap ? $"Angle snap on ({AngleSnap}°)" : "Angle snap off"); return; }
            if (P(ImGuiKey.H)) { OpenPopup("selectbyname"); selectByNameFilter = ""; return; }
            if (P(ImGuiKey.M)) { materialEditorOpen = !materialEditorOpen; return; }
            if (P(ImGuiKey.F3)) { vp.WireframeOnly = !vp.WireframeOnly; return; }
            if (P(ImGuiKey.F4)) { vp.EdgedFaces = !vp.EdgedFaces; return; }
            if (P(ImGuiKey.F2) && Current != null) { renameId = Current.Id; renameBuf = Current.Name; renameFocus = true; return; }
            if (P(ImGuiKey.F9)) { adjustOpen = !adjustOpen; return; }
            if (P(ImGuiKey.Delete)) { if (edit) MaxDelete(); else DeleteSelection(); return; }
            if (P(ImGuiKey.Backspace) && edit) { MaxRemove(); return; }
            if (P(ImGuiKey.Home)) { FrameAll(); return; }
            if (P(ImGuiKey.GraveAccent)) { OpenPopup("view"); return; }
        }
        if (ctrl && !shift && !alt)
        {
            if (P(ImGuiKey.A)) { SelectAll(); return; }
            if (P(ImGuiKey.D)) { SelectNone(); return; }
            if (P(ImGuiKey.I)) { SelectInvert(); return; }
            if (P(ImGuiKey.V)) { if (Level == SubObject.Object && Selected.Count > 0) OpenPopup("cloneopts"); return; }
            if (P(ImGuiKey.Backspace) && edit) { RemoveEdges(true); return; }
            if (P(ImGuiKey.Space)) { Maximized = !Maximized; return; }
        }
        if (alt && !ctrl && !shift)
        {
            if (P(ImGuiKey.W)) { Maximized = !Maximized; return; }
            if (P(ImGuiKey.Q)) { IsolateSelection(); return; }
            if (P(ImGuiKey.L) && edit) { Loop(); return; }
            if (P(ImGuiKey.R) && edit) { Ring(); return; }
        }
        if (ctrl && alt && P(ImGuiKey.C) && edit) { Collapse(); return; }
        if (shift && !ctrl && P(ImGuiKey.E) && edit) { BeginModal(ModalKind.Extrude); return; }
        if (shift && ctrl && P(ImGuiKey.E) && edit) { ConnectEdges(); return; }
        if (shift && ctrl && P(ImGuiKey.I)) { SelectInvert(); return; }
        // edit mode: Ctrl+Up / Ctrl+Down grow / shrink the selection (the arrows alone move the camera)
        if (ctrl && !shift && !alt && edit && P(ImGuiKey.UpArrow)) { Grow(); return; }
        if (ctrl && !shift && !alt && edit && P(ImGuiKey.DownArrow)) { Shrink(); return; }
        if (shift && !ctrl && P(ImGuiKey.Z)) { OpenPopup("shading"); return; }
        if (alt && !ctrl && !shift && P(ImGuiKey.Z)) { SelectVisibleOnly = !SelectVisibleOnly; SetStatus(SelectVisibleOnly ? "Visible only: edit-mode selection takes only what you can see" : "Selection goes through the mesh (hidden vertices / faces too)"); return; }
        if (shift && P(ImGuiKey.Tab)) { Snap = !Snap; return; }
    }

    /// <summary>3ds Max sub-object switching: the number of the current level toggles back to the object.</summary>
    void MaxLevel(SubObject l)
    {
        if (Level == l) { lastEditLevel = l; Level = SubObject.Object; return; }
        SetLevelDirect(l);
    }

    /// <summary>Max Delete in Editable Poly: deletes the selected sub-objects (and the faces using them).</summary>
    void MaxDelete()
    {
        switch (Level)
        {
            case SubObject.Vertex: DoMesh("Delete Vertices", (o, m, s) => { MeshOps.DeleteVertices(m, s.VerticesOf(m, Level)); s.Clear(); }); break;
            case SubObject.Edge: case SubObject.Border: DoMesh("Delete Edges", (o, m, s) => { MeshOps.DeleteEdges(m, EdgesInScope(o, s)); s.Clear(); }); break;
            default: DoMesh("Delete Faces", (o, m, s) => { MeshOps.DeleteFaces(m, FacesFromSelection(m, s)); s.Clear(); }); break;
        }
    }

    /// <summary>Max Backspace (Remove): dissolves edges or vertices without opening holes.</summary>
    void MaxRemove()
    {
        if (Level == SubObject.Vertex)
            DoMesh("Remove Vertices", (o, m, s) =>
            {
                var v = s.VerticesOf(m, Level);
                var edges = m.BuildEdgeMap().Keys.Where(e => v.Contains(e.A) || v.Contains(e.B)).ToHashSet();
                MeshOps.RemoveEdges(m, edges, true); s.Clear();
            });
        else RemoveEdges(false);
    }

    /// <summary>Numpad 1/3/7 (Ctrl = opposite), 5 ortho toggle, 2/4/6/8 orbit by 15° — same in both keymaps.</summary>
    bool NumpadViews(bool ctrl)
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        var cam = Viewports[ActiveViewport].Camera;
        if (P(ImGuiKey.Keypad1)) { cam.SetView(ctrl ? ViewKind.Back : ViewKind.Front); return true; }
        if (P(ImGuiKey.Keypad3)) { cam.SetView(ctrl ? ViewKind.Left : ViewKind.Right); return true; }
        if (P(ImGuiKey.Keypad7)) { cam.SetView(ctrl ? ViewKind.Bottom : ViewKind.Top); return true; }
        if (P(ImGuiKey.Keypad5)) { ToggleOrtho(cam); return true; }
        float step = 15f * MathF.PI / 180f / 0.006f; // Orbit() takes pixels
        if (ImGui.IsKeyPressed(ImGuiKey.Keypad4)) { cam.Orbit(-step, 0); return true; }
        if (ImGui.IsKeyPressed(ImGuiKey.Keypad6)) { cam.Orbit(step, 0); return true; }
        if (ImGui.IsKeyPressed(ImGuiKey.Keypad8)) { cam.Orbit(0, step); return true; }
        if (ImGui.IsKeyPressed(ImGuiKey.Keypad2)) { cam.Orbit(0, -step); return true; }
        return false;
    }

    static void ToggleOrtho(Camera cam)
    {
        if (cam.Ortho) { cam.Ortho = false; cam.Kind = ViewKind.Perspective; }
        else { cam.OrthoHeight = MathF.Max(0.1f, 2f * cam.Distance * MathF.Tan(cam.Fov / 2)); cam.Ortho = true; cam.Kind = ViewKind.User; }
    }

    void ToggleEditMode()
    {
        if (Level != SubObject.Object) { lastEditLevel = Level; Level = SubObject.Object; return; }
        if (MapMode && !MapCanEdit()) return;
        if (Current == null) { SetStatus("Select an object to edit it."); return; }
        lastAdd = null;
        // the picked modifier in the stack is an Edit Poly / Unwrap UVW: Tab edits that step
        int am = ActiveModifierIndex(Current);
        if (am >= 0 && Current.FullStack[am] is MoloMax.Core.Modifiers.IEditableModifier)
        {
            EditModifier(Current, am, Current.FullStack[am] is MoloMax.Core.Modifiers.UnwrapUvwModifier ? SubObject.Polygon : lastEditLevel);
            if (Current.FullStack[am] is MoloMax.Core.Modifiers.UnwrapUvwModifier) OpenUvEditor();
            return;
        }
        Level = lastEditLevel;
    }
    SubObject lastEditLevel = SubObject.Vertex;

    /// <summary>Switch the component mode, entering edit mode if needed (Blender 1/2/3 in edit mode, Industry 1/2/3 anytime).</summary>
    void SetLevelDirect(SubObject l)
    {
        var cur = Current;
        if (cur == null) { SetStatus("Select an object to edit it."); return; }
        if (Level != SubObject.Object && Level != l) SubSel(cur).Convert(cur.Mesh, Level, l); // keep the selection, like Blender
        if (Level == SubObject.Object) lastAdd = null;
        Level = l; lastEditLevel = l;
    }

    // ---------------------------------------------------------------- popups

    void DrawPopups()
    {
        if (pendingPopup != null) { ImGui.OpenPopup(pendingPopup); pendingPopup = null; }
        // Clone Options after a Shift+drag (classic modeller): copy / instance and the number of copies
        if (ImGui.BeginPopup("Clone Options##shift"))
        {
            Ui.Label("Clone Options", Theme.Gold);
            ImGui.Separator();
            Ui.Label("Object", Theme.TextDim);
            ImGui.RadioButton("Copy##sc", ref shiftCloneMode, 0);
            ImGui.RadioButton("Instance##sc", ref shiftCloneMode, 1);
            Theme.Tip("Shares the mesh with the original: edit one, they all change. GTA: one model placed several times.");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Number of copies", ref shiftCloneCount); shiftCloneCount = Math.Clamp(shiftCloneCount, 1, 1000);
            if (shiftCloneDelta == Vector3.Zero && shiftCloneCount > 1) Ui.TextDisabled("More copies follow the move offset (Move tool).");
            if (ImGui.Button("OK", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter)) { ImGui.CloseCurrentPopup(); FinishShiftClone(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) { ImGui.CloseCurrentPopup(); CancelShiftClone(); }
            ImGui.EndPopup();
        }
        // Clone Options (Ctrl+V)
        if (ImGui.BeginPopup("cloneopts"))
        {
            ImGui.TextUnformatted("Clone Options");
            ImGui.Separator();
            int mode = CloneAsInstance ? 1 : 0;
            ImGui.RadioButton("Copy", ref mode, 0);
            Theme.Tip("A new, independent mesh");
            ImGui.RadioButton("Instance", ref mode, 1);
            Theme.Tip("Shares the mesh with the original: edit one, they all change.\nGTA: one model, placed several times.");
            CloneAsInstance = mode == 1;
            Ui.TextDisabled("Shift+drag uses this choice too");
            if (ImGui.Button("OK##2", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter)) { ImGui.CloseCurrentPopup(); CloneSelection(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##2", new Vector2(80, 0)) || closeCloneOpts) { ImGui.CloseCurrentPopup(); closeCloneOpts = false; }
            ImGui.EndPopup();
        }
        bool edit = Level != SubObject.Object && Current != null;

        if (ImGui.BeginPopup("add"))
        {
            Ui.TextDisabled("Add (at the 3D cursor)");
            if (ImGui.BeginMenu("Mesh"))
            {
                foreach (var k in new[] { CreateKind.Box, CreateKind.Plane, CreateKind.Sphere, CreateKind.Cylinder, CreateKind.Cone, CreateKind.Tube, CreateKind.Tufted })
                    if (ImGui.MenuItem(KindLabel(k))) AddPrimitive(k);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("GTA / MLO"))
            {
                foreach (var k in new[] { CreateKind.Room, CreateKind.Wall, CreateKind.Stairs, CreateKind.Door, CreateKind.Portal, CreateKind.CollisionBox })
                    if (ImGui.MenuItem(KindLabel(k))) AddPrimitive(k);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Import model (GLB, glTF, OBJ)...", "Ctrl+Shift+O")) ImportDialog();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("delete"))
        {
            if (edit)
            {
                Ui.TextDisabled("Delete");
                if (ImGui.MenuItem("Vertices")) DoMesh("Delete Vertices", (o, m, s) => { MeshOps.DeleteVertices(m, s.VerticesOf(m, Level)); s.Clear(); });
                if (ImGui.MenuItem("Edges")) DoMesh("Delete Edges", (o, m, s) => { var e = EdgesInScope(o, s); MeshOps.DeleteEdges(m, e); s.Clear(); });
                if (ImGui.MenuItem("Faces")) DoMesh("Delete Faces", (o, m, s) => { MeshOps.DeleteFaces(m, FacesFromSelection(m, s)); s.Clear(); });
                if (ImGui.MenuItem("Only Faces")) DoMesh("Delete Only Faces", (o, m, s) => { MeshOps.DeleteFaces(m, FacesFromSelection(m, s), false); s.Clear(); });
                ImGui.Separator();
                if (ImGui.MenuItem("Dissolve Edges")) DoMesh("Dissolve Edges", (o, m, s) => { MeshOps.RemoveEdges(m, EdgesInScope(o, s), true); s.Clear(); });
            }
            else if (ImGui.MenuItem("Delete")) DeleteSelection();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("merge"))
        {
            Ui.TextDisabled("Merge");
            if (ImGui.MenuItem("At Center")) Collapse();
            if (ImGui.MenuItem("At Cursor")) { SelectionToCursor(); Collapse(); }
            if (ImGui.MenuItem("By Distance")) Weld(weldT);
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("normals"))
        {
            Ui.TextDisabled("Normals");
            if (ImGui.MenuItem("Flip")) FlipFaces();
            if (ImGui.MenuItem("Recalculate Outside", "Shift+N")) RecalcNormals(false);
            if (ImGui.MenuItem("Recalculate Inside (MLO room)", "Ctrl+Shift+N")) RecalcNormals(true);
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("apply"))
        {
            Ui.TextDisabled("Apply");
            if (ImGui.MenuItem("Rotation & Scale")) ResetXForm();
            if (ImGui.MenuItem("All Transforms")) ApplyAllTransforms();
            ImGui.Separator();
            if (ImGui.MenuItem("Origin to Geometry")) CenterPivot();
            if (ImGui.MenuItem("Origin to Bottom")) PivotToBase();
            if (ImGui.MenuItem("Origin to 3D Cursor")) OriginToCursor();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("snap"))
        {
            Ui.TextDisabled("Snap");
            if (ImGui.MenuItem("Selection to Cursor")) SelectionToCursor();
            if (ImGui.MenuItem("Cursor to Selected")) CursorToSelected();
            if (ImGui.MenuItem("Cursor to World Origin")) Cursor3D = Vector3.Zero;
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("shading")) { ShadingItems(Viewports[ActiveViewport]); ImGui.EndPopup(); }

        if (ImGui.BeginPopup("uv"))
        {
            Ui.TextDisabled("UV");
            UvMenuItems();
            ImGui.Separator();
            if (ImGui.MenuItem("Box Map (1 m per tile)")) BoxMap(1f, true);
            if (ImGui.MenuItem("Box Map (2 m per tile)")) BoxMap(2f, true);
            if (ImGui.MenuItem("Box Map (0.5 m per tile)")) BoxMap(0.5f, true);
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("quickio")) { QuickIoMenu(); ImGui.EndPopup(); }

        if (ImGui.BeginPopup("view"))
        {
            var cam = Viewports[ActiveViewport].Camera;
            Ui.TextDisabled("View");
            foreach (var (k, key) in new[] { (ViewKind.Front, "Numpad 1"), (ViewKind.Back, "Ctrl+Numpad 1"), (ViewKind.Right, "Numpad 3"), (ViewKind.Left, "Ctrl+Numpad 3"), (ViewKind.Top, "Numpad 7"), (ViewKind.Bottom, "Ctrl+Numpad 7") })
                if (ImGui.MenuItem(k.ToString(), key)) cam.SetView(k);
            if (ImGui.MenuItem("Perspective / Orthographic", "Numpad 5")) ToggleOrtho(cam);
            ImGui.Separator();
            if (ImGui.MenuItem("Frame Selected", K("Numpad .", "F"))) FrameSelection();
            if (ImGui.MenuItem("Frame All", K("Home", "A"))) FrameAll();
            ImGui.EndPopup();
        }

        if (MaxKeymap)
        {
            ImGui.SetNextWindowPos(quadCenter, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (ImGui.BeginPopup("context", ImGuiWindowFlags.NoMove)) { QuadMenu(edit); ImGui.EndPopup(); }
        }
        else if (ImGui.BeginPopup("context")) { ContextMenu(edit); ImGui.EndPopup(); }

        SelectByName();
        ModeChooser();
    }

    HashSet<EdgeKey> EdgesInScope(SceneObject o, PolySelection s)
    {
        if (Level is SubObject.Edge or SubObject.Border) return new HashSet<EdgeKey>(s.Edges);
        var v = s.VerticesOf(o.Mesh, Level);
        return o.Mesh.BuildEdgeMap().Keys.Where(e => v.Contains(e.A) && v.Contains(e.B)).ToHashSet();
    }

    HashSet<int> FacesFromSelection(PolyMesh m, PolySelection s)
    {
        if (Level is SubObject.Polygon or SubObject.Element) return new HashSet<int>(s.Faces);
        var v = s.VerticesOf(m, Level);
        return Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].V.All(v.Contains)).ToHashSet();
    }

    void ContextMenu(bool edit)
    {
        if (!edit)
        {
            Ui.TextDisabled("Object");
            if (ImGui.MenuItem("Clone...", K("Shift+D", "Ctrl+V"), false, Selected.Count > 0)) OpenPopup("cloneopts");
            Theme.Tip("Copy or instance (shares the mesh) of the selection");
            if (ImGui.MenuItem("Join", "Ctrl+J", false, Selected.Count > 1)) JoinSelected();
            if (ImGui.MenuItem("Edit Mode", "Tab", false, Current != null)) ToggleEditMode();
            if (ImGui.MenuItem("Cut opening (door / window)", null, false, Current != null)) StartOpeningTool();
            if (ImGui.MenuItem("Clean up (fewer edges, same shape)", null, false, Current != null)) CleanUpSelected();
            Theme.Tip("Wall Opening: drag a rectangle on the wall, pull its sides, Enter cuts. No cutter, no boolean to set up.");
            if (ImGui.BeginMenu("Set Origin"))
            {
                if (ImGui.MenuItem("Origin to Geometry")) CenterPivot();
                if (ImGui.MenuItem("Origin to Bottom")) PivotToBase();
                if (ImGui.MenuItem("Origin to 3D Cursor")) OriginToCursor();
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Apply Rotation & Scale", K("Ctrl+A", ""))) ResetXForm();
            if (ImGui.MenuItem("Shade Smooth (auto 40°)")) { var objs = SelObjects().ToList(); Do("Shade Smooth", () => { foreach (var o in objs) { MeshOps.AutoSmooth(o.Mesh, null, 40); o.Touch(); } }); }
            if (ImGui.MenuItem("Shade Flat")) { var objs = SelObjects().ToList(); Do("Shade Flat", () => { foreach (var o in objs) { MeshOps.ClearSmoothing(o.Mesh, Enumerable.Range(0, o.Mesh.Faces.Count)); o.Touch(); } }); }
            UvMenu();
            if (ImGui.MenuItem("Auto scale (GTA size)", null, false, Selected.Count > 0)) AutoScale();
            if (ImGui.MenuItem("Add ped (scale)")) AddPedReference();
            if (ImGui.MenuItem("Flip Normals", null, false, Selected.Count > 0)) { var objs = SelObjects().ToList(); Do("Flip Normals", () => { foreach (var o in objs) { MeshOps.FlipAll(o.Mesh); o.Touch(); } }); }
            ImGui.Separator();
            SendToModelMenuItem();
            HideMenuItems();
            if (ImGui.MenuItem("Delete", K("X", "Del"), false, Selected.Count > 0)) DeleteSelection();
            ImGui.Separator();
            if (ImGui.MenuItem("Add...", "Shift+A")) OpenPopup("add");
            return;
        }
        Ui.TextDisabled(Level switch { SubObject.Vertex => "Vertex", SubObject.Edge or SubObject.Border => "Edge", _ => "Face" });
        if (Level is SubObject.Polygon or SubObject.Element)
        {
            if (ImGui.MenuItem("Extrude Faces", K("E", "Ctrl+E"))) BeginModal(ModalKind.Extrude);
            if (ImGui.MenuItem("Inset Faces", K("I", "I"))) BeginModal(ModalKind.Inset);
            if (ImGui.MenuItem("Subdivide (Tessellate)")) Tessellate();
            if (ImGui.MenuItem("Separate (Detach)", "P")) DetachFaces(false);
            bool br = ImGui.MenuItem("Bridge"); TutRect("bridge_face");
            if (br) Bridge();
            Theme.Tip("Joins two faces (or two groups of faces) with a tube, any corner counts");
        }
        if (Level is SubObject.Edge or SubObject.Border)
        {
            if (ImGui.MenuItem("Loop Cut", "Ctrl+R")) BeginModal(ModalKind.LoopCut);
            if (ImGui.MenuItem("Bridge")) Bridge();
            Theme.Tip("Joins two edges, two edge chains or two loops with new faces");
            if (ImGui.MenuItem("Connect (subdivide ring)")) ConnectEdges();
            if (ImGui.MenuItem("Select Loop")) Loop();
            if (ImGui.MenuItem("Select Ring")) Ring();
            if (ImGui.MenuItem("Dissolve Edges")) RemoveEdges(true);
            if (ImGui.MenuItem("Fill Hole (Cap)")) Cap();
        }
        if (Level == SubObject.Vertex)
        {
            if (ImGui.MenuItem("Merge at Center", "M")) Collapse();
            if (ImGui.MenuItem("Merge by Distance")) Weld(weldT);
        }
        if (Level is SubObject.Vertex or SubObject.Edge or SubObject.Border && ImGui.MenuItem("Separate (Detach)", "P")) DetachFaces(false);
        if (ImGui.MenuItem("Make Face", K("F", ""))) MakeFace();
        if (Level is not (SubObject.Edge or SubObject.Border) && ImGui.MenuItem("Loop Cut", "Ctrl+R")) BeginModal(ModalKind.LoopCut); // already in the edge section
        ImGui.Separator();
        UvMenu();
        if (ImGui.MenuItem("Flip Normals")) FlipFaces();
        if (ImGui.MenuItem("Recalculate Normals Outside", "Shift+N")) RecalcNormals(false);
        if (ImGui.MenuItem("Recalculate Normals Inside", "Ctrl+Shift+N")) RecalcNormals(true);
        ImGui.Separator();
        if (ImGui.MenuItem("Select All", K("A", "Ctrl+A"))) SelectAll();
        if (ImGui.MenuItem("Select Linked", "Ctrl+L")) SelectLinked();
        if (ImGui.MenuItem("Delete", K("X", "Del"))) MaxDelete();
        ImGui.Separator();
        if (ImGui.MenuItem("Object Mode", "Tab")) ToggleEditMode();
    }

    // ---------------------------------------------------------------- 3ds Max quad menu

    Vector2 quadCenter;

    static void QuadHeader(string title)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddRectFilled(p, p + new Vector2(w, ImGui.GetTextLineHeight() + 4), ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.25f, 0.25f, 1)));
        ImGui.SetCursorScreenPos(p + new Vector2(4, 2));
        Ui.TextColored(Theme.Gold, title);
        ImGui.Spacing();
    }

    /// <summary>The 3ds Max right-click quad menu: tools 1 / display on top, tools 2 / transform below.</summary>
    void QuadMenu(bool edit)
    {
        if (!ImGui.BeginTable("##quad", 2, ImGuiTableFlags.BordersInner | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("l", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("r", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableNextRow();

        // ---- top left: tools 1
        ImGui.TableNextColumn();
        QuadHeader("tools 1");
        if (edit)
        {
            foreach (var (l, n) in new[] { (SubObject.Vertex, "Vertex"), (SubObject.Edge, "Edge"), (SubObject.Border, "Border"), (SubObject.Polygon, "Polygon"), (SubObject.Element, "Element") })
                if (ImGui.MenuItem(n, ((int)l).ToString(), Level == l)) SetLevelDirect(l);
            if (ImGui.MenuItem("Top-level", "6")) { lastEditLevel = Level; Level = SubObject.Object; }
        }
        else
        {
            if (ImGui.BeginMenu("Create"))
            {
                foreach (CreateKind k in Enum.GetValues<CreateKind>())
                    if (k != CreateKind.None && ImGui.MenuItem(KindLabel(k))) { AddPrimitive(k); lastCreateKind = k; }
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Import model...", "Ctrl+Shift+O")) ImportDialog();
            if (ImGui.MenuItem("Editable Poly: Vertex", "1", false, Current != null)) SetLevelDirect(SubObject.Vertex);
            if (ImGui.MenuItem("Editable Poly: Polygon", "4", false, Current != null)) SetLevelDirect(SubObject.Polygon);
            if (ImGui.MenuItem("Attach (Join)", null, false, Selected.Count > 1)) JoinSelected();
        }

        // ---- top right: display
        ImGui.TableNextColumn();
        QuadHeader("display");
        HideMenuItems();
        var vp = Viewports[ActiveViewport];
        if (ImGui.MenuItem("Edged Faces", "F4", vp.EdgedFaces)) vp.EdgedFaces ^= true;
        if (ImGui.MenuItem("Face Orientation", null, FaceOrientation)) FaceOrientation = !FaceOrientation;
        if (ImGui.MenuItem("Wireframe", K("Shift+Z", "Alt+W", "F3"), vp.WireframeOnly)) vp.WireframeOnly ^= true;

        ImGui.TableNextRow();

        // ---- bottom left: tools 2
        ImGui.TableNextColumn();
        QuadHeader("tools 2");
        if (edit)
        {
            if (Level is SubObject.Polygon or SubObject.Element)
            {
                if (ImGui.MenuItem("Extrude", "Shift+E")) BeginModal(ModalKind.Extrude);
                if (ImGui.MenuItem("Inset")) BeginModal(ModalKind.Inset);
                if (ImGui.MenuItem("Bevel")) BevelFaces(bevelH, bevelO, byPolygon);
                if (ImGui.MenuItem("Tessellate")) Tessellate();
                if (ImGui.MenuItem("Detach")) DetachFaces(false);
                if (ImGui.MenuItem("Flip")) FlipFaces();
            }
            if (Level is SubObject.Edge or SubObject.Border)
            {
                if (ImGui.MenuItem("Connect", "Ctrl+Shift+E")) ConnectEdges();
                if (ImGui.MenuItem("Swift Loop")) BeginModal(ModalKind.LoopCut);
                if (ImGui.MenuItem("Remove", "Backspace")) MaxRemove();
                if (ImGui.MenuItem("Cap")) Cap();
            }
            if (Level == SubObject.Vertex)
            {
                if (ImGui.MenuItem("Weld")) Weld(weldT);
                if (ImGui.MenuItem("Collapse", "Ctrl+Alt+C")) Collapse();
                if (ImGui.MenuItem("Remove", "Backspace")) MaxRemove();
                if (ImGui.MenuItem("Swift Loop")) BeginModal(ModalKind.LoopCut);
            }
            if (Level is SubObject.Vertex or SubObject.Edge or SubObject.Border && ImGui.MenuItem("Detach##ve")) DetachFaces(false);
            if (ImGui.BeginMenu("Make Planar"))
            {
                if (ImGui.MenuItem("Level (same Z)")) MakePlanar(2);
                if (ImGui.MenuItem("Same X")) MakePlanar(0);
                if (ImGui.MenuItem("Same Y")) MakePlanar(1);
                if (ImGui.MenuItem("Best-fit plane")) MakePlanar(-1);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Create (Make Face)")) MakeFace();
            if (ImGui.MenuItem("Normals Inside (MLO room)")) RecalcNormals(true);
            if (ImGui.MenuItem("Normals Outside")) RecalcNormals(false);
        }
        else
        {
            if (ImGui.MenuItem("Reset XForm")) ResetXForm();
            if (ImGui.MenuItem("Pivot: Center")) CenterPivot();
            if (ImGui.MenuItem("Pivot: Bottom")) PivotToBase();
            if (ImGui.BeginMenu("Mirror"))
            {
                if (ImGui.MenuItem("X")) MirrorObjects(0);
                if (ImGui.MenuItem("Y")) MirrorObjects(1);
                if (ImGui.MenuItem("Z")) MirrorObjects(2);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Material Editor", "M")) materialEditorOpen = true;
            if (ImGui.MenuItem("Delete", "Del", false, Selected.Count > 0)) DeleteSelection();
        }

        // ---- bottom right: transform
        ImGui.TableNextColumn();
        QuadHeader("transform");
        if (ImGui.MenuItem("Move", "W", Tool == Tool.Move)) Tool = Tool.Move;
        if (ImGui.MenuItem("Rotate", "E", Tool == Tool.Rotate)) Tool = Tool.Rotate;
        if (ImGui.MenuItem("Scale", "R", Tool == Tool.Scale)) Tool = Tool.Scale;
        if (ImGui.MenuItem("Select", "Q", Tool == Tool.Select)) Tool = Tool.Select;
        if (edit)
        {
            if (ImGui.MenuItem("Select Loop", "Alt+L")) Loop();
            if (ImGui.MenuItem("Select Ring", "Alt+R")) Ring();
            if (ImGui.MenuItem("Grow")) Grow();
            if (ImGui.MenuItem("Shrink")) Shrink();
        }
        else
        {
            if (ImGui.MenuItem("Clone", "Ctrl+V / Shift+drag", false, Selected.Count > 0)) OpenPopup("cloneopts");
            if (ImGui.MenuItem("Instance", null, false, Selected.Count > 0)) CloneSelection(true);
            if (ImGui.MenuItem("Rename", "F2", false, Current != null)) { renameId = Current.Id; renameBuf = Current.Name; renameFocus = true; }
            if (ImGui.MenuItem("Select by Name...", "H")) { OpenPopup("selectbyname"); selectByNameFilter = ""; }
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- 3ds Max select by name (H)

    string selectByNameFilter = "";

    void SelectByName()
    {
        ImGui.SetNextWindowSize(new Vector2(360, 420), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f, gpu.Height / 2f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopup("selectbyname")) return;
        Ui.Text("Select From Scene");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        bool enter = ImGui.InputTextWithHint("##sbn", "Find...", ref selectByNameFilter, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.BeginChild("##sbnlist", new Vector2(0, -30), ImGuiChildFlags.Borders);
        var matches = Scene.Objects.Where(o => selectByNameFilter.Length == 0 || o.Name.Contains(selectByNameFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var o in matches)
        {
            ImGui.PushID(o.Id);
            if (ImGui.Selectable(o.Name + (o.Hidden ? "  (hidden)" : ""), Selected.Contains(o.Id)))
            {
                if (!ImGui.GetIO().KeyCtrl) Selected.Clear();
                Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged();
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        if (enter && matches.Count > 0) { Selected.Clear(); foreach (var o in matches) Selected.Add(o.Id); ActiveId = matches[0].Id; SelectionChanged(); ImGui.CloseCurrentPopup(); }
        if (ImGui.Button("OK##3", new Vector2(100, 0))) ImGui.CloseCurrentPopup();
        ImGui.SameLine(); Ui.TextDisabled($"{matches.Count} object(s)");
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- first run: Blender or 3ds Max mode

    bool modeChooserOpen;

    void ModeChooser()
    {
        if (!MaxModeEnabled) return;
        if (!Settings.ModeChosen && !modeChooserOpen) { ImGui.OpenPopup("Welcome to Polymorph"); modeChooserOpen = true; }
        var vpc = new Vector2(gpu.Width / 2f, gpu.Height / 2f);
        ImGui.SetNextWindowPos(vpc, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Welcome to Polymorph", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            Ui.Text("Pick the layout you prefer: its look, its shortcuts and its viewports.");
            Ui.TextDisabled("You can switch at any time: Edit > Look.");
            ImGui.Spacing();
            var bw = new Vector2(330, 40);
            ImGui.BeginGroup();
            if (Theme.Toggle("Standard mode##mode", Settings.Mode == UiMode.Blender, bw)) { SetMode(UiMode.Blender); modeChooserOpen = false; ImGui.CloseCurrentPopup(); }
            Ui.TextDisabled("Standard look (dark grey, blue, orange selection)\nOne big viewport, outliner + properties on the right\nMMB orbit · Shift+MMB pan · wheel zoom\nG / R / S then X Y Z and typed values\nTab edit mode · 1 2 3 vertex / edge / face\nShift+A add · E extrude · I inset · Ctrl+R loop cut\nX delete · F make face · right-click context menu");
            ImGui.EndGroup();
            ImGui.SameLine(0, 18);
            ImGui.BeginGroup();
            if (Theme.Toggle("Classic mode##mode", Settings.Mode == UiMode.Max, bw)) { SetMode(UiMode.Max); modeChooserOpen = false; ImGui.CloseCurrentPopup(); }
            Ui.TextDisabled("Classic look, 4 viewports, Command Panel\nAlt+MMB orbit · MMB pan · wheel zoom\nQ W E R select / move / rotate / scale (gizmo)\nShift+drag clone · Ctrl+V clone\n1-5 vertex / edge / border / polygon / element\nZ zoom extents · Alt+W maximize · H select by name\nF3 wireframe · F4 edged faces · right-click quad menu");
            ImGui.EndGroup();
            ImGui.EndPopup();
        }
    }

    // ---------------------------------------------------------------- shortcut reference (Help)

    (string Keys, string What)[] ShortcutTable() => Settings.Keymap == Keymap.Max ? MaxShortcuts : Settings.Keymap == Keymap.Blender
        ? new[]
        {
            ("MMB / Shift+MMB / Ctrl+MMB / wheel", "Orbit / Pan / Zoom"),
            ("Numpad 1 3 7 (Ctrl = opposite), 5, 2 4 6 8", "Front, Right, Top views, ortho toggle, orbit 15°"),
            ("Numpad . / Home / ` (² on AZERTY) / Numpad /", "Frame selected / Frame all / View menu / Local view"),
            ("LMB, Shift+LMB, drag", "Select, toggle, box (Shift adds, Ctrl removes)"),
            ("Alt+LMB / Ctrl+Alt+LMB", "Edge or face loop / ring"),
            ("A / Alt+A / Ctrl+I / L / Ctrl+L / Ctrl+Numpad +-", "Select all / none / invert / linked under mouse / linked / more, less"),
            ("G / R / S", "Move / Rotate / Scale: then X Y Z (twice = local, Shift = plane), type a value, Ctrl snaps"),
            ("Alt+G / Alt+R / Alt+S", "Clear location / rotation / scale"),
            ("Shift+A / Shift+RMB / Shift+C / Shift+S", "Add at 3D cursor / place cursor / reset cursor / snap menu"),
            ("Shift+D / X, Del / Ctrl+X / Ctrl+J / P", "Duplicate & move / Delete / Dissolve / Join / Separate"),
            ("Tab / 1 2 3", "Edit mode / Vertex, Edge, Face"),
            ("E / I / Ctrl+R / F / M", "Extrude / Inset / Loop cut / Make face / Merge"),
            ("Alt+N / Shift+N / Ctrl+Shift+N", "Normals menu / Recalculate outside / inside"),
            ("Ctrl+A / U", "Apply menu / UV menu"),
            ("H / Shift+H / Alt+H", "Hide / Hide others / Unhide"),
            ("Z / Shift+Z / Shift+Tab", "Shading menu / Wireframe / Snapping"),
            ("N / T / Ctrl+Space / Ctrl+Alt+Q", "Sidebar / Outliner / Maximize area / Quad view"),
            ("F2 / F9 / RMB", "Rename / Adjust last operation / Context menu"),
            ("Ctrl+Z / Ctrl+Shift+Z / Ctrl+S / Ctrl+Shift+O", "Undo / Redo / Save / Import model"),
        }
        : new[]
        {
            ("Alt+LMB / Alt+MMB / Alt+RMB, wheel", "Orbit / Pan / Zoom"),
            ("F1 F2 F3 (Ctrl = opposite) / F4 / F5", "Front, Right, Top views / Perspective / Orbit 180°"),
            ("F / A / V / Shift+I", "Frame selected / Frame all / View menu / Local view"),
            ("LMB, Shift+LMB, drag", "Select, toggle, box (Shift adds, Ctrl removes)"),
            ("Double-click / Alt+double-click", "Edge or face loop / ring"),
            ("Ctrl+A / Ctrl+Shift+A / Ctrl+I / Up, Down / Ctrl+L", "Select all / none / invert / more, less / linked"),
            ("Q / W / E / R / T", "Select / Move / Rotate / Scale / Transform tool"),
            ("Alt+W", "Wireframe on / off"),
            ("Alt+E / Alt+R", "Clear rotation / scale"),
            ("Shift+RMB / X / Shift+X", "Place cursor / Snapping / Snap menu"),
            ("Ctrl+D / Del, Backspace / Ctrl+Del", "Duplicate / Delete / Dissolve"),
            ("Tab / 1 2 3 / 4", "Edit mode on / off / Vertex, Edge, Face / Object mode"),
            ("Space", "Search every command"),
            ("Ctrl+E / I / Alt+C", "Extrude / Inset / Loop cut"),
            ("Ctrl+H / Shift+H / Alt+H", "Hide / Hide others / Unhide"),
            ("Ctrl+[ / Ctrl+] / Enter", "Toolbar (explorer) / Sidebar / Rename"),
            ("RMB", "Context menu (add, join, merge, normals...)"),
            ("Ctrl+Z / Ctrl+Shift+Z / Ctrl+S / Ctrl+Shift+O", "Undo / Redo / Save / Import model"),
        };

    static readonly (string, string)[] MaxShortcuts =
    {
        ("Alt+MMB / MMB / wheel, Ctrl+Alt+MMB", "Orbit / Pan / Zoom"),
        ("T B F L P U, Numpad 1 3 7 5", "Top, Bottom, Front, Left, Perspective, Orthographic"),
        ("Z / Ctrl+Shift+Z / Alt+W", "Zoom extents selected / all / Maximize viewport"),
        ("LMB, Ctrl+LMB, Alt+LMB, drag", "Select, add, remove, region select"),
        ("Double-click", "Edge loop / polygon loop"),
        ("Ctrl+A / Ctrl+D / Ctrl+I / H", "Select all / none / invert / Select by name"),
        ("Q / W / E / R", "Select / Move / Rotate / Scale (gizmo)"),
        ("Shift+drag gizmo / Ctrl+V", "Clone"),
        ("1 2 3 4 5 / 6 (same number again = object)", "Vertex, Edge, Border, Polygon, Element / Top-level"),
        ("Shift+E / Ctrl+Shift+E / Ctrl+Alt+C", "Extrude / Connect / Collapse"),
        ("Alt+L / Alt+R", "Edge loop / ring"),
        ("Del / Backspace / Ctrl+Backspace", "Delete / Remove / Remove with vertices"),
        ("Alt+Q / S, A / G", "Isolate / Snaps toggle / Grid"),
        ("F3 / F4 / M / F2", "Wireframe / Edged faces / Material Editor / Rename"),
        ("Right-click", "Quad menu (tools, display, transform)"),
        ("Ctrl+Z / Ctrl+Y / Ctrl+S / Ctrl+Shift+O / F9", "Undo / Redo / Save / Import model / Adjust last operation"),
    };
}
