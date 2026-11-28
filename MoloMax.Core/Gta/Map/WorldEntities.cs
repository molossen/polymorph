using System.Numerics;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>One placed object of the game map, as a light record (no model, no editing): enough to choose what to draw.</summary>
public struct WorldEnt
{
    public uint Arch;
    public Vector3 Pos;
    public Quaternion Rot;
    public float ScaleXY, ScaleZ;
    public float LodDist, ChildLodDist;
    public byte Level;      // 0 HD, 1 LOD, 2 SLOD1, 3 SLOD2, 4 SLOD3, 5 orphan HD
    public bool Mlo;
    public int Ymap;        // index into Ymaps
    public int Index;       // place in its .ymap's entity list
    public int Parent;      // global index of the LOD parent, -1 none
    public int FirstChild, ChildCount; // into ChildList
    public Matrix4x4 World => Matrix4x4.CreateScale(ScaleXY, ScaleXY, ScaleZ) * Matrix4x4.CreateFromQuaternion(Rot) * Matrix4x4.CreateTranslation(Pos);
}

/// <summary>
/// Every object of the game map at once (about a million light records), linked by the game's level-of-detail tree, and
/// the choice of what to draw for a camera the way the game does it: from far away one simplified model (SLOD) stands
/// for a whole district, closer it is replaced by its children (LOD), then by theirs (HD). Read once from the game files
/// (some seconds) and kept as a small binary file for the next starts.
/// </summary>
public sealed class WorldEntities
{
    public WorldEnt[] E = Array.Empty<WorldEnt>();
    public int[] ChildList = Array.Empty<int>();
    public string[] Ymaps = Array.Empty<string>();
    Dictionary<string, int> ymapIndex;
    /// <summary>Place of a .ymap (by name) in <see cref="Ymaps"/>, -1 when not part of the world.</summary>
    public int YmapIndex(string name)
    {
        ymapIndex ??= Ymaps.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        return name != null && ymapIndex.TryGetValue(name, out var i) ? i : -1;
    }
    /// <summary>Entities without a parent: the far ones (LOD / SLOD with children) are visited every time, the HD ones by cells.</summary>
    public int[] FarRoots = Array.Empty<int>();
    readonly Dictionary<long, List<int>> cells = new();
    public const float CellSize = 100f, NearRootDistance = 750f;
    const int Magic = 0x45574D50, Version = 4; // "PMWE"; 2: scripted maps left out, 3: parents in the same map (flag 8 clear), 4: North Yankton left out

    static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// <summary>Reads every .ymap of the index (or the cache file when it matches).</summary>
    public static WorldEntities Build(MapWorldIndex idx, Func<uint, float> archetypeLodDist, string cacheFile, string stamp, Action<string, float> progress = null)
    {
        var w = cacheFile != null ? Load(cacheFile, stamp) : null;
        if (w == null)
        {
            w = Read(idx, progress);
            if (cacheFile != null) try { w.Save(cacheFile, stamp); } catch { }
        }
        // draw distances the files leave at 0 come from the archetype
        for (int i = 0; i < w.E.Length; i++)
            if (w.E[i].LodDist <= 0) { float a = archetypeLodDist?.Invoke(w.E[i].Arch) ?? 0; w.E[i].LodDist = a > 0 ? a : 120; }
        w.Index();
        progress?.Invoke($"{w.E.Length:N0} map objects ready", 1);
        return w;
    }

