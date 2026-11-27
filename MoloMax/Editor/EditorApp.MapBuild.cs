using System.Windows.Forms;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Modeling right on the map: objects made in MAP mode (primitives, Edit Poly, the MLO Room Builder, sculpt, paint...) and
/// game models opened for editing in place. They are written with the map: new models (.ydr), their .ytyp, an MLO when
/// there are rooms, and the entities that place them, in the same FiveM resource as the edited .ymap files.
/// </summary>
public sealed partial class EditorApp
{
    public const string MapBuildFolder = "Polymorph build";

    /// <summary>An object made here in MAP mode (not a game entity): every modeling tool works on it.</summary>
    public bool MapEditable(SceneObject o) => o != null && o.Id >= 0 && o.Gta.MapArchetype == null && !entOf.ContainsKey(o.Id) && !intEntOf.ContainsKey(o.Id) && o.Id != placeGhost;

    /// <summary>MAP: a model placed by a .ymap (read-only, shared by every placement) whose mesh is here: it can become your own copy.</summary>
    public bool MapGameModel(SceneObject o) => MapMode && o != null && entOf.ContainsKey(o.Id) && o.Mesh != null && o.Mesh.Faces.Count > 0 && !ReferenceEquals(o.Mesh, GpuOnlyStub);

    /// <summary>Panels of a .ymap model (Material, Paint): one button makes your own copy of it, then the full panel shows. True while the
    /// model is still the shared one (the caller draws nothing more).</summary>
    bool MapOwnCopyGate(SceneObject o, string what)
    {
        if (!MapGameModel(o)) return false;
        Ui.TextWrapped($"{o.Name} is placed by a map file and shared by all its placements. To {what}, make your own copy: it is saved as a new model with the map.");
        if (Theme.Toggle("EDIT THIS MODEL (MY OWN COPY)", true, new Vector2(-1, 36))) EditInPlace(o);
        TutRect("map_own_copy");
        return MapGameModel(o);
    }

    /// <summary>The objects that will be written as new models when the map is saved.</summary>
    List<SceneObject> MapBuildObjects() => Scene.Objects.Where(o => MapEditable(o) && o.Gta.Role != GtaRole.Ignore && (o.Mesh.Faces.Count > 0 || o.GtaType is GtaObjectType.Light or GtaObjectType.Portal)).ToList();

    /// <summary>Undo in MAP mode copies the meshes of what is modeled here (game meshes stay shared, they never change).</summary>
    void MapUndoSetup() { if (MapMode && Undo != null) Undo.MutableMesh = o => o.Id >= 0 && o.Gta.MapArchetype == null; }

