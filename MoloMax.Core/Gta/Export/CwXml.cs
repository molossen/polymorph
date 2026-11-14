using System.Globalization;
using System.Numerics;
using System.Text;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Gta.Export;

/// <summary>CodeWalker XML for drawables (.ydr.xml) and bounds (.ybn.xml) — the same format Sollumz writes.</summary>
public static class CwXml
{
    static readonly CultureInfo ci = CultureInfo.InvariantCulture;
    static string F(float v) => Xw.F(v);

    // ------------------------------------------------------------------ drawable

    /// <summary>Shader + textures chosen for one material of a model.</summary>
    sealed class ShaderSetup
    {
        public ShaderDef Def;
        public string Preset;
        public int Bucket;
        public List<(ShaderParamDef P, string Texture, Vector4 Value)> Values = new();
    }

    static ShaderSetup Setup(GtaMaterial m, TextureBank bank, List<ExportTexture> used)
    {
        var resolved = ShaderDb.FromPreset(m.Shader) ?? ShaderDb.FromPreset("default").Value;
        var s = new ShaderSetup { Def = resolved.Def, Bucket = resolved.Bucket, Preset = ShaderDb.FromPreset(m.Shader) != null ? Path.GetFileNameWithoutExtension(m.Shader) : "default" };

        ExportTexture Tex(string path, TexKind kind)
        {
            var t = bank.FromFile(path, kind) ?? bank.Placeholder(kind, m.ViewportColour);
            if (!used.Contains(t)) used.Add(t);
            return t;
        }

        foreach (var p in s.Def.Params)
        {
            if (p.IsTexture)
            {
                ExportTexture t = p.Name switch
                {
                    "DiffuseSampler" => Tex(m.DiffuseTexture, TexKind.Diffuse),
                    "BumpSampler" => Tex(m.BumpTexture, TexKind.Normal),
                    "SpecSampler" => Tex(m.SpecTexture, TexKind.Specular),
                    "DetailSampler" => Tex(m.DetailTexture, TexKind.Detail),
                    "EnvironmentSampler" => Tex(null, TexKind.Environment),
                    "TextureSampler_layer0" or "TextureSampler_layer1" or "TextureSampler_layer2" or "TextureSampler_layer3"
                        => Tex(m.Layer(p.Name[^1] - '0') ?? m.DiffuseTexture, TexKind.Diffuse),
                    "BumpSampler_layer0" or "BumpSampler_layer1" or "BumpSampler_layer2" or "BumpSampler_layer3"
                        => Tex(m.LayerBump(p.Name[^1] - '0') ?? m.BumpTexture, TexKind.Normal),
                    _ => Tex(null, p.Name.Contains("Bump") ? TexKind.Normal : p.Name.Contains("Spec") ? TexKind.Specular : TexKind.Diffuse),
                };
                s.Values.Add((p, t.Name, default));
                continue;
            }
            var v = p.Default;
            switch (p.Name)
            {
                case "bumpiness": v = new Vector4(m.Bumpiness, 0, 0, 0); break;
                case "specularIntensityMult": v = new Vector4(m.SpecularIntensity, 0, 0, 0); break;
                case "specularFalloffMult": v = new Vector4(m.SpecularFalloff, 0, 0, 0); break;
                case "specularFresnel": v = new Vector4(m.SpecularFresnel, 0, 0, 0); break;
                case "specMapIntMask": v = new Vector4(m.SpecMapIntMask, 0); break;
                case "emissiveMultiplier": v = new Vector4(m.EmissiveMultiplier, 0, 0, 0); break;
                case "detailSettings": v = m.DetailSettings; break;
            }
            s.Values.Add((p, null, v));
        }
        return s;
    }

