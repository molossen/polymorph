using System.Numerics;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Scenes;

/// <summary>What the object becomes in GTA.</summary>
/// <summary>
/// What an object is for GTA. Drawable = a game mesh (geometry). Model = a GIMS / Sollumz "Drawable": an empty
/// node whose child game meshes are joined into one model, with its collision as a child too.
/// </summary>
public enum GtaObjectType { Drawable, Collision, MloRoom, Portal, Model, Light, CollisionGroup }

/// <summary>What a drawable becomes when the scene is exported to GTA.</summary>
public enum GtaRole
{
    /// <summary>MLO: shell when it is a room or touches no room bounds... see GtaExporter.ResolveRole.</summary>
    Auto,
    /// <summary>Joined into the MLO shell model.</summary>
    Shell,
    /// <summary>Its own model, placed as an entity (MLO prop or exterior prop).</summary>
    Prop,
    /// <summary>A door: its own model, attached to the portal it stands in.</summary>
    Door,
    /// <summary>Joined into the exterior model(s) (split at 60k vertices).</summary>
    Exterior,
    /// <summary>Not exported.</summary>
    Ignore,
}

/// <summary>Embedded collision of a prop model.</summary>
public enum GtaPropCollision { None, Box, Mesh }

public enum UvAnimKind { None, Scroll, Spritesheet }

/// <summary>UV animation of a prop (exported to the resource's .ycd at 30 fps, archetype flag 1024).</summary>
public sealed class GtaAnim
{
    public UvAnimKind Kind;
    /// <summary>Scroll: texture repeats travelled per loop along U and V, and loop length in seconds.</summary>
    public float ScrollU = 1, ScrollV, Duration = 2;
    /// <summary>Spritesheet: grid and how many 30 fps frames each cell stays.</summary>
    public int Cols = 4, Rows = 1, HoldFrames = 4;
    /// <summary>Material to animate (-1 = every material of the model that supports UV animation).</summary>
    public int MaterialId = -1;

    public GtaAnim Clone() => (GtaAnim)MemberwiseClone();
}

/// <summary>Per-object GTA export settings (the GIMS Evo / Sollumz object properties).</summary>
public enum GtaLightKind { Point, Spot, Capsule }

/// <summary>A GTA light (LightAttributes of a drawable). Position / direction come from the object.</summary>
public sealed class GtaLight
{
    public GtaLightKind Kind = GtaLightKind.Point;
    public Vector3 Colour = new(1f, 0.86f, 0.7f);
    public float Intensity = 8f;
    /// <summary>Reach in metres (GTA "falloff").</summary>
    public float Range = 6f;
    public float FalloffExponent = 8f;
    public float ConeInner = 20f, ConeOuter = 50f;
    public float CapsuleLength = 1f;
    public bool CastShadows = true, DrawVolume, NoSpecular, InteriorOnly, ExteriorOnly;
    public float VolumeIntensity = 0.3f, VolumeSize = 1f;
    public float CoronaSize, CoronaIntensity;
    public byte Flashiness;
    /// <summary>Hours the light is on (bit per hour, 0xFFFFFF = always).</summary>
    public uint TimeFlags = 0xFFFFFF;

    /// <summary>Colour from a blackbody temperature instead of <see cref="Colour"/> (Kelvin, 1000 .. 12000).</summary>
    public bool UseTemperature;
    public float Temperature = 4000f;
    /// <summary>Size of the light source (soft highlights and soft shadow edge), metres.</summary>
    public float Radius = 0.1f;
    /// <summary>Viewport only: how much the light adds to diffuse and to specular (1 = physical).</summary>
    public float DiffuseFactor = 1f, SpecularFactor = 1f;
    /// <summary>Spot blend (0 hard edge .. 1 fully soft); keeps ConeInner = ConeOuter * (1 - blend).</summary>
    public float SpotBlend => ConeOuter > 0 ? Math.Clamp(1 - ConeInner / ConeOuter, 0, 1) : 0;

    /// <summary>The colour actually emitted (temperature or picked colour).</summary>
    public Vector3 EffectiveColour => UseTemperature ? Kelvin(Temperature) : Colour;