    static WorldEntities Read(MapWorldIndex idx, Action<string, float> progress)
    {
        var w = new WorldEntities();
        var ymaps = idx.Ymaps.ToList();
        var names = ymaps.Select(y => y.Name.ToLowerInvariant()).ToArray();
        var perMap = new (WorldEnt[] Ents, uint Parent, uint Name, uint Flags, bool ShortLod)[ymaps.Count];
        var inParent = new bool[ymaps.Count][]; // entity flag 8: its LOD parent is in the parent map, else in its own map
        int done = 0;
        System.Threading.Tasks.Parallel.For(0, ymaps.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) }, i =>
        {
            try
            {
                var y = Cw.RpfFile.GetFile<Cw.YmapFile>(ymaps[i].Entry);
                if (y == null) return;
                var list = new List<WorldEnt>(); var inPar = new List<bool>();
                var all = y.AllEntities ?? Array.Empty<Cw.YmapEntityDef>();
                for (int ei = 0; ei < all.Length; ei++)
                {
                    var e = all[ei];
                    if (e == null || e.MloParent != null) continue;
                    var c = e._CEntityDef;
                    var q = e.Orientation;
                    inPar.Add((c.flags & 8) != 0);
                    list.Add(new WorldEnt
                    {
                        Arch = c.archetypeName.Hash, Pos = new Vector3(e.Position.X, e.Position.Y, e.Position.Z),
                        Rot = Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)), ScaleXY = e.Scale.X, ScaleZ = e.Scale.Z,
                        LodDist = c.lodDist, ChildLodDist = c.childLodDist, Level = (byte)Math.Clamp((int)c.lodLevel, 0, 5), Mlo = e.IsMlo,
                        Ymap = i, Index = ei, Parent = c.parentIndex, // local index for now
                    });
                }
                inParent[i] = inPar.ToArray();
                perMap[i] = (list.ToArray(), y._CMapData.parent.Hash, y._CMapData.name.Hash, y._CMapData.flags, all.Length > 0 && all.All(x => x?._CEntityDef.lodDist is > 0f and <= 10f));
            }
            catch { }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 300 == 0) progress?.Invoke($"Map objects {d:N0} / {ymaps.Count:N0}", d / (float)ymaps.Count);
        });
        // what the game loads by itself: a map a script switches on (flag 1) only when it is one of the usual ones
        // (rules in ScriptedMaps), and of two maps with one inner name the one whose file has that name
        var keep = ScriptedMaps.Kept(names, perMap.Select(p => (p.Name, p.Parent, p.Flags, p.ShortLod, p.Ents != null)).ToArray());
        for (int i = 0; i < ymaps.Count; i++) if (!keep[i]) perMap[i].Ents = null;
        // one array, then the parents: the parent .ymap (by name hash) + the parent's place in it
        var first = new int[ymaps.Count]; int total = 0;
        for (int i = 0; i < ymaps.Count; i++) { first[i] = total; total += perMap[i].Ents?.Length ?? 0; }
        var E = new WorldEnt[total];
        var mapByHash = new Dictionary<uint, int>();
        for (int i = 0; i < names.Length; i++) if (keep[i] && perMap[i].Name != 0) mapByHash.TryAdd(perMap[i].Name, i);
        for (int i = 0; i < names.Length; i++) if (keep[i]) mapByHash.TryAdd(Cw.JenkHash.GenHash(names[i]), i);
        // a .ymap's entity place (file order, MLO children skipped) -> global index
        var placeMaps = new Dictionary<int, int>[ymaps.Count];
        for (int i = 0; i < ymaps.Count; i++)
        {
            var ents = perMap[i].Ents; if (ents == null) continue;
            var pm = placeMaps[i] = new Dictionary<int, int>(ents.Length);
            for (int k = 0; k < ents.Length; k++) { E[first[i] + k] = ents[k]; pm[ents[k].Index] = first[i] + k; }
        }
        for (int i = 0; i < ymaps.Count; i++)
        {
            var ents = perMap[i].Ents; if (ents == null) continue;
            int pmap = perMap[i].Parent != 0 && mapByHash.TryGetValue(perMap[i].Parent, out var pi) ? pi : -1;
            for (int k = 0; k < ents.Length; k++)
            {
                int g = first[i] + k, local = E[g].Parent;
                int m = inParent[i] != null && k < inParent[i].Length && !inParent[i][k] ? i : pmap;
                int par = m >= 0 && local >= 0 && placeMaps[m] != null && placeMaps[m].TryGetValue(local, out var pg) && pg != g ? pg : -1;
                // a parent is coarser than its child, else the game ignores the link
                if (par >= 0 && Coarse(E[par].Level) <= Coarse(E[g].Level)) par = -1;
                E[g].Parent = par;
            }
        }
        w.E = E; w.Ymaps = names;
        return w;
    }

    static int Coarse(byte level) => level == 5 ? 0 : level;

    int[] mapFirst, mapCount;
    /// <summary>Global index of the object at place <paramref name="index"/> of map <paramref name="ymap"/> (-1 none).</summary>
    public int Find(int ymap, int index)
    {
        if (mapFirst == null || ymap < 0 || ymap >= mapFirst.Length) return -1;
        int lo = mapFirst[ymap], hi = lo + mapCount[ymap] - 1;
        while (lo <= hi) { int m = (lo + hi) >> 1; int v = E[m].Index; if (v == index) return m; if (v < index) lo = m + 1; else hi = m - 1; }
        return -1;
    }

    /// <summary>Children lists, far roots and the grid of the near roots.</summary>
    void Index()
    {
        mapFirst = new int[Ymaps.Length]; mapCount = new int[Ymaps.Length];
        for (int i = E.Length - 1; i >= 0; i--) { int y = E[i].Ymap; if (y >= 0 && y < mapFirst.Length) { mapFirst[y] = i; mapCount[y]++; } }
        var count = new int[E.Length];
        foreach (var e in E) if (e.Parent >= 0) count[e.Parent]++;
        int at = 0;
        for (int i = 0; i < E.Length; i++) { E[i].FirstChild = at; E[i].ChildCount = 0; at += count[i]; }
        ChildList = new int[at];
        for (int i = 0; i < E.Length; i++) { int p = E[i].Parent; if (p >= 0) ChildList[E[p].FirstChild + E[p].ChildCount++] = i; }
        var far = new List<int>();
        cells.Clear();
        for (int i = 0; i < E.Length; i++)
        {
            if (E[i].Parent >= 0) continue;
            if (E[i].LodDist > NearRootDistance || E[i].ChildCount > 0) far.Add(i);
            else
            {
                var k = Key((int)MathF.Floor(E[i].Pos.X / CellSize), (int)MathF.Floor(E[i].Pos.Y / CellSize));
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                l.Add(i);
            }
        }
        FarRoots = far.ToArray();
    }

    /// <summary>What to draw for a camera position (indices into E). <paramref name="skip"/>: entities shown another way (their
    /// .ymap is open for editing) - not drawn, but their place in the tree still decides for the others.</summary>
    public List<int> Select(Vector3 cam, float mult, Func<int, bool> skipMap = null, List<int> into = null)
    {
        var res = into ?? new List<int>(8192); res.Clear();
        foreach (var r in FarRoots) Visit(r, true, cam, mult, skipMap, res);
        float rr = NearRootDistance * mult; int r0x = (int)MathF.Floor((cam.X - rr) / CellSize), r1x = (int)MathF.Floor((cam.X + rr) / CellSize);
        int r0y = (int)MathF.Floor((cam.Y - rr) / CellSize), r1y = (int)MathF.Floor((cam.Y + rr) / CellSize);
        for (int x = r0x; x <= r1x; x++)
            for (int y = r0y; y <= r1y; y++)
                if (cells.TryGetValue(Key(x, y), out var l))
                    foreach (var i in l) Visit(i, true, cam, mult, skipMap, res);
        return res;
    }

    void Visit(int i, bool root, Vector3 cam, float mult, Func<int, bool> skipMap, List<int> res)
    {
        ref var e = ref E[i];
        float d = Vector3.Distance(cam, e.Pos);
        if (root && d > e.LodDist * mult) return;
        if (e.ChildCount > 0 && HandsOver(i, d, cam, mult))
        {
            for (int k = 0; k < e.ChildCount; k++) Visit(ChildList[e.FirstChild + k], false, cam, mult, skipMap, res);
            return;
        }
        // a stand-in for detailed objects that are gone (no children left), within its switch distance: not shown up close
        if (e.Level is not (0 or 5) && e.ChildCount == 0 && e.ChildLodDist > 0 && d < e.ChildLodDist * mult) return;
        if (skipMap != null && skipMap(e.Ymap)) return;
        res.Add(i);
    }

    bool HandsOver(int i, float d, Vector3 cam, float mult)
    {
        ref var e = ref E[i];
        if (e.ChildLodDist > 0 && d < e.ChildLodDist * mult) return true;
        for (int k = 0; k < e.ChildCount; k++)
        {
            ref var c = ref E[ChildList[e.FirstChild + k]];
            if (Vector3.Distance(cam, c.Pos) <= c.LodDist * mult) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- cache file

    void Save(string path, string stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic); w.Write(Version); w.Write(stamp ?? "");
            w.Write(Ymaps.Length); foreach (var n in Ymaps) w.Write(n);
            w.Write(E.Length);
            foreach (var e in E)
            {
                w.Write(e.Arch); w.Write(e.Pos.X); w.Write(e.Pos.Y); w.Write(e.Pos.Z);
                w.Write(e.Rot.X); w.Write(e.Rot.Y); w.Write(e.Rot.Z); w.Write(e.Rot.W);
                w.Write(e.ScaleXY); w.Write(e.ScaleZ); w.Write(e.LodDist); w.Write(e.ChildLodDist);
                w.Write(e.Level); w.Write(e.Mlo); w.Write(e.Ymap); w.Write(e.Index); w.Write(e.Parent);
            }
        }
        File.Move(tmp, path, true);
    }

    static WorldEntities Load(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadString() != (stamp ?? "")) return null;
            var w = new WorldEntities();
            w.Ymaps = new string[r.ReadInt32()];
            for (int i = 0; i < w.Ymaps.Length; i++) w.Ymaps[i] = r.ReadString();
            var E = new WorldEnt[r.ReadInt32()];
            for (int i = 0; i < E.Length; i++)
                E[i] = new WorldEnt
                {
                    Arch = r.ReadUInt32(), Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    ScaleXY = r.ReadSingle(), ScaleZ = r.ReadSingle(), LodDist = r.ReadSingle(), ChildLodDist = r.ReadSingle(),
                    Level = r.ReadByte(), Mlo = r.ReadBoolean(), Ymap = r.ReadInt32(), Index = r.ReadInt32(), Parent = r.ReadInt32(),
                };
            w.E = E;
            return w;
        }
        catch { return null; }
    }
}
