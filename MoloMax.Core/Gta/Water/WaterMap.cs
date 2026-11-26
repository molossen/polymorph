using System.Globalization;
using System.Numerics;
using System.Xml;

namespace MoloMax.Core.Gta.Water;

/// <summary>A flat piece of the game's water at height Z: the rectangle, or the half left when one corner is cut along the diagonal.</summary>
public readonly record struct WaterQuad(float MinX, float MaxX, float MinY, float MaxY, float Z, int Shape);

/// <summary>
/// The water surfaces of the map from the game's water.xml (sea, lakes, rivers, pools), and the open ocean around the
/// area that file covers.
/// </summary>
public sealed class WaterMap
{
    public static readonly string[] Files = { @"common.rpf\data\levels\gta5\water.xml", @"update\update.rpf\common\data\levels\gta5\water_heistisland.xml" };

    public readonly List<WaterQuad> Quads = new();
    /// <summary>Area covered by the main water file (min x, min y, max x, max y): the open ocean is outside, at height 0.</summary>
    public Vector4 OceanBounds;

    public static WaterMap Load(Func<string, byte[]> read)
    {
        var m = new WaterMap();
        for (int i = 0; i < Files.Length; i++)
        {
            byte[] data; try { data = read(Files[i]); } catch { data = null; }
            if (data == null) continue;
            try
            {
                var q = Read(new MemoryStream(data));
                if (i == 0 && q.Count > 0) m.OceanBounds = new Vector4(q.Min(x => x.MinX), q.Min(x => x.MinY), q.Max(x => x.MaxX), q.Max(x => x.MaxY));
                m.Quads.AddRange(q);
            }
            catch { }
        }
        return m;
    }

    public static List<WaterQuad> Read(Stream xml)
    {
        var quads = new List<WaterQuad>();
        using var r = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        bool inWater = false;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.Name is "WaterQuads") { inWater = true; continue; }
            if (r.Name is "CalmingQuads" or "WaveQuads") { inWater = false; continue; }
            if (r.Name != "Item" || !inWater) continue;
            var v = new Dictionary<string, string>();
            using (var it = r.ReadSubtree())
            {
                it.Read();
                while (it.Read()) if (it.NodeType == XmlNodeType.Element && it.GetAttribute("value") is { } val) v[it.Name] = val;
            }
            float G(string n) => v.TryGetValue(n, out var t) && float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            if (v.TryGetValue("IsInvisible", out var inv) && inv.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            int type = (int)G("Type");
            quads.Add(new WaterQuad(G("minX"), G("maxX"), G("minY"), G("maxY"), G("z"), type is >= 0 and <= 4 ? type : 0));
        }
        return quads;
    }

    /// <summary>Triangles of every surface, plus the open ocean as a ring of big quads out to <paramref name="reach"/> metres.</summary>
    public List<Vector3> Triangles(float reach = 30000)
    {
        var t = new List<Vector3>(Quads.Count * 6 + 24);
        void Quad(float x0, float x1, float y0, float y1, float z, int shape)
        {
            var sw = new Vector3(x0, y0, z); var se = new Vector3(x1, y0, z); var nw = new Vector3(x0, y1, z); var ne = new Vector3(x1, y1, z);
            switch (shape)
            {
                case 1: t.Add(sw); t.Add(se); t.Add(nw); break;
                case 2: t.Add(sw); t.Add(ne); t.Add(nw); break;
                case 3: t.Add(se); t.Add(ne); t.Add(nw); break;
                case 4: t.Add(sw); t.Add(se); t.Add(ne); break;
                default: t.Add(sw); t.Add(se); t.Add(nw); t.Add(nw); t.Add(se); t.Add(ne); break;
            }
        }
        foreach (var q in Quads) Quad(q.MinX, q.MaxX, q.MinY, q.MaxY, q.Z, q.Shape);
        if (OceanBounds != Vector4.Zero)
        {
            var b = OceanBounds; float R = reach;
            Quad(-R, R, -R, b.Y, 0, 0);          // south
            Quad(-R, R, b.W, R, 0, 0);           // north
            Quad(-R, b.X, b.Y, b.W, 0, 0);       // west
            Quad(b.Z, R, b.Y, b.W, 0, 0);        // east
        }
        return t;
    }
}
