using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Asset Browser: a grid of thumbnails of your own assets (folders of .mmx / .glb / .obj / .ydr / .yft) and of the
/// game's props by family. Thumbnails are rendered by the app the first time and kept on disk. Double-click (or drag
/// into the view) puts the asset in the scene; in MAP mode a game prop follows the mouse to be dropped.
/// </summary>
public sealed partial class EditorApp
{
    bool assetsOpen;
    int assetSource; // 0 my assets, 1 game props
    int assetFamily = 0;
    string assetSearch = "";
    float assetCell = 112;
    List<AssetItem> assetItems = new(); string assetKey;
    AssetItem assetSel;

    sealed class AssetItem
    {
        public string Name, File; public GameAsset Game;
        public string Key => Game != null ? "game_" + Game.Name + Game.Ext : "file_" + File.GetHashCode().ToString("X8") + "_" + System.IO.Path.GetFileName(File);
    }

    static string AssetThumbDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "Thumbnails");
    static string DefaultAssetFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "Assets");
    static readonly string[] AssetExts = { ".mmx", ".fbx", ".glb", ".gltf", ".obj", ".ydr", ".yft", ".ydd" };

    public void OpenAssetBrowser() { assetsOpen = true; if (Settings.AssetFolders.Count == 0) { Directory.CreateDirectory(DefaultAssetFolder); Settings.AssetFolders.Add(DefaultAssetFolder); Settings.Save(); } }

    string ThumbPath(AssetItem a) => Path.Combine(AssetThumbDir, string.Concat(a.Key.Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '_')) + ".png");

    void DrawAssetBrowser()
    {
        if (!assetsOpen) return;
        ImGui.SetNextWindowSize(new Vector2(980, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(explorerW + 40, gpu.Height - 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Asset Browser###assets", ref assetsOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        float h = ImGui.GetContentRegionAvail().Y;

        // ---- sources
        ImGui.BeginChild("##asrc", new Vector2(190, h), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
        Ui.Label("MY ASSETS", Theme.TextDim);
        for (int i = 0; i < Settings.AssetFolders.Count; i++)
        {
            var f = Settings.AssetFolders[i];
            if (ImGui.Selectable(Path.GetFileName(f.TrimEnd('\\', '/')) + "##af" + i, assetSource == 0 && assetFamily == i)) { assetSource = 0; assetFamily = i; assetSel = null; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(f);
            if (ImGui.BeginPopupContextItem("##afctx" + i))
            {
                if (ImGui.MenuItem("Show in Explorer")) { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + f + "\""); } catch { } }
                if (ImGui.MenuItem("Remove from the browser")) { Settings.AssetFolders.RemoveAt(i); Settings.Save(); assetKey = null; ImGui.EndPopup(); break; }
                ImGui.EndPopup();
            }
        }
        if (ImGui.SmallButton("+ Add a folder"))
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { Description = "A folder of your models (.mmx, .glb, .obj, .ydr, .yft)", UseDescriptionForTitle = true };
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK && !Settings.AssetFolders.Contains(d.SelectedPath)) { Settings.AssetFolders.Add(d.SelectedPath); Settings.Save(); assetSource = 0; assetFamily = Settings.AssetFolders.Count - 1; assetKey = null; }
            Input.ReleaseAllKeys();
        }
        Theme.Tip("Add a folder of your models: they show here as thumbnails, ready to drop in the scene.");
        ImGui.Spacing();
        Ui.Label("GAME PROPS", Theme.TextDim);
        if (game == null)
        {
            Ui.TextDisabled("Load the game files first.");
            if (ImGui.SmallButton("Open the game files")) OpenGameBrowser();
        }
        else
        {
            StartClassify();
            for (int i = 0; i < GameCategories.All.Length; i++)
                if (ImGui.Selectable(GameCategories.All[i].Name + "##gf" + i, assetSource == 1 && assetFamily == i)) { assetSource = 1; assetFamily = i; assetSel = null; }
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // ---- grid
        ImGui.BeginChild("##agrid", new Vector2(0, h), ImGuiChildFlags.Borders);
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##asearch", assetSource == 1 ? "search a prop (bench, chair, prop_bin...)" : "search my assets", ref assetSearch, 80);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120); ImGui.SliderFloat("##acell", ref assetCell, 72, 200, "size %.0f");
        ImGui.SameLine(); if (ImGui.SmallButton("Refresh")) assetKey = null;
        RefreshAssetItems();
        ImGui.SameLine(); Ui.TextDisabled($"{assetItems.Count:N0}{(assetItems.Count >= 3000 ? "+" : "")} assets");
        if (assetSel != null) { ImGui.SameLine(); Ui.TextDisabled("· double-click or drag into the view to place " + assetSel.Name); }

        ImGui.BeginChild("##acells", Vector2.Zero);
        float cell = assetCell;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 6) / (cell + 6)));
        int rows = (assetItems.Count + perRow - 1) / perRow;
        float rowH = cell + 24;
        float scroll = ImGui.GetScrollY(), viewH = ImGui.GetWindowHeight();
        int first = Math.Max(0, (int)(scroll / rowH) - 1), last = Math.Min(rows - 1, (int)((scroll + viewH) / rowH) + 1);
        ImGui.Dummy(new Vector2(0, first * rowH));
        var dl = ImGui.GetWindowDrawList();
        for (int r = first; r <= last; r++)
        {
            for (int c = 0; c < perRow; c++)
            {
                int i = r * perRow + c; if (i >= assetItems.Count) break;
                var a = assetItems[i];
                if (c > 0) ImGui.SameLine(0, 6);
                ImGui.BeginGroup(); ImGui.PushID(i);
                var p = ImGui.GetCursorScreenPos();
                if (ImGui.InvisibleButton("##t", new Vector2(cell, cell))) assetSel = a;
                bool hov = ImGui.IsItemHovered();
                if (hov && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) PlaceAsset(a, null);
                if (a.Game != null && ImGui.BeginPopupContextItem("##actx")) { assetSel = a; FamilyMenu(a.Game); ImGui.EndPopup(); }
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoPreviewTooltip)) { assetDrag = a; assetSel = a; ImGui.SetDragDropPayload("POLY_ASSET", IntPtr.Zero, 0); ImGui.TextUnformatted(a.Name); ImGui.EndDragDropSource(); }
                var tp = ThumbPath(a);
                var srv = File.Exists(tp) ? renderer.Textures.Thumb(tp) : null;
                if (srv == null) { dl.AddRectFilled(p, p + new Vector2(cell), 0xFF2A2A2E); dl.AddText(p + new Vector2(8, cell / 2 - 7), 0xFF808080, "..."); RequestThumb(a); }
                else dl.AddImage(imgui.IdOf(srv), p, p + new Vector2(cell));
                dl.AddRect(p, p + new Vector2(cell), assetSel == a ? 0xFF30B0FF : hov ? 0xFF707070 : 0xFF202020, 3, ImDrawFlags.None, assetSel == a ? 2.5f : 1);
                var label = a.Name.Length * 7 > cell ? a.Name[..Math.Max(4, (int)(cell / 7) - 1)] + "…" : a.Name;
                Ui.TextDisabled(label);
                if (hov) ImGui.SetTooltip(a.Game != null ? a.Game.Name + a.Game.Ext + "  (game)" : a.File);
                ImGui.PopID(); ImGui.EndGroup();
            }
        }
        ImGui.Dummy(new Vector2(0, Math.Max(0, rows - last - 1) * rowH));
        if (assetItems.Count == 0) Ui.TextDisabled(assetSource == 0 ? "No model in this folder yet: save .mmx scenes or put .glb / .obj / .ydr files in it." : "Type a few letters or pick another family.");
        ImGui.EndChild();
        ImGui.EndChild();
        ImGui.End();
    }

    void RefreshAssetItems()
    {
        var key = $"{assetSource}|{assetFamily}|{assetSearch}|{Settings.AssetFolders.Count}|{game != null}|{gameClassified}|{familyRev}";
        if (key == assetKey) return;
        assetKey = key;
        var text = assetSearch.Trim();
        var list = new List<AssetItem>();
        if (assetSource == 0 && assetFamily < Settings.AssetFolders.Count && Directory.Exists(Settings.AssetFolders[assetFamily]))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Settings.AssetFolders[assetFamily], "*.*", SearchOption.AllDirectories))
                {
                    if (!AssetExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (text.Length > 0 && !n.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new AssetItem { Name = n, File = f });
                    if (list.Count >= 3000) break;
                }
            }
            catch { }
        }
        else if (assetSource == 1 && game != null && gameClassified)
        {
            long bit = 1L << assetFamily;
            IEnumerable<GameAsset> q = text.Length >= 2 ? game.Search(text, true, true, false, false) : game.Models.Where(m => m.Ext is ".ydr" or ".yft");
            q = q.Where(m => (m.Categories & bit) != 0 && !m.Name.Contains("_lod") && !m.Name.EndsWith("_slod"));
            list.AddRange(q.Take(3000).Select(m => new AssetItem { Name = m.Name, Game = m }));
        }
        assetItems = list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- placing

    AssetItem assetDrag;

    /// <summary>Puts an asset in the scene: at <paramref name="at"/> (a drop point) or at the 3D cursor; MAP mode: on the ground.</summary>
    void PlaceAsset(AssetItem a, Vector3? at)
    {
        if (a.Game != null)
        {
            if (MapMode) { StartPlacement(a.Game.Name); return; }
            if (at is Vector3 p0) Cursor3D = p0;
            GameImport(a.Game, null);
            return;
        }
        var pos = at ?? (MapMode ? MapGround() : Cursor3D);
        if (a.File.EndsWith(".mmx", StringComparison.OrdinalIgnoreCase))
        {
            Scene src;
            try { src = SceneIO.Load(a.File); } catch (Exception e) { SetStatus("Could not open " + a.Name + ": " + e.Message); return; }
            if (MapMode) { var keep = Cursor3D; ImportSceneOnMap(src, a.Name); return; }
            PlaceSceneCopy(src, a.Name, pos);
            return;
        }
        var objs = ImportFile(a.File, pos);
        importReportOpen = false;
        if (MapMode) foreach (var o in objs.Concat(objs.Select(Scene.Parent).Where(x => x != null)).Distinct()) { o.Folder = ""; MapAdopt(o); }
        SetStatus($"{a.Name}: {objs.Count} object(s) placed.");
    }

    /// <summary>MODEL mode: a saved scene copied in, its base on <paramref name="at"/>.</summary>
    void PlaceSceneCopy(Scene src, string label, Vector3 at)
    {
        var items = src.Objects.Where(o => o.Mesh != null).ToList();
        if (items.Count == 0) return;
        var faces = items.Where(o => o.Mesh.Faces.Count > 0).ToList();
        var mn = faces.Select(o => o.WorldBounds().Min).DefaultIfEmpty(Vector3.Zero).Aggregate(Vector3.Min);
        var mx = faces.Select(o => o.WorldBounds().Max).DefaultIfEmpty(Vector3.Zero).Aggregate(Vector3.Max);
        var shift = at - new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
        var made = new List<SceneObject>();
        Do("Place " + label, () =>
        {
            var matMap = new Dictionary<int, int>();
            foreach (var m in src.Materials)
            {
                var same = Scene.Materials.FirstOrDefault(x => x.Name == m.Name && x.Shader == m.Shader);
                if (same != null) { matMap[m.Id] = same.Id; continue; }
                var c = m.Clone(); c.Id = Scene.NextMaterialId++; Scene.Materials.Add(c); matMap[m.Id] = c.Id;
            }
            var idMap = new Dictionary<int, int>();
            foreach (var o in items)
            {
                var c = o.Clone(); c.Id = Scene.NextObjectId++; idMap[o.Id] = c.Id;
                foreach (var f in c.Mesh.Faces) if (matMap.TryGetValue(f.MatId, out var nm)) f.MatId = nm;
                if (o.ParentId < 0) c.Position += shift;
                Scene.Objects.Add(c); made.Add(c);
            }
            foreach (var c in made) { c.ParentId = c.ParentId >= 0 && idMap.TryGetValue(c.ParentId, out var np) ? np : -1; c.Touch(); }
            Selected.Clear(); foreach (var c in made.Where(c => c.ParentId < 0)) Selected.Add(c.Id); SelectionChanged();
        });
        renderer.Collect(Scene);
        SetStatus($"{label}: {made.Count} object(s) placed.");
    }

    /// <summary>A drag from the browser released over a view: the asset lands where the mouse points.</summary>
    void AssetDropOnView()
    {
        if (assetDrag == null || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        var a = assetDrag; assetDrag = null;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)) return; // released over a window, not a view
        var m = Input.Mouse;
        var vp = Viewports.FirstOrDefault(v => m.X >= v.X && m.X < v.X + v.W && m.Y >= v.Y && m.Y < v.Y + v.H && v.W > 0);
        if (vp == null) return;
        Vector3 at;
        var ray = RayAt(vp, m);
        if (MapMode) { if (!MapPick(vp, m, -1, out at, out _)) return; }
        else
        {
            var hit = Picking.PickObject(Scene, ray);
            if (hit.HasValue) at = hit.Value.Point;
            else if (!Picking.RayGround(ray, 0, out at)) return;
        }
        PlaceAsset(a, at);
    }

    // ---------------------------------------------------------------- thumbnails

    readonly Queue<AssetItem> thumbQueue = new();
    readonly HashSet<string> thumbAsked = new();
    System.Threading.Tasks.Task<Scene> thumbTask; AssetItem thumbItem;
    PreviewTarget thumbRt;

    void RequestThumb(AssetItem a)
    {
        if (thumbAsked.Add(a.Key)) thumbQueue.Enqueue(a);
    }

    /// <summary>One thumbnail at a time: the model loads in the background, then it is drawn once and saved as a PNG.</summary>
    void PumpThumbs()
    {
        if (!assetsOpen) return;
        if (thumbTask == null)
        {
            while (thumbQueue.Count > 0)
            {
                var a = thumbQueue.Dequeue();
                if (File.Exists(ThumbPath(a))) continue;
                thumbItem = a;
                var g = game; var opt = ImportOpts();
                thumbTask = System.Threading.Tasks.Task.Run(() => LoadThumbScene(a, g, opt));
                break;
            }
            return;
        }
        if (!thumbTask.IsCompleted) return;
        var t = thumbTask; thumbTask = null;
        var path = ThumbPath(thumbItem);
        if (t.IsFaulted || t.Result == null) { try { WriteBlankThumb(path); } catch { } return; }
        var sc = t.Result;
        try { RenderThumb(sc, path); }
        catch { try { WriteBlankThumb(path); } catch { } }
        finally { renderer.FreePreview(sc.Objects.Select(o => o.Id)); }
    }

    static Scene LoadThumbScene(AssetItem a, GameFiles g, MoloMax.Core.IO.ImportOptions opt)
    {
        if (a.File != null && a.File.EndsWith(".mmx", StringComparison.OrdinalIgnoreCase))
        {
            var s = SceneIO.Load(a.File);
            int id = -300000; foreach (var o in s.Objects) o.Id = id--;
            foreach (var o in s.Objects) o.ParentId = -1; // drawn in world space below
            return s;
        }
        string file = a.File;
        if (a.Game != null) file = g.Extract(a.Game, Path.Combine(GameExtractDir, a.Game.Name));
        var r = MoloMax.Core.IO.ModelImporter.Import(file, opt);
        var sc = new Scene(); sc.Materials.Clear();
        for (int i = 0; i < r.Materials.Count; i++) { var m = r.Materials[i].Clone(); m.Id = i; sc.Materials.Add(m); }
        if (sc.Materials.Count == 0) sc.Materials.Add(MoloMax.Core.Gta.GtaMaterial.CreateDefault(0, "default", new Vector3(0.7f)));
        int nid = -200000;
        foreach (var im in r.Meshes) if (im.GtaType != GtaObjectType.Collision) sc.Objects.Add(new SceneObject { Id = nid--, Name = im.Name, Mesh = im.Mesh, GtaType = im.GtaType });
        foreach (var tex in r.TexturesWritten) sc.Gta.ResourceName = Path.GetDirectoryName(tex); // texture folder, picked up below
        return sc;
    }

    void RenderThumb(Scene sc, string path)
    {
        const int T = 192;
        thumbRt ??= new PreviewTarget(gpu.Device, T, T);
        if (!string.IsNullOrEmpty(sc.Gta.ResourceName) && Directory.Exists(sc.Gta.ResourceName) && !renderer.Textures.SearchFolders.Contains(sc.Gta.ResourceName))
        { renderer.Textures.SearchFolders.Add(sc.Gta.ResourceName); renderer.Textures.ClearResolveCache(); }
        var draw = sc.Objects.Where(o => o.Mesh != null && o.Mesh.Faces.Count > 0 && o.GtaType != GtaObjectType.Collision).ToList();
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var o in draw) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
        if (mn.X > mx.X) { mn = Vector3.Zero; mx = Vector3.One; }
        var centre = (mn + mx) * 0.5f; float dist = MathF.Max(0.3f, (mx - mn).Length() * 1.05f);
        var ctx = gpu.Context;
        ctx.OutputMerger.SetRenderTargets(thumbRt.Dsv, thumbRt.Rtv);
        ctx.ClearRenderTargetView(thumbRt.Rtv, new SharpDX.Mathematics.Interop.RawColor4(0.15f, 0.16f, 0.18f, 1));
        ctx.ClearDepthStencilView(thumbRt.Dsv, SharpDX.Direct3D11.DepthStencilClearFlags.Depth, 1f, 0);
        ctx.Rasterizer.SetViewport(0, 0, T, T);
        ctx.Rasterizer.SetScissorRectangle(0, 0, T, T);
        float yaw = -2.2f, pitch = 0.42f;
        var dir = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var eye = centre - dir * -dist;
        var view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1f, dist * 0.01f, dist * 20f);
        var blend = new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0);
        renderer.EditObjectId = int.MinValue;
        renderer.SetLights(null, Settings.Filmic ? RenderFlags.Filmic : RenderFlags.None, Matrix4x4.Identity, 0, 0);
        renderer.NoCull = false;
        renderer.BeginScene(view * proj, eye, MoloMax.Core.Gta.GtaLighting.TimeOfDay(13f, 1f), ShadingMode.Realistic, Exposure, MathF.Max(AmbientBoost, 0.25f));
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthDefault);
        ctx.OutputMerger.SetBlendState(renderer.Opaque, blend, -1);
        foreach (var o in draw) renderer.DrawObject(sc, o, false, Vector4.Zero, false);
        ctx.OutputMerger.SetBlendState(renderer.Alpha, blend, -1);
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
        foreach (var o in draw) renderer.DrawObject(sc, o, true, Vector4.Zero, false);
        gpu.BindBackBuffer();
        thumbRt.SavePng(ctx, path);
    }

    static void WriteBlankThumb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var bmp = new System.Drawing.Bitmap(8, 8);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.FromArgb(255, 42, 42, 46));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
