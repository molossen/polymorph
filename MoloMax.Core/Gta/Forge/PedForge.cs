using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Forge;

/// <summary>
/// Ped forge: a 3D character or animal becomes a FiveM add-on ped by cloning a game ped (the donor). The donor's
/// skeleton, ragdoll and animations are kept (its .yft); the model is skinned automatically (each vertex takes the bone
/// weights of the nearest point of the donor's body) and written as the ped's body (.ydd), with its texture (.ytd),
/// its variations (.ymt) and its peds.meta entry cloned from the donor.
/// </summary>
public static class PedForge
{
    public sealed record Donor(string Label, string Model, bool Animal);

    public static readonly Donor[] Donors =
    {
        new("Man (hipster)", "a_m_y_hipster_01", false),
        new("Woman (hipster)", "a_f_y_hipster_01", false),
        new("Man (business)", "a_m_y_business_01", false),
        new("Deer", "a_c_deer", true),
        new("Boar", "a_c_boar", true),
        new("Cow", "a_c_cow", true),
        new("Pig", "a_c_pig", true),
        new("Coyote", "a_c_coyote", true),
        new("Mountain lion", "a_c_mtlion_02", true),
        new("Dog (rottweiler)", "a_c_rottweiler_02", true),
        new("Dog (Chop)", "a_c_chop_02", true),
        new("Dog (pug)", "a_c_pug", true),
        new("Dog (poodle)", "a_c_poodle", true),
        new("Dog (westie)", "a_c_westy", true),
        new("Cat", "a_c_cat_01", true),
        new("Rabbit", "a_c_rabbit_01", true),
        new("Rat", "a_c_rat", true),
        new("Hen", "a_c_hen", true),
        new("Chimp", "a_c_chimp", true),
        new("Monkey (rhesus)", "a_c_rhesus", true),
    };

    static readonly string[] Comps = { "head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib" };

    /// <summary>The donor's first-variant body (all components, bind pose, model space) and its skinning.</summary>
    public sealed class Body
    {
        public string Model;
        public int BoneCount;
        public List<ForgeBone> Bones = new();
        public List<Vector3> Points = new();
        public List<int> Triangles = new();
        /// <summary>4 skeleton bone indices and 4 weights (0..1) per point.</summary>
        public List<int> Idx = new();
        public List<float> W = new();
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public byte[] Yft;
    }