    /// <summary>Ground point at the middle of the view (where new objects and the room plan go).</summary>
    Vector3 MapGround()
    {
        var vp = Viewports[ActiveViewport];
        var mid = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.5f);
        return MapPick(vp, mid, -1, out var p, out _) ? p : MapFocus();
    }

    /// <summary>Called before edit mode in MAP: true when the current object can be edited (a game model is turned into an own copy first).</summary>
    bool MapCanEdit()
    {
        var o = Current;
        if (MapEditable(o)) return true;
        if (o != null && entOf.ContainsKey(o.Id)) return EditInPlace(o);
        SetStatus(o == null ? "Select an object to edit it." : "This object belongs to a game interior: open it as a model to edit its mesh.");
        return false;
    }

    /// <summary>
    /// A game model of the map becomes an own model: its mesh is copied (the other places that use it are untouched), the
    /// entity leaves its .ymap and the copy is written as a new model when the map is saved.
    /// </summary>
    public bool EditInPlace(SceneObject o)
    {
        if (o == null || !entOf.TryGetValue(o.Id, out var de)) return false;
        if (archSlots.TryGetValue(de.Ent.ArchetypeHash, out var slot) && slot.State != 2) { SetStatus("This model is still loading: try again in a moment."); return false; }
        string name = MapSafeName((de.Ent.Archetype ?? de.Ent.ArchetypeHash.ToString("x8")) + "_edit");
        Do("Edit game model in place", () =>
        {
            de.Doc.Remove(de.Ent); objOf.Remove(de.Ent); entOf.Remove(o.Id); de.Doc.Modified = true;
            o.Mesh = o.Mesh.Clone(); o.InstanceKey = null; o.Touch();
            o.Gta.MapArchetype = null; o.Gta.Role = GtaRole.Auto; o.Gta.PropCollision = GtaPropCollision.Mesh;
            o.Name = Scene.UniqueName(name); o.Folder = MapBuildFolder;
            if (!Scene.Folders.Contains(MapBuildFolder)) Scene.Folders.Add(MapBuildFolder);
        });
        SetStatus($"{o.Name}: your own copy of this game model, edit it freely. Saved as a new model with the map (the game's collision of the original stays in its .ybn).");
        return true;
    }

    /// <summary>The .mmx that holds what is modeled on the map (MAP mode has its own file: the model scene keeps CurrentFile).</summary>
    public string MapWorkFile;

    /// <summary>
    /// File > Save in MAP mode: what you modeled on the map goes into a .mmx (asked the first time, and on Save As), and the
    /// map resource (.ymap, new models) is written as before. Returns false when the file window was cancelled or saving failed.
    /// </summary>
    public bool SaveMapWork(bool saveAs, string pathOverride = null, string mapDir = null)
    {
        var path = pathOverride ?? MapWorkFile;
        // nothing modeled on the map yet: Save only writes the map resource, no file window
        if (pathOverride == null && !saveAs && path == null && !Scene.Objects.Any(MapEditable)) { SaveMaps(mapDir); Dirty = false; return true; }
        if (pathOverride == null && (saveAs || path == null))
        {
            var start = path ?? (CurrentFile != null && CurrentFile.EndsWith(".mmx", StringComparison.OrdinalIgnoreCase) ? CurrentFile : null);
            using var d = new SaveFileDialog { Filter = SceneFilter, Title = "Save what you modeled on the map", FileName = start != null ? Path.GetFileName(start) : "my_map.mmx" };
            if (start != null && Directory.Exists(Path.GetDirectoryName(start))) d.InitialDirectory = Path.GetDirectoryName(start);
            if (ShowFileDialog(d) != DialogResult.OK) { Input.ReleaseAllKeys(); SetStatus("Save cancelled"); return false; }
            path = d.FileName; Input.ReleaseAllKeys();
        }
        int objs;
        try
        {
            BakeAllPaint();
            var work = MapWorkScene();
            objs = work.Objects.Count;
            BackupBeforeSave(path);
            SceneIO.Save(work, path);
            MapWorkFile = path; AddRecent(path);
        }
        catch (Exception e)
        {
            if (inApi) throw;
            MessageBox.Show(form, "Could not save:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        int n = 0; string mapMsg;
        try { n = SaveMaps(mapDir); mapMsg = status; } catch (Exception e) { mapMsg = "map resource not written: " + e.Message; }
        Dirty = false;
        SetStatus($"Saved {Path.GetFileName(path)} ({objs} objects modeled on the map)" + (n > 0 ? " · " + mapMsg : ""));
        return true;
    }

    /// <summary>A scene of its own with the objects modeled on the map (world positions kept) and only the materials they use.</summary>
    Scene MapWorkScene()
    {
        var objs = Scene.Objects.Where(MapEditable).ToList();
        var sc = new Scene(); sc.Materials.Clear(); sc.Folders.Clear();
        var used = new HashSet<int>(objs.SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)));
        sc.Materials.AddRange(Scene.Materials.Where(m => used.Contains(m.Id) || m.Id == 0));
        if (sc.Materials.Count == 0) sc.Materials.Add(Scene.Material(0));
        sc.NextMaterialId = Scene.NextMaterialId;
        var keep = objs.Select(o => o.Id).ToHashSet();
        foreach (var o in objs)
        {
            var c = o.Clone();
            if (c.ParentId >= 0 && !keep.Contains(c.ParentId)) c.ParentId = -1;
            sc.Objects.Add(c);
            if (!string.IsNullOrEmpty(c.Folder) && !sc.Folders.Contains(c.Folder)) sc.Folders.Add(c.Folder);
        }
        sc.NextObjectId = Scene.NextObjectId;
        sc.Plan = Scene.Plan;
        sc.Gta = (modelScene?.Gta ?? new GtaProjectSettings()).Clone();
        return sc;
    }

    /// <summary>File > Open in MAP mode: the objects of a .mmx come onto the map where they were saved (one undo step), editable.</summary>
    public void OpenMapWork(string path)
    {
        Scene src;
        try { src = SceneIO.Load(path); }
        catch (Exception e) { if (inApi) throw; MessageBox.Show(form, "Could not open the scene:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        int added = 0;
        Do("Open " + Path.GetFileName(path), () =>
        {
            var matMap = new Dictionary<int, int>();
            foreach (var m in src.Materials)
            {
                var same = Scene.Materials.FirstOrDefault(x => x.Name == m.Name && x.Shader == m.Shader && x.DiffuseTexture == m.DiffuseTexture);
                if (same != null) { matMap[m.Id] = same.Id; continue; }
                var c = m.Clone(); c.Id = Scene.NextMaterialId++; Scene.Materials.Add(c); matMap[m.Id] = c.Id;
            }
            var idMap = new Dictionary<int, int>();
            foreach (var o in src.Objects) idMap[o.Id] = Scene.NextObjectId++;
            Selected.Clear();
            foreach (var o in src.Objects)
            {
                var c = o.Clone(); c.Id = idMap[o.Id];
                c.ParentId = o.ParentId >= 0 && idMap.TryGetValue(o.ParentId, out var pid) ? pid : -1;
                foreach (var f in c.Mesh.Faces) f.MatId = matMap.TryGetValue(f.MatId, out var mid) ? mid : 0;
                c.Gta.MapArchetype = null; c.InstanceKey = null;
                if (string.IsNullOrEmpty(c.Folder)) c.Folder = MapBuildFolder;
                if (!Scene.Folders.Contains(c.Folder)) Scene.Folders.Add(c.Folder);
                c.Name = Scene.UniqueName(c.Name);
                c.Touch();
                Scene.Objects.Add(c); Selected.Add(c.Id); added++;
            }
            if (src.Objects.Count > 0) ActiveId = idMap[src.Objects[0].Id];
        });
        MapWorkFile = path; AddRecent(path);
        renderer.Textures.SearchFolders.Add(Path.GetDirectoryName(Path.GetFullPath(path)));
        renderer.Textures.ClearResolveCache();
        Dirty = false;
        SetStatus($"{Path.GetFileName(path)}: {added} objects put back on the map, editable (Ctrl+S saves them there again)");
    }

    /// <summary>New object in MAP mode: in the build folder.</summary>
    void MapAdopt(SceneObject o)
    {
        if (!MapMode || o == null || o.Gta.MapArchetype != null) return;
        if (string.IsNullOrEmpty(o.Folder)) { o.Folder = MapBuildFolder; if (!Scene.Folders.Contains(MapBuildFolder)) Scene.Folders.Add(MapBuildFolder); }
    }

    /// <summary>
    /// Writes what was modeled on the map into <c>stream/build</c> of the map resource: models, .ytyp, MLO, collisions and
    /// the entities placing them. Returns the number of files written.
    /// </summary>
    int SaveMapBuild(string dir, List<string> errors)
    {
        var buildDir = Path.Combine(dir, "stream", "build");
        var objs = MapBuildObjects();
        if (objs.Count == 0)
        {
            if (Directory.Exists(buildDir)) try { Directory.Delete(buildDir, true); } catch { }
            return 0;
        }
        BakeAllPaint();
        // a small scene of its own, around an anchor (an MLO is placed by its origin; world-sized offsets inside it are avoided)
        var anchor = MapBuildAnchor(objs);
        var sc = new Scene(); sc.Materials.Clear(); sc.Materials.AddRange(Scene.Materials); sc.NextMaterialId = Scene.NextMaterialId; sc.Folders.Clear();
        var keep = objs.Select(o => o.Id).ToHashSet();
        foreach (var o in objs)
        {
            var c = o.Clone();
            c.Position -= anchor;
            if (c.ParentId >= 0 && !keep.Contains(c.ParentId)) c.ParentId = -1;
            sc.Objects.Add(c);
        }
        sc.NextObjectId = Scene.NextObjectId;
        sc.GtaOrigin = anchor;
        var res = MapSafeName(Path.GetFileName(dir) + "_build");
        sc.Gta = (modelScene?.Gta ?? new GtaProjectSettings()).Clone();
        sc.Gta.ResourceName = res; sc.Gta.Heading = 0; sc.Gta.SharedPivot = false; // models keep their own pivot: they stand where they were made
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_build_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rep = GtaExporter.Export(sc, tmp, LoadImageRgba, renderer.Textures.SearchFolders.ToList());
            errors.AddRange(rep.Errors.Select(e => "build: " + e));
            mapBuildReport = rep;
            if (!rep.Ok && rep.Models == 0) return 0;
            var src = Path.Combine(tmp, res, "stream");
            if (Directory.Exists(buildDir)) Directory.Delete(buildDir, true);
            int n = 0;
            foreach (var f in Directory.Exists(src) ? Directory.GetFiles(src, "*", SearchOption.AllDirectories) : Array.Empty<string>())
            {
                var rel = Path.GetRelativePath(src, f);
                var to = Path.Combine(buildDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to, true); n++;
            }
            return n;
        }
        catch (Exception e) { errors.Add("build: " + e.Message); return 0; }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
    ExportReport mapBuildReport;

    /// <summary>Where the build sits: the room plan's origin when there are rooms, else the middle of the built objects on the ground.</summary>
    Vector3 MapBuildAnchor(List<SceneObject> objs)
    {
        if (objs.Any(o => o.Folder == PlanFolder && o.GtaType == GtaObjectType.MloRoom) && Scene.Plan != null)
            return new Vector3(Scene.Plan.OriginX, Scene.Plan.OriginY, Scene.Plan.OriginZ);
        var mn = objs.Select(o => o.WorldBounds().Min).Aggregate(Vector3.Min); var mx = objs.Select(o => o.WorldBounds().Max).Aggregate(Vector3.Max);
        return new Vector3(MathF.Round((mn.X + mx.X) * 0.5f), MathF.Round((mn.Y + mx.Y) * 0.5f), MathF.Floor(mn.Z));
    }

    /// <summary>Self-tests and films: the map scene starts without anything modeled in it.</summary>
    void ClearMapBuild() { Scene.Objects.RemoveAll(o => MapEditable(o)); Scene.Plan = new MoloMax.Core.Tools.RoomPlanData(); Selected.Clear(); SelectionChanged(); }

    /// <summary>Map panel: what is modeled here and where it goes.</summary>
    void MapBuildPanel()
    {
        Ui.Text("Model on the map");
        Theme.Tip("Everything made here is written with the map: new models, an MLO when you draw rooms, and the entities that place them.");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Box here", new Vector2(w2, 0))) { Cursor3D = MapGround(); AddPrimitive(CreateKind.Box); }
        TutRect("map_box");
        Theme.Tip("A box on the ground at the middle of the view. Tab (or 1-5) to edit it like in MODEL mode.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("MLO rooms here", new Vector2(w2, 0))) OpenPlanOnMap();
        TutRect("map_mlo");
        Theme.Tip("Opens the MLO Room Builder with its plan on the ground at the middle of the view: the rooms build right on the map.");
        if (Current != null && entOf.ContainsKey(Current.Id))
        {
            if (ImGui.Button("Edit this game model", new Vector2(-1, 0)) && EditInPlace(Current)) SetLevel(SubObject.Polygon);
            TutRect("map_edit");
            Theme.Tip("Makes your own copy of the selected game model, right where it is, and opens it in edit mode.");
        }
        if (ImGui.Button("Import a 3D scene here...", new Vector2(w2, 0))) ImportSceneOnMapDialog();
        TutRect("map_import");
        Theme.Tip("A Polymorph scene (.mmx), a .glb / .obj or a GTA model, put on the ground at the middle of the view. It is saved with the map.");
        ImGui.SameLine(0, 4);
        var ms = otherMode?.Scene;
        ImGui.BeginDisabled(ms == null || ms.Objects.Count == 0);
        if (ImGui.Button("My MODEL scene here", new Vector2(w2, 0)) && ms != null) AskPlaceOnMap(ms, "MODEL scene", string.IsNullOrEmpty(CurrentFile) ? null : Path.GetDirectoryName(CurrentFile));
        ImGui.EndDisabled();
        Theme.Tip("A copy of what you modeled in MODEL mode, on the ground at the middle of the view.");
        var n = MapBuildObjects().Count;
        Ui.TextDisabled(n == 0 ? "Nothing modeled yet." : $"{n} object(s) modeled here, written to stream/build on save.");
    }

    void ImportSceneOnMapDialog()
    {
        using var d = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "Scenes and models|*.mmx;*.fbx;*.glb;*.gltf;*.obj;*.ydr;*.yft;*.ydd|Polymorph scene (*.mmx)|*.mmx|glTF (*.glb, *.gltf)|*.glb;*.gltf|Wavefront OBJ (*.obj)|*.obj|GTA V (*.ydr, *.yft, *.ydd)|*.ydr;*.yft;*.ydd",
            Title = "Import a 3D scene onto the map",
        };
        if (ShowFileDialog(d) != System.Windows.Forms.DialogResult.OK) return;
        var path = d.FileName;
        if (path.EndsWith(".mmx", StringComparison.OrdinalIgnoreCase))
        {
            Scene src;
            try { src = MoloMax.Core.Scenes.SceneIO.Load(path); } catch (Exception e) { SetStatus("Could not open the scene: " + e.Message); return; }
            AskPlaceOnMap(src, Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(path));
            return;
        }
        var g = MapGround();
        var objs = ImportFile(path, g);
        importReportOpen = false;
        foreach (var o in objs.Concat(objs.Select(Scene.Parent).Where(x => x != null)).Distinct()) { o.Folder = ""; MapAdopt(o); }
        Selected.Clear(); foreach (var o in objs) Selected.Add(o.Id); SelectionChanged();
        SetStatus($"{Path.GetFileName(path)}: {objs.Count} object(s) on the map, saved with it.");
    }

    /// <summary>Copies a whole scene onto the map, its lowest point on the ground at the middle of the view.</summary>
    public List<SceneObject> ImportSceneOnMap(Scene src, string label, Matrix4x4? place = null)
    {
        var res = new List<SceneObject>();
        var items = src.Objects.Where(o => o.Mesh != null && !(o.Gta.Role == GtaRole.Ignore && o.Name.StartsWith("ped_scale"))).ToList();
        if (items.Count == 0) { SetStatus("That scene is empty."); return res; }
        var mn = items.Where(o => o.Mesh.Faces.Count > 0).Select(o => o.WorldBounds().Min).DefaultIfEmpty(Vector3.Zero).Aggregate(Vector3.Min);
        var mx = items.Where(o => o.Mesh.Faces.Count > 0).Select(o => o.WorldBounds().Max).DefaultIfEmpty(Vector3.Zero).Aggregate(Vector3.Max);
        var shift = MapGround() - new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
        Do("Import " + label + " on the map", () =>
        {
            // materials: same name and shader = the same material, else a copy with a new id
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
                if (o.ParentId < 0)
                {
                    if (place is { } pl)
                    {
                        // where a .ymap (or the scene's GTA position) puts it: scene coordinates x placement
                        Matrix4x4.Decompose(o.World * pl, out var sc, out var r, out var t);
                        c.Position = t; c.Rotation = Quaternion.Normalize(r); c.Scale = sc;
                    }
                    else c.Position += shift;
                }
                c.Folder = ""; c.Gta.MapArchetype = null;
                Scene.Objects.Add(c); MapAdopt(c); res.Add(c);
            }
            foreach (var c in res) c.ParentId = c.ParentId >= 0 && idMap.TryGetValue(c.ParentId, out var np) ? np : -1;
            foreach (var c in res) c.Touch();
            Selected.Clear(); foreach (var c in res.Where(c => c.ParentId < 0)) Selected.Add(c.Id); SelectionChanged();
        });
        renderer.Collect(Scene);
        var at = place?.Translation ?? shift;
        SetStatus($"{label}: {res.Count} object(s) on the map at {at.X:0}, {at.Y:0}{(place != null ? ", where its .ymap / GTA position puts it" : "")}. Saved with the map.");
        return res;
    }

    /// <summary>Room Builder on the map: the plan's origin goes to the ground at the middle of the view (when the plan is new).</summary>
    void OpenPlanOnMap()
    {
        Scene.Plan ??= new MoloMax.Core.Tools.RoomPlanData();
        if (Scene.Plan.Rooms.Count == 0)
        {
            var g = MapGround();
            Scene.Plan.OriginX = MathF.Round(g.X); Scene.Plan.OriginY = MathF.Round(g.Y); Scene.Plan.OriginZ = MathF.Round(g.Z * 10) / 10;
            planZoom = 7; planCenterNext = true; planFoot = null; // the buildings around, seen from above, 1 m = 7 px
        }
        planOpen = true;
    }
}

