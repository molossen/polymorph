using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Paint;

public enum LayerKind { Fill, Paint, Folder }
public enum PaintBlend { Normal, Multiply, Overlay, Screen, Add, Darken, Lighten, ColorBurn, ColorDodge, SoftLight, HardLight, LinearLight, Difference, Subtract, Divide, Hue, Saturation, Color, Value }
public enum MaskGen { None, Painted, AmbientOcclusion, Edges, Cavity, Top, Bottom, GradientZ, Noise, Streaks, Tiles, Voronoi, Cracks, Brick, Checker, Wave, Magic, Perlin, Parts, Dirt, Speckles, Pores }
public enum PaintChannel { Color, Height, Roughness, Metal }
public enum PaintView { Final, Roughness, Metal, Height, AmbientOcclusion, Curvature, LayerMask, Emissive, Opacity }
/// <summary>What a stroke does: paint the brush values, copy another place (clone) or drag the colours along (smudge).</summary>
public enum BrushMode { Paint, Clone, Smudge }
/// <summary>Fill tool: how much of the model one click fills.</summary>
public enum FillScope { Face, Element, UvIsland, All }
public enum FilterKind { Blur, Sharpen, Levels, HueSaturation, Invert, BrightnessContrast, Gamma, Posterize, ColorRamp, DirectionalBlur, Highpass, Threshold, Clamp, Outline }
public enum PaintExportFormat { Dds, Png, Tga, Sollumz }

/// <summary>Brush settings (radius in metres, like a 3D brush in Substance).</summary>
public sealed class PaintBrush
{
    public bool Erase;
    /// <summary>Particle brush (ArmorPaint): each print may let drops run down the surface, thinner as they go (0 = off).</summary>
    public float Drips, DripLength = 0.3f;
    public BrushMode Mode;
    public float Radius = 0.08f, Strength = 0.6f, Hardness = 0.5f, Spacing = 0.15f;
    public bool Color = true, Height, Roughness = true, Metal, Emissive, Alpha;
    public Vector3 ColorValue = new(0.8f, 0.15f, 0.1f);
    public float HeightValue = 0.65f, RoughValue = 0.5f, MetalValue, EmissiveValue = 1f, AlphaValue = 1f;
    /// <summary>Skips texels facing away from the stroke, so a brush does not paint through thin walls.</summary>
    public bool BackfaceCull = true;
    /// <summary>Stamp image (a game decal, a logo...): projected square under the brush; its colours and alpha are printed.</summary>
    public RgbaImage Stamp;
    public string StampName;
    public float StampAngle;
    /// <summary>Stamp: one print per click instead of a continuous stroke.</summary>
    public bool StampOnce = true;
    /// <summary>Brush tip (alpha): the shape of the brush (scratches, splatter...), printed in the brush colour.</summary>
    public RgbaImage Tip;
    public string TipName;
    /// <summary>Random size (0..1) and rotation (degrees) for every print of the tip.</summary>
    public float SizeJitter, AngleJitter;
    /// <summary>Strokes paint the mask of the selected layer (white shows it, the eraser hides it) instead of its content.</summary>
    public bool MaskMode;
    /// <summary>Clone: source = painted point + offset (object space), set by Ctrl+click on the source.</summary>
    public Vector3 CloneOffset;
    public bool CloneSourceSet;
    /// <summary>Mirror strokes across the object's axes (object space, through the pivot).</summary>
    public bool SymX, SymY, SymZ;
    /// <summary>Lazy mouse: the brush trails behind the cursor by this distance (metres), for clean lines.</summary>
    public float Lazy;
    /// <summary>Scatter: each print lands at a random spot inside this fraction of the radius.</summary>
    public float Scatter;
    /// <summary>Radial symmetry: copies of every print around the object's vertical axis (1 = off).</summary>
    public int Radial = 1;
}

/// <summary>A filter on a layer: on its mask (blur, sharpen, levels, invert) or on its colour (levels, hue / saturation, invert).</summary>
public sealed class PaintFilter
{
    public FilterKind Kind;
    public bool OnMask = true;
    public bool Enabled = true;
    public float Radius = 2f, Amount = 1f;
    public float Black, White = 1f, Gamma = 1f;
    public float Hue, Saturation = 1f, Brightness;
    public float Contrast; public int Steps = 4;
    /// <summary>Directional blur angle in the texture (degrees).</summary>
    public float Angle;
    /// <summary>Colour ramp: dark values become A, bright values B (on a mask: the ramp of A..B greys).</summary>
    public Vector3 RampA = new(0.1f, 0.06f, 0.03f), RampB = new(0.9f, 0.8f, 0.6f);
    public PaintFilter Copy() => (PaintFilter)MemberwiseClone();
    [JsonIgnore] public string Label => Kind switch
    {
        FilterKind.Blur => $"Blur {Radius:0} px", FilterKind.Sharpen => $"Sharpen {Amount:0.0}", FilterKind.Levels => "Levels",
        FilterKind.HueSaturation => "Hue / saturation", FilterKind.BrightnessContrast => "Brightness / contrast", FilterKind.Gamma => $"Gamma {Gamma:0.00}",
        FilterKind.Posterize => $"Posterize {Steps}", FilterKind.ColorRamp => "Colour ramp",
        FilterKind.DirectionalBlur => $"Directional blur {Radius:0} px", FilterKind.Highpass => $"Highpass {Radius:0} px", FilterKind.Threshold => $"Threshold {Black:0.00}",
        FilterKind.Clamp => "Clamp", FilterKind.Outline => $"Outline {Radius:0} px", _ => "Invert",
    } + (OnMask ? " (mask)" : " (colour)");
}

/// <summary>A layer of a texture set: a fill (uniform material, optionally masked), painted strokes, or a folder of layers.</summary>
public sealed class PaintLayer
{
    public int Id;
    public string Name = "Layer";
    public LayerKind Kind;
    /// <summary>Id of the folder this layer is in (-1 = top level).</summary>
    public int Folder = -1;
    public bool Collapsed;
    public bool Visible = true;
    public float Opacity = 1f;
    public PaintBlend Blend;
    public bool UseColor = true, UseHeight, UseRough = true, UseMetal, UseEmissive, UseAlpha;

    // fill values
    public Vector3 Color = new(0.5f, 0.5f, 0.5f);
    public float Height = 0.5f, Rough = 0.6f, Metal, Emissive, Alpha = 1f;
    /// <summary>Procedural variation of the fill: colour and height noise.</summary>
    public float ColorNoise, HeightNoise, NoiseScale = 8f;
    /// <summary>Procedural pattern of the fill (ArmorPaint textures): Color where the pattern is 0, PatternColor where it is 1.</summary>
    public FillPattern Pattern;
    public Vector3 PatternColor = new(0.15f, 0.12f, 0.1f);
    public float PatternScale = 4f, PatternRelief, PatternRough = -1f;
    /// <summary>Warp (ArmorPaint warp node): bends the pattern and the mask with noise; amount in metres.</summary>
    public float Warp, WarpScale = 2f;
    /// <summary>Node material (ArmorPaint style): when set, the fill's colour / roughness / relief / metal come from it.</summary>
    public NodeGraph Graph;

    // texture fill (Molo library material or any image), projected triplanar in object space
    public string TexDiffuse, TexSpec, TexHeight;
    public float TileMetres = 2f, TexHeightAmount = 0.35f;
    [JsonIgnore] internal Vector3[] texColor; [JsonIgnore] internal float[] texRough, texHeight; [JsonIgnore] internal bool texDirty = true;
    [JsonIgnore] public bool HasTextures => TexDiffuse != null || TexSpec != null || TexHeight != null;
    public void TexturesChanged() => texDirty = true;

    // mask
    public MaskGen Mask;
    public float MaskThreshold = 0.5f, MaskContrast = 4f, MaskBreakup, MaskNoiseScale = 12f;
    public bool MaskInvert;
    public int Seed;
    /// <summary>Mask "picked parts" (ArmorPaint's colour-ID mask, without an ID map to make): the parts clicked on the model,
    /// as faces, connected elements or UV islands.</summary>
    public List<int> MaskParts = new();
    public FillScope MaskPartScope = FillScope.Element;
    public List<PaintFilter> Filters = new();

    // paint buffers (compact texels of the TexelMap); null until painted
    [JsonIgnore] public float[] Cover; [JsonIgnore] public Vector3[] PColor; [JsonIgnore] public float[] PHeight, PRough, PMetal, PEmis, PAlpha;
    /// <summary>Mask of a "where I paint" layer (0 = hidden).</summary>
    [JsonIgnore] public float[] PaintedMask;
    /// <summary>Painted mask on top of any other mask (multiplier, 1 = unchanged); null until painted.</summary>
    [JsonIgnore] public float[] MaskPaint;

    [JsonIgnore] internal float[] maskCache; [JsonIgnore] internal bool maskDirty = true;
    public void MaskChanged() => maskDirty = true;
    [JsonIgnore] public bool IsFolder => Kind == LayerKind.Folder;

