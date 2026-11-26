using System.Numerics;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// The game's distant lights: the points of light that show the city, the roads and the houses at night from far away
/// (the *_distantlights.ymap files). Colour packed as the game keeps it: intensity, red, green, blue (low byte first).
/// With each one, when its *_lodlights.ymap has it: how far it lights (m), where a spot points, its cone, its glow and
/// the hours it is on (bit h = lit at hour h).
/// </summary>
public sealed class DistantLights
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public uint[] Colours = Array.Empty<uint>();
    public Lamp[] Lamps = Array.Empty<Lamp>();
    public int Files, Detailed;

    /// <summary>One lamp's light on the surfaces around it: Reach 0 = no detail known (lit at night, 8 m all around).</summary>
    public struct Lamp
    {
        public float Reach, Corona, CosOuter, CosInner;
        public Vector3 Direction;
        public uint Hours;
    }

    /// <summary>Hours of the night (20:00 to 6:00), for the lights without detail.</summary>
    public const uint NightHours = 0xF0003F;

    public static DistantLights Load(MapWorldIndex idx)
    {
        var maps = idx.Ymaps.Where(y => y.Name.Contains("distantlights", StringComparison.OrdinalIgnoreCase) || y.Name.Contains("lodlights", StringComparison.OrdinalIgnoreCase)).ToList();
        var dist = new (uint Name, List<Vector3> P, List<uint> C)?[maps.Count];
        var lods = new (uint Parent, Cw.YmapLODLights L)?[maps.Count];
        int files = 0;
        System.Threading.Tasks.Parallel.For(0, maps.Count, i =>
        {
            try
            {
                var y = Cw.RpfFile.GetFile<Cw.YmapFile>(maps[i].Entry);
                if (y == null) return;
                var d = y.DistantLODLights;
                if (d?.positions != null && d.colours != null)
                {
                    var p = new List<Vector3>(d.positions.Length); var c = new List<uint>(d.positions.Length);
                    for (int k = 0; k < d.positions.Length && k < d.colours.Length; k++) { var v = d.positions[k]; p.Add(new Vector3(v.x, v.y, v.z)); c.Add(d.colours[k]); }
                    dist[i] = (Cw.JenkHash.GenHash(Path.GetFileNameWithoutExtension(maps[i].Name).ToLowerInvariant()), p, c);
                    System.Threading.Interlocked.Increment(ref files);
                }
                if (y.LODLights?.falloff != null) lods[i] = (y._CMapData.parent.Hash, y.LODLights);
            }
            catch { }
        });
        var detail = new Dictionary<uint, Cw.YmapLODLights>();
        foreach (var l in lods) if (l is { } x && x.Parent != 0) detail.TryAdd(x.Parent, x.L);
        var res = new DistantLights { Files = files };
        var pos = new List<Vector3>(); var col = new List<uint>(); var lamps = new List<Lamp>();
        var seen = new HashSet<uint>();
        foreach (var e in dist)
        {
            if (e is not { } m || !seen.Add(m.Name)) continue;
            detail.TryGetValue(m.Name, out var lod);
            for (int k = 0; k < m.P.Count; k++)
            {
                pos.Add(m.P[k]); col.Add(m.C[k]);
                lamps.Add(lod != null && k < lod.falloff.Length ? Detail(lod, k) : new Lamp { Hours = NightHours, CosOuter = -2, CosInner = -2, Corona = 0.5f });
                if (lod != null && k < lod.falloff.Length) res.Detailed++;
            }
        }
        res.Positions = pos.ToArray(); res.Colours = col.ToArray(); res.Lamps = lamps.ToArray();
        return res;
    }

    static Lamp Detail(Cw.YmapLODLights l, int k)
    {
        uint flags = l.timeAndStateFlags != null && k < l.timeAndStateFlags.Length ? l.timeAndStateFlags[k] : 0;
        var lamp = new Lamp
        {
            Reach = Math.Clamp(l.falloff[k], 1f, 60f), Hours = flags & 0xFFFFFF, CosOuter = -2, CosInner = -2,
            Corona = 0.5f + (l.coronaIntensity != null && k < l.coronaIntensity.Length ? l.coronaIntensity[k] : 0) / 255f * 2f,
        };
        if (lamp.Hours == 0) lamp.Hours = NightHours;
        bool spot = ((flags >> 26) & 7) == 2;
        if (spot && l.direction != null && k < l.direction.Length)
        {
            var dir = new Vector3(l.direction[k].x, l.direction[k].y, l.direction[k].z);
            float outer = MathF.Min((l.coneOuterAngleOrCapExt?[k] ?? 0) * 180f / 255f, 89f);
            float inner = MathF.Min((l.coneInnerAngle?[k] ?? 0) * 180f / 255f, outer * 0.9f);
            if (dir.LengthSquared() > 1e-6f && outer >= 1f)
            {
                lamp.Direction = Vector3.Normalize(dir);
                lamp.CosOuter = MathF.Cos(outer * MathF.PI / 180f); lamp.CosInner = MathF.Cos(inner * MathF.PI / 180f);
            }
        }
        return lamp;
    }
}
