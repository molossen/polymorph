using System.Numerics;
using CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Game;

/// <summary>A model file inside the game archives.</summary>
public sealed class GameAsset
{
    /// <summary>Families (<see cref="GameCategories"/>), filled when the browser classifies the models.</summary>
    public long Categories = -1;
    public string Name, Ext, Path;
    public uint Hash;
    public long Size;
    internal RpfFileEntry Entry;
    public override string ToString() => Name + Ext;
}

/// <summary>Where the game places an archetype (from the map .ymap files).</summary>
public readonly record struct GamePlacement(Vector3 Position, Quaternion Rotation, Vector3 Scale, string Ymap);

/// <summary>
/// The GTA V install read through CodeWalker: archives decrypted with the keys found in the game's exe, an index
/// of every model (.ydr / .yft / .ydd / .ybn), and on demand where each archetype stands in the map and which
/// texture dictionary it uses. Models are extracted with their .ytd so the normal GTA importer can read them.
/// </summary>
public sealed class GameFiles
{
    public RpfManager Rpf { get; private set; }
    /// <summary>The AES key found in the exe (base64), worth saving: next loads skip the slow search.</summary>
    public static string CurrentKey => GTA5Keys.PC_AES_KEY != null ? Convert.ToBase64String(GTA5Keys.PC_AES_KEY) : null;
    public bool Gen9 { get; private set; }
    public string Folder { get; private set; }
    public readonly List<GameAsset> Models = new();
    readonly Dictionary<uint, GameAsset> ytdByHash = new();
    readonly Dictionary<uint, GameAsset> modelByHash = new();
    readonly Dictionary<uint, uint> txdOfArchetype = new();
    /// <summary>Largest side of each archetype's bounding box (metres), from the .ytyp files.</summary>
    readonly Dictionary<uint, float> sizeOfArchetype = new();
    /// <summary>Archetypes whose model sits in a drawable dictionary (.ydd): most far LOD / SLOD models of the map.</summary>
    readonly Dictionary<uint, uint> yddOfArchetype = new();
    readonly Dictionary<uint, GameAsset> yddByHash = new();
    /// <summary>Interiors (MLO archetypes): the .ytyp holding each one.</summary>
    readonly Dictionary<uint, RpfFileEntry> ytypOfMlo = new();
    /// <summary>Texture dictionary parents (gtxd): a model's textures can live in its dictionary's parent, grand-parent...</summary>
    readonly Dictionary<uint, uint> parentTxd = new();
    readonly object typeLock = new();
    public bool TypesIndexed { get; private set; }
    readonly Dictionary<uint, List<GamePlacement>> placements = new();
    public bool MapIndexed { get; private set; }

