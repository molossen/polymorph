using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// The editor: scene, selection, tools, viewports and UI. Split over several partial files
/// (Core = frame loop and drawing, Input = viewport interaction, Commands = undoable edits,
/// Create = interactive primitive creation, and the UI panels).
/// </summary>
public sealed partial class EditorApp : IDisposable
{
    readonly GpuDevice gpu;
    readonly ImGuiBackend imgui;
    readonly Form form;
    readonly SceneRenderer renderer;
    readonly OutlineRenderer outline;
    readonly LineBatch lines;
    public readonly InputState Input = new();

    public Scene Scene = new();
    public UndoStack Undo = new();
    public readonly HashSet<int> Selected = new();
    public SubObject Level = SubObject.Object;
    readonly Dictionary<int, PolySelection> subSel = new();

    public Tool Tool = Tool.Select;
    public bool LocalCoords;
    public bool Snap;
    public float GridStep = 0.1f;
    public float AngleSnap = 5f;

    public readonly Viewport[] Viewports = { new(ViewKind.Top), new(ViewKind.Front), new(ViewKind.Left), new(ViewKind.Perspective) };
    public int ActiveViewport = 3;
    public bool Maximized = true;

    public float Hour = 13f;
    public bool InteriorLight;
    public float InteriorLevel = 0.45f;
    public float Exposure = 1f;
    public float AmbientBoost = 0.12f;
    public bool BackfaceCull = true;
    public bool ShowGrid = true;
    public bool ShowStats = true;

    public string CurrentFile;
    public bool Dirty;
    string status = "Welcome to Polymorph. Create > Standard Primitives or GTA, then click-drag in a viewport.";
    double statusTime;
    readonly Gizmo gizmo = new();
    readonly Dictionary<int, (int Version, EdgeKey[] Edges)> edgeCache = new();
    readonly WireCache wires = new();

    // layout (pixels)
    float menuH, toolbarH = 38, explorerW = 270, panelW = 300, statusH = 46;

    public EditorApp(GpuDevice gpu, ImGuiBackend imgui, Form form)
    {
        this.gpu = gpu; this.imgui = imgui; this.form = form;
        renderer = new SceneRenderer(gpu.Device);
        outline = new OutlineRenderer(gpu.Device);
        lines = new LineBatch(gpu.Device);
        // Classic is only a look (colours + 4 views): the keys stay Standard or Industry Compatible
        if (Settings.Keymap == Keymap.Max) Settings.Keymap = Keymap.Blender;
        Settings.KeymapChosen = true; Settings.ModeChosen = true;
        Theme.Apply(Settings.Mode);
        Maximized = Settings.Mode == UiMode.Blender;
        Viewports[3].Camera.Frame(new Vector3(-4, -4, 0), new Vector3(4, 4, 3));
        foreach (var v in Viewports.Take(3)) v.Camera.OrthoHeight = 16;
        UpdateTitle();
        StartApi();
    }

    /// <summary>The active object (last clicked): the one edit mode works on.</summary>
    public int ActiveId = -1;

    public SceneObject Current
    {
        get
        {
            var a = Scene.Find(ActiveId);
            if (a != null && Selected.Contains(a.Id)) return a;
            return Selected.Count == 1 ? Scene.Find(Selected.First()) : null;
        }
    }

    public PolySelection SubSel(SceneObject o)
    {
        if (o == null) return new PolySelection();
        if (!subSel.TryGetValue(o.Id, out var s)) subSel[o.Id] = s = new PolySelection();
        return s;
    }

    public void SetStatus(string s) { status = s; statusTime = ImGui.GetTime(); }

    public GtaGlobalLights Lights => InteriorLight ? GtaLighting.Interior(InteriorLevel) : GtaLighting.TimeOfDay(Hour, InteriorLevel);

    void UpdateTitle()
    {
        var name = CurrentFile != null ? Path.GetFileName(CurrentFile) : "Untitled";
        form.Text = $"{name}{(Dirty ? " *" : "")} - Polymorph";
    }

    // ---------------------------------------------------------------- frame

