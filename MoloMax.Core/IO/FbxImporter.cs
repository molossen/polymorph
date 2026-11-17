using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.IO;

/// <summary>
/// FBX reader (binary 6100-7700 and ASCII): meshes with their n-gons, UVs, vertex colours and material
/// slots, the node hierarchy (Lcl translation / rotation / scaling, pre / post rotation, geometric offsets), the file's
/// unit and up axis, materials with their diffuse / normal / specular textures (files next to the model, or the textures
/// embedded in the FBX). Skinned meshes come in their bind pose (the skeleton is not imported yet).
/// </summary>
public static class FbxImporter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- node tree (both formats)
    public sealed class Node
    {
        public string Name;
        public readonly List<object> Props = new();
        public readonly List<Node> Children = new();
        public Node Child(string n) => Children.FirstOrDefault(c => c.Name == n);
        public IEnumerable<Node> All(string n) => Children.Where(c => c.Name == n);
        public override string ToString() => Name;
    }

    public static Node Parse(byte[] data)
    {
        const string magic = "Kaydara FBX Binary";
        if (data.Length > 27 && Encoding.ASCII.GetString(data, 0, magic.Length) == magic) return ParseBinary(data);
        return ParseAscii(Encoding.UTF8.GetString(data));
    }

    // ---------------------------------------------------------------- binary
    static Node ParseBinary(byte[] d)
    {
        uint version = BitConverter.ToUInt32(d, 23);
        bool wide = version >= 7500;
        var root = new Node { Name = "" };
        int pos = 27;
        while (pos < d.Length)
        {
            var n = ReadNode(d, ref pos, wide);
            if (n == null) break;
            root.Children.Add(n);
        }
        return root;
    }

    static Node ReadNode(byte[] d, ref int pos, bool wide)
    {
        long end, count;
        if (wide) { end = BitConverter.ToInt64(d, pos); count = BitConverter.ToInt64(d, pos + 8); pos += 24; }
        else { end = BitConverter.ToUInt32(d, pos); count = BitConverter.ToUInt32(d, pos + 4); pos += 12; }
        int nameLen = d[pos++];
        if (end == 0) return null; // the null record that closes a list
        var n = new Node { Name = Encoding.ASCII.GetString(d, pos, nameLen) };
        pos += nameLen;
        for (long i = 0; i < count; i++) n.Props.Add(ReadProp(d, ref pos));
        while (pos < end)
        {
            var c = ReadNode(d, ref pos, wide);
            if (c == null) break;
            n.Children.Add(c);
        }
        pos = (int)end;
        return n;
    }

    static object ReadProp(byte[] d, ref int pos)
    {
        char t = (char)d[pos++];
        switch (t)
        {
            case 'Y': { var v = BitConverter.ToInt16(d, pos); pos += 2; return (long)v; }
            case 'C': { var v = d[pos] != 0; pos += 1; return v; }
            case 'I': { var v = BitConverter.ToInt32(d, pos); pos += 4; return (long)v; }
            case 'F': { var v = BitConverter.ToSingle(d, pos); pos += 4; return (double)v; }
            case 'D': { var v = BitConverter.ToDouble(d, pos); pos += 8; return v; }
            case 'L': { var v = BitConverter.ToInt64(d, pos); pos += 8; return v; }
            case 'S': { int len = BitConverter.ToInt32(d, pos); pos += 4; var s = Encoding.UTF8.GetString(d, pos, len); pos += len; return s; }
            case 'R': { int len = BitConverter.ToInt32(d, pos); pos += 4; var b = new byte[len]; Buffer.BlockCopy(d, pos, b, 0, len); pos += len; return b; }
            case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int count = BitConverter.ToInt32(d, pos), enc = BitConverter.ToInt32(d, pos + 4), clen = BitConverter.ToInt32(d, pos + 8);
                    pos += 12;
                    int elem = t switch { 'd' or 'l' => 8, 'b' => 1, _ => 4 };
                    byte[] raw;
                    if (enc == 1)
                    {
                        raw = new byte[count * elem];
                        using var zs = new ZLibStream(new MemoryStream(d, pos, clen), CompressionMode.Decompress);
                        int got = 0;
                        while (got < raw.Length) { int r = zs.Read(raw, got, raw.Length - got); if (r <= 0) break; got += r; }
                    }
                    else { raw = new byte[count * elem]; Buffer.BlockCopy(d, pos, raw, 0, raw.Length); }
                    pos += clen;
                    switch (t)
                    {
                        case 'f': { var a = new double[count]; for (int k = 0; k < count; k++) a[k] = BitConverter.ToSingle(raw, k * 4); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(raw, 0, a, 0, count * 8); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(raw, 0, a, 0, count * 8); return a; }
                        case 'i': { var a = new long[count]; for (int k = 0; k < count; k++) a[k] = BitConverter.ToInt32(raw, k * 4); return a; }
                        default: { var a = new long[count]; for (int k = 0; k < count; k++) a[k] = raw[k]; return a; }
                    }
                }
            default: throw new InvalidDataException($"FBX: unknown property type '{t}'.");
        }
    }

    // ---------------------------------------------------------------- ASCII
    static Node ParseAscii(string s)
    {
        var tok = new List<(char kind, string text)>(); // kind: n=name(with ':'), s=string, v=value, '{', '}', ',', '*', '\n'
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ';') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '\n') { tok.Add(('\n', null)); i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '"') { int j = s.IndexOf('"', i + 1); if (j < 0) j = s.Length; tok.Add(('s', s[(i + 1)..j])); i = j + 1; continue; }
            if (c is '{' or '}' or ',' or '*') { tok.Add((c, null)); i++; continue; }
            int k = i;
            while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not ('{' or '}' or ',' or '"' or ';')) k++;
            var w = s[i..k];
            if (w.EndsWith(':')) tok.Add(('n', w[..^1])); else tok.Add(('v', w));
            i = k;
        }
        int p = 0;
        var root = new Node { Name = "" };
        ParseAsciiChildren(tok, ref p, root, top: true);
        return root;
    }

    static void ParseAsciiChildren(List<(char kind, string text)> tok, ref int p, Node parent, bool top)
    {
        while (p < tok.Count)
        {
            var t = tok[p];
            if (t.kind == '\n' || t.kind == ',') { p++; continue; }
            if (t.kind == '}') { p++; if (!top) return; continue; }
            if (t.kind != 'n') { p++; continue; }
            p++;
            var n = new Node { Name = t.text };
            parent.Children.Add(n);
            // properties until the end of the line (a ',' carries them over) or a '{'
            while (p < tok.Count)
            {
                var a = tok[p];
                if (a.kind == '\n') { if (p + 1 < tok.Count && tok[p - 1].kind == ',') { p++; continue; } break; }
                if (a.kind == '{') break;
                if (a.kind == '}' || a.kind == 'n') break;
                if (a.kind == ',') { p++; continue; }
                if (a.kind == '*')
                {
                    // array: *N { a: v, v, ... }
                    p += 2; // '*' and the count
                    while (p < tok.Count && tok[p].kind != '{') p++;
                    p++;
                    var vals = new List<double>();
                    while (p < tok.Count && tok[p].kind != '}')
                    {
                        if (tok[p].kind == 'v' && double.TryParse(tok[p].text, NumberStyles.Float, Inv, out var dv)) vals.Add(dv);
                        p++;
                    }
                    p++;
                    n.Props.Add(vals.ToArray());
                    continue;
                }
                if (a.kind == 's') n.Props.Add(a.text);
                else if (a.kind == 'v')
                {
                    if (long.TryParse(a.text, NumberStyles.Integer, Inv, out var lv)) n.Props.Add(lv);
                    else if (double.TryParse(a.text, NumberStyles.Float, Inv, out var dv)) n.Props.Add(dv);
                    else n.Props.Add(a.text);
                }
                p++;
            }
            if (p < tok.Count && tok[p].kind == '{')
            {
                p++;
                ParseAsciiChildren(tok, ref p, n, top: false);
                // ASCII arrays written as "Name: *N {" have no other props: a child "a" holds the values
                if (n.Props.Count == 0 && n.Children.Count == 1 && n.Children[0].Name == "a")
                {
                    var vals = n.Children[0].Props.Select(ToDouble).ToArray();
                    n.Props.Add(vals); n.Children.Clear();
                }
            }
        }
    }

    // ---------------------------------------------------------------- property helpers
    static double ToDouble(object o) => o switch { double d => d, long l => l, bool b => b ? 1 : 0, string s => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0, _ => 0 };
    static long ToLong(object o) => o switch { long l => l, double d => (long)d, bool b => b ? 1 : 0, _ => 0 };
    static double[] Doubles(Node n) => n?.Props.Count > 0 ? n.Props[0] switch { double[] d => d, long[] l => l.Select(x => (double)x).ToArray(), _ => n.Props.Select(ToDouble).ToArray() } : Array.Empty<double>();
    static long[] Longs(Node n) => n?.Props.Count > 0 ? n.Props[0] switch { long[] l => l, double[] d => d.Select(x => (long)x).ToArray(), _ => n.Props.Select(ToLong).ToArray() } : Array.Empty<long>();
    static string Str(Node n, int i = 0) => n != null && n.Props.Count > i ? n.Props[i] as string ?? n.Props[i]?.ToString() : null;

    /// <summary>Properties70 / Properties60 entries: name -> values after the 4 (or 3) type fields.</summary>
    static Dictionary<string, object[]> P70(Node owner)
    {
        var d = new Dictionary<string, object[]>();
        var props = owner?.Child("Properties70") ?? owner?.Child("Properties60");
        if (props == null) return d;
        bool v7 = props.Name == "Properties70";
        foreach (var p in props.Children)
        {
            if (p.Props.Count == 0 || p.Props[0] is not string name) continue;
            int skip = v7 ? 4 : 3;
            d[name] = p.Props.Skip(skip).ToArray();
        }
        return d;
    }
    static Vector3 Vec(Dictionary<string, object[]> p, string k, Vector3 def) =>
        p.TryGetValue(k, out var v) && v.Length >= 3 ? new Vector3((float)ToDouble(v[0]), (float)ToDouble(v[1]), (float)ToDouble(v[2])) : def;
    static double Num(Dictionary<string, object[]> p, string k, double def) => p.TryGetValue(k, out var v) && v.Length >= 1 ? ToDouble(v[0]) : def;

    /// <summary>Object names are "Name\0\1Class" (binary) or "Class::Name" (ASCII).</summary>
    static string CleanName(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int z = s.IndexOf('\0'); if (z >= 0) s = s[..z];
        int c = s.IndexOf("::", StringComparison.Ordinal); if (c >= 0) s = s[(c + 2)..];
        return s;
    }

    // ---------------------------------------------------------------- scene
    sealed class Obj { public long Id; public string Name, Kind, SubKind; public Node N; public long Parent = 0; public readonly List<long> Children = new(); }

    public static ImportResult Import(string path, ImportOptions opt)
    {
        var root = Parse(File.ReadAllBytes(path));
        var res = new ImportResult();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        var model = ModelImporter.SafeName(Path.GetFileNameWithoutExtension(path), "model");
        var texDir = opt.TextureFolder ?? Path.Combine(dir, model + "_textures");

        // units and axes
        var gs = P70(root.Child("GlobalSettings"));
        double unit = Num(gs, "UnitScaleFactor", 1.0) / 100.0; // FBX length unit in cm -> metres
        int up = (int)Num(gs, "UpAxis", 1), upSign = (int)Num(gs, "UpAxisSign", 1);
        int front = (int)Num(gs, "FrontAxis", 2), frontSign = (int)Num(gs, "FrontAxisSign", 1);
        int coord = (int)Num(gs, "CoordAxis", 0), coordSign = (int)Num(gs, "CoordAxisSign", 1);
        // file axes -> Z-up (X right, Y forward away from the camera, Z up), right-handed
        Vector3 Axis(int a, int s) => (a switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }) * (s < 0 ? -1 : 1);
        var upV = Axis(up, upSign); var rightV = Axis(coord, coordSign); var frontV = Axis(front, frontSign);
        // rows: where each file axis goes. file "front" (towards the viewer) -> -Y, file up -> +Z, file coord -> +X
        var toZUp = Matrix4x4.Identity;
        {
            // basis matrix B maps (X, -Y, Z) of our space to file axes (right, front, up); we need its inverse
            var B = new Matrix4x4(rightV.X, rightV.Y, rightV.Z, 0, -frontV.X, -frontV.Y, -frontV.Z, 0, upV.X, upV.Y, upV.Z, 0, 0, 0, 0, 1);
            // a file vector v = a*right + b*(-front) + c*up  ->  our (a, b, c)
            if (Matrix4x4.Invert(B, out var inv)) toZUp = inv;
            if (Math.Abs(toZUp.GetDeterminant() + 1) < 1e-3) toZUp *= Matrix4x4.CreateScale(-1, 1, 1); // left-handed set-ups: mirror X back
        }
        var fileToOurs = Matrix4x4.CreateScale((float)(unit * opt.Scale)) * toZUp;

        // objects + connections
        var objs = new Dictionary<long, Obj>();
        var objects = root.Child("Objects");
        if (objects == null) throw new InvalidDataException("FBX: no Objects section.");
        foreach (var n in objects.Children)
        {
            if (n.Props.Count == 0) continue;
            long id = n.Props[0] is long l ? l : 0;
            if (id == 0) continue;
            objs[id] = new Obj { Id = id, N = n, Kind = n.Name, Name = CleanName(Str(n, 1)), SubKind = Str(n, 2) };
        }
        var links = new List<(long child, long parent, string prop)>();
        foreach (var c in root.Child("Connections")?.Children ?? new List<Node>())
        {
            if (c.Props.Count < 3) continue;
            string type = c.Props[0] as string;
            long ch = ToLong(c.Props[1]), pa = ToLong(c.Props[2]);
            links.Add((ch, pa, type == "OP" && c.Props.Count > 3 ? c.Props[3] as string : null));
            if (objs.TryGetValue(ch, out var co) && type == "OO" && objs.TryGetValue(pa, out var po) && co.Kind == "Model" && po.Kind == "Model") { co.Parent = pa; po.Children.Add(ch); }
        }

        // materials
        var matSlot = new Dictionary<long, int>();
        int MatIndex(long matId)
        {
            if (matSlot.TryGetValue(matId, out int k)) return k;
            var mo = objs[matId];
            var p = P70(mo.N);
            var col = Vec(p, "DiffuseColor", Vec(p, "Diffuse", new Vector3(0.75f)));
            var m = GtaMaterial.CreateDefault(0, ModelImporter.SafeName(mo.Name, $"{model}_mat{matSlot.Count}"), col);
            m.ViewportColour = col;
            foreach (var (ch, pa, prop) in links.Where(x => x.parent == matId && x.prop != null))
            {
                if (!objs.TryGetValue(ch, out var tex) || tex.Kind != "Texture") continue;
                var file = TextureFile(tex, objs, links, dir, texDir, res, opt);
                if (file == null) continue;
                var pl = prop.ToLowerInvariant();
                if (pl.Contains("diffuse") || pl.Contains("basecolor") || pl == "maya|basecolor") m.DiffuseTexture ??= file;
                else if (pl.Contains("normal") || pl.Contains("bump")) m.BumpTexture ??= file;
                else if (pl.Contains("specular") || pl.Contains("reflection")) m.SpecTexture ??= file;
            }
            m.Shader = m.BumpTexture != null ? (m.SpecTexture != null ? "normal_spec" : "normal") : (m.SpecTexture != null ? "spec" : "default");
            res.Materials.Add(m);
            return matSlot[matId] = res.Materials.Count - 1;
        }

        // world matrices of the models (row vectors: local = S * Rpost^-1 * R * Rpre * T, then the parent)
        var world = new Dictionary<long, Matrix4x4>();
        Matrix4x4 Local(Obj o)
        {
            var p = P70(o.N);
            var t = Vec(p, "Lcl Translation", Vector3.Zero);
            var r = Vec(p, "Lcl Rotation", Vector3.Zero);
            var s = Vec(p, "Lcl Scaling", Vector3.One);
            var pre = Vec(p, "PreRotation", Vector3.Zero);
            var post = Vec(p, "PostRotation", Vector3.Zero);
            int order = (int)Num(p, "RotationOrder", 0);
            var R = Euler(r, order); var Rpre = Euler(pre, 0); var Rpost = Euler(post, 0);
            Matrix4x4.Invert(Rpost, out var RpostInv);
            return Matrix4x4.CreateScale(s) * RpostInv * R * Rpre * Matrix4x4.CreateTranslation(t);
        }
        Matrix4x4 World(long id)
        {
            if (world.TryGetValue(id, out var w)) return w;
            var o = objs[id];
            w = Local(o);
            if (o.Parent != 0 && objs.ContainsKey(o.Parent)) w *= World(o.Parent);
            return world[id] = w;
        }

        // meshes: a Geometry hangs under a Model (instances: the same geometry under several models)
        int unnamed = 0;
        foreach (var (geoId, modelId, prop) in links.Where(x => x.prop == null))
        {
            if (!objs.TryGetValue(geoId, out var geo) || geo.Kind != "Geometry" || geo.SubKind != "Mesh") continue;
            if (!objs.TryGetValue(modelId, out var mdl) || mdl.Kind != "Model") continue;
            var mats = links.Where(x => x.parent == modelId && x.prop == null && objs.TryGetValue(x.child, out var m) && m.Kind == "Material").Select(x => x.child).ToList();
            var p = P70(mdl.N);
            var geom = Matrix4x4.CreateScale(Vec(p, "GeometricScaling", Vector3.One)) * Euler(Vec(p, "GeometricRotation", Vector3.Zero), 0)
                       * Matrix4x4.CreateTranslation(Vec(p, "GeometricTranslation", Vector3.Zero));
            var m4 = geom * World(modelId) * fileToOurs;
            var mesh = BuildMesh(geo.N, mats, MatIndex, m4, res);
            if (mesh == null || mesh.Faces.Count == 0) continue;
            if (links.Any(x => x.parent == geoId && objs.TryGetValue(x.child, out var dfm) && dfm.Kind == "Deformer" && dfm.SubKind == "Skin"))
                if (!res.Warnings.Contains(SkinWarning)) res.Warnings.Add(SkinWarning);
            res.Meshes.Add(new ImportedMesh { Name = ModelImporter.SafeName(mdl.Name, $"{model}_{++unnamed:00}"), Mesh = mesh });
        }
        if (res.Meshes.Count == 0) res.Warnings.Add("No mesh found in the FBX (only bones, cameras or lights?).");

        // positions are already Z-up metres: the common finish must not turn them again
        var fin = new ImportOptions { YUpToZUp = false, Scale = 1, WeldVertices = opt.WeldVertices, AutoSmoothAngle = opt.AutoSmoothAngle, MergeIntoOne = opt.MergeIntoOne };
        ModelImporter.Finish(res, fin);
        return res;
    }

    const string SkinWarning = "Skinned mesh imported in its bind pose (the skeleton is not imported yet).";

    /// <summary>Euler angles in degrees, FBX order (0 = XYZ: X first, then Y, then Z).</summary>
    static Matrix4x4 Euler(Vector3 deg, int order)
    {
        var x = Matrix4x4.CreateRotationX(deg.X * MathF.PI / 180f);
        var y = Matrix4x4.CreateRotationY(deg.Y * MathF.PI / 180f);
        var z = Matrix4x4.CreateRotationZ(deg.Z * MathF.PI / 180f);
        return order switch
        {
            1 => x * z * y, // XZY
            2 => y * z * x, // YZX
            3 => y * x * z, // YXZ
            4 => z * x * y, // ZXY
            5 => z * y * x, // ZYX
            _ => x * y * z, // XYZ
        };
    }

    static PolyMesh BuildMesh(Node g, List<long> mats, Func<long, int> matIndex, Matrix4x4 m4, ImportResult res)
    {
        var vd = Doubles(g.Child("Vertices"));
        var pvi = Longs(g.Child("PolygonVertexIndex"));
        if (vd.Length < 9 || pvi.Length < 3) return null;
        var mesh = new PolyMesh();
        for (int i = 0; i + 2 < vd.Length; i += 3)
            mesh.AddVertex(Vector3.Transform(new Vector3((float)vd[i], (float)vd[i + 1], (float)vd[i + 2]), m4));
        bool mirrored = m4.GetDeterminant() < 0;

        // layers
        var uvL = g.All("LayerElementUV").FirstOrDefault();
        double[] uv = Doubles(uvL?.Child("UV")); long[] uvIdx = Longs(uvL?.Child("UVIndex"));
        string uvMap = Str(uvL?.Child("MappingInformationType")) ?? "", uvRef = Str(uvL?.Child("ReferenceInformationType")) ?? "";
        var colL = g.All("LayerElementColor").FirstOrDefault();
        double[] col = Doubles(colL?.Child("Colors")); long[] colIdx = Longs(colL?.Child("ColorIndex"));
        string colMap = Str(colL?.Child("MappingInformationType")) ?? "", colRef = Str(colL?.Child("ReferenceInformationType")) ?? "";
        var matL = g.All("LayerElementMaterial").FirstOrDefault();
        long[] matArr = Longs(matL?.Child("Materials"));
        string matMap = Str(matL?.Child("MappingInformationType")) ?? "AllSame";

        int SlotOf(int polyIndex)
        {
            long local = matArr.Length == 0 ? 0 : matMap == "AllSame" ? matArr[0] : polyIndex < matArr.Length ? matArr[polyIndex] : 0;
            if (mats.Count == 0) return -1;
            return matIndex(mats[(int)Math.Clamp(local, 0, mats.Count - 1)]);
        }
        int defaultMat = -1;
        int Default()
        {
            if (defaultMat >= 0) return defaultMat;
            res.Materials.Add(GtaMaterial.CreateDefault(0, "fbx_default", new Vector3(0.75f)));
            return defaultMat = res.Materials.Count - 1;
        }
        static int LayerIndex(string map, string refType, long[] idx, int cornerIndex, int ctrlPoint, int poly)
        {
            int i = map switch { "ByPolygonVertex" => cornerIndex, "ByVertice" or "ByVertex" or "ByControlPoint" => ctrlPoint, "ByPolygon" => poly, _ => 0 };
            if (refType == "IndexToDirect" || refType == "Index") i = i < idx.Length ? (int)idx[i] : -1;
            return i;
        }

        var corners = new List<int>(); var cUv = new List<Vector2>(); var cCol = new List<Vector4>();
        int poly = 0;
        for (int c = 0; c < pvi.Length; c++)
        {
            long raw = pvi[c];
            bool last = raw < 0;
            int v = (int)(last ? ~raw : raw);
            if (v < 0 || v >= mesh.Verts.Count) { corners.Clear(); cUv.Clear(); cCol.Clear(); if (last) poly++; continue; }
            corners.Add(v);
            Vector2 t = default;
            if (uv.Length > 0)
            {
                int ui = LayerIndex(uvMap, uvRef, uvIdx, c, v, poly);
                if (ui >= 0 && ui * 2 + 1 < uv.Length) t = new Vector2((float)uv[ui * 2], 1f - (float)uv[ui * 2 + 1]);
            }
            cUv.Add(t);
            Vector4 cc = PolyMesh.DefaultColour;
            if (col.Length > 0)
            {
                int ci = LayerIndex(colMap, colRef, colIdx, c, v, poly);
                if (ci >= 0 && ci * 4 + 3 < col.Length) cc = new Vector4((float)col[ci * 4], (float)col[ci * 4 + 1], (float)col[ci * 4 + 2], (float)col[ci * 4 + 3]);
            }
            cCol.Add(cc);
            if (!last) continue;
            if (corners.Count >= 3 && corners.Distinct().Count() >= 3)
            {
                int mat = SlotOf(poly); if (mat < 0) mat = Default();
                var vi = corners.ToArray(); var uvs = cUv.ToArray(); var cols = cCol.ToArray();
                if (mirrored) { Array.Reverse(vi); Array.Reverse(uvs); Array.Reverse(cols); }
                mesh.Faces.Add(new Face(vi, mat, 1u) { UV = uvs, Col = cols });
            }
            corners.Clear(); cUv.Clear(); cCol.Clear();
            poly++;
        }
        mesh.CleanFaces();
        return mesh;
    }

    /// <summary>A texture's file: next to the FBX (relative path, then the file name alone), its absolute path,
    /// or the copy embedded in the FBX (Video "Content"), written to the texture folder.</summary>
    static string TextureFile(Obj tex, Dictionary<long, Obj> objs, List<(long child, long parent, string prop)> links, string dir, string texDir, ImportResult res, ImportOptions opt)
    {
        string rel = Str(tex.N.Child("RelativeFilename")), abs = Str(tex.N.Child("FileName")) ?? Str(tex.N.Child("Filename"));
        foreach (var cand in new[] { rel, abs })
        {
            if (string.IsNullOrWhiteSpace(cand)) continue;
            var c = cand.Replace('\\', Path.DirectorySeparatorChar);
            var p1 = Path.IsPathRooted(c) ? c : Path.Combine(dir, c);
            if (File.Exists(p1)) return Path.GetFullPath(p1);
            var p2 = Path.Combine(dir, Path.GetFileName(c));
            if (File.Exists(p2)) return p2;
            var p3 = Path.Combine(texDir, Path.GetFileName(c));
            if (File.Exists(p3)) return p3;
        }
        // embedded: Video -> Texture connection with Content bytes
        foreach (var (ch, pa, _) in links.Where(x => x.parent == tex.Id))
        {
            if (!objs.TryGetValue(ch, out var vid) || vid.Kind != "Video") continue;
            var content = vid.N.Child("Content");
            if (content?.Props.Count > 0 && content.Props[0] is byte[] bytes && bytes.Length > 0)
            {
                var name = Path.GetFileName((rel ?? abs ?? vid.Name ?? "texture.png").Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(Path.GetExtension(name))) name += ".png";
                Directory.CreateDirectory(texDir);
                var outPath = Path.Combine(texDir, name);
                if (!(opt.SkipExistingTextures && File.Exists(outPath))) File.WriteAllBytes(outPath, bytes);
                res.TexturesWritten.Add(outPath);
                return outPath;
            }
        }
        var missing = Path.GetFileName((rel ?? abs ?? "").Replace('\\', '/'));
        if (missing.Length > 0) res.Warnings.Add($"Texture {missing} not found next to the FBX.");
        return null;
    }
}
