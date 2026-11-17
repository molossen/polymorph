using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Scenes;
using MoloMax.Core.Mesh;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Molo Hub: a GIMS Evo style panel — a home grid of task tiles, each opening one page
/// (export the FiveM resource, build the MLO, object properties, collision, checks, then the creation tools).
/// </summary>
public sealed partial class EditorApp
{
    public bool HubOpen;
    enum HubPage { Home, Export, Mlo, Object, Collision, Checks, Coming, Tripo, Tools, Anim, GTools, Create, Settings, Info, Results }
    HubPage hubPage = HubPage.Home;
    string hubComing = "";

    // export state
    System.Threading.Tasks.Task<ExportReport> exportTask;
    volatile string exportProgress = "";
    ExportReport lastReport;

    // MLO preview
    MloLayout hubLayout;
    double hubLayoutTime = -10;
    bool showRoomsInViewport = true;

    static readonly string[] Timecycles =
    {
        "", "int_gasstation", "int_carshowroom", "int_lesters", "int_hospital_dark", "int_hospital_small", "int_methlab_small",
        "int_clothesstore", "int_barber", "int_tattoo", "int_office_hall", "int_motel", "int_garage", "int_warehouse_small",
        "mp_gr_int01_white", "mp_gr_int01_grey", "mp_gr_int01_black", "mp_bkr_int01_garage", "mp_ba_club_dance", "int_nightclub",
    };

    static readonly Vector4 HubAccent = new(0.83f, 0.69f, 0.29f, 1f); // Molo aged gold

    // ---------------------------------------------------------------- window

    void DrawHub() => DrawGimsHub();

    public void OpenHub(int page = 0) { HubOpen = true; hubPage = (HubPage)page; }

    // ---------------------------------------------------------------- home grid