    /// <summary>Blackbody colour of a temperature (Tanner Helland's fit), normalised so the brightest channel is 1.</summary>
    public static Vector3 Kelvin(float k)
    {
        float t = Math.Clamp(k, 1000, 40000) / 100f;
        float r = t <= 66 ? 255 : 329.698727446f * MathF.Pow(t - 60, -0.1332047592f);
        float g = t <= 66 ? 99.4708025861f * MathF.Log(t) - 161.1195681661f : 288.1221695283f * MathF.Pow(t - 60, -0.0755148492f);
        float b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
        var c = new Vector3(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255)) / 255f;
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m > 0 ? c / m : Vector3.One;
    }

    // ---- the rest of the game's light attributes (Sollumz shows the same fields; written as is to the .ydr / XML)
    public byte GroupId, LightHash, ShadowBlur;
    /// <summary>Distances (m, 0 = game default) where the light / its shadow / its specular / its volume fade out.</summary>
    public byte LightFadeDistance, ShadowFadeDistance, SpecularFadeDistance, VolumetricFadeDistance;
    public float ShadowNearClip = 0.01f, CoronaZBias = 0.1f;
    public Vector3 VolumeOuterColour = Vector3.One;
    public float VolumeOuterIntensity, VolumeOuterExponent = 1f;
    public Vector3 CullingPlaneNormal = new(0, 0, -1);
    public float CullingPlaneOffset = 1f;
    /// <summary>Texture projected by a spot (texture name, empty = none).</summary>
    public string ProjectedTexture = "";
    /// <summary>Other light flag bits (buzzing, corona only, cull plane...), see <see cref="FlagNames"/>.</summary>
    public uint ExtraFlags;

    /// <summary>Light flag bits by name, as the game and Sollumz name them.</summary>
    public static readonly (uint Bit, string Name)[] FlagNames =
    {
        (1u << 0, "Interior only"), (1u << 1, "Exterior only"), (1u << 2, "Don't use in cutscene"), (1u << 3, "Vehicle"), (1u << 4, "FX"),
        (1u << 5, "Texture projection"), (1u << 6, "Cast shadows"), (1u << 7, "Cast static shadows"), (1u << 8, "Cast dynamic shadows"),
        (1u << 9, "Calc from sun"), (1u << 10, "Enable buzzing"), (1u << 11, "Force buzzing"), (1u << 12, "Draw volume"), (1u << 13, "No specular"),
        (1u << 14, "Both interior and exterior"), (1u << 15, "Corona only"), (1u << 16, "Not in reflection"), (1u << 17, "Only in reflection"),
        (1u << 18, "Use cull plane"), (1u << 19, "Use volume outer colour"), (1u << 20, "Cast higher res shadows"), (1u << 21, "Cast only low res shadows"),
        (1u << 22, "Far LOD light"), (1u << 23, "Don't light alpha"), (1u << 24, "Cast shadows if possible"), (1u << 25, "Cutscene"),
        (1u << 26, "Moving light source"), (1u << 27, "Use vehicle twin"), (1u << 28, "Force medium LOD light"), (1u << 29, "Corona only LOD light"),
        (1u << 30, "Delay render"), (1u << 31, "Already tested for occlusion"),
    };
    /// <summary>Bits driven by the main checkboxes (the others live in <see cref="ExtraFlags"/>).</summary>
    public const uint MainFlagBits = 1u | 2u | 64u | 128u | 4096u | 8192u;

    public uint Flags => (InteriorOnly ? 1u : 0) | (ExteriorOnly ? 2u : 0) | (CastShadows ? 64u | 128u : 0) | (DrawVolume ? 4096u : 0) | (NoSpecular ? 8192u : 0)
                         | (ExtraFlags & ~MainFlagBits) | (VolumeOuterIntensity > 0 ? 1u << 19 : 0);
    public GtaLight Clone() => (GtaLight)MemberwiseClone();
}

public sealed class GtaInfo
{
    /// <summary>Light settings (objects of type Light).</summary>
    public GtaLight Light;
    /// <summary>Map mode: the archetype this entity places (game or project model).</summary>
    public string MapArchetype;
    /// <summary>MLO entity set this prop belongs to (switched on / off by scripts). Empty = always there.</summary>
    public string EntitySet = "";
    /// <summary>LOD level of a game mesh under a Drawable (0 High, 1 Med, 2 Low, 3 Very low), like GIMS.</summary>
    public int LodLevel;
    /// <summary>Generate Med / Low / Very low automatically at export when no LOD meshes were made by hand.</summary>
    public bool AutoLod = true;
    /// <summary>Triangles kept per LOD level (Med, Low, Very low).</summary>
    public float[] LodRatios = { 0.5f, 0.25f, 0.1f };
    /// <summary>Distance where each level stops (High, Med, Low, Very low). 0 = automatic from the model size.</summary>
    public float[] LodDistances = { 0, 0, 0, 0 };
    public GtaRole Role = GtaRole.Auto;
    /// <summary>Model / archetype name. Empty = the object name in lowercase. Objects sharing a name share one model.</summary>
    public string ModelName = "";
    /// <summary>Entity and archetype LOD distance in metres; 0 = automatic.</summary>
    public float LodDist;
    public uint ArchetypeFlags;
    /// <summary>Entity flags; 0 = automatic (props 1572872, shell 0).</summary>
    public uint EntityFlags;
    public GtaPropCollision PropCollision = GtaPropCollision.Box;
    /// <summary>Collision material (index in materials.dat, see CollisionMaterials). Used by collision objects and generated collision.</summary>
    public int CollisionMaterial = 1;
    /// <summary>MLO room: timecycle modifier (empty = none).</summary>
    public string RoomTimecycle = "";
    public uint RoomFlags;
    /// <summary>Collision objects: force a RoomID (-1 = the room the triangle is in).</summary>
    public int RoomIdOverride = -1;
    /// <summary>Collision material flags (FLAG_STAIRS...); used when <see cref="CollisionFlagsAuto"/> is off.</summary>
    public uint CollisionFlags;
    /// <summary>Flags worked out from the name, the material and the shape (stairs, glass, water, foliage).</summary>
    public bool CollisionFlagsAuto = true;
    /// <summary>Portal objects: portal flags (1 = one-way, 2 = link, 4 = mirror ...).</summary>
    public uint PortalFlags;
    /// <summary>Left out of the automatic collision (decor, glass, things players walk through).</summary>
    public bool NoAutoCollision;
    /// <summary>MLO room read from a .ytyp: only its bounds (a volume, not a model): drawn as a wire box, not picked in the view.</summary>
    public bool BoundsOnly;
    public GtaAnim Anim = new();
    /// <summary>Export this prop as a fragment (.yft): a physical object that can be pushed and knocked over.</summary>
    public bool Fragment;
    /// <summary>Fragment mass in kg.</summary>
    public float FragMass = 10f;

