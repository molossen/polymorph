using System.Numerics;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// The game's distant lights: the points of light that show the city, the roads and the houses at night from far away
/// (the *_distantlights.ymap files). Colour packed as the game keeps it: intensity, red, green, blue (low byte first).
/// </summary>
public sealed class DistantLights
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public uint[] Colours = Array.Empty<uint>();
    public int Files;

    public static DistantLights Load(MapWorldIndex idx)
    {
        var maps = idx.Ymaps.Where(y => y.Name.Contains("distantlights", StringComparison.OrdinalIgnoreCase) || y.Name.Contains("lodlights", StringComparison.OrdinalIgnoreCase)).ToList();
        var pos = new List<Vector3>[maps.Count]; var col = new List<uint>[maps.Count];
        int files = 0;
        System.Threading.Tasks.Parallel.For(0, maps.Count, i =>
        {
            try
            {
                var y = Cw.RpfFile.GetFile<Cw.YmapFile>(maps[i].Entry);
                var d = y?.DistantLODLights;
                if (d?.positions == null || d.colours == null) return;
                var p = new List<Vector3>(d.positions.Length); var c = new List<uint>(d.positions.Length);
                for (int k = 0; k < d.positions.Length && k < d.colours.Length; k++) { var v = d.positions[k]; p.Add(new Vector3(v.x, v.y, v.z)); c.Add(d.colours[k]); }
                pos[i] = p; col[i] = c;
                System.Threading.Interlocked.Increment(ref files);
            }
            catch { }
        });
        var res = new DistantLights { Files = files };
        res.Positions = pos.Where(l => l != null).SelectMany(l => l).ToArray();
        res.Colours = col.Where(l => l != null).SelectMany(l => l).ToArray();
        return res;
    }
}