    /// <param name="embedTextures">True: textures go inside the .ydr (needs the .dds files next to the XML when compiling).</param>
    public static string Drawable(GtaModel model, TextureBank bank, bool embedTextures, out List<ExportTexture> textures)
    {
        textures = new List<ExportTexture>();
        var setups = new Dictionary<int, (int Index, ShaderSetup S)>();
        var order = new List<ShaderSetup>();
        foreach (var part in model.Parts.Concat(model.Lods.SelectMany(l => l.Parts)))
        {
            if (setups.ContainsKey(part.Material.Id)) continue;
            var s = Setup(part.Material, bank, textures);
            setups[part.Material.Id] = (order.Count, s);
            order.Add(s);
        }

        var x = new Xw();
        x.Open("Drawable");
        x.Text("Name", model.Name);
        var c = model.Center;
        x.Vec("BoundingSphereCenter", c);
        x.Value("BoundingSphereRadius", model.Radius);
        x.Vec("BoundingBoxMin", model.Min);
        x.Vec("BoundingBoxMax", model.Max);
        // LOD switch distances: one level = shown up to the archetype distance; with LODs each level stops at its distance
        var ld = model.Lods.Count > 0 && model.LodDists != null ? model.LodDists : new[] { 9998f, 9998f, 9998f, 9998f };
        x.Value("LodDistHigh", ld[0]); x.Value("LodDistMed", ld[1]); x.Value("LodDistLow", ld[2]); x.Value("LodDistVlow", ld[3]);
        x.Value("FlagsHigh", 1); x.Value("FlagsMed", model.Lods.Count > 0 ? 1 : 0); x.Value("FlagsLow", model.Lods.Count > 1 ? 1 : 0); x.Value("FlagsVlow", model.Lods.Count > 2 ? 1 : 0);

        x.Open("ShaderGroup");
        if (embedTextures && textures.Count > 0)
        {
            x.Open("TextureDictionary");
            foreach (var t in textures) TextureItem(x, t);
            x.Close("TextureDictionary");
        }
        x.Open("Shaders");
        foreach (var s in order)
        {
            x.Open("Item");
            x.Text("Name", s.Def.Name);
            x.Text("FileName", s.Preset + ".sps");
            x.Value("RenderBucket", s.Bucket);
            x.Open("Parameters");
            foreach (var (p, tex, v) in s.Values)
            {
                if (p.IsTexture)
                {
                    x.Open("Item", $"name=\"{p.Name}\" type=\"Texture\"");
                    x.Text("Name", tex);
                    x.Close("Item");
                }
                else if (p.Count > 1)
                {
                    x.Open("Item", $"name=\"{p.Name}\" type=\"Array\"");
                    for (int i = 0; i < p.Count; i++) x.Vec("Value", i == 0 ? v : Vector4.Zero);
                    x.Close("Item");
                }
                else x.Empty("Item", $"name=\"{p.Name}\" type=\"Vector\" x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" w=\"{F(v.W)}\"");
            }
            x.Close("Parameters");
            x.Close("Item");
        }
        x.Close("Shaders");
        x.Close("ShaderGroup");
        if (model.Skin != null) Skeleton(x, model.Skin, model.Name);
        int boneCount = model.Skin?.Bones.Count ?? 0;

        void Level(string tag, List<ModelPart> parts)
        {
            x.Open(tag);
            x.Open("Item");
            if (boneCount > 0) { x.Value("RenderMask", 255); x.Value("Flags", 1); x.Value("HasSkin", 1); x.Value("BoneIndex", 0); x.Value("Unknown1", boneCount); }
            else { x.Value("RenderMask", 255); x.Value("Flags", 0); x.Value("HasSkin", 0); x.Value("BoneIndex", 0); x.Value("Unknown1", 0); }
            x.Open("Geometries");
            foreach (var part in parts)
            {
                var (index, s) = setups[part.Material.Id];
                Geometry(x, part, index, s.Def.Layout, boneCount);
            }
            x.Close("Geometries");
            x.Close("Item");
            x.Close(tag);
        }
        Level("DrawableModelsHigh", model.Parts);
        string[] lodTags = { "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow" };
        for (int i = 0; i < model.Lods.Count && i < 3; i++) if (model.Lods[i].Parts.Count > 0) Level(lodTags[i], model.Lods[i].Parts);

        if (model.EmbeddedCollision != null && !model.EmbeddedCollision.IsEmpty)
            BoundsComposite(x, model.EmbeddedCollision, "Bounds");
        Lights(x, model.Lights);
        x.Close("Drawable");
        return x.ToString();
    }

