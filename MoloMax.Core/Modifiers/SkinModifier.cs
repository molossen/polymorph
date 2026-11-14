using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Modifiers;

/// <summary>
/// 3ds Max Skin / Blender Armature: the mesh follows bones (scene objects marked as bones). Each vertex has up to 4
/// bones with weights that add up to 1 (painted with the weight brush, or made automatically from the distance to the
/// bones). The pose the weights were made in is the bind pose; moving or turning a bone then bends the mesh.
/// Weights are stored per vertex of the mesh coming into the modifier: changing that mesh's points count afterwards
/// leaves them unused until "Auto weights" is run again (3ds Max behaves the same).
/// </summary>
public sealed class SkinModifier : Modifier
{
    public List<int> BoneIds = new();
    /// <summary>World matrix of each bone in the bind pose (16 floats each, row-major like Matrix4x4).</summary>
    public List<float[]> BindBones = new();
    /// <summary>World matrix of the skinned object in the bind pose, and its id (the modifier needs its current place).</summary>
    public float[] BindObject; public int OwnerId = -1;
    /// <summary>Per vertex, 4 bone slots (index into BoneIds, -1 empty) and their weights.</summary>
    public int[] WIdx = Array.Empty<int>(); public float[] WVal = Array.Empty<float>();
    /// <summary>Points count of the mesh the weights were made for.</summary>
    public int VertexCount;

    /// <summary>Current world matrix of a scene object by id (set by the editor; null outside it: no deformation).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public static Func<int, Matrix4x4?> WorldOf;

    public SkinModifier() { Name = "Skin"; }
    public override string TypeName => "Skin";
    public override ModifierCategory Category => ModifierCategory.Deform;

