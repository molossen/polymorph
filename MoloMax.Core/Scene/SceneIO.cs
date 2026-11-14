using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Scenes;

/// <summary>Native scene format (.mmx, JSON) and OBJ export for Blender / Sollumz.</summary>
public static class SceneIO
{
    // ---- DTOs keep the file format independent of the runtime classes
    sealed class FaceDto { public int[] V { get; set; } public float[] UV { get; set; } public float[] C { get; set; } public float[] C1 { get; set; } public int M { get; set; } public uint S { get; set; } }
    sealed class MeshDto
    {
        // version 1: numbers in the text (slow and big for 500k polygons)
        public float[] V { get; set; } public List<FaceDto> F { get; set; }
        // version 2: the same data as raw binary blocks (base64): points, polygon sizes, corners, materials, smoothing, UVs, colours
        public byte[] Vb { get; set; } public byte[] Nb { get; set; } public byte[] Ib { get; set; } public byte[] Mb { get; set; } public byte[] Sb { get; set; }
        public byte[] Tb { get; set; } public byte[] Cb { get; set; } public byte[] C1f { get; set; } public byte[] C1b { get; set; }
    }
    sealed class ObjDto
    {
        public int Id { get; set; } public string Name { get; set; } public float[] Pos { get; set; } public float[] Rot { get; set; } public float[] Scale { get; set; }
        public float[] Wire { get; set; } public bool Hidden { get; set; } public bool Frozen { get; set; } public string Type { get; set; } public MeshDto Mesh { get; set; }
        public GtaInfo Gta { get; set; }
        public string Tripo { get; set; }
        public string PaintFile { get; set; }
        public string Splat { get; set; }
        public SplatLook SplatLook { get; set; }
        public string Folder { get; set; }
        public int? Parent { get; set; }
        public List<MoloMax.Core.Modifiers.Modifier> Modifiers { get; set; }
        public string Inst { get; set; }
        public int? MeshOf { get; set; }
        public MoloMax.Core.Mesh.ShapeData Shape { get; set; }
        public float? Bone { get; set; }
    }
    sealed class MatDto
    {
        public string[] Layers { get; set; } public string[] LayerBumps { get; set; }
        public int Id { get; set; } public string Name { get; set; } public string Shader { get; set; }
        public string Diffuse { get; set; } public string Bump { get; set; } public string Spec { get; set; } public string Detail { get; set; }
        public float Bumpiness { get; set; } public float SpecIntensity { get; set; } public float SpecFalloff { get; set; } public float SpecFresnel { get; set; }
        public float[] SpecMask { get; set; } public float Emissive { get; set; } public float[] DetailSettings { get; set; } public float[] Colour { get; set; }
    }
    sealed class SceneDto
    {
        public string Format { get; set; } = "MoloMax"; public int Version { get; set; } = 2;
        public int NextObjectId { get; set; } public int NextMaterialId { get; set; } public float[] GtaOrigin { get; set; } public List<string> Folders { get; set; } public MoloMax.Core.Tools.RoomPlanData Plan { get; set; }
        public List<MatDto> Materials { get; set; } public List<ObjDto> Objects { get; set; }
        public GtaProjectSettings Gta { get; set; }
        public MoloMax.Core.Anim.SceneAnim Anim { get; set; }
    }

