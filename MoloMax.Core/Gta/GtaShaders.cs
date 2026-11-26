using System.Numerics;

namespace MoloMax.Core.Gta;

/// <summary>What a GTA V shader preset (.sps) uses and how it draws.</summary>
public sealed record GtaShaderPreset(
    string Name,
    string Label,
    bool Bump,
    bool Spec,
    bool Detail,
    bool Emissive,
    bool Cutout,
    bool Decal,
    bool Glass,
    int RenderBucket,
    string Notes)
{
    /// <summary>Drawn in the alpha pass (blended) rather than opaque with alpha test.</summary>
    public bool AlphaBlended => Decal || Glass;
}

/// <summary>
/// The GTA V shaders a building modder reaches for most. Flags drive the viewport shader
/// exactly the way CodeWalker's BasicShader does (diffuse / bump / spec / detail, emissive adds
/// the unlit albedo, opaque alpha-tests at 0.33, decals and glass blend).
/// </summary>
public static class GtaShaders
{
    public static readonly IReadOnlyList<GtaShaderPreset> All = new List<GtaShaderPreset>
    {
        new("default",             "Default",                    false, false, false, false, false, false, false, 0, "Diffuse only. Cheapest; good for far or simple props."),
        new("spec",                "Spec",                       false, true,  false, false, false, false, false, 0, "Diffuse + specular map."),
        new("normal",              "Normal",                     true,  false, false, false, false, false, false, 0, "Diffuse + normal map."),
        new("normal_spec",         "Normal Spec",                true,  true,  false, false, false, false, false, 0, "The usual choice for walls, floors and props."),
        new("normal_spec_detail",  "Normal Spec Detail",         true,  true,  true,  false, false, false, false, 0, "Adds a tiling detail map for close-up surfaces."),
        new("normal_cutout",       "Normal Cutout",              true,  false, false, false, true,  false, false, 3, "Alpha tested (fences, leaves, grates)."),
        new("normal_spec_cutout",  "Normal Spec Cutout",         true,  true,  false, false, true,  false, false, 3, "Alpha tested with specular."),
        new("cutout_fence",        "Cutout Fence",               false, false, false, false, true,  false, false, 3, "Chain-link and wire fences."),
        new("decal",               "Decal",                      false, false, false, false, false, true,  false, 2, "Alpha-blended overlay (dirt, posters, graffiti)."),
        new("normal_decal",        "Normal Decal",               true,  false, false, false, false, true,  false, 2, "Decal with its own normal map."),
        new("normal_spec_decal",   "Normal Spec Decal",          true,  true,  false, false, false, true,  false, 2, "Decal with normal and specular."),
        new("decal_dirt",          "Decal Dirt",                 false, false, false, false, false, true,  false, 2, "Baked shadow / AO maps on an offset copy of the surface."),
        new("decal_normal_only",   "Decal Normal Only",          true,  false, false, false, false, true,  false, 2, "Normal-map-only decal: edge wear, smooth edges (needs specular > 0)."),
        new("emissive",            "Emissive",                   false, false, false, true,  false, false, false, 0, "Self-lit: signs, screens, lamps. Use white vertex colour."),
        new("emissivestrong",      "Emissive Strong",            false, false, false, true,  false, false, false, 0, "Brighter emissive for neon and bright panels."),
        new("emissive_clip",       "Emissive Clip",              false, false, false, true,  true,  false, false, 0, "Emissive with alpha test (neon letters)."),
        new("emissive_additive_alpha", "Emissive Additive Alpha", false, false, false, true, false, true,  false, 1, "Additive glow cards (sign glow halos)."),
        new("parallax",            "Parallax",                   true,  false, false, false, false, false, false, 0, "Fake-depth windows (normal in RGB, height in A of the bump map)."),
        new("glass",               "Glass",                      false, true,  false, false, false, false, true,  1, "Transparent glass."),
        new("glass_pv",            "Glass PV",                   false, true,  false, false, false, false, true,  1, "Breakable window glass (cracks when shot)."),
        new("glass_emissive",      "Glass Emissive",             true,  false, false, true,  false, false, false, 0, "Lit glass panels and light covers (opaque)."),
        new("glass_emissive_alpha","Glass Emissive Alpha",       true,  false, false, true,  false, false, true,  1, "Transparent lit glass."),
        new("terrain_cb_w_4lyr",   "Terrain 4 layers",           true,  false, false, false, false, false, false, 0, "Ground: 4 textures blended by vertex colour 1 (paint them with the vertex colour brush)."),
        new("terrain_cb_w_4lyr_spec", "Terrain 4 layers Spec",   true,  true,  false, false, false, false, false, 0, "Terrain 4 layers with specular."),
    };

    static readonly Dictionary<string, GtaShaderPreset> byName = All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static GtaShaderPreset Get(string name) => name != null && byName.TryGetValue(name, out var s) ? s
        : name != null && name.StartsWith("terrain_cb_w_4lyr", StringComparison.OrdinalIgnoreCase) ? byName["terrain_cb_w_4lyr"] : All[0];
}

/// <summary>A material as GTA understands it: a shader preset, its textures and its parameters.</summary>
public sealed class GtaMaterial
{
    public int Id;
    public string Name = "Material";
    public string Shader = "normal_spec";

    /// <summary>Texture names or file paths (.dds / .png / .jpg / .tga). Null = not set.</summary>
    public string DiffuseTexture, BumpTexture, SpecTexture, DetailTexture;

    public float Bumpiness = 1f;
    public float SpecularIntensity = 0.5f;
    public float SpecularFalloff = 100f;
    public float SpecularFresnel = 0.97f;
    public Vector3 SpecMapIntMask = new(1, 0, 0);
    public float EmissiveMultiplier = 1f;
    public Vector4 DetailSettings = new(0, 0, 1, 1);

    /// <summary>Viewport colour when no diffuse texture is set (not exported).</summary>
    public Vector3 ViewportColour = new(0.75f, 0.75f, 0.75f);

    /// <summary>Copied from the game's models for the map view (not one of the user's materials: hidden in the Material Editor).</summary>
    public bool FromGame;

    public GtaShaderPreset Preset => GtaShaders.Get(Shader);

    /// <summary>Terrain shaders: the 4 layer textures (TextureSampler_layer0..3) and their normal maps (BumpSampler_layer0..3).</summary>
    public string[] Layers, LayerBumps;
    /// <summary>Tint shaders (*_tnt): the palette texture (TintPaletteSampler); the vertex colour's blue picks its column.</summary>
    public string TintTexture;
    /// <summary>A terrain_cb_w_4lyr shader: 4 textures blended by vertex colour 1.</summary>
    public bool IsTerrain => Shader != null && Shader.StartsWith("terrain_cb_w_4lyr", StringComparison.OrdinalIgnoreCase);
    public string Layer(int i) => Layers != null && i < Layers.Length ? Layers[i] : null;
    public string LayerBump(int i) => LayerBumps != null && i < LayerBumps.Length ? LayerBumps[i] : null;
    public void EnsureLayers() { Layers ??= new string[4]; LayerBumps ??= new string[4]; }

    public GtaMaterial Clone()
    {
        var c = (GtaMaterial)MemberwiseClone();
        c.Layers = (string[])Layers?.Clone(); c.LayerBumps = (string[])LayerBumps?.Clone();
        return c;
    }

    public static GtaMaterial CreateDefault(int id, string name, Vector3 colour) => new()
    {
        Id = id, Name = name, ViewportColour = colour, Shader = "normal_spec",
    };
}
