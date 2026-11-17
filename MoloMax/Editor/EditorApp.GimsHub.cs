using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// The Molo GTA panel laid out like GIMS Evo: a black window 120 x 160 units, big light title with a back arrow,
/// square steel-blue tiles (icon above the text), section titles, the settings / info strip at the bottom, and one
/// page per task (tools with two tabs, create object by category, progress, report with warnings / errors).
/// Same layout and behaviour, drawn here (no GIMS code or picture is used).
/// </summary>
public sealed partial class EditorApp
{
    // GIMS colours: black window, snow text, slate grey, steel blue tiles, dodger blue on hover / selected, orange warnings
    static readonly Vector4 GBlack = new(0, 0, 0, 1), GWhite = new(1f, 0.98f, 0.98f, 1), GGray = new(0.44f, 0.5f, 0.56f, 1),
        GTheme = new(0.27f, 0.51f, 0.71f, 1), GAccent = new(0.12f, 0.56f, 1f, 1), GWarning = new(1f, 0.65f, 0f, 1), GInfo = new(0f, 0.5f, 0f, 1);
    static uint U(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    readonly Stack<HubPage> hubBack = new();
    int gToolsTab;          // 0 = Molo, 1 = Game (selected objects)
    string gCreateCat;      // selected category on the Create pages
    bool gCloneSelect = true, gResultErrors;

    void HubGo(HubPage p) { if (p == hubPage) return; hubBack.Push(hubPage); hubPage = p; }
    void HubBack() { hubPage = hubBack.Count > 0 ? hubBack.Pop() : HubPage.Home; }

    static string HubTitle(HubPage p, bool sel) => p switch
    {
        HubPage.Home => "Polymorph GTA",
        HubPage.Export => sel ? "Export models" : "Export",
        HubPage.Mlo => "MLO builder",
        HubPage.Object => "Object",
        HubPage.Collision => "Collision",
        HubPage.Checks => "Checks",
        HubPage.Tripo => "Tripo",
        HubPage.Tools => "Tools",
        HubPage.GTools => "Tools",
        HubPage.Anim => "Animation",
        HubPage.Create => "Create",
        HubPage.Settings => "Settings",
        HubPage.Info => "Information",
        HubPage.Results => "Report",
        _ => "Coming",
    };

    void DrawGimsHub()
    {
        if (!HubOpen) return;
        float y0 = menuH + toolbarH;
        float right = gpu.Width - (ShowPanel ? panelW : 0);
        // while a tutorial film records, the panel stays put (a click near the header must not drag it)
        ImGui.SetNextWindowPos(new Vector2(right - 470, y0 + 8), recordDir != null ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(456, MathF.Min(608, gpu.Height - y0 - statusH - 16)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 420), new Vector2(1000, 2000));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GBlack);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, GBlack);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.2f, 0.2f, 0.2f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = HubOpen;
        bool shown = ImGui.Begin("Polymorph GTA###hub", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar();
        if (!shown) { ImGui.End(); ImGui.PopStyleColor(3); HubOpen = open; return; }

        var wp = ImGui.GetWindowPos(); var ws = ImGui.GetWindowSize();
        float u = ws.X / 120f;                       // one GIMS unit (a millimetre at 100 %)
        var dl = ImGui.GetWindowDrawList();
        // ---- header: back arrow, big light title, close
        float headH = 17 * u;
        bool back = hubPage != HubPage.Home;
        float tx = 6 * u;
        if (back)
        {
            ImGui.SetCursorScreenPos(wp + new Vector2(3 * u, 4 * u));
            if (ImGui.InvisibleButton("##hubback", new Vector2(9 * u, 9 * u))) HubBack();
            TutRect("g:back");
            bool hv = ImGui.IsItemHovered();
            var c = wp + new Vector2(7.5f * u, 8.5f * u); float r = 3.2f * u;
            dl.AddCircle(c, r + 0.6f * u, U(hv ? GAccent : GGray), 24, 1.5f);
            dl.AddLine(c + new Vector2(r * 0.55f, 0), c - new Vector2(r * 0.6f, 0), U(GWhite), 2);
            dl.AddLine(c - new Vector2(r * 0.6f, 0), c + new Vector2(-r * 0.05f, -r * 0.55f), U(GWhite), 2);
            dl.AddLine(c - new Vector2(r * 0.6f, 0), c + new Vector2(-r * 0.05f, r * 0.55f), U(GWhite), 2);
            tx = 13 * u;
        }
        ImGui.PushFont(Ui.HeaderFont);
        dl.AddText(wp + new Vector2(tx, 2.2f * u), U(GWhite), HubTitle(hubPage, exportSelectedOnly));
        ImGui.PopFont();
        // close (top right) and drag by the header
        ImGui.SetCursorScreenPos(wp + new Vector2(ws.X - 9 * u, 2 * u));
        if (ImGui.InvisibleButton("##hubclose", new Vector2(7 * u, 7 * u))) open = false;
        { bool hv = ImGui.IsItemHovered(); var c = wp + new Vector2(ws.X - 5.5f * u, 5.5f * u); float r = 1.8f * u; uint col = U(hv ? GAccent : GGray); dl.AddLine(c - new Vector2(r, r), c + new Vector2(r, r), col, 2); dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), col, 2); }
        ImGui.SetCursorScreenPos(wp + new Vector2(tx, 0));
        ImGui.InvisibleButton("##hubdrag", new Vector2(MathF.Max(10, ws.X - tx - 10 * u), headH));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ImGui.SetWindowPos(wp + ImGui.GetIO().MouseDelta);

        // ---- body
        float barH = 9 * u;
        ImGui.PushStyleColor(ImGuiCol.Text, GWhite);
        if (hubPage == HubPage.Home)
        {
            GimsMain(wp, u, dl);
        }
        else
        {
            ImGui.SetCursorScreenPos(wp + new Vector2(4 * u, headH));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2 * u, 1 * u));
            ImGui.BeginChild("##hubpage", new Vector2(ws.X - 8 * u, ws.Y - headH - barH - 2 * u), ImGuiChildFlags.AlwaysUseWindowPadding);
            ImGui.PopStyleVar();
            ImGui.PushStyleColor(ImGuiCol.Button, GTheme); ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GAccent); ImGui.PushStyleColor(ImGuiCol.ButtonActive, GAccent);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.1f, 0.1f, 0.12f, 1));
            switch (hubPage)
            {
                case HubPage.Export: GimsExport(); break;
                case HubPage.Mlo: HubMlo(); break;
                case HubPage.Object: HubObject(); break;
                case HubPage.Collision: HubCollision(); break;
                case HubPage.Checks: HubChecks(); break;
                case HubPage.Tripo: HubTripo(); break;
                case HubPage.Tools: HubTools(); break;
                case HubPage.Anim: HubAnim(); break;
                case HubPage.GTools: GimsTools(u); break;
                case HubPage.Create: GimsCreate(u); break;
                case HubPage.Settings: PrefsGta(); break;
                case HubPage.Info: GimsInfo(); break;
                case HubPage.Results: GimsResults(u); break;
                default: HubComing(); break;
            }
            ImGui.PopStyleColor(4);
            ImGui.EndChild();
        }
        // ---- bottom strip: settings and info, like GIMS' misc bar
        var bp = wp + new Vector2(0, ws.Y - barH);
        dl.AddRectFilled(bp, bp + new Vector2(ws.X, barH), U(new Vector4(0.13f, 0.15f, 0.17f, 1)));
        Ui.TextDisabled("");
        dl.AddText(bp + new Vector2(3 * u, (barH - ImGui.GetFontSize()) * 0.5f), U(GGray), Scene.Gta.ResourceName ?? "");
        if (GimsIconButton("##hubset", bp + new Vector2(ws.X - 17 * u, 1 * u), 7 * u, GimsIcon.Settings)) HubGo(HubPage.Settings);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Settings (GTA & FiveM)");
        if (GimsIconButton("##hubinfo", bp + new Vector2(ws.X - 8 * u, 1 * u), 7 * u, GimsIcon.Info)) HubGo(HubPage.Info);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Information");
        ImGui.PopStyleColor();
        ImGui.End();
        ImGui.PopStyleColor(3);
        HubOpen = open;
    }

    // ---------------------------------------------------------------- main page (GIMS "Main" layout)

    void GimsMain(Vector2 wp, float u, ImDrawListPtr dl)
    {
        bool exporting = exportTask is { IsCompleted: false };
        // first row: the two big actions of GIMS, then checks and the server
        if (GimsTile("Import", wp, u, 6, 19, 26, 26, GimsIcon.Import)) ImportDialog();
        GimsTip("Import a model (GLB, OBJ, ydr, yft, ydd, ybn...)");
        if (GimsTile(exporting ? "Exporting" : "Export", wp, u, 34, 19, 26, 26, GimsIcon.Export)) HubGo(HubPage.Export);
        GimsTip("Export the FiveM resource or the selected models");
        if (GimsTile("Checks", wp, u, 62, 19, 26, 26, GimsIcon.Check)) { HubGo(HubPage.Checks); RunGtaChecks(); }
        GimsTip("Vertex colours, normals, limbo, names, rooms");
        if (GimsTile("Server", wp, u, 90, 19, 26, 26, GimsIcon.Send)) SendToServer();
        GimsTip("Export into your FiveM server and restart the resource");

        GimsSection("Scene", wp, u, 4, 47);
        if (GimsTile("Material editor", wp, u, 6, 55, 54, 26, GimsIcon.Materials)) materialEditorOpen = true;
        GimsTip("GTA shaders: diffuse, normal, specular, parameters");
        if (GimsTile("Tools", wp, u, 62, 55, 26, 26, GimsIcon.Tools)) HubGo(HubPage.GTools);
        GimsTip("Selected objects actions and the building tools (doors, finishing, decals...)");
        if (GimsTile("MLO", wp, u, 90, 55, 26, 26, GimsIcon.Mlo)) HubGo(HubPage.Mlo);
        GimsTip("Rooms, automatic portals, limbo, timecycles");
        if (GimsTile("Create object", wp, u, 6, 83, 54, 9, GimsIcon.None)) { gCreateCat = null; HubGo(HubPage.Create); }
        if (GimsTile("Object properties", wp, u, 62, 83, 54, 9, GimsIcon.None)) HubGo(HubPage.Object);

        GimsSection("Miscellaneous", wp, u, 4, 94);
        if (GimsTile("Browse game models", wp, u, 6, 102, 110, 9, GimsIcon.None)) OpenGameBrowser();
        GimsTip("Every GTA V model of your game by family: preview it, import it with its textures");
        if (GimsTile("Clear cache", wp, u, 6, 113, 26, 26, GimsIcon.Cache)) ClearHubCache();
        GimsTip("Thumbnails, extracted game files and the map mesh cache are rebuilt when needed");
        if (GimsTile("Animation", wp, u, 34, 113, 26, 26, GimsIcon.Play)) HubGo(HubPage.Anim);
        GimsTip("UV and object animations, written as a GTA .ycd");
    }

    void ClearHubCache()
    {
        long freed = 0; int files = 0;
        foreach (var dir in new[] { GameExtractDir, Path.Combine(Path.GetTempPath(), "polymorph_thumbs") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { try { freed += new FileInfo(f).Length; File.Delete(f); files++; } catch { } }
            }
            catch { }
        }
        SetStatus($"Cache cleared: {files} file(s), {freed / 1048576.0:0.0} MB.");
    }

    void GimsTip(string t) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(t); }

    void GimsSection(string text, Vector2 wp, float u, float x, float y)
    {
        var dl = ImGui.GetWindowDrawList();
        ImGui.PushFont(Ui.SubHeaderFont);
        dl.AddText(wp + new Vector2(x * u, y * u - 1.5f * u), U(GWhite), text);
        ImGui.PopFont();
    }

    enum GimsIcon { None, Import, Export, Materials, Tools, Settings, Info, Cache, Check, Send, Mlo, Collision, Play, Cube }

    /// <summary>A GIMS tile: steel blue square, dodger blue under the mouse, icon above the text (or text only for thin ones).</summary>
    bool GimsTile(string text, Vector2 wp, float u, float x, float y, float w, float h, GimsIcon icon)
    {
        var p = wp + new Vector2(x * u, y * u); var size = new Vector2(w * u, h * u);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton("##gt" + text, size); TutRect("g:" + text);
        bool hv = ImGui.IsItemHovered(), act = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, U(hv || act ? GAccent : GTheme));
        ImGui.PushFont(Ui.TileFont);
        var ts = ImGui.CalcTextSize(text);
        if (icon == GimsIcon.None || h < 12)
            dl.AddText(p + (size - ts) * 0.5f, U(GWhite), text);
        else
        {
            float iz = MathF.Min(size.X, size.Y) * 0.42f;
            var ic = p + new Vector2(size.X * 0.5f, size.Y * 0.42f);
            DrawGimsIcon(dl, icon, ic, iz, U(GWhite));
            dl.AddText(p + new Vector2((size.X - ts.X) * 0.5f, size.Y - ts.Y - 1.2f * u), U(GWhite), text);
        }
        ImGui.PopFont();
        return clicked;
    }

    bool GimsIconButton(string id, Vector2 p, float s, GimsIcon icon)
    {
        ImGui.SetCursorScreenPos(p);
        bool c = ImGui.InvisibleButton(id, new Vector2(s, s));
        DrawGimsIcon(ImGui.GetWindowDrawList(), icon, p + new Vector2(s, s) * 0.5f, s * 0.8f, U(ImGui.IsItemHovered() ? GAccent : GWhite));
        return c;
    }

    /// <summary>Simple line icons (our own drawings) at <paramref name="c"/>, about <paramref name="s"/> pixels wide.</summary>
    static void DrawGimsIcon(ImDrawListPtr dl, GimsIcon icon, Vector2 c, float s, uint col)
    {
        float h = s * 0.5f, t = MathF.Max(1.5f, s * 0.07f);
        switch (icon)
        {
            case GimsIcon.Import:
            case GimsIcon.Export:
            {
                // tray + arrow (down into it = import, up out of it = export)
                dl.AddLine(c + new Vector2(-h, h * 0.2f), c + new Vector2(-h, h), col, t);
                dl.AddLine(c + new Vector2(-h, h), c + new Vector2(h, h), col, t);
                dl.AddLine(c + new Vector2(h, h), c + new Vector2(h, h * 0.2f), col, t);
                bool up = icon == GimsIcon.Export;
                var a = c + new Vector2(0, up ? h * 0.55f : -h); var b = c + new Vector2(0, up ? -h : h * 0.55f);
                dl.AddLine(a, b, col, t);
                var tip = b; float d = up ? 1 : -1;
                dl.AddLine(tip, tip + new Vector2(-h * 0.4f, h * 0.4f * d), col, t);
                dl.AddLine(tip, tip + new Vector2(h * 0.4f, h * 0.4f * d), col, t);
                break;
            }
            case GimsIcon.Materials:
                dl.AddCircle(c, h * 0.9f, col, 32, t);
                dl.AddCircleFilled(c + new Vector2(-h * 0.3f, -h * 0.3f), h * 0.18f, col, 16);
                dl.AddBezierCubic(c + new Vector2(-h * 0.9f, h * 0.05f), c + new Vector2(-h * 0.3f, h * 0.45f), c + new Vector2(h * 0.3f, h * 0.45f), c + new Vector2(h * 0.9f, h * 0.05f), col, t * 0.7f);
                break;
            case GimsIcon.Tools:
            {
                // wrench: a handle and a ring with its jaw open
                var a = c + new Vector2(-h * 0.75f, h * 0.75f); var b = c + new Vector2(h * 0.25f, -h * 0.25f);
                dl.AddLine(a, b, col, t * 1.6f);
                dl.PathArcTo(c + new Vector2(h * 0.45f, -h * 0.45f), h * 0.42f, MathF.PI * 0.9f, MathF.PI * 2.6f, 16);
                dl.PathStroke(col, ImDrawFlags.None, t * 1.4f);
                break;
            }
            case GimsIcon.Settings:
            {
                dl.AddCircle(c, h * 0.42f, col, 20, t);
                for (int i = 0; i < 8; i++)
                {
                    float an = i * MathF.PI / 4; var d = new Vector2(MathF.Cos(an), MathF.Sin(an));
                    dl.AddLine(c + d * h * 0.62f, c + d * h * 0.95f, col, t * 1.6f);
                }
                dl.AddCircle(c, h * 0.68f, col, 24, t);
                break;
            }
            case GimsIcon.Info:
                dl.AddCircle(c, h * 0.9f, col, 24, t);
                dl.AddCircleFilled(c + new Vector2(0, -h * 0.42f), t * 0.9f, col);
                dl.AddLine(c + new Vector2(0, -h * 0.12f), c + new Vector2(0, h * 0.5f), col, t * 1.2f);
                break;
            case GimsIcon.Cache:
                // bin
                dl.AddLine(c + new Vector2(-h * 0.75f, -h * 0.55f), c + new Vector2(h * 0.75f, -h * 0.55f), col, t);
                dl.AddLine(c + new Vector2(-h * 0.25f, -h * 0.55f), c + new Vector2(-h * 0.2f, -h * 0.8f), col, t);
                dl.AddLine(c + new Vector2(-h * 0.2f, -h * 0.8f), c + new Vector2(h * 0.2f, -h * 0.8f), col, t);
                dl.AddLine(c + new Vector2(h * 0.2f, -h * 0.8f), c + new Vector2(h * 0.25f, -h * 0.55f), col, t);
                dl.AddQuad(c + new Vector2(-h * 0.6f, -h * 0.45f), c + new Vector2(h * 0.6f, -h * 0.45f), c + new Vector2(h * 0.45f, h * 0.85f), c + new Vector2(-h * 0.45f, h * 0.85f), col, t);
                for (int i = -1; i <= 1; i++) dl.AddLine(c + new Vector2(i * h * 0.22f, -h * 0.25f), c + new Vector2(i * h * 0.18f, h * 0.65f), col, t * 0.8f);
                break;
            case GimsIcon.Check:
                dl.AddLine(c + new Vector2(-h * 0.7f, 0), c + new Vector2(-h * 0.15f, h * 0.55f), col, t * 1.5f);
                dl.AddLine(c + new Vector2(-h * 0.15f, h * 0.55f), c + new Vector2(h * 0.8f, -h * 0.6f), col, t * 1.5f);
                break;
            case GimsIcon.Send:
                dl.AddTriangle(c + new Vector2(-h * 0.85f, -h * 0.1f), c + new Vector2(h * 0.85f, -h * 0.7f), c + new Vector2(h * 0.2f, h * 0.8f), col, t);
                dl.AddLine(c + new Vector2(h * 0.85f, -h * 0.7f), c + new Vector2(-h * 0.1f, h * 0.15f), col, t);
                break;
            case GimsIcon.Mlo:
                dl.AddLine(c + new Vector2(-h * 0.9f, -h * 0.05f), c + new Vector2(0, -h * 0.85f), col, t);
                dl.AddLine(c + new Vector2(0, -h * 0.85f), c + new Vector2(h * 0.9f, -h * 0.05f), col, t);
                dl.AddRect(c + new Vector2(-h * 0.65f, -h * 0.15f), c + new Vector2(h * 0.65f, h * 0.85f), col, 0, ImDrawFlags.None, t);
                dl.AddRect(c + new Vector2(-h * 0.18f, h * 0.3f), c + new Vector2(h * 0.18f, h * 0.85f), col, 0, ImDrawFlags.None, t * 0.8f);
                break;
            case GimsIcon.Collision:
                dl.AddRect(c - new Vector2(h * 0.75f, h * 0.75f), c + new Vector2(h * 0.75f, h * 0.75f), col, 0, ImDrawFlags.None, t);
                for (int i = -1; i <= 1; i++) dl.AddLine(c + new Vector2(-h * 0.75f, i * h * 0.37f), c + new Vector2(h * 0.75f, i * h * 0.37f), col, t * 0.5f);
                for (int i = -1; i <= 1; i++) dl.AddLine(c + new Vector2(i * h * 0.37f, -h * 0.75f), c + new Vector2(i * h * 0.37f, h * 0.75f), col, t * 0.5f);
                break;
            case GimsIcon.Play:
                dl.AddCircle(c, h * 0.9f, col, 24, t);
                dl.AddTriangleFilled(c + new Vector2(-h * 0.3f, -h * 0.45f), c + new Vector2(h * 0.5f, 0), c + new Vector2(-h * 0.3f, h * 0.45f), col);
                break;
            case GimsIcon.Cube:
            {
                var top = c + new Vector2(0, -h * 0.85f); var l = c + new Vector2(-h * 0.8f, -h * 0.4f); var r = c + new Vector2(h * 0.8f, -h * 0.4f);
                var m = c + new Vector2(0, 0.05f * h); var bl = c + new Vector2(-h * 0.8f, h * 0.5f); var br = c + new Vector2(h * 0.8f, h * 0.5f); var b = c + new Vector2(0, h * 0.95f);
                dl.AddQuad(top, r, m, l, col, t); dl.AddLine(l, bl, col, t); dl.AddLine(r, br, col, t); dl.AddLine(m, b, col, t); dl.AddLine(bl, b, col, t); dl.AddLine(br, b, col, t);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- tools page (GIMS: two tabs, GIMS / Game)

    void GimsTools(float u)
    {
        float tw = 26 * u;
        if (Theme.Toggle("Polymorph##gtt", gToolsTab == 0, new Vector2(tw, 9 * u))) gToolsTab = 0;
        ImGui.SameLine(0, 2 * u);
        if (Theme.Toggle("Game##gtt", gToolsTab == 1, new Vector2(tw, 9 * u))) gToolsTab = 1;
        TutRect("g:tab_game");
        ImGui.Spacing();
        if (gToolsTab == 0) { HubTools(); return; }
        float full = ImGui.GetContentRegionAvail().X, half = (full - 2 * u) / 2;
        GimsGroup("Hierarchy");
        if (ImGui.Button("Create object", new Vector2(half, 9 * u))) { gCreateCat = null; HubGo(HubPage.Create); }
        ImGui.SameLine(0, 2 * u);
        if (ImGui.Button("Drawable from selection", new Vector2(half, 9 * u))) CreateDrawable();
        GimsGroup("Selected objects actions");
        if (ImGui.Button("Clone", new Vector2(26 * u, 9 * u))) { var before = Selected.ToHashSet(); CloneSelection(); if (!gCloneSelect) { Selected.Clear(); foreach (var id in before) Selected.Add(id); SelectionChanged(); } }
        ImGui.SameLine(0, 2 * u); ImGui.Checkbox("Select cloned objects", ref gCloneSelect);
        if (ImGui.Button("Collision from selection", new Vector2(full, 9 * u))) CollisionFromSelection();
        bool fixVc = ImGui.Button("Fix vertex colours (green inside, red outside, white emissive)", new Vector2(full, 9 * u)); TutRect("g:fixvc");
        if (fixVc)
        { Do("Fix vertex colours", () => MoloMax.Core.Gta.Check.GtaChecker.FixAll(Scene)); hubIssuesTime = -10; }
        if (ImGui.Button("Clean up (fewer edges, same shape)", new Vector2(full, 9 * u))) CleanUpSelected();
        if (ImGui.Button("Run the checks", new Vector2(full, 9 * u))) { HubGo(HubPage.Checks); RunGtaChecks(); }
        GimsGroup("Pages");
        if (ImGui.Button("Object properties", new Vector2(half, 9 * u))) HubGo(HubPage.Object);
        ImGui.SameLine(0, 2 * u);
        if (ImGui.Button("Collision settings", new Vector2(half, 9 * u))) HubGo(HubPage.Collision);
        if (ImGui.Button("Paint", new Vector2(half, 9 * u))) { propTab = PropTab.Paint; ShowPanel = true; }
        ImGui.SameLine(0, 2 * u);
        if (ImGui.Button("Materials library", new Vector2(half, 9 * u))) OpenLibrary(LibraryAction.Browse);
    }

    void GimsGroup(string t)
    {
        ImGui.Spacing();
        ImGui.PushFont(Ui.TileFont); Ui.TextColored(GGray, t); ImGui.PopFont();
    }

    // ---------------------------------------------------------------- create object (category list, then object list)

    static readonly (string Cat, (string Name, string Tip)[] Items)[] GimsCreateCats =
    {
        ("Model", new[] { ("Drawable from selection", "The selected meshes become one GTA model"), ("Box", "Primitive"), ("Plane", "Primitive"), ("Cylinder", "Primitive"), ("Sphere", "Primitive"), ("Stairs", "Primitive"), ("Door", "MLO door (own pivot)") }),
        ("Collision", new[] { ("Collision from selection", "Collision mesh made from the selected objects"), ("Collision box", "A box collision"), ("Collision settings", "Automatic collision, materials, RoomID, flags") }),
        ("MLO", new[] { ("Room", "A closed room with normals inside"), ("Portal", "A portal between two rooms (or to the outside)"), ("Room builder (floor plan)", "Draw rooms on a plan") }),
        ("Light", new[] { ("Point light", "GTA point light"), ("Spot light", "GTA spot light") }),
    };

    void GimsCreate(float u)
    {
        if (gCreateCat == null)
        {
            ImGui.PushFont(Ui.TileFont); Ui.Text("Select an object category"); ImGui.PopFont();
            ImGui.BeginChild("##gcats", new Vector2(-1, -1), ImGuiChildFlags.Borders);
            foreach (var (cat, _) in GimsCreateCats)
            {
                if (ImGui.Selectable(cat, false, ImGuiSelectableFlags.None, new Vector2(0, 7 * u))) gCreateCat = cat;
                TutRect("gc:" + cat);
            }
            ImGui.EndChild();
            return;
        }
        ImGui.PushFont(Ui.TileFont); Ui.Text("Select an object to create"); ImGui.PopFont();
        if (ImGui.SmallButton("< categories")) { gCreateCat = null; return; }
        ImGui.BeginChild("##gobjs", new Vector2(-1, -1), ImGuiChildFlags.Borders);
        foreach (var (name, tip) in GimsCreateCats.First(c => c.Cat == gCreateCat).Items)
        {
            if (ImGui.Selectable(name, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(0, 7 * u)) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) GimsCreateObject(name);
            TutRect("gc:" + name);
            GimsTip(tip + " (double-click)");
        }
        ImGui.EndChild();
    }

    void GimsCreateObject(string name)
    {
        switch (name)
        {
            case "Drawable from selection": CreateDrawable(); break;
            case "Box": AddPrimitive(CreateKind.Box); break;
            case "Plane": AddPrimitive(CreateKind.Plane); break;
            case "Cylinder": AddPrimitive(CreateKind.Cylinder); break;
            case "Sphere": AddPrimitive(CreateKind.Sphere); break;
            case "Stairs": AddPrimitive(CreateKind.Stairs); break;
            case "Door": AddPrimitive(CreateKind.Door); break;
            case "Collision from selection": CollisionFromSelection(); break;
            case "Collision box": AddPrimitive(CreateKind.CollisionBox); break;
            case "Collision settings": HubGo(HubPage.Collision); break;
            case "Room": AddPrimitive(CreateKind.Room); break;
            case "Portal": AddPrimitive(CreateKind.Portal); break;
            case "Room builder (floor plan)": OpenRoomBuilder(); break;
            case "Point light": AddLight(GtaLightKind.Point); break;
            case "Spot light": AddLight(GtaLightKind.Spot); break;
        }
    }

    // ---------------------------------------------------------------- export: settings, progress, report

    void GimsExport()
    {
        bool busy = exportTask is { IsCompleted: false };
        if (busy)
        {
            // GIMS "Processing..." page: progress bar and the task list
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, GAccent);
            ImGui.ProgressBar((float)((ImGui.GetTime() * 0.35) % 1.0), new Vector2(-1, 6), "");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            Ui.TextWrapped(exportProgress ?? "");
            return;
        }
        if (exportTask != null && exportTask.IsCompleted)
        {
            try { lastReport = exportTask.Result; }
            catch (Exception e) { lastReport = new MoloMax.Core.Gta.Export.ExportReport(); lastReport.Errors.Add(e.InnerException?.Message ?? e.Message); }
            exportTask = null;
            SetStatus(lastReport.Ok ? $"Exported {lastReport.Files.Count} files to {lastReport.ResourceFolder}" : "Export failed, see the report.");
            gResultErrors = !lastReport.Ok;
            HubGo(HubPage.Results);
            return;
        }
        HubExport();
    }

    void GimsResults(float u)
    {
        var r = lastReport;
        if (r == null) { Ui.TextDisabled("No report yet."); return; }
        ImGui.PushFont(Ui.TileFont);
        Ui.TextWrapped(r.Ok ? "The operation's completed" + (r.Warnings.Count > 0 ? " with some messages" : "") : "Something's gone wrong.");
        ImGui.PopFont();
        var list = gResultErrors ? r.Errors : r.Warnings.Concat(r.Info).ToList();
        ImGui.BeginChild("##gres", new Vector2(-1, -11 * u), ImGuiChildFlags.Borders);
        if (list.Count == 0) Ui.TextDisabled(gResultErrors ? "No error." : "No warning.");
        foreach (var l in list) { ImGui.PushTextWrapPos(); Ui.TextColored(gResultErrors ? new Vector4(1f, 0.42f, 0.38f, 1) : r.Warnings.Contains(l) ? GWarning : GWhite, l); ImGui.PopTextWrapPos(); }
        ImGui.EndChild();
        if (ImGui.Button("Copy", new Vector2(26 * u, 9 * u))) ImGui.SetClipboardText(r.ToString());
        ImGui.SameLine(0, 4 * u);
        // warnings / errors switch
        Ui.TextColored(GWarning, $"{r.Warnings.Count} warnings"); ImGui.SameLine();
        if (Theme.Toggle(gResultErrors ? " > ##sw" : " < ##sw", false, new Vector2(11 * u, 0))) gResultErrors = !gResultErrors;
        ImGui.SameLine(); Ui.TextColored(new Vector4(1f, 0.42f, 0.38f, 1), $"{r.Errors.Count} errors");
        if (r.ResourceFolder != null && Directory.Exists(r.ResourceFolder) && ImGui.Button("Open the resource folder", new Vector2(-1, 9 * u)))
            try { System.Diagnostics.Process.Start("explorer.exe", r.ResourceFolder); } catch { }
    }

    void GimsInfo()
    {
        ImGui.PushFont(Ui.TileFont);
        Ui.Text("Polymorph");
        ImGui.PopFont();
        Ui.TextDisabled("Version " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"));
        ImGui.Spacing();
        Ui.TextWrapped("3D modelling for GTA V / FiveM by Molo Modding: models, MLO, collision, textures and animations, exported straight to a FiveM resource.");
        ImGui.Spacing();
        Ui.TextWrapped("This panel follows the layout of GIMS Evo (3Doomer, kirill_mapper), the 3ds Max tool many of us learned with.");
        ImGui.Spacing();
        if (ImGui.Button("Website", new Vector2(-1, 0))) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://molossen.github.io/polymorph/") { UseShellExecute = true }); } catch { }
        if (ImGui.Button("Preferences", new Vector2(-1, 0))) { prefsOpen = true; prefsSection = Array.IndexOf(PrefSections, "GTA & FiveM"); }
    }
}
