using System.Numerics;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.IO;

public sealed class ImportOptions
{
    /// <summary>glTF and OBJ are Y-up; MoloMax and GTA are Z-up. On by default.</summary>
    public bool YUpToZUp = true;
    /// <summary>Uniform scale applied on import (1 = file units are metres).</summary>
    public float Scale = 1f;
    /// <summary>Merge vertices that share a position, so the mesh is connected and editable (UV seams kept per corner).</summary>
    public bool WeldVertices = true;
    /// <summary>Smoothing groups from the angle between faces (0 = flat).</summary>
    public float AutoSmoothAngle = 40f;
    /// <summary>Join every mesh of the file into one object.</summary>
    public bool MergeIntoOne;
    /// <summary>Folder where embedded textures are written. Null = next to the model in "&lt;name&gt;_textures".</summary>
    public string TextureFolder;
    /// <summary>Texture name -> file already known by the caller: the .ytd next to the model is then not read at all
    /// (the library builder hands the textures of a whole interior in one go).</summary>
    public Dictionary<string, string> KnownTextures;
    /// <summary>Textures already in the texture folder are not written again (library builder: same textures in many models).</summary>
    public bool SkipExistingTextures;
    /// <summary>GTA models read from the game: only the textures their materials use are written (not the whole dictionary).</summary>
    public bool OnlyUsedTextures;
    /// <summary>GTA models: more texture dictionaries to look in (parsed .ytd objects, first wins) when a material names a
    /// texture the model does not carry: the archetype's dictionary from the .ytyp, the game's. Written only when used.</summary>
    public System.Collections.Generic.IList<object> ExtraDictionaries;
}

/// <summary>A mesh read from a file. Face MatId values index <see cref="ImportResult.Materials"/>.</summary>
public sealed class ImportedMesh
{
    public string Name;
    public PolyMesh Mesh;
    /// <summary>GTA files: drawable or collision (bounds).</summary>
    public Scenes.GtaObjectType GtaType = Scenes.GtaObjectType.Drawable;
    /// <summary>Collision material index (materials.dat) for imported bounds.</summary>
    public int CollisionMaterial = -1;
    /// <summary>RoomID of imported bounds (0: outside / none).</summary>
    public int CollisionRoom;
    /// <summary>Drawable dictionaries (.ydd): name hash of the drawable this mesh comes from (its archetype).</summary>
    public uint DrawableHash;
}

public sealed class ImportResult
{
    public readonly List<ImportedMesh> Meshes = new();
    public readonly List<GtaMaterial> Materials = new();
    public readonly List<string> Warnings = new();
    public readonly List<string> TexturesWritten = new();
    /// <summary>GTA files: keep the file origin as the object origin (no pivot to base).</summary>
    public bool KeepFileOrigin;
}

public static class ModelImporter
{
    public static readonly string[] Extensions = { ".glb", ".gltf", ".obj", ".fbx", ".ydr", ".ydd", ".yft", ".ybn" };

    public static bool CanImport(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || Gta.Import.GtaImporter.CanImport(path);

    public static ImportResult Import(string path, ImportOptions opt = null)
    {
        opt ??= new ImportOptions();
        if (Gta.Import.GtaImporter.CanImport(path)) return Gta.Import.GtaImporter.Import(path, opt); // Z-up, metres, own origin
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var res = ext switch
        {
            ".glb" or ".gltf" => GltfImporter.Import(path, opt),
            ".obj" => ObjImporter.Import(path, opt),
            ".fbx" => FbxImporter.Import(path, opt), // already Z-up metres and finished
            _ => throw new NotSupportedException($"{ext} is not supported. Use .fbx, .glb, .gltf or .obj."),
        };
        if (ext != ".fbx") Finish(res, opt);
        return res;
    }

    /// <summary>Axis conversion, scale, welding, smoothing and the optional merge.</summary>
    internal static void Finish(ImportResult res, ImportOptions opt)
    {
        // (x, y, z) Y-up → (x, -z, y) Z-up: a +90° rotation about X, so winding stays outward.
        var m = Matrix4x4.CreateScale(opt.Scale);
        if (opt.YUpToZUp) m *= new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
        foreach (var im in res.Meshes)
        {
            im.Mesh.Transform(m);
            if (opt.WeldVertices) MeshOps.WeldByDistance(im.Mesh, 1e-5f * MathF.Max(opt.Scale, 1e-3f));
            im.Mesh.CleanFaces();
            im.Mesh.RemoveUnusedVertices();
            if (opt.AutoSmoothAngle > 0 && im.Mesh.Faces.Count > 0) MeshOps.AutoSmooth(im.Mesh, null, opt.AutoSmoothAngle);
        }
        res.Meshes.RemoveAll(x => x.Mesh.Faces.Count == 0);
        if (opt.MergeIntoOne && res.Meshes.Count > 1)
        {
            var all = new PolyMesh();
            foreach (var im in res.Meshes) all.Append(im.Mesh);
            var name = res.Meshes[0].Name;
            res.Meshes.Clear();
            res.Meshes.Add(new ImportedMesh { Name = name, Mesh = all });
        }
        if (res.Materials.Count == 0) res.Materials.Add(GtaMaterial.CreateDefault(0, "imported", new Vector3(0.75f)));
    }

    /// <summary>Lowercase letters, digits and underscores (GTA-safe names).</summary>
    public static string SafeName(string s, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray();
        var r = new string(chars).Trim('_');
        while (r.Contains("__")) r = r.Replace("__", "_");
        if (r.Length == 0) return fallback;
        return r.Length > 48 ? r[..48] : r;
    }
}
