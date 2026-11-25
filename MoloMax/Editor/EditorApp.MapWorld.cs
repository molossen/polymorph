using System.Collections.Concurrent;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// The whole GTA map shown at once with its far (LOD / SLOD) models: light, read-only, outside the scene.
/// A click on it loads the real (.ymap HD) maps of that area as editable entities; the far models there step aside.
/// </summary>
public sealed partial class EditorApp
{
    readonly List<SceneObject> backdrop = new();
    readonly HashSet<int> backdropHidden = new();
    readonly Queue<YmapInfo> mapLoadQueue = new();
    readonly ConcurrentQueue<MapDocument> backdropIn = new();
    System.Threading.Tasks.Task backdropTask;
    System.Threading.CancellationTokenSource backdropCancel;
    /// <summary>Levels of detail switched on, by game level: 0 HD, 1 LOD, 2 SLOD1, 3 SLOD2, 4 SLOD3, 5 orphan HD.
    /// HD / orphan HD are the editable entities of the loaded maps; 1..4 are the far world. Each level is a checkbox.</summary>
    public readonly bool[] MapLevelOn = { true, false, true, true, true, true };
    public static readonly string[] MapLevelNames = { "HD", "LOD", "SLOD1", "SLOD2", "SLOD3", "HD (orphan)" };
    int backdropLevel = -1, backdropNextId = -1_000_000, backdropMaps, backdropTotal;
    public bool ShowFarWorld = true;
    /// <summary>Finest far level to read (the floor of the LOD tree): the lowest level switched on among LOD..SLOD3. Coarser
    /// levels are read too and each entity shows at its own distance, like the game: no holes where a level is missing.
    /// Switching a level off only filters (no reload); switching a finer one on reads the extra maps.</summary>
    int BackdropLodLevel { get { for (int l = 1; l <= TopLod; l++) if (MapLevelOn[l]) return l; return TopLod + 1; } }
    const int TopLod = 4; // SLOD3
    readonly int[] farLevelCount = new int[6]; double farCountTime = -10;

    /// <summary>Switches a level of detail on / off. HD applies to the loaded maps' entities at once.</summary>
    public void SetMapLevel(int level, bool on)
    {
        if (level < 0 || level > 5 || MapLevelOn[level] == on) return;
        MapLevelOn[level] = on;
        if (level is 0 or 5)
            foreach (var o in Scene.Objects)
                if (entOf.TryGetValue(o.Id, out var de) && de.Ent.LodLevel == level) o.Hidden = !on;
        farCountTime = -10;
    }

    /// <summary>LOD tree data of a far model: shown between its children's distance and its own (squared metres).</summary>
    sealed class FarInfo
    {
        public float LodDist2, ChildLodDist2; public int Level;
        /// <summary>Levels of its children (bit per level: a tree can skip levels, e.g. SLOD3 over LOD) and of its parent (-1: none).</summary>
        public int KidMask, ParentLevel = -1;
        public bool HasKids => KidMask != 0; public bool HasParent => ParentLevel >= 0;
    }
    readonly Dictionary<int, FarInfo> farInfo = new();
    // (map name hash, entity index) -> far model id, and children still looking for their parent
    readonly Dictionary<(uint, int), int> farByIndex = new();
    readonly List<(int Child, uint ParentMap, int ParentIndex)> farLinks = new();

    /// <summary>Is this far model the one to show at this distance (its children not close enough, its parent not far enough)?</summary>
    /// <summary>Far levels both read and switched on (bit per level): only those can take over from a parent.</summary>
    int LiveFarMask
    {
        get { int m = 0; for (int l = Math.Max(backdropLevel, 1); l <= TopLod; l++) if (MapLevelOn[l]) m |= 1 << l; return backdropLevel < 0 ? 0 : m; }
    }