public sealed partial class EditorApp
{
    /// <summary>
    /// MAP mode, over the active view: what the selected object is, and for a model placed by a map file one big button that
    /// takes it into your project (your own copy: every modeling tool works on it, it is saved and exported with the map).
    /// </summary>
    void MapModelBanner()
    {
        if (!MapMode || PlayOn || paintMode || SculptMode || transforming || ModalActive) return;
        var o = Current; if (o == null) return;
        bool game = MapGameModel(o), mine = MapEditable(o) && o.Mesh != null && o.Mesh.Faces.Count > 0;
        if (!game && !mine) return;
        var vp = Viewports[ActiveViewport];
        var orange = new Vector4(0.95f, 0.42f, 0.11f, 1f); var green = new Vector4(0.36f, 0.78f, 0.36f, 1f);
        string label = "+  ADD THIS MODEL TO MY PROJECT";
        float bw = ImGui.CalcTextSize(label).X + 44;
        ImGui.SetNextWindowPos(new Vector2(vp.X + vp.W * 0.5f, vp.Y + 34), ImGuiCond.Always, new Vector2(0.5f, 0));
        ImGui.SetNextWindowBgAlpha(0.82f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8); ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.Begin("##mapmodelbanner", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                   | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove);
        if (game)
        {
            Ui.TextColored(new Vector4(0.75f, 0.78f, 0.82f, 1), $"{o.Name}  ·  model of a map file (read only)");
            ImGui.PushStyleColor(ImGuiCol.Button, orange); ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.52f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.36f, 0.08f, 1f)); ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.06f, 0.06f, 0.07f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6);
            bool add = ImGui.Button(label, new Vector2(bw, 34)); TutRect("map_add_model");
            ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
            if (add) EditInPlace(o);
            Ui.TextDisabled("Your own copy: every modeling tool, saved and exported with the map.");
        }
        else
        {
            Ui.TextColored(green, $"{o.Name}  ·  MY MODEL");
            ImGui.SameLine(); Ui.TextDisabled("every tool works on it  ·  saved and exported with the map (Ctrl+S)");
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
