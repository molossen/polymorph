// The game water, its zones of calm and waves and its look, read from the game files (taken from the reference map view).
#nullable enable
using System.Globalization;
using System.Numerics;
using System.Xml;

namespace MoloMax.Core.Gta.WaterLook;

// Shape of a WaterQuad: the whole rectangle, or the half of it left when one corner is cut off along the diagonal
// (value of the "Type" field of water.xml).
public enum WaterQuadShape
{
    Rectangle = 0,

    WithoutNorthEast = 1,

    WithoutSouthEast = 2,

    WithoutSouthWest = 3,

    WithoutNorthWest = 4,
}

// A flat piece of water surface at height Z (sea at 0, lakes and rivers higher).
public readonly record struct WaterQuad(float MinX, float MaxX, float MinY, float MaxY, float Z, WaterQuadShape Shape)
{
    public IEnumerable<Vector3> Triangles()
    {
        var southWest = new Vector3(MinX, MinY, Z);
        var southEast = new Vector3(MaxX, MinY, Z);
        var northWest = new Vector3(MinX, MaxY, Z);
        var northEast = new Vector3(MaxX, MaxY, Z);
        Vector3[] corners = Shape switch
        {
            WaterQuadShape.WithoutNorthEast => [southWest, southEast, northWest],
            WaterQuadShape.WithoutSouthEast => [southWest, northEast, northWest],
            WaterQuadShape.WithoutSouthWest => [southEast, northEast, northWest],
            WaterQuadShape.WithoutNorthWest => [southWest, southEast, northEast],
            _ => [southWest, southEast, northWest, northWest, southEast, northEast],
        };
        return corners;
    }

    // Whether the surface covers a point (seen from above).
    public bool Contains(float x, float y)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY)
            return false;
        float u = MaxX > MinX ? (x - MinX) / (MaxX - MinX) : 0f;
        float v = MaxY > MinY ? (y - MinY) / (MaxY - MinY) : 0f;
        return Shape switch
        {
            WaterQuadShape.WithoutNorthEast => u + v <= 1f,
            WaterQuadShape.WithoutSouthEast => v >= u,
            WaterQuadShape.WithoutSouthWest => u + v >= 1f,
            WaterQuadShape.WithoutNorthWest => v <= u,
            _ => true,
        };
    }
}

// A zone of water.xml where the waves are smaller (harbours, marinas, behind breakwaters): their height is multiplied
// by Dampening (0 = flat).
public readonly record struct WaterCalming(float MinX, float MaxX, float MinY, float MaxY, float Dampening);

// A zone of water.xml where waves roll in towards the shore: their size (Amplitude, about metres) and the way they go.
public readonly record struct WaterWave(float MinX, float MaxX, float MinY, float MaxY, float Amplitude, Vector2 Direction);

// Colour of deep water at each place (the game's waterfog images): Rgba (4 bytes per texel, north row first) covers
// Area (min x, min y, max x, max y).
public sealed record WaterColorMap(int Width, int Height, byte[] Rgba, Vector4 Area);

// A tiling pattern of the game's water (Channels bytes per texel): height of the swell, light on the bottom...
public sealed record WaterPattern(int Width, int Height, int Channels, byte[] Values);

// Water surfaces of the map, read from the game's water.xml (and the Cayo Perico one): flat rectangles and triangles,
// and the zones where the waves are calmer or roll in towards the shore.
public sealed class WaterMap
{
    public static readonly string[] WaterFiles =
    [
        @"common.rpf\data\levels\gta5\water.xml",
        @"update\update.rpf\common\data\levels\gta5\water_heistisland.xml",
    ];

    public List<WaterQuad> Quads { get; } = [];

    public List<WaterCalming> Calming { get; } = [];

    public List<WaterWave> Waves { get; } = [];

    // Rectangle covered by the main map's water file (min X, min Y, max X, max Y); the open ocean is outside.
    public Vector4 OceanBounds { get; set; }

    // Height of the open ocean (Vice City's sea is 6 m up).
    public float OceanHeight { get; set; }

    // Colour of the water per place: the main map first, then Cayo Perico's (empty: one colour everywhere).
    public List<WaterColorMap> ColorMaps { get; } = [];

