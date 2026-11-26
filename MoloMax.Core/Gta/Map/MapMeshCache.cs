using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// Map models already read once, kept as a small binary file (mesh + materials): the next time the map opens they load
/// in a few milliseconds instead of being extracted and converted again.
/// </summary>
public static class MapMeshCache
{
    const int Magic = 0x434D4D50; // "PMMC"
    const int Version = 3; // 2: terrain layers + vertex colour 1, 3: tint palette

    public static void Save(string path, PolyMesh m, IReadOnlyList<GtaMaterial> mats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic); w.Write(Version);
            w.Write(mats.Count);
            foreach (var x in mats)
            {
                Str(w, x.Name); Str(w, x.Shader); Str(w, x.DiffuseTexture); Str(w, x.BumpTexture); Str(w, x.SpecTexture); Str(w, x.DetailTexture);
                w.Write(x.Bumpiness); w.Write(x.SpecularIntensity); w.Write(x.SpecularFalloff); w.Write(x.SpecularFresnel);
                V3(w, x.SpecMapIntMask); w.Write(x.EmissiveMultiplier); V4(w, x.DetailSettings); V3(w, x.ViewportColour);
                for (int k = 0; k < 4; k++) { Str(w, x.Layer(k)); Str(w, x.LayerBump(k)); }
                Str(w, x.TintTexture);
            }
            w.Write(m.Verts.Count);
            foreach (var v in m.Verts) V3(w, v);
            w.Write(m.Faces.Count);
            foreach (var f in m.Faces)
            {
                w.Write(f.V.Length); w.Write(f.MatId); w.Write(f.Smooth); w.Write(f.Col1 != null);
                for (int i = 0; i < f.V.Length; i++)
                {
                    w.Write(f.V[i]);
                    var uv = f.UV[i]; w.Write(uv.X); w.Write(uv.Y);
                    V4(w, f.Col[i]);
                    if (f.Col1 != null) V4(w, f.Col1[i]);
                }
            }
        }
        File.Move(tmp, path, true);
    }

    public static (PolyMesh Mesh, List<GtaMaterial> Materials)? Load(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            int nm = r.ReadInt32();
            var mats = new List<GtaMaterial>(nm);
            for (int i = 0; i < nm; i++)
            {
                var x = new GtaMaterial
                {
                    Name = Str(r) ?? "Material", Shader = Str(r) ?? "normal_spec", DiffuseTexture = Str(r), BumpTexture = Str(r), SpecTexture = Str(r), DetailTexture = Str(r),
                    Bumpiness = r.ReadSingle(), SpecularIntensity = r.ReadSingle(), SpecularFalloff = r.ReadSingle(), SpecularFresnel = r.ReadSingle(),
                };
                x.SpecMapIntMask = V3(r); x.EmissiveMultiplier = r.ReadSingle(); x.DetailSettings = V4(r); x.ViewportColour = V3(r);
                for (int k = 0; k < 4; k++)
                {
                    var l = Str(r); var lb = Str(r);
                    if (l != null || lb != null) { x.EnsureLayers(); x.Layers[k] = l; x.LayerBumps[k] = lb; }
                }
                x.TintTexture = Str(r);
                mats.Add(x);
            }
            // the geometry (most of the file) read in one block and decoded from memory
            var rest = new byte[fs.Length - fs.Position];
            int got = 0; while (got < rest.Length) { int k = fs.Read(rest, got, rest.Length - got); if (k <= 0) break; got += k; }
            var b = rest; int p = 0;
            int I() { int v = BitConverter.ToInt32(b, p); p += 4; return v; }
            float F() { float v = BitConverter.ToSingle(b, p); p += 4; return v; }
            var m = new PolyMesh();
            int nv = I();
            var verts = new Vector3[nv];
            for (int i = 0; i < nv; i++) verts[i] = new Vector3(F(), F(), F());
            m.Verts = new List<Vector3>(verts);
            int nf = I();
            var faces = new Face[nf];
            for (int i = 0; i < nf; i++)
            {
                int n = I(); int mat = I(); uint sm = (uint)I(); bool c1 = b[p++] != 0;
                var vi = new int[n]; var uv = new Vector2[n]; var col = new Vector4[n]; var col1 = c1 ? new Vector4[n] : null;
                for (int c = 0; c < n; c++)
                {
                    vi[c] = I();
                    uv[c] = new Vector2(F(), F());
                    col[c] = new Vector4(F(), F(), F(), F());
                    if (c1) col1[c] = new Vector4(F(), F(), F(), F());
                }
                faces[i] = Face.FromArrays(vi, uv, col, col1, mat, sm);
            }
            m.Faces = new List<Face>(faces);
            return (m, mats);
        }
        catch { return null; }
    }

    static void Str(BinaryWriter w, string s) { w.Write(s != null); if (s != null) w.Write(s); }
    static string Str(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
    static void V3(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void V4(BinaryWriter w, Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
    static Vector3 V3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static Vector4 V4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