    public static float[] Pack(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
    public static Matrix4x4 Unpack(float[] a) => a is { Length: 16 } ? new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]) : Matrix4x4.Identity;

    public bool HasWeights(int verts) => VertexCount == verts && WIdx.Length == verts * 4 && WVal.Length == verts * 4;

    /// <summary>The skinning matrix of each bone, in the object's local space (bind local -> posed local).</summary>
    public Matrix4x4[] BoneMatrices()
    {
        var res = new Matrix4x4[BoneIds.Count];
        var objNow = OwnerId >= 0 && WorldOf?.Invoke(OwnerId) is { } ow ? ow : Unpack(BindObject);
        Matrix4x4.Invert(objNow, out var objInv);
        var objBind = Unpack(BindObject);
        for (int b = 0; b < BoneIds.Count; b++)
        {
            var bind = b < BindBones.Count ? Unpack(BindBones[b]) : Matrix4x4.Identity;
            var now = WorldOf?.Invoke(BoneIds[b]) ?? bind;
            Matrix4x4.Invert(bind, out var bindInv);
            res[b] = objBind * bindInv * now * objInv;
        }
        return res;
    }

    public override bool IsNoOp(PolyMesh m, bool render)
    {
        if (!HasWeights(m.Verts.Count) || BoneIds.Count == 0) return true;
        foreach (var mt in BoneMatrices()) if (!Near(mt, Matrix4x4.Identity)) return false;
        return true;
    }
    static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (MathF.Abs(a[i, j] - b[i, j]) > 1e-5f) return false;
        return true;
    }

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        if (!HasWeights(m.Verts.Count) || BoneIds.Count == 0) return m;
        var mats = BoneMatrices();
        var v = m.Verts;
        for (int i = 0; i < v.Count; i++)
        {
            var p = v[i]; var r = Vector3.Zero; float tw = 0;
            for (int k = 0; k < 4; k++)
            {
                int b = WIdx[i * 4 + k]; float w = WVal[i * 4 + k];
                if (b < 0 || b >= mats.Length || w <= 0) continue;
                r += Vector3.Transform(p, mats[b]) * w; tw += w;
            }
            v[i] = tw > 1e-6f ? r / tw : p;
        }
        return m;
    }

    public override Modifier Clone()
    {
        var c = (SkinModifier)MemberwiseClone();
        c.BoneIds = new List<int>(BoneIds); c.BindBones = BindBones.Select(a => (float[])a.Clone()).ToList();
        c.BindObject = (float[])BindObject?.Clone(); c.WIdx = (int[])WIdx.Clone(); c.WVal = (float[])WVal.Clone();
        return c;
    }

    // ------------------------------------------------------------------------------------------- weights

    /// <summary>Weight of bone slot <paramref name="bone"/> (index in BoneIds) on vertex <paramref name="v"/>.</summary>
    public float Weight(int v, int bone)
    {
        for (int k = 0; k < 4; k++) if (WIdx[v * 4 + k] == bone) return WVal[v * 4 + k];
        return 0;
    }

    /// <summary>Sets one bone's weight on a vertex; the others are scaled so the total stays 1 (3ds Max normalize).</summary>
    public void SetWeight(int v, int bone, float w)
    {
        w = Math.Clamp(w, 0, 1);
        int o = v * 4, slot = -1;
        for (int k = 0; k < 4; k++) if (WIdx[o + k] == bone) slot = k;
        if (slot < 0)
        {
            if (w <= 0) return;
            // the empty or the weakest slot takes this bone
            slot = 0; for (int k = 1; k < 4; k++) if (WIdx[o + k] < 0 || WVal[o + k] < WVal[o + slot]) { slot = k; if (WIdx[o + k] < 0) break; }
            WIdx[o + slot] = bone;
        }
        float others = 0; for (int k = 0; k < 4; k++) if (k != slot && WIdx[o + k] >= 0) others += WVal[o + k];
        WVal[o + slot] = w;
        float rest = 1 - w;
        for (int k = 0; k < 4; k++)
        {
            if (k == slot || WIdx[o + k] < 0) continue;
            WVal[o + k] = others > 1e-6f ? WVal[o + k] / others * rest : 0;
        }
        if (others <= 1e-6f) WVal[o + slot] = 1; // nothing else to give the rest to
        for (int k = 0; k < 4; k++) if (WIdx[o + k] >= 0 && WVal[o + k] <= 1e-4f && k != slot) { WIdx[o + k] = -1; WVal[o + k] = 0; }
    }

    /// <summary>Binds to these bones in their current pose and makes weights from the distance of each point to each
    /// bone (the 4 nearest, falling off with distance), like 3ds Max envelopes / Blender automatic weights.</summary>
    public void Bind(PolyMesh m, Matrix4x4 objWorld, IReadOnlyList<(int Id, Matrix4x4 World, float Length)> bones)
    {
        BoneIds = bones.Select(b => b.Id).ToList();
        BindBones = bones.Select(b => Pack(b.World)).ToList();
        BindObject = Pack(objWorld);
        int n = m.Verts.Count; VertexCount = n;
        WIdx = new int[n * 4]; WVal = new float[n * 4];
        Array.Fill(WIdx, -1);
        var segs = bones.Select(b => (A: b.World.Translation, B: Vector3.Transform(new Vector3(0, 0, MathF.Max(b.Length, 1e-3f)), b.World))).ToArray();
        float size = 0;
        foreach (var s in segs) size = MathF.Max(size, Vector3.Distance(s.A, s.B));
        var dist = new float[segs.Length];
        for (int i = 0; i < n; i++)
        {
            var p = Vector3.Transform(m.Verts[i], objWorld);
            for (int b = 0; b < segs.Length; b++)
            {
                var (a, c) = segs[b]; var ab = c - a; float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
                dist[b] = Vector3.Distance(p, a + ab * t);
            }
            var order = Enumerable.Range(0, segs.Length).OrderBy(b => dist[b]).Take(4).ToArray();
            float near = dist[order[0]];
            float sum = 0;
            var w = new float[order.Length];
            for (int k = 0; k < order.Length; k++)
            {
                // falls off quickly past the nearest bone: joints blend, far bones do not pull
                float x = (dist[order[k]] + 1e-4f) / (near + 1e-4f + size * 0.05f);
                w[k] = 1 / MathF.Pow(x, 6);
                sum += w[k];
            }
            for (int k = 0; k < order.Length; k++)
            {
                float v = w[k] / sum;
                if (v < 0.01f) continue;
                WIdx[i * 4 + k] = order[k]; WVal[i * 4 + k] = v;
            }
            Normalize(i);
        }
    }

    public void Normalize(int v)
    {
        int o = v * 4; float s = 0;
        for (int k = 0; k < 4; k++) if (WIdx[o + k] >= 0) s += WVal[o + k];
        if (s <= 1e-6f) return;
        for (int k = 0; k < 4; k++) if (WIdx[o + k] >= 0) WVal[o + k] /= s;
    }

    /// <summary>Weight brush: paints bone <paramref name="bone"/> on the points within <paramref name="radius"/> of
    /// <paramref name="centre"/> (same space as <paramref name="pts"/>), with a smooth falloff. Mode 0 add, 1 subtract, 2 smooth (towards the
    /// neighbours' average). Returns how many points changed.</summary>
    public int Paint(IReadOnlyList<Vector3> pts, Vector3 centre, float radius, int bone, float strength, int mode, List<int>[] vertNeighbours = null)
    {
        if (!HasWeights(pts.Count)) return 0;
        int changed = 0;
        var target = new List<(int V, float W)>();
        for (int i = 0; i < pts.Count; i++)
        {
            float dd = Vector3.Distance(pts[i], centre);
            if (dd > radius) continue;
            float f = 1 - dd / radius; f = f * f * (3 - 2 * f); // smoothstep falloff
            float cur = Weight(i, bone), nw;
            if (mode == 0) nw = cur + (1 - cur) * strength * f;
            else if (mode == 1) nw = cur - cur * strength * f;
            else
            {
                if (vertNeighbours == null || vertNeighbours[i].Count == 0) continue;
                float avg = 0; foreach (var nb in vertNeighbours[i]) avg += Weight(nb, bone); avg /= vertNeighbours[i].Count;
                nw = cur + (avg - cur) * strength * f;
            }
            if (MathF.Abs(nw - cur) > 1e-5f) target.Add((i, nw));
        }
        foreach (var (v, w) in target) { SetWeight(v, bone, w); changed++; }
        return changed;
    }
}
