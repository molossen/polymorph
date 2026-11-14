using System.Numerics;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Gta.Export;

/// <summary>A bone of an exported skeleton: local place relative to its parent (bind pose, model space).</summary>
public sealed class SkinBone
{
    public string Name;
    public int Tag, Parent = -1;
    public Vector3 Translation;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>Bind matrix in model space (parent chain applied).</summary>
    public Matrix4x4 Model = Matrix4x4.Identity;
}

/// <summary>Skeleton and per-vertex weights of a skinned model (bone 0 is the root the game expects).</summary>
public sealed class ModelSkin
{
    public List<SkinBone> Bones = new();
    /// <summary>Points of the bind-pose mesh in model space, with 4 bone slots (export bone index) and weights each.</summary>
    public Vector3[] Points = Array.Empty<Vector3>();
    public int[] Idx = Array.Empty<int>();
    public float[] W = Array.Empty<float>();

    /// <summary>Gives every vertex of the model (and its LODs) the weights of the bind-pose point at its place
    /// (the nearest one for points a LOD moved).</summary>
    public void Assign(GtaModel model)
    {
        var grid = new Dictionary<(int, int, int), List<int>>();
        float cell = 0.05f;
        if (Points.Length > 0)
        {
            var mn = Points.Aggregate(Vector3.Min); var mx = Points.Aggregate(Vector3.Max);
            float size = MathF.Max(mx.X - mn.X, MathF.Max(mx.Y - mn.Y, mx.Z - mn.Z));
            cell = MathF.Max(1e-3f, size / MathF.Max(1, MathF.Cbrt(Points.Length) * 2));
        }
        (int, int, int) K(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int i = 0; i < Points.Length; i++) { var k = K(Points[i]); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new(); l.Add(i); }
        int Nearest(Vector3 p)
        {
            var (x, y, z) = K(p); int best = -1; float bd = float.MaxValue;
            for (int r = 0; r < 64 && (best < 0 || r <= 1); r++)
            {
                for (int a = -r; a <= r; a++) for (int b = -r; b <= r; b++) for (int c = -r; c <= r; c++)
                {
                    if (Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c))) != r) continue;
                    if (!grid.TryGetValue((x + a, y + b, z + c), out var l)) continue;
                    foreach (var i in l) { float d = Vector3.DistanceSquared(Points[i], p); if (d < bd) { bd = d; best = i; } }
                }
            }
            return best;
        }
        foreach (var part in model.Parts.Concat(model.Lods.SelectMany(l => l.Parts)))
        {
            part.Blend = new byte[part.Verts.Count * 8];
            for (int v = 0; v < part.Verts.Count; v++)
            {
                int s = Nearest(part.Verts[v].Position);
                Span<int> bi = stackalloc int[4]; Span<float> bw = stackalloc float[4];
                for (int k = 0; k < 4; k++) { bi[k] = s >= 0 ? Idx[s * 4 + k] : -1; bw[k] = s >= 0 && bi[k] >= 0 ? MathF.Max(0, W[s * 4 + k]) : 0; }
                Pack(bi, bw, part.Blend.AsSpan(v * 8, 8));
            }
        }
    }

    /// <summary>4 weights (bytes adding up to 255) then 4 bone indices; no weight at all goes to the root.</summary>
    static void Pack(Span<int> bi, Span<float> bw, Span<byte> dst)
    {
        float sum = 0; for (int k = 0; k < 4; k++) sum += bw[k];
        if (sum <= 1e-6f) { dst.Clear(); dst[0] = 255; return; }
        int total = 0, big = 0;
        for (int k = 0; k < 4; k++)
        {
            int q = (int)MathF.Round(bw[k] / sum * 255f);
            dst[k] = (byte)q; dst[4 + k] = (byte)Math.Max(0, bi[k]); total += q;
            if (bw[k] > bw[big]) big = k;
        }
        dst[big] = (byte)Math.Clamp(dst[big] + 255 - total, 0, 255);
        for (int k = 0; k < 4; k++) if (dst[k] == 0) dst[4 + k] = 0;
    }
}

/// <summary>Skinned meshes (Skin modifier) for the GTA export: the bind pose, a skeleton and the weights.</summary>
public static class SkinExport
{
    /// <summary>A skinned object of the export copy: its Skin modifier, taken off so the mesh stays in the bind pose.</summary>
    public sealed class Source
    {
        public SkinModifier Skin;
        public Vector3[] Points; // bind-pose points, object space
    }

    /// <summary>GTA bone tag of a name (the game's hash: ElfHash % 0xFE8F + 0x170).</summary>
    public static int Tag(string name)
    {
        uint h = 0;
        foreach (char ch in name.ToUpperInvariant())
        {
            h = (h << 4) + ch;
            uint x = h & 0xF0000000;
            if (x != 0) h ^= x >> 24;
            h &= ~x;
        }
        return (int)(h % 0xFE8F + 0x170);
    }