    public PaintLayer CloneLayer()
    {
        var c = (PaintLayer)MemberwiseClone();
        c.Cover = (float[])Cover?.Clone(); c.PColor = (Vector3[])PColor?.Clone();
        c.PHeight = (float[])PHeight?.Clone(); c.PRough = (float[])PRough?.Clone(); c.PMetal = (float[])PMetal?.Clone();
        c.PEmis = (float[])PEmis?.Clone(); c.PAlpha = (float[])PAlpha?.Clone();
        c.PaintedMask = (float[])PaintedMask?.Clone(); c.MaskPaint = (float[])MaskPaint?.Clone(); c.maskCache = null; c.maskDirty = true;
        c.texColor = null; c.texRough = null; c.texHeight = null; c.texDirty = true;
        c.Filters = Filters.Select(f => f.Copy()).ToList();
        c.MaskParts = new List<int>(MaskParts ?? new());
        c.Graph = Graph?.Copy();
        return c;
    }

    /// <summary>Settings only (no painted pixels), for smart materials saved as files.</summary>
    public string SettingsJson() => JsonSerializer.Serialize(this, PaintJson.Options);
    public static PaintLayer FromSettingsJson(string json) => JsonSerializer.Deserialize<PaintLayer>(json, PaintJson.Options);
}


public static class PaintJson
{
    public static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A paintable texture set (Substance Painter-like): layers composited into colour, height, roughness, metal,
/// emissive and opacity, turned into GTA diffuse / normal / specular textures. One per material of an object.
/// </summary>
public sealed class TextureSet
{
    public int Size = 1024;
    /// <summary>Name of the set (the material it paints) and the paint material id its faces use.</summary>
    public string Name = "";
    public int MatId = -1;
    /// <summary>A second material of the same set (the GTA emissive material made from its emissive faces).</summary>
    public int AltMatId = -1;
    public readonly List<PaintLayer> Layers = new();
    public TexelMap Map;
    public PaintView View = PaintView.Final;
    public int SelectedLayer;
    public float NormalStrength = 4f;
    public int NextId = 1;

    // export
    public int ExportSize = 1024;
    public PaintExportFormat ExportFormat = PaintExportFormat.Dds;
    /// <summary>GTA shader used when the set has transparency: "normal_spec_decal" (blended) or "cutout_hard".</summary>
    public string AlphaShader = "normal_spec_decal";

    /// <summary>Normal map baked from a high-poly (tangent space per texel), combined with the painted relief.</summary>
    public Vector3[] BakedNormal;
    public bool HasBakedMaps;

    // composite (compact) and preview images
    public Vector3[] CColor; public float[] CHeight, CRough, CMetal, CEmis, CAlpha;
    public RgbaImage ColorImage, NormalImage, SpecImage, EmissiveImage;
    /// <summary>Pixel rectangle changed since the last upload (x0, y0, x1, y1), or null.</summary>
    public (int X0, int Y0, int X1, int Y1)? Dirty;
    public int Version;
    int[] folderOf = Array.Empty<int>();

    public static TextureSet Create(PolyMesh mesh, int size, int aoRays = 48, int matId = -1, string name = "")
    {
        var ts = new TextureSet { Size = size, MatId = matId, Name = name };
        ts.Map = TexelMap.Build(mesh, size, aoRays, ts.FaceFilter(mesh));
        ts.AddLayerRaw(new PaintLayer { Name = "Base", Kind = LayerKind.Fill, Color = new Vector3(0.55f), Rough = 0.7f, UseRough = true });
        ts.Allocate();
        ts.CompositeAll();
        return ts;
    }

    /// <summary>Faces painted by this set (all of them when it has no material).</summary>
    public Func<int, bool> FaceFilter(PolyMesh mesh) => MatId < 0 ? null : f => mesh.Faces[f].MatId == MatId || (AltMatId >= 0 && mesh.Faces[f].MatId == AltMatId);

    public void AddLayerRaw(PaintLayer l) { l.Id = NextId++; Layers.Add(l); }
    public PaintLayer ById(int id) => id < 0 ? null : Layers.FirstOrDefault(x => x.Id == id);

    public void Rebuild(PolyMesh mesh, int aoRays = 48)
    {
        var px = Layers.Select(l => ToPixels(l)).ToList();
        var bn = BakedNormal != null ? PixelsOf(BakedNormal) : null;
        Map = TexelMap.Build(mesh, Size, aoRays, FaceFilter(mesh));
        for (int i = 0; i < Layers.Count; i++) FromPixels(Layers[i], px[i]);
        if (bn != null) BakedNormal = TexelsOf(bn);
        Allocate();
        CompositeAll();
    }

    /// <summary>New working resolution: painted pixels are resampled (nearest), masks and maps rebuilt.</summary>
    public void Resize(PolyMesh mesh, int newSize, int aoRays = 48)
    {
        if (newSize == Size) return;
        int old = Size;
        var px = Layers.Select(l => ToPixels(l)).ToList();
        var bn = BakedNormal != null ? PixelsOf(BakedNormal) : null;
        byte[] R(byte[] a, int ch)
        {
            if (a == null) return null;
            var r = new byte[newSize * newSize * ch];
            for (int y = 0; y < newSize; y++) { int sy = y * old / newSize; for (int x = 0; x < newSize; x++) { int sx = x * old / newSize; for (int c = 0; c < ch; c++) r[(x + y * newSize) * ch + c] = a[(sx + sy * old) * ch + c]; } }
            return r;
        }
        ushort[] RH(ushort[] a)
        {
            if (a == null) return null;
            var r = new ushort[newSize * newSize];
            for (int y = 0; y < newSize; y++) for (int x = 0; x < newSize; x++) r[x + y * newSize] = a[x * old / newSize + y * old / newSize * old];
            return r;
        }
        Size = newSize;
        Map = TexelMap.Build(mesh, Size, aoRays, FaceFilter(mesh));
        for (int i = 0; i < Layers.Count; i++)
        {
            var p = px[i];
            FromPixels(Layers[i], new PixelLayer { Cover = R(p.Cover, 1), Col = R(p.Col, 3), Rough = R(p.Rough, 1), Metal = R(p.Metal, 1), Mask = R(p.Mask, 1), MaskPaint = R(p.MaskPaint, 1), Emis = R(p.Emis, 1), Alpha = R(p.Alpha, 1), Height = RH(p.Height) });
        }
        if (bn != null) BakedNormal = TexelsOf(R(bn, 3));
        foreach (var l in Layers) l.texDirty = true;
        Allocate();
        CompositeAll();
    }

    void Allocate()
    {
        int n = Map.Count;
        CColor = new Vector3[n]; CHeight = new float[n]; CRough = new float[n]; CMetal = new float[n]; CEmis = new float[n]; CAlpha = new float[n];
        ColorImage = new RgbaImage(Size, Size); NormalImage = new RgbaImage(Size, Size); SpecImage = new RgbaImage(Size, Size); EmissiveImage = new RgbaImage(Size, Size);
        foreach (var l in Layers) l.maskDirty = true;
    }

    public PaintLayer Current => Layers.Count == 0 ? null : Layers[Math.Clamp(SelectedLayer, 0, Layers.Count - 1)];

    /// <summary>Folder settings apply to every layer inside (visibility, opacity, mask).</summary>
    void PrepareFolders()
    {
        if (folderOf.Length != Layers.Count) folderOf = new int[Layers.Count];
        for (int i = 0; i < Layers.Count; i++)
        {
            var l = Layers[i];
            folderOf[i] = -1;
            if (l.Folder < 0 || l.IsFolder) continue;
            for (int k = 0; k < Layers.Count; k++) if (Layers[k].Id == l.Folder && Layers[k].IsFolder) { folderOf[i] = k; break; }
        }
    }

    public bool HasEmissive => CEmis != null && CEmis.Any(e => e > 0.05f);
    public bool HasAlpha => CAlpha != null && CAlpha.Any(a => a < 0.98f);

    // ------------------------------------------------------------------------------ masks