    static Cw.YddFile FindYdd(GameFiles game, string model, out GameAsset asset)
    {
        asset = game.Models.FirstOrDefault(m => m.Ext.Equals(".ydd", StringComparison.OrdinalIgnoreCase) && m.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return null;
        var y = new Cw.YddFile(); y.Load(game.ModelBytes(asset));
        return y;
    }

    /// <summary>Reads the donor ped: skeleton (.yft) and the first variation of each component (.ydd) with its weights.</summary>
    public static Body LoadDonor(GameFiles game, string model)
    {
        model = ForgeCommon.SafeName(model);
        var yftAsset = game.Models.FirstOrDefault(m => m.Ext.Equals(".yft", StringComparison.OrdinalIgnoreCase) && m.Name.Equals(model, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(model + ".yft is not in the game files (not a ped?).");
        var yftBytes = game.ModelBytes(yftAsset);
        var yft = new Cw.YftFile(); yft.Load(yftBytes);
        var skel = yft.Fragment?.Drawable?.Skeleton ?? throw new InvalidDataException(model + ".yft has no skeleton.");
        var ydd = FindYdd(game, model, out _) ?? throw new FileNotFoundException(model + ".ydd is not in the game files.");
        var body = new Body { Model = model, Yft = yftBytes, Bones = ForgeCommon.Bones(skel) };
        body.BoneCount = body.Bones.Count;

        // first variant of each component: head_000_r, uppr_000_u ... (the dictionary keys are their hashes)
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Comps)
            foreach (var r in new[] { "u", "r" })
            {
                var n = $"{c}_000_{r}";
                wanted.Add(n); wanted.Add("hash_" + Cw.JenkHash.GenHash(n).ToString("X8"));
            }
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_peddonor");
        Directory.CreateDirectory(tmp);
        var doc = new XmlDocument(); doc.LoadXml(Cw.YddXml.GetXml(ydd, tmp));
        var items = doc.SelectNodes("/DrawableDictionary/Item").Cast<XmlElement>().ToList();
        var use = items.Where(i => wanted.Contains(i["Name"]?.InnerText?.Trim() ?? "")).ToList();
        if (use.Count == 0) use = items; // unknown naming: every drawable
        foreach (var d in use) ReadDrawable(d, body);
        if (body.Points.Count == 0) throw new InvalidDataException(model + ".ydd: no skinned body found.");
        return body;
    }

    static void ReadDrawable(XmlElement d, Body body)
    {
        var high = d.SelectSingleNode("DrawableModelsHigh") as XmlElement;
        if (high == null) return;
        foreach (XmlElement g in high.SelectNodes("Item/Geometries/Item"))
        {
            var ids = (g["BoneIDs"]?.InnerText ?? "").Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var layout = g.SelectSingleNode("VertexBuffer/Layout")?.ChildNodes.OfType<XmlElement>().Select(e => e.Name).ToList();
            var data = g.SelectSingleNode("VertexBuffer/Data")?.InnerText;
            if (layout == null || data == null) continue;
            int iPos = layout.IndexOf("Position"), iW = layout.IndexOf("BlendWeights"), iI = layout.IndexOf("BlendIndices");
            if (iPos < 0 || iW < 0 || iI < 0) continue;
            int baseIndex = body.Points.Count;
            foreach (var raw in data.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split("   ");
                if (parts.Length < layout.Count) continue;
                float[] N(int k) => parts[k].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                var p = N(iPos); var w = N(iW); var bi = N(iI);
                var pt = new Vector3(p[0], p[1], p[2]);
                body.Points.Add(pt);
                body.Min = Vector3.Min(body.Min, pt); body.Max = Vector3.Max(body.Max, pt);
                for (int k = 0; k < 4; k++)
                {
                    int slot = (int)bi[k];
                    int bone = ids.Length > 0 ? (slot < ids.Length ? ids[slot] : 0) : slot;
                    body.Idx.Add(w[k] > 0 ? bone : -1);
                    body.W.Add(w[k] / 255f);
                }
            }
            var ib = g.SelectSingleNode("IndexBuffer/Data")?.InnerText ?? "";
            foreach (var s in ib.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                body.Triangles.Add(baseIndex + int.Parse(s, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The donor's body as one mesh (for the orange ghost in the scene).</summary>
    public static PolyMesh GhostMesh(Body body)
    {
        var m = new PolyMesh();
        foreach (var p in body.Points) m.AddVertex(p);
        for (int t = 0; t + 2 < body.Triangles.Count; t += 3)
        {
            int a = body.Triangles[t], b = body.Triangles[t + 1], c = body.Triangles[t + 2];
            if (a == b || b == c || a == c || c >= body.Points.Count) continue;
            m.AddFace(new[] { a, b, c });
        }
        return m;
    }

    /// <summary>Fit: same height as the donor, feet on the donor's feet, centred on it (uniform scale).</summary>
    public static Matrix4x4 Fit(IReadOnlyList<Vector3> pts, Body body)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        float h = mx.Z - mn.Z, dh = body.Max.Z - body.Min.Z;
        float s = h > 1e-4f ? dh / h : 1f;
        var c = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
        var dc = new Vector3((body.Min.X + body.Max.X) / 2, (body.Min.Y + body.Max.Y) / 2, body.Min.Z);
        return Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(dc);
    }

    public sealed class Options
    {
        public Donor Donor;
        public string Name = "molo_ped";
    }

    public static ForgeReport Export(GameFiles game, Scene scene, IReadOnlyList<SceneObject> objects, Options o, string outputFolder,
                                     Func<string, RgbaImage> imageLoader, IEnumerable<string> textureFolders, Body body = null)
    {
        var rep = new ForgeReport();
        try
        {
            var name = ForgeCommon.SafeName(o.Name);
            if (string.IsNullOrEmpty(name)) name = "molo_ped";
            var res = Path.Combine(outputFolder, name);
            rep.Folder = res;
            var stream = Path.Combine(res, "stream");
            Directory.CreateDirectory(stream);
            body ??= LoadDonor(game, o.Donor.Model);

            // ---- the body (.ydd): our model, skinned on the donor's skeleton, in the head component
            var model = GtaModel.Build("head_000_r", scene, objects.Select(x => (x, x.World)));
            if (model.IsEmpty) { rep.Errors.Add("The selected objects have no geometry."); return rep; }
            var src = model.Parts.Select(p => p.Material).FirstOrDefault(m => !string.IsNullOrEmpty(m.DiffuseTexture)) ?? model.Parts[0].Material;
            if (model.Parts.Select(p => p.Material.DiffuseTexture).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                rep.Warnings.Add("Several textures: a ped body has one texture. The first one is used (bake them into one atlas for the right look).");
            var pedMat = new GtaMaterial { Id = int.MaxValue - 11, Name = "ped", Shader = "ped", DiffuseTexture = src.DiffuseTexture, BumpTexture = src.BumpTexture, SpecTexture = src.SpecTexture, ViewportColour = src.ViewportColour };
            foreach (var p in model.Parts) p.Material = pedMat;
            model.Skin = DonorSkin(body);
            // arms held differently from the donor (T-pose on an A-pose ped...): the donor is posed like the model for the
            // weights, then the model is brought back to the donor's pose (the pose the game's skeleton expects)
            var pose = MatchArms(body, model.Parts.SelectMany(p => p.Verts.Select(v => v.Position)).ToList(), rep);
            if (pose != null) model.Skin.Points = PosePoints(body, pose);
            model.Skin.Assign(model);
            if (pose != null) Unpose(model, pose);

            var bank = new TextureBank { ImageLoader = imageLoader };
            if (textureFolders != null) bank.SearchFolders.AddRange(textureFolders);
            var ytd = new List<ExportTexture>();
            void AddTex(ExportTexture t, string as_) { if (t != null && !ytd.Any(x => x.Name == as_)) ytd.Add(new ExportTexture { Name = as_, Dds = t.Dds, Kind = t.Kind, Width = t.Width, Height = t.Height, Mips = t.Mips, Format = t.Format }); }
            AddTex(bank.FromFile(pedMat.DiffuseTexture, TexKind.Diffuse) ?? bank.Placeholder(TexKind.Diffuse, pedMat.ViewportColour), "head_diff_000_a_uni");
            AddTex(bank.FromFile(pedMat.BumpTexture, TexKind.Normal) ?? bank.Placeholder(TexKind.Normal, default), "head_normal_000");
            AddTex(bank.FromFile(pedMat.SpecTexture, TexKind.Specular) ?? bank.Placeholder(TexKind.Specular, default), "head_spec_000");
            foreach (var w in bank.Warnings) rep.Warnings.Add(w);

            var dict = new XmlDocument();
            dict.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><DrawableDictionary/>");
            dict.DocumentElement.AppendChild(PedDrawable(dict, model, "head", "head_000_r", SkeletonXml(body)));
            // the upper and lower body stay as tiny hidden pieces, like the game's animals
            foreach (var comp in new[] { "uppr", "lowr" })
            {
                var stub = StubModel(comp + "_000_u", body, pedMat);
                dict.DocumentElement.AppendChild(PedDrawable(dict, stub, comp, comp + "_000_u", null));
                var px = bank.Placeholder(TexKind.Diffuse, new Vector3(0.5f, 0.5f, 0.5f));
                AddTex(px, comp + "_diff_000_a_uni");
                AddTex(bank.Placeholder(TexKind.Normal, default), comp + "_normal_000");
                AddTex(bank.Placeholder(TexKind.Specular, default), comp + "_spec_000");
            }
            var tmp = Path.Combine(Path.GetTempPath(), "polymorph_pedforge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var yddData = Cw.XmlYdd.GetYdd(dict, tmp).Save();
                var check = new Cw.YddFile(); check.Load(yddData);
                if (check.DrawableDict?.Drawables?.data_items == null || check.DrawableDict.Drawables.data_items.Length != 3) throw new InvalidDataException("the .ydd did not load back");
                File.WriteAllBytes(Path.Combine(stream, name + ".ydd"), yddData);
                rep.Files.Add($"stream/{name}.ydd");
                rep.Info.Add($"{name}.ydd: {model.VertexCount:N0} vertices, skinned on the {body.BoneCount} bones of {body.Model} (weights taken from its body)");

                // ---- textures (.ytd)
                foreach (var t in ytd) File.WriteAllBytes(Path.Combine(tmp, t.FileName), t.Dds);
                var tdoc = new XmlDocument(); tdoc.LoadXml(CwXml.TextureDictionary(ytd));
                var ytdData = Cw.XmlYtd.GetYtd(tdoc, tmp).Save();
                new Cw.YtdFile().Load(ytdData);
                File.WriteAllBytes(Path.Combine(stream, name + ".ytd"), ytdData);
                rep.Files.Add($"stream/{name}.ytd");

                // ---- variations (.ymt)
                var mdoc = new XmlDocument(); mdoc.LoadXml(Ymt());
                var ymtData = Cw.XmlMeta.GetData(mdoc, Cw.MetaFormat.RSC, tmp) ?? throw new InvalidDataException("CodeWalker could not build the .ymt"); // RSC: a PSO .ymt is ignored in a stream folder (grey low-poly ped)
                File.WriteAllBytes(Path.Combine(stream, name + ".ymt"), ymtData);
                rep.Files.Add($"stream/{name}.ymt");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }

            // ---- skeleton, ragdoll and collision: the donor's .yft under the new name
            File.WriteAllBytes(Path.Combine(stream, name + ".yft"), body.Yft);
            rep.Files.Add($"stream/{name}.yft");
            rep.Info.Add($"{name}.yft: skeleton and ragdoll of {body.Model}");

            // ---- peds.meta: the donor's entry, renamed
            var xp = $"//InitDatas/Item[translate(Name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{body.Model}']";
            // base game peds are in peds.ymt (binary), DLC peds in peds.meta
            var item = ForgeCommon.FindItem(game, "peds.ymt", body.Model, xp, out var from) ?? ForgeCommon.FindItem(game, "peds.meta", body.Model, xp, out from);
            var meta = new XmlDocument();
            meta.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CPedModelInfo__InitDataList><residentTxd>comp_peds_generic</residentTxd><residentAnims /><InitDatas /><txdRelationships /><multiTxdRelationships /></CPedModelInfo__InitDataList>");
            XmlElement it;
            if (item != null) it = (XmlElement)meta.ImportNode(item, true);
            else { rep.Warnings.Add($"{body.Model} not found in the game's peds.meta: a basic entry is written."); it = meta.CreateElement("Item"); }
            ForgeCommon.SetChild(it, "Name", name);
            meta.SelectSingleNode("//InitDatas").AppendChild(it);
            // names the game files only keep as hashes (hash_XXXXXXXX): known ones back to text, else the file goes binary
            int unknown = 0;
            foreach (var el in meta.SelectNodes("//*").Cast<XmlElement>().Where(e => !e.HasChildNodes || e.ChildNodes.Count == 1 && e.FirstChild is XmlText))
            {
                var t = el.InnerText.Trim();
                if (!t.StartsWith("hash_") || t.Length != 13 || !uint.TryParse(t[5..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) continue;
                var str = Cw.JenkIndex.TryGetString(h);
                if (!string.IsNullOrEmpty(str)) el.InnerText = str; else unknown++;
            }
            string metaFile = "peds.meta";
            if (unknown > 0)
            {
                // a text .meta would hand the game "hash_..." as names: the PSO keeps the exact hashes
                var tmp2 = Path.Combine(Path.GetTempPath(), "polymorph_pedmeta_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp2);
                try
                {
                    var pso = Cw.XmlMeta.GetData(meta, Cw.MetaFormat.PSO, tmp2) ?? throw new InvalidDataException("CodeWalker could not build peds.ymt");
                    metaFile = "peds.ymt";
                    File.WriteAllBytes(Path.Combine(res, metaFile), pso);
                }
                finally { try { Directory.Delete(tmp2, true); } catch { } }
                File.WriteAllText(Path.Combine(res, "peds_readable.xml"), ForgeCommon.Pretty(meta));
            }
            else File.WriteAllText(Path.Combine(res, metaFile), ForgeCommon.Pretty(meta));
            rep.Files.Add(metaFile);
            if (item != null) rep.Info.Add($"{metaFile}: {body.Model} cloned as {name} (from {from})" + (unknown > 0 ? $", binary because {unknown} name(s) are only known by hash (readable copy: peds_readable.xml)" : ""));

            File.WriteAllText(Path.Combine(res, "fxmanifest.lua"),
                "fx_version 'cerulean'\ngame 'gta5'\nlua54 'yes'\n\nauthor 'Molo Modding'\ndescription '" + name + " - ped made with Polymorph'\n\n" +
                "files { '" + metaFile + "' }\ndata_file 'PED_METADATA_FILE' '" + metaFile + "'\n\nclient_script 'client.lua'\n");
            File.WriteAllText(Path.Combine(res, "client.lua"),
                "-- /" + name + " : become the ped (test)\n" +
                "RegisterCommand('" + name + "', function()\n" +
                "  local m = `" + name + "`\n" +
                "  if not IsModelInCdimage(m) then print('" + name + ": model not found (is the resource started?)') return end\n" +
                "  RequestModel(m)\n  while not HasModelLoaded(m) do Wait(10) end\n" +
                "  SetPlayerModel(PlayerId(), m)\n  SetPedDefaultComponentVariation(PlayerPedId())\n  SetModelAsNoLongerNeeded(m)\nend)\n");
            rep.Files.Add("fxmanifest.lua"); rep.Files.Add("client.lua");
            rep.Info.Add($"In game: ensure {name}, then /{name} (or spawn the ped '{name}').");
        }
        catch (Exception e) { rep.Errors.Add(e.Message); }
        return rep;
    }

    /// <summary>Per bone: the transform taking the donor's pose to the model's (identity for most bones), or null when the
    /// arms already match (within 8 degrees) or the donor has no human arms.</summary>
    static Matrix4x4[] MatchArms(Body body, List<Vector3> pts, ForgeReport rep)
    {
        var pose = Enumerable.Repeat(Matrix4x4.Identity, body.BoneCount).ToArray();
        bool any = false;
        foreach (var side in new[] { "L", "R" })
        {
            var upper = body.Bones.FirstOrDefault(b => b.Name == $"SKEL_{side}_UpperArm");
            var hand = body.Bones.FirstOrDefault(b => b.Name == $"SKEL_{side}_Hand");
            if (upper == null || hand == null) continue;
            var sh = upper.Position;
            var dDir = hand.Position - sh; float dLen = dDir.Length();
            if (dLen < 1e-3f) continue;
            dDir /= dLen;
            float sx = MathF.Sign(sh.X); if (sx == 0) continue;
            // the model's hand on that side: the farthest point beyond the shoulder, among points at least as far out
            Vector3 best = default; float bd = 0;
            foreach (var p in pts)
            {
                if ((p.X - sh.X) * sx <= 0.05f) continue;
                float d = Vector3.Distance(p, sh);
                if (d > bd && d < dLen * 2.2f) { bd = d; best = p; }
            }
            if (bd < dLen * 0.6f) continue;
            var uDir = Vector3.Normalize(best - sh);
            float ang = MathF.Acos(Math.Clamp(Vector3.Dot(dDir, uDir), -1, 1));
            if (ang < 8 * MathF.PI / 180) continue;
            var axis = Vector3.Cross(dDir, uDir);
            if (axis.LengthSquared() < 1e-8f) continue;
            var m = Matrix4x4.CreateTranslation(-sh) * Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), ang) * Matrix4x4.CreateTranslation(sh);
            // the upper arm and every bone under it
            for (int i = 0; i < body.BoneCount; i++)
            {
                int b = i, guard = 0;
                while (b >= 0 && guard++ < 256) { if (b == upper.Index) { pose[i] = m; break; } b = body.Bones[b].Parent; }
            }
            any = true;
            rep.Info.Add($"{side} arm: yours is {ang * 180 / MathF.PI:0} deg away from the donor's, brought to its pose automatically");
        }
        return any ? pose : null;
    }

    /// <summary>The donor's body points in the model's pose (linear blend of the bone transforms).</summary>
    static Vector3[] PosePoints(Body body, Matrix4x4[] pose)
    {
        var res = new Vector3[body.Points.Count];
        for (int i = 0; i < res.Length; i++)
        {
            Vector3 acc = Vector3.Zero; float ws = 0;
            for (int k = 0; k < 4; k++)
            {
                int b = body.Idx[i * 4 + k]; float w = body.W[i * 4 + k];
                if (b < 0 || w <= 0 || b >= pose.Length) continue;
                acc += Vector3.Transform(body.Points[i], pose[b]) * w; ws += w;
            }
            res[i] = ws > 0 ? acc / ws : body.Points[i];
        }
        return res;
    }

    /// <summary>Brings the model from its pose back to the donor's: each vertex by the inverse of its bones' transforms.</summary>
    static void Unpose(GtaModel model, Matrix4x4[] pose)
    {
        var inv = pose.Select(m => Matrix4x4.Invert(m, out var i) ? i : Matrix4x4.Identity).ToArray();
        model.Min = new Vector3(float.MaxValue); model.Max = new Vector3(float.MinValue);
        foreach (var part in model.Parts)
            for (int v = 0; v < part.Verts.Count; v++)
            {
                var rv = part.Verts[v];
                Vector3 p = Vector3.Zero, n = Vector3.Zero, t = Vector3.Zero; float ws = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = part.Blend[v * 8 + k] / 255f; int b = part.Blend[v * 8 + 4 + k];
                    if (w <= 0 || b >= inv.Length) continue;
                    p += Vector3.Transform(rv.Position, inv[b]) * w;
                    n += Vector3.TransformNormal(rv.Normal, inv[b]) * w;
                    t += Vector3.TransformNormal(new Vector3(rv.Tangent.X, rv.Tangent.Y, rv.Tangent.Z), inv[b]) * w;
                    ws += w;
                }
                if (ws > 0)
                {
                    rv.Position = p / ws;
                    if (n.LengthSquared() > 1e-12f) rv.Normal = Vector3.Normalize(n);
                    if (t.LengthSquared() > 1e-12f) { var tn = Vector3.Normalize(t); rv.Tangent = new Vector4(tn, rv.Tangent.W); }
                    part.Verts[v] = rv;
                }
                model.Min = Vector3.Min(model.Min, rv.Position); model.Max = Vector3.Max(model.Max, rv.Position);
            }
    }

    /// <summary>Weights source: every point of the donor's body with its bones (the export skeleton is the donor's, stripped later).</summary>
    static ModelSkin DonorSkin(Body body)
    {
        var skin = new ModelSkin();
        for (int i = 0; i < body.BoneCount; i++) skin.Bones.Add(new SkinBone { Name = body.Bones[i].Name, Tag = body.Bones[i].Tag, Parent = body.Bones[i].Parent });
        skin.Points = body.Points.ToArray();
        skin.Idx = body.Idx.ToArray();
        skin.W = body.W.ToArray();
        return skin;
    }

    /// <summary>A 1 mm triangle inside the body (a component the game expects, not seen).</summary>
    static GtaModel StubModel(string name, Body body, GtaMaterial mat)
    {
        var m = new GtaModel { Name = name };
        var c = (body.Min + body.Max) * 0.5f;
        var part = new ModelPart { Material = mat };
        foreach (var d in new[] { Vector3.Zero, new Vector3(0.001f, 0, 0), new Vector3(0, 0.001f, 0) })
            part.Verts.Add(new RenderVertex { Position = c + d, Normal = Vector3.UnitZ, Colour = Vector4.One, Tangent = new Vector4(1, 0, 0, 1) });
        part.Indices.AddRange(new[] { 0, 1, 2 });
        m.Parts.Add(part);
        m.Min = c; m.Max = c + new Vector3(0.001f);
        m.Skin = new ModelSkin { Bones = Enumerable.Range(0, body.BoneCount).Select(i => new SkinBone { Name = "b" + i, Parent = i == 0 ? -1 : 0 }).ToList() };
        part.Blend = new byte[3 * 8];
        for (int v = 0; v < 3; v++) part.Blend[v * 8] = 255; // all on bone 0 (root)
        return m;
    }

    /// <summary>A model as a ped dictionary item: our CodeWalker drawable, without skeleton (the .yft has it), ped shader
    /// with the texture names the game looks for (comp_diff_000_a_uni...).</summary>
    static XmlElement PedDrawable(XmlDocument dict, GtaModel model, string comp, string drawableName, XmlElement skeleton)
    {
        var bank = new TextureBank(); // textures are named below and go to the .ytd
        var xml = CwXml.Drawable(model, bank, false, out _);
        var d = new XmlDocument(); d.LoadXml(xml);
        var root = d.DocumentElement;
        if (root["Skeleton"] is { } sk) root.RemoveChild(sk);
        if (root["Joints"] is { } jt) root.RemoveChild(jt);
        if (root["Name"] is { } nm) nm.InnerText = drawableName;
        // one ped shader with the conventional texture names
        var sg = root["ShaderGroup"];
        if (sg != null) root.RemoveChild(sg);
        var sgx = new XmlDocument();
        sgx.LoadXml(PedShaderGroup(comp));
        var newSg = d.ImportNode(sgx.DocumentElement, true);
        var after = root["FlagsVlow"];
        if (after != null) root.InsertAfter(newSg, after); else root.PrependChild(newSg);
        foreach (XmlElement si in root.SelectNodes("//ShaderIndex")) si.SetAttribute("value", "0");
        // the game needs the skeleton in the head drawable too (CreatePed fails without it): the donor's, as in the .yft
        if (skeleton != null) root.InsertAfter(d.ImportNode(skeleton, true), root["ShaderGroup"]);
        var item = dict.CreateElement("Item");
        foreach (XmlNode c in root.ChildNodes) item.AppendChild(dict.ImportNode(c, true));
        return item;
    }

    static string PedShaderGroup(string comp) =>
$@"<ShaderGroup>
 <Shaders>
  <Item>
   <Name>ped</Name>
   <FileName>ped.sps</FileName>
   <RenderBucket value=""0"" />
   <Parameters>
    <Item name=""DiffuseSampler"" type=""Texture""><Name>{comp}_diff_000_a_uni</Name></Item>
    <Item name=""TextureSamplerDiffPal"" type=""Texture"" />
    <Item name=""VolumeSampler"" type=""Texture""><Name>givemechecker</Name></Item>
    <Item name=""BumpSampler"" type=""Texture""><Name>{comp}_normal_000</Name></Item>
    <Item name=""SpecSampler"" type=""Texture""><Name>{comp}_spec_000</Name></Item>
    <Item name=""umGlobalParams"" type=""Vector"" x=""0"" y=""0"" z=""7"" w=""7"" />
    <Item name=""envEffFatThickness"" type=""Vector"" x=""25"" y=""25"" z=""0"" w=""0"" />
    <Item name=""specularIntensityMult"" type=""Vector"" x=""1"" y=""0"" z=""0"" w=""0"" />
    <Item name=""specularFalloffMult"" type=""Vector"" x=""250"" y=""0"" z=""0"" w=""0"" />
    <Item name=""specularFresnel"" type=""Vector"" x=""0.96"" y=""0"" z=""0"" w=""0"" />
    <Item name=""bumpiness"" type=""Vector"" x=""1"" y=""0"" z=""0"" w=""0"" />
    <Item name=""detailSettings"" type=""Vector"" x=""0.4"" y=""0.4"" z=""41"" w=""0"" />
    <Item name=""StubbleControl"" type=""Vector"" x=""2"" y=""0.6"" z=""0"" w=""0"" />
   </Parameters>
  </Item>
 </Shaders>
</ShaderGroup>";

    /// <summary>Drawables in a .ydd and how many are skinned (checks).</summary>
    public static (int Drawables, int Skinned) YddInfo(byte[] data)
    {
        var y = new Cw.YddFile(); y.Load(data);
        var items = y.DrawableDict?.Drawables?.data_items ?? Array.Empty<Cw.Drawable>();
        return (items.Length, items.Count(x => x.DrawableModels?.High?.Any(m => m.HasSkin > 0) == true));
    }

    /// <summary>The donor's skeleton as CodeWalker XML (taken from its .yft).</summary>
    static XmlElement SkeletonXml(Body body)
    {
        var yft = new Cw.YftFile(); yft.Load(body.Yft);
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_pedskel");
        Directory.CreateDirectory(tmp);
        var doc = new XmlDocument(); doc.LoadXml(Cw.YftXml.GetXml(yft, tmp));
        return doc.SelectSingleNode("/Fragment/Drawable/Skeleton") as XmlElement ?? throw new InvalidDataException(body.Model + ".yft: no skeleton");
    }

    /// <summary>A .ymt read back as CodeWalker XML (checks).</summary>
    public static string YmtXml(byte[] data)
    {
        var y = new Cw.YmtFile(); Cw.RpfFile.LoadResourceFile(y, data, 2);
        return Cw.MetaXml.GetXml(y, out _) ?? "";
    }

    /// <summary>Variations: head (our body), upper and lower body (hidden stubs), one drawable and one texture each.</summary>
    static string Ymt()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CPedVariationInfo>\n <bHasTexVariations value=\"false\" />\n <bHasDrawblVariations value=\"false\" />\n <bHasLowLODs value=\"false\" />\n <bIsSuperLOD value=\"false\" />\n");
        sb.Append(" <availComp>0 255 255 1 2 255 255 255 255 255 255 255</availComp>\n <aComponentData3 itemType=\"CPVComponentData\">\n");
        for (int i = 0; i < 3; i++)
            sb.Append("  <Item>\n   <numAvailTex value=\"1\" />\n   <aDrawblData3 itemType=\"CPVDrawblData\">\n    <Item>\n     <propMask value=\"1\" />\n     <numAlternatives value=\"0\" />\n" +
                      "     <aTexData itemType=\"CPVTextureData\">\n      <Item>\n       <texId value=\"0\" />\n       <distribution value=\"255\" />\n      </Item>\n     </aTexData>\n" +
                      "     <clothData>\n      <ownsCloth value=\"false\" />\n     </clothData>\n    </Item>\n   </aDrawblData3>\n  </Item>\n");
        sb.Append(" </aComponentData3>\n <aSelectionSets itemType=\"CPedSelectionSet\" />\n <compInfos itemType=\"CComponentInfo\">\n");
        foreach (var c in new[] { 0, 3, 4 })
            sb.Append("  <Item>\n   <pedXml_audioID>none</pedXml_audioID>\n   <pedXml_audioID2>none</pedXml_audioID2>\n   <pedXml_expressionMods>0 0 0 0 0</pedXml_expressionMods>\n" +
                      "   <flags value=\"0\" />\n   <inclusions>0</inclusions>\n   <exclusions>0</exclusions>\n   <pedXml_vfxComps>PV_COMP_HEAD</pedXml_vfxComps>\n" +
                      $"   <pedXml_flags value=\"0\" />\n   <pedXml_compIdx value=\"{c}\" />\n   <pedXml_drawblIdx value=\"0\" />\n  </Item>\n");
        sb.Append(" </compInfos>\n <propInfo>\n  <numAvailProps value=\"0\" />\n  <aPropMetaData itemType=\"CPedPropMetaData\" />\n  <aAnchors itemType=\"CAnchorProps\" />\n </propInfo>\n <dlcName />\n</CPedVariationInfo>\n");
        return sb.ToString();
    }
}
