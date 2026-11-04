using System.Numerics;
using System.Text;

namespace MoloMax.Core.Gta.Export;

/// <summary>An archetype (.ytyp CBaseArchetypeDef).</summary>
public sealed class ArchetypeDef
{
    public string Name;
    public float LodDist = 100;
    public uint Flags;
    public Vector3 Min, Max;
    public string TextureDictionary = "";
    public string ClipDictionary = "";
    public string AssetType = "ASSET_TYPE_DRAWABLE";
    public float HdTextureDist = 40;
}

/// <summary>An entity placement (CEntityDef), in ymap world space or MLO local space.</summary>
public sealed class EntityDef
{
    /// <summary>Entity set name (MLO), empty = main entity list.</summary>
    public string EntitySet;
    /// <summary>Room index for an entity-set entity.</summary>
    public int Room;
    public string Archetype;
    public uint Flags;
    public uint Guid;
    public Vector3 Position;
    /// <summary>Real rotation of the entity; written inverted, as the game stores CEntityDef rotations.</summary>
    public Quaternion Rotation = Quaternion.Identity;
    public float ScaleXY = 1, ScaleZ = 1;
    public float LodDist = 100;
    public Vector3 Min, Max; // world bounds, for the streaming extents
}

/// <summary>CodeWalker meta XML for .ytyp (CMapTypes) and .ymap (CMapData).</summary>
public static class MetaXml
{
    public static uint Jenkins(string s)
    {
        uint h = 0;
        foreach (var ch in (s ?? "").ToLowerInvariant())
        {
            h += (byte)ch; h += h << 10; h ^= h >> 6;
        }
        h += h << 3; h ^= h >> 11; h += h << 15;
        return h;
    }

    static void ArchetypeCommon(Xw x, float lodDist, uint flags, Vector3 mn, Vector3 mx, float hdDist, string name, string txd, string physics, string assetType, string clips = "")
    {
        x.Value("lodDist", lodDist);
        x.Value("flags", flags);
        x.Value("specialAttribute", 0);
        x.Vec("bbMin", mn); x.Vec("bbMax", mx);
        x.Vec("bsCentre", (mn + mx) * 0.5f);
        x.Value("bsRadius", (mx - mn).Length() * 0.5f);
        x.Value("hdTextureDist", hdDist);
        x.Text("name", name);
        x.Text("textureDictionary", txd);
        x.Text("clipDictionary", clips);
        x.Empty("drawableDictionary");
        x.Text("physicsDictionary", physics);
        x.Text("assetType", assetType);
        x.Text("assetName", name);
        x.Empty("extensions");
    }

    static void Entity(Xw x, EntityDef e, bool mloChild)
    {
        x.Open("Item", "type=\"CEntityDef\"");
        x.Text("archetypeName", e.Archetype);
        x.Value("flags", e.Flags);
        x.Value("guid", e.Guid);
        x.Vec("position", e.Position);
        var q = Quaternion.Normalize(e.Rotation);
        x.Quat("rotation", q.IsIdentity ? Quaternion.Identity : Quaternion.Conjugate(q));
        x.Value("scaleXY", e.ScaleXY); x.Value("scaleZ", e.ScaleZ);
        x.Value("parentIndex", -1);
        x.Value("lodDist", e.LodDist);
        x.Value("childLodDist", 0f);
        x.Text("lodLevel", "LODTYPES_DEPTH_ORPHANHD");
        x.Value("numChildren", 0);
        x.Text("priorityLevel", "PRI_REQUIRED");
        x.Empty("extensions");
        x.Value("ambientOcclusionMultiplier", 255);
        x.Value("artificialAmbientOcclusion", 255);
        x.Value("tintValue", 0);
        x.Close("Item");
    }

