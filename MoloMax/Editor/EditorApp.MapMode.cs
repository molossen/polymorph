using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode (the tab at the top, like RAGE Tool / CodeWalker): the GTA world streamed around the camera, .ymap files
/// opened from the game or from disk, game or project props placed on the ground or on walls, entities moved with the
/// usual gizmo, and the changed / new .ymap files saved as a FiveM map resource.
/// Every entity is a scene object (instanced per archetype); the folders of the Scene Explorer are the .ymap files.
/// </summary>
public sealed partial class EditorApp
{
    public bool MapMode { get; private set; }

    sealed class ModeState
    {
        public Scene Scene; public UndoStack Undo; public HashSet<int> Selected = new(); public int ActiveId = -1;
        public Dictionary<int, PolySelection> SubSel = new(); public Camera[] Cams; public bool Dirty; public bool Maximized; public int ActiveViewport;
    }
    ModeState otherMode;   // the mode that is not on screen
    Scene modelScene => MapMode ? otherMode?.Scene : Scene;

    // ---- world and documents
    MapWorldIndex world;
    System.Threading.Tasks.Task worldTask;
    string worldStatus = "";
    float worldProgress;
    readonly Dictionary<string, MapDocument> mapDocs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> mapPinned = new(StringComparer.OrdinalIgnoreCase);     // opened by hand: never unloaded
    readonly Dictionary<int, (MapDocument Doc, MapEntity Ent)> entOf = new();
    readonly Dictionary<MapEntity, int> objOf = new();
    readonly HashSet<int> mapDeleted = new();
    readonly HashSet<int> mapCulled = new();
    string mapActive;                    // ymap that receives new entities
    /// <summary>HD around the camera loads by itself when the camera is close (like the game) and goes when it zooms out.
    /// Off: only a click on the far world loads an area.</summary>
    public bool MapStream = true;
    /// <summary>Camera distance (zoom) under which the HD around it loads.</summary>
    public float MapHdZoom = 600f;
    readonly Queue<YmapInfo> mapStreamQueue = new();
    public float MapStreamRadius = 160f;
    public bool MapShowLodFar = false;
    double mapLastStream;
    System.Threading.Tasks.Task<(YmapInfo Info, MapDocument Doc, string Error)> mapLoadTask;
    int mapCullTick;
    readonly HashSet<string> mapChanged = new(StringComparer.OrdinalIgnoreCase);
    double mapChangedTime;

    // ---- archetypes (models), loaded in the background and shared by every entity that uses them
    sealed class ArchSlot
    {
        public uint Hash; public string Name; public GameAsset Asset; public int State; // 0 waiting, 1 loading, 2 ready, 3 failed / not found
        public PolyMesh Mesh; public Vector3 Min = new(-0.5f, -0.5f, 0), Max = new(0.5f, 0.5f, 1);
        public System.Threading.Tasks.Task<ArchLoad> Task;
        public string Error; public float Priority = float.MaxValue;
        public bool Seen; // went through a priority scan (until then nobody knows if it is wanted)
        public int Ver;   // GPU mesh version shared by every object of this model
        public bool GpuOnly; public double LastUsed; // level-of-detail world: only on the graphics card (the CPU copy let go), last frame drawn
    }
    /// <summary>A model read in the background: merged mesh (materials still numbered as in the file) and its GPU triangles.</summary>
    sealed class ArchLoad { public string Why; public MoloMax.Core.IO.ImportResult R; public PolyMesh Mesh; public List<MoloMax.Core.Mesh.RenderGroup> Groups; public List<(SharpDX.Direct3D11.Buffer Vb, SharpDX.Direct3D11.Buffer Ib, int Count)> Gpu; public Vector3 Min, Max; }
    readonly Dictionary<uint, ArchSlot> archSlots = new();
    readonly List<ArchSlot> archQueue = new(); int archQueuePos;
    readonly List<ArchSlot> archInFlight = new();
    double slotCountTime = -10; int slotLoading, slotWaiting, slotReady;
    readonly Dictionary<string, ArchSlot> mapSlotOf = new();
    int archLoading;
    double archScanTime, texForgetTime;
    /// <summary>Models read at the same time: most cores (the UI thread keeps priority over them).</summary>
    static readonly int ArchParallel = Math.Clamp(Environment.ProcessorCount - 2, 3, 12);
    static string MapMeshesDir => Path.Combine(MapCacheDir, "meshes");
    static string MapCacheDir => Environment.GetEnvironmentVariable("POLYMORPH_MAPCACHE") is { Length: > 0 } envDir ? envDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Polymorph", "mapcache");
    /// <summary>Self tests: the old way (model written to disk then read) to compare.</summary>
    static readonly bool MapDiskImport = Environment.GetEnvironmentVariable("POLYMORPH_MAP_DISKIMPORT") == "1";
    /// <summary>One folder for every extracted model and its textures (one texture search folder, game textures shared by name).</summary>
    static string MapModelsDir => Path.Combine(MapCacheDir, "models");

    // ---- placement
    string placeArchetype;      // archetype being placed (game or project model)
    int placeGhost = -1;
    float placeYaw;
    public bool PlaceAlign = true;
    public int PlaceAlignMode;  // 0 stand on the surface (Z up, flat on walls), 1 lay along the surface normal
    public bool PlaceRandomYaw;
    string mapSearch = "", placeSearch = "";
    List<string> placeResults = new();
    string placeResultsKey;
    string mapYmapSearch = "";

    // ------------------------------------------------------------------------------------------ mode switch

    public void SetMapMode(bool on)
    {
        if (on == MapMode) return;
        if (PlayOn) StopPlay();
        if (ModalActive) CancelModal();
        if (placeGhost >= 0) EndPlacement();
        cubeProj = null; lastMeshOp = null; lastAdd = null; SculptMode = false; paintMode = false;
        if (Level != SubObject.Object) Level = SubObject.Object;
        var cur = new ModeState
        {
            Scene = Scene, Undo = Undo, Selected = new HashSet<int>(Selected), ActiveId = ActiveId, SubSel = new(subSel),
            Cams = Viewports.Select(v => v.Camera).ToArray(), Dirty = Dirty, Maximized = Maximized, ActiveViewport = ActiveViewport,
        };
        var next = otherMode;
        if (next == null)
        {
            // first time in map mode: an empty world scene, one big perspective view
            var sc = new Scene(); sc.Folders.Clear();
            next = new ModeState
            {
                Scene = sc, Undo = new UndoStack { ImmutableMeshes = true }, Cams = Viewports.Select(v => new Camera { Kind = v.Camera.Kind, Ortho = v.Camera.Ortho }).ToArray(),
                Maximized = true, ActiveViewport = 3,
            };
            foreach (var c in next.Cams) { c.Far = 6000; }
            var p = next.Cams[3];
            p.Target = new Vector3(195, -934, 30); p.Distance = 80;
        }
        otherMode = cur;
        MapMode = on;
        // map mode allocates a lot while streaming: avoid blocking full collections (hitches)
        System.Runtime.GCSettings.LatencyMode = on ? System.Runtime.GCLatencyMode.SustainedLowLatency : System.Runtime.GCLatencyMode.Interactive;
        // background model loads use every core: the UI thread keeps the upper hand so the view never waits behind them
        try { System.Threading.Thread.CurrentThread.Priority = on ? System.Threading.ThreadPriority.AboveNormal : System.Threading.ThreadPriority.Normal; } catch { }
        Scene = next.Scene; Undo = next.Undo; MapUndoSetup();
        Selected.Clear(); Selected.UnionWith(next.Selected); ActiveId = next.ActiveId;
        subSel.Clear(); foreach (var kv in next.SubSel) subSel[kv.Key] = kv.Value;
        for (int i = 0; i < Viewports.Length && i < next.Cams.Length; i++) Viewports[i].Camera = next.Cams[i];
        Dirty = next.Dirty; Maximized = next.Maximized; ActiveViewport = next.ActiveViewport;
        renderer.Invalidate();
        hubLayout = null;
        SelectionChanged();
        propTab = on ? PropTab.Map : PropTab.Object;
        ShowPanel = true;
        SetStatus(on ? "MAP mode: the GTA world around the camera, place props, save the .ymap files (Map tab on the right)." : "MODEL mode.");
        if (on && game == null && gameTask == null && GameFiles.LooksLikeGta(Settings.GtaFolder, out _)) StartGameLoad(Settings.GtaFolder);
    }

