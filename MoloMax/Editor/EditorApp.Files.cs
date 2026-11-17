using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public sealed partial class EditorApp
{
    const string SceneFilter = "Polymorph scene (*.mmx)|*.mmx|All files (*.*)|*.*";

    public bool ConfirmDiscard()
    {
        if (!Dirty) return true;
        var r = MessageBox.Show(form, "Save changes to the current scene?", "Polymorph", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save(false);
        return true;
    }

    public void NewScene()
    {
        if (!ConfirmDiscard()) return;
        Scene = new Scene(); Undo.Clear(); lastAdd = null; ActiveId = -1; Selected.Clear(); subSel.Clear(); Level = SubObject.Object;
        CurrentFile = null; Dirty = false; renderer.Invalidate(); edgeCache.Clear();
        ResetForge(); cubeProj = null; decalPlace = null; showMode = 0; showModeHidden = null;
        SetStatus("New scene");
    }

    public void OpenFile(string path)
    {
        try
        {
            Scene = SceneIO.Load(path);
            lastAdd = null; ActiveId = -1;
            Undo.Clear(); Selected.Clear(); subSel.Clear(); Level = SubObject.Object;
            CurrentFile = path; Dirty = false; renderer.Invalidate(); edgeCache.Clear();
            ResetForge(); cubeProj = null; decalPlace = null; showMode = 0; showModeHidden = null;
            // MLO rooms imported before: plain 8-corner volumes are bounds, not models
            foreach (var o in Scene.Objects)
                if (o.GtaType == GtaObjectType.MloRoom && o.Folder != PlanFolder && o.Mesh.Verts.Count == 8 && o.Mesh.Faces.Count == 6) o.Gta.BoundsOnly = true;
            renderer.Textures.SearchFolders.Clear();
            renderer.Textures.SearchFolders.Add(Path.GetDirectoryName(Path.GetFullPath(path)));
            renderer.Textures.ClearResolveCache();
            FrameSelection(Viewports[3]);
            LoadPaintSets();
            if (!path.Contains(Path.Combine("Polymorph", "autosave"))) AddRecent(path);
            SetStatus($"Opened {Path.GetFileName(path)} ({Scene.Objects.Count} objects)");
        }
        catch (Exception e) { if (inApi) throw; MessageBox.Show(form, "Could not open the scene:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public bool Save(bool saveAs)
    {
        if (MapMode) { SaveMaps(); return true; }
        var path = CurrentFile;
        if (saveAs || path == null)
        {
            using var d = new SaveFileDialog { Filter = SceneFilter, Title = "Save scene", FileName = path != null ? Path.GetFileName(path) : "scene.mmx" };
            if (ShowFileDialog(d) != DialogResult.OK) { Input.ReleaseAllKeys(); return false; }
            path = d.FileName;
            Input.ReleaseAllKeys();
        }
        try
        {
            CurrentFile = path;
            BakeAllPaint();               // painted textures and layer files next to the scene
            BackupBeforeSave(path);
            SceneIO.Save(Scene, path);
            AddRecent(path);
            Dirty = false;
            SetStatus("Saved " + Path.GetFileName(path));
            return true;
        }
        catch (Exception e) { if (inApi) throw; MessageBox.Show(form, "Could not save:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
    }

    public void ExportObj(bool selectedOnly)
    {
        using var d = new SaveFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", Title = "Export OBJ for Sollumz", FileName = "polymorph_export.obj" };
        if (ShowFileDialog(d) != DialogResult.OK) { Input.ReleaseAllKeys(); return; }
        Input.ReleaseAllKeys();
        try
        {
            SceneIO.ExportObj(Scene, d.FileName, selectedOnly, Selected);
            SetStatus("Exported " + Path.GetFileName(d.FileName) + " (Y-up, vertex colours kept)");
        }
        catch (Exception e) { MessageBox.Show(form, "Export failed:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public string PickTexture()
    {
        using var d = new OpenFileDialog { Filter = "Textures (*.dds;*.png;*.jpg;*.bmp)|*.dds;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*", Title = "Choose a texture" };
        var r = ShowFileDialog(d) == DialogResult.OK ? d.FileName : null;
        Input.ReleaseAllKeys();
        return r;
    }

    public void DropFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mmx") { if (ConfirmDiscard()) OpenFile(path); return; }
        if (TryImportSplat(path, null)) return;
        if (MoloMax.Core.IO.ModelImporter.CanImport(path)) { ImportFilesAsync(new[] { path }); return; }
        if (ext is ".dds" or ".png" or ".jpg" or ".jpeg" or ".bmp")
        {
            // dropping a texture: new material on the selection
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var m = Scene.AddMaterial(name, new System.Numerics.Vector3(0.8f));
            m.DiffuseTexture = path;
            m.Shader = "default";
            if (Selected.Count > 0) AssignMaterial(m.Id);
            selectedMaterial = m.Id; materialEditorOpen = true;
            SetStatus($"Material {name} created from the dropped texture");
            return;
        }
        SetStatus($"{ext} files are not supported.");
    }

    // ---------------------------------------------------------------- model import

    string importReportTitle;
    readonly List<string> importReport = new();
    bool importReportOpen;

    public void ImportDialog()
    {
        using var d = new OpenFileDialog
        {
            Filter = "All supported|*.fbx;*.glb;*.gltf;*.obj;*.ydr;*.ydd;*.yft;*.ybn;*.xml;*.ply;*.spz;*.splat|Gaussian splat scans (*.ply;*.spz;*.splat)|*.ply;*.spz;*.splat|GTA V (*.ydr;*.ydd;*.yft;*.ybn)|*.ydr;*.ydd;*.yft;*.ybn|CodeWalker XML (*.ydr.xml, *.yft.xml...)|*.xml|FBX (*.fbx)|*.fbx|glTF binary (*.glb)|*.glb|glTF (*.gltf)|*.gltf|Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*",
            Title = "Import model",
            Multiselect = true,
            InitialDirectory = Settings.LastFolder != null && Directory.Exists(Settings.LastFolder) ? Settings.LastFolder : "",
        };
        var ok = ShowFileDialog(d) == DialogResult.OK;
        Input.ReleaseAllKeys();
        if (!ok) return;
        Settings.LastFolder = Path.GetDirectoryName(d.FileNames[0]); Settings.Save();
        ImportFilesAsync(d.FileNames);
    }

    MoloMax.Core.IO.ImportOptions ImportOpts() => new()
    {
        YUpToZUp = Settings.ImportYUp,
        Scale = Settings.ImportScale <= 0 ? 1 : Settings.ImportScale,
        AutoSmoothAngle = Settings.ImportSmoothAngle,
        MergeIntoOne = Settings.ImportMerge,
    };

    /// <summary>Imports a model file; its base lands on <paramref name="at"/> (default: the 3D cursor). Returns the new objects.</summary>
    public List<SceneObject> ImportFile(string path, Vector3? at = null, Quaternion? rot = null, Vector3? scale = null)
    {
        if (TryImportSplat(path, at, wait: true))
        {
            var made = Scene.Objects.Where(x => string.Equals(x.SplatFile, path, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Id).Take(1).ToList();
            return made;
        }
        MoloMax.Core.IO.ImportResult r;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { r = MoloMax.Core.IO.ModelImporter.Import(path, ImportOpts()); }
        catch (Exception e) { ImportFailed(path, e); return new List<SceneObject>(); }
        return AddImported(path, r, at, sw, rot, scale);
    }

    void ImportFailed(string path, Exception e)
    {
        importReportTitle = "Import failed: " + Path.GetFileName(path);
        importReport.Clear(); importReport.Add(e.Message); importReportOpen = true;
        SetStatus("Import failed: " + e.Message);
    }

    // ---- background import: files are read and prepared on worker threads, added to the scene on the UI thread
    sealed class PendingImport { public string Path; public Vector3? At; public Quaternion? Rot; public Vector3? Scale; public System.Threading.Tasks.Task<MoloMax.Core.IO.ImportResult> Task; public System.Diagnostics.Stopwatch Sw; }
    readonly List<PendingImport> pendingImports = new();
    int importTotal, importDone;

    public void ImportFilesAsync(IEnumerable<string> paths, Vector3? at = null, Quaternion? rot = null, Vector3? scale = null, Func<string, string> prepare = null)
    {
        var opt = ImportOpts();
        foreach (var p in paths)
        {
            var path = p;
            if (prepare == null && TryImportSplat(path, at)) continue;
            pendingImports.Add(new PendingImport
            {
                Path = path, At = at, Rot = rot, Scale = scale, Sw = System.Diagnostics.Stopwatch.StartNew(),
                // prepare (optional) runs on the worker too, e.g. extracting the file from the game archives first
                Task = System.Threading.Tasks.Task.Run(() => MoloMax.Core.IO.ModelImporter.Import(prepare != null ? prepare(path) : path, opt)),
            });
            importTotal++;
        }
    }

    /// <summary>Adds finished imports (in the order they were asked) and shows the progress window.</summary>
    void PumpImports()
    {
        while (pendingImports.Count > 0 && pendingImports[0].Task.IsCompleted)
        {
            var pi = pendingImports[0]; pendingImports.RemoveAt(0); importDone++;
            if (pi.Task.IsFaulted) ImportFailed(pi.Path, pi.Task.Exception?.InnerException ?? pi.Task.Exception);
            else AddImported(pi.Path, pi.Task.Result, pi.At, pi.Sw, pi.Rot, pi.Scale);
        }
        if (pendingImports.Count == 0) { importTotal = importDone = 0; return; }
        var cur = pendingImports[0];
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f, menuH + toolbarH + 50), ImGuiCond.Always, new Vector2(0.5f, 0));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##importprogress", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove);
        ImGui.Text($"Importing {Path.GetFileName(cur.Path)}   ({importDone + 1}/{importTotal})");
        float t = (float)(ImGui.GetTime() % 1.2) / 1.2f;
        ImGui.ProgressBar(importTotal > 1 ? (importDone + t) / importTotal : t, new Vector2(320, 0), $"{cur.Sw.Elapsed.TotalSeconds:0.0} s");
        Ui.TextDisabled("You can keep working; the model appears when it is ready.");
        ImGui.End();
    }

    List<SceneObject> AddImported(string path, MoloMax.Core.IO.ImportResult r, Vector3? at, System.Diagnostics.Stopwatch sw, Quaternion? rotation = null, Vector3? scale = null)
    {
        if (r.Meshes.Count == 0) { SetStatus("No triangles found in " + Path.GetFileName(path)); return new List<SceneObject>(); }

        var created = new List<SceneObject>();
        Do("Import " + Path.GetFileName(path), () =>
        {
            // materials: file index → scene id
            var map = new int[r.Materials.Count];
            for (int i = 0; i < r.Materials.Count; i++)
            {
                var m = r.Materials[i].Clone();
                m.Id = Scene.NextMaterialId++;
                var baseName = m.Name; int n = 2;
                while (Scene.Materials.Any(x => x.Name == m.Name)) m.Name = $"{baseName}_{n++}";
                Scene.Materials.Add(m);
                map[i] = m.Id;
            }
            foreach (var im in r.Meshes)
            {
                foreach (var f in im.Mesh.Faces) f.MatId = f.MatId >= 0 && f.MatId < map.Length ? map[f.MatId] : 0;
                var o = Scene.Add(im.Name, im.Mesh, Vector3.Zero);
                o.Name = UniqueImportName(im.Name, o);
                o.GtaType = im.GtaType;
                if (im.CollisionMaterial >= 0) o.Gta.CollisionMaterial = im.CollisionMaterial;
                if (!r.KeepFileOrigin) o.PivotToBase();           // origin at the bottom centre, like GTA props
                subSel.Remove(o.Id);
                created.Add(o);
            }
            // keep the parts where the file put them relative to each other, with the whole model's base on the 3D cursor
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var o in created) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
            // GTA files keep their own origin (the archetype origin), placed on the 3D cursor like Sollumz places it at the world origin
            var groupBase = r.KeepFileOrigin ? Vector3.Zero : new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
            var target = at ?? Cursor3D;
            foreach (var o in created) o.Position = target + (o.Position - groupBase);
            // placed like the game places it (game files browser): rotation / scale around the model origin
            if (rotation.HasValue || scale.HasValue)
            {
                var q = rotation ?? Quaternion.Identity; var sc = scale ?? Vector3.One;
                foreach (var o in created) { o.Position = target + Vector3.Transform((o.Position - target) * sc, q); o.Rotation = q * o.Rotation; o.Scale *= sc; }
            }
            // one GTA model (ydr / yft) arrives like in GIMS: a Drawable named after the file, game mesh(es) and collision under it
            var ext = Path.GetExtension(path).ToLowerInvariant();
            SceneObject node = null;
            if (ext is ".ydr" or ".yft" or ".xml" && created.Any(o => o.GtaType == GtaObjectType.Drawable))
            {
                var model = Path.GetFileNameWithoutExtension(ext == ".xml" ? Path.GetFileNameWithoutExtension(path) : path);
                node = Scene.Add(model, new PolyMesh(), target, GtaObjectType.Model);
                node.Name = model;
                node.Gta.Fragment = Path.GetExtension(ext == ".xml" ? Path.GetFileNameWithoutExtension(path) : path).Equals(".yft", StringComparison.OrdinalIgnoreCase);
                int hi = 0;
                // GIMS layout: Drawable -> game meshes + "Drawable Col" group -> collisions
                SceneObject colGroup = null;
                if (created.Any(o => o.GtaType == GtaObjectType.Collision))
                {
                    colGroup = Scene.Add(model + "_col", new PolyMesh(), target, GtaObjectType.CollisionGroup);
                    colGroup.Name = model + "_col";
                    Scene.SetParent(colGroup, node);
                }
                int ci = 0; var colNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in created)
                {
                    if (o.GtaType == GtaObjectType.Collision)
                    {
                        Scene.SetParent(o, colGroup);
                        // named after the collision material (one object per material, coloured like GIMS)
                        string cm = MoloMax.Core.Gta.Export.CollisionMaterials.NameOf(o.Gta.CollisionMaterial).ToLowerInvariant();
                        string nm = $"{model}_col_{cm}";
                        if (!colNames.Add(nm)) { nm = $"{model}_col_{cm}_{ci}"; colNames.Add(nm); }
                        ci++;
                        o.Name = nm;
                        continue;
                    }
                    Scene.SetParent(o, node);
                    if (o.GtaType == GtaObjectType.Drawable && o.Name.Equals(model, StringComparison.OrdinalIgnoreCase)) o.Name = $"{model}_high_{hi++}";
                }
            }
            Selected.Clear();
            if (node != null && (rotation.HasValue || scale.HasValue)) { node.Rotation = rotation ?? Quaternion.Identity; node.Scale = scale ?? Vector3.One; }
            if (node != null) Selected.Add(node.Id); else foreach (var o in created) Selected.Add(o.Id);
            ActiveId = node?.Id ?? created[0].Id;
            Level = SubObject.Object;
        });
        foreach (var tex in r.TexturesWritten)
        {
            var dir = Path.GetDirectoryName(tex);
            if (dir != null && !renderer.Textures.SearchFolders.Contains(dir)) renderer.Textures.SearchFolders.Add(dir);
        }
        renderer.Textures.ClearResolveCache();
        FrameSelection();

        int verts = created.Sum(o => o.Mesh.Verts.Count), faces = created.Sum(o => o.Mesh.Faces.Count);
        importReportTitle = "Imported " + Path.GetFileName(path);
        importReport.Clear();
        importReport.Add($"{created.Count} object(s), {verts:N0} vertices, {faces:N0} polygons, {r.Materials.Count} material(s), {sw.ElapsedMilliseconds} ms");
        if (r.TexturesWritten.Count > 0) importReport.Add($"{r.TexturesWritten.Count} texture(s) extracted to {Path.GetDirectoryName(r.TexturesWritten[0])}");
        importReport.AddRange(r.Warnings.Distinct());
        importReportOpen = r.Warnings.Count > 0;
        SetStatus(importReport[0]);
        return created;
    }

    string UniqueImportName(string name, SceneObject self)
    {
        bool Taken(string n) => Scene.Objects.Any(x => x != self && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (int i = 2; ; i++) { var n = $"{name}_{i:00}"; if (!Taken(n)) return n; }
    }

    void DrawImportPanel()
    {
        if (!importReportOpen) return;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(gpu.Width / 2f, gpu.Height / 3f), ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 0), ImGuiCond.Appearing);
        if (ImGui.Begin(importReportTitle + "###importreport", ref importReportOpen, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            foreach (var line in importReport) MoloMax.UI.Ui.TextWrapped("• " + line);
            if (ImGui.Button("OK", new System.Numerics.Vector2(100, 0))) importReportOpen = false;
        }
        ImGui.End();
    }
}