    bool Tile(string title, string text, float w, bool enabled = true)
    {
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(w, 74);
        bool clicked = ImGui.InvisibleButton(title, size);
        bool hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var bg = hover && enabled ? Theme.ButtonHover : Theme.Button;
        if (!enabled) bg *= new Vector4(0.8f, 0.8f, 0.8f, 1);
        dl.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(bg), ImGui.GetStyle().FrameRounding);
        dl.AddRectFilled(p, p + new Vector2(4, size.Y), ImGui.ColorConvertFloat4ToU32(enabled ? HubAccent : Theme.TextDim));
        dl.AddText(p + new Vector2(12, 8), ImGui.ColorConvertFloat4ToU32(enabled ? Theme.Text : Theme.TextDim), title);
        // wrap the description inside the tile
        var desc = enabled ? text : text + " (coming)";
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), p + new Vector2(12, 28), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), desc, w - 20);
        return clicked;
    }

    // ---- smart home: what the scene needs now, one-click actions, then every task
    List<MoloMax.Core.Gta.Check.CheckIssue> hubIssues = new();
    double hubIssuesTime = -10; int hubIssuesVersion = -1;
    string hubFilter = "";

    int SceneVersionSum() { unchecked { int v = Scene.Objects.Count * 7919; foreach (var o in Scene.Objects) v = v * 31 + o.Version + (o.Hidden ? 1 : 0); return v; } }

    void HubHome()
    {
        // quick check, refreshed when the scene changed (at most twice a second)
        int ver = SceneVersionSum();
        if ((ver != hubIssuesVersion && ImGui.GetTime() - hubIssuesTime > 0.5) || ImGui.GetTime() - hubIssuesTime > 10)
        {
            try { hubIssues = MoloMax.Core.Gta.Check.GtaChecker.Run(Scene); } catch { hubIssues = new(); }
            hubIssuesTime = ImGui.GetTime(); hubIssuesVersion = ver;
        }
        int errors = hubIssues.Count(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Error), warns = hubIssues.Count(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Warning);
        var objs = Scene.Objects;
        int meshes = objs.Count(o => o.GtaType == GtaObjectType.Drawable && o.Mesh.Faces.Count > 0), rooms = objs.Count(o => o.GtaType == GtaObjectType.MloRoom),
            portals = objs.Count(o => o.GtaType == GtaObjectType.Portal), cols = objs.Count(o => o.GtaType == GtaObjectType.Collision), lights = objs.Count(o => o.GtaType == GtaObjectType.Light),
            drawables = objs.Count(o => o.GtaType == GtaObjectType.Model);
        var dl = ImGui.GetWindowDrawList();
        float W = ImGui.GetContentRegionAvail().X;

        // ---- status card
        {
            var p = ImGui.GetCursorScreenPos(); float h = 70;
            dl.AddRectFilled(p, p + new Vector2(W, h), ImGui.ColorConvertFloat4ToU32(Theme.Rollout), 6);
            var stateCol = errors > 0 ? new Vector4(1f, 0.38f, 0.32f, 1) : warns > 0 ? HubAccent : new Vector4(0.12f, 0.71f, 0.64f, 1);
            dl.AddRectFilled(p, p + new Vector2(5, h), ImGui.ColorConvertFloat4ToU32(stateCol), 6);
            ImGui.SetCursorScreenPos(p + new Vector2(14, 8));
            Ui.Label(Scene.Gta.ResourceName, Theme.Text);
            ImGui.SetCursorScreenPos(p + new Vector2(14, 28));
            Ui.TextDisabled(meshes + drawables == 0 ? "Empty scene" : $"{meshes} mesh(es) · {drawables} drawable(s) · {rooms} room(s) · {portals} portal(s) · {cols} collision(s) · {lights} light(s)");
            ImGui.SetCursorScreenPos(p + new Vector2(14, 46));
            Ui.TextColored(stateCol, meshes + drawables == 0 ? "Nothing to export yet" : errors > 0 ? $"{errors} error(s) to fix before FiveM" : warns > 0 ? $"Exportable · {warns} warning(s)" : "Ready for FiveM");
            ImGui.SetCursorScreenPos(p + new Vector2(0, h + 6));
        }

        // ---- one-click actions
        float b3 = (W - 8) / 3;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.95f, 0.42f, 0.11f, 1)); ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.52f, 0.2f, 1)); ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
        ImGui.BeginDisabled(meshes + drawables == 0 || exportTask is { IsCompleted: false });
        if (ImGui.Button(exportTask is { IsCompleted: false } ? "Exporting..." : "Export now", new Vector2(b3, 30))) { hubPage = HubPage.Export; StartExport(); }
        ImGui.EndDisabled();
        ImGui.PopStyleColor(3);
        Theme.Tip("Writes the FiveM resource to " + Path.Combine(Scene.Gta.OutputFolder ?? "", Scene.Gta.ResourceName));
        ImGui.SameLine(0, 4);
        ImGui.BeginDisabled(hubIssues.Count == 0);
        if (ImGui.Button($"Fix all ({hubIssues.Count})", new Vector2(b3, 30))) { Do("Fix all", () => MoloMax.Core.Gta.Check.GtaChecker.FixAll(Scene)); hubIssuesTime = -10; }
        ImGui.EndDisabled();
        Theme.Tip("Vertex colours, names, normals: what the checker can fix by itself.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Send to server", new Vector2(b3, 30))) SendToServer();
        Theme.Tip("Copies the resource into your FiveM server and restarts it.");

        // ---- suggestions (what the scene / selection needs)
        var tips = new List<(string Text, Action Go)>();
        var cur = Current;
        if (meshes + drawables == 0)
        {
            tips.Add(("Bring a GTA model in (with its textures): Game Files", OpenGameBrowser));
            tips.Add(("Start an MLO: add rooms in the MLO builder", () => hubPage = HubPage.Mlo));
            tips.Add(("Generate a prop from text or a picture: Tripo", () => hubPage = HubPage.Tripo));
        }
        if (cur != null)
        {
            if (cur.GtaType == GtaObjectType.Collision || cur.GtaType == GtaObjectType.CollisionGroup) tips.Add(($"'{cur.Name}' is a collision: material, RoomID, flags", () => hubPage = HubPage.Collision));
            else if (cur.GtaType == GtaObjectType.Light) tips.Add(($"'{cur.Name}' is a light: colour, power, cone, GTA flags", () => { propTab = PropTab.Gta; ShowPanel = true; }));
            else if (cur.GtaType is GtaObjectType.Drawable or GtaObjectType.Model) tips.Add(($"'{cur.Name}': role (shell / prop / door), model name, LOD", () => hubPage = HubPage.Object));
            if (cur.GtaType == GtaObjectType.Model && !Scene.Descendants(cur).Any(d => d.GtaType == GtaObjectType.Collision)) tips.Add(($"'{cur.Name}' has no collision of its own: make one", () => hubPage = HubPage.Collision));
        }
        if (errors > 0) tips.Insert(0, ($"{errors} error(s): see the checks", () => { hubPage = HubPage.Checks; RunGtaChecks(); }));
        if (rooms > 0 && portals == 0) tips.Add(("Rooms but no portal yet: cut doorways, the MLO builder finds the portals", () => hubPage = HubPage.Mlo));
        if (rooms > 0 && cols == 0 && !Scene.Gta.AutoCollision) tips.Add(("No collision in the MLO and auto collision is off", () => hubPage = HubPage.Collision));
        if (meshes > 0 && string.IsNullOrWhiteSpace(Scene.Gta.OutputFolder)) tips.Add(("Choose where the resource goes (output folder)", () => hubPage = HubPage.Export));
        if (tips.Count > 0)
        {
            ImGui.Spacing();
            Ui.Label("SUGGESTED", Theme.TextDim);
            foreach (var (text, go) in tips.Take(4))
            {
                ImGui.PushID(text);
                if (ImGui.Selectable("›  " + text, false)) go();
                ImGui.PopID();
            }
        }

        // ---- every task, filterable
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##hubfilter", "Find a task...", ref hubFilter, 40);
        float w = (W - 6) / 2;
        var tiles = new (string Title, string Text, string Section, Action Go)[]
        {
            ("Export resource", "ydr, ybn, ytyp, ymap and fxmanifest, ready for FiveM.", "GTA V / FIVEM", () => hubPage = HubPage.Export),
            ("MLO builder", "Rooms, automatic portals, limbo, timecycles.", "GTA V / FIVEM", () => hubPage = HubPage.Mlo),
            ("Object properties", "Shell, prop, door or exterior; model name, LOD, flags.", "GTA V / FIVEM", () => hubPage = HubPage.Object),
            ("Collision", "Col groups, materials, RoomID, flags (stairs, water...).", "GTA V / FIVEM", () => hubPage = HubPage.Collision),
            ("Checks", "Vertex colours, normals, names, rooms.", "GTA V / FIVEM", () => { hubPage = HubPage.Checks; RunGtaChecks(); }),
            ("Game Files", "Search every GTA model by family, preview it, import it with its textures.", "GTA V / FIVEM", OpenGameBrowser),
            ("Tripo", "Text or image to 3D model, P2, Smart UV, texture.", "CREATE", () => hubPage = HubPage.Tripo),
            ("Molo Tools", "Doors, DDS, gradient, sign glow, AO, edge dirt, wear.", "CREATE", () => hubPage = HubPage.Tools),
            ("Molo Paint", "Paint textures on the model, layers and smart materials.", "CREATE", () => { propTab = PropTab.Paint; ShowPanel = true; }),
            ("UV", "Unwrap UVW modifier: seams, peel, relax, stitch, pack, density.", "CREATE", OpenUnwrapModifier),
            ("Sculpt", "Smooth, relax, flatten... to clean generated meshes.", "CREATE", ToggleSculptMode),
            ("Animation", "UV and object animations to YCD (30 fps).", "CREATE", () => hubPage = HubPage.Anim),
            ("Python & MCP", "Scripts and Claude driving Polymorph.", "CREATE", () => { prefsOpen = true; prefsSection = 8; }),
        };
        string section = null; int col = 0;
        foreach (var t in tiles)
        {
            if (hubFilter.Length > 0 && !(t.Title + " " + t.Text).Contains(hubFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Section != section) { if (col == 1) ImGui.NewLine(); section = t.Section; col = 0; ImGui.Spacing(); Ui.Label(section, Theme.TextDim); }
            if (col == 1) ImGui.SameLine(0, 6);
            if (Tile(t.Title, t.Text, w)) t.Go();
            col = 1 - col;
        }
    }

    void HubComing()
    {
        Ui.TextWrapped($"{hubComing} is being built and arrives in a next Polymorph update.");
    }

    // ---------------------------------------------------------------- export page

    void HubExport()
    {
        // two ways: the selected models, one file each (nothing else), or the whole FiveM resource
        float hw = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (Theme.Toggle("Selected models", exportSelectedOnly, new Vector2(hw, 26))) exportSelectedOnly = true;
        ImGui.SameLine(0, 4);
        if (Theme.Toggle("Whole resource", !exportSelectedOnly, new Vector2(hw, 26))) exportSelectedOnly = false;
        ImGui.Spacing();
        if (exportSelectedOnly) { HubExportSelected(); return; }
        var st = Scene.Gta;
        float lw = 120;
        void Label(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }
        bool exporting = exportTask != null && !exportTask.IsCompleted;
        ImGui.BeginDisabled(exporting);

        if (Ui.Header("Resource", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Label("Name");
            string name = st.ResourceName;
            if (ImGui.InputText("##resname", ref name, 64)) st.ResourceName = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) st.ResourceName = TextureBank.SafeName(st.ResourceName);
            Label("Output folder");
            if (string.IsNullOrEmpty(st.OutputFolder)) st.OutputFolder = DefaultExportFolder();
            string folder = st.OutputFolder;
            ImGui.SetNextItemWidth(-70);
            if (ImGui.InputText("##outdir", ref folder, 260)) st.OutputFolder = folder;
            ImGui.SameLine();
            if (ImGui.Button("Browse", new Vector2(-1, 0)))
            {
                using var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = st.OutputFolder, Description = "Folder that will receive the resource folder" };
                if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) st.OutputFolder = d.SelectedPath;
            }
            Ui.TextDisabled($"Writes {Path.Combine(st.OutputFolder ?? "", st.ResourceName)}\\  (stream\\mlo, stream\\exterior)");
        }

        if (Ui.Header("Placement on the map", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Label("Position");
            var o = Scene.GtaOrigin;
            if (ImGui.DragFloat3("##gtaorigin", ref o, 0.05f, 0, 0, "%.3f")) Scene.GtaOrigin = o;
            Label("Heading");
            ImGui.DragFloat("##heading", ref st.Heading, 0.5f, -360, 360, "%.1f°");
            if (ImGui.Button("Paste coords (vector3 / x, y, z)", new Vector2(-1, 0)))
            {
                var txt = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : "";
                // numbers inside vector3(...) / vector4(...) if present, never the 3 / 4 of the word itself
                var inner = Regex.Match(txt, @"\(([^)]*)\)");
                var body = inner.Success ? inner.Groups[1].Value : txt;
                var nums = Regex.Matches(body, @"(?<![A-Za-z_\d.])-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?").Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                if (nums.Count >= 3) { Scene.GtaOrigin = new Vector3(nums[0], nums[1], nums[2]); if (nums.Count >= 4) st.Heading = nums[3]; SetStatus("Map position pasted."); }
                else SetStatus("The clipboard has no x, y, z coordinates.");
            }
            Theme.Tip("Copy a position from FiveM (vector3 or vector4 with heading), CodeWalker or a script.");
            Ui.TextDisabled("The scene origin (0,0,0) lands on this point. The MLO is placed here.");
        }

        if (Ui.Header("Options"))
        {
            ImGui.Checkbox("Automatic collision when there is none", ref st.AutoCollision);
            ImGui.Checkbox("Shell in the limbo room (avoids the crash)", ref st.ShellInLimbo);
            ImGui.Checkbox("One shared pivot for every model (they import aligned)", ref st.SharedPivot);
            Theme.Tip("Every .ydr keeps the scene origin as pivot, so all your files import lined up. Doors, fragments and models placed several times keep their own pivot.");
            ImGui.Checkbox("LODs on the joined exterior too", ref st.ExteriorLods);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##defsets", "entity sets on by default (comma separated)", ref st.DefaultEntitySets, 200);
            ImGui.Checkbox("Separate .ytd per model (linked in the ytyp)", ref st.SeparateYtd);
            Theme.Tip("Off: textures embedded in the .ydr (easier while developing).");
            ImGui.Checkbox("Also write CodeWalker XML (for Sollumz / CodeWalker)", ref st.WriteXml);
            ImGui.SetNextItemWidth(120);
            int ts = Array.IndexOf(new[] { 256, 512, 1024, 2048 }, st.MaxTextureSize); if (ts < 0) ts = 2;
            if (ImGui.Combo("Max texture size", ref ts, new[] { "256", "512", "1024", "2048" }, 4)) st.MaxTextureSize = new[] { 256, 512, 1024, 2048 }[ts];
            ImGui.SetNextItemWidth(120);
            ImGui.DragInt("Join models up to (vertices)", ref st.MaxVertsPerModel, 500, 1000, 2000000);
            Theme.Tip("Small pieces are joined into models up to this size. There is no hard limit: every model is cut into 65k-vertex pieces at export.");
            ImGui.SetNextItemWidth(120);
            ImGui.DragFloat("Portal max gap (m)", ref st.PortalMaxGap, 0.01f, 0.05f, 3f, "%.2f");
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        bool busy = exportTask != null && !exportTask.IsCompleted;
        if (busy)
        {
            Theme.Toggle($"Exporting... {exportProgress}", true, new Vector2(-1, 38));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, HubAccent * new Vector4(0.8f, 0.8f, 0.8f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, HubAccent);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.05f, 1));
            if (ImGui.Button("EXPORT", new Vector2(-1, 38))) StartExport();
            ImGui.PopStyleColor(3);
            bool sending = sendTask is { IsCompleted: false };
            if (sending) ImGui.BeginDisabled();
            if (ImGui.Button(sending ? "Sending..." : "SEND TO MY FIVEM SERVER", new Vector2(-1, 28))) SendToServer();
            if (sending) ImGui.EndDisabled();
            Theme.Tip("Exports into your local server's resources folder and restarts the resource (bridge needed for the restart, see Preferences > GTA & FiveM).");
        }
        if (exportTask != null && exportTask.IsCompleted)
        {
            try { lastReport = exportTask.Result; }
            catch (Exception e) { lastReport = new ExportReport(); lastReport.Errors.Add(e.InnerException?.Message ?? e.Message); }
            exportTask = null;
            SetStatus(lastReport.Ok ? $"Exported {lastReport.Files.Count} files to {lastReport.ResourceFolder}" : "Export failed, see the GTA panel.");
        }
        if (lastReport != null) ReportView(lastReport);
    }

    string DefaultExportFolder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var map = Path.Combine(desktop, "base_para_testes", "resources", "[map]");
        return Directory.Exists(map) ? map : desktop;
    }

    void StartExport()
    {
        BakeAllPaint(); // painted textures written to their .dds before the exporter reads them
        // quick GTA check first: errors open the GTA Check window (export still runs)
        var issues = MoloMax.Core.Gta.Check.GtaChecker.Run(Scene);
        if (issues.Any(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Error)) RunGtaChecks();
        var snapshot = Undo.SnapshotForSave(Scene); // immutable, shares unchanged meshes (no full copy of big scenes)
        var folders = renderer.Textures.SearchFolders.ToList();
        var outDir = Scene.Gta.OutputFolder;
        exportProgress = "";
        exportTask = System.Threading.Tasks.Task.Run(() => GtaExporter.Export(snapshot, outDir, LoadImageRgba, folders, s => exportProgress = s));
    }

    /// <summary>PNG / JPG / BMP / GIF / TIFF to RGBA for the DDS encoder.</summary>
    static RgbaImage LoadImageRgba(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        var img = new RgbaImage(bmp.Width, bmp.Height);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < bmp.Width; x++)
                {
                    int s = x * 4, d = (y * bmp.Width + x) * 4;
                    img.Pixels[d] = row[s + 2]; img.Pixels[d + 1] = row[s + 1]; img.Pixels[d + 2] = row[s]; img.Pixels[d + 3] = row[s + 3];
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return img;
    }

    void ReportView(ExportReport r)
    {
        ImGui.Spacing();
        Ui.TextColored(r.Ok ? new Vector4(0.45f, 0.85f, 0.45f, 1) : new Vector4(1f, 0.4f, 0.35f, 1),
            r.Ok ? $"Done: {r.Models} model(s), {r.Entities} entities, {r.CollisionTriangles:N0} collision triangles." : "Export failed.");
        foreach (var e in r.Errors) Ui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1), "x " + e);
        foreach (var w in r.Warnings) { ImGui.PushTextWrapPos(); Ui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1), "! " + w); ImGui.PopTextWrapPos(); }
        foreach (var i in r.Info) { ImGui.PushTextWrapPos(); Ui.TextDisabled(i); ImGui.PopTextWrapPos(); }
        if (r.Files.Count > 0 && ImGui.TreeNode($"{r.Files.Count} files###files"))
        {
            foreach (var f in r.Files) Ui.TextDisabled(f);
            ImGui.TreePop();
        }
        if (r.ResourceFolder != null && Directory.Exists(r.ResourceFolder) && ImGui.Button("Open the resource folder", new Vector2(-1, 0)))
        {
            try { System.Diagnostics.Process.Start("explorer.exe", r.ResourceFolder); } catch { }
        }
    }

    // ---------------------------------------------------------------- MLO page

    MloLayout HubLayout()
    {
        if (hubLayout == null || ImGui.GetTime() - hubLayoutTime > 0.5)
        {
            try { hubLayout = MloLayout.Build(Scene, Scene.Gta); } catch { hubLayout = null; }
            hubLayoutTime = ImGui.GetTime();
        }
        return hubLayout;
    }

    void HubMlo()
    {
        var lay = HubLayout();
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Add room at cursor", new Vector2(w2, 0))) { AddPrimitive(CreateKind.Room); }
        ImGui.SameLine();
        if (ImGui.Button("Selected -> Room", new Vector2(-1, 0))) SetGtaType(GtaObjectType.MloRoom);
        if (ImGui.Button("Cut an opening in a room wall (door, window, arch, gothic, round)", new Vector2(-1, 0))) StartOpeningTool();
        Theme.Tip("Select the room, click, drag on its wall, pick Rect / Arch / Gothic / Round in the panel, Enter. The portal is found from the opening.");
        Theme.Tip("Rooms are closed boxes with normals facing inward. Cut the doorways in the walls: portals are found from those openings.");
        if (ImGui.Button("Selected -> Portal", new Vector2(w2, 0))) SetGtaType(GtaObjectType.Portal);
        Theme.Tip("A flat object standing in a doorway (between two rooms, or a room and the outside).");
        ImGui.SameLine();
        ImGui.Checkbox("Show in viewport", ref showRoomsInViewport);

        if (lay == null || lay.Rooms.Count <= 1)
        {
            ImGui.Spacing();
            Ui.TextWrapped("No room yet. Add a room box (or turn your room meshes into rooms), leave a small gap between rooms and cut a doorway in both walls: Polymorph finds the portals by itself.");
            return;
        }
        ImGui.Spacing();
        Ui.TextColored(HubAccent, $"{lay.Rooms.Count - 1} room(s), {lay.Portals.Count} portal(s), {lay.ExitPortals} to the outside");
        foreach (var wmsg in lay.Warnings) { ImGui.PushTextWrapPos(); Ui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1), "! " + wmsg); ImGui.PopTextWrapPos(); }

        if (Ui.Header("Rooms", ImGuiTreeNodeFlags.DefaultOpen) && ImGui.BeginTable("rooms", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22);
            ImGui.TableSetupColumn("Room");
            ImGui.TableSetupColumn("Timecycle", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();
            for (int i = 0; i < lay.Rooms.Count; i++)
            {
                var r = lay.Rooms[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); Ui.TextDisabled(i.ToString());
                ImGui.TableNextColumn();
                if (r.Source != null)
                {
                    if (ImGui.Selectable(r.Name, Selected.Contains(r.Source.Id))) { Selected.Clear(); Selected.Add(r.Source.Id); ActiveId = r.Source.Id; SelectionChanged(); }
                }
                else Ui.TextDisabled("limbo (outside)");
                ImGui.TableNextColumn();
                if (r.Source != null)
                {
                    ImGui.SetNextItemWidth(-1);
                    string tc = r.Source.Gta.RoomTimecycle ?? "";
                    if (ImGui.BeginCombo("##tc", tc.Length == 0 ? "(none)" : tc))
                    {
                        foreach (var t in Timecycles)
                            if (ImGui.Selectable(t.Length == 0 ? "(none)" : t, t == tc)) { r.Source.Gta.RoomTimecycle = t; Dirty = true; hubLayout = null; }
                        ImGui.EndCombo();
                    }
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (Ui.Header("Portals", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var p in lay.Portals)
            {
                string from = lay.Rooms[p.RoomFrom].Name, to = p.RoomTo == 0 ? "outside" : lay.Rooms[p.RoomTo].Name;
                float wdt = (p.Corners[0] - p.Corners[3]).Length(), h = (p.Corners[1] - p.Corners[0]).Length();
                Ui.Text($"{from} -> {to}");
                ImGui.SameLine(); Ui.TextDisabled($"{wdt:0.00} x {h:0.00} m  {p.Origin}");
            }
            if (lay.Portals.Count == 0) Ui.TextDisabled("No portal found.");
        }
        Ui.TextDisabled(Scene.Gta.ShellInLimbo ? "Shell attached to limbo (Export > Options)." : "Shell attached to its room.");
    }

    void SetGtaType(GtaObjectType t)
    {
        var objs = SelObjects().ToList();
        if (objs.Count == 0) { SetStatus("Select objects first."); return; }
        Do("Set GTA Type", () => { foreach (var o in objs) { o.GtaType = t; o.Touch(); } });
        hubLayout = null;
    }

    /// <summary>Rooms (cyan) and portals (gold) drawn over the viewport while the MLO or export page is open.</summary>
    void HubOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (!HubOpen || !showRoomsInViewport || (hubPage != HubPage.Mlo && hubPage != HubPage.Export)) return;
        var lay = HubLayout();
        if (lay == null || lay.Rooms.Count <= 1) return;
        bool P(Vector3 w, out Vector2 s)
        {
            bool ok = vp.Camera.Project(w, vp.W, vp.H, out var l, out _);
            s = l + new Vector2(vp.X, vp.Y);
            return ok;
        }
        uint roomCol = 0x9900D8FF, portalCol = 0xFF40D2FF, extCol = 0xFF40A0FF;
        for (int i = 1; i < lay.Rooms.Count; i++)
        {
            var r = lay.Rooms[i];
            var c = new Vector3[8];
            for (int k = 0; k < 8; k++) c[k] = new Vector3((k & 1) != 0 ? r.Max.X : r.Min.X, (k & 2) != 0 ? r.Max.Y : r.Min.Y, (k & 4) != 0 ? r.Max.Z : r.Min.Z);
            int[,] e = { { 0, 1 }, { 1, 3 }, { 3, 2 }, { 2, 0 }, { 4, 5 }, { 5, 7 }, { 7, 6 }, { 6, 4 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            for (int k = 0; k < 12; k++)
                if (P(c[e[k, 0]], out var a) && P(c[e[k, 1]], out var b)) dl.AddLine(a, b, roomCol, 1f);
            if (P((r.Min + r.Max) * 0.5f + new Vector3(0, 0, (r.Max.Z - r.Min.Z) * 0.5f), out var t)) dl.AddText(t, roomCol, $"{i} {r.Name}");
        }
        foreach (var p in lay.Portals)
        {
            var pts = new Vector2[4]; bool ok = true;
            for (int k = 0; k < 4; k++) ok &= P(p.Corners[k], out pts[k]);
            if (!ok) continue;
            uint col = p.RoomTo == 0 ? extCol : portalCol;
            dl.AddQuadFilled(pts[0], pts[1], pts[2], pts[3], (col & 0x00FFFFFF) | 0x40000000);
            dl.AddQuad(pts[0], pts[1], pts[2], pts[3], col, 2f);
        }
    }

    // ---------------------------------------------------------------- object properties page

    /// <summary>GTA light settings (Sollumz-like), applied to every selected light.</summary>
    void LightProps(List<SceneObject> objs)
    {
        LightEditor(objs.Where(x => x.GtaType == GtaObjectType.Light).ToList());
        Ui.TextDisabled("Lights under a Drawable go inside its model. Free lights go on the MLO shell (inside) or the exterior model.");
    }

    /// <summary>LOD settings of a model (Drawable node or standalone prop) and a button to make editable LOD meshes.</summary>
    void LodProps(List<SceneObject> objs, SceneObject o)
    {
        if (!Ui.Header("LODs (Medium / Low / Very low)")) return;
        bool auto = o.Gta.AutoLod;
        if (ImGui.Checkbox("Generate LODs at export", ref auto)) { foreach (var x in objs) x.Gta.AutoLod = auto; Dirty = true; }
        Theme.Tip("Used when you did not make LOD meshes yourself. Models under 300 triangles keep one level.");
        var r = o.Gta.LodRatios ??= new[] { 0.5f, 0.25f, 0.1f };
        string[] names = { "Medium", "Low", "Very low" };
        for (int i = 0; i < 3; i++)
        {
            float v = r[i] * 100;
            ImGui.SetNextItemWidth(-90);
            if (ImGui.SliderFloat(names[i] + " %##lr" + i, ref v, 1, 99, "%.0f %%")) { foreach (var x in objs) { x.Gta.LodRatios ??= (float[])r.Clone(); x.Gta.LodRatios[i] = v / 100f; } Dirty = true; }
        }
        var d = o.Gta.LodDistances ??= new float[4];
        string[] dn = { "High until", "Medium until", "Low until", "Very low until" };
        for (int i = 0; i < 4; i++)
        {
            float v = d[i];
            ImGui.SetNextItemWidth(-90);
            if (ImGui.DragFloat(dn[i] + "##ld" + i, ref v, 1, 0, 5000, v <= 0 ? "auto" : "%.0f m")) { foreach (var x in objs) { x.Gta.LodDistances ??= new float[4]; x.Gta.LodDistances[i] = MathF.Max(0, v); } Dirty = true; }
        }
        if (ImGui.Button("Make LOD meshes to edit (GIMS style)", new Vector2(-1, 0))) MakeLodMeshes(o);
        Theme.Tip("Creates hidden _med, _low and _vlow meshes under the Drawable from the current settings. Edit them freely: export uses them instead of generating.");
    }

    void MakeLodMeshes(SceneObject o)
    {
        var node = o.GtaType == GtaObjectType.Model ? o : Scene.Parent(o) is { GtaType: GtaObjectType.Model } p ? p : null;
        var highs = node != null ? Scene.Children(node).Where(c => c.GtaType == GtaObjectType.Drawable && c.Gta.LodLevel == 0 && c.Mesh.Faces.Count > 0).ToList() : new List<SceneObject> { o };
        if (highs.Count == 0) { SetStatus("No game mesh to make LODs from."); return; }
        var info = (node ?? o).Gta;
        int made = 0;
        Do("Make LOD meshes", () =>
        {
            if (node == null) { node = Scene.Add(o.Name, new MoloMax.Core.Mesh.PolyMesh(), o.Position, GtaObjectType.Model); node.Rotation = o.Rotation; node.Folder = o.Folder; node.Gta.LodRatios = (float[])info.LodRatios.Clone(); node.Gta.LodDistances = (float[])info.LodDistances.Clone(); Scene.SetParent(o, node); }
            string[] suffix = { "med", "low", "vlow" };
            foreach (var h in highs)
                for (int i = 0; i < 3; i++)
                {
                    float ratio = (info.LodRatios ?? new[] { 0.5f, 0.25f, 0.1f })[i];
                    var m = MoloMax.Core.Mesh.MeshSimplify.Decimate(h.Mesh, ratio);
                    var l = Scene.Add($"{h.Name}_{suffix[i]}", m, h.Position, GtaObjectType.Drawable);
                    l.Rotation = h.Rotation; l.Scale = h.Scale; l.Gta.LodLevel = i + 1; l.Hidden = true; l.WireColour = h.WireColour;
                    Scene.SetParent(l, node);
                    made++;
                }
        });
        SetStatus($"{made} LOD mesh(es) made (hidden). Show them in the Outliner to edit.");
    }

    static readonly (uint Bit, string Name)[] RoomFlagNames =
    {
        (1, "Freeze vehicles"), (2, "Freeze peds"), (4, "No directional light"), (8, "No exterior lights"), (16, "Force freeze"),
        (32, "Reduce cars"), (64, "Reduce peds"), (128, "Force directional light on"), (256, "Don't render exterior"), (512, "Mirror potentially visible"),
    };
    static readonly (uint Bit, string Name)[] PortalFlagNames =
    {
        (1, "One way"), (2, "Link interiors"), (4, "Mirror"), (8, "Ignore modifier"), (16, "Mirror (expensive shaders)"), (32, "Low LOD only"),
        (64, "Allow closing (doors)"), (128, "Mirror sees directional light"), (256, "Mirror using portal traversal"), (512, "Mirror floor"),
        (1024, "Mirror sees exterior"), (2048, "Water surface"), (4096, "Water to the horizon"), (8192, "Use light bleed"),
    };

    static bool FlagBoxes(ref uint flags, (uint Bit, string Name)[] names)
    {
        bool ch = false;
        foreach (var (bit, name) in names)
        {
            bool on = (flags & bit) != 0;
            if (ImGui.Checkbox(name + "##f" + bit, ref on)) { flags = on ? flags | bit : flags & ~bit; ch = true; }
        }
        Ui.TextDisabled($"value {flags}");
        return ch;
    }

    static uint NightHours() { uint m = 0; for (int h = 0; h < 24; h++) if (h >= 20 || h < 6) m |= 1u << h; return m; }

    public void AddLight(GtaLightKind kind)
    {
        SceneObject l = null;
        var parent = Current is { GtaType: GtaObjectType.Model } n ? n : Current != null ? Scene.Parent(Current) is { GtaType: GtaObjectType.Model } p ? p : null : null;
        Do("Add light", () =>
        {
            l = Scene.Add(kind == GtaLightKind.Spot ? "spot_light" : "light", new MoloMax.Core.Mesh.PolyMesh(), Cursor3D + new Vector3(0, 0, kind == GtaLightKind.Spot ? 2.5f : 2f), GtaObjectType.Light);
            l.Gta.Light = new GtaLight { Kind = kind };
            l.WireColour = new Vector3(1f, 0.85f, 0.4f);
            if (parent != null) Scene.SetParent(l, parent);
        });
        Selected.Clear(); Selected.Add(l.Id); ActiveId = l.Id; SelectionChanged();
        SetStatus(parent != null ? $"Light added under {parent.Name}." : "Light added at the 3D cursor.");
    }

    void HubObject()
    {
        var objs = SelObjects().ToList();
        if (objs.Count == 0) { Ui.TextWrapped("Select one or more objects. Changes apply to every selected object."); return; }
        var o = Current ?? objs[0];
        Ui.Text(objs.Count == 1 ? o.Name : $"{objs.Count} objects (showing {o.Name})");
        // a collision selected: its settings are one click away (like GIMS, collision has no tile of its own)
        if (o.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup && ImGui.Button("Collision settings (material, RoomID, flags)", new Vector2(-1, 0))) hubPage = HubPage.Collision;
        var lay = HubLayout();
        if (o.GtaType == GtaObjectType.Drawable)
            Ui.TextDisabled("Exports as: " + GtaExporter.ResolveRole(o, lay, Scene.Gta.PortalMaxGap, Scene));
        float lw = 120;
        void Label(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }

        Label("GTA type");
        int t = (int)o.GtaType;
        if (ImGui.Combo("##gtatype", ref t, new[] { "Game mesh", "Collision", "MLO Room", "Portal", "Drawable (GIMS node)", "Light", "Collision group (Drawable Col)" }, 7))
        {
            foreach (var x in objs) { x.GtaType = (GtaObjectType)t; if (x.GtaType == GtaObjectType.Light) x.Gta.Light ??= new GtaLight(); }
            Dirty = true; hubLayout = null;
        }
        if (o.GtaType == GtaObjectType.Light) { LightProps(objs); return; }
        if (o.GtaType == GtaObjectType.CollisionGroup)
        {
            var cols = objs.SelectMany(Scene.Descendants).Where(c => c.GtaType == GtaObjectType.Collision).Distinct().ToList();
            Ui.TextDisabled($"Holds {cols.Count} collision mesh(es). Settings below apply to all of them.");
            if (cols.Count > 0) CollisionProps(cols, cols[0]);
            return;
        }
        if (o.GtaType == GtaObjectType.Model)
        {
            Label("Role");
            int rr = (int)o.Gta.Role;
            if (ImGui.Combo("##rolem", ref rr, new[] { "Auto (own model)", "Shell (joined)", "Prop", "Door", "Exterior (joined)", "Do not export" }, 6)) { foreach (var x in objs) x.Gta.Role = (GtaRole)rr; Dirty = true; }
            bool fr = o.Gta.Fragment;
            if (ImGui.Checkbox("Fragment (.yft): physical, can be pushed", ref fr)) { foreach (var x in objs) x.Gta.Fragment = fr; Dirty = true; }
            Ui.TextDisabled("One Drawable = one model: its game meshes are joined, its collision and lights go inside.");
            LodProps(objs, o);
            return;
        }

        if (o.GtaType == GtaObjectType.Drawable)
        {
            Label("Role");
            int r = (int)o.Gta.Role;
            if (ImGui.Combo("##role", ref r, new[] { "Auto", "Shell (joined)", "Prop", "Door", "Exterior (joined)", "Do not export" }, 6)) { foreach (var x in objs) x.Gta.Role = (GtaRole)r; Dirty = true; }
            Theme.Tip("Auto: inside a room = prop (or shell for floors and ceilings), between rooms = shell, outside = exterior.");
            Label("Model name");
            string mn = o.Gta.ModelName ?? "";
            if (ImGui.InputTextWithHint("##model", o.Name.ToLowerInvariant(), ref mn, 63)) { foreach (var x in objs) x.Gta.ModelName = mn; Dirty = true; }
            Theme.Tip("Props sharing a model name become one .ydr placed several times.");
            Label("LOD distance");
            float lod = o.Gta.LodDist;
            if (ImGui.DragFloat("##lod", ref lod, 1, 0, 3000, lod <= 0 ? "auto" : "%.0f m")) foreach (var x in objs) x.Gta.LodDist = lod;
            Label("Entity flags");
            int ef = (int)o.Gta.EntityFlags;
            if (ImGui.InputInt("##eflags", ref ef, 0)) foreach (var x in objs) x.Gta.EntityFlags = (uint)Math.Max(0, ef);
            Theme.Tip("0 = automatic (props 1572872, shell 0). Animated props (YCD) use 1572872.");
            Label("Prop collision");
            int pc = (int)o.Gta.PropCollision;
            if (ImGui.Combo("##pcol", ref pc, new[] { "None", "Box", "Mesh" }, 3)) foreach (var x in objs) x.Gta.PropCollision = (GtaPropCollision)pc;
            bool frag = o.Gta.Fragment;
            if (ImGui.Checkbox("Fragment (.yft): physical, can be pushed##2", ref frag)) { foreach (var x in objs) { x.Gta.Fragment = frag; if (frag && x.Gta.Role == GtaRole.Auto) x.Gta.Role = GtaRole.Prop; } Dirty = true; }
            Theme.Tip("Exports a .yft like Sollumz: one bone, physics with the prop collision (a box when none) and the mass. Set Role to Prop.");
            if (o.Gta.Fragment)
            {
                Label("Mass");
                float fm = o.Gta.FragMass;
                if (ImGui.DragFloat("##fragmass", ref fm, 0.5f, 0.1f, 20000, "%.1f kg")) foreach (var x in objs) x.Gta.FragMass = fm;
            }
            if (Scene.Parent(o) is { GtaType: GtaObjectType.Model })
            {
                Label("LOD level");
                int lv = o.Gta.LodLevel;
                if (ImGui.Combo("##lodlv", ref lv, new[] { "High", "Medium", "Low", "Very low" }, 4)) { foreach (var x in objs) x.Gta.LodLevel = lv; Dirty = true; }
                Theme.Tip("GIMS: meshes under a Drawable tagged Medium / Low / Very low become its LODs.");
            }
            else LodProps(objs, o);
            Label("Animation");
            string ak = o.Gta.Anim == null || o.Gta.Anim.Kind == UvAnimKind.None ? "None" : o.Gta.Anim.Kind == UvAnimKind.Scroll ? "UV scroll" : "Spritesheet";
            if (ImGui.Button(ak + "  (edit...)##goanim", new Vector2(-1, 0))) hubPage = HubPage.Anim;
        }
        if (o.GtaType == GtaObjectType.MloRoom)
        {
            Label("Timecycle");
            string tc = o.Gta.RoomTimecycle ?? "";
            if (ImGui.InputText("##rtc", ref tc, 64)) foreach (var x in objs) x.Gta.RoomTimecycle = tc;
            if (ImGui.BeginCombo("##rtcpick", "pick a timecycle...")) { foreach (var tcn in Timecycles) if (ImGui.Selectable(tcn.Length == 0 ? "(none)" : tcn)) foreach (var x in objs) x.Gta.RoomTimecycle = tcn; ImGui.EndCombo(); }
            if (ImGui.TreeNode("Room flags"))
            {
                uint f = o.Gta.RoomFlags;
                if (FlagBoxes(ref f, RoomFlagNames)) { foreach (var x in objs) x.Gta.RoomFlags = f; Dirty = true; hubLayout = null; }
                ImGui.TreePop();
            }
        }
        if (o.GtaType == GtaObjectType.Portal)
        {
            if (ImGui.TreeNode("Portal flags"))
            {
                uint f = o.Gta.PortalFlags;
                if (FlagBoxes(ref f, PortalFlagNames)) { foreach (var x in objs) x.Gta.PortalFlags = f; Dirty = true; hubLayout = null; }
                ImGui.TreePop();
            }
        }
        if (o.GtaType is GtaObjectType.Drawable or GtaObjectType.Model && (o.Gta.Role is GtaRole.Prop or GtaRole.Door or GtaRole.Auto))
        {
            Label("Entity set");
            string es = o.Gta.EntitySet ?? "";
            if (ImGui.InputTextWithHint("##eset", "none (always there)", ref es, 63)) { foreach (var x in objs) x.Gta.EntitySet = es; Dirty = true; }
            Theme.Tip("MLO entity set: props in a set can be switched on / off by scripts (ActivateInteriorEntitySet). Default sets are in Export options.");
        }
        if (o.GtaType == GtaObjectType.Collision) { CollisionProps(objs, o); return; }
        if (o.GtaType is GtaObjectType.Drawable or GtaObjectType.MloRoom)
        {
            Label("Collision mat.");
            CollisionMaterialCombo(objs, o);
        }
        if (o.GtaType is GtaObjectType.Drawable or GtaObjectType.MloRoom)
        {
            bool nac = o.Gta.NoAutoCollision;
            if (ImGui.Checkbox("No auto collision (decor, glass, curtains)", ref nac)) foreach (var x in objs) x.Gta.NoAutoCollision = nac;
        }
    }

    /// <summary>Collision: one-click setups (floor, stairs, water, glass...), material, RoomID, flags (auto or by hand).</summary>
    void CollisionProps(List<SceneObject> objs, SceneObject o)
    {
        float lw = 110;
        void Label(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }
        Ui.TextDisabled("Quick setup");
        float w4 = (ImGui.GetContentRegionAvail().X - 6) / 4;
        for (int i = 0; i < CollisionFlags.Presets.Length; i++)
        {
            var (label, mat, flags, tip) = CollisionFlags.Presets[i];
            if (i % 4 != 0) ImGui.SameLine(0, 2);
            if (ImGui.Button(label, new Vector2(w4, 0)))
                Do("Collision: " + label, () =>
                {
                    foreach (var x in objs)
                    {
                        if (mat != null) x.Gta.CollisionMaterial = CollisionMaterials.IndexOf(mat);
                        if (label == "Floor") { x.Gta.CollisionFlagsAuto = true; x.Gta.CollisionFlags = 0; }
                        else { x.Gta.CollisionFlagsAuto = false; x.Gta.CollisionFlags = flags; }
                    }
                });
            Theme.Tip(tip);
        }
        ImGui.Spacing();
        Label("Material");
        CollisionMaterialCombo(objs, o);
        Label("RoomID");
        bool autoRoom = o.Gta.RoomIdOverride < 0;
        if (ImGui.Checkbox("Auto##rida", ref autoRoom)) foreach (var x in objs) x.Gta.RoomIdOverride = autoRoom ? -1 : 1;
        Theme.Tip("Auto: the room the triangle is in (MLO), 0 outside. Off: forced value (the shell collision uses RoomID 1).");
        if (!autoRoom)
        {
            ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
            int rid = Math.Max(0, o.Gta.RoomIdOverride);
            if (ImGui.DragInt("##rid", ref rid, 0.1f, 0, 31)) foreach (var x in objs) x.Gta.RoomIdOverride = Math.Clamp(rid, 0, 31);
        }
        Label("Flags");
        bool auto = o.Gta.CollisionFlagsAuto;
        if (Theme.Toggle(auto ? "AUTO" : "Manual", auto, new Vector2(-1, 0))) foreach (var x in objs) { if (x.Gta.CollisionFlagsAuto) x.Gta.CollisionFlags = CollisionFlags.Auto(x); x.Gta.CollisionFlagsAuto = !auto; }
        Theme.Tip("Auto: from the name, the material and the shape (stairs, glass, water, foliage...). Click to set them by hand.");
        uint cur = CollisionFlags.Resolve(o);
        Ui.TextWrapped(cur == 0 ? "No flag (normal surface)" : string.Join(", ", CollisionFlags.All.Where(f => (cur & f.Bit) != 0).Select(f => f.Label)));
        if (!auto && ImGui.TreeNode("Edit the flags"))
        {
            foreach (var (bit, _, label) in CollisionFlags.All)
            {
                bool on = (o.Gta.CollisionFlags & bit) != 0;
                if (ImGui.Checkbox(label + "##cf" + bit, ref on)) foreach (var x in objs) x.Gta.CollisionFlags = on ? x.Gta.CollisionFlags | bit : x.Gta.CollisionFlags & ~bit;
            }
            ImGui.TreePop();
        }
    }

    void CollisionMaterialCombo(List<SceneObject> objs, SceneObject o)
    {
        var cur = CollisionMaterials.NameOf(o.Gta.CollisionMaterial);
        if (ImGui.BeginCombo("##cmat", cur, ImGuiComboFlags.HeightLarge))
        {
            Ui.TextDisabled("Common");
            foreach (var (label, name) in CollisionMaterials.Common)
                if (ImGui.Selectable($"{label}  ({name})", name == cur)) foreach (var x in objs) x.Gta.CollisionMaterial = CollisionMaterials.IndexOf(name);
            ImGui.Separator();
            Ui.TextDisabled("All");
            for (int i = 0; i < CollisionMaterials.Names.Length; i++)
                if (ImGui.Selectable($"{i}  {CollisionMaterials.Names[i]}", i == o.Gta.CollisionMaterial)) foreach (var x in objs) x.Gta.CollisionMaterial = i;
            ImGui.EndCombo();
        }
    }

    // ---------------------------------------------------------------- collision page

    void HubCollision()
    {
        var st = Scene.Gta;
        Ui.TextWrapped("Automatic: the export builds collision from the shell (MLO, RoomID = the room of each triangle) and the exterior, with each object's material. Collision objects are added on top.");
        ImGui.Checkbox("Automatic collision", ref st.AutoCollision);
        ImGui.Separator();
        Ui.TextDisabled("Manual");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Copy selection as collision", new Vector2(-1, 0))) MakeCollision(false);
        Theme.Tip("Duplicates the selected meshes as Collision objects (same place, same shape).");
        if (ImGui.Button("Box collision from selection", new Vector2(-1, 0))) MakeCollision(true);
        if (ImGui.Button("Selected -> Collision", new Vector2(w2, 0))) SetGtaType(GtaObjectType.Collision);
        ImGui.SameLine();
        if (ImGui.Button("Selected -> Drawable", new Vector2(-1, 0))) SetGtaType(GtaObjectType.Drawable);
        var objs = SelObjects().ToList();
        if (objs.Count > 0)
        {
            ImGui.Spacing();
            Ui.Text("Material of the selection");
            ImGui.SetNextItemWidth(-1);
            CollisionMaterialCombo(objs, Current ?? objs[0]);
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            int k = 0;
            foreach (var (label, name) in CollisionMaterials.Common.Take(9))
            {
                if (k++ % 3 != 0) ImGui.SameLine();
                if (ImGui.Button(label, new Vector2(w3, 0))) foreach (var x in objs) x.Gta.CollisionMaterial = CollisionMaterials.IndexOf(name);
            }
        }
        int cols = Scene.Objects.Count(o => o.GtaType == GtaObjectType.Collision);
        ImGui.Spacing();
        Ui.TextDisabled(cols == 0 ? "No Collision object: the automatic collision is used alone." : $"{cols} Collision object(s), added to the automatic collision. Mark shell parts 'No auto collision' to replace them.");
    }

    void MakeCollision(bool box)
    {
        var objs = SelObjects().Where(o => o.GtaType != GtaObjectType.Collision).ToList();
        if (objs.Count == 0) { SetStatus("Select the objects to make collision from."); return; }
        Do(box ? "Box Collision" : "Copy as Collision", () =>
        {
            foreach (var o in objs)
            {
                Core.Mesh.PolyMesh mesh;
                Vector3 pos;
                if (box)
                {
                    var (mn, mx) = o.WorldBounds();
                    mesh = Core.Mesh.Primitives.Box(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z);
                    pos = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
                }
                else
                {
                    mesh = o.Mesh.Clone();
                    mesh.Transform(o.World);
                    pos = Vector3.Zero;
                }
                var c = Scene.Add(o.Name + "_col", mesh, pos, GtaObjectType.Collision);
                c.Gta.CollisionMaterial = o.Gta.CollisionMaterial;
                c.WireColour = new Vector3(1f, 0.55f, 0.15f);
            }
        });
        SetStatus($"{objs.Count} collision object(s) created.");
    }

    // ---------------------------------------------------------------- checks page

    void HubChecks()
    {
        if (ImGui.Button("Run checks again", new Vector2(-1, 0))) RunGtaChecks();
        foreach (var r in checkResults) { ImGui.PushTextWrapPos(); Ui.Text(r); ImGui.PopTextWrapPos(); }
    }
}
