using System.Numerics;
using System.Text;
using System.Xml;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Export;

/// <summary>What an export produced, for the report page.</summary>
public sealed class ExportReport
{
    public string ResourceFolder;
    public List<string> Files = new();
    public List<string> Info = new();
    public List<string> Warnings = new();
    public List<string> Errors = new();
    public MloLayout Layout;
    public int Models, Entities, CollisionTriangles;
    public bool Ok => Errors.Count == 0;

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var e in Errors) sb.AppendLine("ERROR: " + e);
        foreach (var w in Warnings) sb.AppendLine("Warning: " + w);
        foreach (var i in Info) sb.AppendLine(i);
        return sb.ToString();
    }
}

/// <summary>
/// Sollumz-style export of a whole scene to a ready-to-stream FiveM resource:
/// <c>fxmanifest.lua</c>, <c>stream/mlo</c> (shell, props, collision, ytyp, _milo_ ymap) and <c>stream/exterior</c>
/// (joined exterior models, exterior props, exterior collision, ymap). Everything is written as CodeWalker XML
/// and compiled to binary game files with CodeWalker.Core.
/// </summary>
public static class GtaExporter
{
    /// <summary>Archetype flags of a UV animated model: "UV anims (YCD)" (bit 10) + "Auto start anim" (bit 19).
    /// Without auto start the clip is loaded but never plays (the texture stays still in game).</summary>
    public const uint UvAnimFlags = 1024u | 524288u;

    sealed class PropModel
    {
        public string Name;
        public SceneObject First;
        public List<SceneObject> Instances = new();
        public GtaModel Model;
        public bool Door;
    }

    public static string SafeName(string s) => TextureBank.SafeName(s);

    /// <summary>The object slides its texture: UV keys on the timeline or a UV animation in its GTA properties.</summary>
    public static bool HasUvAnim(SceneObject o, Scene scene) =>
        o.Gta.Anim is { } a && a.Kind != UvAnimKind.None
        || scene?.Anim?.Track(o.Id) is { } t && MoloMax.Core.Anim.Ch.Uv.Any(t.Has);

    /// <summary>The role an object really gets (Auto resolved).</summary>
    public static GtaRole ResolveRole(SceneObject o, MloLayout layout, float gap, Scene scene = null)
    {
        if (o.GtaType == GtaObjectType.MloRoom) return GtaRole.Shell;
        if (o.Gta.Role != GtaRole.Auto) return o.Gta.Role;
        // a texture animation needs its own model (the clip is found by the model's name): never joined into the shell / exterior
        if (HasUvAnim(o, scene)) return GtaRole.Prop;
        bool mlo = layout != null && layout.Rooms.Count > 1;
        if (!mlo) return GtaRole.Exterior;
        var (mn, mx) = o.WorldBounds();
        var c = (mn + mx) * 0.5f;
        if (layout.RoomAt(c) != 0)
        {
            // big pieces inside a room (floors, ceilings) belong to the shell, furniture is a prop
            var room = layout.Rooms[layout.RoomAt(c)];
            var rs = room.Max - room.Min; var os = mx - mn;
            bool spansRoom = os.X > rs.X * 0.6f && os.Y > rs.Y * 0.6f || os.Z > rs.Z * 0.8f && (os.X > rs.X * 0.6f || os.Y > rs.Y * 0.6f);
            // a ground plane bigger than the MLO is exterior, not shell
            bool withinMlo = Inside(mn, layout.Rooms[0].Min - new Vector3(gap), layout.Rooms[0].Max + new Vector3(gap))
                          && Inside(mx, layout.Rooms[0].Min - new Vector3(gap), layout.Rooms[0].Max + new Vector3(gap));
            if (spansRoom) return withinMlo ? GtaRole.Shell : GtaRole.Exterior;
            return GtaRole.Prop;
        }
        // between rooms (connecting floors, gap fillers): shell; elsewhere: exterior
        return InsideMlo(layout, c, gap) ? GtaRole.Shell : GtaRole.Exterior;
    }