    public float[] MaskOf(PaintLayer l)
    {
        int n = Map.Count;
        if (!l.maskDirty && l.maskCache != null && l.maskCache.Length == n) return l.maskCache;
        var m = l.maskCache != null && l.maskCache.Length == n ? l.maskCache : new float[n];
        var (zmin, zmax) = (Map.Min.Z, Map.Max.Z);
        float zr = MathF.Max(zmax - zmin, 1e-5f);
        var parts = l.Mask == MaskGen.Parts && l.MaskParts is { Count: > 0 } ? new HashSet<int>(l.MaskParts) : null;
        Parallel.For(0, n, t =>
        {
            var wp = l.Warp > 0 ? ProcTex.Warp(Map.Pos[t], l.Warp, l.WarpScale, l.Seed) : Map.Pos[t];
            float g = l.Mask switch
            {
                MaskGen.None => 1f,
                MaskGen.Painted => l.PaintedMask?[t] ?? 0f,
                MaskGen.AmbientOcclusion => 1 - Map.Ao[t],
                MaskGen.Edges => MathF.Max(0, Map.Curv[t]),
                MaskGen.Cavity => MathF.Max(0, -Map.Curv[t]),
                MaskGen.Top => Map.Nrm[t].Z * 0.5f + 0.5f,
                MaskGen.Bottom => 0.5f - Map.Nrm[t].Z * 0.5f,
                MaskGen.GradientZ => (Map.Pos[t].Z - zmin) / zr,
                MaskGen.Noise => Noise.Fbm(wp * l.MaskNoiseScale, l.Seed),
                // leaks: noise stretched along Z, stronger under the top of the model
                MaskGen.Streaks => Noise.Fbm(new Vector3(Map.Pos[t].X * l.MaskNoiseScale, Map.Pos[t].Y * l.MaskNoiseScale, Map.Pos[t].Z * l.MaskNoiseScale * 0.06f), l.Seed) * (0.4f + 0.6f * (Map.Pos[t].Z - zmin) / zr),
                // tile grout lines (scale = tiles per metre), projected on the dominant axis
                MaskGen.Tiles => Grout(wp, Map.Nrm[t], l.MaskNoiseScale),
                MaskGen.Voronoi => ProcTex.Voronoi(wp, l.MaskNoiseScale, 1, l.Seed).Cell,
                MaskGen.Cracks => 1 - MathF.Min(1, ProcTex.Voronoi(wp, l.MaskNoiseScale, 1, l.Seed).Edge / 0.08f),
                MaskGen.Brick => ProcTex.Brick(ProcTex.PlaneUv(wp, Map.Nrm[t]), l.MaskNoiseScale).Mortar,
                MaskGen.Checker => ProcTex.Checker(wp, l.MaskNoiseScale),
                MaskGen.Wave => ProcTex.Wave(wp, l.MaskNoiseScale * 0.05f, false, 2, 1, 3),
                MaskGen.Magic => ProcTex.Magic(wp, l.MaskNoiseScale * 0.2f),
                MaskGen.Perlin => ProcTex.Perlin(wp, l.MaskNoiseScale, 6, 0.5f, 0.3f, l.Seed),
                MaskGen.Parts => parts != null && parts.Contains(PartId(t, l.MaskPartScope)) ? 1f : 0f,
                // grime blotches broken up by fine specks
                MaskGen.Dirt => Smooth((Noise.Fbm(wp * l.MaskNoiseScale, l.Seed) * 0.8f + Noise.Fbm(wp * l.MaskNoiseScale * 5, l.Seed ^ 0x2f1) * 0.35f - 0.5f) / 0.22f),
                // small random dots
                MaskGen.Speckles => Smooth((Noise.Value(wp * l.MaskNoiseScale * 24, l.Seed) - 0.62f) / 0.12f),
                // round pits (cells' centres)
                MaskGen.Pores => 1 - Smooth((ProcTex.Voronoi(wp, l.MaskNoiseScale * 3, 1, l.Seed).F1 - 0.08f) / 0.12f),
                _ => 1f,
            };
            if (l.Mask is not (MaskGen.None or MaskGen.Painted or MaskGen.Parts))
            {
                if (l.MaskBreakup > 0) g += (Noise.Fbm(Map.Pos[t] * l.MaskNoiseScale, l.Seed + 7) - 0.5f) * l.MaskBreakup * 2;
                g = Math.Clamp((g - l.MaskThreshold) * l.MaskContrast + 0.5f, 0, 1);
            }
            if (l.MaskInvert) g = 1 - g;
            if (l.MaskPaint != null) g *= l.MaskPaint[t];
            m[t] = g;
        });
        foreach (var f in l.Filters) if (f.Enabled && f.OnMask) ApplyMaskFilter(f, m);
        l.maskCache = m; l.maskDirty = false;
        return m;
    }

    /// <summary>The part a texel belongs to: its face, connected element or UV island.</summary>
    public int PartId(int t, FillScope scope) => scope switch { FillScope.Face => Map.FaceOf[t], FillScope.UvIsland => Map.IslandOf[t], _ => Map.ElementOf[t] };

    /// <summary>The part of a face (-1: the face is not in this texture set).</summary>
    public int PartOfFace(int face, FillScope scope)
    {
        if (scope == FillScope.Face) return Array.IndexOf(Map.FaceOf, face) >= 0 ? face : -1;
        int t = Array.IndexOf(Map.FaceOf, face);
        return t < 0 ? -1 : PartId(t, scope);
    }

    /// <summary>Adds the part under a face to the layer's picked-parts mask, or takes it out when it is in.</summary>
    public bool TogglePart(PaintLayer l, int face)
    {
        int id = PartOfFace(face, l.MaskPartScope);
        if (id < 0) return false;
        l.MaskParts ??= new();
        if (!l.MaskParts.Remove(id)) l.MaskParts.Add(id);
        l.Mask = MaskGen.Parts; l.MaskChanged();
        return true;
    }

    static float Grout(Vector3 p, Vector3 n, float perMetre)
    {
        var a = new Vector3(MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));
        Vector2 uv = a.Z >= a.X && a.Z >= a.Y ? new(p.X, p.Y) : a.X >= a.Y ? new(p.Y, p.Z) : new(p.X, p.Z);
        uv *= MathF.Max(perMetre, 0.01f);
        float fx = uv.X - MathF.Floor(uv.X), fy = uv.Y - MathF.Floor(uv.Y);
        float d = MathF.Min(MathF.Min(fx, 1 - fx), MathF.Min(fy, 1 - fy));
        return 1 - Math.Clamp(d / 0.06f, 0, 1); // 1 on the grout line
    }

    // ------------------------------------------------------------------------------ filters

    void ApplyMaskFilter(PaintFilter f, float[] m)
    {
        switch (f.Kind)
        {
            case FilterKind.Blur: { var b = BlurTexels(m, Math.Max(1, (int)MathF.Round(f.Radius))); Array.Copy(b, m, m.Length); break; }
            case FilterKind.Sharpen: { var b = BlurTexels(m, Math.Max(1, (int)MathF.Round(f.Radius))); for (int t = 0; t < m.Length; t++) m[t] = Math.Clamp(m[t] + (m[t] - b[t]) * f.Amount, 0, 1); break; }
            case FilterKind.Levels: for (int t = 0; t < m.Length; t++) m[t] = Levels(m[t], f); break;
            case FilterKind.Invert: for (int t = 0; t < m.Length; t++) m[t] = 1 - m[t]; break;
            case FilterKind.BrightnessContrast: for (int t = 0; t < m.Length; t++) m[t] = Math.Clamp(BrightContrast(m[t], f), 0, 1); break;
            case FilterKind.Gamma: for (int t = 0; t < m.Length; t++) m[t] = MathF.Pow(Math.Clamp(m[t], 0, 1), MathF.Max(f.Gamma, 0.01f)); break;
            case FilterKind.Posterize: for (int t = 0; t < m.Length; t++) m[t] = Posterize(m[t], f.Steps); break;
            case FilterKind.DirectionalBlur: { var b = DirectionalBlurTexels(m, Math.Max(1, (int)MathF.Round(f.Radius)), f.Angle); Array.Copy(b, m, m.Length); break; }
            case FilterKind.Highpass: { var b = BlurTexels(m, Math.Max(1, (int)MathF.Round(f.Radius))); for (int t = 0; t < m.Length; t++) m[t] = Math.Clamp(0.5f + (m[t] - b[t]) * f.Amount, 0, 1); break; }
            case FilterKind.Threshold: for (int t = 0; t < m.Length; t++) m[t] = m[t] >= f.Black ? 1 : 0; break;
            case FilterKind.Clamp: for (int t = 0; t < m.Length; t++) m[t] = Math.Clamp(m[t], f.Black, MathF.Max(f.Black, f.White)); break;
            case FilterKind.Outline:
                {
                    // the border of the mask: a band about Radius px wide around the 0.5 line
                    var bin = new float[m.Length]; for (int t = 0; t < m.Length; t++) bin[t] = m[t] >= 0.5f ? 1 : 0;
                    var b = BlurTexels(bin, Math.Max(1, (int)MathF.Round(f.Radius * 0.6f)));
                    for (int t = 0; t < m.Length; t++) m[t] = Math.Clamp(1 - MathF.Abs(b[t] * 2 - 1) * 1.6f, 0, 1);
                    break;
                }
            case FilterKind.ColorRamp:
                {
                    float a = Luma(f.RampA), b = Luma(f.RampB);
                    for (int t = 0; t < m.Length; t++) m[t] = a + (b - a) * m[t];
                    break;
                }
        }
    }

    /// <summary>ArmorPaint brightness / contrast: a = 1 + contrast, b = brightness - contrast / 2.</summary>
    static float BrightContrast(float v, PaintFilter f)
    {
        float a = 1 + f.Contrast, b = f.Brightness - f.Contrast * 0.5f;
        return MathF.Max(a * v + b, 0);
    }
    static float Posterize(float v, int steps) { steps = Math.Max(steps, 2); return MathF.Round(Math.Clamp(v, 0, 1) * (steps - 1)) / (steps - 1); }
    static float Luma(Vector3 c) => c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;

    static float Levels(float v, PaintFilter f)
    {
        v = Math.Clamp((v - f.Black) / MathF.Max(f.White - f.Black, 1e-4f), 0, 1);
        return MathF.Pow(v, 1 / MathF.Max(f.Gamma, 0.01f));
    }

