using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Library;
using MoloMax.Core.Paint;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Molo Paint workspace, laid out like Substance Painter: a tool strip on the left of the viewport
/// (brush, eraser, decal, fill colour) and a vertical shelf docked at the right of the viewport with thumbnails of the GTA
/// materials, the decals and the smart materials. One click applies; no window to open.
/// </summary>
public sealed partial class EditorApp
{
    const float ShelfW = 330;
    (float X, float Y, float W, float H) shelfRect;
    int shelfTab; // 0 GTA materials, 1 decals, 2 smart materials, 3 brush tips (alphas)
    string shelfSearch = "", shelfCategory = "All", shelfShader = "";
    List<LibMaterial> shelfItems = new();
    string shelfKey;
    (string Shader, int Count)[] shelfShaders; int shelfShadersTab = -1;

    bool PaintShelfVisible => paintMode && CurSet != null && Level == SubObject.Object;

    /// <summary>Vertical shelf docked at the right of the viewport: tabs, search, category and shader filters, thumbnail grid.</summary>
    void DrawPaintShelf()
    {
        if (!PaintShelfVisible) return;
        var (x, y, w, h) = shelfRect;
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.Begin("##shelf", Fixed);
        var ts = CurSet;
        float fw = ImGui.GetContentRegionAvail().X;

        string[] tabs = { "Materials", "Decals", "Smart", "Alphas", "Relief" };
        float tw = (fw - 8) / 5;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 2);
            if (Theme.Toggle(tabs[i], shelfTab == i, new Vector2(tw, 0))) shelfTab = i;
        }
        if (shelfTab < 2)
        {
            EnsureLibrary();
            ImGui.SetNextItemWidth(fw);
            ImGui.InputTextWithHint("##shsearch", "search name, shader, MLO...", ref shelfSearch, 80);
            if (shelfTab == 0)
            {
                ImGui.SetNextItemWidth(fw);
                var cats = MaterialLibrary.Categories.Where(c => c != "Decals" && c != "Dirt & stains").ToArray();
                int ci = Math.Max(0, Array.IndexOf(cats, shelfCategory));
                if (ImGui.Combo("##shcat", ref ci, cats, cats.Length)) shelfCategory = cats[ci];
                Theme.Tip("Category");
            }
            if (library != null)
            {
                if (shelfShadersTab != shelfTab) { shelfShaders = library.Shaders(shelfTab == 1).ToArray(); shelfShadersTab = shelfTab; shelfShader = ""; }
                ImGui.SetNextItemWidth(fw);
                if (ImGui.BeginCombo("##shshader", shelfShader == "" ? "All shaders" : shelfShader, ImGuiComboFlags.HeightLarge))
                {
                    if (ImGui.Selectable("All shaders", shelfShader == "")) shelfShader = "";
                    foreach (var (sh, n) in shelfShaders)
                        if (ImGui.Selectable($"{sh}  ({n})", shelfShader == sh)) shelfShader = sh;
                    ImGui.EndCombo();
                }
                Theme.Tip("Filter by GTA shader (.sps)");
            }
            Ui.TextDisabled(shelfTab == 0 ? "drag onto the model = new layer, onto a layer = replace" : "click = decal brush, then click the model");
            if (shelfTab == 0)
            {
                int sc = (int)shelfDropScope;
                ImGui.SetNextItemWidth(fw);
                if (ImGui.Combo("##dropscope", ref sc, DropScopeNames, DropScopeNames.Length)) shelfDropScope = (SurfaceScope)sc;
                Theme.Tip("Where a dropped material goes. Surface: the wall or floor under the mouse (it recognises walls and floors), shown in blue while you drag.");
            }
        }
        else if (shelfTab == 2) Ui.TextDisabled("drag onto the model to add it");
        else if (shelfTab == 3)
        {
            Ui.TextDisabled("click = brush tip (shape of the brush)");
            float bw = (fw - 2) / 2;
            if (ImGui.Button("Import alphas...", new Vector2(bw, 0))) ImportAlphas();
            Theme.Tip("Your own brush shapes (PNG, JPG, TGA): white = paint. They go in Documents\\Polymorph\\alphas.");
            ImGui.SameLine(0, 2);
            if (ImGui.Button("Open the folder##alf", new Vector2(bw, 0))) { Directory.CreateDirectory(BrushTips.UserFolder); try { System.Diagnostics.Process.Start("explorer.exe", BrushTips.UserFolder); } catch { } }
            ImGui.SetNextItemWidth(fw);
            ImGui.InputTextWithHint("##alsearch", "search...", ref alphaSearch, 60);
        }
        else
        {
            Ui.TextDisabled("click = new relief layer (height, tiles every metre)");
            ImGui.SetNextItemWidth(fw);
            ImGui.InputTextWithHint("##rlsearch", "search...", ref reliefSearch, 60);
        }
        ImGui.Separator();

        ImGui.BeginChild("##shelfitems", Vector2.Zero);
        const float gap = 6;
        float avail = ImGui.GetContentRegionAvail().X;
        int cols = Math.Max(1, (int)((avail + gap) / (92 + gap)));
        float cell = MathF.Floor((avail - gap * (cols - 1)) / cols);
        float rowH = cell + ImGui.GetTextLineHeightWithSpacing() + gap;
        if (shelfTab == 2)
        {
            int k = 0;
            void Group(string title, IEnumerable<string> names)
            {
                var list = names.ToList(); if (list.Count == 0) return;
                Ui.TextDisabled(title);
                for (int i = 0; i < list.Count; i++, k++)
                {
                    if (i % cols != 0) ImGui.SameLine(0, gap);
                    var name = list[i];
                    ImGui.BeginGroup();
                    var p = ImGui.GetCursorScreenPos();
                    ImGui.PushID(k);
                    ImGui.InvisibleButton("##sm", new Vector2(cell, cell));
                    bool hov = ImGui.IsItemHovered();
                    ShelfDragSource(null, name);
                    if (title == "Mine" && ImGui.BeginPopupContextItem()) { if (ImGui.MenuItem("Delete")) SmartLibrary.Delete(name); ImGui.EndPopup(); }
                    ImGui.PopID();
                    SmartSwatch(ImGui.GetWindowDrawList(), name, p, cell, hov);
                    Ui.TextDisabled(Clip(name, cell));
                    ImGui.EndGroup();
                }
            }
            Group("General", SmartMaterials.Names);
            Group("Los Santos", SmartMaterials.GtaNames);
            Group("Mine", SmartLibrary.Names());
            Ui.TextDisabled("Save your own in Layers > My smart materials.");
        }
        else if (shelfTab == 3) AlphaGrid(cols, cell, gap);
        else if (shelfTab == 4) ReliefGrid(cols, cell, gap);
        else if (library == null) ImGui.TextWrapped((libraryError ?? "Texture library not found.") + "  (Tools > Molo Materials to set its folder)");
        else
        {
            var key = $"{shelfTab}|{shelfCategory}|{shelfSearch}|{shelfShader}";
            if (key != shelfKey)
            {
                shelfItems = (shelfTab == 1
                    ? library.Search("All", shelfSearch, shelfShader).Where(m => m.IsDecal)
                    : library.Search(shelfCategory, shelfSearch, shelfShader).Where(m => !m.IsDecal)).Take(2000).ToList();
                shelfKey = key;
                ImGui.SetScrollY(0);
            }
            int rows = (shelfItems.Count + cols - 1) / cols;
            // vertical grid: only visible rows are drawn
            float scroll = ImGui.GetScrollY(), viewH = ImGui.GetWindowHeight();
            int firstRow = Math.Max(0, (int)(scroll / rowH) - 1), lastRow = Math.Min(rows - 1, (int)((scroll + viewH) / rowH) + 1);
            if (firstRow > 0) ImGui.Dummy(new Vector2(1, firstRow * rowH - ImGui.GetStyle().ItemSpacing.Y));
            for (int r = firstRow; r <= lastRow; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (i >= shelfItems.Count) break;
                    var m = shelfItems[i];
                    if (c > 0) ImGui.SameLine(0, gap);
                    ImGui.BeginGroup();
                    var p = ImGui.GetCursorScreenPos();
                    ImGui.PushID(i);
                    bool click = ImGui.InvisibleButton("##lm", new Vector2(cell, cell));
                    bool hov = ImGui.IsItemHovered();
                    if (shelfTab == 0) ShelfDragSource(m, null);
                    ImGui.PopID();
                    var dl = ImGui.GetWindowDrawList();
                    var srv = renderer.Textures.Thumb(library.PreviewPath(m));
                    if (srv != null) dl.AddImage(imgui.IdOf(srv), p, p + new Vector2(cell, cell));
                    else dl.AddRectFilled(p, p + new Vector2(cell, cell), 0xFF3A3A3A);
                    bool active = shelfTab == 1 && brush.StampName == m.Name;
                    dl.AddRect(p, p + new Vector2(cell, cell), active ? 0xFF30B0FF : hov ? 0xFFFFFFFF : 0xFF202020, 0, ImDrawFlags.None, active ? 3 : 1);
                    Ui.TextDisabled(Clip(m.Name, cell));
                    if (hov) ImGui.SetTooltip($"{m.Name}\n{m.Shader}\n{m.Category}");
                    ImGui.EndGroup();
                    if (click && shelfTab == 1) LibraryStamp(m); // decals: click picks the stamp brush
                }
            if (lastRow < rows - 1) ImGui.Dummy(new Vector2(1, (rows - 1 - lastRow) * rowH));
            if (shelfItems.Count == 0) Ui.TextDisabled("Nothing found.");
        }
        ImGui.EndChild();
        ImGui.End();
        ShelfDropOnViewport();
    }

    // ---------------------------------------------------------------- drag and drop from the shelf

    LibMaterial dragMat; string dragSmart;
    const string DragType = "MOLO_MAT", LayerDragType = "MOLO_LAYER";

    void ShelfDragSource(LibMaterial m, string smart)
    {
        if (!ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID)) return;
        dragMat = m; dragSmart = smart;
        unsafe { int dummy = 0; ImGui.SetDragDropPayload(DragType, (IntPtr)(&dummy), sizeof(int)); }
        if (m != null)
        {
            var srv = renderer.Textures.Thumb(library.PreviewPath(m));
            if (srv != null) ImGui.Image(imgui.IdOf(srv), new Vector2(64, 64));
        }
        ImGui.Text(m?.Name ?? smart);
        Ui.TextDisabled("drop on the model: new layer   |   on a layer: replace");
        ImGui.EndDragDropSource();
    }

    /// <summary>Released over the 3D view (not over a panel): adds the dragged material as a layer on the object under the mouse.</summary>
    void ShelfDropOnViewport()
    {
        if (dragMat == null && dragSmart == null) return;
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { dragMat = null; dragSmart = null; } // drag finished last frame
            return;
        }
        var mouse = ImGui.GetMousePos();
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)) return; // over a panel: the layer list takes the drop this frame
        var (m, smart) = (dragMat, dragSmart);
        dragMat = null; dragSmart = null;
        var vp = Viewports.FirstOrDefault(v => (!Maximized || v == Viewports[ActiveViewport]) && v.Contains(mouse));
        if (vp == null) return;
        var cur = Current;
        var hit = cur != null ? Picking.PickFace(cur, RayAt(vp, mouse)) : null;
        if (hit == null) { SetStatus("Drop the material on the object you are painting."); return; }
        var ts = CurSet;
        var faces = shelfDropScope == SurfaceScope.Whole ? null : Surfaces.Region(cur.Mesh, hit.Value.Face, shelfDropScope);
        var before = new HashSet<PaintLayer>(ts.Layers);
        if (m != null) LibraryFill(m);
        else
        {
            foreach (var l in SmartMaterials.Make(smart, Environment.TickCount & 0xFFFF)) AddLayer(ts, l);
            SetStatus($"{smart} added.");
        }
        if (faces != null && faces.Count > 0)
        {
            // only the new layers, only on the recognised surface
            foreach (var l in ts.Layers.Where(l => !before.Contains(l) && !l.IsFolder).ToList()) ts.LimitLayerToFaces(l, faces);
            var n = cur.Mesh.FaceNormal(cur.Mesh.Faces[hit.Value.Face]);
            string what = shelfDropScope switch { SurfaceScope.Walls => "every wall", SurfaceScope.Floor => "the floor", SurfaceScope.Ceiling => "the ceiling", _ => Surfaces.IsFloor(n) ? "the floor" : Surfaces.IsCeiling(n) ? "the ceiling" : Surfaces.IsWall(n) ? "this wall" : "this surface" };
            SetStatus($"{m?.Name ?? smart} on {what} ({faces.Count} face(s)). Change 'Drop on' above the materials for walls / floor / whole object.");
        }
    }

    static readonly string[] DropScopeNames = { "Drop on: surface under the mouse", "Drop on: every wall", "Drop on: the floor", "Drop on: the ceiling", "Drop on: whole object" };
    SurfaceScope shelfDropScope = SurfaceScope.Surface;

    /// <summary>While a material is dragged over the model: the faces it would cover, tinted blue.</summary>
    void ShelfDropPreviewLines()
    {
        if (dragMat == null && dragSmart == null) return;
        var cur = Current; if (cur == null || shelfDropScope == SurfaceScope.Whole) return;
        var mouse = ImGui.GetMousePos();
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)) return;
        var vp = Viewports.FirstOrDefault(v => (!Maximized || v == Viewports[ActiveViewport]) && v.Contains(mouse));
        if (vp == null || vp != drawViewport) return;
        var hit = Picking.PickFace(cur, RayAt(vp, mouse));
        if (hit == null) return;
        if (shelfPreview.Face != hit.Value.Face || shelfPreview.Scope != shelfDropScope || shelfPreview.Ver != cur.Version)
            shelfPreview = (hit.Value.Face, shelfDropScope, cur.Version, Surfaces.Region(cur.Mesh, hit.Value.Face, shelfDropScope));
        var w = cur.World; var m = cur.Mesh; var col = new Vector4(0.25f, 0.6f, 1f, 0.35f);
        foreach (var fi in shelfPreview.Faces)
        {
            if (fi >= m.Faces.Count) continue;
            var f = m.Faces[fi];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                lines.Tri(Vector3.Transform(m.Verts[f.V[a]], w), Vector3.Transform(m.Verts[f.V[b]], w), Vector3.Transform(m.Verts[f.V[c]], w), col);
        }
    }
    (int Face, SurfaceScope Scope, int Ver, HashSet<int> Faces) shelfPreview = (-1, SurfaceScope.Surface, -1, new());

    /// <summary>Drop target on a layer row: a material replaces that layer's textures, a smart material goes above it; layers can be dragged to reorder.</summary>
    void LayerDragDrop(TextureSet ts, int i)
    {
        if (ImGui.BeginDragDropSource())
        {
            unsafe { int idx = i; ImGui.SetDragDropPayload(LayerDragType, (IntPtr)(&idx), sizeof(int)); }
            ImGui.Text(ts.Layers[i].Name);
            ImGui.EndDragDropSource();
        }
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            var p = ImGui.AcceptDragDropPayload(DragType);
            if (p.NativePtr != null)
            {
                var l = ts.Layers[i];
                if (dragMat != null)
                {
                    if (l.Kind == LayerKind.Fill) { ApplyLibToLayer(l, dragMat); ts.SelectedLayer = i; l.TexturesChanged(); ts.Refresh(); SetStatus($"Layer '{l.Name}' now uses {dragMat.Name}."); }
                    else { ts.SelectedLayer = i; LibraryFill(dragMat); }
                }
                else if (dragSmart != null) { ts.SelectedLayer = i; foreach (var nl in SmartMaterials.Make(dragSmart, Environment.TickCount & 0xFFFF)) AddLayer(ts, nl); }
                dragMat = null; dragSmart = null;
            }
            var lp = ImGui.AcceptDragDropPayload(LayerDragType);
            if (lp.NativePtr != null)
            {
                int from = *(int*)lp.Data;
                if (from != i && from >= 0 && from < ts.Layers.Count)
                {
                    var l = ts.Layers[from]; var target = ts.Layers[i];
                    if (target.IsFolder && !l.IsFolder) { l.Folder = target.Id; ts.Layers.RemoveAt(from); ts.Layers.Insert(ts.Layers.IndexOf(target), l); } // dropped on a folder: goes inside, just under it
                    else { ts.Layers.RemoveAt(from); ts.Layers.Insert(i, l); if (!l.IsFolder) l.Folder = target.IsFolder ? -1 : target.Folder; }
                    ts.SelectedLayer = ts.Layers.IndexOf(l); ts.Refresh(); paintUndo.Clear();
                }
            }
        }
        ImGui.EndDragDropTarget();
    }

    static string Clip(string s, float width)
    {
        if (ImGui.CalcTextSize(s).X <= width) return s;
        while (s.Length > 1 && ImGui.CalcTextSize(s + "…").X > width) s = s[..^1];
        return s + "…";
    }

    /// <summary>Small drawing of a brush tip (sampled from its alpha).</summary>
    static void TipPreview(ImDrawListPtr dl, string name, Vector2 p, float s)
    {
        var tip = BrushTips.Get(name);
        if (tip == null) { dl.AddCircleFilled(p + new Vector2(s / 2), s * 0.38f, 0xFFE0E0E0, 32); return; }
        const int G = 32; float c = s / G;
        for (int y = 0; y < G; y++)
            for (int x = 0; x < G; x++)
            {
                int sx = x * tip.Width / G + tip.Width / G / 2, sy = y * tip.Height / G + tip.Height / G / 2;
                byte a = tip.Pixels[(sx + sy * tip.Width) * 4 + 3];
                if (a > 20) dl.AddRectFilled(p + new Vector2(x * c, y * c), p + new Vector2((x + 1) * c, (y + 1) * c), (uint)(a << 24) | 0x00E0E0E0);
            }
    }

    /// <summary>Little preview of a smart material: its layer colours stacked like the result (base, edges, dirt).</summary>
    static void SmartSwatch(ImDrawListPtr dl, string name, Vector2 p, float s, bool hov)
    {
        var layers = SmartMaterials.Make(name, 1);
        uint C(Vector3 c) => ImGui.ColorConvertFloat4ToU32(new Vector4(c, 1));
        dl.AddRectFilled(p, p + new Vector2(s, s), C(layers.Count > 0 ? layers[0].Color : new Vector3(0.5f)), 6);
        // edges / dirt as bands, metal as a highlight
        for (int i = 1; i < layers.Count; i++)
        {
            var l = layers[i];
            float t = i / (float)layers.Count;
            if (l.Mask == MaskGen.Edges) dl.AddRect(p + new Vector2(3), p + new Vector2(s - 3), C(l.Color), 6, ImDrawFlags.None, 5);
            else if (l.Mask == MaskGen.Top) dl.AddRectFilled(p, p + new Vector2(s, s * 0.25f), C(l.Color), 6, ImDrawFlags.RoundCornersTop);
            else dl.AddRectFilled(p + new Vector2(0, s * (0.7f + 0.1f * t)), p + new Vector2(s, s), C(l.Color), 6, ImDrawFlags.RoundCornersBottom);
        }
        if (layers.Any(l => l.Metal > 0.5f)) dl.AddCircleFilled(p + new Vector2(s * 0.3f, s * 0.3f), s * 0.08f, 0xA0FFFFFF);
        dl.AddRect(p, p + new Vector2(s, s), hov ? 0xFFFFFFFF : 0xFF202020, 6);
    }

    /// <summary>Vertical tool strip at the left of the active viewport while painting.</summary>
    void DrawPaintToolStrip()
    {
        if (!PaintShelfVisible) return;
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + 8, vp.Y + 40));
        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.Begin("##painttools", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        var sz = new Vector2(64, 28);
        foreach (var (t, label, tip) in ToolButtons)
        {
            if (Theme.Toggle(label, paintTool == t, sz)) SetPaintTool(t);
            Theme.Tip(tip);
        }
        ImGui.Separator();
        if (paintTool == PaintTool.VertexColour) ImGui.ColorEdit4("##vcol", ref vertexColour, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
        else ImGui.ColorEdit3("##bcol", ref brush.ColorValue, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
        Theme.Tip("Colour");
        ImGui.SetNextItemWidth(64);
        ImGui.DragFloat("##bsize", ref brush.Radius, 0.002f, 0.002f, 20f, "%.2f m");
        Theme.Tip("Brush size ([ and ])");
        ImGui.SetNextItemWidth(64);
        ImGui.SliderFloat("##bstr", ref brush.Strength, 0.02f, 1f, "%.2f");
        Theme.Tip("Strength");
        if (Theme.Toggle("Mask", brush.MaskMode, sz)) brush.MaskMode = !brush.MaskMode;
        Theme.Tip("Paint the mask of the selected layer");
        if (Theme.Toggle("Mirror X", brush.SymX, sz)) brush.SymX = !brush.SymX;
        ImGui.Separator();
        if (ImGui.Button("Undo", sz)) PaintUndo();
        if (ImGui.Button("Save", sz)) BakePaint(Current);
        Theme.Tip("Write the GTA textures (.dds, _n, _s)");
        if (ImGui.Button("Done", sz)) paintMode = false;
        ImGui.End();
    }
}
