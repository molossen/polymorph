using System.Numerics;
using System.Xml;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.IO;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;
using SDX = SharpDX;

namespace MoloMax.Core.Gta.Import;

/// <summary>
/// Reads GTA V resources through CodeWalker: .ydr, .ydd, .yft, .ybn (binary or CodeWalker .xml).
/// Drawables become editable meshes (high LOD, one material per shader, embedded textures saved as .dds),
/// bounds become collision objects with their collision material, fragments keep their bones' placement.
/// Same result as importing with Sollumz: the file origin stays the object origin.
/// </summary>
public static class GtaImporter
{
    public static readonly string[] Extensions = { ".ydr", ".ydd", ".yft", ".ybn" };

    public static bool CanImport(string path)
    {
        var p = path.ToLowerInvariant();
        return Extensions.Any(e => p.EndsWith(e) || p.EndsWith(e + ".xml"));
    }

    sealed class Ctx
    {
        public ImportResult Res;
        public string Name, Dir, TexDir;
        public bool SkipExisting;
        /// <summary>Weld + smoothing of the game triangles (off for the texture library build: only materials are read).</summary>
        public bool Weld = true;
        public readonly Dictionary<string, string> TextureFiles = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> MaterialKey = new();
        /// <summary>Textures of the parent dictionaries (name.parentN.ytd): written only when a material uses them.</summary>
        public readonly Dictionary<string, Cw.Texture> Pending = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Only the textures a material uses are written (into Pending first).</summary>
        public bool Lazy;
        public static Dictionary<uint, string> PresetByHash;
    }

    public static ImportResult Import(string path, ImportOptions opt = null)
    {
        opt ??= new ImportOptions();
        var c = new Ctx { Res = new ImportResult(), Dir = Path.GetDirectoryName(Path.GetFullPath(path)), SkipExisting = opt.SkipExistingTextures, Weld = opt.WeldVertices };
        var lower = path.ToLowerInvariant();
        bool xml = lower.EndsWith(".xml");
        string ext = Path.GetExtension(xml ? lower[..^4] : lower);
        c.Name = ModelImporter.SafeName(Path.GetFileNameWithoutExtension(xml ? Path.GetFileNameWithoutExtension(path) : path), "model");
        c.TexDir = opt.TextureFolder ?? Path.Combine(c.Dir, c.Name + "_textures");
        EnsurePresets();

        if (opt.KnownTextures != null) foreach (var kv in opt.KnownTextures) c.TextureFiles[kv.Key] = kv.Value;
        // textures from a .ytd with the same name next to the file (Sollumz does the same lookup)
        var ytdPath = Path.Combine(c.Dir, c.Name + ".ytd");
        if (opt.KnownTextures == null)
        {
            if (File.Exists(ytdPath)) { try { var ytd = new Cw.YtdFile(); ytd.Load(File.ReadAllBytes(ytdPath)); SaveTextures(c, ytd.TextureDict); } catch (Exception e) { c.Res.Warnings.Add($"{c.Name}.ytd: {e.Message}"); } }
            // parent dictionaries (game files browser): big shared dictionaries, so only what the model uses gets written
            foreach (var pf in Directory.GetFiles(c.Dir, c.Name + ".parent*.ytd").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var ytd = new Cw.YtdFile(); ytd.Load(File.ReadAllBytes(pf));
                    foreach (var t in ytd.TextureDict?.Textures?.data_items ?? Array.Empty<Cw.Texture>())
                        if (t?.Name != null && !c.TextureFiles.ContainsKey(t.Name)) c.Pending.TryAdd(t.Name, t);
                }
                catch (Exception e) { c.Res.Warnings.Add($"{Path.GetFileName(pf)}: {e.Message}"); }
            }
        }

        if (opt.ExtraDictionaries != null)
            foreach (var o in opt.ExtraDictionaries)
                if (o is Cw.YtdFile ey)
                    foreach (var t in ey.TextureDict?.Textures?.data_items ?? Array.Empty<Cw.Texture>())
                        if (t?.Name != null && !c.TextureFiles.ContainsKey(t.Name)) c.Pending.TryAdd(t.Name, t);

