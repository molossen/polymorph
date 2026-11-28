using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// A FiveM resource's stream folder read like a CodeWalker project: its .ymap files, its models (.ydr / .yft / .ydd) and
/// what its .ytyp files say about them (names, drawable dictionaries, interiors).
/// </summary>
public sealed class StreamFolder
{
    public string Folder;
    public readonly List<string> Ymaps = new();
    /// <summary>Archetype hash -> its .ydr / .yft in the folder.</summary>
    public readonly Dictionary<uint, string> Models = new();
    /// <summary>Drawable dictionary hash -> its .ydd in the folder.</summary>
    public readonly Dictionary<uint, string> Dictionaries = new();
    /// <summary>Archetype hash -> the drawable dictionary holding its model (from the .ytyp).</summary>
    public readonly Dictionary<uint, uint> DictionaryOf = new();
    /// <summary>Texture dictionaries (.ytd) of the folder: name hash -> file.</summary>
    public readonly Dictionary<uint, string> Ytds = new();
    /// <summary>Archetype hash -> the texture dictionary its .ytyp names (textureDictionary).</summary>
    public readonly Dictionary<uint, uint> TextureDictOf = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Cw.YtdFile>> parsedYtd = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A .ytd of the folder, parsed once (null when it cannot be read).</summary>
    public Cw.YtdFile ParsedYtd(string file) =>
        parsedYtd.GetOrAdd(file, f => new Lazy<Cw.YtdFile>(() => { try { var y = new Cw.YtdFile(); y.Load(File.ReadAllBytes(f)); return y; } catch { return null; } })).Value;
    /// <summary>Interior (MLO archetype) hash -> the .ytyp holding it.</summary>
    public readonly Dictionary<uint, string> Interiors = new();
    public readonly Dictionary<uint, string> Names = new();
    public readonly List<string> Ytyps = new();
    public readonly List<string> Errors = new();

    /// <summary>Where a model of the stream folder finds the textures it does not carry: the dictionary its .ytyp names
    /// (in the folder, else the game's with its parents), a .ytd named like the model, then every other .ytd of the folder.</summary>
    public IList<object> TexturesFor(MoloMax.Core.Gta.Game.GameFiles g, uint hash)
    {
        var res = new List<object>(); var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Local(uint h) { if (Ytds.TryGetValue(h, out var f) && done.Add(f) && ParsedYtd(f) is { } y) res.Add(y); }
        if (TextureDictOf.TryGetValue(hash, out var txd))
        {
            Local(txd);
            if (g != null && !Ytds.ContainsKey(txd)) try { res.AddRange(g.ParsedTextureChain(txd)); } catch { }
        }
        Local(hash);
        foreach (var f in Ytds.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) if (done.Add(f) && ParsedYtd(f) is { } y) res.Add(y);
        // the game's dictionary of a model with the same name (a game model reworked in the folder keeps the game's textures)
        if (g != null && !TextureDictOf.ContainsKey(hash)) try { res.AddRange(g.ParsedTextureChain(hash)); if (g.FindModel(hash) is { } gm) foreach (var c in g.TextureChainOf(gm, hash)) res.AddRange(g.ParsedTextureChain(c.Hash)); } catch { }
        return res;
    }

    public static StreamFolder Read(string folder)
    {
        var s = new StreamFolder { Folder = folder };
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            uint h = MapDocument.Hash(name);
            switch (ext)
            {
                case ".ymap": s.Ymaps.Add(f); break;
                case ".ydr": case ".yft":
                    if (name.EndsWith("_hi") && ext == ".yft") break; // the high LOD of a fragment: not an archetype
                    s.Models[h] = f; s.Names[h] = name; break;
                case ".ydd": s.Dictionaries[h] = f; break;
                case ".ytyp": s.Ytyps.Add(f); break;
                case ".ytd": s.Ytds[h] = f; break;
            }
        }
        foreach (var f in s.Ytyps)
        {
            try
            {
                var y = new Cw.YtypFile();
                y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = Path.GetFileName(f) };
                y.Load(File.ReadAllBytes(f));
                foreach (var a in y.AllArchetypes ?? Array.Empty<Cw.Archetype>())
                {
                    if (a == null) continue;
                    uint h = a.Hash.Hash;
                    if (!string.IsNullOrEmpty(a.Name) && !a.Name.StartsWith("0x")) s.Names.TryAdd(h, a.Name.ToLowerInvariant());
                    if (a.TextureDict.Hash != 0) s.TextureDictOf[h] = a.TextureDict.Hash;
                    if (a is Cw.MloArchetype) s.Interiors[h] = f;
                    else if (a.DrawableDict.Hash != 0 && !s.Models.ContainsKey(h)) s.DictionaryOf[h] = a.DrawableDict.Hash;
                }
            }
            catch (Exception e) { s.Errors.Add(Path.GetFileName(f) + ": " + e.Message); }
        }
        s.Ymaps.Sort(StringComparer.OrdinalIgnoreCase);
        return s;
    }

    /// <summary>The file holding this archetype's model in the folder (.ydr / .yft, else its .ydd), or null.</summary>
    public string ModelFile(uint hash) =>
        Models.TryGetValue(hash, out var f) ? f : DictionaryOf.TryGetValue(hash, out var d) && Dictionaries.TryGetValue(d, out var dd) ? dd : null;

    public bool Contains(string path) =>
        path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
