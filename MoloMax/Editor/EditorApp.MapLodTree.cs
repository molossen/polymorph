using System.Numerics;
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
    public float MapLodMult = 1f;
    readonly Dictionary<uint, SceneObject> worldTpl = new();
    int worldTplNext = -2_000_000;
    (string Ymap, int Index)? pendingWorldSelect;

    /// <summary>The level-of-detail world is on and read.</summary>
    bool LodTreeOn => Settings.MapLodTree && worldEnts != null;

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
        if (worldSelTask != null && worldSelTask.IsCompleted) { (worldSel, worldSelNext) = (worldSelNext, worldSel); worldSelTask = null; }
        var eye = Viewports[ActiveViewport].Camera.Eye;
        if (worldSelTask == null && (Vector3.Distance(eye, worldSelCam) > 8 || worldSelMaps != mapDocs.Count || worldSelMult != MapLodMult))
        {
            worldSelCam = eye; worldSelMaps = mapDocs.Count; worldSelMult = MapLodMult;
            var open = new HashSet<int>(mapDocs.Keys.Select(worldEnts.YmapIndex).Where(i => i >= 0));
            var we = worldEnts; float mult = MapLodMult; var into = worldSelNext;
            worldSelTask = System.Threading.Tasks.Task.Run(() => we.Select(eye, mult, open.Count > 0 ? open.Contains : null, into));
        }
    }

    /// <summary>The shared object standing for every world entity of one model (drawn instanced).</summary>
    SceneObject WorldTemplate(ArchSlot s)
    {
        if (!worldTpl.TryGetValue(s.Hash, out var t))
        {
            worldTpl[s.Hash] = t = new SceneObject { Id = worldTplNext--, Name = s.Name ?? s.Hash.ToString("X8"), InstanceKey = "map:" + s.Hash.ToString("X8"), Mesh = s.Mesh };
            t.Gta.MapArchetype = s.Name; t.Gta.Role = GtaRole.Ignore;
            if (s.Ver != 0) t.Version = s.Ver; else { t.Touch(); s.Ver = t.Version; }
        }
        else if (!ReferenceEquals(t.Mesh, s.Mesh)) { t.Mesh = s.Mesh; if (s.Ver != 0) t.Version = s.Ver; else { t.Touch(); s.Ver = t.Version; } }
        return t;
    }

    bool IsWorldTemplate(SceneObject o) => o != null && o.Id <= -2_000_000;

    /// <summary>Adds the chosen world entities to the draw lists (frustum, tiny-on-screen and view radius culled).</summary>
    void GatherWorld(Vector4[] planes, Vector3 eye, float pix, float vr2)
    {
        if (!LodTreeOn) return;
        var we = worldEnts;
        foreach (var i in worldSel)
        {
            ref var e = ref we.E[i];
            if (e.Mlo) continue;
            if (!archSlots.TryGetValue(e.Arch, out var s)) s = Slot(e.Arch, null);
            if (s.State != 2 || s.Mesh == null) continue;
            float sc = MathF.Max(e.ScaleXY, e.ScaleZ);
            var lc = (s.Min + s.Max) * 0.5f;
            var c = e.Pos + Vector3.Transform(lc * new Vector3(e.ScaleXY, e.ScaleXY, e.ScaleZ), e.Rot);
            float r = (s.Max - s.Min).Length() * 0.5f * sc;
            bool vis = true;
            foreach (var p in planes) if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) { vis = false; break; }
            if (!vis) continue;
            float d = Vector3.Distance(c, eye);
            if (pix > 0 && d > r && r / d < pix * 1.5f) continue;
            if (vr2 > 0 && MathF.Max(0, d - r) * MathF.Max(0, d - r) > vr2) continue;
            var t = WorldTemplate(s);
            if (!instGroups.TryGetValue(t.InstanceKey, out var grp)) instGroups[t.InstanceKey] = grp = new InstGroup();
            if (grp.Template == null) grp.Template = t;
            else if (!ReferenceEquals(grp.Template.Mesh, t.Mesh)) continue;
            if (grp.Count == grp.Worlds.Length) { Array.Resize(ref grp.Worlds, grp.Worlds.Length * 2); Array.Resize(ref grp.Objs, grp.Worlds.Length); Array.Resize(ref grp.Ents, grp.Worlds.Length); }
            grp.Objs[grp.Count] = t; grp.Ents[grp.Count] = i;
            grp.Worlds[grp.Count++] = e.World;
            MapVisible++;
        }
    }

    /// <summary>Archetypes the chosen world entities need, nearest first (added to the reading order).</summary>
    void WorldPriorities(Vector3 eye)
    {
        if (!LodTreeOn) return;
        foreach (var i in worldSel)
        {
            ref var e = ref worldEnts.E[i];
            if (e.Mlo) continue;
            if (!archSlots.TryGetValue(e.Arch, out var s)) s = Slot(e.Arch, null);
            if (s.State != 0) continue;
            s.Priority = MathF.Min(s.Priority, Vector3.DistanceSquared(e.Pos, eye));
        }
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