    /// <summary>LightAttributes, the way CodeWalker reads them (degrees for the cone, metres for the falloff).</summary>
    public static void Lights(Xw x, List<ModelLight> lights)
    {
        if (lights == null || lights.Count == 0) { x.Empty("Lights"); return; }
        x.Open("Lights");
        foreach (var ml in lights)
        {
            var l = ml.L;
            x.Open("Item");
            x.Vec("Position", ml.Position);
            var lc = l.EffectiveColour;
            x.Empty("Colour", $"r=\"{(int)(Math.Clamp(lc.X, 0, 1) * 255)}\" g=\"{(int)(Math.Clamp(lc.Y, 0, 1) * 255)}\" b=\"{(int)(Math.Clamp(lc.Z, 0, 1) * 255)}\"");
            x.Value("Flashiness", (int)l.Flashiness);
            x.Value("Intensity", l.Intensity);
            x.Value("Flags", l.Flags);
            x.Value("BoneId", 0);
            x.Text("Type", l.Kind.ToString());
            x.Value("GroupId", (int)l.GroupId);
            x.Value("TimeFlags", l.TimeFlags);
            x.Value("Falloff", l.Range);
            x.Value("FalloffExponent", l.FalloffExponent);
            x.Vec("CullingPlaneNormal", l.CullingPlaneNormal);
            x.Value("CullingPlaneOffset", l.CullingPlaneOffset);
            x.Value("Unknown45", 0); x.Value("Unknown46", 0);
            x.Value("VolumeIntensity", l.DrawVolume ? l.VolumeIntensity : 0f);
            x.Value("VolumeSizeScale", l.VolumeSize);
            var vo = l.VolumeOuterColour;
            x.Empty("VolumeOuterColour", $"r=\"{(int)(Math.Clamp(vo.X, 0, 1) * 255)}\" g=\"{(int)(Math.Clamp(vo.Y, 0, 1) * 255)}\" b=\"{(int)(Math.Clamp(vo.Z, 0, 1) * 255)}\"");
            x.Value("LightHash", (int)l.LightHash);
            x.Value("VolumeOuterIntensity", l.VolumeOuterIntensity);
            x.Value("CoronaSize", l.CoronaSize);
            x.Value("VolumeOuterExponent", l.VolumeOuterExponent);
            x.Value("LightFadeDistance", (int)l.LightFadeDistance); x.Value("ShadowBlur", (int)l.ShadowBlur); x.Value("ShadowFadeDistance", (int)l.ShadowFadeDistance);
            x.Value("SpecularFadeDistance", (int)l.SpecularFadeDistance); x.Value("VolumetricFadeDistance", (int)l.VolumetricFadeDistance);
            x.Value("ShadowNearClip", l.ShadowNearClip);
            x.Value("CoronaIntensity", l.CoronaIntensity);
            x.Value("CoronaZBias", l.CoronaZBias);
            x.Vec("Direction", ml.Direction);
            x.Vec("Tangent", ml.Tangent);
            x.Value("ConeInnerAngle", l.ConeInner);
            x.Value("ConeOuterAngle", l.ConeOuter);
            x.Vec("Extent", new Vector3(l.CapsuleLength, l.CapsuleLength, l.CapsuleLength));
            if (string.IsNullOrWhiteSpace(l.ProjectedTexture)) x.Empty("ProjectedTextureHash"); else x.Text("ProjectedTextureHash", l.ProjectedTexture.Trim().ToLowerInvariant());
            x.Close("Item");
        }
        x.Close("Lights");
    }