    public GtaInfo Clone() { var c = (GtaInfo)MemberwiseClone(); c.Anim = (Anim ?? new GtaAnim()).Clone(); c.Light = Light?.Clone(); c.LodRatios = (float[])(LodRatios ?? new[] { 0.5f, 0.25f, 0.1f }).Clone(); c.LodDistances = (float[])(LodDistances ?? new float[4]).Clone(); return c; }
}

/// <summary>Scene-wide export settings: one scene = one FiveM resource.</summary>
public sealed class GtaProjectSettings
{
    /// <summary>Resource / MLO name (lowercase, letters digits and underscores).</summary>
    public string ResourceName = "molo_new_map";
    /// <summary>Heading of the scene on the map, degrees around Z (the scene origin sits at Scene.GtaOrigin).</summary>
    public float Heading;
    public string OutputFolder = "";
    /// <summary>Generate the MLO collision from the shell when there is no collision object.</summary>
    public bool AutoCollision = true;
    /// <summary>Attach the shell to the limbo room (Mimi's rule: otherwise it can crash).</summary>
    public bool ShellInLimbo = true;
    /// <summary>Every model keeps the scene origin as pivot (they import aligned in Blender). Doors, fragments and instanced models keep their own.</summary>
    public bool SharedPivot = true;
    /// <summary>Generate Med / Low / Very low inside the joined exterior models too.</summary>
    public bool ExteriorLods;
    /// <summary>Entity sets active when the interior loads (comma separated).</summary>
    public string DefaultEntitySets = "";
    /// <summary>Embedded textures (default) or one .ytd per model linked through the archetype textureDictionary.</summary>
    public bool SeparateYtd;
    public int MaxTextureSize = 1024;
    /// <summary>Largest gap between two rooms that still gets an automatic portal.</summary>
    public float PortalMaxGap = 1.0f;
    public int MaxVertsPerModel = 60000;
    public bool WriteXml = true;

    public GtaProjectSettings Clone() => (GtaProjectSettings)MemberwiseClone();
}

/// <summary>How a splat capture is drawn (simple settings).</summary>
public sealed class SplatLook
{
    /// <summary>Size of the blobs (1 = as captured).</summary>
    public float Size = 1f;
    /// <summary>Opacity (1 = as captured).</summary>
    public float Opacity = 1f;
    /// <summary>Brightness (1 = as captured).</summary>
    public float Brightness = 1f;
    /// <summary>Hide splats fainter than this (0..1): cleans the haze around a capture.</summary>
    public float MinOpacity = 0.05f;
    /// <summary>Keep only what is inside the box (the object's box, see Crop).</summary>
    public bool Crop;
    public System.Numerics.Vector3 CropMin, CropMax;
    public SplatLook Clone() => (SplatLook)MemberwiseClone();
}

/// <summary>One node in the scene: an Editable Poly with a transform (3ds Max object).</summary>
public sealed class SceneObject
{
    public int Id;
    public string Name = "Object";
    public PolyMesh Mesh = new();
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public Vector3 WireColour = new(0.34f, 0.88f, 0.78f);
    public bool Hidden, Frozen;
    /// <summary>Outliner folder (3ds Max layer style). Empty = the default folder.</summary>
    public string Folder = "";
    /// <summary>Parent object id (-1 = none), like 3ds Max / GIMS: the meshes of a model and its collision sit under it.</summary>
    public int ParentId = -1;
    public GtaObjectType GtaType = GtaObjectType.Drawable;
    public GtaInfo Gta = new();
    /// <summary>Tripo task that produced this mesh (lets Smart UV / texture / retopo run on it).</summary>
    public string TripoTaskId;
    /// <summary>Molo Paint texture set (layers), saved next to the scene in <see cref="PaintFile"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Paint.PaintDoc Paint;
    public string PaintFile;
    /// <summary>Gaussian splat capture shown by this object (.ply / .spz / .splat), or null. The mesh is then only its box.</summary>
    public string SplatFile;
    public SplatLook Splat;
    [System.Text.Json.Serialization.JsonIgnore] public global::MoloMax.Core.Splats.SplatCloud SplatCloud;
    /// <summary>Instance group (3ds Max instance / Blender linked duplicate): objects with the same key share one mesh,
    /// its modifiers and its paint. Null = a unique object. GTA export writes the model once and places it several times.</summary>
    public string InstanceKey;
    /// <summary>A line drawn point by point (3ds Max spline): when set, the mesh is built from it (see ShapeBuilder).</summary>
    public global::MoloMax.Core.Mesh.ShapeData Shape;
    /// <summary>A bone (3ds Max bone / Blender armature bone) for the Skin modifier: drawn as a bone, never exported to GTA.
    /// It points along its local +Z, <see cref="BoneLength"/> long.</summary>
    public bool IsBone; public float BoneLength = 0.3f;
    /// <summary>UV animation rows at the current frame (GTA globalAnimUV0 / 1), w = 1 when set; drawn only, never saved.</summary>
    public Vector4 UvRow0, UvRow1;