    static Vector3 ColourFilters(Vector3 c, List<PaintFilter> filters)
    {
        foreach (var f in filters)
        {
            if (!f.Enabled || f.OnMask) continue;
            switch (f.Kind)
            {
                case FilterKind.Levels: c = new Vector3(Levels(c.X, f), Levels(c.Y, f), Levels(c.Z, f)); break;
                case FilterKind.Invert: c = Vector3.One - c; break;
                case FilterKind.BrightnessContrast: c = Vector3.Clamp(new Vector3(BrightContrast(c.X, f), BrightContrast(c.Y, f), BrightContrast(c.Z, f)), Vector3.Zero, Vector3.One); break;
                case FilterKind.Gamma: { float g = MathF.Max(f.Gamma, 0.01f); c = new Vector3(MathF.Pow(MathF.Max(c.X, 0), g), MathF.Pow(MathF.Max(c.Y, 0), g), MathF.Pow(MathF.Max(c.Z, 0), g)); break; }
                case FilterKind.Posterize: c = new Vector3(Posterize(c.X, f.Steps), Posterize(c.Y, f.Steps), Posterize(c.Z, f.Steps)); break;
                case FilterKind.ColorRamp: c = Vector3.Lerp(f.RampA, f.RampB, Math.Clamp(Luma(c), 0, 1)); break;
                case FilterKind.Threshold: c = Luma(c) >= f.Black ? Vector3.One : Vector3.Zero; break;
                case FilterKind.Clamp: c = Vector3.Clamp(c, new Vector3(f.Black), new Vector3(MathF.Max(f.Black, f.White))); break;
                case FilterKind.HueSaturation:
                    {
                        var (h, s, v) = ToHsv(c);
                        h = (h + f.Hue / 360f + 1) % 1; s = Math.Clamp(s * f.Saturation, 0, 1); v = Math.Clamp(v + f.Brightness, 0, 1);
                        c = FromHsv(h, s, v); break;
                    }
            }
        }
        return c;
    }