    bool FarLodVisible(SceneObject o, Vector3 eye)
    {
        if (!farInfo.TryGetValue(o.Id, out var fi)) return true;
        if (!MapLevelOn[fi.Level]) return false;
        float d2 = Vector3.DistanceSquared(o.Position, eye);
        // a level switched off is treated as absent: its parent / children stay on screen at every distance
        if ((fi.KidMask & LiveFarMask) != 0 && d2 < fi.ChildLodDist2) return false;                         // its finer children (read and on) take over
        if (fi.ParentLevel >= 0 && MapLevelOn[fi.ParentLevel] && d2 > fi.LodDist2) return false;            // its coarser parent takes over
        return true;
    }

    /// <summary>Links children to parents among the far models read so far (maps arrive in any order).</summary>
    void ResolveFarLinks()
    {
        for (int i = farLinks.Count - 1; i >= 0; i--)
        {
            var (c, pm, pi) = farLinks[i];
            if (!farByIndex.TryGetValue((pm, pi), out var pid)) continue;
            if (farInfo.TryGetValue(pid, out var pf) && farInfo.TryGetValue(c, out var cf)) { pf.KidMask |= 1 << cf.Level; cf.ParentLevel = pf.Level; }
            farLinks.RemoveAt(i);
        }
    }

    void StartBackdrop()
    {
        backdropCancel?.Cancel();
        ClearBackdrop();
        if (world == null || !ShowFarWorld) return;
        backdropLevel = BackdropLodLevel;
        int lvl = backdropLevel; var w = world; var focus = MapFocus();
        var list = w.Ymaps.Where(y => y.Levels != null && Enumerable.Range(lvl, TopLod - lvl + 1).Any(l => y.Levels[l] > 0))
                          .OrderBy(y => Vector3.DistanceSquared(Vector3.Clamp(focus, y.Min, y.Max), focus)).ToList();
        backdropTotal = list.Count; backdropMaps = 0;
        var cts = backdropCancel = new System.Threading.CancellationTokenSource();
        backdropTask = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var y in list)
            {
                if (cts.IsCancellationRequested) return;
                try { backdropIn.Enqueue(w.Open(y)); } catch { }
            }
        });
    }

    /// <summary>Drops the far models finer than <paramref name="floor"/> (their level was switched off and nothing finer is on).</summary>
    void PruneBackdrop(int floor)
    {
        var gone = new HashSet<int>();
        foreach (var kv in farInfo) if (kv.Value.Level < floor) gone.Add(kv.Key);
        backdrop.RemoveAll(o => gone.Contains(o.Id));
        foreach (var id in gone) { farInfo.Remove(id); backdropHidden.Remove(id); }
        foreach (var k in farByIndex.Where(kv => gone.Contains(kv.Value)).Select(kv => kv.Key).ToList()) farByIndex.Remove(k);
        farLinks.RemoveAll(l => gone.Contains(l.Child));
        renderer.KeepInstanceKeys = new HashSet<string>(backdrop.Select(o => o.InstanceKey));
        backdropLevel = floor; farCountTime = -10;
    }

    void ClearBackdrop()
    {
        backdrop.Clear(); backdropHidden.Clear(); farInfo.Clear(); farByIndex.Clear(); farLinks.Clear();
        while (backdropIn.TryDequeue(out _)) { }
        renderer.KeepInstanceKeys = new HashSet<string>();
        backdropLevel = -1; backdropMaps = 0;
    }

    void PumpBackdrop()
    {
        if (world == null) return;
        if (ShowFarWorld && (backdropLevel < 0 || BackdropLodLevel < backdropLevel)) StartBackdrop(); // a finer level switched on: read its maps
        else if (ShowFarWorld && backdropLevel == 1 && BackdropLodLevel > 1) PruneBackdrop(Math.Min(BackdropLodLevel, 2)); // the heavy LOD level switched off: let its models go (SLODs stay, they are light)
        if (!ShowFarWorld && backdrop.Count > 0) { backdropCancel?.Cancel(); ClearBackdrop(); }
        int lvl = backdropLevel;
        for (int n = 0; n < 8 && backdropIn.TryDequeue(out var d); n++)
        {
            backdropMaps++; backdropHiddenDirty = true;
            foreach (var e in d.Entities)
            {
                if (e.LodLevel < lvl || e.LodLevel > TopLod) continue;
                var slot = Slot(e.ArchetypeHash, e.Archetype);
                var o = new SceneObject
                {
                    Id = backdropNextId--, Name = e.DisplayName, Mesh = slot.Mesh ?? Placeholder(slot),
                    Position = e.Position, Rotation = e.Rotation, Scale = new Vector3(e.Scale.X, e.Scale.X, e.Scale.Z),
                    InstanceKey = "map:" + e.ArchetypeHash.ToString("X8"), Frozen = true,
                };
                backdrop.Add(o);
                renderer.KeepInstanceKeys.Add(o.InstanceKey);
                farInfo[o.Id] = new FarInfo
                {
                    Level = e.LodLevel, LodDist2 = e.LodDist > 0 ? e.LodDist * e.LodDist : float.MaxValue, ChildLodDist2 = e.ChildLodDist * e.ChildLodDist,
                };
                if (e.Index >= 0) farByIndex[(d.NameHash, e.Index)] = o.Id;
                if (e.ParentIndex >= 0 && d.ParentHash != 0) farLinks.Add((o.Id, d.ParentHash, e.ParentIndex));
            }
            ResolveFarLinks();
        }
        if (backdropMaps > 0 && backdropIn.IsEmpty && backdropHiddenDirty && ImGui.GetTime() - backdropHiddenTime > 1) { backdropHiddenTime = ImGui.GetTime(); UpdateBackdropHidden(); }
    }
    bool backdropHiddenDirty = true; double backdropHiddenTime = -10;

    /// <summary>Far models inside a loaded editable map step aside (no double geometry).</summary>
    void UpdateBackdropHidden()
    {
        backdropHidden.Clear(); backdropHiddenDirty = false;
        // a loaded map takes over only once its models are on screen: until then the far models stay (no empty ground while streaming)
        var docs = mapDocs.Values.Where(d => d.Entities.Count > 0 && d.FromGame).ToList();
        var ready = docs.Where(MapDocModelsReady).ToList();
        var boxes = ready.Select(d => (d.EntitiesMin, d.EntitiesMax)).ToList();
        if (boxes.Count < docs.Count) backdropHiddenDirty = true; // look again when more models arrive
        if (boxes.Count == 0) return;
        // the loaded models show up to their own draw distance; past it the far models stay (no bare ground far away)
        var dists = ready.SelectMany(d => d.Entities).Where(e => e.IsHd).Select(e => MathF.Max(e.LodDist, 30) * 1.15f).OrderBy(x => x).ToList();
        float r = dists.Count > 0 ? dists[(int)(dists.Count * 0.2f)] : 120; // low: a far model stays where most loaded models would already be culled (no holes)
        backdropHdRange2 = r * r;
        foreach (var o in backdrop)
        {
            // finest far models whose middle is in the loaded area; coarser ones only when they lie inside it (big SLODs
            // reaching past it stay, else holes around)
            var (mn, mx) = o.FastWorldBounds(); var c = (mn + mx) * 0.5f;
            bool floor = !farInfo.TryGetValue(o.Id, out var fi) || fi.Level <= backdropLevel || (fi.KidMask & LiveFarMask) == 0;
            foreach (var (a, b) in boxes)
            {
                bool hit = floor ? c.X >= a.X && c.X <= b.X && c.Y >= a.Y && c.Y <= b.Y
                                 : mn.X >= a.X - 25 && mx.X <= b.X + 25 && mn.Y >= a.Y - 25 && mx.Y <= b.Y + 25;
                if (hit) { backdropHidden.Add(o.Id); break; }
            }
        }
    }

    /// <summary>Has this loaded map got its models on screen, so that the far models of its area can step aside?
    /// Yes when 90 % of its models (counted per model, not per entity: a hundred trees of two models must not outvote the
    /// big ground piece) are read, or once the model streaming has settled for a moment (models nobody looks at never load).</summary>
    bool MapDocModelsReady(MoloMax.Core.Gta.Map.MapDocument d)
    {
        double now = ImGui.GetTime();
        if (!docSeenAt.TryGetValue(d.Name, out var t0)) docSeenAt[d.Name] = t0 = now;
        var seen = new HashSet<uint>(); int ready = 0;
        foreach (var e in d.Entities)
        {
            if (!seen.Add(e.ArchetypeHash)) continue;
            if (!archSlots.TryGetValue(e.ArchetypeHash, out var s) || s.State >= 2) ready++;
        }
        if (ready >= seen.Count * 0.9f) return true;
        bool idle = archLoading == 0 && archQueuePos >= archQueue.Count;
        if (!idle) archBusyAt = now;
        return now - t0 > 2 && now - archBusyAt > 1.5;
    }
    float backdropHdRange2 = 150 * 150;
    /// <summary>Far model in a loaded area, close enough for the loaded models to be drawn instead of it.</summary>
    bool FarSteppedAside(SceneObject o, Vector3 eye) => backdropHidden.Contains(o.Id) && Vector3.DistanceSquared(o.Position, eye) < backdropHdRange2;
    readonly Dictionary<string, double> docSeenAt = new(StringComparer.OrdinalIgnoreCase);
    double archBusyAt;

    /// <summary>Loads the editable maps (HD) around a point of the far world.</summary>
    public int LoadAreaAt(Vector3 p, float margin = 15)
    {
        if (world == null) return 0;
        int n = 0;
        foreach (var y in world.Ymaps.Where(y => !y.LodOnly && y.HdEntities > 0 && !mapDocs.ContainsKey(y.Name) && !mapLoadQueue.Contains(y)
                                              && p.X >= y.Min.X - margin && p.X <= y.Max.X + margin && p.Y >= y.Min.Y - margin && p.Y <= y.Max.Y + margin)
                                    .OrderBy(y => (y.Max - y.Min).X * (y.Max - y.Min).Y).Take(24))
        { mapLoadQueue.Enqueue(y); n++; }
        backdropHiddenDirty = true;
        SetStatus(n > 0 ? $"Loading {n} map(s) of this area for editing..." : "This area is already loaded.");
        return n;
    }

    /// <summary>Map mode click: on the far world it loads that area; on an editable entity the normal selection goes on.</summary>
    bool MapClick(Viewport hv)
    {
        if (world == null || backdrop.Count == 0) return false;
        var ray = RayAt(hv, Input.Mouse);
        var near = Picking.PickNearest(Scene.Objects, ray, o => o.Frozen || mapCulled.Contains(o.Id));
        var eye = hv.Camera.Eye;
        var far = Picking.PickNearest(backdrop, ray, o => FarSteppedAside(o, eye) || !FarLodVisible(o, eye));
        if (!far.HasValue || near.HasValue && near.Value.Distance <= far.Value.Distance + 0.5f) return false;
        LoadAreaAt(far.Value.Point);
        return true;
    }

    void FarWorldPanel()
    {
        if (world == null) return;
        if (ImGui.Checkbox("Show the whole map (far models)", ref ShowFarWorld)) { }
        Theme.Tip("The full GTA map with its light far models. Click on it to load that area for editing.");
        if (ShowFarWorld)
            Ui.TextDisabled($"{backdrop.Count:N0} far models" + (backdropMaps < backdropTotal ? $"  ·  reading {backdropMaps}/{backdropTotal} maps" : ""));
        // levels of detail, one checkbox each (like the game's LOD tree: HD close, LOD, SLOD1..3 far)
        if (ImGui.GetTime() - farCountTime > 1)
        {
            farCountTime = ImGui.GetTime();
            Array.Clear(farLevelCount);
            foreach (var fi in farInfo.Values) farLevelCount[fi.Level]++;
            foreach (var de in entOf.Values) if (de.Ent.LodLevel is 0 or 5) farLevelCount[de.Ent.LodLevel]++;
        }
        if (ImGui.TreeNodeEx("Levels of detail", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            string[] tips =
            {
                "The real entities (what the player sees up close). They load by themselves around the camera when you zoom in (see below). Off: hide them to look at the far levels alone.",
                "LOD: first far level, many models (heavier). On: read the LOD maps and show them where the player would see them.",
                "SLOD1: medium far level, the one the game shows a few hundred metres away.",
                "SLOD2: far level, whole blocks in one model.",
                "SLOD3: the farthest level, the city seen from the sky.",
                "Orphan HD: entities without a LOD tree (props, small details) in the loaded maps.",
            };
            int[] order = { 0, 5, 1, 2, 3, 4 };
            foreach (int l in order)
            {
                bool on = MapLevelOn[l];
                if (ImGui.Checkbox($"{MapLevelNames[l]}##lod{l}", ref on)) SetMapLevel(l, on);
                Theme.Tip(tips[l]);
                ImGui.SameLine(130);
                Ui.TextDisabled(l is 0 or 5 ? (farLevelCount[l] == 0 && MapStream && !HdStreamingNow ? "zoom in to load" : $"{farLevelCount[l]:N0} entities") : farLevelCount[l] > 0 ? $"{farLevelCount[l]:N0} models" : (l < backdropLevel ? "not read" : ""));
            }
            if (ImGui.SmallButton("Far only")) { SetMapLevel(0, false); SetMapLevel(5, false); for (int l = 1; l <= 4; l++) SetMapLevel(l, true); }
            Theme.Tip("Hide the editable entities, show every far level");
            ImGui.SameLine();
            if (ImGui.SmallButton("Light")) { SetMapLevel(0, true); SetMapLevel(5, true); SetMapLevel(1, false); SetMapLevel(2, false); SetMapLevel(3, true); SetMapLevel(4, true); }
            Theme.Tip("SLOD2 + SLOD3 only: the lightest far world");
            ImGui.SameLine();
            if (ImGui.SmallButton("All")) for (int l = 0; l <= 5; l++) SetMapLevel(l, true);
            ImGui.SameLine();
            if (ImGui.SmallButton("None")) for (int l = 0; l <= 5; l++) SetMapLevel(l, false);
            Theme.Tip("Hide the whole game map: only your own objects (custom map, what you model) stay");
            ImGui.TreePop();
        }
        Ui.TextDisabled("Click the map to load that area (its .ymap) for editing.");
        ImGui.Checkbox("Load HD around the camera", ref MapStream);
        Theme.Tip("Like the game: when the camera is close, the real models (HD) around it load by themselves and go when you zoom out. Changed maps always stay.");
        if (MapStream)
        {
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Radius", ref MapStreamRadius, 40, 600, "%.0f m");
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Under zoom", ref MapHdZoom, 100, 2000, "%.0f m");
            Theme.Tip("The HD loads when the camera is closer than this to its target");
            if (mapStreamQueue.Count > 0) Ui.TextDisabled($"loading HD: {mapStreamQueue.Count} map(s) to go");
        }
        ImGui.Checkbox("Show the interiors (MLO)", ref MapShowInteriors);
        Theme.Tip("The inside of the game interiors near the camera shows by itself (view only). Pick something inside to edit that interior.");
        if (MapShowInteriors) { ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Interiors within", ref MapInteriorRadius, 20, 300, "%.0f m"); }
        bool vro = Settings.MapViewRadiusOn;
        if (ImGui.Checkbox("Only show what is near the camera", ref vro)) { Settings.MapViewRadiusOn = vro; Settings.Save(); mapLiveCount = -1; }
        Theme.Tip("For big custom maps: only the objects within this distance of the camera are drawn and can be picked, so the view stays smooth whatever the angle. The selected object always shows.");
        if (Settings.MapViewRadiusOn)
        {
            float vr = Settings.MapViewRadius; ImGui.SetNextItemWidth(-90);
            if (ImGui.SliderFloat("View radius", ref vr, 50, 3000, "%.0f m", ImGuiSliderFlags.Logarithmic)) { Settings.MapViewRadius = vr; mapLiveCount = -1; }
            if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
            Ui.TextDisabled($"drawn: {mapLive.Count} of {Scene.Objects.Count} objects");
        }
        if (mapDocs.Count > 0 && ImGui.Button("Unload the unchanged maps", new Vector2(-1, 0)))
            { var ch = ChangedMapDocs(); UnloadMapDocs(mapDocs.Values.Where(d => d.FromGame && !d.Modified && !ch.Contains(d.Name) && d.Name != mapActive).ToList()); }
    }
}