    /// <summary>Blender-style modifier stack, evaluated on top of <see cref="Mesh"/> (non-destructive).</summary>
    public List<global::MoloMax.Core.Modifiers.Modifier> Modifiers = new();

    /// <summary>Bumped whenever the mesh changes so the viewport re-uploads it.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int Version = System.Threading.Interlocked.Increment(ref versionSeed);

    PolyMesh evalCache; int evalVersion = -1; bool evalEdit, evalRender;

    /// <summary>The mesh after the modifier stack (viewport, edit mode or render / export rules). Cached per version.</summary>
    public PolyMesh Evaluated(bool editMode = false, bool render = false)
    {
        if (Modifiers == null || Modifiers.Count == 0) return Mesh;
        if (evalCache != null && evalVersion == Version && evalEdit == editMode && evalRender == render) return evalCache;
        evalCache = global::MoloMax.Core.Modifiers.ModifierStack.Evaluate(Mesh, Modifiers, editMode, render);
        evalVersion = Version; evalEdit = editMode; evalRender = render;
        return evalCache;
    }

    // ---- editing a modifier of the stack (Edit Poly / Unwrap UVW)
    /// <summary>While a modifier is edited, <see cref="Mesh"/> is that modifier's working mesh and <see cref="Modifiers"/> holds
    /// only the modifiers after it; the real base mesh and full stack wait here.</summary>
    public sealed class ModEditState { public PolyMesh Base; public List<global::MoloMax.Core.Modifiers.Modifier> All; public int Index; }
    [System.Text.Json.Serialization.JsonIgnore] public ModEditState ModEdit;
    /// <summary>The full stack, also while a modifier is being edited.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<global::MoloMax.Core.Modifiers.Modifier> FullStack => ModEdit?.All ?? Modifiers;
    /// <summary>The modifier being edited, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public global::MoloMax.Core.Modifiers.Modifier EditedModifier => ModEdit?.All[ModEdit.Index];

    /// <summary>Starts editing the editable modifier at <paramref name="index"/> of the stack.</summary>
    public bool BeginModifierEdit(int index)
    {
        if (ModEdit != null) { if (ModEdit.Index == index) return true; CommitModifierEdit(); }
        var all = Modifiers;
        if (index < 0 || index >= all.Count || all[index] is not global::MoloMax.Core.Modifiers.IEditableModifier em) return false;
        var input = global::MoloMax.Core.Modifiers.ModifierStack.Evaluate(Mesh, all.Take(index).ToList(), false, false);
        var work = em.BeginEdit(input);
        if (ReferenceEquals(work, Mesh)) work = Mesh.Clone();
        ModEdit = new ModEditState { Base = Mesh, All = all, Index = index };
        Mesh = work;
        Modifiers = all.Skip(index + 1).ToList();
        Touch();
        return true;
    }

    /// <summary>Ends the modifier edit: the modifier keeps the edited mesh, the object gets its base mesh and full stack back.</summary>
    public void CommitModifierEdit()
    {
        var st = ModEdit; if (st == null) return;
        var input = global::MoloMax.Core.Modifiers.ModifierStack.Evaluate(st.Base, st.All.Take(st.Index).ToList(), false, false);
        if (st.All[st.Index] is global::MoloMax.Core.Modifiers.IEditableModifier em) em.Store(Mesh, input);
        // the stored base stays untouched (undo snapshots may share it): the object gets its own copy
        Mesh = st.Base.Clone();
        Modifiers = st.All;
        ModEdit = null;
        Touch();
    }

    /// <summary>Bakes the whole stack into the mesh (export snapshot, or "Apply" on every modifier).</summary>
    public void BakeModifiers(bool render = true)
    {
        if (Modifiers == null || Modifiers.Count == 0) return;
        Mesh = global::MoloMax.Core.Modifiers.ModifierStack.Evaluate(Mesh, Modifiers, false, render).Clone();
        Modifiers = new();
        Touch();
    }

    public Matrix4x4 World => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    static int versionSeed = 1000;
    /// <summary>Marks the mesh as changed. Versions are unique across all objects, so caches keyed on (id, version) never mix two states.</summary>
    public void Touch() => Version = System.Threading.Interlocked.Increment(ref versionSeed);