    /// <summary>On a scene copy, before its modifiers are baked: every object whose Skin modifier is the last one
    /// (and has weights for its mesh) loses it, so the export sees the bind pose. Returns them by object id.</summary>
    public static Dictionary<int, Source> Take(Scene scene, List<string> warnings)
    {
        var res = new Dictionary<int, Source>();
        foreach (var o in scene.Objects)
        {
            var mods = o.Modifiers;
            if (mods == null || mods.Count == 0) continue;
            int si = mods.FindLastIndex(m => m is SkinModifier && m.ShowRender);
            if (si < 0) continue;
            var sk = (SkinModifier)mods[si];
            if (mods.Skip(si + 1).Any(m => m.ShowRender))
            {
                warnings?.Add($"'{o.Name}': the Skin modifier is not the last one of the stack; exported posed, without bones.");
                continue;
            }
            var input = ModifierStack.Evaluate(o.Mesh, mods.Take(si).ToList(), false, true);
            if (!sk.HasWeights(input.Verts.Count) || sk.BoneIds.Count == 0)
            {
                warnings?.Add($"'{o.Name}': the Skin has no weights for this mesh (press Bind); exported without bones.");
                continue;
            }
            if (sk.BoneIds.Count > 254) { warnings?.Add($"'{o.Name}': more than 254 bones; exported without bones."); continue; }
            res[o.Id] = new Source { Skin = (SkinModifier)sk.Clone(), Points = input.Verts.ToArray() };
            o.Modifiers = mods.Where((m, i) => i != si).ToList();
            o.Touch();
        }
        return res;
    }

    /// <summary>The skeleton (root + the Skin's bones, parents before children) and weights, in model space:
    /// <paramref name="toModel"/> is the object -> model matrix the model was built with.</summary>
    public static ModelSkin Make(Scene scene, SceneObject o, Source src, Matrix4x4 toModel, string modelName)
    {
        var sk = src.Skin;
        var skin = new ModelSkin();
        // object (bind) space -> model space, and world (bind) -> model space
        var objBind = SkinModifier.Unpack(sk.BindObject);
        Matrix4x4.Invert(objBind, out var objBindInv);
        var worldToModel = objBindInv * toModel;
        string root = Clean(modelName) + "_root";
        skin.Bones.Add(new SkinBone { Name = root, Tag = 0, Parent = -1 });

        // bone order: parents first (a bone's parent is its scene parent when that is one of the Skin's bones)
        int n = sk.BoneIds.Count;
        var parentSlot = new int[n];
        for (int b = 0; b < n; b++)
        {
            parentSlot[b] = -1;
            var bo = scene.Find(sk.BoneIds[b]);
            var p = bo != null ? scene.Parent(bo) : null;
            int guard = 0;
            while (p != null && guard++ < 256)
            {
                int ps = sk.BoneIds.IndexOf(p.Id);
                if (ps >= 0) { parentSlot[b] = ps; break; }
                p = scene.Parent(p);
            }
        }
        var order = new List<int>(); var placed = new bool[n];
        void Place(int b, int depth) { if (placed[b] || depth > n) return; if (parentSlot[b] >= 0) Place(parentSlot[b], depth + 1); if (!placed[b]) { placed[b] = true; order.Add(b); } }
        for (int b = 0; b < n; b++) Place(b, 0);
        var exportIndex = new int[n];
        for (int i = 0; i < order.Count; i++) exportIndex[order[i]] = i + 1;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        foreach (int b in order)
        {
            var bo = scene.Find(sk.BoneIds[b]);
            string name = Clean(bo?.Name ?? $"bone{b}");
            while (!names.Add(name)) name += "_2";
            var bind = b < sk.BindBones.Count ? SkinModifier.Unpack(sk.BindBones[b]) : Matrix4x4.Identity;
            var m = Rigid(bind * worldToModel);
            int parent = parentSlot[b] >= 0 ? exportIndex[parentSlot[b]] : 0;
            var pm = skin.Bones[parent].Model;
            Matrix4x4.Invert(pm, out var pInv);
            var local = m * pInv;
            Matrix4x4.Decompose(local, out _, out var rot, out var tr);
            skin.Bones.Add(new SkinBone { Name = name, Tag = Tag(name), Parent = parent, Translation = tr, Rotation = Quaternion.Normalize(rot), Model = m });
        }

        skin.Points = src.Points.Select(p => Vector3.Transform(p, toModel)).ToArray();
        int nv = src.Points.Length;
        skin.Idx = new int[nv * 4]; skin.W = new float[nv * 4];
        for (int i = 0; i < nv * 4; i++)
        {
            int slot = sk.WIdx[i];
            skin.Idx[i] = slot >= 0 && slot < n ? exportIndex[slot] : -1;
            skin.W[i] = skin.Idx[i] >= 0 ? sk.WVal[i] : 0;
        }
        return skin;
    }

    /// <summary>Rotation and translation only (bones carry no scale in the game).</summary>
    static Matrix4x4 Rigid(Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out _, out var r, out var t)) { r = Quaternion.Identity; t = m.Translation; }
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
    }

    static string Clean(string s) => new((s ?? "bone").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
}