    // GtaInfo / GtaProjectSettings are plain field classes
    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false, IncludeFields = true };

    static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
    static float[] A(Vector4 v) => new[] { v.X, v.Y, v.Z, v.W };
    static Vector3 V3(float[] a) => a == null ? Vector3.Zero : new Vector3(a[0], a[1], a[2]);
    static Vector4 V4(float[] a) => a == null ? Vector4.Zero : new Vector4(a[0], a[1], a[2], a[3]);

    public static string ToJson(Scene s) => JsonSerializer.Serialize(BuildDto(s), jsonOptions);

    static SceneDto BuildDto(Scene s)
    {
        var firstOfInst = new Dictionary<string, SceneObject>();
        foreach (var o in s.Objects) if (o.InstanceKey != null) firstOfInst.TryAdd(o.InstanceKey, o);
        var dto = new SceneDto
        {
            NextObjectId = s.NextObjectId, NextMaterialId = s.NextMaterialId, GtaOrigin = A(s.GtaOrigin), Gta = s.Gta, Folders = s.Folders.Count > 0 ? s.Folders : null, Plan = s.Plan?.Rooms.Count > 0 ? s.Plan : null,
            Anim = s.Anim == null || s.Anim.IsEmpty ? null : s.Anim,
            Materials = s.Materials.Select(m => new MatDto
            {
                Id = m.Id, Name = m.Name, Shader = m.Shader, Diffuse = m.DiffuseTexture, Bump = m.BumpTexture, Spec = m.SpecTexture, Detail = m.DetailTexture,
                Bumpiness = m.Bumpiness, SpecIntensity = m.SpecularIntensity, SpecFalloff = m.SpecularFalloff, SpecFresnel = m.SpecularFresnel,
                SpecMask = A(m.SpecMapIntMask), Emissive = m.EmissiveMultiplier, DetailSettings = A(m.DetailSettings), Colour = A(m.ViewportColour),
                Layers = m.Layers?.Any(x => x != null) == true ? m.Layers : null, LayerBumps = m.LayerBumps?.Any(x => x != null) == true ? m.LayerBumps : null,
            }).ToList(),
            Objects = s.Objects.Select(o => ObjOut(o, firstOfInst)).ToList(),
        };
        return dto;
    }

    static ObjDto ObjOut(SceneObject o, Dictionary<string, SceneObject> firstOfInst)
    {
        // an object in the middle of an Edit Poly / Unwrap edit is written as if the edit had ended
        if (o.ModEdit != null) { var c = o.Clone(); c.CommitModifierEdit(); o = c; firstOfInst = new(); }
        // an instance stores its mesh once: the other members point to the first one
        if (o.InstanceKey != null && firstOfInst.TryGetValue(o.InstanceKey, out var first) && first != o && ReferenceEquals(first.Mesh, o.Mesh))
            return new ObjDto
            {
                Id = o.Id, Name = o.Name, Pos = A(o.Position), Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }, Scale = A(o.Scale),
                Wire = A(o.WireColour), Hidden = o.Hidden, Frozen = o.Frozen, Folder = string.IsNullOrEmpty(o.Folder) ? null : o.Folder, Parent = o.ParentId >= 0 ? o.ParentId : null, Type = o.GtaType.ToString(), Gta = o.Gta, Tripo = o.TripoTaskId, PaintFile = o.PaintFile, Splat = o.SplatFile, SplatLook = o.Splat,
                Inst = o.InstanceKey, MeshOf = first.Id,
            };
        return new ObjDto
            {
                Id = o.Id, Name = o.Name, Pos = A(o.Position), Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }, Scale = A(o.Scale),
                Wire = A(o.WireColour), Hidden = o.Hidden, Frozen = o.Frozen, Folder = string.IsNullOrEmpty(o.Folder) ? null : o.Folder, Parent = o.ParentId >= 0 ? o.ParentId : null, Type = o.GtaType.ToString(), Gta = o.Gta, Tripo = o.TripoTaskId, PaintFile = o.PaintFile, Splat = o.SplatFile, SplatLook = o.Splat, Modifiers = o.Modifiers?.Count > 0 ? o.Modifiers : null,
                Inst = o.InstanceKey, Shape = o.Shape, Bone = o.IsBone ? o.BoneLength : null,
                Mesh = PackMesh(o.Mesh),
            };
    }

    public static Scene FromJson(string json) => FromDto(JsonSerializer.Deserialize<SceneDto>(json, jsonOptions) ?? throw new InvalidDataException("Empty scene file."));

    static Scene FromDto(SceneDto dto)
    {
        if (dto.Format != "MoloMax") throw new InvalidDataException("This is not a MoloMax scene.");
        var s = new Scene { NextObjectId = dto.NextObjectId, NextMaterialId = dto.NextMaterialId, GtaOrigin = V3(dto.GtaOrigin), Gta = dto.Gta ?? new GtaProjectSettings(), Folders = dto.Folders ?? new(), Plan = dto.Plan ?? new(), Anim = dto.Anim ?? new() };
        s.Materials.Clear();
        foreach (var m in dto.Materials ?? new())
            s.Materials.Add(new GtaMaterial
            {
                Id = m.Id, Name = m.Name, Shader = m.Shader, DiffuseTexture = m.Diffuse, BumpTexture = m.Bump, SpecTexture = m.Spec, DetailTexture = m.Detail,
                Bumpiness = m.Bumpiness, SpecularIntensity = m.SpecIntensity, SpecularFalloff = m.SpecFalloff, SpecularFresnel = m.SpecFresnel,
                SpecMapIntMask = V3(m.SpecMask), EmissiveMultiplier = m.Emissive, DetailSettings = V4(m.DetailSettings), ViewportColour = V3(m.Colour),
                Layers = m.Layers, LayerBumps = m.LayerBumps,
            });
        if (s.Materials.Count == 0) s.Materials.Add(GtaMaterial.CreateDefault(0, "default_grey", new Vector3(0.72f)));
        var meshOfId = new Dictionary<int, SceneObject>();
        var pending = new List<(SceneObject Obj, int Of)>();
        foreach (var o in dto.Objects ?? new())
        {
            if (o.Mesh == null)
            {
                // instance: takes its mesh from the object it points to (resolved below)
                var inst = new SceneObject
                {
                    Id = o.Id, Name = o.Name, Mesh = new PolyMesh(), Position = V3(o.Pos),
                    Rotation = o.Rot != null ? new Quaternion(o.Rot[0], o.Rot[1], o.Rot[2], o.Rot[3]) : Quaternion.Identity,
                    Scale = o.Scale != null ? V3(o.Scale) : Vector3.One, WireColour = V3(o.Wire), Hidden = o.Hidden, Frozen = o.Frozen, Folder = o.Folder ?? "", ParentId = o.Parent ?? -1,
                    GtaType = Enum.TryParse<GtaObjectType>(o.Type, out var it) ? it : GtaObjectType.Drawable,
                    Gta = o.Gta ?? new GtaInfo(), TripoTaskId = o.Tripo, PaintFile = o.PaintFile, SplatFile = o.Splat, Splat = o.SplatLook, Modifiers = o.Modifiers ?? new(), InstanceKey = o.Inst,
                };
                s.Objects.Add(inst); meshOfId[inst.Id] = inst;
                if (o.MeshOf is int of) pending.Add((inst, of));
                continue;
            }
            var mesh = new PolyMesh();
            if (o.Mesh.Vb != null) mesh = UnpackMesh(o.Mesh);
            else
            {
            for (int i = 0; i + 2 < o.Mesh.V.Length; i += 3) mesh.Verts.Add(new Vector3(o.Mesh.V[i], o.Mesh.V[i + 1], o.Mesh.V[i + 2]));
            foreach (var f in o.Mesh.F)
            {
                var face = new Face(f.V, f.M, f.S);
                for (int i = 0; i < f.V.Length; i++)
                {
                    if (f.UV != null && f.UV.Length >= 2 * f.V.Length) face.UV[i] = new Vector2(f.UV[2 * i], f.UV[2 * i + 1]);
                    if (f.C != null && f.C.Length >= 4 * f.V.Length) face.Col[i] = new Vector4(f.C[4 * i], f.C[4 * i + 1], f.C[4 * i + 2], f.C[4 * i + 3]);
                    if (f.C1 != null && f.C1.Length >= 4 * f.V.Length) { face.Col1 ??= new Vector4[f.V.Length]; face.Col1[i] = new Vector4(f.C1[4 * i], f.C1[4 * i + 1], f.C1[4 * i + 2], f.C1[4 * i + 3]); }
                }
                mesh.Faces.Add(face);
            }
            }
            // repair what older versions could leave (a polygon pinched by a weld, a face with a missing vertex) instead of refusing the file
            mesh.Faces.RemoveAll(f => f.V.Length < 3 || f.V.Any(v => v < 0 || v >= mesh.Verts.Count));
            mesh.CleanFaces();
            mesh.Validate();
            s.Objects.Add(new SceneObject
            {
                Id = o.Id, Name = o.Name, Mesh = mesh, Position = V3(o.Pos),
                Rotation = o.Rot != null ? new Quaternion(o.Rot[0], o.Rot[1], o.Rot[2], o.Rot[3]) : Quaternion.Identity,
                Scale = o.Scale != null ? V3(o.Scale) : Vector3.One, WireColour = V3(o.Wire), Hidden = o.Hidden, Frozen = o.Frozen, Folder = o.Folder ?? "", ParentId = o.Parent ?? -1,
                GtaType = Enum.TryParse<GtaObjectType>(o.Type, out var t) ? t : GtaObjectType.Drawable,
                Gta = o.Gta ?? new GtaInfo(), TripoTaskId = o.Tripo, PaintFile = o.PaintFile, SplatFile = o.Splat, Splat = o.SplatLook, Modifiers = o.Modifiers ?? new(), InstanceKey = o.Inst, Shape = o.Shape,
                IsBone = o.Bone.HasValue, BoneLength = o.Bone ?? 0.3f,
            });
            meshOfId[o.Id] = s.Objects[^1];
        }
        foreach (var (inst, of) in pending)
            if (meshOfId.TryGetValue(of, out var src)) { inst.Mesh = src.Mesh; inst.Modifiers = src.Modifiers; inst.Version = src.Version; }
        // a lone member is a plain object
        foreach (var g in s.Objects.Where(o => o.InstanceKey != null).GroupBy(o => o.InstanceKey).Where(g => g.Count() == 1)) g.First().InstanceKey = null;
        s.SyncInstances();
        return s;
    }

    public static void Save(Scene s, string path)
    {
        // written straight to the file (no 300 MB string in memory first)
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        JsonSerializer.Serialize(fs, BuildDto(s), jsonOptions);
    }
    public static Scene Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var dto = JsonSerializer.Deserialize<SceneDto>(fs, jsonOptions) ?? throw new InvalidDataException("Empty scene file.");
        return FromDto(dto);
    }

    // ---- version 2 mesh blocks
    static byte[] Bytes<T>(T[] a) where T : struct => System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray();
    static T[] Arr<T>(byte[] b) where T : struct => b == null ? Array.Empty<T>() : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(b).ToArray();

    static MeshDto PackMesh(PolyMesh m)
    {
        int fc = m.Faces.Count;
        var off = new int[fc + 1];
        for (int i = 0; i < fc; i++) off[i + 1] = off[i] + m.Faces[i].V.Length;
        int nc = off[fc];
        var n = new int[fc]; var mat = new int[fc]; var sm = new uint[fc]; var idx = new int[nc]; var uv = new Vector2[nc]; var col = new Vector4[nc];
        var c1f = new byte[fc]; bool anyC1 = false;
        System.Threading.Tasks.Parallel.For(0, (fc + 8191) / 8192, b =>
        {
            for (int i = b * 8192; i < Math.Min(fc, (b + 1) * 8192); i++)
            {
                var f = m.Faces[i]; int o = off[i]; n[i] = f.V.Length; mat[i] = f.MatId; sm[i] = f.Smooth;
                Array.Copy(f.V, 0, idx, o, f.V.Length); Array.Copy(f.UV, 0, uv, o, f.V.Length); Array.Copy(f.Col, 0, col, o, f.V.Length);
                if (f.Col1 != null && f.Col1.Length == f.V.Length) { c1f[i] = 1; anyC1 = true; }
            }
        });
        byte[] c1b = null;
        if (anyC1)
        {
            var c1 = new List<Vector4>();
            for (int i = 0; i < fc; i++) if (c1f[i] == 1) c1.AddRange(m.Faces[i].Col1);
            c1b = Bytes(c1.ToArray());
        }
        return new MeshDto
        {
            Vb = Bytes(m.Verts.ToArray()), Nb = Bytes(n), Ib = Bytes(idx), Mb = Bytes(mat), Sb = Bytes(sm), Tb = Bytes(uv), Cb = Bytes(col),
            C1f = anyC1 ? c1f : null, C1b = c1b,
        };
    }

    static PolyMesh UnpackMesh(MeshDto d)
    {
        var verts = Arr<Vector3>(d.Vb); var n = Arr<int>(d.Nb); var idx = Arr<int>(d.Ib); var mat = Arr<int>(d.Mb); var sm = Arr<uint>(d.Sb);
        var uv = Arr<Vector2>(d.Tb); var col = Arr<Vector4>(d.Cb); var c1 = Arr<Vector4>(d.C1b); var c1f = d.C1f;
        int fc = n.Length;
        var off = new int[fc + 1]; for (int i = 0; i < fc; i++) off[i + 1] = off[i] + n[i];
        var c1off = new int[fc + 1]; for (int i = 0; i < fc; i++) c1off[i + 1] = c1off[i] + (c1f != null && i < c1f.Length && c1f[i] == 1 ? n[i] : 0);
        if (off[fc] > idx.Length) throw new InvalidDataException("Damaged mesh in the scene file.");
        var faces = new Face[fc];
        System.Threading.Tasks.Parallel.For(0, (fc + 8191) / 8192, b =>
        {
            for (int i = b * 8192; i < Math.Min(fc, (b + 1) * 8192); i++)
            {
                int o = off[i], k = n[i];
                var v = new int[k]; Array.Copy(idx, o, v, 0, k);
                var t = new Vector2[k]; if (o + k <= uv.Length) Array.Copy(uv, o, t, 0, k);
                var c = new Vector4[k]; if (o + k <= col.Length) Array.Copy(col, o, c, 0, k); else Array.Fill(c, PolyMesh.DefaultColour);
                Vector4[] cc = null;
                if (c1off[i + 1] > c1off[i] && c1off[i + 1] <= c1.Length) { cc = new Vector4[k]; Array.Copy(c1, c1off[i], cc, 0, k); }
                faces[i] = Face.FromArrays(v, t, c, cc, i < mat.Length ? mat[i] : 0, i < sm.Length ? sm[i] : 0);
            }
        });
        return new PolyMesh { Verts = new List<Vector3>(verts), Faces = new List<Face>(faces) };
    }

    /// <summary>
    /// Wavefront OBJ (+ MTL) in Blender's Y-up convention, with vertex colours ("v x y z r g b")
    /// so Sollumz keeps the GTA colour bake. Portals are skipped.
    /// </summary>
    public static void ExportObj(Scene s, string objPath, bool selectedOnly = false, ISet<int> selectedIds = null)
    {
        var ci = CultureInfo.InvariantCulture;
        string mtlName = Path.GetFileNameWithoutExtension(objPath) + ".mtl";
        var obj = new StringBuilder();
        obj.AppendLine("# MoloMax OBJ export (Z-up scene written Y-up)");
        obj.AppendLine($"mtllib {mtlName}");
        int vBase = 1, tBase = 1;
        var usedMats = new HashSet<int>();
        foreach (var o in s.Objects)
        {
            if (o.GtaType == GtaObjectType.Portal) continue;
            if (selectedOnly && (selectedIds == null || !selectedIds.Contains(o.Id))) continue;
            var w = o.World;
            obj.AppendLine($"o {o.Name.ToLowerInvariant()}");
            // per-vertex colour: average of its corners
            var col = new Vector4[o.Mesh.Verts.Count]; var cnt = new int[o.Mesh.Verts.Count];
            foreach (var f in o.Mesh.Faces) for (int i = 0; i < f.V.Length; i++) { col[f.V[i]] += f.Col[i]; cnt[f.V[i]]++; }
            for (int i = 0; i < o.Mesh.Verts.Count; i++)
            {
                var p = Vector3.Transform(o.Mesh.Verts[i], w);
                var c = cnt[i] > 0 ? col[i] / cnt[i] : PolyMesh.DefaultColour;
                obj.AppendLine(string.Format(ci, "v {0:F5} {1:F5} {2:F5} {3:F4} {4:F4} {5:F4}", p.X, p.Z, -p.Y, c.X, c.Y, c.Z));
            }
            int tCount = 0;
            foreach (var f in o.Mesh.Faces) foreach (var uv in f.UV) { obj.AppendLine(string.Format(ci, "vt {0:F5} {1:F5}", uv.X, 1 - uv.Y)); tCount++; }
            int curMat = int.MinValue, t = tBase;
            foreach (var f in o.Mesh.Faces)
            {
                if (f.MatId != curMat) { curMat = f.MatId; usedMats.Add(curMat); obj.AppendLine($"usemtl {s.Material(curMat).Name}"); }
                obj.Append('f');
                for (int i = 0; i < f.V.Length; i++) obj.Append(' ').Append(f.V[i] + vBase).Append('/').Append(t++);
                obj.AppendLine();
            }
            vBase += o.Mesh.Verts.Count; tBase += tCount;
        }
        File.WriteAllText(objPath, obj.ToString());

        var mtl = new StringBuilder("# MoloMax materials (shader name kept in a comment for Sollumz)\n");
        foreach (var id in usedMats)
        {
            var m = s.Material(id);
            mtl.AppendLine($"newmtl {m.Name}");
            mtl.AppendLine($"# gta_shader {m.Shader}.sps");
            mtl.AppendLine(string.Format(ci, "Kd {0:F4} {1:F4} {2:F4}", m.ViewportColour.X, m.ViewportColour.Y, m.ViewportColour.Z));
            if (!string.IsNullOrEmpty(m.DiffuseTexture)) mtl.AppendLine($"map_Kd {m.DiffuseTexture}");
            if (!string.IsNullOrEmpty(m.BumpTexture)) mtl.AppendLine($"map_Bump {m.BumpTexture}");
            if (!string.IsNullOrEmpty(m.SpecTexture)) mtl.AppendLine($"map_Ks {m.SpecTexture}");
            mtl.AppendLine();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".", mtlName), mtl.ToString());
    }
}