    public WaterPattern? Caustics { get; set; }

    // Heights of the open sea's swell (grey), and how the waves rolling in change along their crest (grey).
    public WaterPattern? OceanWaves { get; set; }

    public WaterPattern? ShoreWaves { get; set; }

    // Small ripples: slope across and along (two channels, 0.5 = flat).
    public WaterPattern? Ripples { get; set; }

    public WaterPattern? Foam { get; set; }

    public List<string> Errors { get; } = [];

    // Height of the water surface at a point (the highest one when several cover it), the open ocean's outside the
    // map's water file; null where there is no water.
    public float? HeightAt(float x, float y)
    {
        float? best = null;
        foreach (var quad in Quads)
        {
            if (quad.Contains(x, y) && (best is null || quad.Z > best))
                best = quad.Z;
        }
        if (best is null && OceanBounds != Vector4.Zero
            && (x < OceanBounds.X || y < OceanBounds.Y || x > OceanBounds.Z || y > OceanBounds.W))
            return OceanHeight;
        return best;
    }

    public static WaterMap Load(Func<string, byte[]?> read)
    {
        var map = new WaterMap();
        for (int i = 0; i < WaterFiles.Length; i++)
        {
            var path = WaterFiles[i];
            byte[]? data; try { data = read(path); } catch { data = null; }
            if (data is null)
            {
                if (i == 0)
                    map.Errors.Add($"{path} : file not found");
                continue;
            }
            try
            {
                var (quads, calming, waves) = Read(new MemoryStream(data));
                if (i == 0)
                    map.OceanBounds = BoundsOf(quads);
                map.Quads.AddRange(quads);
                map.Calming.AddRange(calming);
                map.Waves.AddRange(waves);
            }
            catch (XmlException ex)
            {
                map.Errors.Add($"{path} : {ex.Message}");
            }
        }
        return map;
    }

    public static List<WaterQuad> Parse(Stream xml) => Read(xml).Quads;

    // The three lists of a water file: surfaces, calm zones, zones of waves rolling in.
    public static (List<WaterQuad> Quads, List<WaterCalming> Calming, List<WaterWave> Waves) Read(Stream xml)
    {
        var quads = new List<WaterQuad>();
        var calming = new List<WaterCalming>();
        var waves = new List<WaterWave>();
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        string? section = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.Name is "WaterQuads" or "CalmingQuads" or "WaveQuads")
            {
                section = reader.Name;
                continue;
            }
            if (reader.Name != "Item" || section is null)
                continue;
            var values = new Dictionary<string, string>();
            using (var item = reader.ReadSubtree())
            {
                item.Read();
                while (item.Read())
                {
                    if (item.NodeType == XmlNodeType.Element && item.GetAttribute("value") is { } value)
                        values[item.Name] = value;
                }
            }

            float Get(string name) => values.TryGetValue(name, out var text)
                && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            switch (section)
            {
                case "WaterQuads":
                    if (values.TryGetValue("IsInvisible", out var invisible) && invisible.Equals("true", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int type = (int)Get("Type");
                    quads.Add(new WaterQuad(Get("minX"), Get("maxX"), Get("minY"), Get("maxY"), Get("z"),
                        Enum.IsDefined((WaterQuadShape)type) ? (WaterQuadShape)type : WaterQuadShape.Rectangle));
                    break;
                case "CalmingQuads":
                    calming.Add(new WaterCalming(Get("minX"), Get("maxX"), Get("minY"), Get("maxY"), Math.Clamp(Get("fDampening"), 0f, 1f)));
                    break;
                default:
                    var direction = new Vector2(Get("XDirection"), Get("YDirection"));
                    direction = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
                    waves.Add(new WaterWave(Get("minX"), Get("maxX"), Get("minY"), Get("maxY"), MathF.Max(Get("Amplitude"), 0f), direction));
                    break;
            }
        }
        return (quads, calming, waves);
    }

    private static Vector4 BoundsOf(List<WaterQuad> quads) => quads.Count == 0
        ? Vector4.Zero
        : new Vector4(quads.Min(q => q.MinX), quads.Min(q => q.MinY), quads.Max(q => q.MaxX), quads.Max(q => q.MaxY));
}