        switch (ext)
        {
            case ".ydr":
                DoYdr(c, xml ? Cw.XmlYdr.GetYdr(LoadXml(path), c.Dir) : LoadRes<Cw.YdrFile>(path, (y, d) => y.Load(d)));
                break;
            case ".ydd":
                DoYdd(c, xml ? Cw.XmlYdd.GetYdd(LoadXml(path), c.Dir) : LoadRes<Cw.YddFile>(path, (y, d) => y.Load(d)));
                break;
            case ".yft":
                DoYft(c, xml ? Cw.XmlYft.GetYft(LoadXml(path), c.Dir) : LoadRes<Cw.YftFile>(path, (y, d) => y.Load(d)));
                break;
            case ".ybn":
                {
                    var f = xml ? Cw.XmlYbn.GetYbn(LoadXml(path)) : LoadRes<Cw.YbnFile>(path, (y, d) => y.Load(d));
                    if (f?.Bounds == null) throw new InvalidDataException("No bounds in this file.");
                    AddBounds(c, f.Bounds, c.Name, Matrix4x4.Identity);
                    break;
                }
            default: throw new NotSupportedException($"{ext} is not a GTA model file.");
        }
        return Finish(c);
    }

    static ImportResult Finish(Ctx c)
    {
        // trees: their plain vertex-coloured shapes are the game's shadow casters, never seen
        if (c.Res.Materials.Any(m => m.Shader != null && m.Shader.StartsWith("trees", StringComparison.OrdinalIgnoreCase)))
            foreach (var m in c.Res.Materials) if (string.Equals(m.Shader, "cpv_only", StringComparison.OrdinalIgnoreCase)) m.ShadowOnly = true;
        foreach (var m in c.Res.Meshes) { m.Mesh.CleanFaces(); m.Mesh.RemoveUnusedVertices(); }
        c.Res.Meshes.RemoveAll(m => m.Mesh.Faces.Count == 0);
        if (c.Res.Materials.Count == 0) c.Res.Materials.Add(GtaMaterial.CreateDefault(0, "imported", new Vector3(0.75f)));
        c.Res.KeepFileOrigin = true;
        return c.Res;
    }

    static void DoYdr(Ctx c, Cw.YdrFile f)
    {
        if (f?.Drawable == null) throw new InvalidDataException("No drawable in this file.");
        AddDrawable(c, f.Drawable, c.Name, null);
        if (f.Drawable.Bound != null) AddBounds(c, f.Drawable.Bound, c.Name + "_col", Matrix4x4.Identity);
    }

    static void DoYdd(Ctx c, Cw.YddFile f)
    {
        var list = f?.Drawables ?? Array.Empty<Cw.Drawable>();
        if (list.Length == 0) throw new InvalidDataException("No drawable in this dictionary.");
        var hashOf = new Dictionary<Cw.Drawable, uint>();
        if (f.Dict != null) foreach (var kv in f.Dict) if (kv.Value != null) hashOf.TryAdd(kv.Value, kv.Key);
        foreach (var d in list)
        {
            var n = ModelImporter.SafeName(d.Name, c.Name);
            int first = c.Res.Meshes.Count;
            AddDrawable(c, d, n, null);
            if (d.Bound != null) AddBounds(c, d.Bound, n + "_col", Matrix4x4.Identity);
            uint h = hashOf.TryGetValue(d, out var hv) ? hv : 0;
            for (int i = first; i < c.Res.Meshes.Count; i++) c.Res.Meshes[i].DrawableHash = h;
        }
    }

    static void DoYft(Ctx c, Cw.YftFile f)
    {
        var frag = f?.Fragment ?? throw new InvalidDataException("No fragment in this file.");
        if (frag.Drawable != null) AddDrawable(c, frag.Drawable, c.Name, frag.Drawable.Skeleton);
        var lod = frag.PhysicsLODGroup?.PhysicsLOD1;
        if (lod?.Bound != null) AddBounds(c, lod.Bound, c.Name + "_col", Matrix4x4.Identity);
        int glass = frag.GlassWindows?.data_items?.Length ?? 0;
        if (glass > 0) c.Res.Warnings.Add($"{glass} breakable glass window(s) are not imported.");
        if ((frag.Cloths?.data_items?.Length ?? 0) > 0) c.Res.Warnings.Add("Cloth is not imported.");
    }

    /// <summary>
    /// A model read straight from the game archive (no file written and read back): <paramref name="raw"/> is the
    /// decompressed resource, <paramref name="textures"/> its texture dictionary then the parent ones (already parsed,
    /// shared between models). Textures the materials use are written into <paramref name="folder"/>/name_textures.
    /// </summary>
    /// <summary>Profiling of in-memory imports (ticks summed over threads): 0 parse, 1 convert, 2 texture files, 3 finish.</summary>
    public static readonly long[] Prof = new long[4];
    static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

    public static ImportResult ImportResource(string name, string ext, byte[] raw, Cw.RpfResourceFileEntry entry, IList<Cw.YtdFile> textures, string folder, ImportOptions opt = null)
    {
        opt ??= new ImportOptions();
        var c = new Ctx { Res = new ImportResult(), Dir = folder, SkipExisting = opt.SkipExistingTextures, Weld = opt.WeldVertices, Lazy = opt.OnlyUsedTextures };
        c.Name = ModelImporter.SafeName(name, "model");
        c.TexDir = opt.TextureFolder ?? Path.Combine(folder, c.Name + "_textures");
        EnsurePresets();
        if (opt.KnownTextures != null) foreach (var kv in opt.KnownTextures) c.TextureFiles[kv.Key] = kv.Value;
        if (textures != null)
            for (int k = 0; k < textures.Count; k++)
            {
                var ytd = textures[k]; if (ytd?.TextureDict == null) continue;
                try
                {
                    if (k == 0) SaveTextures(c, ytd.TextureDict);
                    else foreach (var t in ytd.TextureDict.Textures?.data_items ?? Array.Empty<Cw.Texture>())
                            if (t?.Name != null && !c.TextureFiles.ContainsKey(t.Name)) c.Pending.TryAdd(t.Name, t);
                }
                catch (Exception e) { c.Res.Warnings.Add($"textures: {e.Message}"); }
            }
        switch (ext.ToLowerInvariant())
        {
            case ".ydr": { long t0 = Now; var y = new Cw.YdrFile(); y.Load(raw, entry); long t1 = Now; DoYdr(c, y); System.Threading.Interlocked.Add(ref Prof[0], t1 - t0); System.Threading.Interlocked.Add(ref Prof[1], Now - t1); break; }
            case ".ydd": { long t0 = Now; var y = new Cw.YddFile(); y.Load(raw, entry); long t1 = Now; DoYdd(c, y); System.Threading.Interlocked.Add(ref Prof[0], t1 - t0); System.Threading.Interlocked.Add(ref Prof[1], Now - t1); break; }
            case ".yft": { long t0 = Now; var y = new Cw.YftFile(); y.Load(raw, entry); long t1 = Now; DoYft(c, y); System.Threading.Interlocked.Add(ref Prof[0], t1 - t0); System.Threading.Interlocked.Add(ref Prof[1], Now - t1); break; }
            default: throw new NotSupportedException($"{ext} is not a GTA model file.");
        }
        long tf = Now; var res = Finish(c); System.Threading.Interlocked.Add(ref Prof[3], Now - tf);
        return res;
    }

    static XmlDocument LoadXml(string path) { var d = new XmlDocument(); d.Load(path); return d; }

    static T LoadRes<T>(string path, Action<T, byte[]> load) where T : new()
    {
        var f = new T();
        load(f, File.ReadAllBytes(path));
        return f;
    }

    static void EnsurePresets()
    {
        if (Ctx.PresetByHash != null) return;
        var d = new Dictionary<uint, string>();
        foreach (var p in ShaderDb.AllPresets()) { d.TryAdd(MetaXml.Jenkins(p + ".sps"), p); d.TryAdd(MetaXml.Jenkins(p), p); }
        foreach (var p in GtaShaders.All) { d.TryAdd(MetaXml.Jenkins(p.Name + ".sps"), p.Name); d.TryAdd(MetaXml.Jenkins(p.Name), p.Name); }
        Ctx.PresetByHash = d;
    }

    // ------------------------------------------------------------------------------------------ textures

    static void SaveTextures(Ctx c, Cw.TextureDictionary td)
    {
        var items = td?.Textures?.data_items;
        if (items == null) return;
        if (c.Lazy)
        {
            // MAP mode: only the textures a material uses are written, once (a dictionary holds many more)
            foreach (var t in items) if (t?.Name != null && !c.TextureFiles.ContainsKey(t.Name)) c.Pending.TryAdd(t.Name, t);
            return;
        }
        foreach (var t in items)
        {
            if (t?.Name == null || c.TextureFiles.ContainsKey(t.Name)) continue;
            try
            {
                var file = Path.Combine(c.TexDir, ModelImporter.SafeName(t.Name, "texture") + ".dds");
                if (c.SkipExisting && File.Exists(file)) { c.TextureFiles[t.Name] = file; continue; }
                var dds = CodeWalker.Utils.DDSIO.GetDDSFile(t);
                Directory.CreateDirectory(c.TexDir);
                File.WriteAllBytes(file, dds);
                c.TextureFiles[t.Name] = file;
                c.Res.TexturesWritten.Add(file);
            }
            catch (Exception e) { c.Res.Warnings.Add($"Texture {t.Name}: {e.Message}"); }
        }
    }

    static string TextureRef(Ctx c, object data)
    {
        if (data is not Cw.TextureBase tb || string.IsNullOrEmpty(tb.Name)) return null;
        if (c.TextureFiles.TryGetValue(tb.Name, out var f)) return f;
        // a texture of a parent dictionary: written now that a material needs it
        if (c.Pending.TryGetValue(tb.Name, out var pt))
        {
            long tt = Now;
            try
            {
                Directory.CreateDirectory(c.TexDir);
                var file = Path.Combine(c.TexDir, ModelImporter.SafeName(pt.Name, "texture") + ".dds");
                if (!(c.SkipExisting && (File.Exists(file) || MoloMax.Core.IO.PendingFiles.IsPending(file))))
                {
                    // MAP mode: the file is written in the background, the model does not wait for its textures
                    if (c.Lazy) { var tex = pt; MoloMax.Core.IO.PendingFiles.Queue(file, () => CodeWalker.Utils.DDSIO.GetDDSFile(tex)); }
                    else File.WriteAllBytes(file, CodeWalker.Utils.DDSIO.GetDDSFile(pt));
                }
                System.Threading.Interlocked.Add(ref Prof[2], Now - tt);
                c.TextureFiles[pt.Name] = file; c.Res.TexturesWritten.Add(file);
                return file;
            }
            catch (Exception e) { c.Res.Warnings.Add($"Texture {pt.Name}: {e.Message}"); }
        }
        return tb.Name; // name only: resolved through texture folders
    }

    static readonly uint HDiffuse = MetaXml.Jenkins("DiffuseSampler"), HBump = MetaXml.Jenkins("BumpSampler"),
        HSpec = MetaXml.Jenkins("SpecSampler"), HDetail = MetaXml.Jenkins("DetailSampler"), HTexture = MetaXml.Jenkins("TextureSampler"), HTint = MetaXml.Jenkins("TintPaletteSampler"), HLookup = MetaXml.Jenkins("lookupSampler");
    static readonly uint[] HLayer = Enumerable.Range(0, 4).Select(i => MetaXml.Jenkins("TextureSampler_layer" + i)).ToArray();
    static readonly uint[] HLayerBump = Enumerable.Range(0, 4).Select(i => MetaXml.Jenkins("BumpSampler_layer" + i)).ToArray();
    static readonly uint[] HLayerHeight = Enumerable.Range(0, 4).Select(i => MetaXml.Jenkins("heightMapSamplerLayer" + i)).ToArray();
    static readonly uint[] HLayerHScale = Enumerable.Range(0, 4).Select(i => MetaXml.Jenkins("heightScale" + i)).ToArray();
    static readonly uint[] HLayerHBias = Enumerable.Range(0, 4).Select(i => MetaXml.Jenkins("heightBias" + i)).ToArray();
    static readonly uint HHeight = MetaXml.Jenkins("heightSampler"), HHeightScale = MetaXml.Jenkins("heightScale"), HHeightBias = MetaXml.Jenkins("heightBias");

    /// <summary>One GtaMaterial per shader of the drawable; returns the material index (into the import result).</summary>
    static int MaterialFor(Ctx c, Cw.ShaderFX s, string drawableName, int shaderIndex)
    {
        var key = $"{drawableName}/{shaderIndex}";
        if (c.MaterialKey.TryGetValue(key, out var idx)) return idx;
        uint fileHash = s != null ? (uint)s.FileName : 0, nameHash = s != null ? (uint)s.Name : 0;
        string preset = Ctx.PresetByHash.TryGetValue(fileHash, out var p) ? p : Ctx.PresetByHash.TryGetValue(nameHash, out p) ? p : null;
        if (preset == null) { c.Res.Warnings.Add($"{drawableName}: unknown shader {fileHash:X8}, imported as default."); preset = "default"; }
        var mat = GtaMaterial.CreateDefault(c.Res.Materials.Count, $"{drawableName}_{shaderIndex}", new Vector3(0.75f));
        mat.Shader = preset;
        var prm = s?.ParametersList;
        if (prm?.Parameters != null && prm.Hashes != null)
        {
            for (int i = 0; i < prm.Parameters.Length && i < prm.Hashes.Length; i++)
            {
                uint h = (uint)prm.Hashes[i]; var par = prm.Parameters[i];
                if (par.DataType == 0)
                {
                    var tex = TextureRef(c, par.Data);
                    if (tex == null) continue;
                    int li = Array.IndexOf(HLayer, h), lb = Array.IndexOf(HLayerBump, h);
                    if (li >= 0 || lb >= 0)
                    {
                        mat.EnsureLayers();
                        if (li >= 0) mat.Layers[li] = tex; else mat.LayerBumps[lb] = tex;
                        continue;
                    }
                    int lh = Array.IndexOf(HLayerHeight, h);
                    if (lh >= 0) { mat.LayerHeights ??= new string[4]; mat.LayerHeights[lh] = tex; continue; }
                    if (h == HHeight) { mat.HeightTexture = tex; continue; }
                    if (h == HDiffuse || h == HTexture) mat.DiffuseTexture = tex;
                    else if (h == HBump) mat.BumpTexture = tex;
                    else if (h == HSpec) mat.SpecTexture = tex;
                    else if (h == HDetail) mat.DetailTexture = tex;
                    else if (h == HTint) mat.TintTexture = tex;
                    else if (h == HLookup) mat.LookupTexture = tex;
                }
                else if (par.Data is SDX.Vector4 v)
                {
                    if (h == MetaXml.Jenkins("bumpiness")) mat.Bumpiness = v.X;
                    else if (h == MetaXml.Jenkins("specularIntensityMult")) mat.SpecularIntensity = v.X;
                    else if (h == MetaXml.Jenkins("specularFalloffMult")) mat.SpecularFalloff = v.X;
                    else if (h == MetaXml.Jenkins("specularFresnel")) mat.SpecularFresnel = v.X;
                    else if (h == MetaXml.Jenkins("emissiveMultiplier")) mat.EmissiveMultiplier = v.X;
                    else if (h == HHeightScale) mat.HeightScale = v.X;
                    else if (h == HHeightBias) mat.HeightBias = v.X;
                    else if (Array.IndexOf(HLayerHScale, h) is int si and >= 0) { var q = mat.LayerHeightScale; if (si == 0) q.X = v.X; else if (si == 1) q.Y = v.X; else if (si == 2) q.Z = v.X; else q.W = v.X; mat.LayerHeightScale = q; }
                    else if (Array.IndexOf(HLayerHBias, h) is int bi and >= 0) { var q = mat.LayerHeightBias; if (bi == 0) q.X = v.X; else if (bi == 1) q.Y = v.X; else if (bi == 2) q.Z = v.X; else q.W = v.X; mat.LayerHeightBias = q; }
                }
            }
        }
        // terrain: no single diffuse; the first layer names the material (and is what a non-terrain view shows)
        if (mat.DiffuseTexture == null && mat.Layers?.FirstOrDefault(t => t != null) is { } first)
        { mat.DiffuseTexture = first; mat.BumpTexture ??= mat.LayerBumps.FirstOrDefault(t => t != null); }
        if (mat.DiffuseTexture != null) mat.Name = ModelImporter.SafeName(Path.GetFileNameWithoutExtension(mat.DiffuseTexture), mat.Name);
        c.Res.Materials.Add(mat);
        c.MaterialKey[key] = mat.Id;
        return mat.Id;
    }

    // ------------------------------------------------------------------------------------------ drawables

    static Matrix4x4 M(SDX.Matrix m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    static Vector3 V(SDX.Vector3 v) => new(v.X, v.Y, v.Z);

    static void AddDrawable(Ctx c, Cw.DrawableBase d, string name, Cw.Skeleton skel)
    {
        SaveTextures(c, d.ShaderGroup?.TextureDictionary);
        var models = d.DrawableModels?.High;
        if (models == null || models.Length == 0) { c.Res.Warnings.Add($"{name}: no high LOD model."); return; }
        var shaders = d.ShaderGroup?.Shaders?.data_items ?? Array.Empty<Cw.ShaderFX>();
        var bones = skel?.Bones?.Items;
        var mesh = new PolyMesh();
        bool skinWarn = false;
        foreach (var model in models)
        {
            // fragments and props bind rigid models to a bone: place them with the bone's rest transform
            var place = Matrix4x4.Identity;
            if (bones != null && model.HasSkin == 0 && model.BoneIndex < bones.Length && model.BoneIndex > 0)
                place = M(bones[model.BoneIndex].AbsTransform);
            if (model.HasSkin != 0 && !skinWarn) { c.Res.Warnings.Add($"{name}: skinned mesh imported in its bind pose (the rig is not imported yet)."); skinWarn = true; }
            foreach (var g in model.Geometries ?? Array.Empty<Cw.DrawableGeometry>())
            {
                var vd = g.VertexData; var idx = g.IndexBuffer?.Indices;
                if (vd == null || idx == null || vd.VertexCount == 0) continue;
                int shaderIndex = g.ShaderID;
                var shader = g.Shader ?? (shaderIndex < shaders.Length ? shaders[shaderIndex] : null);
                int mat = MaterialFor(c, shader, name, shaderIndex);
                uint flags = vd.Info?.Flags ?? 0;
                bool Has(Cw.VertexSemantics s) => (flags & (1u << (int)s)) != 0;
                int baseV = mesh.Verts.Count;
                var uv = new Vector2[vd.VertexCount]; var col = new Vector4[vd.VertexCount];
                // terrain_cb_w_4lyr: the layer weights come from colour 1 (blue: layer 0/1 and 2/3, green: pair 0-1 / 2-3), like the game
                bool terrain = c.Res.Materials[mat].Layers != null && Has(Cw.VertexSemantics.Colour1);
                var col1 = terrain ? new Vector4[vd.VertexCount] : null;
                // terrain with a blend mask: its second texture coordinates say where the mask is read
                var uv1 = terrain && c.Res.Materials[mat].LookupTexture != null && Has(Cw.VertexSemantics.TexCoord1) ? new Vector2[vd.VertexCount] : null;
                // GTA cables: each point is there twice on the line, the game's shader opens them into a strip facing the
                // camera (normal = cable direction, texcoord.x = half width). Here: two crossed strips, seen from every side.
                if (string.Equals(c.Res.Materials[mat].Shader, "cable", StringComparison.OrdinalIgnoreCase) && Has(Cw.VertexSemantics.Normal) && Has(Cw.VertexSemantics.TexCoord0))
                {
                    AddCable(mesh, vd, idx, mat, place);
                    continue;
                }
                // far tree cards (trees_lod2): the 4 corners of each card sit on one point, the game's shader opens them
                // facing the camera (size from treeLod2Params x texcoord 2). Here: two crossed cards, seen from every side.
                if (string.Equals(c.Res.Materials[mat].Shader, "trees_lod2", StringComparison.OrdinalIgnoreCase) && Has(Cw.VertexSemantics.TexCoord0))
                {
                    AddTreeCards(mesh, vd, idx, mat, place, ShaderVec(shader, "treeLod2Params"), Has(Cw.VertexSemantics.TexCoord2), Has(Cw.VertexSemantics.Colour0), Has(Cw.VertexSemantics.TexCoord1));
                    continue;
                }
                for (int i = 0; i < vd.VertexCount; i++)
                {
                    mesh.AddVertex(Vector3.Transform(V(vd.GetVector3(i, (int)Cw.VertexSemantics.Position)), place));
                    uv[i] = Has(Cw.VertexSemantics.TexCoord0) ? ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord0) : Vector2.Zero;
                    if (Has(Cw.VertexSemantics.Colour0)) { var cc = vd.GetColour(i, (int)Cw.VertexSemantics.Colour0); col[i] = new Vector4(cc.R, cc.G, cc.B, cc.A) / 255f; }
                    else col[i] = PolyMesh.DefaultColour;
                    if (terrain) { var c1 = vd.GetColour(i, (int)Cw.VertexSemantics.Colour1); col1[i] = new Vector4(c1.R, c1.G, c1.B, c1.A) / 255f; }
                    if (uv1 != null) uv1[i] = ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord1);
                }
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], cI = idx[t + 2];
                    if (a >= vd.VertexCount || b >= vd.VertexCount || cI >= vd.VertexCount || a == b || b == cI || a == cI) continue;
                    var f = mesh.AddFace(new[] { baseV + a, baseV + b, baseV + cI }, mat);
                    if (terrain) f.Col1 = new[] { col1[a], col1[b], col1[cI] };
                    if (uv1 != null) f.UV1 = new[] { uv1[a], uv1[b], uv1[cI] };
                    f.UV = new[] { uv[a], uv[b], uv[cI] };
                    f.Col = new[] { col[a], col[b], col[cI] };
                }
            }
        }
        // shared positions are welded so the mesh is editable (UV / colour seams stay per corner); triangles to quads where flat
        if (c.Weld)
        {
            MeshOps.WeldByDistance(mesh, 1e-5f);
            mesh.CleanFaces();
            MeshOps.AutoSmooth(mesh, null, 40f);
        }
        c.Res.Meshes.Add(new ImportedMesh { Name = name, Mesh = mesh, GtaType = GtaObjectType.Drawable });
    }

    static Vector4 ShaderVec(Cw.ShaderFX s, string name)
    {
        var prm = s?.ParametersList; uint want = Cw.JenkHash.GenHash(name.ToLowerInvariant());
        if (prm?.Parameters == null || prm.Hashes == null) return Vector4.Zero;
        for (int i = 0; i < prm.Parameters.Length && i < prm.Hashes.Length; i++)
            if ((uint)prm.Hashes[i] == want && prm.Parameters[i].Data is SDX.Vector4 v) return new Vector4(v.X, v.Y, v.Z, v.W);
        return Vector4.Zero;
    }

    static void AddTreeCards(PolyMesh mesh, Cw.VertexData vd, ushort[] idx, int mat, Matrix4x4 place, Vector4 prm, bool hasTc2, bool hasCol, bool hasTc1)
    {
        int n = vd.VertexCount;
        var pos = new Vector3[n]; var tc = new Vector2[n]; var sz = new Vector2[n]; var col = new Vector4[n]; var at = new Vector2[n]; // at: place in the texture atlas
        var size = new Vector2(prm.X == 0 ? 10 : prm.X, prm.Y == 0 ? 10 : prm.Y);
        for (int i = 0; i < n; i++)
        {
            pos[i] = V(vd.GetVector3(i, (int)Cw.VertexSemantics.Position));
            tc[i] = ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord0);
            sz[i] = hasTc2 ? ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord2) : Vector2.One;
            at[i] = hasTc1 ? ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord1) : tc[i];
            if (hasCol) { var cc = vd.GetColour(i, (int)Cw.VertexSemantics.Colour0); col[i] = new Vector4(cc.R, cc.G, cc.B, cc.A) / 255f; } else col[i] = PolyMesh.DefaultColour;
        }
        foreach (var side in new[] { Vector3.UnitX, Vector3.UnitY })
        {
            int baseV = mesh.Verts.Count;
            for (int i = 0; i < n; i++)
            {
                var o = size * (new Vector2(0.5f) - tc[i]) * sz[i];
                mesh.AddVertex(Vector3.Transform(pos[i] + side * o.X + Vector3.UnitZ * o.Y, place));
            }
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], cI = idx[t + 2];
                if (a >= n || b >= n || cI >= n || a == b || b == cI || a == cI) continue;
                foreach (var (x, y, z) in new[] { (a, b, cI), (a, cI, b) })
                {
                    var f = mesh.AddFace(new[] { baseV + x, baseV + y, baseV + z }, mat);
                    f.UV = new[] { at[x], at[y], at[z] };
                    f.Col = new[] { col[x], col[y], col[z] };
                }
            }
        }
    }

    static void AddCable(PolyMesh mesh, Cw.VertexData vd, ushort[] idx, int mat, Matrix4x4 place)
    {
        int n = vd.VertexCount;
        var pos = new Vector3[n]; var dir = new Vector3[n]; var tc = new Vector2[n]; var col = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = Vector3.Transform(V(vd.GetVector3(i, (int)Cw.VertexSemantics.Position)), place);
            dir[i] = Vector3.TransformNormal(V(vd.GetVector3(i, (int)Cw.VertexSemantics.Normal)), place);
            tc[i] = ReadUv(vd, i, (int)Cw.VertexSemantics.TexCoord0);
            col[i] = PolyMesh.DefaultColour;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            int baseV = mesh.Verts.Count;
            for (int i = 0; i < n; i++)
            {
                var d = dir[i].LengthSquared() > 1e-8f ? Vector3.Normalize(dir[i]) : Vector3.UnitX;
                var flat = Vector3.Cross(d, Vector3.UnitZ);
                flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitX;
                var side = pass == 0 ? flat : Vector3.Normalize(Vector3.Cross(flat, d));
                float half = MathF.Max(MathF.Abs(tc[i].X), 0.01f) * MathF.Sign(tc[i].X == 0 ? 1 : tc[i].X);
                mesh.AddVertex(pos[i] + side * half);
            }
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], cI = idx[t + 2];
                if (a >= n || b >= n || cI >= n || a == b || b == cI || a == cI) continue;
                // both sides (a cable has no inside)
                foreach (var (x, y, z) in new[] { (a, b, cI), (a, cI, b) })
                {
                    var f = mesh.AddFace(new[] { baseV + x, baseV + y, baseV + z }, mat);
                    f.UV = new[] { new Vector2(tc[x].X > 0 ? 1 : 0, tc[x].Y), new Vector2(tc[y].X > 0 ? 1 : 0, tc[y].Y), new Vector2(tc[z].X > 0 ? 1 : 0, tc[z].Y) };
                    f.Col = new[] { col[x], col[y], col[z] };
                }
            }
        }
    }

    static Vector2 ReadUv(Cw.VertexData vd, int v, int comp)
    {
        var ct = vd.Info.GetComponentType(comp);
        switch (ct)
        {
            case Cw.VertexComponentType.Half2: { var h = vd.GetHalf2(v, comp); return new Vector2((float)h.X, (float)h.Y); }
            case Cw.VertexComponentType.Float2: { var f = vd.GetVector2(v, comp); return new Vector2(f.X, f.Y); }
            case Cw.VertexComponentType.Float4: { var f = vd.GetVector4(v, comp); return new Vector2(f.X, f.Y); }
            case Cw.VertexComponentType.Half4: { var h = vd.GetHalf4(v, comp); return new Vector2((float)h.X, (float)h.Y); }
            default: return Vector2.Zero;
        }
    }

    // ------------------------------------------------------------------------------------------ bounds

    sealed class ColMesh
    {
        public PolyMesh Mesh = new(); public Dictionary<int, int> MatCount = new();
        /// <summary>Collision material of each face (filled as faces are added).</summary>
        public List<int> FaceMat = new();
        public void Tag(int mat) { while (FaceMat.Count < Mesh.Faces.Count) FaceMat.Add(mat); }
    }

    static void AddBounds(Ctx c, Cw.Bounds b, string name, Matrix4x4 xf)
    {
        var col = new ColMesh();
        Collect(c, b, xf, col);
        if (col.Mesh.Faces.Count == 0) return;
        col.Tag(0);
        var mats = col.FaceMat.Distinct().OrderByDescending(m => col.FaceMat.Count(x => x == m)).ToList();
        if (mats.Count <= 1)
        {
            MeshOps.WeldByDistance(col.Mesh, 1e-5f);
            col.Mesh.CleanFaces();
            c.Res.Meshes.Add(new ImportedMesh { Name = name, Mesh = col.Mesh, GtaType = GtaObjectType.Collision, CollisionMaterial = mats.Count > 0 ? mats[0] & 0xFF : 0, CollisionRoom = mats.Count > 0 ? mats[0] >> 8 : 0 });
            return;
        }
        // one collision object per material and RoomID (like GIMS / Sollumz): each keeps its own material and colour, export writes them back
        bool rooms = mats.Select(m => m >> 8).Distinct().Count() > 1;
        foreach (var m in mats)
        {
            var sub = new PolyMesh(); var remap = new Dictionary<int, int>();
            for (int f = 0; f < col.Mesh.Faces.Count; f++)
            {
                if (col.FaceMat[f] != m) continue;
                var src = col.Mesh.Faces[f].V;
                var vs = new int[src.Length];
                for (int k = 0; k < src.Length; k++)
                {
                    if (!remap.TryGetValue(src[k], out var nv)) { nv = sub.AddVertex(col.Mesh.Verts[src[k]]); remap[src[k]] = nv; }
                    vs[k] = nv;
                }
                sub.AddFace(vs);
            }
            MeshOps.WeldByDistance(sub, 1e-5f);
            sub.CleanFaces();
            if (sub.Faces.Count == 0) continue;
            string mn = MoloMax.Core.Gta.Export.CollisionMaterials.NameOf(m & 0xFF).ToLowerInvariant();
            c.Res.Meshes.Add(new ImportedMesh { Name = rooms ? $"{name}_{mn}_room{m >> 8}" : $"{name}_{mn}", Mesh = sub, GtaType = GtaObjectType.Collision, CollisionMaterial = m & 0xFF, CollisionRoom = m >> 8 });
        }
    }

    static void Collect(Ctx c, Cw.Bounds b, Matrix4x4 xf, ColMesh col)
    {
        switch (b)
        {
            case Cw.BoundComposite comp:
                {
                    var kids = comp.Children?.data_items;
                    if (kids == null) return;
                    for (int i = 0; i < kids.Length; i++)
                    {
                        if (kids[i] == null) continue;
                        var t = Matrix4x4.Identity;
                        if (comp.ChildrenTransformation1 != null && i < comp.ChildrenTransformation1.Length)
                        {
                            var m = comp.ChildrenTransformation1[i];
                            t = new Matrix4x4(m.Column1.X, m.Column1.Y, m.Column1.Z, 0, m.Column2.X, m.Column2.Y, m.Column2.Z, 0,
                                              m.Column3.X, m.Column3.Y, m.Column3.Z, 0, m.Column4.X, m.Column4.Y, m.Column4.Z, 1);
                        }
                        Collect(c, kids[i], t * xf, col);
                    }
                    return;
                }
            case Cw.BoundGeometry g:
                {
                    if (g.Polygons == null) return;
                    int baseV = col.Mesh.Verts.Count;
                    int nv = g.Vertices?.Length ?? 0;
                    for (int i = 0; i < nv; i++) col.Mesh.AddVertex(Vector3.Transform(V(g.GetVertexPos(i)), xf));
                    for (int pi = 0; pi < g.Polygons.Length; pi++)
                    {
                        var p = g.Polygons[pi];
                        int matIdx = 0;
                        if (g.PolygonMaterialIndices != null && pi < g.PolygonMaterialIndices.Length && g.Materials != null)
                        {
                            int mi = g.PolygonMaterialIndices[pi];
                            if (mi < g.Materials.Length) matIdx = (int)(g.Materials[mi].Data1 & 0xFF) | (int)(g.Materials[mi].RoomId & 31) << 8; // material + RoomID
                        }
                        col.MatCount[matIdx] = col.MatCount.GetValueOrDefault(matIdx) + 1;
                        switch (p)
                        {
                            case Cw.BoundPolygonTriangle t:
                                if (t.vertIndex1 < nv && t.vertIndex2 < nv && t.vertIndex3 < nv)
                                    col.Mesh.AddFace(new[] { baseV + t.vertIndex1, baseV + t.vertIndex2, baseV + t.vertIndex3 });
                                break;
                            case Cw.BoundPolygonBox bx:
                                AddPolyBox(col.Mesh, xf, V(bx.Vertex1), V(bx.Vertex2), V(bx.Vertex3), V(bx.Vertex4));
                                break;
                            case Cw.BoundPolygonSphere s:
                                AddPrim(col.Mesh, Primitives.Sphere(s.sphereRadius, 12), Matrix4x4.CreateTranslation(V(g.GetVertexPos(s.sphereIndex))) * xf);
                                break;
                            case Cw.BoundPolygonCapsule cp:
                                AddCapsule(col.Mesh, V(g.GetVertexPos(cp.capsuleIndex1)), V(g.GetVertexPos(cp.capsuleIndex2)), cp.capsuleRadius, xf, true);
                                break;
                            case Cw.BoundPolygonCylinder cy:
                                AddCapsule(col.Mesh, V(g.GetVertexPos(cy.cylinderIndex1)), V(g.GetVertexPos(cy.cylinderIndex2)), cy.cylinderRadius, xf, false);
                                break;
                        }
                        col.Tag(matIdx);
                    }
                    return;
                }
            default:
                {
                    // primitive bounds: box, sphere, capsule, cylinder, disc
                    int matIdx = b.MaterialIndex | (b.RoomId & 31) << 8;
                    col.MatCount[matIdx] = col.MatCount.GetValueOrDefault(matIdx) + 1;
                    var mn = V(b.BoxMin); var mx = V(b.BoxMax); var size = mx - mn; var cen = (mn + mx) * 0.5f;
                    switch (b.Type)
                    {
                        case Cw.BoundsType.Sphere:
                            AddPrim(col.Mesh, Primitives.Sphere(b.SphereRadius, 16), Matrix4x4.CreateTranslation(V(b.SphereCenter)) * xf); break;
                        case Cw.BoundsType.Capsule:
                            {
                                float r = MathF.Min(size.X, size.Z) * 0.5f;
                                AddCapsule(col.Mesh, cen - new Vector3(0, size.Y * 0.5f - r, 0), cen + new Vector3(0, size.Y * 0.5f - r, 0), r, xf, true); break;
                            }
                        case Cw.BoundsType.Cylinder:
                        case Cw.BoundsType.Disc:
                            {
                                float r = MathF.Min(size.X, size.Z) * 0.5f;
                                AddCapsule(col.Mesh, cen - new Vector3(0, size.Y * 0.5f, 0), cen + new Vector3(0, size.Y * 0.5f, 0), r, xf, false); break;
                            }
                        default:
                            AddPrim(col.Mesh, Primitives.Box(size.X, size.Y, size.Z), Matrix4x4.CreateTranslation(cen - new Vector3(0, 0, size.Z * 0.5f)) * xf); break;
                    }
                    col.Tag(matIdx);
                    return;
                }
        }
    }

    static void AddPrim(PolyMesh dst, PolyMesh prim, Matrix4x4 m) { prim.Transform(m); dst.Append(prim); }

    /// <summary>A bound "box" polygon stores 4 alternate corners of the box; the other 4 are their mirror through the centre.</summary>
    static void AddPolyBox(PolyMesh dst, Matrix4x4 xf, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
    {
        var c = (p1 + p2 + p3 + p4) * 0.25f;
        // sign pattern of the 8 corners: p1(+++) p2(+--) p3(-+-) p4(--+), q = mirror
        var q1 = 2 * c - p1; var q2 = 2 * c - p2; var q3 = 2 * c - p3; var q4 = 2 * c - p4;
        // index by sign bits (x,y,z): +++ p1, +-- p2, -+- p3, --+ p4, --- q1, -++ q2, +-+ q3, ++- q4
        var corner = new Vector3[8];
        int K(int sx, int sy, int sz) => (sx > 0 ? 4 : 0) | (sy > 0 ? 2 : 0) | (sz > 0 ? 1 : 0);
        corner[K(1, 1, 1)] = p1; corner[K(1, -1, -1)] = p2; corner[K(-1, 1, -1)] = p3; corner[K(-1, -1, 1)] = p4;
        corner[K(-1, -1, -1)] = q1; corner[K(-1, 1, 1)] = q2; corner[K(1, -1, 1)] = q3; corner[K(1, 1, -1)] = q4;
        int b = dst.Verts.Count;
        foreach (var v in corner) dst.AddVertex(Vector3.Transform(v, xf));
        int[][] faces =
        {
            new[] { K(1,-1,-1), K(1,1,-1), K(1,1,1), K(1,-1,1) }, new[] { K(-1,-1,-1), K(-1,-1,1), K(-1,1,1), K(-1,1,-1) },
            new[] { K(-1,1,-1), K(-1,1,1), K(1,1,1), K(1,1,-1) }, new[] { K(-1,-1,-1), K(1,-1,-1), K(1,-1,1), K(-1,-1,1) },
            new[] { K(-1,-1,1), K(1,-1,1), K(1,1,1), K(-1,1,1) }, new[] { K(-1,-1,-1), K(-1,1,-1), K(1,1,-1), K(1,-1,-1) },
        };
        var cw = Vector3.Transform(c, xf);
        foreach (var fv in faces)
        {
            var f = dst.AddFace(fv.Select(i => b + i).ToArray());
            if (Vector3.Dot(dst.FaceNormal(f), dst.FaceCenter(f) - cw) < 0) f.Flip();
        }
    }

    static void AddCapsule(PolyMesh dst, Vector3 a, Vector3 b, float r, Matrix4x4 xf, bool caps)
    {
        var axis = b - a; float len = axis.Length();
        var dir = len > 1e-6f ? axis / len : Vector3.UnitZ;
        var cyl = Primitives.Cylinder(r, MathF.Max(len, 1e-4f), 12);
        // cylinder is built along +Z from its base: rotate Z onto the axis
        var rot = RotateZTo(dir);
        AddPrim(dst, cyl, rot * Matrix4x4.CreateTranslation(a) * xf);
        if (caps)
        {
            AddPrim(dst, Primitives.Sphere(r, 12), Matrix4x4.CreateTranslation(a) * xf);
            AddPrim(dst, Primitives.Sphere(r, 12), Matrix4x4.CreateTranslation(b) * xf);
        }
    }

    static Matrix4x4 RotateZTo(Vector3 d)
    {
        var z = Vector3.UnitZ; float dot = Vector3.Dot(z, d);
        if (dot > 0.9999f) return Matrix4x4.Identity;
        if (dot < -0.9999f) return Matrix4x4.CreateRotationX(MathF.PI);
        var ax = Vector3.Normalize(Vector3.Cross(z, d));
        return Matrix4x4.CreateFromAxisAngle(ax, MathF.Acos(Math.Clamp(dot, -1, 1)));
    }
}