    public static ExportReport Export(Scene scene, string outputFolder, Func<string, RgbaImage> imageLoader, IEnumerable<string> textureFolders = null, Action<string> progress = null)
    {
        var rep = new ExportReport();
        // modifier stacks are evaluated with their render settings (Blender: what F12 / export sees), on a copy
        if (scene.Objects.Any(o => o.Modifiers?.Count > 0))
        {
            scene = scene.Clone();
            foreach (var o in scene.Objects) o.BakeModifiers(render: true);
        }
        var st = scene.Gta ?? new GtaProjectSettings();
        string res = SafeName(st.ResourceName);
        if (string.IsNullOrWhiteSpace(outputFolder)) { rep.Errors.Add("Choose an output folder."); return rep; }
        string root = Path.Combine(outputFolder, res);
        string mloDir = Path.Combine(root, "stream", "mlo"), extDir = Path.Combine(root, "stream", "exterior");
        string xmlDir = Path.Combine(outputFolder, res + "_xml");
        string tmpDir = Path.Combine(Path.GetTempPath(), "molomax_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        rep.ResourceFolder = root;
        var bank = new TextureBank { ImageLoader = imageLoader, MaxSize = st.MaxTextureSize };
        if (textureFolders != null) bank.SearchFolders.AddRange(textureFolders);

        try
        {
            progress?.Invoke("Rooms and portals");
            scene.SyncInstances();
            var objs = scene.Objects.Where(o => (!o.Hidden || o.Gta.LodLevel > 0) && o.Gta.Role != GtaRole.Ignore && (o.Mesh.Faces.Count > 0 || o.GtaType is GtaObjectType.Model or GtaObjectType.Light or GtaObjectType.CollisionGroup)).ToList();
            int hiddenCount = scene.Objects.Count(o => o.Hidden && o.Gta.LodLevel == 0);
            if (hiddenCount > 0) rep.Info.Add($"{hiddenCount} hidden object(s) skipped.");
            if (objs.Count(o => o.GtaType is not (GtaObjectType.Portal or GtaObjectType.Model or GtaObjectType.Light or GtaObjectType.CollisionGroup)) == 0) { rep.Errors.Add("Nothing to export: the scene has no visible geometry."); return rep; }
            var layout = MloLayout.Build(scene, st);
            bool isMlo = layout.Rooms.Count > 1;
            rep.Layout = isMlo ? layout : null;
            rep.Warnings.AddRange(layout.Warnings);

            // world placement of the scene: GtaOrigin + heading around Z
            var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, st.Heading * MathF.PI / 180f);
            Vector3 ToWorld(Vector3 p) => scene.GtaOrigin + Vector3.Transform(p, heading);

            // ---- sort objects by role
            var shellObjs = new List<SceneObject>(); var extObjs = new List<SceneObject>();
            var colObjs = new List<SceneObject>();
            var props = new Dictionary<string, PropModel>();
            var reserved = new HashSet<string> { res, res + "_shell", res + "_ext", res + "_ext_col", res + "_milo_", res + "_exterior" };
            var meshOfName = new Dictionary<string, PolyMeshRef>();
            var propOfInstance = new Dictionary<string, PropModel>();
            // hierarchy (GIMS style): a child mesh joins its parent's model, a child collision becomes its embedded collision
            var objSet = objs.ToHashSet();
            SceneObject ModelRoot(SceneObject o)
            {
                var r = o; var p = scene.Parent(o); int guard = 0;
                while (p != null && objSet.Contains(p) && p.GtaType is GtaObjectType.Drawable or GtaObjectType.Model or GtaObjectType.CollisionGroup && guard++ < 64) { r = p; p = scene.Parent(p); }
                return r;
            }
            var childParts = new Dictionary<SceneObject, List<SceneObject>>();
            var roleOf = new Dictionary<SceneObject, GtaRole>();
            foreach (var o in objs)
            {
                if (o.GtaType is GtaObjectType.Portal or GtaObjectType.MloRoom or GtaObjectType.CollisionGroup) continue;
                var mroot = ModelRoot(o);
                if (mroot == o || mroot.GtaType is not (GtaObjectType.Drawable or GtaObjectType.Model)) continue;
                if (!roleOf.TryGetValue(mroot, out var rr)) roleOf[mroot] = rr = NodeRole(mroot);
                if (rr is GtaRole.Prop or GtaRole.Door || o.GtaType == GtaObjectType.Drawable)
                {
                    if (!childParts.TryGetValue(mroot, out var l)) childParts[mroot] = l = new();
                    l.Add(o);
                }
            }
            var isChild = childParts.Values.SelectMany(l => l).ToHashSet();
            // a Drawable node (GIMS) is its own model unless its role is set otherwise
            GtaRole NodeRole(SceneObject n) => n.GtaType == GtaObjectType.Model ? (n.Gta.Role == GtaRole.Auto ? GtaRole.Prop : n.Gta.Role) : ResolveRole(n, layout, st.PortalMaxGap, scene);
            var freeLights = new List<SceneObject>();
            foreach (var o in objs)
            {
                if (o.GtaType is GtaObjectType.Portal or GtaObjectType.CollisionGroup) continue; // a collision group is only a folder
                if (o.GtaType == GtaObjectType.Light) { if (!isChild.Contains(o) || !(roleOf.TryGetValue(ModelRoot(o), out var lr) && lr is GtaRole.Prop or GtaRole.Door)) freeLights.Add(o); continue; }
                if (o.GtaType == GtaObjectType.Collision) { if (!isChild.Contains(o)) colObjs.Add(o); continue; }
                if (o.GtaType == GtaObjectType.Drawable && o.Gta.LodLevel > 0) continue; // LOD meshes only go inside their Drawable
                if (o.GtaType == GtaObjectType.Model && !childParts.ContainsKey(o)) continue; // empty Drawable node
                if (isChild.Contains(o))
                {
                    // child mesh of a shell / exterior parent: goes with the parent's role
                    var rr = roleOf[ModelRoot(o)];
                    if (rr is GtaRole.Prop or GtaRole.Door) continue;
                    if (rr == GtaRole.Shell && isMlo) shellObjs.Add(o); else extObjs.Add(o);
                    continue;
                }
                var role = roleOf.TryGetValue(o, out var known) ? known : NodeRole(o);
                if (o.GtaType == GtaObjectType.Model && role is not (GtaRole.Prop or GtaRole.Door)) continue; // its meshes went to the shell / exterior
                switch (role)
                {
                    case GtaRole.Shell:
                        if (isMlo) shellObjs.Add(o); else extObjs.Add(o);
                        break;
                    case GtaRole.Exterior: extObjs.Add(o); break;
                    case GtaRole.Prop:
                    case GtaRole.Door:
                        {
                            // an instance of an object already placed: same model, one more entity
                            if (o.InstanceKey != null && propOfInstance.TryGetValue(o.InstanceKey, out var ipm))
                            {
                                ipm.Instances.Add(o);
                                if (Vector3.Distance(o.Scale, ipm.First.Scale) > 1e-3f) rep.Warnings.Add($"Instance '{o.Name}' has a different scale from '{ipm.First.Name}'; GTA entities can't scale, the first object's scale is used.");
                                break;
                            }
                            // explicit model names are shared on purpose; names derived from the object get the resource prefix
                            // (FiveM model names are global) and stay unique per mesh
                            bool explicitName = !string.IsNullOrWhiteSpace(o.Gta.ModelName);
                            var name = SafeName(explicitName ? o.Gta.ModelName : o.Name);
                            // a model name that is another object's name means "same model as that object"
                            if (explicitName && objs.Any(x => x != o && string.IsNullOrWhiteSpace(x.Gta.ModelName) && SafeName(x.Name) == name)) explicitName = false;
                            if (!explicitName && !name.StartsWith(res + "_")) name = res + "_" + name;
                            bool sharing = !string.IsNullOrWhiteSpace(o.Gta.ModelName);
                            while (reserved.Contains(name) || (!sharing && meshOfName.TryGetValue(name, out var other) && other.Mesh != o.Mesh)) name += "_2";
                            meshOfName.TryAdd(name, new PolyMeshRef(o.Mesh));
                            if (!props.TryGetValue(name, out var pm)) props[name] = pm = new PropModel { Name = name, First = o, Door = role == GtaRole.Door };
                            pm.Instances.Add(o);
                            if (o.InstanceKey != null) propOfInstance.TryAdd(o.InstanceKey, pm);
                            if (Vector3.Distance(o.Scale, pm.First.Scale) > 1e-3f) rep.Warnings.Add($"'{o.Name}' shares the model '{name}' but has a different scale; the first object's scale is used.");
                            break;
                        }
                }
            }

            var archetypes = new List<ArchetypeDef>();
            var mloEntities = new List<EntityDef>();
            var setEntities = new List<EntityDef>(); // MLO entity sets
            var extEntities = new List<EntityDef>();
            var written = new List<(string Dir, string Name, string Xml, string Kind, List<ExportTexture> Tex)>();
            var uvClips = new List<UvClip>();
            var keyedUvClips = new List<MoloMax.Core.Gta.Anim.YcdClip>(); // UV animation keyed on the timeline
            string animDir = null;

            // ---- MLO shell (split at the vertex budget)
            if (isMlo)
            {
                progress?.Invoke("Shell");
                if (shellObjs.Count == 0) rep.Errors.Add("The MLO has rooms but no shell geometry.");
                int part = 1;
                foreach (var group in SplitByVerts(shellObjs, st.MaxVertsPerModel, rep))
                {
                    string name = part == 1 ? res + "_shell" : $"{res}_shell_{part}";
                    part++;
                    var model = GtaModel.Build(name, scene, group.Select(o => (o, o.World)));
                    if (model.IsEmpty) continue;
                    if (part == 2) // first shell model carries the MLO's free lights
                        foreach (var l in freeLights.Where(l => InsideMlo(layout, l.Position, st.PortalMaxGap)).ToList()) { model.Lights.Add(ModelLight.From(l, Matrix4x4.Identity)); freeLights.Remove(l); }
                    model.LodDist = AutoLod(model, 150);
                    PivotShift(model);
                    WriteModel(model, mloDir, written, bank, st, archetypes, rep);
                    mloEntities.Add(new EntityDef
                    {
                        Archetype = name, Flags = 0, Guid = MetaXml.Jenkins(name + "_e"), Position = Vector3.Zero,
                        LodDist = model.LodDist, Min = model.Min, Max = model.Max,
                    });
                }
            }

            // ---- props and doors
            progress?.Invoke("Props");
            foreach (var pm in props.Values)
            {
                // shared pivot (Molo rule): every model keeps the scene origin, so the files import aligned in Blender;
                // doors (they swing), fragments (physics) and models placed several times keep their own pivot
                bool shared = st.SharedPivot && !pm.Door && !pm.First.Gta.Fragment && pm.Instances.Count == 1;
                var s = shared ? pm.First.World : Matrix4x4.CreateScale(pm.First.Scale);
                // children of the parent, in the parent's space (the entity carries its position / rotation)
                Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(pm.First.Rotation) * Matrix4x4.CreateTranslation(pm.First.Position), out var toParent);
                if (shared) toParent = Matrix4x4.Identity;
                var parts = childParts.TryGetValue(pm.First, out var cp) ? cp : new List<SceneObject>();
                var meshes = new List<(SceneObject, Matrix4x4)> { (pm.First, s) };
                meshes.AddRange(parts.Where(c => c.GtaType == GtaObjectType.Drawable && c.Gta.LodLevel == 0).Select(c => (c, c.World * toParent)));
                pm.Model = GtaModel.Build(pm.Name, scene, meshes);
                if (pm.Model.IsEmpty) continue;
                // LODs: meshes tagged Med / Low / Very low under the Drawable (GIMS), else generated
                var manual = Enumerable.Range(1, 3).Select(lv => parts.Where(c => c.GtaType == GtaObjectType.Drawable && c.Gta.LodLevel == lv).Select(c => (c, c.World * toParent)).ToList()).ToList();
                pm.Model.LodDist = pm.First.Gta.LodDist > 0 ? pm.First.Gta.LodDist : AutoLod(pm.Model, 100);
                BuildLods(pm.Model, scene, meshes, manual, pm.First.Gta, rep);
                if (shared) PivotShift(pm.Model);
                foreach (var l in parts.Where(c => c.GtaType == GtaObjectType.Light)) pm.Model.Lights.Add(ModelLight.From(l, toParent));
                int colType = Math.Clamp(pm.First.Gta.CollisionMaterial, 0, CollisionMaterials.Names.Length - 1);
                var colChildren = parts.Where(c => c.GtaType == GtaObjectType.Collision).ToList();
                if (colChildren.Count > 0)
                {
                    var cm = new CollisionMesh();
                    foreach (var c in colChildren) cm.AddCollisionObject(c, c.World * toParent);
                    pm.Model.EmbeddedCollision = cm;
                }
                else pm.Model.EmbeddedCollision = pm.First.Gta.PropCollision switch
                {
                    GtaPropCollision.Box => CollisionMesh.Box(pm.Model.Min, pm.Model.Max, colType),
                    GtaPropCollision.Mesh => MeshCollision(pm.First, s, colType),
                    _ => null,
                };
                bool anyInMlo = false;
                foreach (var o in pm.Instances)
                {
                    var centre = shared ? (pm.Model.Min + pm.Model.Max) * 0.5f : o.Position + Vector3.Transform((pm.Model.Min + pm.Model.Max) * 0.5f, o.Rotation);
                    bool inMlo = isMlo && InsideMlo(layout, centre, st.PortalMaxGap);
                    anyInMlo |= inMlo;
                    uint flags = o.Gta.EntityFlags != 0 ? o.Gta.EntityFlags : 1572872u;
                    var e = new EntityDef
                    {
                        Archetype = pm.Name, Flags = flags, Guid = MetaXml.Jenkins($"{res}_{o.Id}_{o.Name}"), LodDist = pm.Model.LodDist,
                    };
                    var ePos = shared ? Vector3.Zero : o.Position; var eRot = shared ? Quaternion.Identity : o.Rotation;
                    if (inMlo)
                    {
                        e.Position = ePos; e.Rotation = eRot;
                        (e.Min, e.Max) = o.WorldBounds();
                        if (!string.IsNullOrWhiteSpace(o.Gta.EntitySet))
                        {
                            e.EntitySet = SafeName(o.Gta.EntitySet.Trim());
                            e.Room = Math.Max(1, layout.RoomForCollision((e.Min + e.Max) * 0.5f));
                            setEntities.Add(e);
                        }
                        else mloEntities.Add(e);
                    }
                    else
                    {
                        e.Position = ToWorld(ePos); e.Rotation = heading * eRot;
                        var (a, b) = o.WorldBounds(); (e.Min, e.Max) = WorldBox(a, b, ToWorld);
                        extEntities.Add(e);
                    }
                }
                if (pm.First.Gta.Fragment) WriteFragment(pm.Model, pm.First, anyInMlo ? mloDir : extDir, written, bank, st, archetypes, rep);
                else WriteModel(pm.Model, anyInMlo ? mloDir : extDir, written, bank, st, archetypes, rep);

                // UV animation keyed on the timeline: one clip per shader that can animate UVs
                if (scene.Anim?.Track(pm.First.Id) is { } uvTrack && MoloMax.Core.Anim.Ch.Uv.Any(uvTrack.Has))
                {
                    var shaderMats = pm.Model.Parts.Select(p => p.Material).DistinctBy(m => m.Id).ToList();
                    int added = 0;
                    for (int si = 0; si < shaderMats.Count; si++)
                    {
                        var def = ShaderDb.FromPreset(shaderMats[si].Shader)?.Def;
                        if (def == null || !def.Params.Any(p => p.Name == "globalAnimUV0")) { rep.Warnings.Add($"{pm.Name}: shader '{shaderMats[si].Shader}' cannot animate UVs."); continue; }
                        if (MoloMax.Core.Anim.AnimGta.UvClipFor(scene, pm.First, pm.Name, si) is { } kc) { keyedUvClips.Add(kc); added++; }
                    }
                    if (added > 0)
                    {
                        var arch = archetypes.Last(a => a.Name == pm.Name);
                        arch.Flags |= UvAnimFlags; arch.ClipDictionary = res + "_anims";
                        animDir = anyInMlo ? mloDir : animDir ?? extDir;
                    }
                }
                // UV animation: one clip per animated shader of the model, all in <res>_anims.ycd
                var anim = pm.First.Gta.Anim;
                if (anim != null && anim.Kind != UvAnimKind.None && !keyedUvClips.Any(k => k.Name.StartsWith(pm.Name + "_uv_")))
                {
                    var shaderMats = pm.Model.Parts.Select(p => p.Material).DistinctBy(m => m.Id).ToList();
                    int added = 0;
                    for (int si = 0; si < shaderMats.Count; si++)
                    {
                        var mat = shaderMats[si];
                        if (anim.MaterialId >= 0 && mat.Id != anim.MaterialId) continue;
                        var def = ShaderDb.FromPreset(mat.Shader)?.Def;
                        if (def == null || !def.Params.Any(p => p.Name == "globalAnimUV0"))
                        {
                            rep.Warnings.Add($"{pm.Name}: shader '{mat.Shader}' of material '{mat.Name}' cannot animate UVs (use default, spec, normal, emissive or a decal shader).");
                            continue;
                        }
                        uvClips.Add(new UvClip(pm.Name, si, anim));
                        added++;
                    }
                    if (added > 0)
                    {
                        var arch = archetypes.Last(a => a.Name == pm.Name);
                        arch.Flags |= UvAnimFlags;          // UV animated archetype that plays on its own
                        arch.ClipDictionary = res + "_anims";
                        animDir = anyInMlo ? mloDir : animDir ?? extDir;
                    }
                }
            }

            // ---- exterior: joined models, max 60k vertices each (free lights outside the MLO ride on the first one)
            if (extObjs.Count > 0)
            {
                progress?.Invoke("Exterior");
                int part = 1;
                foreach (var group in SplitByVerts(extObjs, st.MaxVertsPerModel, rep))
                {
                    string name = part == 1 ? res + "_ext" : $"{res}_ext_{part}";
                    part++;
                    var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                    foreach (var o in group) { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
                    // shared pivot: every exterior chunk keeps the scene origin (they import aligned in Blender)
                    var pivot = st.SharedPivot ? Vector3.Zero : new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, mn.Z);
                    var model = GtaModel.Build(name, scene, group.Select(o => (o, o.World * Matrix4x4.CreateTranslation(-pivot))));
                    if (model.IsEmpty) continue;
                    if (part == 2) { foreach (var l in freeLights) model.Lights.Add(ModelLight.From(l, Matrix4x4.CreateTranslation(-pivot))); freeLights.Clear(); }
                    model.LodDist = AutoLod(model, 500);
                    if (st.ExteriorLods) BuildLods(model, scene, group.Select(o => (o, o.World * Matrix4x4.CreateTranslation(-pivot))).ToList(), null, new GtaInfo(), rep);
                    WriteModel(model, extDir, written, bank, st, archetypes, rep);
                    var e = new EntityDef
                    {
                        Archetype = name, Flags = 32, Guid = MetaXml.Jenkins(name + "_e"), Position = ToWorld(pivot), Rotation = heading,
                        LodDist = model.LodDist,
                    };
                    (e.Min, e.Max) = WorldBox(mn, mx, ToWorld);
                    extEntities.Add(e);
                }
            }

            // ---- collision: manual Collision objects plus (optionally) the automatic one from the geometry.
            // Triangle by triangle: near a room -> MLO collision (shell space, RoomID = room), elsewhere -> exterior (world space).
            progress?.Invoke("Collision");
            var colMloMesh = new CollisionMesh(); var colExtMesh = new CollisionMesh();
            var place = Matrix4x4.CreateFromQuaternion(heading) * Matrix4x4.CreateTranslation(scene.GtaOrigin);
            void AddCollision(SceneObject o, bool mayBeInterior)
            {
                int type = Math.Clamp(o.Gta.CollisionMaterial, 0, CollisionMaterials.Names.Length - 1);
                int forced = Math.Min(31, o.Gta.RoomIdOverride);
                uint flags = o.GtaType == GtaObjectType.Collision ? CollisionFlags.Resolve(o) : 0;
                CollisionMesh.ForEachTriangle(o, o.World, (a, b, c) =>
                {
                    int room = isMlo && mayBeInterior ? layout.RoomForCollision((a + b + c) / 3f) : 0;
                    if (room > 0) colMloMesh.AddTriangle(a, b, c, type, forced >= 0 ? forced : Math.Min(31, room), flags);
                    else colExtMesh.AddTriangle(Vector3.Transform(a, place), Vector3.Transform(b, place), Vector3.Transform(c, place), type, 0, flags);
                });
            }
            foreach (var o in colObjs) AddCollision(o, true);
            if (st.AutoCollision)
            {
                foreach (var o in shellObjs.Where(o => !o.Gta.NoAutoCollision)) AddCollision(o, true);
                foreach (var o in extObjs.Where(o => !o.Gta.NoAutoCollision)) AddCollision(o, false);
                rep.Info.Add(colObjs.Count > 0
                    ? $"Collision: automatic from the geometry + {colObjs.Count} Collision object(s)."
                    : "Collision generated from the geometry (mark decor 'No auto collision' in Object properties to leave it out).");
            }
            if (!colMloMesh.IsEmpty) { written.Add((mloDir, res, CwXml.BoundsFile(colMloMesh), "ybn", null)); // collision named after the MLO archetype (physicsDictionary) or the game crashes on entry
                if (isMlo) written.Add((mloDir, "_manifest", MetaXml.ManifestYmf(res, res + "_milo_", new[] { res }), "ymf", null)); rep.CollisionTriangles += colMloMesh.TriangleCount; }
            else if (isMlo) rep.Warnings.Add("The MLO has no collision.");
            if (!colExtMesh.IsEmpty) { written.Add((extDir, res + "_ext_col", CwXml.BoundsFile(colExtMesh), "ybn", null)); rep.CollisionTriangles += colExtMesh.TriangleCount; }

            // ---- rooms: attach entities, doors to portals
            if (isMlo)
            {
                for (int i = 0; i < mloEntities.Count; i++)
                {
                    var e = mloEntities[i];
                    bool shell = e.Archetype.StartsWith(res + "_shell");
                    int room;
                    if (shell) room = st.ShellInLimbo ? 0 : layout.RoomAt((e.Min + e.Max) * 0.5f) is int r && r > 0 ? r : 1;
                    else room = layout.RoomForCollision((e.Min + e.Max) * 0.5f);
                    if (props.TryGetValue(e.Archetype, out var pm) && pm.Door)
                    {
                        var c = (e.Min + e.Max) * 0.5f;
                        var portal = layout.Portals.OrderBy(p => Vector3.Distance((p.Corners[0] + p.Corners[2]) * 0.5f, c)).FirstOrDefault();
                        if (portal != null && Vector3.Distance((portal.Corners[0] + portal.Corners[2]) * 0.5f, c) < 2.5f)
                        {
                            portal.AttachedEntities.Add(i);
                            room = portal.RoomFrom; // a door belongs to the room on its side of the portal
                        }
                        else rep.Warnings.Add($"Door '{e.Archetype}' is not in a portal.");
                    }
                    layout.Rooms[room].Entities.Add(i);
                }
            }

            if (uvClips.Count > 0 || keyedUvClips.Count > 0)
            {
                var xml = keyedUvClips.Count == 0 ? AnimXml.ClipDictionary(uvClips)
                    : MoloMax.Core.Gta.Anim.YcdIO.ToXml(uvClips.Select(MoloMax.Core.Anim.AnimGta.FromUvClip).Concat(keyedUvClips));
                written.Add((animDir ?? (isMlo ? mloDir : extDir), res + "_anims", xml, "ycd", null));
                rep.Info.Add($"{uvClips.Count + keyedUvClips.Count} UV animation(s) at 30 fps in {res}_anims.ycd (archetype flags UV anims + auto start){(keyedUvClips.Count > 0 ? $", {keyedUvClips.Count} from the timeline keys" : "")}.");
            }

            // ---- ytyp and ymaps
            progress?.Invoke("Archetypes and placement");
            string metaDir = isMlo ? mloDir : extDir;
            written.Add((metaDir, res, MetaXml.Ytyp(res, archetypes, isMlo ? res : null, mloEntities, isMlo ? layout : null, setEntities), "ytyp", null));
            if (isMlo)
            {
                var wmn = new Vector3(float.MaxValue); var wmx = new Vector3(float.MinValue);
                foreach (var c in Corners(layout.Rooms[0].Min, layout.Rooms[0].Max)) { var p = ToWorld(c); wmn = Vector3.Min(wmn, p); wmx = Vector3.Max(wmx, p); }
                written.Add((mloDir, res + "_milo_", MetaXml.YmapMlo(res + "_milo_", res, scene.GtaOrigin, heading, layout.ExitPortals, wmn, wmx, (st.DefaultEntitySets ?? "").Split(',').Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => SafeName(n.Trim()))), "ymap", null));
            }
            if (extEntities.Count > 0) written.Add((extDir, res + "_exterior", MetaXml.YmapEntities(res + "_exterior", extEntities), "ymap", null));
            rep.Entities = mloEntities.Count + extEntities.Count;