/// <summary>
/// The look of GTA V's water from the game's files: colour of the water per place (waterfog images, placed by the
/// FogMin / FogMax of watertune.xml), the light pattern on the bottom (caustic) and the patterns of its waves: swell,
/// waves along the shore, ripples, foam.
/// </summary>
public static class WaterLookReader
{
    static readonly string[] GraphicsFiles = { @"update\update.rpf\x64\textures\graphics.ytd", @"x64a.rpf\textures\graphics.ytd" };
    static readonly (string Tune, string Texture)[] ColorFiles =
    {
        (@"common.rpf\data\watertune.xml", "waterfog"),
        (@"update\update.rpf\common\data\watertune_heistisland.xml", "waterfog_h4"),
    };

    /// <summary>Adds what is found to the map; a missing piece is left out (the water keeps a plain colour).
    /// ytd: reads a texture dictionary of the game by path; read: reads a file of the game by path.</summary>
    public static void Load(Func<string, CodeWalker.GameFiles.YtdFile?> ytd, Func<string, byte[]?> read, WaterMap map)
    {
        var dicts = new List<CodeWalker.GameFiles.YtdFile>();
        foreach (var p in GraphicsFiles)
        {
            try { if (ytd(p) is { } y) dicts.Add(y); }
            catch (Exception ex) { map.Errors.Add($"{p} : {ex.Message}"); }
        }
        // RGBA of a texture (CodeWalker gives BGRA)
        byte[]? Image(string name, out int w, out int h)
        {
            w = h = 0;
            uint hash = CodeWalker.GameFiles.JenkHash.GenHash(name.ToLowerInvariant());
            foreach (var y in dicts)
            {
                var t = y.TextureDict?.Lookup(hash);
                if (t == null) continue;
                try
                {
                    var px = CodeWalker.Utils.DDSIO.GetPixels(t, 0);
                    w = t.Width; h = t.Height;
                    var rgba = new byte[w * h * 4];
                    int n = Math.Min(px.Length / 4, w * h);
                    for (int i = 0; i < n; i++) { rgba[i * 4] = px[i * 4 + 2]; rgba[i * 4 + 1] = px[i * 4 + 1]; rgba[i * 4 + 2] = px[i * 4]; rgba[i * 4 + 3] = px[i * 4 + 3]; }
                    return rgba;
                }
                catch { }
            }
            return null;
        }
        foreach (var (tune, name) in ColorFiles)
        {
            byte[]? xml; try { xml = read(tune); } catch { xml = null; }
            var area = FogArea(xml);
            if (area is null || Image(name, out int w, out int h) is not { } rgba) continue;
            map.ColorMaps.Add(new WaterColorMap(w, h, rgba, area.Value));
        }
        // channels kept from each image (r, g, b, a); the ripples are a normal map with x in alpha and y in green
        WaterPattern? Pattern(string name, params int[] channels)
        {
            if (Image(name, out int w, out int h) is not { } rgba) return null;
            int count = w * h;
            var values = new byte[count * channels.Length];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < channels.Length; c++) values[i * channels.Length + c] = rgba[i * 4 + channels[c]];
            return new WaterPattern(w, h, channels.Length, values);
        }
        map.Caustics = Pattern("caustic", 0);
        map.OceanWaves = Pattern("oceanwaves", 0);
        map.ShoreWaves = Pattern("shorewaves", 0);
        map.Ripples = Pattern("waterbump", 3, 1);
        map.Foam = Pattern("waterfoam", 1);
    }

    /// <summary>Rectangle covered by the colour image (min x, min y, max x, max y); null if the file lacks it.</summary>
    public static Vector4? FogArea(byte[]? xml)
    {
        if (xml is null) return null;
        Vector2? min = null, max = null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("FogMin" or "FogMax")) continue;
                if (!float.TryParse(reader.GetAttribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(reader.GetAttribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
                if (reader.Name == "FogMin") min = new Vector2(x, y); else max = new Vector2(x, y);
            }
        }
        catch (XmlException) { return null; }
        if (min is not { } a || max is not { } b || b.X <= a.X || b.Y <= a.Y) return null;
        return new Vector4(a.X, a.Y, b.X, b.Y);
    }
}