    [System.Text.Json.Serialization.JsonIgnore] int statVersion = -1, statTris;
    [System.Text.Json.Serialization.JsonIgnore] PolyMesh statMesh; long statSig;
    static long StatSig(PolyMesh m)
    {
        long h = m.Faces.Count * 1000003L + m.Verts.Count;
        int n = m.Faces.Count;
        for (int i = 0; i < Math.Min(n, 8); i++) h = h * 31 + m.Faces[i].V.Length;
        for (int i = Math.Max(8, n - 8); i < n; i++) h = h * 31 + m.Faces[i].V.Length;
        return h;
    }
    /// <summary>Triangle count, cached until the mesh changes (the viewport shows it every frame).</summary>
    public int TriangleCount
    {
        get
        {
            if (statVersion != Version || !ReferenceEquals(statMesh, Mesh))
            {
                // points moved (a drag) leave the polygons as they were: same mesh, same counts, same first / last polygons → no recount
                long sig = StatSig(Mesh);
                if (!ReferenceEquals(statMesh, Mesh) || sig != statSig) statTris = Mesh.TriangleCount;
                statVersion = Version; statMesh = Mesh; statSig = sig;
            }
            return statTris;
        }
    }

    public SceneObject Clone() => Clone(null);

    /// <summary>Copy that uses <paramref name="mesh"/> instead of cloning the mesh (undo snapshots share unchanged meshes).</summary>
    public SceneObject Clone(PolyMesh mesh) => new()
    {
        Id = Id, Name = Name, Mesh = mesh ?? Mesh.Clone(), Position = Position, Rotation = Rotation, Scale = Scale,
        WireColour = WireColour, Hidden = Hidden, Frozen = Frozen, Folder = Folder, ParentId = ParentId, GtaType = GtaType, Version = Version,
        Gta = (Gta ?? new GtaInfo()).Clone(), TripoTaskId = TripoTaskId, Paint = Paint, PaintFile = PaintFile, SplatFile = SplatFile, Splat = Splat?.Clone(), SplatCloud = SplatCloud, InstanceKey = InstanceKey, UvRow0 = UvRow0, UvRow1 = UvRow1, Shape = Shape?.Copy(), IsBone = IsBone, BoneLength = BoneLength,
        Modifiers = (Modifiers ?? new()).Select(m => m.Clone()).ToList(),
        ModEdit = ModEdit == null ? null : new ModEditState { Base = ModEdit.Base, All = ModEdit.All.Select(m => m.Clone()).ToList(), Index = ModEdit.Index },
    };

    // local box cached per mesh version (picking thousands of map entities must not walk every vertex)
    PolyMesh boundsMesh; int boundsVersion = int.MinValue; Vector3 boundsMin, boundsMax;
    sealed class SharedBox { public int Version; public Vector3 Min, Max; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, SharedBox> sharedBounds = new();
    public (Vector3 Min, Vector3 Max) LocalBounds()
    {
        if (!ReferenceEquals(boundsMesh, Mesh) || boundsVersion != Version || Mesh.Verts.Count == 0)
        {
            // instances share mesh and version: the first one walks the vertices, the others reuse its box
            if (sharedBounds.TryGetValue(Mesh, out var sb) && sb.Version == Version && Mesh.Verts.Count > 0) (boundsMin, boundsMax) = (sb.Min, sb.Max);
            else
            {
                (boundsMin, boundsMax) = Mesh.Verts.Count == 0 ? (Vector3.Zero, Vector3.Zero) : Mesh.Bounds();
                sharedBounds.AddOrUpdate(Mesh, new SharedBox { Version = Version, Min = boundsMin, Max = boundsMax });
            }
            boundsMesh = Mesh; boundsVersion = Version;
        }
        return (boundsMin, boundsMax);
    }

    /// <summary>World box of the cached local box (a little larger than <see cref="WorldBounds"/> when rotated, much cheaper).</summary>
    // last world box, valid while the object and its mesh stay where they are
    Vector3 wbPos, wbScale, wbMin, wbMax; Quaternion wbRot; int wbVersion = int.MinValue; PolyMesh wbMesh;
    public (Vector3 Min, Vector3 Max) FastWorldBounds()
    {
        if (Mesh.Verts.Count == 0) return (Position, Position);
        if (ParentId < 0 && wbVersion == Version && ReferenceEquals(wbMesh, Mesh) && wbPos == Position && wbRot == Rotation && wbScale == Scale) return (wbMin, wbMax);
        var r = FastWorldBoundsCore();
        if (ParentId < 0) { wbVersion = Version; wbMesh = Mesh; wbPos = Position; wbRot = Rotation; wbScale = Scale; (wbMin, wbMax) = r; }
        return r;
    }