            // ---- compile with CodeWalker
            progress?.Invoke("Compiling game files");
            Directory.CreateDirectory(root);
            var listFile = Path.Combine(root, ".molomax_export.txt");
            var previous = File.Exists(listFile) ? File.ReadAllLines(listFile).Where(l => l.Length > 0).ToList() : new List<string>();
            foreach (var (dir, name, xml, kind, tex) in written)
            {
                Directory.CreateDirectory(dir);
                if (st.WriteXml)
                {
                    var xd = Path.Combine(xmlDir, Path.GetRelativePath(root, dir));
                    Directory.CreateDirectory(xd);
                    File.WriteAllText(Path.Combine(xd, $"{name}.{kind}.xml"), xml);
                    if (tex != null) foreach (var t in tex) File.WriteAllBytes(Path.Combine(xd, t.FileName), t.Dds);
                }
                try
                {
                    byte[] data = Compile(xml, kind, tex, tmpDir);
                    var path = Path.Combine(dir, $"{name}.{kind}");
                    File.WriteAllBytes(path, data);
                    Verify(data, kind, name);
                    rep.Files.Add(Path.GetRelativePath(root, path));
                }
                catch (Exception ex)
                {
                    rep.Errors.Add($"{name}.{kind}: {ex.Message}");
                }
            }
            // files written by the previous export but not by this one would still stream: remove them
            foreach (var rel in previous.Except(rep.Files))
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is ".ydr" or ".yft" or ".ybn" or ".ytyp" or ".ymap" or ".ytd" or ".ycd" && File.Exists(full)) { try { File.Delete(full); rep.Info.Add("Removed old file " + rel); } catch { } }
            }
            File.WriteAllLines(listFile, rep.Files);
            // the .ytyp must be requested by the manifest, or the game never knows the models (nothing shows, no UV animation)
            var ytypRel = rep.Files.FirstOrDefault(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))?.Replace('\\', '/');
            var manifestPath = Path.Combine(root, "fxmanifest.lua");
            if (!File.Exists(manifestPath)) File.WriteAllText(manifestPath, Manifest(res, ytypRel));
            else if (ytypRel != null)
            {
                var txt = File.ReadAllText(manifestPath);
                // left alone when the resource loads its ytyp another way (a .ymf manifest, or the line is already there)
                if (!txt.Contains(Path.GetFileName(ytypRel), StringComparison.OrdinalIgnoreCase))
                {
                    File.AppendAllText(manifestPath, $"\ndata_file 'DLC_ITYP_REQUEST' '{ytypRel}'\n");
                    rep.Info.Add($"fxmanifest.lua: added data_file 'DLC_ITYP_REQUEST' '{ytypRel}' (the game needs it to know the models).");
                }
            }
            rep.Files.Add("fxmanifest.lua");
            rep.Warnings.AddRange(bank.Warnings.Distinct());
            rep.Info.Insert(0, isMlo
                ? $"MLO '{res}': {layout.Rooms.Count - 1} room(s), {layout.Portals.Count} portal(s) ({layout.ExitPortals} to the outside), {mloEntities.Count} entities."
                : $"Map '{res}': {extEntities.Count} entities.");
            if (st.WriteXml) rep.Info.Add($"CodeWalker XML sources: {xmlDir}");
        }
        catch (Exception ex)
        {
            rep.Errors.Add(ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
        return rep;
    }

    static void WriteModel(GtaModel model, string dir, List<(string, string, string, string, List<ExportTexture>)> written, TextureBank bank, GtaProjectSettings st, List<ArchetypeDef> archetypes, ExportReport rep)
    {
        if (model.VertexCount > st.MaxVertsPerModel) rep.Warnings.Add($"{model.Name} has {model.VertexCount} vertices (limit {st.MaxVertsPerModel}).");
        var xml = CwXml.Drawable(model, bank, !st.SeparateYtd, out var tex);
        written.Add((dir, model.Name, xml, "ydr", tex));
        if (st.SeparateYtd && tex.Count > 0) written.Add((dir, model.Name, CwXml.TextureDictionary(tex), "ytd", tex));
        archetypes.Add(new ArchetypeDef
        {
            Name = model.Name, LodDist = model.LodDist, Min = model.Min, Max = model.Max,
            TextureDictionary = st.SeparateYtd && tex.Count > 0 ? model.Name : "",
            HdTextureDist = MathF.Min(100, model.LodDist),
        });
        rep.Models++;
    }

    /// <summary>A physical prop as a .yft: collision from the prop setting (a box when none), mass from the object.</summary>
    static void WriteFragment(GtaModel model, SceneObject o, string dir, List<(string, string, string, string, List<ExportTexture>)> written, TextureBank bank, GtaProjectSettings st, List<ArchetypeDef> archetypes, ExportReport rep)
    {
        if (model.VertexCount > st.MaxVertsPerModel) rep.Warnings.Add($"{model.Name} has {model.VertexCount} vertices (limit {st.MaxVertsPerModel}).");
        int colType = Math.Clamp(o.Gta.CollisionMaterial, 0, CollisionMaterials.Names.Length - 1);
        var col = model.EmbeddedCollision != null && !model.EmbeddedCollision.IsEmpty ? model.EmbeddedCollision : CollisionMesh.Box(model.Min, model.Max, colType);
        var xml = CwXml.Fragment(model, bank, !st.SeparateYtd, col, o.Gta.FragMass, out var tex);
        written.Add((dir, model.Name, xml, "yft", tex));
        if (st.SeparateYtd && tex.Count > 0) written.Add((dir, model.Name, CwXml.TextureDictionary(tex), "ytd", tex));
        archetypes.Add(new ArchetypeDef
        {
            Name = model.Name, LodDist = model.LodDist, Min = model.Min, Max = model.Max,
            TextureDictionary = st.SeparateYtd && tex.Count > 0 ? model.Name : "",
            HdTextureDist = MathF.Min(100, model.LodDist), AssetType = "ASSET_TYPE_FRAGMENT",
        });
        rep.Models++;
        rep.Info.Add($"{model.Name}.yft: fragment, {o.Gta.FragMass:0.#} kg, {col.TriangleCount} collision triangle(s).");
    }

    /// <summary>
    /// Quick export of the given objects as ONE model file (.ydr or .yft), textures embedded, like Sollumz's
    /// "export selected". The model origin is the first object's origin; collision from the prop setting.
    /// </summary>
    public static string ExportModelFile(Scene scene, IReadOnlyList<SceneObject> objects, string path, Func<string, RgbaImage> imageLoader, IEnumerable<string> textureFolders = null, bool collisions = true)
    {
        if (objects.Count == 0) throw new InvalidOperationException("Nothing selected.");
        // a parent exports with its whole hierarchy (child meshes + collision), like GIMS
        objects = objects.Concat(objects.SelectMany(scene.Descendants)).Distinct().ToList();
        if (scene.Objects.Any(o => o.Modifiers?.Count > 0))
        {
            var ids = objects.Select(o => o.Id).ToHashSet();
            scene = scene.Clone();
            foreach (var o in scene.Objects) o.BakeModifiers(render: true);
            objects = scene.Objects.Where(o => ids.Contains(o.Id)).ToList();
        }
        bool fragment = path.EndsWith(".yft", StringComparison.OrdinalIgnoreCase);
        var name = SafeName(Path.GetFileNameWithoutExtension(path));
        var first = objects[0];
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(first.Rotation) * Matrix4x4.CreateTranslation(first.Position), out var toLocal);
        var highItems = objects.Where(o => o.GtaType == GtaObjectType.Drawable && o.Gta.LodLevel == 0).Select(o => (o, o.World * toLocal)).ToList();
        var model = GtaModel.Build(name, scene, highItems);
        if (model.IsEmpty) throw new InvalidOperationException("The selection has no polygons.");
        model.Lights.AddRange(objects.Where(o => o.GtaType == GtaObjectType.Light).Select(o => ModelLight.From(o, toLocal)));
        var manualLods = Enumerable.Range(1, 3).Select(lv => objects.Where(o => o.GtaType == GtaObjectType.Drawable && o.Gta.LodLevel == lv).Select(o => (o, o.World * toLocal)).ToList()).ToList();
        model.LodDist = first.Gta.LodDist > 0 ? first.Gta.LodDist : AutoLod(model, 100);
        if (!fragment) BuildLods(model, scene, highItems, manualLods, first.Gta, new ExportReport());
        int colType = Math.Clamp(first.Gta.CollisionMaterial, 0, CollisionMaterials.Names.Length - 1);
        var col = new CollisionMesh();
        if (collisions)
        {
            foreach (var o in objects)
            {
                if (o.GtaType == GtaObjectType.Collision) col.AddCollisionObject(o, o.World * toLocal);
            }
            bool hasColChild = !col.IsEmpty;
            if (!hasColChild) foreach (var o in objects) if (o.GtaType == GtaObjectType.Drawable && o.Gta.PropCollision == GtaPropCollision.Mesh) col.AddObject(o, o.World * toLocal, colType, _ => 0);
            if (col.IsEmpty && (fragment || first.Gta.PropCollision == GtaPropCollision.Box)) col = CollisionMesh.Box(model.Min, model.Max, colType);
        }
        var bank = new TextureBank { ImageLoader = imageLoader };
        if (textureFolders != null) bank.SearchFolders.AddRange(textureFolders);
        string xml; List<ExportTexture> tex;
        if (fragment) xml = CwXml.Fragment(model, bank, true, col, first.Gta.FragMass, out tex);
        else { if (!col.IsEmpty) model.EmbeddedCollision = col; xml = CwXml.Drawable(model, bank, true, out tex); }
        var tmp = Path.Combine(Path.GetTempPath(), "molomax_quick_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var data = Compile(xml, fragment ? "yft" : "ydr", tex, tmp);
            Verify(data, fragment ? "yft" : "ydr", name);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllBytes(path, data);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return $"{Path.GetFileName(path)}: {model.VertexCount:N0} vertices, {tex.Count} texture(s) embedded{(col.IsEmpty ? "" : $", {col.TriangleCount} collision triangle(s)")}";
    }

    internal static byte[] Compile(string xml, string kind, List<ExportTexture> textures, string tmpDir)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        switch (kind)
        {
            case "ydr":
            case "ytd":
            case "yft":
                {
                    var dir = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    if (textures != null) foreach (var t in textures) File.WriteAllBytes(Path.Combine(dir, t.FileName), t.Dds);
                    return kind switch
                    {
                        "ydr" => Cw.XmlYdr.GetYdr(doc, dir).Save(),
                        "yft" => Cw.XmlYft.GetYft(doc, dir).Save(),
                        _ => Cw.XmlYtd.GetYtd(doc, dir).Save(),
                    };
                }
            case "ybn": return Cw.XmlYbn.GetYbn(doc).Save();
            case "ycd": return Cw.XmlYcd.GetYcd(doc).Save();
            case "ymf": return Cw.XmlMeta.GetData(doc, Cw.MetaFormat.PSO, tmpDir) ?? throw new InvalidDataException("CodeWalker could not build the ymf.");
            case "ytyp":
            case "ymap":
                return Cw.XmlMeta.GetData(doc, Cw.MetaFormat.RSC, tmpDir) ?? throw new InvalidDataException("CodeWalker could not build the file.");
        }
        throw new NotSupportedException(kind);
    }

    /// <summary>Loads the compiled file back with CodeWalker to make sure the game will read it.</summary>
    internal static void Verify(byte[] data, string kind, string name)
    {
        switch (kind)
        {
            case "ydr": { var f = new Cw.YdrFile(); f.Load(data); if (f.Drawable == null) throw new InvalidDataException("drawable did not load back"); break; }
            case "yft": { var f = new Cw.YftFile(); f.Load(data); if (f.Fragment?.Drawable == null || f.Fragment.PhysicsLODGroup?.PhysicsLOD1 == null) throw new InvalidDataException("fragment did not load back"); break; }
            case "ybn": { var f = new Cw.YbnFile(); f.Load(data); if (f.Bounds == null) throw new InvalidDataException("bounds did not load back"); break; }
            case "ytyp": { var f = new Cw.YtypFile(); f.Load(data); if (f.AllArchetypes == null) throw new InvalidDataException("archetypes did not load back"); break; }
            case "ymap": { var f = new Cw.YmapFile(); f.Load(data); if (f.AllEntities == null) throw new InvalidDataException("entities did not load back"); break; }
            case "ytd": { var f = new Cw.YtdFile(); f.Load(data); break; }
            case "ycd": { var f = new Cw.YcdFile(); Cw.RpfFile.LoadResourceFile(f, data, 46); if (f.ClipDictionary == null) throw new InvalidDataException("clips did not load back"); break; }
        }
    }

    static string Manifest(string res, string ytypRel = null) =>
