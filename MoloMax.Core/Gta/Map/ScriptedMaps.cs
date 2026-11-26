using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// Which .ymap files the game shows by itself. Maps flagged "scripted" are only switched on by missions (prologue, Cayo Perico,
/// open / closed states, cutscene reflections...): only the usual one of each place is kept, the way the game looks
/// in free roam. Maps sharing an inner name with another (a mission variant under another file name) are left out too.
/// </summary>
public static class ScriptedMaps
{
    static readonly Dictionary<string, string[]> Alternatives = new()
    {
        ["open"] = new[] { "closed" }, ["end"] = new[] { "start" }, ["fake"] = new[] { "real" }, ["broken"] = new[] { "closed", "" },
        ["swap"] = new[] { "" }, ["up"] = new[] { "" },
        ["dixon"] = new[] { "forsale" }, ["madonna"] = new[] { "forsale" }, ["solomun"] = new[] { "forsale" }, ["taleofus"] = new[] { "forsale" },
        ["original"] = new[] { "shared", "mansion" }, ["generic"] = new[] { "private" },
    };
    static readonly HashSet<string> SceneOnly = new() { "cutscene", "replay" };

    /// <summary>For each map (file names lower case, inner name hash, parent hash, flags, every object shown within 10 m only,
    /// read): kept in the world or not.</summary>
    public static bool[] Kept(string[] names, (uint Name, uint Parent, uint Flags, bool ShortLod, bool Read)[] maps)
    {
        int n = names.Length;
        var keep = new bool[n];
        var byName = new Dictionary<uint, int>();
        bool Original(int i) => maps[i].Name == Cw.JenkHash.GenHash(names[i]);
        var scripted = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (!maps[i].Read) continue;
            if ((maps[i].Flags & 1) != 0) { scripted.Add(i); continue; }
            if (maps[i].Name == 0) { keep[i] = true; continue; }
            if (!byName.TryGetValue(maps[i].Name, out var ex) || Original(i) && !Original(ex)) byName[maps[i].Name] = i;
        }
        foreach (var i in byName.Values) keep[i] = true;
        // scripted maps listed on their own: not the parent of another scripted map
        var parents = scripted.Select(i => maps[i].Parent).ToHashSet();
        var listed = scripted.Where(i => !parents.Contains(maps[i].Name)).GroupBy(i => names[i]).Select(g => g.First()).ToList();
        var listedNames = listed.Select(i => names[i]).ToHashSet();
        var scriptedByName = new Dictionary<uint, int>();
        foreach (var i in scripted) scriptedByName.TryAdd(maps[i].Name, i);
        var pending = new Queue<int>(listed.Where(i => !Hidden(names[i], listedNames, maps[i].ShortLod)));
        var known = byName.Keys.ToHashSet();
        while (pending.TryDequeue(out var i))
        {
            keep[i] = true; known.Add(maps[i].Name);
            uint p = maps[i].Parent;
            if (p != 0 && !known.Contains(p) && scriptedByName.TryGetValue(p, out var pm) && !keep[pm]) pending.Enqueue(pm);
        }
        return keep;
    }

    /// <summary>A scripted map left out by default.</summary>
    public static bool Hidden(string name, HashSet<string> names, bool shortLod)
    {
        if (name.Contains("shadowsunmesh") || name.Contains("shadowmesh") || name.Contains("proxy")) return true;
        if (shortLod) return true; // placeholders seen within a few metres: their parent would stand in for them from everywhere
        var words = name.Split('_');
        for (int i = 0; i < words.Length; i++)
        {
            if (SceneOnly.Contains(words[i])) return true;
            if (i == 0 || !Alternatives.TryGetValue(words[i], out var defaults)) continue;
            string before = string.Join('_', words, 0, i);
            string after = string.Concat(words.Skip(i + 1).Select(w => "_" + w));
            foreach (var d in defaults)
            {
                string other = d.Length == 0 ? before : $"{before}_{d}";
                if (names.Contains(other) || after.Length > 0 && names.Contains(other + after)) return true;
            }
            if (words[i] == "original" && i >= 2 && names.Contains($"{string.Join('_', words, 0, i - 1)}_mansion_shared")) return true;
        }
        return false;
    }
}
