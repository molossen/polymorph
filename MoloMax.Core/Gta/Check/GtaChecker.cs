using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Library;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Gta.Check;

public enum CheckLevel { Error, Warning, Info }

/// <summary>One finding. <see cref="Fix"/> (when set) repairs it on the scene and returns a short report.</summary>
public sealed class CheckIssue
{
    public CheckLevel Level;
    public string Message;
    public int ObjectId = -1;
    public string FixLabel;
    public Func<string> Fix;
    public string Group;
}

/// <summary>
/// Molo Modding's GTA rules, checked before export, most with a one-click fix:
/// shell in limbo, collision on the shell (RoomID per room), DDS 1024 with mipmaps,
/// shared pivot, vertex colours (green inside, white emissive, red exterior), closed inward rooms, names.
/// </summary>
public static class GtaChecker
{
    public const int MaxVerts = 60000;
    /// <summary>Above this a model gets an information note (never a warning or an error).</summary>
    public const int HeavyVerts = 500000;

    public static List<CheckIssue> Run(Scene scene)
    {
        var res = new List<CheckIssue>();
        var st = scene.Gta;
        var objs = scene.Objects.Where(o => !o.Hidden && o.Gta.Role != GtaRole.Ignore).ToList();
        var rooms = objs.Where(o => o.GtaType == GtaObjectType.MloRoom).ToList();
        MloLayout layout = null;
        try { if (rooms.Count > 0) layout = MloLayout.Build(scene, st); } catch { }
        bool isMlo = layout != null && layout.Rooms.Count > 1;

        // ---- project
        if (isMlo && !st.ShellInLimbo)
            res.Add(new CheckIssue { Level = CheckLevel.Error, Group = "MLO", Message = "The shell is not in the limbo room: the interior can crash.", FixLabel = "Put shell in limbo", Fix = () => { st.ShellInLimbo = true; return "Shell goes in limbo."; } });
        if (!st.SharedPivot)
            res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Project", Message = "Models keep their own pivot: they will not import aligned in Blender.", FixLabel = "Use one shared pivot", Fix = () => { st.SharedPivot = true; return "All models share the scene origin."; } });
        if (st.MaxTextureSize > 1024)
            res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Textures", Message = $"Textures are exported up to {st.MaxTextureSize} px (rule: 1024).", FixLabel = "Set 1024", Fix = () => { st.MaxTextureSize = 1024; return "Max texture size 1024."; } });
        if (string.IsNullOrWhiteSpace(st.ResourceName) || st.ResourceName.Any(c => !(char.IsLetterOrDigit(c) && char.IsLower(c) || char.IsDigit(c) || c == '_')))
            res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Project", Message = $"Resource name '{st.ResourceName}': use lowercase letters, digits and _.", FixLabel = "Clean name", Fix = () => { st.ResourceName = Clean(st.ResourceName, "molo_new_map"); return "Resource: " + st.ResourceName; } });

        // ---- rooms / MLO
        foreach (var r in rooms)
        {
            if (!r.Mesh.IsClosedManifold(out var why) && !OpenOnlyAtPortals(r, layout, ref why))
                res.Add(new CheckIssue { Level = CheckLevel.Error, Group = "MLO", ObjectId = r.Id, Message = $"{r.Name}: room is not a closed cube ({why})." });
            if (Volume(r.Mesh) > 0) res.Add(new CheckIssue { Level = CheckLevel.Error, Group = "MLO", ObjectId = r.Id, Message = $"{r.Name}: room normals face outward (they must face inward).", FixLabel = "Flip inward", Fix = () => { MeshOps.FlipAll(r.Mesh); r.Touch(); return r.Name + " flipped."; } });
        }
        for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var (a0, a1) = rooms[i].WorldBounds(); var (b0, b1) = rooms[j].WorldBounds();
                if (a0.X < b1.X && a1.X > b0.X && a0.Y < b1.Y && a1.Y > b0.Y && a0.Z < b1.Z && a1.Z > b0.Z)
                    res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "MLO", ObjectId = rooms[j].Id, Message = $"{rooms[i].Name} and {rooms[j].Name} overlap: leave a small gap between rooms (joined by a floor)." });
            }
        if (layout != null) foreach (var w in layout.Warnings) res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "MLO", Message = w });
        if (isMlo) res.Add(new CheckIssue { Level = CheckLevel.Info, Group = "MLO", Message = $"MLO: {layout.Rooms.Count - 1} room(s), {layout.Portals.Count} portal(s), {layout.ExitPortals} to the outside. Collision is exported on the shell with RoomID per room (1 for a one-room MLO)." });

        // ---- objects
        foreach (var o in objs)
        {
            if (o.Name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')) || o.Name != o.Name.ToLowerInvariant())
            {
                var obj = o;
                res.Add(new CheckIssue { Level = CheckLevel.Info, Group = "Names", ObjectId = o.Id, Message = $"{o.Name}: names are exported lowercase with letters, digits and _ only.", FixLabel = "Clean name", Fix = () => { obj.Name = Clean(obj.Name, "object"); return "Renamed " + obj.Name; } });
            }
            if (o.GtaType is GtaObjectType.Collision or GtaObjectType.MloRoom or GtaObjectType.Portal && (o.Scale != Vector3.One || o.Rotation != Quaternion.Identity))
            {
                var obj = o;
                res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Objects", ObjectId = o.Id, Message = $"{o.Name}: rotation / scale not applied.", FixLabel = "Reset XForm", Fix = () => { obj.ResetXForm(); obj.Touch(); return obj.Name + ": transform applied."; } });
            }
            if (o.GtaType != GtaObjectType.Drawable || o.Mesh.Faces.Count == 0) continue;
            int vc = RenderVertexCount(o.Mesh);
            // no vertex cap: the export cuts every material into 65k-vertex geometries by itself (planets, terrains...);
            // a very heavy model only gets a note, splitting it is a streaming choice
            if (vc > HeavyVerts)
            {
                var obj = o;
                res.Add(new CheckIssue { Level = CheckLevel.Info, Group = "Models", ObjectId = o.Id, Message = $"{o.Name}: {vc:N0} vertices. Exported fine (cut into 65k pieces automatically); split it only if you want it to stream in parts.", FixLabel = "Split", Fix = () => SplitObject(scene, obj) });
            }
            var want = WantedColours(scene, o, layout, isMlo, st.PortalMaxGap, out string rule);
            if (want != null && !ColoursOk(o.Mesh, want))
            {
                var obj = o; var w = want;
                res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Vertex colours", ObjectId = o.Id, Message = $"{o.Name}: vertex colours should be {rule}.", FixLabel = "Fix colours", Fix = () => { ApplyColours(obj.Mesh, w); obj.Touch(); return obj.Name + ": " + rule; } });
            }
            if (o.Mesh.Faces.Count <= 2 && !o.Mesh.IsClosedManifold(out _))
                res.Add(new CheckIssue { Level = CheckLevel.Info, Group = "Models", ObjectId = o.Id, Message = $"{o.Name}: single faces are seen from one side only in game. Give it thickness." });
        }

        // ---- textures (DDS 1024, mipmaps, power of two)
        var used = objs.Where(o => o.GtaType == GtaObjectType.Drawable).SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)).Distinct().ToHashSet();
        foreach (var m in scene.Materials.Where(m => used.Contains(m.Id)))
            foreach (var (slot, path) in new[] { ("diffuse", m.DiffuseTexture), ("normal", m.BumpTexture), ("spec", m.SpecTexture) })
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path)) { res.Add(new CheckIssue { Level = CheckLevel.Error, Group = "Textures", Message = $"{m.Name}: {slot} texture not found ({Path.GetFileName(path)})." }); continue; }
                if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue; // PNG / JPG are converted at export (1024, mips)
                byte[] head;
                try { using var fs = File.OpenRead(path); head = new byte[148]; fs.Read(head, 0, head.Length); } catch { continue; }
                if (!TextureBank.DdsInfo(head, out int w, out int h, out int mips, out string fmt)) continue;
                var probs = new List<string>();
                if (Math.Max(w, h) > 1024) probs.Add($"{w}x{h} (max 1024)");
                if (mips <= 1) probs.Add("no mipmaps");
                if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0) probs.Add("not a power of two");
                if (probs.Count == 0) continue;
                var p = path;
                res.Add(new CheckIssue { Level = CheckLevel.Warning, Group = "Textures", Message = $"{Path.GetFileName(path)}: {string.Join(", ", probs)}.", FixLabel = "Convert to DDS 1024 + mips", Fix = () => ConvertDds(p) });
            }

        if (res.All(r => r.Level == CheckLevel.Info)) res.Insert(0, new CheckIssue { Level = CheckLevel.Info, Group = "Result", Message = "Ready for GTA: no error found." });
        return res;
    }

    /// <summary>Runs every fix, checking again between passes (a split makes new models that then get their colours).</summary>
    /// <summary>
    /// A room may be open where a door goes through (the hole sits on a portal): only edges elsewhere are a problem.
    /// </summary>
    static bool OpenOnlyAtPortals(SceneObject r, MloLayout layout, ref string why)
    {
        if (layout == null || layout.Portals.Count == 0) return false;
        var m = r.Mesh; var w = r.World;
        var dir = new Dictionary<(int, int), int>();
        foreach (var f in m.Faces)
            for (int i = 0; i < f.V.Length; i++) { var k = (f.V[i], f.V[(i + 1) % f.V.Length]); dir[k] = dir.TryGetValue(k, out var c) ? c + 1 : 1; }
        var boxes = layout.Portals.Select(p =>
        {
            var mn = p.Corners.Aggregate(Vector3.Min); var mx = p.Corners.Aggregate(Vector3.Max);
            var ext = mx - mn; int ax = ext.X <= ext.Y && ext.X <= ext.Z ? 0 : ext.Y <= ext.Z ? 1 : 2;
            var pad = new Vector3(ax == 0 ? 0.6f : 0.1f, ax == 1 ? 0.6f : 0.1f, ax == 2 ? 0.6f : 0.1f);
            return (mn - pad, mx + pad);
        }).ToList();
        foreach (var kv in dir)
        {
            if (kv.Value > 1) return false; // a real problem, not a doorway
            if (dir.ContainsKey((kv.Key.Item2, kv.Key.Item1))) continue;
            var mid = Vector3.Transform((m.Verts[kv.Key.Item1] + m.Verts[kv.Key.Item2]) * 0.5f, w);
            if (!boxes.Any(b => mid.X >= b.Item1.X && mid.X <= b.Item2.X && mid.Y >= b.Item1.Y && mid.Y <= b.Item2.Y && mid.Z >= b.Item1.Z && mid.Z <= b.Item2.Z))
            { why = $"open edge {kv.Key} away from any door / portal"; return false; }
        }
        return true;
    }

    public static List<string> FixAll(Scene scene)
    {
        var done = new List<string>();
        for (int pass = 0; pass < 4; pass++)
        {
            var issues = Run(scene).Where(i => i.Fix != null).ToList();
            if (issues.Count == 0) break;
            // splits first (they replace meshes the other fixes would touch), then everything else on the next pass
            var splits = issues.Where(i => i.FixLabel == "Split at 60k").ToList();
            foreach (var i in splits.Count > 0 ? splits : issues)
                try { done.Add(i.Fix()); } catch (Exception e) { done.Add("Failed: " + e.Message); }
        }
        return done;
    }

    // ------------------------------------------------------------------ vertex colours

    /// <summary>
    /// Per-face wanted colour: white for emissive materials, red for exterior props (natural ambient: sky light),
    /// green inside an MLO (artificial ambient). Null when nothing can be decided.
    /// </summary>
    public static Vector4[] WantedColours(Scene scene, SceneObject o, MloLayout layout, bool isMlo, float gap, out string rule)
    {
        var root = scene.Root(o);
        var role = root.GtaType == GtaObjectType.Model ? (root.Gta.Role == GtaRole.Auto ? GtaRole.Prop : root.Gta.Role) : GtaExporter.ResolveRole(o, layout, gap, scene);
        var (mn, mx) = o.WorldBounds();
        bool inside = isMlo && (role == GtaRole.Shell || GtaExporter.InsideMlo(layout, (mn + mx) * 0.5f, gap));
        var baseCol = inside ? Green : Red;
        rule = inside ? "green (interior), white on emissive" : "red (exterior), white on emissive";
        var res = new Vector4[o.Mesh.Faces.Count];
        for (int i = 0; i < res.Length; i++) res[i] = scene.Material(o.Mesh.Faces[i].MatId).Preset.Emissive ? White : baseCol;
        return res;
    }

    public static readonly Vector4 Green = new(0, 1, 0, 1), Red = new(1, 0, 0, 1), White = new(1, 1, 1, 1);

    static bool ColoursOk(PolyMesh m, Vector4[] want)
    {
        for (int i = 0; i < m.Faces.Count; i++)
        {
            var w = want[i];
            foreach (var c in m.Faces[i].Col)
            {
                if (w == White) { if (c.X < 0.85f || c.Y < 0.85f || c.Z < 0.85f) return false; }
                else if (w == Green) { if (c.Y < 0.2f || c.X > 0.35f || c.Z > 0.35f) return false; }
                else if (c.X < 0.5f || c.Y > 0.35f) return false;
            }
        }
        return true;
    }

    public static void ApplyColours(PolyMesh m, Vector4[] want)
    {
        for (int i = 0; i < m.Faces.Count; i++)
        {
            var f = m.Faces[i];
            for (int c = 0; c < f.Col.Length; c++)
            {
                // keep a darker green / red that was painted on purpose (dark green is fine), force the hue
                var old = f.Col[c]; var w = want[i];
                if (w == Green && old.Y > 0.2f && old.X <= 0.35f && old.Z <= 0.35f) continue;
                if (w == Red && old.X > 0.5f && old.Y <= 0.35f) continue;
                f.Col[c] = new Vector4(w.X, w.Y, w.Z, old.W);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Vertices the game will get: one per distinct (position, uv, colour, normal group) corner.</summary>
    public static int RenderVertexCount(PolyMesh m)
    {
        var set = new HashSet<(int, Vector2, uint)>();
        foreach (var f in m.Faces) for (int i = 0; i < f.V.Length; i++) set.Add((f.V[i], f.UV[i], f.Smooth == 0 ? (uint)f.GetHashCode() : f.Smooth));
        return set.Count;
    }

    static string SplitObject(Scene scene, SceneObject o)
    {
        var parts = MeshOps.SplitByVertexBudget(o.Mesh, MaxVerts - 2000);
        if (parts.Count <= 1) return o.Name + ": nothing to split.";
        o.Mesh = parts[0]; o.Touch();
        var parent = scene.Parent(o);
        for (int i = 1; i < parts.Count; i++)
        {
            var name = $"{o.Name}_{i + 1:00}";
            var n = scene.Add(name, parts[i], o.Position, o.GtaType);
            if (!scene.Objects.Any(x => x != n && x.Name == name)) n.Name = name;
            n.Rotation = o.Rotation; n.Scale = o.Scale; n.Gta = o.Gta.Clone(); n.Folder = o.Folder;
            if (parent != null) scene.SetParent(n, parent);
        }
        return $"{o.Name}: split into {parts.Count} models.";
    }

    static string ConvertDds(string path)
    {
        var img = TextureReader.Load(path, 1024) ?? throw new InvalidDataException("cannot read " + Path.GetFileName(path));
        bool alpha = false;
        for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] < 250) { alpha = true; break; }
        var dds = DdsEncoder.Encode(img, alpha ? DdsFormat.Dxt5 : DdsFormat.Dxt1, 1024, true);
        var orig = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".orig.dds");
        if (!File.Exists(orig)) File.Copy(path, orig);
        File.WriteAllBytes(path, dds);
        return $"{Path.GetFileName(path)}: DDS {(alpha ? "DXT5" : "DXT1")} 1024 with mipmaps (original kept as .orig.dds).";
    }

    static string Clean(string s, string fallback)
    {
        var c = new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        while (c.Contains("__")) c = c.Replace("__", "_");
        return c.Length == 0 ? fallback : c;
    }

    static float Volume(PolyMesh m)
    {
        float v = 0;
        foreach (var f in m.Faces)
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                v += Vector3.Dot(m.Verts[f.V[a]], Vector3.Cross(m.Verts[f.V[b]], m.Verts[f.V[c]])) / 6f;
        return v;
    }
}