    /// <summary>The MODEL / MAP tabs in the menu bar.</summary>
    void ModeTabs()
    {
        float w = 74, x = (gpu.Width - w * 2 - 6) * 0.5f;
        if (ImGui.GetCursorPosX() > x) x = ImGui.GetCursorPosX() + 20;
        ImGui.SetCursorPosX(x);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 1));
        if (Theme.Toggle("MODEL", !MapMode, new Vector2(w, 0))) SetMapMode(false);
        Theme.Tip("Model your props, MLO and buildings");
        ImGui.SameLine(0, 6);
        if (Theme.Toggle("MAP", MapMode, new Vector2(w, 0))) SetMapMode(true);
        Theme.Tip("Open GTA maps (.ymap), place props on the ground or walls, save for FiveM");
        ImGui.PopStyleVar();
    }

    // ------------------------------------------------------------------------------------------ per frame

    void MapFrame()
    {
        if (!MapMode) return;
        // the whole map is ~8 km wide: far clip plane past it, near plane following the zoom (depth precision)
        foreach (var v in Viewports) { v.Camera.Far = 16000; v.Camera.Near = Math.Clamp(v.Camera.Distance * 0.002f, 0.05f, 4f); }
        // world index once the game is loaded
        if (game != null && world == null && worldTask == null)
        {
            var g = game;
            worldTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { var w = MapWorldIndex.Build(g, Path.Combine(MapCacheDir, "ymap_index.json"), (s, p) => { worldStatus = s; worldProgress = p; }); world = w; }
                catch (Exception e) { worldStatus = "Map index failed: " + e.Message; }
            });
        }
        PumpMapLoad(); Prof("map load");
        PumpWorldEntities(); TrimWorldModels();
        // the level-of-detail world replaces the far models (they are not even read while the world is being read)
        if (!Settings.MapLodTree) PumpBackdrop();
        else if (LodTreeOn && backdrop.Count > 0) { backdropCancel?.Cancel(); ClearBackdrop(); }
        Prof("map far");
        PumpArchetypes(); Prof("map models");
        if (MapStream && world != null && ImGui.GetTime() - mapLastStream > 0.5) { mapLastStream = ImGui.GetTime(); StreamWorld(HdStreamingNow); }
        WorldSelectAfterLoad();
        PinPickedMaps();
        AutoInteriors();
        Prof("map stream");
        if (++mapCullTick % 15 == 0) UpdateMapCull();
    }

    /// <summary>A streamed map is view only (not in the Scene Explorer) until it is opened by hand, edited, or one of its models picked.</summary>
    bool IsViewOnlyMap(string folder) =>
        folder != null && mapDocs.TryGetValue(folder, out var d) && !mapPinned.Contains(folder) && folder != mapActive && !d.Modified && d.FromGame;

    /// <summary>Picking a model of a view-only map makes that map editable (it is listed and kept loaded).</summary>
    void PinPickedMaps()
    {
        if (Selected.Count == 0 || Selected.Count > 200) return;
        foreach (var id in Selected)
        {
            if (entOf.TryGetValue(id, out var de) && !mapPinned.Contains(de.Doc.Name)) { mapPinned.Add(de.Doc.Name); SetStatus($"{de.Doc.Name} opened for editing ({de.Doc.Entities.Count} objects)."); }
            if (intEntOf.TryGetValue(id, out var ie) && ie.In.Auto) { ie.In.Auto = false; SetStatus($"Interior {ie.In.Doc.ArchetypeName} opened for editing."); }
        }
    }

    Vector3 MapFocus()
    {
        var c = Viewports[ActiveViewport].Camera;
        return c.Target;
    }

    /// <summary>Is the camera close enough for the HD around it (and is an HD level switched on)?</summary>
    bool HdStreamingNow => (MapLevelOn[0] || MapLevelOn[5]) && Viewports[ActiveViewport].Camera.Distance <= MapHdZoom;

    void StreamWorld(bool near)
    {
        var p = MapFocus();
        bool InRange(YmapInfo y, float r) { var c = Vector3.Clamp(p, y.Min, y.Max); return new Vector2(c.X - p.X, c.Y - p.Y).Length() <= r; }
        // drop queued maps the camera left
        if (mapStreamQueue.Count > 0)
        {
            var keep = near ? mapStreamQueue.Where(y => InRange(y, MapStreamRadius * 1.3f)).ToList() : new List<YmapInfo>();
            mapStreamQueue.Clear(); foreach (var y in keep) mapStreamQueue.Enqueue(y);
        }
        if (near)
            // with the level-of-detail world the game's objects are drawn already: only the maps holding interiors open by themselves
            foreach (var y in world.Near(p, MapStreamRadius).Where(y => (!Settings.MapLodTree || y.Interior) && !mapDocs.ContainsKey(y.Name) && !mapStreamQueue.Contains(y) && !mapLoadQueue.Contains(y))
                                    .OrderBy(y => Vector3.DistanceSquared(Vector3.Clamp(p, y.Min, y.Max), p)))
                mapStreamQueue.Enqueue(y);
        // unload far maps that have no change (the ones opened by hand stay); zoomed out: every streamed one goes
        List<MapDocument> gone = null; HashSet<string> changed = null;
        foreach (var d in mapDocs.Values)
        {
            if (mapPinned.Contains(d.Name) || d.Name == mapActive) continue;
            var c = Vector3.Clamp(p, d.EntitiesMin, d.EntitiesMax);
            if (near && new Vector2(c.X - p.X, c.Y - p.Y).Length() < MapStreamRadius * 1.6f) continue;
            changed ??= ChangedMapDocs();
            if (d.Modified || changed.Contains(d.Name)) continue; // changed: keep it until saved
            (gone ??= new()).Add(d);
        }
        if (gone != null) UnloadMapDocs(gone);
    }

    /// <summary>Names of the open maps with unsaved edits (moved / deleted / added objects), in one pass over the scene.
    /// Same answer as <see cref="SyncMapDoc"/> with apply off, without walking the whole scene once per map.</summary>
    HashSet<string> ChangedMapDocs()
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, (doc, e)) in entOf)
        {
            if (res.Contains(doc.Name)) continue;
            var o = Scene.Find(id);
            if (o == null) { if (mapDeleted.Contains(id)) res.Add(doc.Name); continue; }
            if (o.Folder != doc.Name) { res.Add(doc.Name); if (o.Folder != null && mapDocs.ContainsKey(o.Folder)) res.Add(o.Folder); continue; }
            var sc = new Vector3(o.Scale.X, o.Scale.X, o.Scale.Z);
            bool moved = Vector3.DistanceSquared(o.Position, e.Position) > 1e-8f || MathF.Abs(MathF.Abs(Quaternion.Dot(o.Rotation, e.Rotation)) - 1) > 1e-6f || Vector3.DistanceSquared(sc, e.Scale) > 1e-8f;
            bool renamed = o.Gta.MapArchetype != null && !string.Equals(o.Gta.MapArchetype, e.Archetype ?? e.ArchetypeHash.ToString("X8"), StringComparison.OrdinalIgnoreCase);
            if (moved || renamed) res.Add(doc.Name);
        }
        // objects new to a map folder (placed props)
        foreach (var o in Scene.Objects)
            if (o.Gta.MapArchetype != null && o.Folder != null && !entOf.ContainsKey(o.Id) && mapDocs.ContainsKey(o.Folder)) res.Add(o.Folder);
        return res;
    }

    /// <summary>Unloads several maps at once (one pass over the scene instead of one per map: zooming out drops dozens).</summary>
    void UnloadMapDocs(List<MapDocument> docs)
    {
        if (docs.Count == 0) return;
        var names = new HashSet<string>(docs.Select(d => d.Name), StringComparer.Ordinal);
        var ids = new HashSet<int>();
        foreach (var d in docs) foreach (var e in d.Entities) if (objOf.TryGetValue(e, out var id)) ids.Add(id);
        Scene.Objects.RemoveAll(o => ids.Contains(o.Id) || (o.Folder != null && names.Contains(o.Folder) && !entOf.ContainsKey(o.Id) && o.Gta.MapArchetype != null));
        foreach (var id in ids) { entOf.Remove(id); Selected.Remove(id); }
        foreach (var d in docs)
        {
            foreach (var e in d.Entities) objOf.Remove(e);
            mapDocs.Remove(d.Name); mapPinned.Remove(d.Name);
            Scene.Folders.Remove(d.Name);
        }
        renderer.Collect(Scene);
        UpdateBackdropHidden();
    }

    void StartMapLoad(YmapInfo y, bool pin)
    {
        if (pin) mapPinned.Add(y.Name);
        var w = world;
        mapLoadTask = System.Threading.Tasks.Task.Run(() =>
        {
            try { return (y, w.Open(y), (string)null); }
            catch (Exception e) { return (y, (MapDocument)null, e.Message); }
        });
    }

    void PumpMapLoad()
    {
        // next map of the area queue (a click on the far world)
        if (mapLoadTask == null && mapLoadQueue.Count > 0)
        {
            var y = mapLoadQueue.Dequeue();
            if (!mapDocs.ContainsKey(y.Name)) StartMapLoad(y, true);
        }
        // next map of the HD around the camera
        while (mapLoadTask == null && mapStreamQueue.Count > 0)
        {
            var y = mapStreamQueue.Dequeue();
            if (!mapDocs.ContainsKey(y.Name)) StartMapLoad(y, false);
        }
        if (mapLoadTask == null || !mapLoadTask.IsCompleted) return;
        var (info, doc, err) = mapLoadTask.Result; mapLoadTask = null;
        if (doc == null) { SetStatus($"{info.Name}: {err}"); mapDocs[info.Name] = MapDocument.New(info.Name); return; } // remember the failure: no retry loop
        AddMapDoc(doc);
        UpdateBackdropHidden();
    }

    /// <summary>Puts a map's entities in the scene (no undo step: loading is not an edit).</summary>
    void AddMapDoc(MapDocument doc)
    {
        if (mapDocs.TryGetValue(doc.Name, out var old) && old != doc) UnloadMapDoc(old);
        mapDocs[doc.Name] = doc;
        if (!Scene.Folders.Contains(doc.Name)) Scene.Folders.Add(doc.Name);
        foreach (var e in doc.Entities)
        {
            if (!e.IsHd && !MapShowLodFar) continue;
            SpawnEntity(doc, e);
        }
    }

    SceneObject SpawnEntity(MapDocument doc, MapEntity e)
    {
        var slot = Slot(e.ArchetypeHash, e.Archetype);
        var o = new SceneObject
        {
            Id = Scene.NextObjectId++, Name = e.IsMlo ? "MLO " + e.DisplayName : e.DisplayName, Mesh = slot.Mesh ?? Placeholder(slot),
            Position = e.Position, Rotation = e.Rotation, Scale = new Vector3(e.Scale.X, e.Scale.X, e.Scale.Z),
            Folder = doc.Name, InstanceKey = "map:" + e.ArchetypeHash.ToString("X8"),
            WireColour = e.IsMlo ? new Vector3(0.3f, 0.85f, 0.95f) : new Vector3(0.75f, 0.75f, 0.78f),
        };
        o.Gta.MapArchetype = e.Archetype ?? e.ArchetypeHash.ToString("X8");
        o.Gta.Role = GtaRole.Ignore;
        if (e.LodLevel is 0 or 5 && !MapLevelOn[e.LodLevel]) o.Hidden = true; // that level is switched off in the World panel
        Scene.Objects.Add(o);
        entOf[o.Id] = (doc, e); objOf[e] = o.Id;
        return o;
    }

    void UnloadMapDoc(MapDocument d)
    {
        var ids = new HashSet<int>(d.Entities.Where(objOf.ContainsKey).Select(e => objOf[e]));
        Scene.Objects.RemoveAll(o => ids.Contains(o.Id) || (o.Folder == d.Name && !entOf.ContainsKey(o.Id) && o.Gta.MapArchetype != null));
        foreach (var id in ids) { entOf.Remove(id); Selected.Remove(id); }
        foreach (var e in d.Entities) objOf.Remove(e);
        mapDocs.Remove(d.Name); mapPinned.Remove(d.Name);
        Scene.Folders.Remove(d.Name);
        renderer.Collect(Scene);
        UpdateBackdropHidden();
    }

    /// <summary>After an undo / redo in map mode: entities that vanished only because the snapshot is older than the streaming come back.</summary>
    void MapAfterSceneSwap()
    {
        var present = new HashSet<int>(Scene.Objects.Select(o => o.Id));
        foreach (var d in mapDocs.Values)
        {
            if (!Scene.Folders.Contains(d.Name)) Scene.Folders.Add(d.Name);
            foreach (var e in d.Entities)
            {
                if (!objOf.TryGetValue(e, out var id)) { if (e.IsHd || MapShowLodFar) SpawnEntity(d, e); continue; }
                if (!present.Contains(id) && !mapDeleted.Contains(id)) { entOf.Remove(id); objOf.Remove(e); SpawnEntity(d, e); }
            }
        }
        // objects of maps unloaded since the snapshot
        Scene.Objects.RemoveAll(o => o.Gta.MapArchetype != null && !string.IsNullOrEmpty(o.Folder) && !mapDocs.ContainsKey(o.Folder));
        foreach (var o in Scene.Objects) if (o.InstanceKey != null && o.InstanceKey.StartsWith("map:") && archSlots.TryGetValue(Convert.ToUInt32(o.InstanceKey[4..], 16), out var s) && s.Mesh != null) o.Mesh = s.Mesh;
    }

    void UpdateMapCull()
    {
        mapCulled.Clear(); mapLive.Clear();
        var eye = Viewports[ActiveViewport].Camera.Eye;
        // view radius (setting): on a big custom map only what is around the camera is drawn and picked
        float vr = Settings.MapViewRadiusOn ? MathF.Max(20, Settings.MapViewRadius) : 0, vr2 = vr * vr;
        int cur = Current?.Id ?? int.MinValue;
        foreach (var o in Scene.Objects)
        {
            bool keep = o.Id == cur || Selected.Contains(o.Id);
            if (!keep && entOf.TryGetValue(o.Id, out var de))
            {
                float lod = MathF.Max(de.Ent.LodDist, 30) * 1.15f;
                if (Vector3.DistanceSquared(o.Position, eye) > lod * lod) { mapCulled.Add(o.Id); continue; }
            }
            if (!keep && vr > 0 && o.Mesh != null && o.Mesh.Verts.Count > 0)
            {
                var (mn, mx) = o.FastWorldBounds();
                if (Vector3.DistanceSquared(Vector3.Clamp(eye, mn, mx), eye) > vr2) { mapCulled.Add(o.Id); continue; }
            }
            mapLive.Add(o);
        }
        mapLiveCount = Scene.Objects.Count; mapLiveList = Scene.Objects; mapLiveSel = Selected.Count;
    }
    /// <summary>Scene objects within their draw distance (rebuilt with the culling, or at once when objects come / go):
    /// the drawing walks these instead of every streamed entity.</summary>
    readonly List<SceneObject> mapLive = new();
    int mapLiveCount = -1, mapLiveSel = -1; List<SceneObject> mapLiveList;
    List<SceneObject> MapLive()
    {
        if (mapLiveCount != Scene.Objects.Count || !ReferenceEquals(mapLiveList, Scene.Objects) || mapLiveSel != Selected.Count) UpdateMapCull();
        return mapLive;
    }

    // ------------------------------------------------------------------------------------------ archetypes

    ArchSlot Slot(uint hash, string name)
    {
        if (archSlots.TryGetValue(hash, out var s))
        {
            // a scene object needs the mesh itself: a model kept only on the graphics card is read again (from the cache, quick)
            if (s.GpuOnly) { s.GpuOnly = false; s.Mesh = null; s.State = 0; s.Ver = 0; }
            return s;
        }
        s = new ArchSlot { Hash = hash, Name = name };
        // a model of the project (MODEL mode) with that name: use its mesh at once
        var proj = ProjectModel(name);
        if (proj != null) { s.Mesh = proj; (s.Min, s.Max) = proj.Bounds(); s.State = 2; }
        else
        {
            s.Asset = game?.FindModel(hash);
            if (s.Asset != null && s.Name == null) s.Name = s.Asset.Name;
            // no .ydr / .yft: its model may sit in a drawable dictionary, looked up when it is read (background)
        }
        archSlots[hash] = s;
        mapSlotOf["map:" + hash.ToString("X8")] = s;
        return s;
    }

    readonly Dictionary<ArchSlot, PolyMesh> placeholders = new();
    PolyMesh Placeholder(ArchSlot s)
    {
        if (placeholders.TryGetValue(s, out var m)) return m;
        var size = Vector3.Max(s.Max - s.Min, new Vector3(0.2f));
        m = Primitives.Box(size.X, size.Y, size.Z);
        m.Transform(Matrix4x4.CreateTranslation(new Vector3((s.Min.X + s.Max.X) * 0.5f, (s.Min.Y + s.Max.Y) * 0.5f, s.Min.Z)));
        placeholders[s] = m;
        return m;
    }

    /// <summary>A Drawable / prop of the MODEL scene used as an archetype (its game meshes joined).</summary>
    PolyMesh ProjectModel(string name)
    {
        var ms = modelScene; if (ms == null || string.IsNullOrEmpty(name)) return null;
        var node = ms.Objects.FirstOrDefault(o => (o.GtaType == GtaObjectType.Model || o.GtaType == GtaObjectType.Drawable && ms.Parent(o) == null)
                                                  && string.Equals(string.IsNullOrEmpty(o.Gta.ModelName) ? o.Name : o.Gta.ModelName, name, StringComparison.OrdinalIgnoreCase));
        if (node == null) return null;
        var parts = node.GtaType == GtaObjectType.Model ? ms.Descendants(node).Where(d => d.GtaType == GtaObjectType.Drawable && d.Gta.LodLevel == 0 && d.Mesh.Faces.Count > 0).ToList() : new List<SceneObject> { node };
        var mesh = new PolyMesh();
        Matrix4x4.Invert(node.World, out var inv);
        foreach (var p in parts)
        {
            var c = p.Evaluated().Clone();
            c.Transform(p.World * inv);
            foreach (var f in c.Faces) f.MatId = MapMaterial(ms.Material(f.MatId));
            mesh.Append(c);
        }
        return mesh.Faces.Count > 0 ? mesh : null;
    }

    /// <summary>Copies a material into the map scene (once, by name) and returns its id there.</summary>
    int MapMaterial(GtaMaterial src)
    {
        var sc = MapMode ? Scene : otherMode?.Scene;
        if (sc == null || src == null) return 0;
        var key = src.Name + "|" + src.Shader + "|" + src.DiffuseTexture;
        if (mapMatKey.TryGetValue(key, out var id))
        {
            var m = sc.Material(id);
            if (m.Id == id && m.Name == src.Name && m.Shader == src.Shader && m.DiffuseTexture == src.DiffuseTexture) return id;
        }
        // materials already in the scene (a saved map reopened): indexed as the list grows instead of scanned each time
        if (!ReferenceEquals(mapMatScanScene, sc) || mapMatScanned > sc.Materials.Count) { mapMatScan.Clear(); mapMatScanned = 0; mapMatScanScene = sc; }
        for (; mapMatScanned < sc.Materials.Count; mapMatScanned++) { var m = sc.Materials[mapMatScanned]; mapMatScan.TryAdd(m.Name + "|" + m.Shader + "|" + m.DiffuseTexture, m.Id); }
        if (mapMatScan.TryGetValue(key, out var hid) && sc.Material(hid) is { } have && have.Id == hid && have.Name == src.Name && have.Shader == src.Shader && have.DiffuseTexture == src.DiffuseTexture)
        { mapMatKey[key] = have.Id; return have.Id; }
        var c = src.Clone(); c.Id = sc.NextMaterialId++; c.FromGame = true;
        sc.Materials.Add(c);
        mapMatKey[key] = c.Id;
        return c.Id;
    }

    readonly Dictionary<string, int> mapMatKey = new();
    readonly Dictionary<string, int> mapMatScan = new(); int mapMatScanned; Scene mapMatScanScene;
    bool texturesArrived;
    double archTakeTime = -10;

    void PumpArchetypes()
    {
        if (game == null && stream == null) return;
        // textures of newly loaded models: look up the missing names again, at most twice a second

        // finished loads: taken in batches 4 times a second (finding the objects of each model walks the whole streamed scene)
        Dictionary<string, (List<SceneObject> Scene, List<SceneObject> Far)> byKey = null;
        bool takeNow = ImGui.GetTime() - archTakeTime >= 0.25 || Scene.Objects.Count + backdrop.Count < 4000;
        if (takeNow) archTakeTime = ImGui.GetTime();
        for (int fi = archInFlight.Count - 1; fi >= 0 && takeNow; fi--)
        {
            var s = archInFlight[fi];
            if (s.State != 1 || s.Task == null) { archInFlight.RemoveAt(fi); continue; }
            if (!s.Task.IsCompleted) continue;
            archInFlight.RemoveAt(fi);
            archLoading--;
            if (s.Task.IsFaulted || s.Task.Result?.R == null) { s.State = 3; s.Error = s.Task.Exception?.GetBaseException().Message ?? s.Task.Result?.Why; s.Task = null; continue; }
            var ld = s.Task.Result; var r = ld.R; s.Task = null;
            var sf = renderer.Textures.SearchFolders;
            if (sf.Count < mapTexFolders.Count) { mapTexFolders.Clear(); mapTexFolders.UnionWith(sf); } // the list was reset (a file opened)
            foreach (var tex in r.TexturesWritten)
            {
                var d = Path.GetDirectoryName(tex);
                if (d != null && mapTexFolders.Add(d) && !sf.Contains(d)) sf.Add(d);
            }
            renderer.Textures.ForgetNames(r.TexturesWritten);
            var mesh = ld.Mesh;
            if (mesh == null || mesh.Faces.Count == 0) { s.State = 3; s.Error = "no geometry"; continue; }
            // file material numbers -> scene materials (cheap; the heavy work was done in the background)
            var ids = new int[Math.Max(1, r.Materials.Count)];
            for (int i = 0; i < r.Materials.Count; i++) ids[i] = MapMaterial(r.Materials[i]);
            if (r.Materials.Count == 0) ids[0] = 0;
            foreach (var f in mesh.Faces) f.MatId = ids[f.MatId];
            foreach (var g in ld.Groups) g.MaterialId = ids[Math.Clamp(g.MaterialId, 0, ids.Length - 1)];
            renderer.Prebuilt.AddOrUpdate(mesh, ld.Groups);
            if (ld.Gpu != null) renderer.PrebuiltGpu.AddOrUpdate(mesh, ld.Gpu);
            s.Mesh = mesh; s.Min = ld.Min; s.Max = ld.Max; s.State = 2; archReady++;
            string key = "map:" + s.Hash.ToString("X8");
            if (byKey == null)
            {
                // objects by archetype, built once for all the models finished this frame
                byKey = new Dictionary<string, (List<SceneObject> Scene, List<SceneObject> Far)>();
                foreach (var o in Scene.Objects) if (o.InstanceKey != null) { if (!byKey.TryGetValue(o.InstanceKey, out var l)) byKey[o.InstanceKey] = l = (new(), new()); l.Scene.Add(o); }
                foreach (var o in backdrop) if (o.InstanceKey != null) { if (!byKey.TryGetValue(o.InstanceKey, out var l)) byKey[o.InstanceKey] = l = (new(), new()); l.Far.Add(o); }
            }
            byKey.TryGetValue(key, out var mine);
            SceneObject lead = null;
            if (mine.Scene != null) foreach (var o in mine.Scene) { o.Mesh = mesh; lead ??= o; }
            lead?.Touch();
            int ver = lead?.Version ?? 0;
            if (mapDocs.Count > 0) backdropHiddenDirty = true; // its real size now decides whether it steps aside
            if (mine.Far != null) foreach (var o in mine.Far) { o.Mesh = mesh; if (lead == null) { o.Touch(); lead = o; ver = o.Version; } else o.Version = ver; }
            if (worldTpl.TryGetValue(s.Hash, out var wt)) { wt.Mesh = mesh; if (lead == null) { wt.Touch(); lead = wt; ver = wt.Version; } else wt.Version = ver; }
            s.Ver = lead != null ? ver : 0;
        }
        // reads still running (finished ones wait for the next batch without holding a reader)
        int running = 0; foreach (var x in archInFlight) if (x.Task != null && !x.Task.IsCompleted) running++;
        if (running >= ArchParallel) return;
        // nearest waiting archetypes first: the order is worked out 4 times a second, launches happen every frame
        if (ImGui.GetTime() - archScanTime >= 0.25)
        {
            archScanTime = ImGui.GetTime();
            var eye = Viewports[ActiveViewport].Camera.Eye;
            foreach (var s in archSlots.Values) { s.Priority = float.MaxValue; s.Seen = true; }
            foreach (var o in MapLive().Concat(backdrop))
            {
                if (o.InstanceKey == null || !mapSlotOf.TryGetValue(o.InstanceKey, out var s) || s.State != 0) continue;
                if (o.Id < 0 && (FarSteppedAside(o, eye) || !FarLodVisible(o, eye))) continue; // far model not shown at this distance
                s.Priority = MathF.Min(s.Priority, Vector3.DistanceSquared(o.Position, eye));
            }
            WorldPriorities(eye);
            archQueue.Clear(); archQueue.AddRange(archSlots.Values.Where(s => s.State == 0 && s.Priority < float.MaxValue).OrderBy(s => s.Priority));
            archQueuePos = 0;
        }
        while (running < ArchParallel && archQueuePos < archQueue.Count)
        {
            var s = archQueue[archQueuePos++];
            if (s.State != 0) continue;
            var local = stream?.ModelFile(s.Hash); // a model of the open stream folder wins over the game's
            if (s.Asset == null && local == null) s.Asset = game?.FindModel(s.Hash); // else from the cache or a drawable dictionary (background)
            if (local == null && game == null) { s.State = 3; s.Error = "not in the stream folder (load the game for its models)"; continue; }
            s.State = 1; archLoading++; running++; archInFlight.Add(s);
            var g = game; var a = local == null ? s.Asset : null; uint hash = s.Hash;
            s.Task = System.Threading.Tasks.Task.Run(() => LoadArchetype(g, hash, a, local));
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> yddLocks = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> yddDone = new();
    static string MeshCachePath(uint hash) => Path.Combine(MapMeshesDir, hash.ToString("X8") + ".pmc");

    /// <summary>
    /// One archetype's model, in the background: from the cache when read before, else its .ydr / .yft, else the drawable
    /// dictionary (.ydd) holding it — read once, every model inside goes to the cache (far LOD models come in big dictionaries).
    /// </summary>
    static ArchLoad LoadArchetype(GameFiles g, uint hash, GameAsset a, string local = null)
    {
        if (local != null)
        {
            // a file of the open stream folder: read as it is now (no cache: it changes while you work on it)
            var lo = new MoloMax.Core.IO.ImportOptions { Scale = 1, AutoSmoothAngle = 0 };
            var lr = MoloMax.Core.IO.ModelImporter.Import(local, lo);
            bool dict = local.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase);
            var lp = lr.Meshes.Where(m => m.GtaType != GtaObjectType.Collision && (!dict || m.DrawableHash == hash)).ToList();
            var lld = Merge(lr, lp);
            if (lld.Mesh.Faces.Count > 0) { (lld.Min, lld.Max) = lld.Mesh.Bounds(); lld.Groups = MoloMax.Core.Mesh.RenderMeshBuilder.Build(lld.Mesh); return lld; }
            // not in the folder's file after all (a .ydd of the folder that does not hold this model): the game's model
            if (g == null) { lld.Why = "no geometry in " + Path.GetFileName(local); return lld; }
            a ??= g.FindModel(hash);
        }
        uint asset = g?.AssetOf(hash) ?? hash; // several archetypes can draw one model (named differently)
        var ld = FromCache(hash) ?? (asset != hash ? FromCache(asset) : null) ?? new ArchLoad();
        if (ld.Mesh == null)
        {
            var opt = new MoloMax.Core.IO.ImportOptions { Scale = 1, AutoSmoothAngle = 0, SkipExistingTextures = true, OnlyUsedTextures = true };
            a ??= g.FindModel(hash);
            if (a != null)
            {
                var r = ReadGameModel(g, a, hash, opt);
                var parts = r.Meshes.Where(m => m.GtaType != GtaObjectType.Collision).ToList();
                ld = Merge(r, parts);
                if (ld.Mesh.Faces.Count > 0) try { MapMeshCache.Save(MeshCachePath(hash), ld.Mesh, ld.R.Materials); } catch { }
            }
            else
            {
                var dd = g.FindDictionaryOf(hash);
                if (dd == null) { ld.Why = g.WhyNoModel(hash); return ld; }
                lock (yddLocks.GetOrAdd(dd.Hash, _ => new object()))
                {
                    if ((FromCache(hash) ?? (asset != hash ? FromCache(asset) : null)) is { } again) ld = again;
                    else if (yddDone.ContainsKey(dd.Hash)) ld.Why = $"not in {dd.Name}.ydd";
                    else
                    {
                        yddDone[dd.Hash] = true; // read once, even when it fails
                        var r = ReadGameModel(g, dd, hash, opt);
                        foreach (var grp in r.Meshes.Where(m => m.GtaType != GtaObjectType.Collision && m.DrawableHash != 0).GroupBy(m => m.DrawableHash))
                        {
                            var one = Merge(r, grp.ToList());
                            if (one.Mesh.Faces.Count == 0) continue;
                            try { MapMeshCache.Save(MeshCachePath(grp.Key), one.Mesh, one.R.Materials); } catch { }
                            if (grp.Key == hash || grp.Key == asset) ld = one;
                        }
                        if (ld.Mesh == null) ld.Why = $"{dd.Name}.ydd read: {r.Meshes.Count} parts, hashes {string.Join(",", r.Meshes.Select(m => m.DrawableHash.ToString("X8")).Distinct().Take(4))}";
                    }
                }
            }
        }
        // GPU triangles built here, not on the UI thread
        if (ld.Mesh != null && ld.Mesh.Faces.Count > 0)
        {
            (ld.Min, ld.Max) = ld.Mesh.Bounds(); ld.Groups = MoloMax.Core.Mesh.RenderMeshBuilder.Build(ld.Mesh);
            try { ld.Gpu = MoloMax.Rendering.SceneRenderer.MakeBuffers(ld.Groups); } catch { ld.Gpu = null; } // and on the graphics card, here too
        }
        return ld;
    }

    /// <summary>A game model read in memory (archive → model, no file written then read back); the old way if that fails.</summary>
    static MoloMax.Core.IO.ImportResult ReadGameModel(GameFiles g, GameAsset a, uint hash, MoloMax.Core.IO.ImportOptions opt)
    {
        try
        {
            if (!MapDiskImport && g.ImportInMemory(a, hash, MapModelsDir, opt) is { } r) return r;
        }
        catch { }
        var file = Path.Combine(MapModelsDir, a.Name + a.Ext);
        if (!File.Exists(file)) file = g.Extract(a, MapModelsDir, hash);
        return MoloMax.Core.IO.ModelImporter.Import(file, opt);
    }

    /// <summary>Texture folders of the map models already in the search list (thousands: a list lookup per texture was slow).</summary>
    readonly HashSet<string> mapTexFolders = new(StringComparer.OrdinalIgnoreCase);

    static ArchLoad FromCache(uint hash)
    {
        var path = MeshCachePath(hash);
        if (!File.Exists(path) || MapMeshCache.Load(path) is not { } hit) return null;
        var ld = new ArchLoad { R = new MoloMax.Core.IO.ImportResult(), Mesh = hit.Mesh };
        ld.R.Materials.AddRange(hit.Materials);
        return ld;
    }

    /// <summary>The drawn parts joined into one mesh, with only the materials they use (renumbered from 0).</summary>
    static ArchLoad Merge(MoloMax.Core.IO.ImportResult r, List<MoloMax.Core.IO.ImportedMesh> parts)
    {
        var res = new ArchLoad { R = new MoloMax.Core.IO.ImportResult(), Mesh = new PolyMesh() };
        var remap = new Dictionary<int, int>();
        foreach (var im in parts)
        {
            var m = im.Mesh.Clone();
            foreach (var f in m.Faces)
            {
                int old = f.MatId >= 0 && f.MatId < r.Materials.Count ? f.MatId : 0;
                if (!remap.TryGetValue(old, out var nw))
                {
                    nw = remap[old] = res.R.Materials.Count;
                    res.R.Materials.Add(r.Materials.Count > 0 ? r.Materials[old] : GtaMaterial.CreateDefault(0, "imported", new Vector3(0.75f)));
                }
                f.MatId = nw;
            }
            res.Mesh.Append(m);
        }
        res.R.TexturesWritten.AddRange(r.TexturesWritten);
        return res;
    }

    // ------------------------------------------------------------------------------------------ documents, saving

    MapDocument ActiveDoc()
    {
        if (mapActive != null && mapDocs.TryGetValue(mapActive, out var d)) return d;
        var name = MapSafeName((modelScene?.Gta.ResourceName ?? "molo") + "_map");
        if (!mapDocs.TryGetValue(name, out d)) { d = MapDocument.New(name); mapDocs[name] = d; mapPinned.Add(name); if (!Scene.Folders.Contains(name)) Scene.Folders.Add(name); }
        mapActive = name;
        return d;
    }

    static string MapSafeName(string s) => new string((s ?? "map").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    /// <summary>
    /// Brings a document in line with the scene: moved / rotated / scaled objects, deleted ones, new ones (clones, placed props,
    /// objects moved into this ymap's folder). <paramref name="apply"/> false only tells whether something changed.
    /// </summary>
    bool SyncMapDoc(MapDocument d, bool apply)
    {
        bool changed = false;
        foreach (var e in d.Entities.ToList())
        {
            if (!objOf.TryGetValue(e, out var id)) continue; // LOD entities not shown: untouched
            var o = Scene.Find(id);
            if (o == null)
            {
                if (!mapDeleted.Contains(id)) continue;
                changed = true;
                if (apply) { d.Remove(e); objOf.Remove(e); entOf.Remove(id); }
                continue;
            }
            if (o.Folder != d.Name) continue; // moved to another ymap: handled from there
            var sc = new Vector3(o.Scale.X, o.Scale.X, o.Scale.Z);
            bool moved = Vector3.DistanceSquared(o.Position, e.Position) > 1e-8f || MathF.Abs(MathF.Abs(Quaternion.Dot(o.Rotation, e.Rotation)) - 1) > 1e-6f || Vector3.DistanceSquared(sc, e.Scale) > 1e-8f;
            bool renamed = o.Gta.MapArchetype != null && !string.Equals(o.Gta.MapArchetype, e.Archetype ?? e.ArchetypeHash.ToString("X8"), StringComparison.OrdinalIgnoreCase);
            if (moved || renamed)
            {
                changed = true;
                if (apply)
                {
                    e.Position = o.Position; e.Rotation = Quaternion.Normalize(o.Rotation); e.Scale = sc; d.Modified = true;
                    if (renamed) { e.Archetype = o.Gta.MapArchetype.ToLowerInvariant(); e.ArchetypeHash = MapDocument.Hash(e.Archetype); }
                }
            }
        }
        // entities moved out of this folder, and objects new to it
        foreach (var o in Scene.Objects)
        {
            if (o.Gta.MapArchetype == null) continue;
            bool mine = o.Folder == d.Name;
            if (entOf.TryGetValue(o.Id, out var de))
            {
                if (de.Doc == d && !mine)
                {
                    changed = true;
                    if (apply) { d.Remove(de.Ent); objOf.Remove(de.Ent); entOf.Remove(o.Id); }
                }
                continue;
            }
            if (!mine) continue;
            changed = true;
            if (apply)
            {
                var e = d.Add(o.Gta.MapArchetype, o.Position, o.Rotation, new Vector3(o.Scale.X, o.Scale.X, o.Scale.Z), 200);
                entOf[o.Id] = (d, e); objOf[e] = o.Id;
            }
        }
        if (apply && changed) d.Modified = true;
        return changed || d.Modified && d.Entities.Count > 0 && !d.FromGame && false;
    }

    string MapOutputDir()
    {
        var ms = modelScene;
        var root = string.IsNullOrWhiteSpace(ms?.Gta.OutputFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "FiveM") : ms.Gta.OutputFolder;
        return Path.Combine(root, MapSafeName((ms?.Gta.ResourceName ?? "molo") + "_map"));
    }

    /// <summary>Writes every changed / new .ymap into a FiveM map resource (stream folder + fxmanifest).</summary>
    public int SaveMaps(string dirOverride = null)
    {
        var dir = dirOverride ?? (this.stream != null ? StreamResourceDir() : MapOutputDir());
        // a stream folder is open (like a CodeWalker project): its files are saved where they are, new ones go into it
        var stream = dirOverride == null && this.stream != null ? this.stream.Folder : Path.Combine(dir, "stream");
        int n = 0; var errors = new List<string>();
        foreach (var d in mapDocs.Values.ToList())
        {
            SyncMapDoc(d, apply: true);
            if (!d.Modified) continue;
            if (d.Entities.Count == 0 && !d.FromGame) { d.Modified = false; continue; }
            try
            {
                var bytes = d.Save(h => archSlots.TryGetValue(h, out var s) ? (s.Min, s.Max) : null);
                // a .ymap read from disk (Open file, stream folder) is saved back where it was, like CodeWalker
                var target = dirOverride == null && d.FilePath != null && File.Exists(d.FilePath) ? d.FilePath : Path.Combine(stream, d.Name + ".ymap");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, bytes);
                d.FilePath = target;
                n++;
            }
            catch (Exception e) { errors.Add($"{d.Name}: {e.Message}"); }
        }
        n += SaveInteriors(stream, errors, inPlace: dirOverride == null && this.stream != null); // edited interiors: their .ytyp under the same name
        n += SaveMapBuild(dir, errors);     // what was modeled on the map: new models, MLO, their entities
        if (n > 0 && !File.Exists(Path.Combine(dir, "fxmanifest.lua")) && !File.Exists(Path.Combine(dir, "__resource.lua")))
        {
            File.WriteAllText(Path.Combine(dir, "fxmanifest.lua"),
                "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\n\nauthor 'Molo Modding'\ndescription 'Map made with Polymorph'\n");
        }
        mapDeleted.Clear();
        SetStatus(errors.Count > 0 ? "Map save: " + string.Join(" | ", errors) : n > 0 ? $"{n} .ymap saved in {dir}" : "No map change to save.");
        return n;
    }

    public void OpenYmapFile(string path)
    {
        try
        {
            var d = MapDocument.Load(File.ReadAllBytes(path), Path.GetFileName(path), StreamName);
            d.FilePath = path;
            mapPinned.Add(d.Name);
            AddMapDoc(d);
            mapActive = d.Name;
            FrameMapDoc(d);
            SetStatus($"{d.Name}: {d.Entities.Count} entities.");
        }
        catch (Exception e) { SetStatus("Could not open " + Path.GetFileName(path) + ": " + e.Message); }
    }

    void FrameMapDoc(MapDocument d)
    {
        if (d.Entities.Count == 0) return;
        var mn = d.Entities.Select(e => e.Position).Aggregate(Vector3.Min); var mx = d.Entities.Select(e => e.Position).Aggregate(Vector3.Max);
        var cam = Viewports[ActiveViewport].Camera;
        cam.Target = (mn + mx) * 0.5f; cam.Distance = Math.Clamp((mx - mn).Length() * 0.8f, 20, 1500);
    }

    public void GoTo(Vector3 p)
    {
        var cam = Viewports[ActiveViewport].Camera;
        if (cam.Ortho) { cam.Kind = ViewKind.Perspective; cam.Ortho = false; }
        cam.Target = p; cam.Distance = 70;
    }

    // ------------------------------------------------------------------------------------------ placement

    public void StartPlacement(string archetype)
    {
        if (!MapMode) SetMapMode(true);
        if (placeGhost >= 0) EndPlacement();
        placeArchetype = archetype.ToLowerInvariant();
        var folder = interiorActive != null && interiors.ContainsKey(interiorActive) ? interiorActive : ActiveDoc().Name;
        Undo.Record("Place " + placeArchetype, Scene);
        var hash = MapDocument.Hash(placeArchetype);
        var slot = Slot(hash, placeArchetype);
        var o = new SceneObject
        {
            Id = Scene.NextObjectId++, Name = placeArchetype, Mesh = slot.Mesh ?? Placeholder(slot), Position = MapFocus(),
            Folder = folder, InstanceKey = "map:" + hash.ToString("X8"), WireColour = new Vector3(1f, 0.8f, 0.3f),
        };
        o.Gta.MapArchetype = placeArchetype; o.Gta.Role = GtaRole.Ignore;
        if (PlaceRandomYaw) placeYaw = Random.Shared.NextSingle() * 360;
        Scene.Objects.Add(o);
        placeGhost = o.Id;
        Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged();
        SetStatus($"Placing {placeArchetype}: click to drop (again and again), wheel or R turns, Esc / right click stops.");
    }

    void EndPlacement()
    {
        if (placeGhost < 0) return;
        int id = placeGhost; placeGhost = -1;
        Scene.Objects.RemoveAll(o => o.Id == id);
        Selected.Remove(id);
        Undo.DropLast();
        SelectionChanged();
        SetStatus("Placement finished.");
    }

    /// <summary>Surface under the mouse among the visible map objects (skipping one id).</summary>
    bool MapPick(Viewport vp, Vector2 mouse, int skip, out Vector3 point, out Vector3 normal)
    {
        point = default; normal = Vector3.UnitZ;
        var ray = RayAt(vp, mouse);
        var best = GpuPickMap(vp, mouse, o => o.Id == skip);
        if (best.HasValue && best.Value.Face < 0) { point = best.Value.Point; return true; } // a far model: its point, the ground's up
        if (best.HasValue)
        {
            var b = best.Value; point = b.Point;
            var n = b.Object.Mesh.FaceNormal(b.Face);
            Matrix4x4.Invert(b.Object.World, out var inv);
            n = Vector3.TransformNormal(n, Matrix4x4.Transpose(inv));
            normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            if (Vector3.Dot(normal, ray.Direction) > 0) normal = -normal; // facing the camera
            return true;
        }
        // nothing under the mouse: the plane at the camera target height
        return Picking.RayGround(ray, MapFocus().Z, out point);
    }

    Quaternion SurfaceRotation(Vector3 n, float yawDeg)
    {
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDeg * MathF.PI / 180f);
        if (!PlaceAlign) return yaw;
        bool wall = MathF.Abs(n.Z) < 0.5f;
        if (PlaceAlignMode == 1 || !wall)
        {
            // local Z along the surface normal (lies on slopes, flat against walls)
            var axis = Vector3.Cross(Vector3.UnitZ, n); float s = axis.Length(); float c = Vector3.Dot(Vector3.UnitZ, n);
            var tilt = s < 1e-5f ? (c > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)) : Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, c));
            return PlaceAlignMode == 1 ? Quaternion.Normalize(tilt * yaw) : (n.Z > 0.9f ? yaw : Quaternion.Normalize(tilt * yaw));
        }
        // on a wall, standing: stays upright, its back against the wall (local -Y towards the wall)
        float heading = MathF.Atan2(-n.X, n.Y);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading + yawDeg * MathF.PI / 180f);
    }

    /// <summary>Viewport mouse while placing. True when it took the input.</summary>
    bool MapPlacementInput(Viewport hv, bool uiMouse)
    {
        if (placeGhost < 0) return false;
        var o = Scene.Find(placeGhost);
        if (o == null) { placeGhost = -1; return false; }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) || Input.Pressed(System.Windows.Forms.MouseButtons.Right)) { EndPlacement(); return true; }
        if (hv == null || uiMouse) return false;
        if (MathF.Abs(Input.Wheel) > 0 && !Input.Ctrl) { placeYaw += Input.Wheel * 15; }
        if (ImGui.IsKeyPressed(ImGuiKey.R, false)) placeYaw += 90;
        // feet on the surface: the model's lowest point goes onto the hit point (back against a wall)
        if (MapPick(hv, Input.Mouse, placeGhost, out var p, out var n)) PlaceGhostAt(p, n);
        if (Input.Pressed(System.Windows.Forms.MouseButtons.Left) && !Input.Alt) { ConfirmPlacement(); return true; }
        return true; // the mouse is busy placing: no selection clicks
    }

    /// <summary>Puts the prop being placed on a surface point (mouse hit or given).</summary>
    void PlaceGhostAt(Vector3 p, Vector3 n)
    {
        var o = Scene.Find(placeGhost); if (o == null) return;
        o.Rotation = SurfaceRotation(n, placeYaw);
        var (mn, _) = o.LocalBounds();
        if (PlaceAlign && MathF.Abs(n.Z) < 0.5f && PlaceAlignMode == 0) o.Position = p + n * MathF.Max(0, -mn.Y);
        else o.Position = p + Vector3.Transform(new Vector3(0, 0, -mn.Z), o.Rotation);
    }

    /// <summary>Keeps the prop where it is and starts placing the next one.</summary>
    int ConfirmPlacement()
    {
        if (placeGhost < 0) return -1;
        var keep = placeGhost; placeGhost = -1;
        Dirty = true;
        var arch = placeArchetype; float yaw = placeYaw;
        StartPlacement(arch);
        if (!PlaceRandomYaw) placeYaw = yaw;
        SetStatus($"{arch} placed. Click again for another, Esc to stop.");
        return keep;
    }

    /// <summary>Drops the selected objects onto whatever is under them.</summary>
    void DropToGround()
    {
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        Do("Drop to ground", () =>
        {
            foreach (var o in objs)
            {
                var (mn, mx) = o.FastWorldBounds();
                var start = new Vector3(o.Position.X, o.Position.Y, mx.Z + 0.5f);
                var ray = new Ray(start, -Vector3.UnitZ);
                var best = Picking.PickNearest(Scene.Objects, ray, t => t == o || objs.Contains(t) || mapCulled.Contains(t.Id));
                if (best.HasValue) o.Position += new Vector3(0, 0, best.Value.Point.Z - mn.Z);
            }
        });
    }

    // ------------------------------------------------------------------------------------------ panel

    static readonly (string Name, Vector3 P)[] MapPlaces =
    {
        ("Legion Square", new(195, -934, 30)), ("Maze Bank", new(-75, -818, 45)), ("Vinewood sign", new(711, 1198, 348)), ("Del Perro pier", new(-1650, -1030, 13)),
        ("Airport", new(-1037, -2737, 20)), ("Mission Row PD", new(440, -982, 30)), ("Sandy Shores", new(1850, 3690, 34)), ("Paleto Bay", new(-275, 6230, 31)),
        ("Mount Chiliad", new(501, 5604, 797)), ("Port", new(1200, -3000, 6)), ("Grove Street", new(-50, -1750, 29)), ("Bolingbroke", new(1690, 2600, 45)),
    };

    void MapProps()
    {
        if (!MapMode) { Ui.TextWrapped("Switch to MAP (tab at the top) to open GTA maps and place props."); if (ImGui.Button("Go to MAP mode", new Vector2(-1, 28))) SetMapMode(true); return; }
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        // ---- world
        if (Ui.Header("World", Open))
        {
            if (game == null)
            {
                bool busy = gameTask is { IsCompleted: false };
                Ui.TextWrapped(busy ? gameStatus : "Load the GTA V files to see the world (folder set in Game Files).");
                if (!busy && ImGui.Button("Load the game", new Vector2(-1, 26)))
                {
                    if (GameFiles.LooksLikeGta(Settings.GtaFolder, out _)) StartGameLoad(Settings.GtaFolder); else OpenGameBrowser();
                }
                if (gameError != null) Ui.TextColored(new Vector4(1, 0.4f, 0.3f, 1), gameError);
            }
            else if (world == null) { Ui.TextDisabled(worldStatus == "" ? "Indexing the maps..." : worldStatus); ImGui.ProgressBar(worldProgress, new Vector2(-1, 0)); }
            else
            {
                FarWorldPanel();
                // counted twice a second: tens of thousands of models once the far LOD level has been read
                if (ImGui.GetTime() - slotCountTime > 0.5)
                {
                    slotCountTime = ImGui.GetTime(); slotLoading = slotWaiting = slotReady = 0;
                    foreach (var sl in archSlots.Values) { if (sl.State == 1) slotLoading++; else if (sl.State == 2) slotReady++; else if (sl.State == 0 && sl.Priority < float.MaxValue) slotWaiting++; }
                }
                Ui.TextDisabled($"{mapDocs.Count} map(s), {entOf.Count:N0} entities, {slotReady} models" + (slotLoading + slotWaiting > 0 ? $"  ·  loading {slotLoading + slotWaiting}" : "") + (mapLoadTask != null ? "  ·  reading a map" : ""));
            }
            Ui.Label("Go to", Theme.TextDim);
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            for (int i = 0; i < MapPlaces.Length; i++)
            {
                if (i % 3 != 0) ImGui.SameLine(0, 4);
                if (ImGui.Button(MapPlaces[i].Name, new Vector2(w3, 0))) GoTo(MapPlaces[i].P);
            }
            var t = MapFocus();
            Ui.TextDisabled($"camera on {t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}");
            ImGui.SameLine();
            if (ImGui.SmallButton("copy")) ImGui.SetClipboardText($"vector3({t.X:0.00}, {t.Y:0.00}, {t.Z:0.00})");
        }
        // ---- maps
        if (Ui.Header("Maps (.ymap)", Open))
        {
            StreamFolderPanel(w2);
            if (ImGui.Button("New map", new Vector2(w2, 0))) { var d = MapDocument.New(MapSafeName(NextMapName())); mapDocs[d.Name] = d; mapPinned.Add(d.Name); Scene.Folders.Add(d.Name); mapActive = d.Name; }
            ImGui.SameLine(0, 4);
            if (ImGui.Button("Open file...", new Vector2(w2, 0)))
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog { Filter = "GTA map (*.ymap)|*.ymap", Title = "Open a .ymap" };
                if (ShowFileDialog(dlg) == System.Windows.Forms.DialogResult.OK) OpenYmapFile(dlg.FileName);
                Input.ReleaseAllKeys();
            }
            if (world != null)
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##ymsearch", "Open a game map by name (e.g. dt1_02)...", ref mapYmapSearch, 64);
                if (mapYmapSearch.Length >= 2)
                {
                    ImGui.BeginChild("##ymres", new Vector2(-1, 110), ImGuiChildFlags.Borders);
                    foreach (var y in world.Ymaps.Where(y => y.Name.Contains(mapYmapSearch, StringComparison.OrdinalIgnoreCase)).Take(60))
                    {
                        if (ImGui.Selectable($"{y.Name}   {y.Entities} ent." + (y.LodOnly ? "  (LOD)" : "") + (y.Interior ? "  (interior)" : "")))
                        {
                            if (mapDocs.TryGetValue(y.Name, out var open)) { mapPinned.Add(y.Name); FrameMapDoc(open); }
                            else { StartMapLoad(y, true); GoTo((y.Min + y.Max) * 0.5f); }
                            mapActive = y.Name;
                        }
                    }
                    ImGui.EndChild();
                }
            }
            // which maps have unsaved edits: checked once a second (walking every entity each frame would cost frames)
            if (ImGui.GetTime() - mapChangedTime > 1) { mapChangedTime = ImGui.GetTime(); mapChanged.Clear(); mapChanged.UnionWith(ChangedMapDocs()); }
            Ui.Label("Open maps (folders of the Scene Explorer)", Theme.TextDim);
            ImGui.BeginChild("##ymopen", new Vector2(-1, MathF.Min(8, Math.Max(2, mapDocs.Count)) * ImGui.GetTextLineHeightWithSpacing() + 8), ImGuiChildFlags.Borders);
            foreach (var d in mapDocs.Values.OrderByDescending(d => mapPinned.Contains(d.Name)).ThenBy(d => d.Name).ToList())
            {
                bool changed = d.Modified || mapChanged.Contains(d.Name);
                ImGui.PushID(d.Name);
                if (ImGui.RadioButton("##act", mapActive == d.Name && interiorActive == null)) { mapActive = d.Name; interiorActive = null; }
                Theme.Tip("New props go into this map");
                ImGui.SameLine();
                if (ImGui.Selectable($"{d.Name}{(changed ? " *" : "")}   {d.Entities.Count} ent.{(mapPinned.Contains(d.Name) ? "" : "  (streamed)")}", false)) FrameMapDoc(d);
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("New props go here")) { mapActive = d.Name; interiorActive = null; }
                    if (ImGui.MenuItem("Keep loaded", null, mapPinned.Contains(d.Name))) { if (!mapPinned.Remove(d.Name)) mapPinned.Add(d.Name); }
                    if (ImGui.MenuItem("Save this .ymap", "Ctrl+S")) SaveMapDoc(d);
                    if (ImGui.MenuItem("Save this .ymap as...")) SaveMapDoc(d, saveAs: true);
                    ImGui.Separator();
                    if (ImGui.MenuItem("Select its entities")) { Selected.Clear(); foreach (var o in Scene.Objects) if (o.Folder == d.Name) Selected.Add(o.Id); SelectionChanged(); }
                    if (ImGui.MenuItem("Close (unsaved changes are lost)")) UnloadMapDoc(d);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
            InteriorsPanel();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.95f, 0.42f, 0.11f, 1)); ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
            MapBuildPanel();
            ImGui.Separator();
            if (SelectedMapDoc() is { } sd)
            {
                if (ImGui.Button($"Save {sd.Name}.ymap" + (sd.FilePath != null && File.Exists(sd.FilePath) ? "" : "..."), new Vector2(-1, 26))) SaveMapDoc(sd);
                Theme.Tip(sd.FilePath != null && File.Exists(sd.FilePath) ? "Writes it where it was opened from: " + sd.FilePath : "A game map: pick where to write it (keep its name so FiveM uses yours).");
            }
            if (ImGui.Button(stream != null ? "SAVE (INTO THE STREAM FOLDER)" : "SAVE MAP RESOURCE", new Vector2(-1, 30))) SaveMaps();
            ImGui.PopStyleColor(2);
            Theme.Tip("Writes the changed / new .ymap files (and edited interiors' .ytyp) into a FiveM resource: stream folder + fxmanifest (this_is_a_map). A game file keeps its name, so FiveM uses yours instead.");
            Ui.TextDisabled(stream != null ? stream.Folder : MapOutputDir());
        }
        // ---- place
        if (Ui.Header("Place props", Open))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##plsearch", "Search a prop (game or your MODEL scene)...", ref placeSearch, 64);
            string key = placeSearch + "|" + (game?.Models.Count ?? 0);
            if (key != placeResultsKey)
            {
                placeResultsKey = key; placeResults.Clear();
                var ms = modelScene;
                if (ms != null)
                    foreach (var o in ms.Objects.Where(o => o.GtaType == GtaObjectType.Model || (o.GtaType == GtaObjectType.Drawable && ms.Parent(o) == null && o.Mesh.Faces.Count > 0)))
                    {
                        var n = string.IsNullOrEmpty(o.Gta.ModelName) ? o.Name.ToLowerInvariant() : o.Gta.ModelName;
                        if (placeSearch.Length == 0 || n.Contains(placeSearch, StringComparison.OrdinalIgnoreCase)) placeResults.Add("[mine] " + n);
                    }
                if (game != null && placeSearch.Length >= 2) placeResults.AddRange(game.Search(placeSearch, true, true, false).Take(200).Select(a => a.Name));
            }
            ImGui.BeginChild("##plres", new Vector2(-1, 150), ImGuiChildFlags.Borders);
            foreach (var r in placeResults)
            {
                bool proj = r.StartsWith("[mine] ");
                string an = proj ? r[7..] : r;
                if (ImGui.Selectable(r + "##pl", placeArchetype == an && placeGhost >= 0, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) StartPlacement(an);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(proj ? "Your model (MODEL scene). Double-click to place it." : "Double-click to place it, click the world to drop it.");
            }
            ImGui.EndChild();
            Ui.TextDisabled("[mine] = your models (MODEL scene). Double-click to place, then click on the ground or a wall.");
            ImGui.Checkbox("Stick to the surface", ref PlaceAlign);
            Theme.Tip("Props follow the surface under the mouse: standing on the ground, back against walls.");
            if (PlaceAlign)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.Combo("##alm", ref PlaceAlignMode, new[] { "Stand (upright)", "Follow the normal" }, 2);
            }
            ImGui.Checkbox("Random rotation", ref PlaceRandomYaw);
            if (placeGhost >= 0 && ImGui.Button("Stop placing (Esc)", new Vector2(-1, 0))) EndPlacement();
            if (ImGui.Button("Drop selection to the ground", new Vector2(-1, 0))) DropToGround();
        }
        // ---- selected entity
        var cur = Current;
        if (cur != null && cur.Gta.MapArchetype != null && Ui.Header("Entity", Open))
        {
            entOf.TryGetValue(cur.Id, out var de);
            string arch = cur.Gta.MapArchetype;
            ImGui.SetNextItemWidth(-90);
            if (ImGui.InputText("Archetype", ref arch, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                var a = arch.Trim().ToLowerInvariant();
                Do("Change archetype", () =>
                {
                    var h = MapDocument.Hash(a);
                    var slot = Slot(h, a);
                    foreach (var o in SelObjects()) { o.Gta.MapArchetype = a; o.Name = a; o.InstanceKey = "map:" + h.ToString("X8"); o.Mesh = slot.Mesh ?? Placeholder(slot); o.Touch(); }
                });
            }
            Ui.TextDisabled($"in {cur.Folder}" + (de.Ent != null ? $"  ·  lod {de.Ent.LodDist:0} m  ·  flags {de.Ent.Flags}" + (de.Ent.IsMlo ? "  ·  interior" : "") : "  ·  new"));
            if (de.Ent != null)
            {
                float ld = de.Ent.LodDist; ImGui.SetNextItemWidth(-90);
                if (ImGui.DragFloat("Lod distance", ref ld, 1, 5, 3000, "%.0f m")) { de.Ent.LodDist = ld; de.Doc.Modified = true; }
            }
            if (archSlots.TryGetValue(MapDocument.Hash(cur.Gta.MapArchetype), out var sl) && sl.State == 3)
                Ui.TextColored(new Vector4(1, 0.6f, 0.3f, 1), "Model not shown (" + (sl.Error ?? "not found in the game files") + "): the entity is kept as is.");
            if (de.Ent != null && de.Ent.IsMlo)
            {
                if (ImGui.Button("Edit this interior (rooms)", new Vector2(-1, 0))) EditInterior(cur);
                Theme.Tip("Brings the objects of this interior's rooms into the scene. Move, delete or add props, then SAVE MAP RESOURCE writes its .ytyp.");
            }
            if (intEntOf.TryGetValue(cur.Id, out var ie))
                Ui.TextDisabled($"interior object  ·  room {(ie.Ent.Room > 0 && ie.Ent.Room < ie.In.Doc.Rooms.Count ? ie.In.Doc.Rooms[ie.Ent.Room].Name : "limbo")}");
            var ms = modelScene;
            int nsel = SelectedMapModels().Count;
            if (ms != null && ImGui.Button(nsel > 1 ? $"Send the {nsel} selected models to MODEL" : "Send this model to MODEL", new Vector2(-1, 0))) SendSelectedToModel();
            Theme.Tip("Opens MODEL mode with the game model(s) imported, laid out as on the map. Also in the right-click menu.");
        }
    }

    string NextMapName()
    {
        var b = (modelScene?.Gta.ResourceName ?? "molo") + "_map";
        for (int i = 1; ; i++) { var n = i == 1 ? b : $"{b}{i}"; if (!mapDocs.ContainsKey(MapSafeName(n))) return n; }
    }
}
