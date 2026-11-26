using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode world drawn the way the game picks it: every object of the map is a light record (<see cref="WorldEntities"/>),
/// and what is drawn follows the game's level-of-detail tree for the camera (HD up close, LOD further, SLOD far away).
/// Nothing of it is editable until clicked: a click opens that object's .ymap for editing (its objects then become scene
/// objects, drawn and edited the usual way), and selects it.
/// </summary>
public sealed partial class EditorApp
{
    WorldEntities worldEnts;
    System.Threading.Tasks.Task worldEntsTask;
    string worldEntsStatus = "";
    List<int> worldSel = new(), worldSelNext = new();
    System.Threading.Tasks.Task worldSelTask;
    Vector3 worldSelCam = new(float.NaN); int worldSelMaps = -1; float worldSelMult = -1;
    /// <summary>Distance multiplier of the level-of-detail choice (1 = like the game).</summary>
    public float MapLodMult => Math.Clamp(Settings.MapViewDistance, 0.25f, 4f);
    readonly Dictionary<uint, SceneObject> worldTpl = new();
    int worldTplNext = -2_000_000;
    (string Ymap, int Index)? pendingWorldSelect;

    /// <summary>The level-of-detail world is on and read.</summary>
    bool LodTreeOn => Settings.MapLodTree && worldEnts != null;
    readonly HashSet<int> worldSelSet = new();
    int worldSelGen;
    /// <summary>Models read so far (any change: the shadows are drawn again).</summary>
    int archReady;
    HashSet<int> worldOpenMaps = new();

    /// <summary>An editable object of a game map: drawn only when the tree chose its entity here (else its LOD / HD stands for it),
    /// or when selected.</summary>
    bool WorldHidesEditable(SceneObject o)
    {
        if (!LodTreeOn || !entOf.TryGetValue(o.Id, out var de) || !de.Doc.FromGame || Selected.Contains(o.Id)) return false;
        int g = worldEnts.Find(worldEnts.YmapIndex(de.Doc.Name), de.Ent.Index);
        return g >= 0 && !worldSelSet.Contains(g);
    }