    static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase) { ".ydr", ".yft", ".ydd", ".ybn" };

    public static bool LooksLikeGta(string folder, out bool gen9)
    {
        gen9 = File.Exists(System.IO.Path.Combine(folder ?? "", "GTA5_Enhanced.exe"));
        return gen9 || File.Exists(System.IO.Path.Combine(folder ?? "", "GTA5.exe"));
    }

    /// <summary>Opens the game install (decryption keys come from the game's exe, like CodeWalker). Takes a while.</summary>
    public static GameFiles Open(string folder, Action<string> status, string savedKey = null)
    {
        if (!LooksLikeGta(folder, out bool gen9)) throw new DirectoryNotFoundException("GTA5.exe not found in " + folder);
        status?.Invoke(savedKey == null ? "Finding the game keys in the exe (first time only)..." : "Reading the game keys...");
        try { GTA5Keys.LoadFromPath(folder, gen9, savedKey); }
        catch when (savedKey != null) { GTA5Keys.LoadFromPath(folder, gen9, null); } // game updated: search again
        var m = new RpfManager();
        var errors = new List<string>();
        m.Init(folder, gen9, s => status?.Invoke(s), e => { lock (errors) errors.Add(e); }, false, false);
        var g = new GameFiles { Rpf = m, Gen9 = gen9, Folder = folder };
        status?.Invoke("Indexing models...");
        g.Index(m.AllRpfs);
        return g;
    }

    /// <summary>Builds the index from archives already loaded (tests, mods folders).</summary>
    public static GameFiles FromRpfs(List<RpfFile> rpfs)
    {
        var m = new RpfManager();
        m.Init(rpfs, false);
        var g = new GameFiles { Rpf = m };
        g.Index(m.AllRpfs);
        return g;
    }

    /// <summary>How much a file's place overrides the same name elsewhere: the title update over the DLCs over the base game
    /// (a DLC or the update ships newer copies of base files; the base copy can lack models added later).</summary>
    static int Rank(string path)
    {
        var p = (path ?? "").ToLowerInvariant();
        if (p.StartsWith("update\\") || p.StartsWith("update/")) return 3;
        if (p.Contains("dlcpacks")) return p.Contains("patch") ? 2 : 1;
        return 0;
    }

    static void Put(Dictionary<uint, GameAsset> d, GameAsset a)
    {
        if (!d.TryGetValue(a.Hash, out var old) || Rank(a.Path) >= Rank(old.Path)) d[a.Hash] = a; // same rank: the later archive (DLCs load in order)
    }

    void Index(IEnumerable<RpfFile> rpfs)
    {
        foreach (var rpf in rpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe) continue;
                var ext = System.IO.Path.GetExtension(fe.NameLower ?? fe.Name ?? "");
                var name = System.IO.Path.GetFileNameWithoutExtension(fe.NameLower ?? fe.Name ?? "");
                if (ext == ".ytd") { Put(ytdByHash, new GameAsset { Name = name, Ext = ext, Path = fe.Path, Entry = fe, Hash = JenkHash.GenHash(name) }); continue; }
                if (!ModelExt.Contains(ext)) continue;
                var a = new GameAsset { Name = name, Ext = ext, Path = fe.Path, Entry = fe, Hash = JenkHash.GenHash(name), Size = fe.GetFileSize() };
                Models.Add(a);
                if (ext is ".ydr" or ".yft") Put(modelByHash, a);
                else if (ext == ".ydd") Put(yddByHash, a);
            }
        }
        Models.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    public IEnumerable<GameAsset> Search(string text, bool ydr = true, bool yft = true, bool ydd = true, bool ybn = false)
    {
        var words = (text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Models.Where(a => (a.Ext == ".ydr" && ydr || a.Ext == ".yft" && yft || a.Ext == ".ydd" && ydd || a.Ext == ".ybn" && ybn)
                                 && words.All(w => a.Name.Contains(w)));
    }

    /// <summary>Reads every map (.ymap) and type (.ytyp) file once: placements of each archetype and its texture dictionary.</summary>
    public void BuildMapIndex(Action<string, float> progress = null)
    {
        if (MapIndexed) return;
        var maps = new List<(RpfFile Rpf, RpfFileEntry Entry)>();
        var types = new List<(RpfFile Rpf, RpfFileEntry Entry)>();
        foreach (var rpf in Rpf.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe)
                {
                    var n = fe.NameLower ?? "";
                    if (n.EndsWith(".ymap")) maps.Add((rpf, fe)); else if (n.EndsWith(".ytyp")) types.Add((rpf, fe));
                }
        }
        int done = 0, total = maps.Count + types.Count;
        BuildTypeIndex((st, p) => progress?.Invoke(st, p * types.Count / Math.Max(1, total)));
        done = types.Count;
        foreach (var (rpf, fe) in maps)
        {
            try
            {
                var y = RpfFile.GetFile<YmapFile>(fe);
                if (y?.AllEntities != null)
                    foreach (var e in y.AllEntities)
                    {
                        if (e == null || e.MloParent != null) continue;
                        uint h = e.CEntityDef.archetypeName.Hash;
                        if (!placements.TryGetValue(h, out var l)) placements[h] = l = new List<GamePlacement>(1);
                        var p = e.Position; var q = e.Orientation; var s = e.Scale;
                        l.Add(new GamePlacement(new Vector3(p.X, p.Y, p.Z), new Quaternion(q.X, q.Y, q.Z, q.W), new Vector3(s.X, s.Y, s.Z), fe.Name));
                    }
            }
            catch { }
            if (++done % 200 == 0) progress?.Invoke("Maps " + fe.Name, done / (float)total);
        }
        MapIndexed = true;
        progress?.Invoke("Map index ready", 1);
    }

    /// <summary>
    /// Archetype texture dictionaries (every .ytyp) and the texture dictionary parents (gtxd.ymt / gtxd.meta).
    /// Much quicker than the map index; the browser runs it in the background after loading, Extract waits for it.
    /// </summary>
    public void BuildTypeIndex(Action<string, float> progress = null)
    {
        lock (typeLock)
        {
            if (TypesIndexed) return;
            var types = new List<RpfFileEntry>(); var gtxd = new List<RpfFileEntry>();
            foreach (var rpf in Rpf.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                    if (e is RpfFileEntry fe)
                    {
                        var n = fe.NameLower ?? "";
                        if (n.EndsWith(".ytyp")) types.Add(fe);
                        else if (n.StartsWith("gtxd") && (n.EndsWith(".ymt") || n.EndsWith(".meta"))) gtxd.Add(fe);
                    }
            }
            int done = 0;
            foreach (var fe in types)
            {
                try
                {
                    var y = RpfFile.GetFile<YtypFile>(fe);
                    if (y?.AllArchetypes != null)
                        foreach (var a in y.AllArchetypes)
                            if (a != null)
                            {
                                txdOfArchetype.TryAdd(a.Hash.Hash, a.TextureDict.Hash);
                                var bb = a.BBMax - a.BBMin;
                                sizeOfArchetype.TryAdd(a.Hash.Hash, Math.Max(bb.X, Math.Max(bb.Y, bb.Z)));
                                lodDistOfArchetype.TryAdd(a.Hash.Hash, a.LodDist);
                                if (a.DrawableDict.Hash != 0) yddOfArchetype.TryAdd(a.Hash.Hash, a.DrawableDict.Hash);
                                if (a is MloArchetype) ytypOfMlo.TryAdd(a.Hash.Hash, fe);
                            }
                }
                catch { }
                if (++done % 200 == 0) progress?.Invoke("Types " + fe.Name, done / (float)Math.Max(1, types.Count));
            }
            foreach (var fe in gtxd)
            {
                try
                {
                    if (fe.NameLower.EndsWith(".ymt"))
                    {
                        var y = RpfFile.GetFile<YmtFile>(fe);
                        if (y?.CMapParentTxds != null) foreach (var kv in y.CMapParentTxds) AddParent(kv.Key, kv.Value);
                    }
                    else
                    {
                        var txt = System.Text.Encoding.UTF8.GetString(fe.File.ExtractFile(fe) ?? Array.Empty<byte>());
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(txt, @"<parent>\s*([^<\s]+)\s*</parent>\s*<child>\s*([^<\s]+)\s*</child>"))
                            AddParent(m.Groups[2].Value, m.Groups[1].Value);
                    }
                }
                catch { }
            }
            TypesIndexed = true;
            progress?.Invoke($"Texture index ready ({txdOfArchetype.Count:N0} archetypes, {parentTxd.Count:N0} parent links)", 1);
        }
    }
    void AddParent(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return;
        parentTxd.TryAdd(JenkHash.GenHash(child.ToLowerInvariant()), JenkHash.GenHash(parent.ToLowerInvariant()));
    }

    /// <summary>The drawable dictionary (.ydd) holding an archetype's model, when it has no .ydr / .yft of its own.</summary>
    public GameAsset FindDictionaryOf(uint archetypeHash)
    {
        BuildTypeIndex();
        return yddOfArchetype.TryGetValue(archetypeHash, out var d) && yddByHash.TryGetValue(d, out var a) ? a : null;
    }

    /// <summary>The .ytyp of an interior (MLO archetype) as a standalone file, with its name; null when unknown.</summary>
    public byte[] InteriorTypeFile(uint mloHash, out string name)
    {
        BuildTypeIndex();
        name = null;
        if (!ytypOfMlo.TryGetValue(mloHash, out var fe)) return null;
        name = System.IO.Path.GetFileNameWithoutExtension(fe.NameLower ?? fe.Name);
        return FileBytes(fe);
    }

    /// <summary>Why an archetype has no model (self tests, status).</summary>
    public string WhyNoModel(uint h)
    {
        BuildTypeIndex();
        if (!txdOfArchetype.ContainsKey(h)) return "archetype in no .ytyp";
        if (!yddOfArchetype.TryGetValue(h, out var d)) return "no .ydr/.yft and no drawable dictionary";
        return $"drawable dictionary {JenkIndex.TryGetString(d)} (0x{d:X8}) not in the archives";
    }

    /// <summary>The model's texture dictionary then its parents, as found in the archives (nearest first).</summary>
    public List<GameAsset> TextureChainOf(GameAsset a) => TextureChainOf(a, a.Hash);

    /// <summary>Same, the dictionary chosen by an archetype (a .ydd serves several archetypes).</summary>
    public List<GameAsset> TextureChainOf(GameAsset a, uint archetypeHash)
    {
        var res = new List<GameAsset>();
        uint h = txdOfArchetype.TryGetValue(archetypeHash, out var t) ? t : a.Hash;
        var seen = new HashSet<uint>();
        for (int i = 0; i < 10 && seen.Add(h); i++)
        {
            if (ytdByHash.TryGetValue(h, out var y)) res.Add(y);
            if (!parentTxd.TryGetValue(h, out h)) break;
        }
        if (res.Count == 0 && ytdByHash.TryGetValue(a.Hash, out var same)) res.Add(same);
        return res;
    }

    /// <summary>The .ydr / .yft of an archetype hash (interior entities name their model by hash).</summary>
    public GameAsset FindModel(uint hash) => modelByHash.TryGetValue(hash, out var a) ? a : null;

    public IReadOnlyList<GamePlacement> PlacementsOf(GameAsset a) => placements.TryGetValue(a.Hash, out var l) ? l : Array.Empty<GamePlacement>();

    /// <summary>Texture dictionary of a model: the archetype's (from the .ytyp), else a .ytd with the same name.</summary>
    public GameAsset TextureDictionaryOf(GameAsset a)
    {
        if (TypesIndexed) { var chain = TextureChainOf(a); if (chain.Count > 0) return chain[0]; }
        if (txdOfArchetype.TryGetValue(a.Hash, out var t) && ytdByHash.TryGetValue(t, out var y)) return y;
        return ytdByHash.TryGetValue(a.Hash, out var same) ? same : null;
    }

    /// <summary>Bytes as a standalone file: resources get their RSC7 header back (the archive keeps it in the entry).</summary>
    /// <summary>Every game file with this name (e.g. "vehicles.meta"), read as XML text, base game first then the DLCs.
    /// <paramref name="mustContain"/> skips files that do not mention it (cheap text test before parsing).</summary>
    public IEnumerable<(string Path, string Xml)> ReadTextFiles(string fileName, string mustContain = null)
    {
        foreach (var rpf in Rpf.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !string.Equals(fe.Name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                string text = null;
                try
                {
                    var data = fe.File.ExtractFile(fe);
                    if (data == null) continue;
                    int i = 0; while (i < data.Length && (data[i] == 0xEF || data[i] == 0xBB || data[i] == 0xBF || data[i] <= 32)) i++;
                    if (i < data.Length && data[i] == (byte)'<') text = System.Text.Encoding.UTF8.GetString(data, i, data.Length - i);
                    else text = MetaXml.GetXml(fe, data, out _); // binary meta (RBF / PSO)
                }
                catch { }
                if (string.IsNullOrEmpty(text) || mustContain != null && text.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0) continue;
                yield return (fe.Path, text);
            }
        }
    }

    /// <summary>Largest side of an archetype's bounding box in metres (-1 when unknown).</summary>
    /// <summary>Draw distance the archetype gives its entities when they don't set one (0 = unknown).</summary>
    public float ArchetypeLodDist(uint hash) { BuildTypeIndex(); return lodDistOfArchetype.TryGetValue(hash, out var d) && float.IsFinite(d) ? d : 0; }
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, float> lodDistOfArchetype = new();
    public float ArchetypeSize(uint hash) { BuildTypeIndex(); return sizeOfArchetype.TryGetValue(hash, out var s) && float.IsFinite(s) ? s : -1; }

    /// <summary>A texture dictionary read straight from the archive.</summary>
    public YtdFile LoadYtd(GameAsset a) => RpfFile.GetFile<YtdFile>(a.Entry);

    /// <summary>Every interior (MLO archetype) of the game: its name, the .ytyp it comes from and the archetypes placed in it
    /// (rooms and entity sets).</summary>
    public List<(string Name, string Ytyp, List<uint> Archetypes)> Interiors(Action<string, float> progress = null)
    {
        BuildTypeIndex(progress);
        var res = new List<(string, string, List<uint>)>();
        foreach (var fe in ytypOfMlo.Values.Distinct())
        {
            try
            {
                var y = RpfFile.GetFile<YtypFile>(fe);
                if (y?.AllArchetypes == null) continue;
                foreach (var a in y.AllArchetypes)
                {
                    if (a is not MloArchetype mlo) continue;
                    var hs = new List<uint>();
                    if (mlo.entities != null) foreach (var e in mlo.entities) hs.Add(e._Data.archetypeName.Hash);
                    if (mlo.entitySets != null) foreach (var set in mlo.entitySets) if (set?.Entities != null) foreach (var e in set.Entities) hs.Add(e._Data.archetypeName.Hash);
                    res.Add((a.Name ?? JenkIndex.TryGetString(a.Hash.Hash), System.IO.Path.GetFileNameWithoutExtension(fe.NameLower ?? fe.Name), hs));
                }
            }
            catch { }
        }
        return res;
    }

    /// <summary>Raw bytes of a model file (resource header included), ready for CodeWalker's loaders.</summary>
    public byte[] ModelBytes(GameAsset a) => FileBytes(a.Entry);

    static byte[] FileBytes(RpfFileEntry e)
    {
        var raw = e.File.ExtractFile(e);
        if (raw == null) return null;
        return e is RpfResourceFileEntry rr ? ResourceBuilder.AddResourceHeader(rr, ResourceBuilder.Compress(raw)) : raw;
    }

    // ---- reading a model straight from the archive (map streaming): no file written and read back
    /// <summary>Texture dictionaries already parsed, by archive entry (the big shared parent dictionaries are read once).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<RpfFileEntry, Lazy<YtdFile>> ytdParsed = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<RpfFileEntry> ytdOrder = new();

    YtdFile ParsedYtd(RpfFileEntry e)
    {
        var lz = ytdParsed.GetOrAdd(e, ee => new Lazy<YtdFile>(() =>
        {
            ytdOrder.Enqueue(ee);
            var raw = ee.File.ExtractFile(ee);
            if (raw == null) return null;
            var y = new YtdFile();
            if (ee is RpfResourceFileEntry rr) y.Load(raw, rr); else y.Load(raw);
            return y;
        }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
        // keep the memory in check (big dictionaries): the oldest are dropped past 12
        while (ytdParsed.Count > 12 && ytdOrder.TryDequeue(out var old)) ytdParsed.TryRemove(old, out _);
        try { return lz.Value; } catch { return null; }
    }

    /// <summary>The model's decompressed resource and its entry, and its texture dictionaries (own first, then parents), parsed.</summary>
    public (byte[] Raw, RpfResourceFileEntry Entry, List<YtdFile> Textures) ReadModel(GameAsset a, uint archetypeHash)
    {
        if (a.Entry is not RpfResourceFileEntry rr) return (null, null, null);
        var raw = rr.File.ExtractFile(rr);
        BuildTypeIndex();
        var tex = new List<YtdFile>();
        foreach (var ytd in TextureChainOf(a, archetypeHash)) tex.Add(ParsedYtd(ytd.Entry));
        return (raw, rr, tex);
    }

    /// <summary>Imports a game model in memory (textures the materials use are written under <paramref name="folder"/>). Null when it can't.</summary>
    public MoloMax.Core.IO.ImportResult ImportInMemory(GameAsset a, uint archetypeHash, string folder, MoloMax.Core.IO.ImportOptions opt)
    {
        var (raw, entry, tex) = ReadModel(a, archetypeHash);
        if (raw == null) return null;
        return MoloMax.Core.Gta.Import.GtaImporter.ImportResource(a.Name, a.Ext, raw, entry, tex, folder, opt);
    }

    /// <summary>Writes the model (and its texture dictionary as name.ytd next to it) into a folder; returns the model file path.</summary>
    public string Extract(GameAsset a, string folder) => Extract(a, folder, a.Hash);

    /// <summary>Same, with the textures of this archetype (for a model inside a drawable dictionary).</summary>
    public string Extract(GameAsset a, string folder, uint archetypeHash)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, a.Name + a.Ext);
        File.WriteAllBytes(path, FileBytes(a.Entry) ?? throw new InvalidDataException("Could not read " + a.Name + a.Ext + " from the archive."));
        // the textures: the archetype's dictionary as name.ytd, its parents as name.parent1.ytd, name.parent2.ytd...
        BuildTypeIndex();
        foreach (var old in Directory.GetFiles(folder, a.Name + ".parent*.ytd")) { try { File.Delete(old); } catch { } }
        int k = 0;
        foreach (var ytd in TextureChainOf(a, archetypeHash))
        {
            if (FileBytes(ytd.Entry) is not { } td) continue;
            File.WriteAllBytes(System.IO.Path.Combine(folder, k == 0 ? a.Name + ".ytd" : $"{a.Name}.parent{k}.ytd"), td);
            k++;
        }
        return path;
    }
}