    /// <summary>The .ytyp: plain archetypes plus, for an interior, the MLO archetype with its entities, rooms and portals.</summary>
    public static string Ytyp(string name, IEnumerable<ArchetypeDef> archetypes, string mloName = null, List<EntityDef> mloEntities = null, MloLayout layout = null, List<EntityDef> setEntities = null)
    {
        var x = new Xw();
        x.Open("CMapTypes");
        x.Empty("extensions");
        x.Open("archetypes");
        // plain archetypes first, the MLO archetype last (like CodeWalker / Sollumz interiors)
        foreach (var a in archetypes)
        {
            x.Open("Item", "type=\"CBaseArchetypeDef\"");
            ArchetypeCommon(x, a.LodDist, a.Flags, a.Min, a.Max, a.HdTextureDist, a.Name, a.TextureDictionary, "", a.AssetType, a.ClipDictionary);
            x.Close("Item");
        }
        if (mloName != null && layout != null)
        {
            x.Open("Item", "type=\"CMloArchetypeDef\"");
            // bounds = the limbo room (all rooms); physics dictionary = the MLO's own name, which is also the name of its collision .ybn
            // (declared as the interior's bounds in _manifest.ymf). An MLO whose name matches no collision crashes the game on entry.
            ArchetypeCommon(x, 200, 0, layout.Rooms[0].Min, layout.Rooms[0].Max, 100, mloName, "", mloName, "ASSET_TYPE_ASSETLESS");
            x.Value("mloFlags", 0);
            x.Open("entities");
            foreach (var e in mloEntities ?? new()) Entity(x, e, true);
            x.Close("entities");
            x.Open("rooms", "itemType=\"CMloRoomDef\"");
            foreach (var r in layout.Rooms)
            {
                x.Open("Item");
                x.Text("name", r.Name);
                x.Vec("bbMin", r.Min); x.Vec("bbMax", r.Max);
                x.Value("blend", 1f);
                x.Text("timecycleName", string.IsNullOrWhiteSpace(r.Timecycle) ? "int_gasstation" : r.Timecycle);
                x.Empty("secondaryTimecycleName");
                x.Value("flags", r.Flags != 0 ? r.Flags : 96u);
                x.Value("portalCount", r.PortalCount);
                x.Value("floorId", 0);
                x.Value("exteriorVisibiltyDepth", -1);
                if (r.Entities.Count == 0) x.Empty("attachedObjects");
                else x.Lines("attachedObjects", Chunk(r.Entities, 10));
                x.Close("Item");
            }
            x.Close("rooms");
            x.Open("portals", "itemType=\"CMloPortalDef\"");
            foreach (var p in layout.Portals)
            {
                x.Open("Item");
                x.Value("roomFrom", p.RoomFrom); x.Value("roomTo", p.RoomTo);
                x.Value("flags", p.Flags != 0 ? p.Flags : (p.RoomTo == 0 || p.RoomFrom == 0 ? 8192u : 0u));
                x.Value("mirrorPriority", 0); x.Value("opacity", 0); x.Value("audioOcclusion", 0);
                x.Open("corners");
                foreach (var c in p.Corners) x.Text("Item", $"{Xw.F(c.X)}, {Xw.F(c.Y)}, {Xw.F(c.Z)}, 0");
                x.Close("corners");
                if (p.AttachedEntities.Count == 0) x.Empty("attachedObjects");
                else x.Lines("attachedObjects", Chunk(p.AttachedEntities, 10));
                x.Close("Item");
            }
            x.Close("portals");
            var sets = (setEntities ?? new()).GroupBy(e => e.EntitySet).ToList();
            if (sets.Count == 0) x.Empty("entitySets", "itemType=\"CMloEntitySet\"");
            else
            {
                x.Open("entitySets", "itemType=\"CMloEntitySet\"");
                foreach (var g in sets)
                {
                    x.Open("Item");
                    x.Text("name", g.Key);
                    x.Lines("locations", g.Select(e => e.Room.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    x.Open("entities");
                    foreach (var e in g) Entity(x, e, true);
                    x.Close("entities");
                    x.Close("Item");
                }
                x.Close("entitySets");
            }
            x.Empty("timeCycleModifiers", "itemType=\"CMloTimeCycleModifier\"");
            x.Close("Item");
        }
        x.Close("archetypes");
        x.Text("name", name);
        x.Empty("dependencies");
        x.Empty("compositeEntityTypes", "itemType=\"CCompositeEntityType\"");
        x.Close("CMapTypes");
        return x.ToString();
    }

    /// <summary>_manifest.ymf of an interior: imap / ityp dependencies (INTERIOR_DATA) and the interior's bounds files.</summary>
    public static string ManifestYmf(string mloName, string miloYmap, IEnumerable<string> boundsFiles)
    {
        var x = new Xw();
        x.Open("CPackFileMetaData");
        x.Empty("MapDataGroups", "itemType=\"CMapDataGroup\"");
        x.Empty("HDTxdBindingArray", "itemType=\"CHDTxdAssetBinding\"");
        x.Empty("imapDependencies", "itemType=\"CImapDependency\"");
        x.Open("imapDependencies_2", "itemType=\"CImapDependencies\"");
        x.Open("Item"); x.Text("imapName", miloYmap); x.Text("manifestFlags", "INTERIOR_DATA");
        x.Open("itypDepArray"); x.Text("Item", mloName); x.Close("itypDepArray"); x.Close("Item");
        x.Close("imapDependencies_2");
        x.Open("itypDependencies_2", "itemType=\"CItypDependencies\"");
        x.Open("Item"); x.Text("itypName", mloName); x.Text("manifestFlags", "INTERIOR_DATA"); x.Empty("itypDepArray"); x.Close("Item");
        x.Close("itypDependencies_2");
        x.Open("Interiors", "itemType=\"CInteriorBoundsFiles\"");
        x.Open("Item"); x.Text("Name", mloName);
        x.Open("Bounds"); foreach (var b in boundsFiles) x.Text("Item", b); x.Close("Bounds");
        x.Close("Item");
        x.Close("Interiors");
        x.Close("CPackFileMetaData");
        return x.ToString();
    }

    static IEnumerable<string> Chunk(List<int> v, int n)
    {
        for (int i = 0; i < v.Count; i += n) yield return string.Join(" ", v.Skip(i).Take(n));
    }

    static void MapHeader(Xw x, string name, uint contentFlags, Vector3 eMin, Vector3 eMax, float maxLod)
    {
        x.Text("name", name);
        x.Empty("parent");
        x.Value("flags", 0);
        x.Value("contentFlags", contentFlags);
        x.Vec("streamingExtentsMin", eMin - new Vector3(maxLod));
        x.Vec("streamingExtentsMax", eMax + new Vector3(maxLod));
        x.Vec("entitiesExtentsMin", eMin);
        x.Vec("entitiesExtentsMax", eMax);
    }

    static void MapFooter(Xw x, string name)
    {
        x.Empty("containerLods", "itemType=\"rage__fwContainerLodDef\"");
        x.Empty("boxOccluders", "itemType=\"BoxOccluder\"");
        x.Empty("occludeModels", "itemType=\"OccludeModel\"");
        x.Empty("physicsDictionaries");
        x.Open("instancedData");
        x.Empty("ImapLink");
        x.Empty("PropInstanceList", "itemType=\"rage__fwPropInstanceListDef\"");
        x.Empty("GrassInstanceList", "itemType=\"rage__fwGrassInstanceListDef\"");
        x.Close("instancedData");
        x.Empty("timeCycleModifiers", "itemType=\"CTimeCycleModifier\"");
        x.Empty("carGenerators", "itemType=\"CCarGen\"");
        x.Open("LODLightsSOA");
        x.Empty("direction", "itemType=\"FloatXYZ\"");
        foreach (var t in new[] { "falloff", "falloffExponent", "timeAndStateFlags", "hash", "coneInnerAngle", "coneOuterAngleOrCapExt", "coronaIntensity" }) x.Empty(t);
        x.Close("LODLightsSOA");
        x.Open("DistantLODLightsSOA");
        x.Empty("position", "itemType=\"FloatXYZ\"");
        x.Empty("RGBI");
        x.Value("numStreetLights", 0); x.Value("category", 0);
        x.Close("DistantLODLightsSOA");
        x.Open("block");
        x.Value("version", 0); x.Value("flags", 0);
        x.Text("name", name);
        x.Text("exportedBy", "Polymorph");
        x.Empty("owner");
        x.Text("time", DateTime.Now.ToString("dd MMMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        x.Close("block");
    }

    /// <summary>A ymap of plain entities (exterior props and joined exterior models).</summary>
    public static string YmapEntities(string name, List<EntityDef> entities)
    {
        var x = new Xw();
        x.Open("CMapData");
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue); float lod = 0;
        foreach (var e in entities) { mn = Vector3.Min(mn, e.Min); mx = Vector3.Max(mx, e.Max); lod = MathF.Max(lod, e.LodDist); }
        if (entities.Count == 0) { mn = mx = Vector3.Zero; }
        MapHeader(x, name, 1, mn, mx, lod);
        x.Open("entities");
        foreach (var e in entities) Entity(x, e, false);
        x.Close("entities");
        MapFooter(x, name);
        x.Close("CMapData");
        return x.ToString();
    }

    /// <summary>The _milo_ ymap that places the interior (CMloInstanceDef, rotation written as is).</summary>
    public static string YmapMlo(string name, string mloArchetype, Vector3 position, Quaternion rotation, int exitPortals, Vector3 worldMin, Vector3 worldMax, IEnumerable<string> defaultSets = null)
    {
        var x = new Xw();
        x.Open("CMapData");
        MapHeader(x, name, 9, worldMin, worldMax, 100); // contentFlags 9 (HD + interior), streaming extents = entities +-100 m
        x.Open("entities");
        x.Open("Item", "type=\"CMloInstanceDef\"");
        x.Text("archetypeName", mloArchetype);
        x.Value("flags", 1572865u);
        x.Value("guid", Jenkins(name));
        x.Vec("position", position);
        x.Quat("rotation", Quaternion.Normalize(rotation));
        x.Value("scaleXY", 1f); x.Value("scaleZ", 1f);
        x.Value("parentIndex", -1);
        x.Value("lodDist", 200f);
        x.Value("childLodDist", 0f);
        x.Text("lodLevel", "LODTYPES_DEPTH_HD");
        x.Value("numChildren", 0);
        x.Text("priorityLevel", "PRI_REQUIRED");
        x.Empty("extensions");
        x.Value("ambientOcclusionMultiplier", 255);
        x.Value("artificialAmbientOcclusion", 255);
        x.Value("tintValue", 0);
        x.Value("groupId", 0);
        x.Value("floorId", 0);
        var ds = (defaultSets ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (ds.Count == 0) x.Empty("defaultEntitySets");
        else { x.Open("defaultEntitySets"); foreach (var n in ds) x.Text("Item", n.Trim()); x.Close("defaultEntitySets"); }
        x.Value("numExitPortals", exitPortals);
        x.Value("MLOInstflags", 0);
        x.Close("Item");
        x.Close("entities");
        MapFooter(x, name);
        x.Close("CMapData");
        return x.ToString();
    }
}