    void PumpWorldEntities()
    {
        if (!Settings.MapLodTree || world == null || game == null) return;
        if (worldEnts == null && worldEntsTask == null)
        {
            var w = world; var g = game;
            worldEntsTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { worldEnts = WorldEntities.Build(w, h => g.ArchetypeLodDist(h), Path.Combine(MapCacheDir, "world_entities.bin"), w.Stamp, (s, p) => worldEntsStatus = s); }
                catch (Exception e) { worldEntsStatus = "Map objects failed: " + e.Message; }
            });
        }
        if (worldEnts == null) return;
        // what to draw, worked out in the background when the camera moved (or a map was opened / closed for editing)
        if (worldSelTask != null && worldSelTask.IsCompleted) { (worldSel, worldSelNext) = (worldSelNext, worldSel); worldSelTask = null; worldSelSet.Clear(); worldSelSet.UnionWith(worldSel); worldSelGen++; }
        var eye = Viewports[ActiveViewport].Camera.Eye;
        if (worldSelTask == null && (Vector3.Distance(eye, worldSelCam) > 8 || worldSelMaps != mapDocs.Count || worldSelMult != MapLodMult))
        {
            worldSelCam = eye; worldSelMaps = mapDocs.Count; worldSelMult = MapLodMult;
            // maps open for editing: the tree still chooses their objects; the editable copies show the chosen ones (MapGatherDraws)
            worldOpenMaps = new HashSet<int>(mapDocs.Keys.Select(worldEnts.YmapIndex).Where(i => i >= 0));
            var we = worldEnts; float mult = MapLodMult; var into = worldSelNext;
            worldSelTask = System.Threading.Tasks.Task.Run(() => we.Select(eye, mult, null, into));
        }
    }

    /// <summary>The shared object standing for every world entity of one model (drawn instanced).</summary>
    double worldNow, worldTrimTime;
    static readonly PolyMesh GpuOnlyStub = new();

    /// <summary>Models drawn only by the level-of-detail world let go of their CPU copy once on the graphics card, and leave
    /// the graphics card 20 s after they were last drawn (read again from the cache when the camera comes back).</summary>
    void TrimWorldModels()
    {
        if (!LodTreeOn || ImGui.GetTime() - worldTrimTime < 2) return;
        worldTrimTime = ImGui.GetTime();
        var sceneKeys = new HashSet<string>(Scene.Objects.Where(o => o.InstanceKey != null).Select(o => o.InstanceKey));
        var keep = new HashSet<string>(backdrop.Select(o => o.InstanceKey));
        double now = ImGui.GetTime();
        foreach (var (hash, t) in worldTpl)
        {
            if (!archSlots.TryGetValue(hash, out var s)) continue;
            bool recent = now - s.LastUsed < 20;
            if (recent) keep.Add(t.InstanceKey);
            if (sceneKeys.Contains(t.InstanceKey)) continue; // scene objects use the real mesh
            if (s.GpuOnly && !recent) { s.GpuOnly = false; s.State = 0; s.Mesh = null; s.Ver = 0; t.Mesh = null; continue; } // gone from the graphics card at the next collect
            if (!s.GpuOnly && s.State == 2 && s.Mesh != null && !renderer.NeedsUpload(t))
            {
                s.GpuOnly = true; s.Mesh = null; t.Mesh = GpuOnlyStub; // the CPU copy goes, the GPU one stays (same version)
            }
        }
        // models no scene object and no world entity drew for 20 s (read for the far models, a closed map...): their CPU copy goes too
        foreach (var s in archSlots.Values)
        {
            if (s.State != 2 || s.Mesh == null || worldTpl.ContainsKey(s.Hash) || now - s.LastUsed < 20) continue;
            var key = "map:" + s.Hash.ToString("X8");
            if (sceneKeys.Contains(key) || keep.Contains(key)) { s.LastUsed = now; continue; }
            s.State = 0; s.Mesh = null; s.Ver = 0;
        }
        renderer.KeepInstanceKeys = keep;
    }

    SceneObject WorldTemplate(ArchSlot s)
    {
        if (s.GpuOnly && worldTpl.TryGetValue(s.Hash, out var g)) return g;
        if (!worldTpl.TryGetValue(s.Hash, out var t))
        {
            worldTpl[s.Hash] = t = new SceneObject { Id = worldTplNext--, Name = s.Name ?? s.Hash.ToString("X8"), InstanceKey = "map:" + s.Hash.ToString("X8"), Mesh = s.Mesh };
            t.Gta.MapArchetype = s.Name; t.Gta.Role = GtaRole.Ignore;
            if (s.Ver != 0) t.Version = s.Ver; else { t.Touch(); s.Ver = t.Version; }
        }
        else if (!ReferenceEquals(t.Mesh, s.Mesh)) { t.Mesh = s.Mesh; if (s.Ver != 0) t.Version = s.Ver; else { t.Touch(); s.Ver = t.Version; } }
        return t;
    }

    /// <summary>World objects smaller than this many pixels on screen are not drawn.</summary>
    internal float WorldPixCull = 1.5f;
    internal bool WorldDrawOff;

    bool IsWorldTemplate(SceneObject o) => o != null && o.Id <= -2_000_000;

    /// <summary>Parents shown in place of children whose models are not read yet (like the game: the LOD stays until its detail is ready).</summary>
    readonly HashSet<int> worldBlocked = new(), worldFallbackDrawn = new();
    /// <summary>The last frame drew a parent in place of children still loading (the selection is worked out again soon).</summary>
    int worldFallbacks;

    bool WorldReady(ArchSlot s) => s.State == 2 && s.Mesh != null || s.GpuOnly;

    /// <summary>Adds the chosen world entities to the draw lists (frustum, tiny-on-screen and view radius culled).
    /// A child whose model is not read yet leaves its LOD parent drawn instead, with its brothers, until all are ready.</summary>
    void GatherWorld(Vector4[] planes, Vector3 eye, float pix, float vr2)
    {
        if (!LodTreeOn || WorldDrawOff) return;
        worldNow = ImGui.GetTime();
        worldWanted.Clear(); worldBlocked.Clear(); worldFallbackDrawn.Clear();
        var we = worldEnts;
        // 1. children not ready: their parent stays; they are all asked for (even off screen, else the parent would stay for good)
        foreach (var i in worldSel)
        {
            ref var e = ref we.E[i];
            if (e.Mlo || worldOpenMaps.Contains(e.Ymap)) continue;
            if (!archSlots.TryGetValue(e.Arch, out var s)) s = Slot(e.Arch, null);
            if (WorldReady(s) || s.State == 3) continue;
            if (e.Parent >= 0) worldBlocked.Add(e.Parent);
            if (s.State == 0)
            {
                float d2 = Vector3.DistanceSquared(e.Pos, eye);
                // off screen: asked for after what is in view
                float rr = MathF.Max(game?.ArchetypeSize(e.Arch) ?? 20f, 5f) * MathF.Max(e.ScaleXY, e.ScaleZ);
                foreach (var p in planes) if (p.X * e.Pos.X + p.Y * e.Pos.Y + p.Z * e.Pos.Z + p.W < -rr) { d2 = d2 * 4 + 1e6f; break; }
                if (!worldWanted.TryGetValue(e.Arch, out var w0) || d2 < w0) worldWanted[e.Arch] = d2;
            }
            s.LastUsed = worldNow;
        }
        // 2. what is ready, unless its parent stands in for the family
        foreach (var i in worldSel)
        {
            ref var e = ref we.E[i];
            if (e.Mlo || worldOpenMaps.Contains(e.Ymap)) continue;
            if (e.Parent >= 0 && worldBlocked.Contains(e.Parent)) continue;
            DrawWorldEntity(i, planes, eye, pix, vr2);
        }
        // 3. the parents standing in (or the nearest ancestor that is ready)
        worldFallbacks = 0;
        foreach (var p0 in worldBlocked)
        {
            int p = p0;
            while (p >= 0)
            {
                ref var pe = ref we.E[p];
                if (archSlots.TryGetValue(pe.Arch, out var ps) && WorldReady(ps)) break;
                if (!archSlots.TryGetValue(pe.Arch, out ps)) ps = Slot(pe.Arch, null);
                if (ps.State == 0) { float d2 = Vector3.DistanceSquared(pe.Pos, eye); if (!worldWanted.TryGetValue(pe.Arch, out var w0) || d2 < w0) worldWanted[pe.Arch] = d2; }
                p = pe.Parent;
            }
            if (p < 0 || !worldFallbackDrawn.Add(p)) continue;
            worldFallbacks++;
            DrawWorldEntity(p, planes, eye, pix, vr2);
        }
    }

    void DrawWorldEntity(int i, Vector4[] planes, Vector3 eye, float pix, float vr2)
    {
        var we = worldEnts;
        ref var e = ref we.E[i];
        if (!archSlots.TryGetValue(e.Arch, out var s) || !WorldReady(s)) return;
        float sc = MathF.Max(e.ScaleXY, e.ScaleZ);
        var lc = (s.Min + s.Max) * 0.5f;
        var c = e.Pos + Vector3.Transform(lc * new Vector3(e.ScaleXY, e.ScaleXY, e.ScaleZ), e.Rot);
        float r = (s.Max - s.Min).Length() * 0.5f * sc;
        foreach (var p in planes) if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) return;
        s.LastUsed = worldNow;
        float d = Vector3.Distance(c, eye);
        if (pix > 0 && d > r && r / d < pix * WorldPixCull) return;
        if (vr2 > 0 && MathF.Max(0, d - r) * MathF.Max(0, d - r) > vr2) return;
        var t = WorldTemplate(s);
        if (!instGroups.TryGetValue(t.InstanceKey, out var grp)) instGroups[t.InstanceKey] = grp = new InstGroup();
        if (grp.Template == null) grp.Template = t;
        else if (!ReferenceEquals(grp.Template.Mesh, t.Mesh)) return;
        if (grp.Count == grp.Worlds.Length) { Array.Resize(ref grp.Worlds, grp.Worlds.Length * 2); Array.Resize(ref grp.Objs, grp.Worlds.Length); Array.Resize(ref grp.Ents, grp.Worlds.Length); }
        grp.Objs[grp.Count] = t; grp.Ents[grp.Count] = i;
        grp.Worlds[grp.Count++] = e.World;
        MapVisible++;
    }

    readonly Dictionary<uint, (SceneObject T, List<Matrix4x4> W)> shadowGroups = new();
    List<(SceneObject Template, Matrix4x4[] Worlds, int Count)> shadowList = new();

    /// <summary>The chosen world objects within <paramref name="r"/> of the shadow focus, by model.</summary>
    List<(SceneObject Template, Matrix4x4[] Worlds, int Count)> WorldShadowCasters(Vector3 c, float r, ref long ver, float minRadius = 0)
    {
        shadowList = new();
        if (!LodTreeOn || WorldDrawOff) return shadowList;
        foreach (var g in shadowGroups.Values) g.W.Clear();
        var we = worldEnts;
        foreach (var i in worldSel.Where(x => !(we.E[x].Parent >= 0 && worldBlocked.Contains(we.E[x].Parent))).Concat(worldFallbackDrawn))
        {
            ref var e = ref we.E[i];
            if (e.Mlo || !archSlots.TryGetValue(e.Arch, out var s) || !(s.State == 2 && s.Mesh != null || s.GpuOnly)) continue;
            float rr = (s.Max - s.Min).Length() * 0.5f * MathF.Max(e.ScaleXY, e.ScaleZ);
            if (rr < minRadius) continue; // too small to show in a wide shadow map
            if (MathF.Abs(e.Pos.X - c.X) > r + rr || MathF.Abs(e.Pos.Y - c.Y) > r + rr) continue;
            if (!shadowGroups.TryGetValue(e.Arch, out var g)) shadowGroups[e.Arch] = g = (null, new List<Matrix4x4>());
            g.W.Add(e.World);
            ver += i * 31 + s.Ver;
        }
        foreach (var (h, g) in shadowGroups)
        {
            if (g.W.Count == 0 || !archSlots.TryGetValue(h, out var s)) continue;
            shadowList.Add((WorldTemplate(s), g.W.ToArray(), g.W.Count));
        }
        return shadowList;
    }

    /// <summary>Archetypes the chosen world entities need, nearest first (added to the reading order).</summary>
    readonly Dictionary<uint, float> worldWanted = new();
    void WorldPriorities(Vector3 eye)
    {
        if (!LodTreeOn) return;
        foreach (var (h, d2) in worldWanted)
            if (archSlots.TryGetValue(h, out var s) && s.State == 0) s.Priority = MathF.Min(s.Priority, d2);
    }

    /// <summary>A click on a world entity: its .ymap opens for editing, then the entity is selected.</summary>
    bool OpenWorldEntity(int ent)
    {
        if (worldEnts == null || ent < 0 || ent >= worldEnts.E.Length) return false;
        var e = worldEnts.E[ent];
        var name = worldEnts.Ymaps[e.Ymap];
        var y = world?.Ymaps.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (y == null) return false;
        if (mapDocs.TryGetValue(name, out var doc)) { SelectMapEntity(doc, e.Index); return true; }
        pendingWorldSelect = (name, e.Index);
        if (!mapLoadQueue.Contains(y)) mapLoadQueue.Enqueue(y);
        SetStatus($"Opening {name} for editing...");
        return true;
    }

    void SelectMapEntity(MapDocument doc, int index)
    {
        var me = doc.Entities.FirstOrDefault(x => x.Index == index);
        if (me == null || !objOf.TryGetValue(me, out var id)) return;
        mapPinned.Add(doc.Name);
        Selected.Clear(); Selected.Add(id); ActiveId = id; SelectionChanged();
        SetStatus($"{Scene.Find(id)?.Name} ({doc.Name})");
    }

    /// <summary>After a map load: the entity clicked in the world gets selected.</summary>
    void WorldSelectAfterLoad()
    {
        if (pendingWorldSelect is not { } p || !mapDocs.TryGetValue(p.Ymap, out var doc)) return;
        pendingWorldSelect = null;
        SelectMapEntity(doc, p.Index);
    }
}