    (Vector3 Min, Vector3 Max) FastWorldBoundsCore()
    {
        var (mn, mx) = LocalBounds(); var w = World;
        var a = new Vector3(float.MaxValue); var b = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z), w);
            a = Vector3.Min(a, p); b = Vector3.Max(b, p);
        }
        return (a, b);
    }

    /// <summary>World-space bounding box.</summary>
    public (Vector3 Min, Vector3 Max) WorldBounds()
    {
        var w = World;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        if (Mesh.Verts.Count == 0) return (Position, Position);
        foreach (var v in Mesh.Verts) { var p = Vector3.Transform(v, w); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    /// <summary>Bakes rotation and scale into the vertices (3ds Max Reset XForm + Collapse).</summary>
    public void ResetXForm()
    {
        var m = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation);
        Mesh.Transform(m);
        Rotation = Quaternion.Identity; Scale = Vector3.One;
        Touch();
    }

    /// <summary>Moves the pivot to the centre of the geometry without moving the geometry.</summary>
    public void CenterPivot()
    {
        if (Mesh.Verts.Count == 0) return;
        var (mn, mx) = Mesh.Bounds();
        var c = (mn + mx) * 0.5f;
        for (int i = 0; i < Mesh.Verts.Count; i++) Mesh.Verts[i] -= c;
        Position += Vector3.Transform(c * Scale, Rotation);
        Touch();
    }

    /// <summary>Pivot at the bottom centre (what GTA props and Max boxes use).</summary>
    public void PivotToBase()
    {
        if (Mesh.Verts.Count == 0) return;
        var (mn, mx) = Mesh.Bounds();
        var c = new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, mn.Z);
        for (int i = 0; i < Mesh.Verts.Count; i++) Mesh.Verts[i] -= c;
        Position += Vector3.Transform(c * Scale, Rotation);
        Touch();
    }
}

public sealed class Scene
{
    public List<SceneObject> Objects = new();
    public List<GtaMaterial> Materials = new();
    /// <summary>Outliner folders, in display order (objects point to them by name).</summary>
    public List<string> Folders = new();
    /// <summary>Floor plan of the MLO room generator (kept with the scene so it can be edited again).</summary>
    public MoloMax.Core.Tools.RoomPlanData Plan = new();
    public int NextObjectId = 1;
    public int NextMaterialId = 1;
    /// <summary>World offset of the scene origin in GTA coordinates (where the model sits on the map).</summary>
    public Vector3 GtaOrigin;
    public GtaProjectSettings Gta = new();
    /// <summary>Keyframe animation: timeline range and the curves of each object / the camera.</summary>
    public MoloMax.Core.Anim.SceneAnim Anim = new();

    // 3ds Max wire colours, picked in turn for new objects
    static readonly Vector3[] wires =
    {
        new(0.34f, 0.88f, 0.78f), new(0.90f, 0.60f, 0.84f), new(0.60f, 0.84f, 0.90f), new(0.69f, 0.58f, 0.10f),
        new(0.11f, 0.58f, 0.69f), new(0.53f, 0.35f, 0.84f), new(0.34f, 0.68f, 0.34f), new(0.88f, 0.56f, 0.34f),
        new(0.84f, 0.90f, 0.65f), new(0.69f, 0.10f, 0.10f),
    };

    public Scene()
    {
        Materials.Add(GtaMaterial.CreateDefault(0, "default_grey", new Vector3(0.72f, 0.72f, 0.72f)));
    }

    public GtaMaterial Material(int id)
    {
        if (Materials.Count == 0) Materials.Add(GtaMaterial.CreateDefault(0, "default_grey", new Vector3(0.72f)));
        // id -> place in the list, checked on every use (the list is edited in many places): drawing a map asks thousands of times a frame
        if (matIndex.TryGetValue(id, out var i) && i < Materials.Count && Materials[i].Id == id) return Materials[i];
        // the list usually only grew (map streaming adds materials one by one): index the new ones first
        if (!matIndex.ContainsKey(id) && matIndexed <= Materials.Count)
        {
            for (int k = matIndexed; k < Materials.Count; k++) matIndex.TryAdd(Materials[k].Id, k);
            matIndexed = Materials.Count;
            if (matIndex.TryGetValue(id, out i) && i < Materials.Count && Materials[i].Id == id) return Materials[i];
        }
        matIndex.Clear();
        for (int k = 0; k < Materials.Count; k++) matIndex.TryAdd(Materials[k].Id, k);
        matIndexed = Materials.Count;
        return matIndex.TryGetValue(id, out i) ? Materials[i] : Materials[0];
    }
    readonly Dictionary<int, int> matIndex = new(); int matIndexed;

    public GtaMaterial AddMaterial(string name, Vector3 colour)
    {
        var m = GtaMaterial.CreateDefault(NextMaterialId++, name, colour);
        Materials.Add(m);
        return m;
    }

    public SceneObject Add(string baseName, PolyMesh mesh, Vector3 position, GtaObjectType type = GtaObjectType.Drawable)
    {
        var o = new SceneObject
        {
            Id = NextObjectId++,
            Name = UniqueName(baseName),
            Mesh = mesh,
            Position = position,
            GtaType = type,
            WireColour = wires[(NextObjectId - 2 + wires.Length) % wires.Length],
        };
        Objects.Add(o);
        return o;
    }