$@"fx_version 'cerulean'
game 'gta5'
lua54 'yes'

author 'Molo Modding'
description '{res} - exported with Polymorph'
version '1.0.0'

this_is_a_map 'yes'
" + (ytypRel != null ? $"\nfiles {{ '{ytypRel}' }}\ndata_file 'DLC_ITYP_REQUEST' '{ytypRel}'\n" : "");

    static CollisionMesh MeshCollision(SceneObject o, Matrix4x4 m, int type)
    {
        var c = new CollisionMesh();
        c.AddObject(o, m, type, _ => 0);
        return c;
    }

    /// <summary>
    /// Fills model.Lods (Med, Low, Very low) from hand-made LOD meshes, or by decimating the High meshes, and the
    /// switch distances (from the settings, else from the model size; Very low ends at the archetype distance).
    /// </summary>
    static void BuildLods(GtaModel model, Scene scene, List<(SceneObject, Matrix4x4)> high, List<List<(SceneObject, Matrix4x4)>> manual, GtaInfo info, ExportReport rep)
    {
        bool hasManual = manual != null && manual.Any(l => l.Count > 0);
        int tris = high.Sum(h => h.Item1.Mesh.TriangleCount);
        if (hasManual)
        {
            foreach (var lv in manual) { if (lv.Count == 0) break; var m = GtaModel.Build(model.Name, scene, lv); if (!m.IsEmpty) model.Lods.Add(m); }
        }
        else if (info.AutoLod && tris >= 300)
        {
            var ratios = info.LodRatios ?? new[] { 0.5f, 0.25f, 0.1f };
            foreach (var r in ratios.Take(3))
            {
                if (r <= 0 || r >= 1) break;
                var items = high.Where(h => h.Item1.Mesh.Faces.Count > 0).Select(h => (h.Item1.Clone(MoloMax.Core.Mesh.MeshSimplify.Decimate(h.Item1.Mesh, r)), h.Item2)).ToList();
                var m = GtaModel.Build(model.Name, scene, items);
                if (m.IsEmpty) break;
                model.Lods.Add(m);
            }
        }
        if (model.Lods.Count == 0) return;
        var d = info.LodDistances ?? new float[4];
        float hi = d[0] > 0 ? d[0] : MathF.Max(20, model.Radius * 3);
        var dists = new float[4];
        dists[0] = hi;
        for (int i = 1; i < 4; i++) dists[i] = d[i] > 0 ? d[i] : dists[i - 1] * 2;
        int last = model.Lods.Count; // index of the last used level
        model.LodDist = MathF.Max(model.LodDist, dists[last]);
        dists[last] = model.LodDist;
        for (int i = last + 1; i < 4; i++) dists[i] = model.LodDist;
        model.LodDists = dists;
        rep.Info.Add($"{model.Name}: {model.Lods.Count} LOD level(s) ({(hasManual ? "your LOD meshes" : "generated")}), High up to {dists[0]:0} m.");
    }

    /// <summary>Shared pivot: the game measures the LOD distance from the entity origin (scene 0,0,0), not from the
    /// geometry, so every distance grows by how far the geometry sits from the origin (else far rooms vanish).</summary>
    static void PivotShift(GtaModel m)
    {
        float off = ((m.Min + m.Max) * 0.5f).Length();
        if (off < 1f) return;
        m.LodDist = MathF.Ceiling((m.LodDist + off) / 10f) * 10f;
        if (m.LodDists != null) for (int i = 0; i < m.LodDists.Length; i++) m.LodDists[i] = MathF.Min(m.LodDists[i] + off, m.LodDist);
    }

    static float AutoLod(GtaModel m, float min) => MathF.Round(MathF.Max(min, m.Radius * 3f + 50f) / 10f) * 10f;

    public static bool InsideMlo(MloLayout layout, Vector3 p, float gap) =>
        layout.Rooms.Count > 1 && Inside(p, layout.Rooms[0].Min - new Vector3(gap), layout.Rooms[0].Max + new Vector3(gap));

    static Vector3 Center(SceneObject o) { var (a, b) = o.WorldBounds(); return (a + b) * 0.5f; }
    static bool Inside(Vector3 p, Vector3 mn, Vector3 mx) => p.X >= mn.X && p.Y >= mn.Y && p.Z >= mn.Z && p.X <= mx.X && p.Y <= mx.Y && p.Z <= mx.Z;
    sealed record PolyMeshRef(Core.Mesh.PolyMesh Mesh);

    /// <summary>World bounds of a scene box placed with the map transform (all 8 corners).</summary>
    static (Vector3, Vector3) WorldBox(Vector3 mn, Vector3 mx, Func<Vector3, Vector3> toWorld)
    {
        var a = new Vector3(float.MaxValue); var b = new Vector3(float.MinValue);
        foreach (var c in Corners(mn, mx)) { var p = toWorld(c); a = Vector3.Min(a, p); b = Vector3.Max(b, p); }
        return (a, b);
    }

    static IEnumerable<Vector3> Corners(Vector3 mn, Vector3 mx)
    {
        for (int i = 0; i < 8; i++) yield return new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z);
    }

    /// <summary>Groups objects so each group stays under the vertex budget (whole objects only).</summary>
    static IEnumerable<List<SceneObject>> SplitByVerts(List<SceneObject> objs, int budget, ExportReport rep)
    {
        var cur = new List<SceneObject>(); int count = 0;
        foreach (var o in objs.OrderBy(o => o.Position.X).ThenBy(o => o.Position.Y))
        {
            int v = o.Mesh.Faces.Sum(f => f.V.Length); // corners: upper bound of exported vertices
            if (v > budget) rep.Warnings.Add($"'{o.Name}' alone may exceed {budget} vertices; split it.");
            if (cur.Count > 0 && count + v > budget) { yield return cur; cur = new List<SceneObject>(); count = 0; }
            cur.Add(o); count += v;
        }
        if (cur.Count > 0) yield return cur;
    }
}