    static void TextureItem(Xw x, ExportTexture t)
    {
        x.Open("Item");
        x.Text("Name", t.Name);
        x.Value("Unk32", 128);
        x.Text("Usage", t.Kind switch { TexKind.Normal => "NORMAL", TexKind.Specular => "SPECULAR", TexKind.Detail => "DETAIL", _ => "DIFFUSE" });
        x.Text("UsageFlags", "UNK24");
        x.Value("ExtraFlags", 0);
        x.Value("Width", t.Width); x.Value("Height", t.Height); x.Value("MipLevels", t.Mips);
        x.Text("Format", t.Format);
        x.Text("FileName", t.FileName);
        x.Close("Item");
    }

    /// <summary>A texture dictionary (.ytd.xml) for the separate-YTD option.</summary>
    public static string TextureDictionary(IEnumerable<ExportTexture> textures)
    {
        var x = new Xw();
        x.Open("TextureDictionary");
        foreach (var t in textures) TextureItem(x, t);
        x.Close("TextureDictionary");
        return x.ToString();
    }

    /// <summary>The skeleton of a skinned drawable, like the game's cutscene props: a root bone (tag 0) and the bones.</summary>
    static void Skeleton(Xw x, ModelSkin skin, string name)
    {
        var bones = skin.Bones;
        x.Open("Skeleton");
        x.Value("Unknown1C", 16777216);
        x.Value("Unknown50", MetaXml.Jenkins(name + "_skel")); x.Value("Unknown54", MetaXml.Jenkins(name + "_bones")); x.Value("Unknown58", MetaXml.Jenkins(name + "_tags"));
        x.Open("Bones");
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            int sib = -1;
            for (int j = i + 1; j < bones.Count; j++) if (bones[j].Parent == b.Parent) { sib = j; break; }
            bool kids = bones.Any(o => o.Parent == i);
            x.Open("Item");
            x.Text("Name", b.Name);
            x.Value("Tag", b.Tag); x.Value("Index", i); x.Value("ParentIndex", b.Parent); x.Value("SiblingIndex", b.Parent < 0 ? -1 : sib);
            x.Text("Flags", "RotX, RotY, RotZ, TransX, TransY, TransZ" + (kids ? ", Unk0" : ""));
            x.Vec("Translation", b.Translation);
            x.Empty("Rotation", $"x=\"{F(b.Rotation.X)}\" y=\"{F(b.Rotation.Y)}\" z=\"{F(b.Rotation.Z)}\" w=\"{F(b.Rotation.W)}\"");
            x.Vec("Scale", Vector3.One);
            x.Empty("TransformUnk", "x=\"0\" y=\"4\" z=\"-3\" w=\"0\"");
            x.Close("Item");
        }
        x.Close("Bones");
        x.Close("Skeleton");
    }

    static void Geometry(Xw x, ModelPart part, int shaderIndex, List<string> layout, int boneCount = 0)
    {
        if (layout == null || layout.Count == 0) layout = new List<string> { "Position", "Normal", "Colour0", "TexCoord0" };
        bool skinned = boneCount > 0 && part.Blend != null;
        if (skinned && !layout.Contains("BlendWeights"))
        {
            layout = new List<string>(layout);
            int at = Math.Max(0, layout.IndexOf("Position")) + 1;
            layout.Insert(at, "BlendIndices"); layout.Insert(at, "BlendWeights");
        }
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in part.Verts) { mn = Vector3.Min(mn, v.Position); mx = Vector3.Max(mx, v.Position); }
        x.Open("Item");
        x.Value("ShaderIndex", shaderIndex);
        x.Vec("BoundingBoxMin", new Vector4(mn, mn.X));
        x.Vec("BoundingBoxMax", new Vector4(mx, mx.X));
        if (skinned) x.Text("BoneIDs", string.Join(", ", Enumerable.Range(0, boneCount)));
        x.Open("VertexBuffer");
        x.Value("Flags", 0);
        x.Open("Layout", "type=\"GTAV1\"");
        foreach (var e in layout) x.Empty(e);
        x.Close("Layout");
        x.Lines("Data", part.Verts.Select((v, i) => VertexLine(v, layout, skinned ? part.Blend : null, i)));
        x.Close("VertexBuffer");
        x.Open("IndexBuffer");
        var lines = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < part.Indices.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part.Indices[i].ToString(ci));
            if ((i + 1) % 24 == 0) { lines.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        x.Lines("Data", lines);
        x.Close("IndexBuffer");
        x.Close("Item");
    }

    static string B(float f) => ((int)Math.Clamp(f * 255f + 0.5f, 0, 255)).ToString(ci);

    static string VertexLine(RenderVertex v, List<string> layout, byte[] blend = null, int vi = 0)
    {
        var sb = new StringBuilder();
        foreach (var e in layout)
        {
            if (sb.Length > 0) sb.Append("   ");
            switch (e)
            {
                case "Position": sb.Append(F(v.Position.X)).Append(' ').Append(F(v.Position.Y)).Append(' ').Append(F(v.Position.Z)); break;
                case "Normal": sb.Append(F(v.Normal.X)).Append(' ').Append(F(v.Normal.Y)).Append(' ').Append(F(v.Normal.Z)); break;
                case "Colour0":
                    sb.Append(B(v.Colour.X)).Append(' ').Append(B(v.Colour.Y)).Append(' ').Append(B(v.Colour.Z)).Append(' ').Append(B(v.Colour.W)); break;
                case "Colour1":
                    sb.Append(v.Colour1 & 255).Append(' ').Append(v.Colour1 >> 8 & 255).Append(' ').Append(v.Colour1 >> 16 & 255).Append(' ').Append(v.Colour1 >> 24); break;
                case "BlendWeights":
                    if (blend != null) sb.Append(blend[vi * 8]).Append(' ').Append(blend[vi * 8 + 1]).Append(' ').Append(blend[vi * 8 + 2]).Append(' ').Append(blend[vi * 8 + 3]);
                    else sb.Append("255 0 0 0");
                    break;
                case "BlendIndices":
                    if (blend != null) sb.Append(blend[vi * 8 + 4]).Append(' ').Append(blend[vi * 8 + 5]).Append(' ').Append(blend[vi * 8 + 6]).Append(' ').Append(blend[vi * 8 + 7]);
                    else sb.Append("0 0 0 0");
                    break;
                case "Tangent": sb.Append(F(v.Tangent.X)).Append(' ').Append(F(v.Tangent.Y)).Append(' ').Append(F(v.Tangent.Z)).Append(' ').Append(F(v.Tangent.W)); break;
                default:
                    if (e.StartsWith("TexCoord")) sb.Append(F(v.UV.X)).Append(' ').Append(F(v.UV.Y));
                    else sb.Append("0 0 0"); // not produced by MoloMax shaders
                    break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ bounds

    const string Flags1 = "MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE";
    const string Flags2 = "VEHICLE_NOT_BVH, VEHICLE_BVH, PED, RAGDOLL, ANIMAL, ANIMAL_RAGDOLL, OBJECT, PLANT, PROJECTILE, EXPLOSION, FORKLIFT_FORKS, TEST_WEAPON, TEST_CAMERA, TEST_AI, TEST_SCRIPT, TEST_VEHICLE_WHEEL, GLASS";

    static void BoundHeader(Xw x, Vector3 mn, Vector3 mx, float margin, int unkType = 1)
    {
        // CodeWalker / the game store bound boxes grown by the margin (flat collision would get a zero quantum otherwise)
        mn -= new Vector3(margin); mx += new Vector3(margin);
        var c = (mn + mx) * 0.5f;
        x.Vec("BoxMin", mn); x.Vec("BoxMax", mx); x.Vec("BoxCenter", c); x.Vec("SphereCenter", c);
        x.Value("SphereRadius", (mx - mn).Length() * 0.5f);
        x.Value("Margin", margin); x.Value("Volume", 1f); x.Vec("Inertia", Vector3.One);
        x.Value("MaterialIndex", 0); x.Value("MaterialColourIndex", 0); x.Value("ProceduralID", 0); x.Value("RoomID", 0);
        x.Value("PedDensity", 0); x.Value("UnkFlags", 0); x.Value("PolyFlags", 0); x.Value("UnkType", unkType);
    }

    /// <param name="fragment">Fragment physics bounds: UnkType 2 and no composite flags, like the game's .yft files.</param>
    static void BoundsComposite(Xw x, CollisionMesh col, string tag, bool fragment = false)
    {
        int ut = fragment ? 2 : 1;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var ch in col.Children.Where(c => c.Tris.Count > 0)) { mn = Vector3.Min(mn, ch.Min); mx = Vector3.Max(mx, ch.Max); }
        x.Open(tag, "type=\"Composite\"");
        BoundHeader(x, mn - new Vector3(0.04f), mx + new Vector3(0.04f), 0, ut);
        x.Open("Children");
        foreach (var ch in col.Children)
        {
            if (ch.Tris.Count == 0) continue;
            x.Open("Item", "type=\"GeometryBVH\"");
            // CodeWalker stores vertices relative to the geometry centre
            var center = (ch.Min + ch.Max) * 0.5f;
            BoundHeader(x, ch.Min, ch.Max, 0.04f, ut);
            x.Lines("CompositeTransform", new[] { "1 0 0 0", "0 1 0 0", "0 0 1 0", "0 0 0 1" });
            if (!fragment)
            {
                x.Text("CompositeFlags1", Flags1);
                x.Text("CompositeFlags2", Flags2);
            }
            x.Vec("GeometryCenter", center);
            x.Value("UnkFloat1", 0f); x.Value("UnkFloat2", 0f);
            x.Open("Materials");
            foreach (var (type, room, flags) in ch.Materials)
            {
                x.Open("Item");
                x.Value("Type", type); x.Value("ProceduralID", 0); x.Value("RoomID", room); x.Value("PedDensity", 0);
                x.Text("Flags", CollisionFlags.ToXml(flags)); x.Value("MaterialColourIndex", 0); x.Value("Unk", 0);
                x.Close("Item");
            }
            x.Close("Materials");
            x.Lines("Vertices", ch.Verts.Select(v => { var p = v - center; return $"{F(p.X)}, {F(p.Y)}, {F(p.Z)}"; }));
            x.Lines("Polygons", ch.Tris.Select(t => $"<Triangle m=\"{t.Mat}\" v1=\"{t.A}\" v2=\"{t.B}\" v3=\"{t.C}\" f1=\"0\" f2=\"0\" f3=\"0\" />"));
            x.Close("Item");
        }
        x.Close("Children");
        x.Close(tag);
    }

    // ------------------------------------------------------------------ fragment (.yft)

    /// <summary>
    /// A single-body physical prop as a fragment (.yft.xml), organised like the game's own props and Sollumz fragments:
    /// the drawable gets a one-bone skeleton, the physics LOD has one group and one child bound to that bone,
    /// with the collision as a composite bound, the mass and a box inertia tensor.
    /// </summary>
    public static string Fragment(GtaModel model, TextureBank bank, bool embedTextures, CollisionMesh col, float mass, out List<ExportTexture> textures)
    {
        var keepCol = model.EmbeddedCollision; model.EmbeddedCollision = null;
        var drawable = Drawable(model, bank, embedTextures, out textures);
        model.EmbeddedCollision = keepCol;
        drawable = drawable[(drawable.IndexOf("?>") + 2)..].Trim('\n', '\r', ' ');
        drawable = drawable.Replace($"<Name>{model.Name}</Name>", "<Name>skel</Name>");
        string boneName = model.Name;
        string skeleton = $@"
 <Matrix>
  1 0 0
  0 1 0
  0 0 1
  0 0 0
 </Matrix>
 <Skeleton>
  <Unknown1C value=""16777216"" />
  <Unknown50 value=""{MetaXml.Jenkins(model.Name + "_skel")}"" />
  <Unknown54 value=""{MetaXml.Jenkins(model.Name + "_bones")}"" />
  <Unknown58 value=""{MetaXml.Jenkins(model.Name + "_tags")}"" />
  <Bones>
   <Item>
    <Name>{boneName}</Name>
    <Tag value=""0"" />
    <Index value=""0"" />
    <ParentIndex value=""-1"" />
    <SiblingIndex value=""-1"" />
    <Flags>RotX, RotY, RotZ, TransX, TransY, TransZ, Unk0</Flags>
    <Translation x=""0"" y=""0"" z=""0"" />
    <Rotation x=""0"" y=""0"" z=""0"" w=""1"" />
    <Scale x=""1"" y=""1"" z=""1"" />
    <TransformUnk x=""0"" y=""4"" z=""-3"" w=""0"" />
   </Item>
  </Bones>
 </Skeleton>
";
        int close = drawable.LastIndexOf("</Drawable>", StringComparison.Ordinal);
        drawable = drawable[..close] + skeleton.TrimStart('\n') + drawable[close..];

        // physics: box inertia for the whole mass around the collision centre
        mass = MathF.Max(0.01f, mass);
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var ch in col.Children.Where(c => c.Tris.Count > 0)) { mn = Vector3.Min(mn, ch.Min); mx = Vector3.Max(mx, ch.Max); }
        if (mn.X > mx.X) { mn = model.Min; mx = model.Max; }
        var size = Vector3.Max(mx - mn, new Vector3(0.01f));
        var com = (mn + mx) * 0.5f;
        var inertia = new Vector3(size.Y * size.Y + size.Z * size.Z, size.X * size.X + size.Z * size.Z, size.X * size.X + size.Y * size.Y) * (mass / 12f);
        var inv = new Vector3(1 / inertia.X, 1 / inertia.Y, 1 / inertia.Z);
        float maxI = MathF.Max(inertia.X, MathF.Max(inertia.Y, inertia.Z));

        var x = new Xw();
        x.Open("Fragment");
        x.Text("Name", "pack:/" + model.Name);
        x.Vec("BoundingSphereCenter", model.Center);
        x.Value("BoundingSphereRadius", model.Radius);
        x.Value("UnknownB0", 0); x.Value("UnknownB8", 0); x.Value("UnknownBC", 0);
        x.Value("UnknownC0", 65280); x.Value("UnknownC4", 1); x.Value("UnknownCC", 0f);
        x.Value("GravityFactor", 1f); x.Value("BuoyancyFactor", 1f);
        var body = new System.Text.StringBuilder();
        foreach (var line in drawable.Split('\n')) body.Append(' ').Append(line.TrimEnd('\r')).Append('\n');
        x.Raw(body.ToString());
        x.Open("BoneTransforms", "unk=\"1\"");
        x.Lines("Item", new[] { "1 0 0 0", "0 1 0 0", "0 0 1 0" });
        x.Close("BoneTransforms");
        x.Open("Physics");
        x.Open("LOD1");
        x.Value("Unknown14", maxI * 1e-4f); x.Value("Unknown18", maxI); x.Value("Unknown1C", -1f);
        x.Vec("PositionOffset", com); x.Vec("Unknown40", com); x.Vec("Unknown50", Vector3.Zero);
        x.Vec("DampingLinearC", new Vector3(0.02f)); x.Vec("DampingLinearV", new Vector3(0.02f)); x.Vec("DampingLinearV2", new Vector3(0.01f));
        x.Vec("DampingAngularC", new Vector3(0.02f)); x.Vec("DampingAngularV", new Vector3(0.02f)); x.Vec("DampingAngularV2", new Vector3(0.01f));
        x.Open("Archetype");
        x.Text("Name", model.Name);
        x.Value("Mass", mass); x.Value("MassInv", 1 / mass);
        x.Value("Unknown48", 1f); x.Value("Unknown4C", 150f); x.Value("Unknown50", 6.2831855f); x.Value("Unknown54", 1f);
        x.Vec("InertiaTensor", inertia); x.Vec("InertiaTensorInv", inv);
        BoundsComposite(x, col, "Bounds", fragment: true);
        x.Close("Archetype");
        x.Open("Transforms");
        int kids = Math.Max(1, col.Children.Count(c => c.Tris.Count > 0));
        for (int i = 0; i < kids; i++)
            x.Lines("Item", new[] { "1 0 0 0", "0 1 0 0", "0 0 1 0", $"{F(-com.X)} {F(-com.Y)} {F(-com.Z)} 0" });
        x.Close("Transforms");
        x.Open("Groups");
        x.Open("Item");
        x.Text("Name", model.Name);
        x.Value("ParentIndex", 255); x.Value("GlassWindowIndex", 0); x.Value("GlassFlags", 0); x.Value("Strength", -1f);
        x.Value("ForceTransmissionScaleUp", 0.25f); x.Value("ForceTransmissionScaleDown", 0.25f); x.Value("JointStiffness", 0f);
        x.Value("MinSoftAngle1", -1f); x.Value("MaxSoftAngle1", 1f); x.Value("MaxSoftAngle2", 1f); x.Value("MaxSoftAngle3", 1f);
        x.Value("RotationSpeed", 0f); x.Value("RotationStrength", 0f); x.Value("RestoringStrength", 0f); x.Value("RestoringMaxTorque", 0f);
        x.Value("LatchStrength", 0f); x.Value("Mass", mass); x.Value("MinDamageForce", 100f); x.Value("DamageHealth", 1000f);
        x.Value("UnkFloat5C", 0f); x.Value("UnkFloat60", 1f); x.Value("UnkFloat64", 0f); x.Value("UnkFloat68", 0f); x.Value("UnkFloat6C", 0f);
        x.Value("UnkFloat70", 1f); x.Value("UnkFloat74", 1f); x.Value("UnkFloat78", 1f); x.Value("UnkFloatA8", 1f);
        x.Close("Item");
        x.Close("Groups");
        x.Open("Children");
        for (int i = 0; i < kids; i++)
        {
            float m = mass / kids;
            x.Open("Item");
            x.Value("GroupIndex", 0); x.Value("BoneTag", 0);
            x.Value("PristineMass", m); x.Value("DamagedMass", m);
            x.Value("UnkFloat", 400f);
            x.Vec("UnkVec", Vector4.Zero);
            x.Vec("InertiaTensor", new Vector4(inertia / kids, 0));
            x.Empty("EventSet");
            x.Open("Drawable");
            x.Empty("Name");
            x.Lines("Matrix", new[] { "1 0 0", "0 1 0", "0 0 1", "0 0 0" });
            x.Vec("BoundingSphereCenter", Vector3.Zero); x.Value("BoundingSphereRadius", 0f);
            x.Vec("BoundingBoxMin", Vector3.Zero); x.Vec("BoundingBoxMax", Vector3.Zero);
            x.Value("LodDistHigh", 0f); x.Value("LodDistMed", 0f); x.Value("LodDistLow", 0f); x.Value("LodDistVlow", 0f);
            x.Value("FlagsHigh", 0); x.Value("FlagsMed", 0); x.Value("FlagsLow", 0); x.Value("FlagsVlow", 0);
            x.Close("Drawable");
            x.Close("Item");
        }
        x.Close("Children");
        x.Close("LOD1");
        x.Close("Physics");
        x.Empty("Lights");
        x.Close("Fragment");
        return x.ToString();
    }

    /// <summary>A standalone collision file (.ybn.xml).</summary>
    public static string BoundsFile(CollisionMesh col)
    {
        var x = new Xw();
        x.Open("BoundsFile");
        BoundsComposite(x, col, "Bounds");
        x.Close("BoundsFile");
        return x.ToString();
    }
}