    /// <summary>Max naming: Box001, Box002… (lowercase is applied at GTA export time).</summary>
    public string UniqueName(string baseName)
    {
        // names cached between calls: adding thousands of objects (instances, imports) stays linear
        if (nameSet == null || !ReferenceEquals(nameList, Objects) || nameCount > Objects.Count)
        {
            nameSet = new HashSet<string>(Objects.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            nameHint = new(StringComparer.OrdinalIgnoreCase); nameList = Objects;
        }
        else for (int k = nameCount; k < Objects.Count; k++) nameSet.Add(Objects[k].Name);
        nameCount = Objects.Count;
        for (int i = nameHint.TryGetValue(baseName, out var h) ? h : 1; ; i++)
        {
            var n = $"{baseName}{i:000}";
            if (nameSet.Contains(n) || Objects.Count < 64 && Objects.Any(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
            nameHint[baseName] = i + 1; nameSet.Add(n);
            return n;
        }
    }
    HashSet<string> nameSet; Dictionary<string, int> nameHint; List<SceneObject> nameList; int nameCount;

    // id index: big maps hold thousands of objects and Find runs many times per frame
    Dictionary<int, SceneObject> index; List<SceneObject> indexList; int indexCount = -1;
    public SceneObject Find(int id)
    {
        if (index == null || !ReferenceEquals(indexList, Objects) || indexCount != Objects.Count) RebuildIndex();
        if (index.TryGetValue(id, out var o) && o.Id == id) return o;
        // not there: an id that no longer exists is asked for many times per frame (a stale active object); the index
        // is rebuilt once for it, not on every call (that cost 20-40 ms per frame on big maps)
        if (o == null && id == lastMissId && lastMissCount == Objects.Count && ReferenceEquals(lastMissList, Objects)) return null;
        RebuildIndex(); // ids changed in place, or a new object since the last build
        if (index.TryGetValue(id, out o)) return o;
        lastMissId = id; lastMissCount = Objects.Count; lastMissList = Objects;
        return null;
    }
    int lastMissId = int.MinValue, lastMissCount = -1; List<SceneObject> lastMissList;
    void RebuildIndex()
    {
        index = new Dictionary<int, SceneObject>(Objects.Count);
        foreach (var o in Objects) index.TryAdd(o.Id, o);
        indexList = Objects; indexCount = Objects.Count;
    }

    // ---- instances (shared mesh, one transform each)
    /// <summary>Objects that share <paramref name="o"/>'s mesh, itself included.</summary>
    public List<SceneObject> InstancesOf(SceneObject o) =>
        o?.InstanceKey == null ? (o == null ? new() : new() { o }) : Objects.Where(x => x.InstanceKey == o.InstanceKey).ToList();
    public int InstanceCount(SceneObject o) => o?.InstanceKey == null ? 1 : Objects.Count(x => x.InstanceKey == o.InstanceKey);

    /// <summary>New object sharing <paramref name="src"/>'s mesh, modifiers and paint (3ds Max "Instance").</summary>
    public SceneObject AddInstance(SceneObject src, string name = null)
    {
        src.InstanceKey ??= NewInstanceKey();
        var c = src.Clone(src.Mesh);
        c.Id = NextObjectId++;
        c.Name = name ?? UniqueName(BaseName(src.Name));
        c.Modifiers = src.Modifiers;
        c.ParentId = src.ParentId;
        Objects.Add(c);
        return c;
    }
    public static string NewInstanceKey() => Guid.NewGuid().ToString("N")[..12];
    /// <summary>"chair_2" / "Box003" -> "chair_" / "Box" (base for the next unique name).</summary>
    public static string BaseName(string n)
    {
        var b = n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return b.Length > 0 ? b : "Object";
    }

    /// <summary>Moves the pivot by <paramref name="local"/> (mesh space) without moving the geometry; every instance keeps its place.</summary>
    public void MovePivotLocal(SceneObject o, Vector3 local)
    {
        if (local == Vector3.Zero) return;
        for (int i = 0; i < o.Mesh.Verts.Count; i++) o.Mesh.Verts[i] -= local;
        foreach (var m in InstancesOf(o)) m.Position += Vector3.Transform(local * m.Scale, m.Rotation);
        o.Touch();
    }

    /// <summary>Gives the object its own copy of the mesh (3ds Max "Make Unique").</summary>
    public void MakeUnique(SceneObject o)
    {
        if (o.InstanceKey == null) return;
        var key = o.InstanceKey;
        o.Mesh = o.Mesh.Clone();
        o.Modifiers = (o.Modifiers ?? new()).Select(m => m.Clone()).ToList();
        o.InstanceKey = null;
        o.Touch();
        var rest = Objects.Where(x => x.InstanceKey == key).ToList();
        if (rest.Count == 1) rest[0].InstanceKey = null; // a group of one is just an object
    }

    /// <summary>
    /// Keeps every instance group on one mesh: the member changed last (highest version) wins and the others take its mesh,
    /// modifiers and paint. Call once per frame and after loading / undo. Returns true when something was shared again.
    /// </summary>
    public bool SyncInstances()
    {
        // nothing moved since the last call (same members, versions and meshes): skip, big maps have thousands of instances
        long sig = Objects.Count;
        foreach (var o in Objects)
        {
            if (o.InstanceKey == null) continue;
            sig = sig * 1000003 + o.Id * 7919L + o.Version + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Mesh) * 31L
                + (o.Modifiers == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Modifiers)) * 17L
                + (o.Paint == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Paint));
        }
        if (sig == syncSig) return false;
        bool r = SyncInstancesCore();
        syncSig = r ? long.MinValue : sig; // after a change the next call checks again
        return r;
    }
    long syncSig = long.MinValue;