    /// <summary>One frame. Returns false when the window is occluded.</summary>
    public bool Frame(float dt)
    {
        if (recordDir != null) dt = 1f / 30f; // recording a video: every frame is 1/30 s of the film, however long the capture takes
        Input.BeginFrame();
        imgui.NewFrame(gpu.Width, gpu.Height, dt);

        try
        {
            ProfStart();
            renderer.Textures.FrameBudgetMs = MapMode ? 4 : 0; // map: textures read in the background, a few per frame
            renderer.Textures.BeginFrame();
            DrawMainMenu();
            DrawToolbar();
            Prof("menus");
            DrawSceneExplorer();
            Prof("outliner");
            DrawPropertiesEditor();
            Prof("properties");
            DrawSplitters();
            DrawStatusBar();
            DrawTimeline();
            DrawYcdWindow();
            DrawMaterialEditor();
            DrawDialogs();
            DrawModelExport();
            DrawMapPlacementDialog();
            DrawNodeEditor();
            DrawPopups();
            if (cubeProj != null) DrawCubeProjectionPanel(); else if (lastMeshOp != null) DrawMeshOpPanel(); else DrawAdjustPanel();
            DrawImportPanel();
            PumpTripoJobs();
            DrawHub();
            LayoutViewports();
            DrawViewportLabels();
            LoadAddons();
            DrawLibraryWindow();
            DrawPreferences();
            DrawSearch();
            DrawPaintShelf();
            DrawPaintToolStrip();
            DrawSculptPanel();
            Prof("windows");
            MaterialDropCheck(); // a material dragged from the Material Editor, released over a viewport
            HandleShortcuts();
            AnimFrameUpdate(dt);
            WalkCamera(dt);
            HandleViewportInput();
            Prof("input");
            MoloMax.Core.Gta.Library.TextureReader.BitmapLoader ??= LoadImageRgba;
            MoloMax.Core.Paint.BrushTips.PackFolder ??= Path.Combine(AppContext.BaseDirectory, "Assets", "paint", "alphas");
            PaintFrame();
            AutosaveFrame();
            DrawChecker();
            PumpImports();
            DrawGameBrowser();
            DrawOpenBrowser();
            DrawAssetBrowser();
            AssetDropOnView();
            DrawUvEditor();
            DrawRoomBuilder();
            PumpSend();
            DrawBridgeHelp();
            DrawScriptConsole();
            Prof("tools");
            SelfTestTick(dt);
            inApi = true;
            try { Api.Pump(); }    // scripting / MCP calls, between two frames
            finally { inApi = false; }
        }
        catch (Exception e)
        {
            // keep the ImGui frame alive (its error recovery closes any window left open) and report
            SetStatus("Error: " + e.Message);
            frameError = e;
        }
        TrackSelection();      // selection changes are steps for Ctrl+Z
        UpdateTick();
        if (!updateStarted) { updateStarted = true; AutoCheckUpdates(); StartupAddons(); }
        ApplyViewLens();       // focal length, clip start / end
        ModEditGuard();        // an Edit Poly / Unwrap edit ends when its object leaves edit mode
        SkinTick();            // skinned meshes follow their bones
        // an edit on one instance reaches the others before drawing (MAP mode: the game models share one mesh already;
        // checking tens of thousands of streamed entities each frame costs more than it brings, every 10th frame is enough)
        if (!MapMode || ++syncTick % 10 == 0) Scene.SyncInstances();
        Prof("sync");

        var ctx = gpu.Context;
        PumpPreview();
        MapFrame();          // MAP mode: streaming, model loads
        Prof("map");
        RenderPreview(dt);   // game files viewer picture
        PumpThumbs();        // asset browser thumbnails, one at a time
        PrepareLighting();   // scene lights + sun shadow map for the realistic view
        Prof("lighting");
        gpu.BindBackBuffer();
        ctx.ClearRenderTargetView(gpu.BackBufferView, new SharpDX.Mathematics.Interop.RawColor4(0.27f, 0.27f, 0.27f, 1));
        renderer.UploadBudgetMs = MapMode ? 12 : 0; renderer.BeginFrameUploads();
        PumpSplats(); splats?.BeginFrame();
        gpuTimer ??= new MoloMax.Rendering.GpuTimer(gpu.Device);
        gpuTimer.Begin(ctx);
        for (int i = 0; i < Viewports.Length; i++)
        {
            if (Maximized && i != ActiveViewport) continue;
            RenderViewport(Viewports[i], i == ActiveViewport);
        }
        gpuTimer.End(ctx);
        Prof("viewports");
        ctx.Rasterizer.SetScissorRectangle(0, 0, gpu.Width, gpu.Height);
        try { DrawOverlays(); } catch (Exception e) { SetStatus("Error: " + e.Message); frameError = e; }
        Prof("overlays");
        imgui.Render();
        Prof("imgui");
        SelfTestAfterRender();
        if (apiShotPath != null) { try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(apiShotPath))); gpu.Capture(apiShotPath); } catch (Exception e) { SetStatus("Screenshot failed: " + e.Message); } apiShotPath = null; }
        bool visible = gpu.Present(true);
        Prof("present");
        // freeing GPU meshes of deleted objects: when the object count changed, else a few times a second (it scans every object)
        if (collectTick % (MapMode ? 30 : 10) == 0 || Scene.Objects.Count != collectCount && !MapMode) { renderer.Collect(Scene); collectCount = Scene.Objects.Count; }
        if (++collectTick % (MapMode ? 300 : 30) == 0) wires.Collect(Scene.Objects.Select(o => o.Id).ToHashSet());
        if (collectTick % 20 == 10 && !ModalActive && !painting && !transforming) Undo.Prefreeze(Scene); // the next undo step already copied (big imports)
        UpdateTitle();
        Prof("collect");
        if (frameError != null) { var e = frameError; frameError = null; throw new InvalidOperationException(e.Message, e); }
        return visible;
    }
    Exception frameError;
    /// <summary>A scripting / MCP call is running: errors go back to the caller, no message box (it would block the API).</summary>
    bool inApi;
    /// <summary>Graphics card time of the 3D views (profiling).</summary>
    MoloMax.Rendering.GpuTimer gpuTimer;
    public double GpuViewMs => gpuTimer?.Ms ?? 0;
    int collectTick, collectCount = -1;

    // ---- frame profile (milliseconds per section, smoothed): self tests and the stats overlay read it
    readonly System.Diagnostics.Stopwatch profSw = new();
    public readonly Dictionary<string, double> FrameProfile = new();
    double profUp0, profTex0, profGatherMs; TimeSpan profGc0;
    void ProfStart() { profSw.Restart(); profGatherMs = 0; profGc0 = GC.GetTotalPauseDuration(); profUp0 = renderer.UploadMs; profTex0 = renderer.Textures.LoadMs; }
    int syncTick;

    void Prof(string section)
    {
        double ms = profSw.Elapsed.TotalMilliseconds; profSw.Restart();
        FrameProfile[section] = FrameProfile.TryGetValue(section, out var old) ? old * 0.9 + ms * 0.1 : ms;
        profCur[section] = ms;
        if (section == "collect")
        {
            double t = profCur.Values.Sum(); LastFrameCpuMs = t - profCur.GetValueOrDefault("present");
            if (t > WorstFrameMs) { WorstFrameMs = t; worstFrame = profCur.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value:0.0}").ToList(); worstFrame.Add($"(of which mesh uploads {renderer.UploadMs - profUp0:0.0}, textures {renderer.Textures.LoadMs - profTex0:0.0}, map culling {profGatherMs:0.0}, GC pause {(GC.GetTotalPauseDuration() - profGc0).TotalMilliseconds:0.0})"); }
            profCur.Clear();
        }
    }
    readonly Dictionary<string, double> profCur = new();
    List<string> worstFrame = new();
    /// <summary>Slowest frame since the last reset and where its time went (self tests).</summary>
    public double WorstFrameMs;
    /// <summary>Processor time of the last frame (all sections, without waiting for the screen).</summary>
    public double LastFrameCpuMs;
    public void ResetWorstFrame() { WorstFrameMs = 0; worstFrame = new(); }
    public string WorstFrameText() => $"slowest frame {WorstFrameMs:0} ms: " + string.Join(", ", worstFrame);
    public string ProfileText(int n = 6) => string.Join(", ", FrameProfile.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Key} {kv.Value:0.0}")) + $"  [graphics card: views {GpuViewMs:0.0}]";

    /// <summary>Height of the Blender-mode outliner at the top of the right column.</summary>
    float OutlinerH => ShowPanel ? MathF.Floor((gpu.Height - menuH - toolbarH - statusH) * 0.32f) : gpu.Height - menuH - toolbarH - statusH;

    void LayoutViewports()
    {
        // 3ds Max: explorer left, command panel right. Blender: outliner above the properties, both on the right.
        // outliner on the left, properties / modifiers on the right (both keymaps)
        float x0 = ShowExplorer ? explorerW : 0, y0 = menuH + toolbarH;
        float x1 = gpu.Width - (ShowPanel ? panelW : 0), y1 = gpu.Height - statusH - (TimelineOpen ? TimelineH : 0);
        // Molo Paint: the shelf (materials, decals, smart materials) docks as a column at the right of the viewport
        if (PaintShelfVisible) { x1 -= ShelfW; shelfRect = (x1, y0, ShelfW, y1 - y0); }
        float w = MathF.Max(x1 - x0, 50), h = MathF.Max(y1 - y0, 50);
        if (Maximized)
        {
            var v = Viewports[ActiveViewport];
            v.X = x0; v.Y = y0; v.W = w; v.H = h;
            return;
        }
        float hw = MathF.Floor(w / 2), hh = MathF.Floor(h / 2);
        for (int i = 0; i < 4; i++)
        {
            var v = Viewports[i];
            v.X = x0 + (i % 2) * (hw + 2); v.Y = y0 + (i / 2) * (hh + 2);
            v.W = hw - 2; v.H = hh - 2;
        }
    }

    // ---------------------------------------------------------------- 3D drawing

    EdgeKey[] Edges(SceneObject o)
    {
        if (edgeCache.TryGetValue(o.Id, out var e) && e.Version == o.Version) return e.Edges;
        var arr = o.Mesh.BuildEdgeMap().Keys.ToArray();
        edgeCache[o.Id] = (o.Version, arr);
        return arr;
    }

    readonly Dictionary<int, (int Version, EdgeKey[] Edges)> evalEdgeCache = new();
    /// <summary>Edges of the mesh after modifiers (object-mode wire and outline).</summary>
    EdgeKey[] EvalEdges(SceneObject o)
    {
        if (o.Modifiers == null || o.Modifiers.Count == 0) return Edges(o);
        if (evalEdgeCache.TryGetValue(o.Id, out var e) && e.Version == o.Version) return e.Edges;
        var arr = o.Evaluated().BuildEdgeMap().Keys.ToArray();
        evalEdgeCache[o.Id] = (o.Version, arr);
        return arr;
    }

    static Vector4 V4(Vector3 c, float a = 1) => new(c, a);

    void RenderViewport(Viewport vp, bool active)
    {
        var ctx = gpu.Context;
        ctx.Rasterizer.SetViewport(vp.X, vp.Y, vp.W, vp.H);
        ctx.Rasterizer.SetScissorRectangle((int)vp.X, (int)vp.Y, (int)(vp.X + vp.W), (int)(vp.Y + vp.H));
        ctx.ClearDepthStencilView(gpu.DepthView, SharpDX.Direct3D11.DepthStencilClearFlags.Depth, 1f, 0);
        var cam = vp.Camera;
        drawCam = cam; drawViewport = vp;
        var viewProj = cam.ViewProj(vp.Aspect);
        lines.SetView(cam.Ortho, cam.Near, cam.Far, cam.OrthoHeight);
        var blendCol = new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0);

        // background (3ds Max 2016: gradient in perspective, flat grey in orthographic views)
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthOff);
        ctx.OutputMerger.SetBlendState(renderer.Opaque, blendCol, -1);
        ctx.Rasterizer.State = renderer.CullNone;
        var (top, bot) = Theme.ViewportBackground(cam.Ortho);
        lines.Tris.Add(new LineVertex(new(-1, 1, 0.5f), top)); lines.Tris.Add(new LineVertex(new(1, 1, 0.5f), top)); lines.Tris.Add(new LineVertex(new(1, -1, 0.5f), bot));
        lines.Tris.Add(new LineVertex(new(-1, 1, 0.5f), top)); lines.Tris.Add(new LineVertex(new(1, -1, 0.5f), bot)); lines.Tris.Add(new LineVertex(new(-1, -1, 0.5f), bot));
        lines.Flush(Matrix4x4.Identity);

        // home grid
        if (ShowGrid)
        {
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
            ctx.Rasterizer.State = renderer.LinesState;
            DrawGrid(vp);
            lines.Flush(viewProj);
        }

        // geometry with the GTA shader
        var l = ScaledLights(Lights);
        var cur = Current;
        renderer.EditObjectId = Level != SubObject.Object && cur != null ? cur.Id : -1;
        renderer.BeginScene(viewProj, cam.Eye, l, vp.Shading, Exposure, AmbientBoost);
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthDefault);
        ctx.OutputMerger.SetBlendState(renderer.Opaque, blendCol, -1);
        if (MapMode && !wireOnly(vp)) { long g0 = System.Diagnostics.Stopwatch.GetTimestamp(); MapGatherDraws(vp, viewProj); profGatherMs += System.Diagnostics.Stopwatch.GetElapsedTime(g0).TotalMilliseconds; }
        // what the camera can see (objects outside the view are not sent to the graphics card), worked out once for both passes
        drawList.Clear();
        if (!(MapMode && !wireOnly(vp)) && !wireOnly(vp))
        {
            var planes = FrustumPlanes(viewProj);
            foreach (var o in Scene.Objects)
            {
                if (o.Hidden || IsVolumeOnly(o)) continue;
                if (o.SplatFile != null) { if (o.SplatCloud != null) splatDraws.Add(o); continue; } // scans: drawn after the transparent pass
                if (o.Modifiers is not { Count: > 0 } && o.Mesh != null && o.Mesh.Verts.Count > 0)
                {
                    var (bmn, bmx) = o.FastWorldBounds();
                    if (!BoxVisible(planes, bmn - new Vector3(0.01f), bmx + new Vector3(0.01f))) continue;
                }
                drawList.Add(o);
            }
        }
        if (MapMode && !wireOnly(vp)) MapDraw(false);
        else foreach (var o in drawList)
        {
            var hl = o.Frozen ? new Vector4(0.55f, 0.55f, 0.6f, 0.6f) : Vector4.Zero;
            renderer.DrawObject(Scene, o, false, hl, BackfaceCull);
        }
        ctx.OutputMerger.SetBlendState(renderer.Alpha, blendCol, -1);
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
        if (MapMode && !wireOnly(vp)) MapDraw(true);
        else foreach (var o in drawList)
        {
            renderer.DrawObject(Scene, o, true, Vector4.Zero, BackfaceCull);
        }
        DrawSplats(vp);

        // wires, edges, selection, brackets, creation preview
        ctx.Rasterizer.State = renderer.LinesState;
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
        // a selected Drawable node shows its game meshes as selected
        HashSet<int> nodeKids = null;
        foreach (var id in Selected)
            if (Scene.Find(id) is { GtaType: GtaObjectType.Model } node) { nodeKids ??= new(); foreach (var d in Scene.Descendants(node)) nodeKids.Add(d.Id); }
        // a very heavy selection (millions of points) gets its corner brackets only: drawing every selected mesh twice more
        // for the silhouette, in every view, made the frame crawl
        long selPts = 0;
        foreach (var id in Selected) if (Scene.Find(id) is { } so) selPts += so.Mesh?.Verts.Count ?? 0;
        if (nodeKids != null) foreach (var id in nodeKids) if (Scene.Find(id) is { } ko) selPts += ko.Mesh?.Verts.Count ?? 0;
        bool outlineSel = selPts <= 1_500_000;
        foreach (var o in MapMode ? MapLive() : Scene.Objects) // MAP: what is within draw distance (selected objects always are)
        {
            if (o.Hidden) continue;
            if (o.GtaType == GtaObjectType.Model) { DrawableMarker(o, Selected.Contains(o.Id)); continue; }
            if (o.GtaType == GtaObjectType.Light) { LightGizmo(o, Selected.Contains(o.Id) || nodeKids != null && nodeKids.Contains(o.Id)); continue; }
            bool sel = Selected.Contains(o.Id) || nodeKids != null && nodeKids.Contains(o.Id);
            if (MapMode && !sel) continue; // thousands of map entities: outlines / wires only for the selection
            bool editing = Level != SubObject.Object && o == cur;
            // object mode: a selected object gets an outline (its silhouette), not all its edges; Edged Faces (F4) shows the edges
            Vector4 selCol = o.Id == ActiveId ? Theme.ActiveWire : Theme.SelectWire;
            if (sel && outlineSel && !editing && !wireOnly(vp) && !vp.EdgedFaces && o.Mesh != null && o.SplatFile == null)
            {
                var obj = o;
                outlineItems.Add(new OutlineRenderer.Item(o.Id, o.Version, o.World, () => obj.Evaluated(), selCol));
            }
            if (wireOnly(vp) || vp.EdgedFaces || editing || IsVolumeOnly(o))
            {
                Vector4 col = editing ? new Vector4(0.8f, 0.8f, 0.8f, 1) : sel ? selCol : wireOnly(vp) || IsVolumeOnly(o) ? V4(o.WireColour) : V4(o.WireColour * 0.55f);
                // wire buffer cached on the GPU per mesh version, drawn with the object's matrix
                var obj = o; bool ed = editing;
                var (vb, count) = wires.Get(lines.Device, o.Id, ed, o.Version, () => ed ? (obj.Mesh.Verts, obj.Mesh.DrawEdges()) : (obj.Evaluated().Verts, obj.Evaluated().DrawEdges()));
                lines.DrawStatic(vb, count, o.World * viewProj, col, 0.0004f);
            }
            if (Level == SubObject.Object && o.Mesh != null && Selected.Contains(o.Id)) { var (bmn, bmx) = o.LocalBounds(); bracketBoxes.Add((o.World, bmn, bmx)); }
        }
        // a selected Drawable node: one box around all its meshes
        if (Level == SubObject.Object)
            foreach (var id in Selected)
                if (Scene.Find(id) is { GtaType: GtaObjectType.Model } node)
                {
                    var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                    foreach (var d in Scene.Descendants(node))
                    {
                        if (d.Mesh == null || d.Hidden || d.Mesh.Verts.Count == 0) continue;
                        var (a0, b0) = d.LocalBounds(); // cached per mesh version (Mesh.Bounds walked every point, every frame, every view)
                        for (int c = 0; c < 8; c++)
                        {
                            var wp = Vector3.Transform(new Vector3((c & 1) != 0 ? b0.X : a0.X, (c & 2) != 0 ? b0.Y : a0.Y, (c & 4) != 0 ? b0.Z : a0.Z), d.World);
                            mn = Vector3.Min(mn, wp); mx = Vector3.Max(mx, wp);
                        }
                    }
                    if (mn.X <= mx.X) bracketBoxes.Add((Matrix4x4.Identity, mn, mx));
                }
        if (cur != null && !cur.Hidden && Level != SubObject.Object) { SubObjectHighlightsGpu(cur, viewProj); DrawModifierSeams(cur); }
        // object mode: a clean outline around the selection, no inner edges (Blender style)
        if (outlineItems.Count > 0)
        {
            lines.Flush(viewProj, 0.0004f);
            outline.Draw(gpu.DepthView, outlineItems, viewProj, vp.W, vp.H, SelectionOutlinePx);
            outlineItems.Clear();
            ctx.Rasterizer.State = renderer.LinesState;
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
            ctx.OutputMerger.SetBlendState(renderer.Alpha, blendCol, -1);
        }
        // selection brackets on top of everything, so they show even from inside a room
        if (bracketBoxes.Count > 0)
        {
            lines.Flush(viewProj, 0.0004f);
            foreach (var (bw, bmn, bmx) in bracketBoxes) Brackets(bw, bmn, bmx);
            bracketsDrawn = bracketBoxes.Count;
            bracketBoxes.Clear();
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthOff);
            lines.Flush(viewProj, 0f);
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
        }
        ShapeLines();
        ModalPreviewLines();
        PaintCursorLines();
        SculptCursorLines(); SnapMarkerLines(); ShelfDropPreviewLines();
        lines.Flush(viewProj, 0.0004f);
    }

    static bool wireOnly(Viewport vp) => vp.WireframeOnly;
    /// <summary>An MLO room volume read from a .ytyp: a wire box only (unless it is being edited).</summary>
    bool IsVolumeOnly(SceneObject o) => o.Gta is { BoundsOnly: true } && !(Level != SubObject.Object && o == Current);
    readonly List<OutlineRenderer.Item> outlineItems = new();
    /// <summary>Width of the object-mode selection outline, in pixels.</summary>
    public float SelectionOutlinePx = 2f;

    // ---- selection outline: edges between a face turned to the camera and one turned away, plus open borders
    sealed class SilEdges { public int Version; public Vector3[] A, B, N1, N2; public bool[] Border; }
    readonly Dictionary<int, SilEdges> silCache = new();

    void Silhouette(SceneObject o, Viewport vp, Vector4 col)
    {
        if (!silCache.TryGetValue(o.Id, out var se) || se.Version != o.Version)
        {
            var m = o.Evaluated();
            var faceN = new Vector3[m.Faces.Count];
            for (int i = 0; i < faceN.Length; i++) faceN[i] = m.FaceNormal(i);
            var map = new Dictionary<EdgeKey, (int F1, int F2)>();
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi];
                for (int i = 0; i < f.V.Length; i++)
                {
                    var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]);
                    map[e] = map.TryGetValue(e, out var x) ? (x.F1, fi) : (fi, -1);
                }
            }
            int n = map.Count, k = 0;
            se = new SilEdges { Version = o.Version, A = new Vector3[n], B = new Vector3[n], N1 = new Vector3[n], N2 = new Vector3[n], Border = new bool[n] };
            foreach (var (e, (f1, f2)) in map)
            {
                se.A[k] = m.Verts[e.A]; se.B[k] = m.Verts[e.B]; se.N1[k] = faceN[f1];
                se.Border[k] = f2 < 0; se.N2[k] = f2 < 0 ? faceN[f1] : faceN[f2]; k++;
            }
            silCache[o.Id] = se;
            if (silCache.Count > 64) foreach (var id in silCache.Keys.Where(id => id != o.Id && !Selected.Contains(id)).ToList()) silCache.Remove(id);
        }
        var w = o.World;
        Matrix4x4.Invert(w, out var inv);
        var cam = vp.Camera;
        bool ortho = cam.Ortho;
        var eye = Vector3.Transform(cam.Eye, inv);
        var dir = Vector3.Normalize(Vector3.TransformNormal(-cam.Forward, inv));
        for (int i = 0; i < se.A.Length; i++)
        {
            bool show = se.Border[i];
            if (!show)
            {
                var toEye = ortho ? dir : eye - se.A[i];
                show = Vector3.Dot(se.N1[i], toEye) > 0 != Vector3.Dot(se.N2[i], toEye) > 0;
            }
            if (show) lines.Line(Vector3.Transform(se.A[i], w), Vector3.Transform(se.B[i], w), col);
        }
    }

    /// <summary>Light: a small star, its range as three circles when selected, the cone for spots.</summary>
    void LightGizmo(SceneObject o, bool sel)
    {
        var L = o.Gta.Light ?? new GtaLight();
        var w = o.World; var p = Vector3.Transform(Vector3.Zero, w);
        var col = sel ? Theme.ActiveWire : new Vector4(L.EffectiveColour, 1);
        float r = 0.15f;
        foreach (var d in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new Vector3(1, 1, 1)), Vector3.Normalize(new Vector3(1, -1, 1)), Vector3.Normalize(new Vector3(-1, 1, 1)), Vector3.Normalize(new Vector3(-1, -1, 1)) })
            lines.Line(p - d * r, p + d * r, col);
        if (L.Kind == GtaLightKind.Spot)
        {
            var dir = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, w));
            Triangulator.Basis(dir, out var u, out var v);
            float len = sel ? L.Range : MathF.Min(L.Range, 1.5f), rad = len * MathF.Tan(L.ConeOuter * MathF.PI / 180f);
            Vector3 prev = default;
            for (int i = 0; i <= 24; i++)
            {
                float a = i / 24f * MathF.PI * 2;
                var q = p + dir * len + (u * MathF.Cos(a) + v * MathF.Sin(a)) * rad;
                if (i > 0) lines.Line(prev, q, col * new Vector4(1, 1, 1, 0.7f));
                if (i % 6 == 0) lines.Line(p, q, col * new Vector4(1, 1, 1, 0.7f));
                prev = q;
            }
        }
        else if (L.Kind == GtaLightKind.Capsule)
        {
            var dir = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, w));
            var half = dir * (L.CapsuleLength * 0.5f * MathF.Max(o.Scale.X, MathF.Max(o.Scale.Y, o.Scale.Z)));
            lines.Line(p - half, p + half, col);
            Triangulator.Basis(dir, out var u, out var v);
            foreach (var e in new[] { p - half, p + half }) for (int i = 0; i < 16; i++)
            {
                float a0 = i / 16f * MathF.PI * 2, a1 = (i + 1) / 16f * MathF.PI * 2;
                lines.Line(e + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * r, e + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * r, col);
            }
        }
        if (sel && L.Kind != GtaLightKind.Spot)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 prev = default;
                for (int i = 0; i <= 32; i++)
                {
                    float a = i / 32f * MathF.PI * 2; var cs = new Vector2(MathF.Cos(a), MathF.Sin(a)) * L.Range;
                    var q = p + (axis == 0 ? new Vector3(cs.X, cs.Y, 0) : axis == 1 ? new Vector3(cs.X, 0, cs.Y) : new Vector3(0, cs.X, cs.Y));
                    if (i > 0) lines.Line(prev, q, col * new Vector4(1, 1, 1, 0.45f));
                    prev = q;
                }
            }
        }
    }

    /// <summary>GIMS-style marker for a Drawable node: a small right-angle triangle with axes at its pivot.</summary>
    void DrawableMarker(SceneObject o, bool sel)
    {
        var w = o.World; float s = 0.25f;
        var col = sel ? Theme.ActiveWire : new Vector4(0.93f, 0.76f, 0.32f, 1);
        var p0 = Vector3.Transform(Vector3.Zero, w);
        var px = Vector3.Transform(new Vector3(s, 0, 0), w); var pz = Vector3.Transform(new Vector3(0, 0, s), w); var py = Vector3.Transform(new Vector3(0, s, 0), w);
        lines.Line(p0, px, col); lines.Line(p0, pz, col); lines.Line(px, pz, col); lines.Line(p0, py, col * new Vector4(1, 1, 1, 0.6f));
    }

    readonly List<SceneObject> drawList = new();

    void DrawGrid(Viewport vp)
    {
        var cam = vp.Camera;
        float span = cam.Ortho ? cam.OrthoHeight * vp.Aspect : cam.Distance * 2.5f;
        float step = 1f;
        while (span / step > 120) step *= 10;
        while (span / step < 12 && step > 0.01f) step /= 10;
        int n = (int)MathF.Ceiling(span / step);
        var c = cam.Target;
        float cx = MathF.Round(c.X / step) * step, cy = MathF.Round(c.Y / step) * step;
        // orthographic side views draw their grid in their own plane, like Max's grid per viewport
        int plane = cam.Ortho ? (cam.Kind is ViewKind.Front or ViewKind.Back ? 1 : cam.Kind is ViewKind.Left or ViewKind.Right ? 0 : 2) : 2;
        Vector3 P(float a, float b) => plane switch { 0 => new Vector3(0, a, b), 1 => new Vector3(a, 0, b), _ => new Vector3(a, b, 0) };
        float ca = plane == 0 ? c.Y : c.X, cb = plane == 2 ? c.Y : c.Z;
        ca = MathF.Round(ca / step) * step; cb = MathF.Round(cb / step) * step;
        var minor = new Vector4(0.33f, 0.33f, 0.33f, 1); var major = new Vector4(0.22f, 0.22f, 0.22f, 1); var axis = new Vector4(0.05f, 0.05f, 0.05f, 1);
        for (int i = -n; i <= n; i++)
        {
            float a = ca + i * step, b = cb + i * step;
            var colA = MathF.Abs(a) < step * 0.01f ? axis : (MathF.Round(a / step) % 10 == 0 ? major : minor);
            var colB = MathF.Abs(b) < step * 0.01f ? axis : (MathF.Round(b / step) % 10 == 0 ? major : minor);
            lines.Line(P(a, cb - n * step), P(a, cb + n * step), colA);
            lines.Line(P(ca - n * step, b), P(ca + n * step, b), colB);
        }
    }

    readonly List<(Matrix4x4 W, Vector3 Min, Vector3 Max)> bracketBoxes = new();
    int bracketsDrawn;
    void Brackets(Matrix4x4 w, Vector3 mn, Vector3 mx)
    {
        var white = Vector4.One;
        var size = mx - mn;
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    var p = new Vector3(x == 1 ? mx.X : mn.X, y == 1 ? mx.Y : mn.Y, z == 1 ? mx.Z : mn.Z);
                    var wp = Vector3.Transform(p, w);
                    lines.Line(wp, Vector3.Transform(p + new Vector3((x == 1 ? -1 : 1) * size.X * 0.2f, 0, 0), w), white);
                    lines.Line(wp, Vector3.Transform(p + new Vector3(0, (y == 1 ? -1 : 1) * size.Y * 0.2f, 0), w), white);
                    lines.Line(wp, Vector3.Transform(p + new Vector3(0, 0, (z == 1 ? -1 : 1) * size.Z * 0.2f), w), white);
                }
    }

    // ---------------------------------------------------------------- screen-space overlays

    void DrawOverlays()
    {
        var dl = ImGui.GetBackgroundDrawList();
        for (int i = 0; i < Viewports.Length; i++)
        {
            if (Maximized && i != ActiveViewport) continue;
            var vp = Viewports[i];
            var min = new Vector2(vp.X, vp.Y); var max = new Vector2(vp.X + vp.W, vp.Y + vp.H);
            dl.PushClipRect(min, max, true);
            var cur = Current;
            if (cur != null && Level == SubObject.Vertex) VertexTicks(dl, vp, cur);
            Prof("ov begin");
            if (ShowStats && i == ActiveViewport) Statistics(dl, vp);
            Prof("ov stats");
            ViewCube.Draw(dl, vp);
            var layout = GizmoLayout(vp);
            DrawCage(dl, vp);
            if (Tool != Tool.Select && layout.Valid && !ModalActive) gizmo.Draw(dl, vp, layout, Tool);
            Prof("ov gizmo");
            SnapDotsOverlay(dl, vp);
            ForgeOverlay(dl, vp);
            FlyOverlay(dl, vp);
            Prof("ov snap");
            CursorOverlay(dl, vp);
            HubOverlay(dl, vp);
            if (boxSelecting && boxViewport == vp)
            {
                var a = Vector2.Min(boxStart, Input.Mouse); var b = Vector2.Max(boxStart, Input.Mouse);
                dl.AddRect(a, b, 0xFFFFFFFF, 0, ImDrawFlags.None, 1f);
            }
            ModalOverlay(dl, vp);
            dl.PopClipRect();
            if (i == ActiveViewport) TutorialOverlay();
            uint border = i == ActiveViewport ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.25f, 1)) : 0xFF202020;
            dl.AddRect(min - Vector2.One, max + Vector2.One, border, 0, ImDrawFlags.None, i == ActiveViewport ? 2f : 1f);
        }
    }


    /// <summary>How many vertex ticks the last frame drew (self tests).</summary>
    int vertexTicksDrawn;

    void VertexTicks(ImDrawListPtr dl, Viewport vp, SceneObject o)
    {
        var s = SubSel(o);
        var blue = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.35f, 1f, 1));
        var red = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.1f, 0.1f, 1));
        // screen positions cached per camera / mesh change (shared with picking); big meshes: only the selected
        // points and the ones around the mouse get a tick
        var sc = ScreenVerts(vp, o); var P = sc.P; var Ok = sc.Ok;
        Prof("ov ticks project");
        var org = new Vector2(vp.X, vp.Y);
        int n = P.Length;
        // every point on screen gets its tick (a GTA room is often 50 000+ points: showing only the ones near the mouse hid
        // most of the mesh while editing). Very dense meshes (a joined building, 800 000 points): one tick per 3 px cell of
        // the screen, so the whole mesh still shows its points without a million squares per frame
        var r2 = new Vector2(2f); var r3 = new Vector2(3f);
        int drawn = 0;
        if (n <= 150000)
        {
            for (int i = 0; i < n; i++)
            {
                if (!Ok[i]) continue;
                var p = org + P[i];
                if (s.Verts.Contains(i)) dl.AddRectFilled(p - r3, p + r3, red); else dl.AddRectFilled(p - r2, p + r2, blue);
                drawn++;
            }
            vertexTicksDrawn = drawn;
            return;
        }
        const int Cell = 3;
        int gw = (int)vp.W / Cell + 1, gh = (int)vp.H / Cell + 1;
        if (tickCells == null || tickCells.Length < gw * gh) tickCells = new int[gw * gh];
        int stamp = ++tickStamp;
        for (int i = 0; i < n; i++)
        {
            if (!Ok[i]) continue;
            int cx = (int)(P[i].X / Cell), cy = (int)(P[i].Y / Cell);
            if (cx < 0 || cy < 0 || cx >= gw || cy >= gh) continue;
            int k = cx + cy * gw;
            if (tickCells[k] == stamp) continue;
            tickCells[k] = stamp;
            var p = org + P[i]; dl.AddRectFilled(p - r2, p + r2, blue); drawn++;
        }
        Prof("ov ticks cells");
        foreach (var i in s.Verts)
        {
            if (i >= n || !Ok[i]) continue;
            var p = org + P[i]; dl.AddRectFilled(p - r3, p + r3, red);
        }
        vertexTicksDrawn = drawn;
    }
    int[] tickCells; int tickStamp;

    (int, int, int, int) statsCache; double statsTime = -1; int statsCount = -1;
    void Statistics(ImDrawListPtr dl, Viewport vp)
    {
        // totals cached: walking every face of a 3M-polygon map each frame costs more than drawing it
        // a changed object count refreshes at once on small scenes; on a streamed map (thousands, changing every frame) once a second
        bool big = Scene.Objects.Count > 3000;
        if (ImGui.GetTime() - statsTime > (big ? 1.0 : 0.5) || !big && statsCount != Scene.Objects.Count) { statsCache = Scene.Stats(); statsTime = ImGui.GetTime(); statsCount = Scene.Objects.Count; }
        var (objs, polys, tris, verts) = statsCache;
        var cur = Current;
        var p = new Vector2(vp.X + 12, vp.Y + 30);
        uint white = 0xFFE8E8E8;
        dl.AddText(p, white, $"            Total      {(cur != null ? cur.Name : "")}");
        dl.AddText(p + new Vector2(0, 16), white, $"Polys:   {polys,8:N0}   {(cur != null ? cur.Mesh.Faces.Count.ToString("N0") : "")}");
        dl.AddText(p + new Vector2(0, 32), white, $"Tris:     {tris,8:N0}   {(cur != null ? cur.TriangleCount.ToString("N0") : "")}");
        dl.AddText(p + new Vector2(0, 48), white, $"Verts:   {verts,8:N0}   {(cur != null ? cur.Mesh.Verts.Count.ToString("N0") : "")}");
    }

    public void Dispose()
    {
        lines.Dispose(); wires.Dispose(); outline.Dispose(); splats?.Dispose(); StopPhotoRender(); Api.Dispose();
        renderer.Dispose();
    }
}