    public static (float H, float S, float V) ToHsv(Vector3 c)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z)), d = mx - mn;
        float h = 0;
        if (d > 1e-6f) h = mx == c.X ? ((c.Y - c.Z) / d + 6) % 6 : mx == c.Y ? (c.Z - c.X) / d + 2 : (c.X - c.Y) / d + 4;
        return (h / 6, mx > 0 ? d / mx : 0, mx);
    }

    public static Vector3 FromHsv(float h, float s, float v)
    {
        float i = MathF.Floor(h * 6), f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return ((int)i % 6) switch { 0 => new(v, t, p), 1 => new(q, v, p), 2 => new(p, v, t), 3 => new(p, q, v), 4 => new(t, p, v), _ => new(v, p, q) };
    }

    /// <summary>Box blur (two passes) in texture space: texels go to the image, blurred, and read back.</summary>
    public float[] BlurTexels(float[] v, int radius)
    {
        int S = Size; var img = new float[S * S];
        Parallel.For(0, S, y =>
        {
            for (int x = 0; x < S; x++) { int id = x + y * S; int t = Map.Index[id]; if (t < 0) t = Map.Dilate[id]; img[id] = t < 0 ? 0 : v[t]; }
        });
        for (int pass = 0; pass < 2; pass++) { BoxH(img, S, radius); BoxV(img, S, radius); }
        var res = new float[v.Length];
        for (int t = 0; t < res.Length; t++) res[t] = img[Map.Pixel[t]];
        return res;
    }

    /// <summary>Blur along one direction of the texture (scratches, rain, brushed metal), in texture space like <see cref="BlurTexels"/>.</summary>
    public float[] DirectionalBlurTexels(float[] v, int radius, float angleDeg)
    {
        int S = Size; var img = new float[S * S];
        Parallel.For(0, S, y =>
        {
            for (int x = 0; x < S; x++) { int id = x + y * S; int t = Map.Index[id]; if (t < 0) t = Map.Dilate[id]; img[id] = t < 0 ? 0 : v[t]; }
        });
        float a = angleDeg * MathF.PI / 180f, dx = MathF.Cos(a), dy = MathF.Sin(a);
        var res = new float[v.Length];
        Parallel.For(0, v.Length, t =>
        {
            int px = Map.Pixel[t] % S, py = Map.Pixel[t] / S; float acc = 0; int n = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int x = Math.Clamp((int)MathF.Round(px + dx * k), 0, S - 1), y = Math.Clamp((int)MathF.Round(py + dy * k), 0, S - 1);
                acc += img[x + y * S]; n++;
            }
            res[t] = acc / n;
        });
        return res;
    }

    static void BoxH(float[] img, int S, int r)
    {
        Parallel.For(0, S, y =>
        {
            var row = new float[S]; int o = y * S; float acc = 0; int w = 2 * r + 1;
            for (int x = -r; x <= r; x++) acc += img[o + Math.Clamp(x, 0, S - 1)];
            for (int x = 0; x < S; x++) { row[x] = acc / w; acc += img[o + Math.Min(S - 1, x + r + 1)] - img[o + Math.Max(0, x - r)]; }
            Array.Copy(row, 0, img, o, S);
        });
    }

    static void BoxV(float[] img, int S, int r)
    {
        Parallel.For(0, S, x =>
        {
            var col = new float[S]; float acc = 0; int w = 2 * r + 1;
            for (int y = -r; y <= r; y++) acc += img[x + Math.Clamp(y, 0, S - 1) * S];
            for (int y = 0; y < S; y++) { col[y] = acc / w; acc += img[x + Math.Min(S - 1, y + r + 1) * S] - img[x + Math.Max(0, y - r) * S]; }
            for (int y = 0; y < S; y++) img[x + y * S] = col[y];
        });
    }

    // ------------------------------------------------------------------------------ composite

    /// <summary>Samples a layer's fill textures at every texel (triplanar, tile size in metres).</summary>
    public void PrepareTextures(PaintLayer l)
    {
        if (!l.HasTextures) { l.texColor = null; l.texRough = null; l.texHeight = null; l.texDirty = false; return; }
        if (!l.texDirty && l.texColor != null && l.texColor.Length == Map.Count) return;
        var dif = Gta.Library.TextureReader.Load(l.TexDiffuse); var spec = Gta.Library.TextureReader.Load(l.TexSpec); var hgt = Gta.Library.TextureReader.Load(l.TexHeight);
        int n = Map.Count;
        l.texColor = new Vector3[n]; l.texRough = new float[n]; l.texHeight = new float[n];
        float inv = 1f / MathF.Max(l.TileMetres, 0.01f);
        float avg = 0.5f;
        if (dif != null) { double s = 0; for (int i = 0; i < dif.Pixels.Length; i += 16) s += (dif.Pixels[i] * 0.3 + dif.Pixels[i + 1] * 0.59 + dif.Pixels[i + 2] * 0.11) / 255.0; avg = (float)(s / (dif.Pixels.Length / 16.0)); }
        Parallel.For(0, n, t =>
        {
            var p = Map.Pos[t] * inv; var nr = Map.Nrm[t];
            var w = new Vector3(MathF.Pow(MathF.Abs(nr.X), 4), MathF.Pow(MathF.Abs(nr.Y), 4), MathF.Pow(MathF.Abs(nr.Z), 4));
            w /= MathF.Max(w.X + w.Y + w.Z, 1e-6f);
            Vector4 Tri(RgbaImage img)
            {
                if (img == null) return Vector4.One;
                var r = Vector4.Zero;
                if (w.X > 0.01f) r += Gta.Library.TextureReader.Sample(img, p.Y * MathF.Sign(nr.X == 0 ? 1 : nr.X), -p.Z) * w.X;
                if (w.Y > 0.01f) r += Gta.Library.TextureReader.Sample(img, -p.X * MathF.Sign(nr.Y == 0 ? 1 : nr.Y), -p.Z) * w.Y;
                if (w.Z > 0.01f) r += Gta.Library.TextureReader.Sample(img, p.X, -p.Y * MathF.Sign(nr.Z == 0 ? 1 : nr.Z)) * w.Z;
                return r;
            }
            var c = Tri(dif);
            l.texColor[t] = dif != null ? new Vector3(c.X, c.Y, c.Z) : Vector3.One;
            if (spec != null) { var s = Tri(spec); l.texRough[t] = Math.Clamp(1 - MathF.Sqrt(Math.Clamp(s.X, 0, 1)) * 0.85f, 0.05f, 1); }
            else l.texRough[t] = l.Rough;
            if (hgt != null) l.texHeight[t] = Math.Clamp(0.5f + (Tri(hgt).X - 0.5f) * l.TexHeightAmount * 2, 0, 1);
            else if (dif != null) l.texHeight[t] = Math.Clamp(0.5f + ((c.X * 0.3f + c.Y * 0.59f + c.Z * 0.11f) - avg) * l.TexHeightAmount, 0, 1);
            else l.texHeight[t] = l.Height;
        });
        l.texDirty = false;
    }

    public void CompositeAll()
    {
        if (Map == null) return;
        PrepareFolders();
        foreach (var l in Layers) { MaskOf(l); if (l.Kind == LayerKind.Fill) PrepareTextures(l); }
        Parallel.For(0, Map.Count, CompositeTexel);
        Dirty = (0, 0, Size - 1, Size - 1);
        WriteImagesParallel();
        Version++;
    }

    void CompositeTexel(int t)
    {
        var col = new Vector3(0.5f); float h = 0.5f, r = 0.7f, me = 0, em = 0, al = 1;
        for (int i = 0; i < Layers.Count; i++)
        {
            var l = Layers[i];
            if (l.IsFolder || !l.Visible || l.Opacity <= 0) continue;
            float a = l.Opacity * (l.maskCache?[t] ?? 1f);
            int fi = i < folderOf.Length ? folderOf[i] : -1;
            if (fi >= 0) { var f = Layers[fi]; if (!f.Visible) continue; a *= f.Opacity * (f.maskCache?[t] ?? 1f); }
            if (l.Kind == LayerKind.Paint) a *= l.Cover?[t] ?? 0f;
            if (a <= 0) continue;
            Vector3 lc; float lh, lr, lm, le, la;
            if (l.Kind == LayerKind.Fill)
            {
                lc = l.Color; lh = l.Height; lr = l.Rough; lm = l.Metal; le = l.Emissive; la = l.Alpha;
                if (l.texColor != null) { lc = l.texColor[t] * l.Color; lr = l.texRough[t]; lh = l.texHeight[t]; }
                if (l.Graph != null && l.Graph.Evaluate(this, t) is { } gv) { lc = gv.Color; lr = gv.Rough; lh = gv.Height; lm = gv.Metal; }
                if (l.Pattern != FillPattern.None)
                {
                    var wp = l.Warp > 0 ? ProcTex.Warp(Map.Pos[t], l.Warp, l.WarpScale, l.Seed) : Map.Pos[t];
                    float pv;
                    if (l.Pattern == FillPattern.Brick)
                    {
                        var (mortar, tint) = ProcTex.Brick(ProcTex.PlaneUv(wp, Map.Nrm[t]), l.PatternScale);
                        lc *= 0.8f + 0.4f * tint; // each brick a bit lighter or darker
                        pv = mortar;
                    }
                    else pv = ProcTex.Pattern(l.Pattern, wp, Map.Nrm[t], l.PatternScale, l.Seed);
                    lc = Vector3.Lerp(lc, l.PatternColor, pv);
                    if (l.PatternRelief != 0) lh = Math.Clamp(lh - (pv - 0.5f) * l.PatternRelief, 0, 1);
                    if (l.PatternRough >= 0) lr = lr + (l.PatternRough - lr) * pv;
                }
                if (l.ColorNoise > 0 || l.HeightNoise > 0)
                {
                    float nz = Noise.Fbm(Map.Pos[t] * l.NoiseScale, l.Seed + 3) - 0.5f;
                    lc = Vector3.Clamp(lc * (1 + nz * l.ColorNoise * 2), Vector3.Zero, Vector3.One);
                    lh = Math.Clamp(lh + nz * l.HeightNoise, 0, 1);
                }
            }
            else
            {
                lc = l.PColor?[t] ?? l.Color; lh = l.PHeight?[t] ?? l.Height; lr = l.PRough?[t] ?? l.Rough; lm = l.PMetal?[t] ?? l.Metal;
                le = l.PEmis?[t] ?? l.Emissive; la = l.PAlpha?[t] ?? l.Alpha;
            }
            if (l.Filters.Count > 0) lc = ColourFilters(lc, l.Filters);
            if (l.UseColor) col = Blend(col, lc, a, l.Blend);
            if (l.UseHeight) h = Blend1(h, lh, a, l.Blend);
            if (l.UseRough) r = Blend1(r, lr, a, l.Blend);
            if (l.UseMetal) me = Blend1(me, lm, a, l.Blend);
            if (l.UseEmissive) em = Blend1(em, le, a, l.Blend);
            if (l.UseAlpha) al = Blend1(al, la, a, PaintBlend.Normal);
        }
        CColor[t] = col; CHeight[t] = h; CRough[t] = r; CMetal[t] = me; CEmis[t] = em; CAlpha[t] = al;
    }

    /// <summary>A layer blend (for the node material's Mix node).</summary>
    public static Vector3 BlendPublic(Vector3 b, Vector3 v, float a, PaintBlend mode) => Vector3.Clamp(Blend(b, v, a, mode), Vector3.Zero, Vector3.One);

    static Vector3 Blend(Vector3 b, Vector3 v, float a, PaintBlend mode) => mode switch
    {
        PaintBlend.Multiply => Vector3.Lerp(b, b * v, a),
        PaintBlend.Screen => Vector3.Lerp(b, Vector3.One - (Vector3.One - b) * (Vector3.One - v), a),
        PaintBlend.Add => Vector3.Min(Vector3.One, b + v * a),
        PaintBlend.Overlay => Vector3.Lerp(b, new Vector3(Ov(b.X, v.X), Ov(b.Y, v.Y), Ov(b.Z, v.Z)), a),
        PaintBlend.Normal => Vector3.Lerp(b, v, a),
        PaintBlend.Hue or PaintBlend.Saturation or PaintBlend.Color or PaintBlend.Value => Vector3.Lerp(b, HsvMix(b, v, mode), a),
        _ => new Vector3(Blend1(b.X, v.X, a, mode), Blend1(b.Y, v.Y, a, mode), Blend1(b.Z, v.Z, a, mode)),
    };
    /// <summary>Hue / Saturation / Color / Value blends (ArmorPaint): part of the layer's HSV over the colour below.</summary>
    static Vector3 HsvMix(Vector3 b, Vector3 v, PaintBlend mode)
    {
        var (bh, bs, bv) = ToHsv(b); var (vh, vs, vv) = ToHsv(v);
        return mode switch
        {
            PaintBlend.Hue => FromHsv(vh, bs, bv),
            PaintBlend.Saturation => FromHsv(bh, vs, bv),
            PaintBlend.Color => FromHsv(vh, vs, bv),
            _ => FromHsv(bh, bs, vv),
        };
    }
    static float Blend1(float b, float v, float a, PaintBlend mode) => mode switch
    {
        PaintBlend.Multiply => b + (b * v - b) * a,
        PaintBlend.Screen => b + ((1 - (1 - b) * (1 - v)) - b) * a,
        PaintBlend.Add => MathF.Min(1, b + v * a),
        PaintBlend.Overlay => b + (Ov(b, v) - b) * a,
        // the ArmorPaint blend modes (paint/sources/render/make_material.c), results kept in 0..1
        PaintBlend.Darken => b + (MathF.Min(b, v) - b) * a,
        PaintBlend.Lighten => MathF.Max(b, v * a),
        PaintBlend.ColorBurn => b + (Math.Clamp(1 - (1 - b) / MathF.Max(v, 1e-3f), 0, 1) - b) * a,
        PaintBlend.ColorDodge => b + (Math.Clamp(b / MathF.Max(1 - v, 1e-3f), 0, 1) - b) * a,
        PaintBlend.SoftLight => (1 - a) * b + a * ((1 - b) * v * b + b * (1 - (1 - v) * (1 - b))),
        PaintBlend.HardLight => b + (Ov(v, b) - b) * a,
        PaintBlend.LinearLight => Math.Clamp(b + a * (2 * (v - 0.5f)), 0, 1),
        PaintBlend.Difference => b + (MathF.Abs(b - v) - b) * a,
        PaintBlend.Subtract => b + (MathF.Max(0, b - v) - b) * a,
        PaintBlend.Divide => b + (Math.Clamp(b / MathF.Max(v, 1e-3f), 0, 1) - b) * a,
        PaintBlend.Hue or PaintBlend.Saturation or PaintBlend.Color => b, // colour-only modes leave the grey channels alone
        _ => b + (v - b) * a,
    };
    static float Ov(float b, float v) => b < 0.5f ? 2 * b * v : 1 - 2 * (1 - b) * (1 - v);

    /// <summary>Fills the preview images for a pixel rectangle (with island padding), in the chosen view.</summary>
    void WriteImages(int x0, int y0, int x1, int y1)
    {
        var sel = Current;
        float[] lm = View == PaintView.LayerMask && sel != null ? MaskOf(sel) : null;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int id = x + y * Size;
                int t = Map.Index[id]; if (t < 0) t = Map.Dilate[id];
                int o = id * 4;
                if (t < 0)
                {
                    // outside the islands: neutral values so mips do not bleed odd colours
                    ColorImage.Pixels[o] = ColorImage.Pixels[o + 1] = ColorImage.Pixels[o + 2] = 128; ColorImage.Pixels[o + 3] = 255;
                    SpecImage.Pixels[o] = SpecImage.Pixels[o + 1] = SpecImage.Pixels[o + 2] = 20; SpecImage.Pixels[o + 3] = 40;
                    EmissiveImage.Pixels[o] = EmissiveImage.Pixels[o + 1] = EmissiveImage.Pixels[o + 2] = 0; EmissiveImage.Pixels[o + 3] = 255;
                    continue;
                }
                Vector3 c = View switch
                {
                    PaintView.Roughness => new Vector3(CRough[t]),
                    PaintView.Metal => new Vector3(CMetal[t]),
                    PaintView.Height => new Vector3(CHeight[t]),
                    PaintView.AmbientOcclusion => new Vector3(Map.Ao[t]),
                    PaintView.Curvature => new Vector3(Map.Curv[t] * 0.5f + 0.5f),
                    PaintView.LayerMask => new Vector3(lm?[t] ?? 1),
                    PaintView.Emissive => CColor[t] * CEmis[t],
                    PaintView.Opacity => new Vector3(CAlpha[t]),
                    _ => CColor[t],
                };
                ColorImage.Pixels[o] = B(c.X); ColorImage.Pixels[o + 1] = B(c.Y); ColorImage.Pixels[o + 2] = B(c.Z);
                ColorImage.Pixels[o + 3] = View == PaintView.Final ? B(CAlpha[t]) : (byte)255;
                // GTA spec map: intensity in RGB (metal shines more), glossiness in alpha
                float gloss = 1 - CRough[t];
                float inten = Math.Clamp(gloss * gloss * (0.35f + 0.65f * CMetal[t]), 0, 1);
                SpecImage.Pixels[o] = SpecImage.Pixels[o + 1] = SpecImage.Pixels[o + 2] = B(inten); SpecImage.Pixels[o + 3] = B(gloss);
                var e = CColor[t] * CEmis[t];
                EmissiveImage.Pixels[o] = B(e.X); EmissiveImage.Pixels[o + 1] = B(e.Y); EmissiveImage.Pixels[o + 2] = B(e.Z); EmissiveImage.Pixels[o + 3] = 255;
            }
        // normal from height (one pixel border for the gradients), combined with the baked high-poly normal
        int nx0 = Math.Max(0, x0 - 1), ny0 = Math.Max(0, y0 - 1), nx1 = Math.Min(Size - 1, x1 + 1), ny1 = Math.Min(Size - 1, y1 + 1);
        int Tx(int x, int y) { x = Math.Clamp(x, 0, Size - 1); y = Math.Clamp(y, 0, Size - 1); int id = x + y * Size; int t = Map.Index[id]; return t < 0 ? Map.Dilate[id] : t; }
        float H(int x, int y) { int t = Tx(x, y); return t < 0 ? 0.5f : CHeight[t]; }
        float s = NormalStrength;
        var baked = BakedNormal;
        for (int y = ny0; y <= ny1; y++)
            for (int x = nx0; x <= nx1; x++)
            {
                float dx = (H(x + 1, y) - H(x - 1, y)) * s, dy = (H(x, y + 1) - H(x, y - 1)) * s;
                var n = Vector3.Normalize(new Vector3(-dx, dy, 1)); // y up in the texture (DirectX layout of the existing maps)
                if (baked != null)
                {
                    int t = Tx(x, y);
                    if (t >= 0) { var b = baked[t]; n = Vector3.Normalize(new Vector3(b.X + n.X, b.Y + n.Y, b.Z * n.Z)); } // whiteout blend
                }
                int o = (x + y * Size) * 4;
                NormalImage.Pixels[o] = B(n.X * 0.5f + 0.5f); NormalImage.Pixels[o + 1] = B(n.Y * 0.5f + 0.5f); NormalImage.Pixels[o + 2] = B(n.Z * 0.5f + 0.5f); NormalImage.Pixels[o + 3] = 255;
            }
    }

    static byte B(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    public void SetView(PaintView v) { View = v; WriteImagesParallel(); Dirty = (0, 0, Size - 1, Size - 1); Version++; }

    /// <summary>Whole images in horizontal bands on all cores.</summary>
    void WriteImagesParallel()
    {
        const int band = 64;
        Parallel.For(0, (Size + band - 1) / band, b => WriteImages(0, b * band, Size - 1, Math.Min(Size - 1, b * band + band - 1)));
    }

    /// <summary>Recomposite after a layer setting changed.</summary>
    /// <summary>Shows a layer only on these mesh faces (a wall, the floor...): its painted mask is 1 there, 0 elsewhere.</summary>
    public void LimitLayerToFaces(PaintLayer l, ISet<int> faces)
    {
        if (Map == null || l == null) return;
        var m = new float[Map.Count];
        for (int t = 0; t < Map.Count; t++) m[t] = faces.Contains(Map.FaceOf[t]) ? 1f : 0f;
        l.MaskPaint = m; l.MaskChanged();
        CompositeAll();
    }

    public void Refresh(bool masks = false)
    {
        if (masks) foreach (var l in Layers) l.maskDirty = true;
        CompositeAll();
    }

    // ------------------------------------------------------------------------------ brush

    /// <summary>Layer a stroke goes to: the selected layer, or its folder's mask in mask mode on a folder.</summary>
    bool PaintTarget(PaintBrush b, out PaintLayer l, out bool maskTarget)
    {
        l = Current; maskTarget = false;
        if (l == null || Map == null) return false;
        if (l.IsFolder) { maskTarget = true; return true; }                       // folders only have a mask to paint
        if (b.MaskMode) { maskTarget = true; return true; }
        if (l.Kind == LayerKind.Fill) { maskTarget = l.Mask == MaskGen.Painted; return maskTarget; }
        return true;
    }

    /// <summary>One brush print at an object-space point. Returns false when the current layer cannot be painted.</summary>
    public bool Stamp(PaintBrush b, Vector3 center, Vector3 normal, float radius, float angle = float.NaN, Vector3 smudgeFrom = default)
    {
        if (!PaintTarget(b, out var l, out bool maskT)) return false;
        int n = Map.Count;
        PrepareTarget(l, b, maskT, n);
        float inner = radius * Math.Clamp(b.Hardness, 0, 0.99f);
        var img = b.Stamp ?? b.Tip;
        bool imgColour = b.Stamp != null;
        Vector3 tu = default, tv = default;
        if (img != null)
        {
            Triangulator.Basis(Vector3.Normalize(normal), out var u0, out var v0);
            float ang = (float.IsNaN(angle) ? b.StampAngle : angle) * MathF.PI / 180f;
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            tu = u0 * ca + v0 * sa; tv = -u0 * sa + v0 * ca;
        }
        float aspect = img != null ? img.Width / (float)Math.Max(1, img.Height) : 1;
        // smudge picks up the colour under the previous print
        Vector3 smudgeCol = b.ColorValue; float smudgeRough = b.RoughValue;
        if (b.Mode == BrushMode.Smudge)
        {
            Vector3 acc = Vector3.Zero; float ar = 0; int cnt = 0;
            Map.Query(smudgeFrom, radius * 0.6f, (t, d) => { acc += CColor[t]; ar += CRough[t]; cnt++; });
            if (cnt > 0) { smudgeCol = acc / cnt; smudgeRough = ar / cnt; }
        }
        var hits = new List<(int T, float A, Vector3 Col)>();
        Map.Query(center, img != null ? radius * 1.5f * MathF.Max(1, aspect) : radius, (t, d) =>
        {
            if (b.BackfaceCull && Vector3.Dot(Map.Nrm[t], normal) < 0.05f) return;
            float a; Vector3 c = b.ColorValue;
            if (img != null)
            {
                var dp = Map.Pos[t] - center;
                float su = Vector3.Dot(dp, tu) / (radius * MathF.Max(1, aspect)) * 0.5f + 0.5f, sv = 0.5f - Vector3.Dot(dp, tv) / (radius * MathF.Max(1, 1 / aspect)) * 0.5f;
                if (su < 0 || su > 1 || sv < 0 || sv > 1) return;
                var s = Gta.Library.TextureReader.Sample(img, su, sv, false);
                a = s.W * b.Strength;
                if (imgColour) c = new Vector3(s.X, s.Y, s.Z);
            }
            else
            {
                float f = d <= inner ? 1 : 1 - Smooth((d - inner) / MathF.Max(radius - inner, 1e-6f));
                a = f * b.Strength;
            }
            if (a > 0) hits.Add((t, a, c));
        });
        if (hits.Count == 0) return true;
        // clone: read every source value first (the paint below changes the composite)
        if (b.Mode == BrushMode.Clone && b.CloneSourceSet && !maskT)
        {
            var src = new (int S, float A)[hits.Count];
            for (int i = 0; i < hits.Count; i++) src[i] = (Map.Nearest(Map.Pos[hits[i].T] + b.CloneOffset, radius), hits[i].A);
            var vals = src.Select(x => x.S < 0 ? default : (CColor[x.S], CHeight[x.S], CRough[x.S], CMetal[x.S])).ToArray();
            var touched = new List<int>();
            for (int i = 0; i < hits.Count; i++)
            {
                if (src[i].S < 0) continue;
                var (c, h, r, me) = vals[i];
                ApplyTexel(l, hits[i].T, hits[i].A, false, b, c, h, r, me);
                touched.Add(hits[i].T);
            }
            Finish(l, false, touched);
            return true;
        }
        var list = new List<int>(hits.Count);
        foreach (var (t, a, c) in hits)
        {
            var col = b.Mode == BrushMode.Smudge ? smudgeCol : c;
            var rough = b.Mode == BrushMode.Smudge ? smudgeRough : b.RoughValue;
            ApplyTexel(l, t, a, maskT, b, col, b.HeightValue, rough, b.MetalValue);
            list.Add(t);
        }
        Finish(l, maskT, list);
        return true;
    }

    void PrepareTarget(PaintLayer l, PaintBrush b, bool maskT, int n)
    {
        if (maskT)
        {
            if (l.Kind == LayerKind.Fill && l.Mask == MaskGen.Painted && !b.MaskMode) l.PaintedMask ??= new float[n];
            else l.MaskPaint ??= Enumerable.Repeat(1f, n).ToArray();
            return;
        }
        l.Cover ??= new float[n];
        bool col = b.Color || b.Mode != BrushMode.Paint;
        if (col) l.PColor ??= Enumerable.Repeat(b.ColorValue, n).ToArray();
        if (b.Height || b.Mode == BrushMode.Clone) l.PHeight ??= Enumerable.Repeat(0.5f, n).ToArray();
        if (b.Roughness || b.Mode != BrushMode.Paint) l.PRough ??= Enumerable.Repeat(b.RoughValue, n).ToArray();
        if (b.Metal || b.Mode == BrushMode.Clone) l.PMetal ??= Enumerable.Repeat(b.MetalValue, n).ToArray();
        if (b.Emissive) l.PEmis ??= new float[n];
        if (b.Alpha) l.PAlpha ??= Enumerable.Repeat(1f, n).ToArray();
        if (col) l.UseColor = true;
        if (b.Height || b.Mode == BrushMode.Clone) l.UseHeight = true;
        if (b.Roughness || b.Mode != BrushMode.Paint) l.UseRough = true;
        if (b.Metal || b.Mode == BrushMode.Clone) l.UseMetal = true;
        if (b.Emissive) l.UseEmissive = true;
        if (b.Alpha) l.UseAlpha = true;
    }

    /// <summary>One texel of a stroke or a fill: coverage <paramref name="a"/> towards the given values (or the mask).</summary>
    void ApplyTexel(PaintLayer l, int t, float a, bool maskT, PaintBrush b, Vector3 col, float height, float rough, float metal)
    {
        if (maskT)
        {
            var m = l.Kind == LayerKind.Fill && l.Mask == MaskGen.Painted && !b.MaskMode ? l.PaintedMask : l.MaskPaint;
            m[t] = b.Erase ? m[t] * (1 - a) : m[t] + (1 - m[t]) * a;
            return;
        }
        if (b.Erase) { l.Cover[t] *= 1 - a; return; }
        float c0 = l.Cover[t];
        l.Cover[t] = c0 + (1 - c0) * a;
        float k = c0 <= 0.001f ? 1 : a;
        bool clone = b.Mode == BrushMode.Clone;
        if ((b.Color || b.Mode != BrushMode.Paint) && l.PColor != null) l.PColor[t] = Vector3.Lerp(l.PColor[t], col, k);
        if ((b.Height || clone) && l.PHeight != null) l.PHeight[t] += (height - l.PHeight[t]) * k;
        if ((b.Roughness || b.Mode != BrushMode.Paint) && l.PRough != null) l.PRough[t] += (rough - l.PRough[t]) * k;
        if ((b.Metal || clone) && l.PMetal != null) l.PMetal[t] += (metal - l.PMetal[t]) * k;
        if (b.Emissive && l.PEmis != null) l.PEmis[t] += (b.EmissiveValue - l.PEmis[t]) * k;
        if (b.Alpha && l.PAlpha != null) l.PAlpha[t] += (b.AlphaValue - l.PAlpha[t]) * k;
    }

    void Finish(PaintLayer l, bool maskT, List<int> touched)
    {
        if (touched.Count == 0) return;
        bool folder = l.IsFolder;
        if (maskT)
        {
            l.maskDirty = true; MaskOf(l);
            if (folder || l.Filters.Any(f => f.Enabled && f.OnMask && f.Kind is FilterKind.Blur or FilterKind.Sharpen or FilterKind.DirectionalBlur or FilterKind.Highpass or FilterKind.Outline)) { CompositeAll(); return; }
        }
        PrepareFolders();
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        foreach (var t in touched)
        {
            CompositeTexel(t);
            int px = Map.Pixel[t] % Size, py = Map.Pixel[t] / Size;
            if (px < x0) x0 = px; if (px > x1) x1 = px; if (py < y0) y0 = py; if (py > y1) y1 = py;
        }
        // padding pixels around the stroke follow their source texels
        int pad = Math.Max(2, Size / 128) + 1;
        x0 = Math.Max(0, x0 - pad); y0 = Math.Max(0, y0 - pad); x1 = Math.Min(Size - 1, x1 + pad); y1 = Math.Min(Size - 1, y1 + pad);
        WriteImages(x0, y0, x1, y1);
        Dirty = Dirty is { } d ? (Math.Min(d.X0, x0), Math.Min(d.Y0, y0), Math.Max(d.X1, x1), Math.Max(d.Y1, y1)) : (x0, y0, x1, y1);
        Version++;
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    /// <summary>Fill tool: the face, connected element, UV island or whole set under the click gets the brush values (or mask).</summary>
    public bool Fill(PaintBrush b, int face, FillScope scope)
    {
        if (!PaintTarget(b, out var l, out bool maskT)) return false;
        int seed = scope == FillScope.All ? 0 : Array.IndexOf(Map.FaceOf, face);
        if (seed < 0) return false; // the face belongs to another material
        int isl = Map.IslandOf[seed], el = Map.ElementOf[seed];
        PrepareTarget(l, b, maskT, Map.Count);
        var touched = new List<int>();
        for (int t = 0; t < Map.Count; t++)
        {
            bool inside = scope switch { FillScope.Face => Map.FaceOf[t] == face, FillScope.Element => Map.ElementOf[t] == el, FillScope.UvIsland => Map.IslandOf[t] == isl, _ => true };
            if (!inside) continue;
            ApplyTexel(l, t, b.Strength, maskT, b, b.ColorValue, b.HeightValue, b.RoughValue, b.MetalValue);
            touched.Add(t);
        }
        Finish(l, maskT, touched);
        return true;
    }

    // ------------------------------------------------------------------------------ bake

    /// <summary>Uses a high-poly bake: its normal joins the painted relief, its AO / curvature drive the masks, its colour becomes a layer.</summary>
    public void ApplyBake(BakeResult r, bool colourLayer)
    {
        if (r.Normal != null) BakedNormal = r.Normal;
        if (r.Ao != null) Map.Ao = r.Ao;
        if (r.Curv != null) Map.Curv = r.Curv;
        HasBakedMaps = true;
        if (colourLayer && r.Colour != null)
        {
            var l = new PaintLayer { Name = "Baked colour (high-poly)", Kind = LayerKind.Paint, UseColor = true, UseRough = false };
            l.Cover = r.Hit.Select(h => h ? 1f : 0f).ToArray(); l.PColor = r.Colour;
            l.Id = NextId++;
            Layers.Insert(Math.Min(1, Layers.Count), l);
            SelectedLayer = Layers.IndexOf(l);
        }
        Refresh(true);
    }

    // ------------------------------------------------------------------------------ undo snapshots

    public PaintLayer Snapshot(int layer) => Layers[layer].CloneLayer();
    public void Restore(int layer, PaintLayer snap) { if (layer < Layers.Count) Layers[layer] = snap.CloneLayer(); CompositeAll(); }

    // ------------------------------------------------------------------------------ bake to GTA textures

    /// <summary>Final textures (always the Final view): diffuse (alpha = opacity), normal (relief + baked), GTA specular, emissive.</summary>
    public (RgbaImage Diffuse, RgbaImage Normal, RgbaImage Spec, RgbaImage Emissive) BakeImages()
    {
        var keep = View;
        View = PaintView.Final;
        WriteImages(0, 0, Size - 1, Size - 1);
        var res = (Copy(ColorImage), Copy(NormalImage), Copy(SpecImage), Copy(EmissiveImage));
        View = keep;
        if (keep != PaintView.Final) WriteImages(0, 0, Size - 1, Size - 1);
        return res;
    }

    static RgbaImage Copy(RgbaImage i) => new(i.Width, i.Height) { Pixels = (byte[])i.Pixels.Clone() };

    // ------------------------------------------------------------------------------ save / load (pixel space, survives mesh edits that keep the UVs)

    sealed class PixelLayer { public byte[] Cover, Col, Rough, Metal, Mask, MaskPaint, Emis, Alpha; public ushort[] Height; }

    byte[] Px(float[] v) { if (v == null) return null; var p = new byte[Size * Size]; for (int t = 0; t < Map.Count; t++) p[Map.Pixel[t]] = B(v[t]); return p; }
    float[] Tx(byte[] p) => p == null ? null : Enumerable.Range(0, Map.Count).Select(t => p[Map.Pixel[t]] / 255f).ToArray();
    byte[] PixelsOf(Vector3[] v) { var p = new byte[Size * Size * 3]; for (int t = 0; t < Map.Count; t++) { int i = Map.Pixel[t] * 3; p[i] = B(v[t].X * 0.5f + 0.5f); p[i + 1] = B(v[t].Y * 0.5f + 0.5f); p[i + 2] = B(v[t].Z * 0.5f + 0.5f); } return p; }
    Vector3[] TexelsOf(byte[] p) => Enumerable.Range(0, Map.Count).Select(t => { int i = Map.Pixel[t] * 3; var v = new Vector3(p[i] / 127.5f - 1, p[i + 1] / 127.5f - 1, p[i + 2] / 127.5f - 1); return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitZ; }).ToArray();

    PixelLayer ToPixels(PaintLayer l)
    {
        var p = new PixelLayer { Cover = Px(l.Cover), Rough = Px(l.PRough), Metal = Px(l.PMetal), Mask = Px(l.PaintedMask), MaskPaint = Px(l.MaskPaint), Emis = Px(l.PEmis), Alpha = Px(l.PAlpha) };
        int N = Size * Size;
        if (l.PColor != null) { p.Col = new byte[N * 3]; for (int t = 0; t < Map.Count; t++) { int i = Map.Pixel[t] * 3; var c = l.PColor[t]; p.Col[i] = B(c.X); p.Col[i + 1] = B(c.Y); p.Col[i + 2] = B(c.Z); } }
        if (l.PHeight != null) { p.Height = new ushort[N]; for (int t = 0; t < Map.Count; t++) p.Height[Map.Pixel[t]] = (ushort)Math.Clamp(l.PHeight[t] * 65535f, 0, 65535); }
        return p;
    }

    void FromPixels(PaintLayer l, PixelLayer p)
    {
        int n = Map.Count;
        l.Cover = Tx(p.Cover); l.PRough = Tx(p.Rough); l.PMetal = Tx(p.Metal); l.PaintedMask = Tx(p.Mask); l.MaskPaint = Tx(p.MaskPaint); l.PEmis = Tx(p.Emis); l.PAlpha = Tx(p.Alpha);
        l.PColor = p.Col == null ? null : Enumerable.Range(0, n).Select(t => { int i = Map.Pixel[t] * 3; return new Vector3(p.Col[i] / 255f, p.Col[i + 1] / 255f, p.Col[i + 2] / 255f); }).ToArray();
        l.PHeight = p.Height == null ? null : Enumerable.Range(0, n).Select(t => p.Height[Map.Pixel[t]] / 65535f).ToArray();
        l.maskDirty = true;
    }

    static void Arr(BinaryWriter w, byte[] a) { w.Write(a != null); if (a != null) { w.Write(a.Length); w.Write(a); } }
    static byte[] Arr(BinaryReader r) => r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null;

    /// <summary>Current format: settings as JSON (forward compatible), pixels as compressed arrays.</summary>
    public void Write(BinaryWriter w)
    {
        w.Write("SET3"); w.Write(Size); w.Write(Name ?? ""); w.Write(NormalStrength); w.Write(SelectedLayer); w.Write(NextId);
        w.Write(ExportSize); w.Write((int)ExportFormat); w.Write(AlphaShader ?? "");
        w.Write(Layers.Count);
        foreach (var l in Layers)
        {
            w.Write(l.SettingsJson());
            var p = ToPixels(l);
            Arr(w, p.Cover); Arr(w, p.Col); Arr(w, p.Rough); Arr(w, p.Metal); Arr(w, p.Mask); Arr(w, p.MaskPaint); Arr(w, p.Emis); Arr(w, p.Alpha);
            w.Write(p.Height != null); if (p.Height != null) { w.Write(p.Height.Length); foreach (var h in p.Height) w.Write(h); }
        }
        w.Write(HasBakedMaps);
        if (HasBakedMaps) { Arr(w, BakedNormal != null ? PixelsOf(BakedNormal) : null); Arr(w, Px(Map.Ao)); Arr(w, Px(Map.Curv.Select(c => c * 0.5f + 0.5f).ToArray())); }
    }

    public static TextureSet Read(BinaryReader r, PolyMesh mesh, int aoRays, int matId, int altMatId = -1)
    {
        var magic = r.ReadString();
        if (magic != "SET3") throw new InvalidDataException("Unknown paint set version " + magic);
        var ts = new TextureSet { Size = r.ReadInt32(), MatId = matId, AltMatId = altMatId };
        ts.Name = r.ReadString(); ts.NormalStrength = r.ReadSingle(); ts.SelectedLayer = r.ReadInt32(); ts.NextId = r.ReadInt32();
        ts.ExportSize = r.ReadInt32(); ts.ExportFormat = (PaintExportFormat)r.ReadInt32(); ts.AlphaShader = r.ReadString();
        int count = r.ReadInt32();
        ts.Map = TexelMap.Build(mesh, ts.Size, aoRays, ts.FaceFilter(mesh));
        for (int i = 0; i < count; i++)
        {
            var l = PaintLayer.FromSettingsJson(r.ReadString());
            l.Filters ??= new();
            var p = new PixelLayer { Cover = Arr(r), Col = Arr(r), Rough = Arr(r), Metal = Arr(r), Mask = Arr(r), MaskPaint = Arr(r), Emis = Arr(r), Alpha = Arr(r) };
            if (r.ReadBoolean()) { int n = r.ReadInt32(); p.Height = new ushort[n]; for (int k = 0; k < n; k++) p.Height[k] = r.ReadUInt16(); }
            ts.Layers.Add(l);
            ts.FromPixels(l, p);
        }
        if (r.ReadBoolean())
        {
            var bn = Arr(r); var ao = Arr(r); var cv = Arr(r);
            if (bn != null) ts.BakedNormal = ts.TexelsOf(bn);
            if (ao != null) ts.Map.Ao = ts.Tx(ao);
            if (cv != null) ts.Map.Curv = ts.Tx(cv).Select(c => c * 2 - 1).ToArray();
            ts.HasBakedMaps = true;
        }
        if (ts.NextId <= ts.Layers.Select(l => l.Id).DefaultIfEmpty(0).Max()) ts.NextId = ts.Layers.Max(l => l.Id) + 1;
        ts.Allocate();
        ts.CompositeAll();
        return ts;
    }

    /// <summary>Files written before multi-material painting (MPNT1 / MPNT2): one set for the whole object.</summary>
    internal static TextureSet ReadLegacy(BinaryReader r, string magic, PolyMesh mesh, int aoRays, int matId)
    {
        bool v2 = magic == "MPNT2";
        var ts = new TextureSet { Size = r.ReadInt32(), MatId = matId };
        ts.NormalStrength = r.ReadSingle(); ts.SelectedLayer = r.ReadInt32();
        int count = r.ReadInt32();
        ts.Map = TexelMap.Build(mesh, ts.Size, aoRays, ts.FaceFilter(mesh));
        for (int i = 0; i < count; i++)
        {
            var l = new PaintLayer
            {
                Name = r.ReadString(), Kind = (LayerKind)r.ReadInt32(), Visible = r.ReadBoolean(), Opacity = r.ReadSingle(), Blend = (PaintBlend)r.ReadInt32(),
                UseColor = r.ReadBoolean(), UseHeight = r.ReadBoolean(), UseRough = r.ReadBoolean(), UseMetal = r.ReadBoolean(),
                Color = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Height = r.ReadSingle(), Rough = r.ReadSingle(), Metal = r.ReadSingle(),
                ColorNoise = r.ReadSingle(), HeightNoise = r.ReadSingle(), NoiseScale = r.ReadSingle(),
                Mask = (MaskGen)r.ReadInt32(), MaskThreshold = r.ReadSingle(), MaskContrast = r.ReadSingle(), MaskBreakup = r.ReadSingle(), MaskNoiseScale = r.ReadSingle(), MaskInvert = r.ReadBoolean(), Seed = r.ReadInt32(),
            };
            if (v2)
            {
                string S() { var s = r.ReadString(); return s.Length == 0 ? null : s; }
                l.TexDiffuse = S(); l.TexSpec = S(); l.TexHeight = S(); l.TileMetres = r.ReadSingle(); l.TexHeightAmount = r.ReadSingle();
            }
            var p = new PixelLayer { Cover = Arr(r), Col = Arr(r), Rough = Arr(r), Metal = Arr(r), Mask = Arr(r) };
            if (r.ReadBoolean()) { int n = r.ReadInt32(); p.Height = new ushort[n]; for (int k = 0; k < n; k++) p.Height[k] = r.ReadUInt16(); }
            l.Id = ts.NextId++;
            ts.Layers.Add(l);
            ts.FromPixels(l, p);
        }
        ts.Allocate();
        ts.CompositeAll();
        return ts;
    }
}

/// <summary>Everything painted on one object: one texture set per material (like Substance's texture sets).</summary>
public sealed class PaintDoc
{
    public readonly List<TextureSet> Sets = new();
    public int Active;
    public TextureSet Cur => Sets.Count == 0 ? null : Sets[Math.Clamp(Active, 0, Sets.Count - 1)];

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var z = new DeflateStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(z);
        w.Write("MPDOC1"); w.Write(Active); w.Write(Sets.Count);
        foreach (var s in Sets) { w.Write(s.Name ?? ""); s.Write(w); }
    }

    /// <param name="matIdOfSet">Paint material id of a set from its name (the app keeps one material per set).</param>
    /// <param name="altOfSet">Its second (emissive) material id, -1 when none.</param>
    public static PaintDoc Load(string path, PolyMesh mesh, int aoRays, Func<string, int> matIdOfSet, Func<string, int> altOfSet = null)
    {
        using var fs = File.OpenRead(path);
        using var z = new DeflateStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        var magic = r.ReadString();
        var doc = new PaintDoc();
        if (magic is "MPNT1" or "MPNT2") { doc.Sets.Add(TextureSet.ReadLegacy(r, magic, mesh, aoRays, -1)); return doc; }
        if (magic != "MPDOC1") throw new InvalidDataException("Not a MoloMax paint file.");
        doc.Active = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var name = r.ReadString();
            int matId = n > 1 ? matIdOfSet?.Invoke(name) ?? -1 : -1;
            doc.Sets.Add(TextureSet.Read(r, mesh, aoRays, matId, n > 1 ? altOfSet?.Invoke(name) ?? -1 : -1));
        }
        return doc;
    }
}

/// <summary>Deterministic 3D value noise (fBm) for masks and fill variation.</summary>
public static class Noise
{
    static float Hash(int x, int y, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 1442695041);
        h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    public static float Value(Vector3 p, int seed)
    {
        int ix = (int)MathF.Floor(p.X), iy = (int)MathF.Floor(p.Y), iz = (int)MathF.Floor(p.Z);
        float fx = p.X - ix, fy = p.Y - iy, fz = p.Z - iz;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Hash(ix, iy, iz, seed), Hash(ix + 1, iy, iz, seed), fx), L(Hash(ix, iy + 1, iz, seed), Hash(ix + 1, iy + 1, iz, seed), fx), fy),
                 L(L(Hash(ix, iy, iz + 1, seed), Hash(ix + 1, iy, iz + 1, seed), fx), L(Hash(ix, iy + 1, iz + 1, seed), Hash(ix + 1, iy + 1, iz + 1, seed), fx), fy), fz);
    }

    public static float Fbm(Vector3 p, int seed)
    {
        float s = 0, a = 0.5f, norm = 0;
        for (int o = 0; o < 4; o++) { s += Value(p, seed + o * 31) * a; norm += a; p *= 2.03f; a *= 0.5f; }
        return s / norm;
    }
}