    bool SyncInstancesCore()
    {
        Dictionary<string, SceneObject> lead = null;
        foreach (var o in Objects)
        {
            if (o.InstanceKey == null) continue;
            lead ??= new();
            if (!lead.TryGetValue(o.InstanceKey, out var l) || o.Version > l.Version) lead[o.InstanceKey] = o;
        }
        if (lead == null) return false;
        bool changed = false;
        foreach (var o in Objects)
        {
            if (o.InstanceKey == null) continue;
            var l = lead[o.InstanceKey];
            if (ReferenceEquals(l, o)) continue;
            if (!ReferenceEquals(o.Mesh, l.Mesh) || o.Version != l.Version || !ReferenceEquals(o.Modifiers, l.Modifiers) || !ReferenceEquals(o.Paint, l.Paint))
            {
                o.Mesh = l.Mesh; o.Modifiers = l.Modifiers; o.Paint = l.Paint; o.PaintFile = l.PaintFile; o.Version = l.Version;
                changed = true;
            }
        }
        return changed;
    }

    // ---- hierarchy (parent / children, GIMS style)
    public SceneObject Parent(SceneObject o) => o.ParentId >= 0 ? Find(o.ParentId) : null;
    public IEnumerable<SceneObject> Children(SceneObject o) => Objects.Where(c => c.ParentId == o.Id);
    public IEnumerable<SceneObject> Descendants(SceneObject o)
    {
        foreach (var c in Children(o)) { yield return c; foreach (var d in Descendants(c)) yield return d; }
    }
    /// <summary>Topmost ancestor (the model) of an object, itself when it has no parent.</summary>
    public SceneObject Root(SceneObject o)
    {
        var seen = new HashSet<int>();
        while (Parent(o) is { } p && seen.Add(o.Id)) o = p;
        return o;
    }
    /// <summary>Removes objects; their children move up to the nearest surviving ancestor.</summary>
    public void RemoveObjects(Func<SceneObject, bool> pred)
    {
        var dead = Objects.Where(pred).Select(o => o.Id).ToHashSet();
        foreach (var o in Objects)
        {
            if (dead.Contains(o.Id)) continue;
            var guard = 0;
            while (o.ParentId >= 0 && dead.Contains(o.ParentId) && guard++ < 1000) o.ParentId = Find(o.ParentId)?.ParentId ?? -1;
        }
        Objects.RemoveAll(o => dead.Contains(o.Id));
    }

    /// <summary>Links child under parent (null = unlink). Refuses cycles. Folder follows the parent.</summary>
    public bool SetParent(SceneObject child, SceneObject parent)
    {
        if (parent == null) { child.ParentId = -1; return true; }
        if (parent == child || Descendants(child).Contains(parent)) return false;
        child.ParentId = parent.Id;
        child.Folder = parent.Folder;
        foreach (var d in Descendants(child)) d.Folder = parent.Folder;
        return true;
    }

    public Scene Clone() => Clone(null);

    public Scene Clone(Func<SceneObject, PolyMesh> meshFor)
    {
        var s = new Scene
        {
            NextObjectId = NextObjectId, NextMaterialId = NextMaterialId, GtaOrigin = GtaOrigin, Gta = (Gta ?? new GtaProjectSettings()).Clone(),
            Objects = Objects.Select(o => o.Clone(meshFor?.Invoke(o))).ToList(),
            Materials = Materials.Select(m => m.Clone()).ToList(),
            Folders = Folders.ToList(),
            Plan = System.Text.Json.JsonSerializer.Deserialize<MoloMax.Core.Tools.RoomPlanData>(System.Text.Json.JsonSerializer.Serialize(Plan ?? new())),
            Anim = (Anim ?? new()).Clone(),
        };
        return s;
    }

    sealed class StatBox { public int Faces = -1, Tris; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, StatBox> statTris = new();
    public (int Objects, int Polys, int Tris, int Verts) Stats()
    {
        // triangle counts kept per mesh (thousands of map entities share a few hundred game meshes)
        int p = 0, t = 0, v = 0;
        foreach (var o in Objects)
        {
            var m = o.Mesh; if (m == null) continue;
            p += m.Faces.Count; v += m.Verts.Count;
            if (o.Modifiers is { Count: > 0 }) { t += o.TriangleCount; continue; }
            var c = statTris.GetValue(m, _ => new StatBox());
            if (c.Faces != m.Faces.Count) { c.Faces = m.Faces.Count; c.Tris = m.TriangleCount; }
            t += c.Tris;
        }
        return (Objects.Count, p, t, v);
    }
}
