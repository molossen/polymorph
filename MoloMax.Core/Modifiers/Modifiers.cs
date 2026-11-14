using System.Numerics;
using System.Text.Json.Serialization;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Modifiers;

/// <summary>Blender modifier categories, as in the Add Modifier menu.</summary>
public enum ModifierCategory { Edit, Generate, Deform, Normals }

/// <summary>
/// A non-destructive modifier, evaluated top to bottom like Blender's modifier stack.
/// The base mesh is never changed until the modifier is applied.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MirrorModifier), "mirror")]
[JsonDerivedType(typeof(SymmetryModifier), "symmetry")]
[JsonDerivedType(typeof(ArrayModifier), "array")]
[JsonDerivedType(typeof(SolidifyModifier), "solidify")]
[JsonDerivedType(typeof(SubdivisionModifier), "subsurf")]
[JsonDerivedType(typeof(WeldModifier), "weld")]
[JsonDerivedType(typeof(CleanUpModifier), "clean_up")]
[JsonDerivedType(typeof(TriangulateModifier), "triangulate")]
[JsonDerivedType(typeof(SmoothModifier), "smooth")]
[JsonDerivedType(typeof(SmoothByAngleModifier), "smooth_by_angle")]
[JsonDerivedType(typeof(DisplaceNoiseModifier), "displace")]
[JsonDerivedType(typeof(CastModifier), "cast")]
[JsonDerivedType(typeof(EditPolyModifier), "edit_poly")]
[JsonDerivedType(typeof(UnwrapUvwModifier), "unwrap_uvw")]
[JsonDerivedType(typeof(WallOpeningModifier), "wall_opening")]
[JsonDerivedType(typeof(FfdModifier), "ffd")]
[JsonDerivedType(typeof(ProOptimizerModifier), "pro_optimizer")]
public abstract class Modifier
{
    public string Name;
    /// <summary>Blender header toggles: show in edit mode, show in viewport, use at render (= GTA export).</summary>
    public bool ShowInEditMode = true, ShowViewport = true, ShowRender = true;
    /// <summary>Panel expanded in the Modifiers tab.</summary>
    public bool Expanded = true;

    [JsonIgnore] public abstract string TypeName { get; }
    [JsonIgnore] public abstract ModifierCategory Category { get; }

    /// <summary>Returns the modified mesh. <paramref name="m"/> is a private copy and may be changed in place.</summary>
    public abstract PolyMesh Apply(PolyMesh m, bool render);

    /// <summary>Cheap check that <see cref="Apply"/> would give the mesh back unchanged (the stack then skips it, and the copy
    /// of a big mesh it would need). False when not sure.</summary>
    public virtual bool IsNoOp(PolyMesh m, bool render) => false;

    public virtual Modifier Clone() => (Modifier)MemberwiseClone();

    public static readonly (string Name, ModifierCategory Cat, Func<Modifier> Make)[] Catalog =
    {
        ("Edit Poly", ModifierCategory.Edit, () => new EditPolyModifier()),
        ("Unwrap UVW", ModifierCategory.Edit, () => new UnwrapUvwModifier()),
        ("Weld", ModifierCategory.Edit, () => new WeldModifier()),
        ("Clean Up", ModifierCategory.Edit, () => new CleanUpModifier()),
        ("Wall Opening", ModifierCategory.Generate, () => new WallOpeningModifier()),
        ("ProOptimizer", ModifierCategory.Generate, () => new ProOptimizerModifier()),
        ("Array", ModifierCategory.Generate, () => new ArrayModifier()),
        ("Mirror", ModifierCategory.Generate, () => new MirrorModifier()),
        ("Symmetry", ModifierCategory.Generate, () => new SymmetryModifier()),
        ("Solidify", ModifierCategory.Generate, () => new SolidifyModifier()),
        ("Subdivision Surface", ModifierCategory.Generate, () => new SubdivisionModifier()),
        ("Triangulate", ModifierCategory.Generate, () => new TriangulateModifier()),
        ("Cast", ModifierCategory.Deform, () => new CastModifier()),
        ("FFD 2x2x2", ModifierCategory.Deform, () => { var f = new FfdModifier(); f.SetResolution(2, 2, 2); return f; }),
        ("FFD 3x3x3", ModifierCategory.Deform, () => { var f = new FfdModifier(); f.SetResolution(3, 3, 3); return f; }),
        ("FFD 4x4x4", ModifierCategory.Deform, () => { var f = new FfdModifier(); f.SetResolution(4, 4, 4); return f; }),
        ("Displace", ModifierCategory.Deform, () => new DisplaceNoiseModifier()),
        ("Smooth", ModifierCategory.Deform, () => new SmoothModifier()),
        ("Smooth by Angle", ModifierCategory.Normals, () => new SmoothByAngleModifier()),
    };
}

/// <summary>Evaluates a modifier stack, with the same rules as Blender (viewport / edit mode / render toggles).</summary>
public static class ModifierStack
{
    public static PolyMesh Evaluate(PolyMesh baseMesh, IReadOnlyList<Modifier> mods, bool editMode, bool render)
    {
        if (mods == null || mods.Count == 0) return baseMesh;
        PolyMesh m = null;
        foreach (var mod in mods)
        {
            if (render ? !mod.ShowRender : (!mod.ShowViewport || (editMode && !mod.ShowInEditMode))) continue;
            try
            {
                if (mod.IsNoOp(m ?? baseMesh, render)) continue;
                m ??= baseMesh.Clone();
                m = mod.Apply(m, render);
            }
            catch { /* a broken modifier is skipped, like a disabled one */ }
        }
        return m ?? baseMesh;
    }

    /// <summary>Unique Blender-style name: "Mirror", "Mirror.001", ...</summary>
    public static string UniqueName(IEnumerable<Modifier> existing, string baseName)
    {
        var names = existing.Select(x => x.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (int i = 1; ; i++) { var n = $"{baseName}.{i:000}"; if (!names.Contains(n)) return n; }
    }
}

// ------------------------------------------------------------------------------------------- Generate

public sealed class MirrorModifier : Modifier
{
    public bool AxisX = true, AxisY, AxisZ;
    public bool BisectX, BisectY, BisectZ;
    public bool FlipX, FlipY, FlipZ;
    public bool Clipping;
    public bool Merge = true;
    public float MergeDistance = 0.001f;
    public bool MirrorU, MirrorV;
    public float OffsetU, OffsetV;

    public MirrorModifier() { Name = "Mirror"; }
    public override string TypeName => "Mirror";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        bool[] ax = { AxisX, AxisY, AxisZ };
        bool[] bis = { BisectX, BisectY, BisectZ };
        bool[] flip = { FlipX, FlipY, FlipZ };
        for (int a = 0; a < 3; a++)
        {
            if (!ax[a]) continue;
            if (bis[a]) m = Bisect(m, a, flip[a]);
            int n0 = m.Verts.Count;
            var copy = m.Clone();
            for (int i = 0; i < copy.Verts.Count; i++) { var p = copy.Verts[i]; Set(ref p, a, -Get(p, a)); copy.Verts[i] = p; }
            foreach (var f in copy.Faces)
            {
                f.Flip();
                for (int c = 0; c < f.UV.Length; c++)
                {
                    var uv = f.UV[c];
                    if (MirrorU) uv.X = 1 - uv.X + OffsetU;
                    if (MirrorV) uv.Y = 1 - uv.Y + OffsetV;
                    f.UV[c] = uv;
                }
            }
            m.Append(copy);
            if (Merge)
            {
                // only vertices on the mirror plane are merged with their mirror twin
                var near = new HashSet<int>();
                for (int i = 0; i < m.Verts.Count; i++)
                    if (MathF.Abs(Get(m.Verts[i], a)) <= MergeDistance) { var p = m.Verts[i]; Set(ref p, a, 0); m.Verts[i] = p; near.Add(i); }
                if (near.Count > 0) MeshOps.WeldByDistance(m, MathF.Max(MergeDistance, 1e-6f), near);
            }
            m.CleanFaces();
        }
        return m;
    }

    /// <summary>Cuts the mesh on the mirror plane and keeps the positive side (or the negative one when flipped).</summary>
    static PolyMesh Bisect(PolyMesh m, int axis, bool flip)
    {
        float s = flip ? -1 : 1;
        var res = new PolyMesh();
        var map = new Dictionary<int, int>();
        var cut = new Dictionary<EdgeKey, int>();
        int Keep(int v) { if (!map.TryGetValue(v, out var n)) map[v] = n = res.AddVertex(m.Verts[v]); return n; }
        foreach (var f in m.Faces)
        {
            var vs = new List<int>(); var uvs = new List<Vector2>(); var cols = new List<Vector4>();
            for (int i = 0; i < f.Count; i++)
            {
                int j = (i + 1) % f.Count;
                var pa = m.Verts[f.V[i]]; var pb = m.Verts[f.V[j]];
                float da = s * Get(pa, axis), db = s * Get(pb, axis);
                if (da >= 0) { vs.Add(Keep(f.V[i])); uvs.Add(f.UV[i]); cols.Add(f.Col[i]); }
                if ((da >= 0) != (db >= 0) && MathF.Abs(da - db) > 1e-12f)
                {
                    float t = da / (da - db);
                    var key = EdgeKey.Make(f.V[i], f.V[j]);
                    if (!cut.TryGetValue(key, out var nv))
                    {
                        var p = Vector3.Lerp(pa, pb, t); Set(ref p, axis, 0);
                        cut[key] = nv = res.AddVertex(p);
                    }
                    vs.Add(nv); uvs.Add(Vector2.Lerp(f.UV[i], f.UV[j], t)); cols.Add(Vector4.Lerp(f.Col[i], f.Col[j], t));
                }
            }
            if (vs.Count < 3) continue;
            var nf = res.AddFace(vs.ToArray(), f.MatId, f.Smooth);
            nf.UV = uvs.ToArray(); nf.Col = cols.ToArray();
        }
        return res;
    }

    internal static float Get(Vector3 p, int a) => a == 0 ? p.X : a == 1 ? p.Y : p.Z;
    internal static void Set(ref Vector3 p, int a, float v) { if (a == 0) p.X = v; else if (a == 1) p.Y = v; else p.Z = v; }
}

/// <summary>
/// 3ds Max Symmetry: one mirror axis, the model is sliced on the mirror plane (only one side is kept and copied),
/// the seam is welded, and the plane can be moved along the axis (the Max "Mirror" sub-object).
/// </summary>
public sealed class SymmetryModifier : Modifier
{
    public int Axis;            // 0 X, 1 Y, 2 Z
    public bool Flip;           // keep the other side
    public bool Slice = true;   // Slice Along Mirror
    public bool Weld = true;    // Weld Seam
    public float Threshold = 0.001f;
    /// <summary>Position of the mirror plane along the axis (object space, metres).</summary>
    public float Offset;

    public SymmetryModifier() { Name = "Symmetry"; }
    public override string TypeName => "Symmetry";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        var shift = new Vector3(Axis == 0 ? Offset : 0, Axis == 1 ? Offset : 0, Axis == 2 ? Offset : 0);
        if (Offset != 0) m.Transform(Matrix4x4.CreateTranslation(-shift));
        // Max keeps the positive side by default; Flip keeps the negative one
        var mir = new MirrorModifier
        {
            AxisX = Axis == 0, AxisY = Axis == 1, AxisZ = Axis == 2,
            BisectX = Slice && Axis == 0, BisectY = Slice && Axis == 1, BisectZ = Slice && Axis == 2,
            FlipX = Flip && Axis == 0, FlipY = Flip && Axis == 1, FlipZ = Flip && Axis == 2,
            Merge = Weld, MergeDistance = Threshold,
        };
        m = mir.Apply(m, render);
        if (Offset != 0) m.Transform(Matrix4x4.CreateTranslation(shift));
        return m;
    }
}

public sealed class ArrayModifier : Modifier
{
    public int Count = 2;
    public bool UseRelativeOffset = true;
    public Vector3 RelativeOffset = new(1, 0, 0);
    public bool UseConstantOffset;
    public Vector3 ConstantOffset = new(0, 0, 0);
    public bool MergeVertices;
    public float MergeDistance = 0.01f;

    public ArrayModifier() { Name = "Array"; }
    public override string TypeName => "Array";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        int count = Math.Clamp(Count, 1, 1000);
        if (count == 1 || m.Verts.Count == 0) return m;
        var (mn, mx) = m.Bounds();
        var step = Vector3.Zero;
        if (UseRelativeOffset) step += RelativeOffset * (mx - mn);
        if (UseConstantOffset) step += ConstantOffset;
        var src = m.Clone();
        for (int k = 1; k < count; k++)
        {
            var c = src.Clone();
            c.Transform(Matrix4x4.CreateTranslation(step * k));
            m.Append(c);
        }
        if (MergeVertices) MeshOps.WeldByDistance(m, MathF.Max(MergeDistance, 1e-6f));
        m.CleanFaces();
        return m;
    }
}

public sealed class SolidifyModifier : Modifier
{
    public float Thickness = 0.01f;
    /// <summary>-1: the original surface stays outside (shell grows inward), 1: grows outward.</summary>
    public float Offset = -1f;
    public bool EvenThickness;
    public bool FillRim = true;
    public bool FlipNormals;

    public SolidifyModifier() { Name = "Solidify"; }
    public override string TypeName => "Solidify";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        int n = m.Verts.Count;
        if (n == 0) return m;
        // area-weighted vertex normals (angle-weighted when "Even Thickness" so corners keep the thickness)
        var vn = new Vector3[n];
        foreach (var f in m.Faces)
        {
            var fn = m.FaceNormal(f) * MathF.Max(m.FaceArea(f), 1e-9f);
            for (int i = 0; i < f.Count; i++) vn[f.V[i]] += EvenThickness ? Vector3.Normalize(m.FaceNormal(f)) : fn;
        }
        var scale = new float[n];
        for (int i = 0; i < n; i++) { vn[i] = vn[i].LengthSquared() > 1e-20f ? Vector3.Normalize(vn[i]) : Vector3.UnitZ; scale[i] = 1; }
        if (EvenThickness)
        {
            var sum = new float[n]; var cnt = new int[n];
            foreach (var f in m.Faces) { var fn = m.FaceNormal(f); for (int i = 0; i < f.Count; i++) { sum[f.V[i]] += MathF.Max(0.2f, Vector3.Dot(fn, vn[f.V[i]])); cnt[f.V[i]]++; } }
            for (int i = 0; i < n; i++) if (cnt[i] > 0) scale[i] = 1f / (sum[i] / cnt[i]);
        }
        float t = Thickness;
        float outer = t * (Offset + 1) / 2, inner = t * (Offset - 1) / 2;
        var res = new PolyMesh();
        for (int i = 0; i < n; i++) res.AddVertex(m.Verts[i] + vn[i] * outer * scale[i]);
        for (int i = 0; i < n; i++) res.AddVertex(m.Verts[i] + vn[i] * inner * scale[i]);
        foreach (var f in m.Faces)
        {
            var a = res.AddFace((int[])f.V.Clone(), f.MatId, f.Smooth); a.UV = (Vector2[])f.UV.Clone(); a.Col = (Vector4[])f.Col.Clone();
            var b = res.AddFace(f.V.Select(v => v + n).ToArray(), f.MatId, f.Smooth); b.UV = (Vector2[])f.UV.Clone(); b.Col = (Vector4[])f.Col.Clone();
            b.Flip();
            if (FlipNormals) { a.Flip(); b.Flip(); }
        }
        if (FillRim)
        {
            var map = m.BuildEdgeMap();
            foreach (var (e, faces) in map)
            {
                if (faces.Count != 1) continue;
                var f = m.Faces[faces[0]];
                // walk direction of the edge inside its face keeps the rim facing out
                int ia = f.IndexOf(e.A), ib = f.IndexOf(e.B);
                int va = e.A, vb = e.B, ca = ia, cb = ib;
                if ((ia + 1) % f.Count != ib) { va = e.B; vb = e.A; ca = ib; cb = ia; }
                var q = res.AddFace(new[] { vb, va, va + n, vb + n }, f.MatId, 0);
                q.Col = new[] { f.Col[cb], f.Col[ca], f.Col[ca], f.Col[cb] };
                float len = (m.Verts[va] - m.Verts[vb]).Length();
                q.UV = new[] { new Vector2(len, 0), new Vector2(0, 0), new Vector2(0, MathF.Abs(t)), new Vector2(len, MathF.Abs(t)) };
                if (FlipNormals ^ (t < 0)) q.Flip();
            }
        }
        return res;
    }
}

public sealed class SubdivisionModifier : Modifier
{
    public bool CatmullClark; // false = Simple (adds faces, keeps the shape, the default); true = Catmull-Clark (rounds)
    public int LevelsViewport = 1, LevelsRender = 2;

    public SubdivisionModifier() { Name = "Subdivision"; ShowInEditMode = true; }
    public override string TypeName => "Subdivision Surface";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        int lv = Math.Clamp(render ? LevelsRender : LevelsViewport, 0, 6);
        for (int i = 0; i < lv && m.Faces.Count < 2_000_000; i++) m = Subdivide(m, CatmullClark);
        return m;
    }

    /// <summary>One Catmull-Clark (or simple) step: every n-gon becomes n quads. UVs and colours are interpolated linearly.</summary>
    public static PolyMesh Subdivide(PolyMesh m, bool smooth)
    {
        var edgeMap = m.BuildEdgeMap();
        var res = new PolyMesh();
        int nv = m.Verts.Count;
        var facePt = new Vector3[m.Faces.Count];
        for (int fi = 0; fi < m.Faces.Count; fi++) facePt[fi] = m.FaceCenter(fi);

        // vertex points
        var vertFaces = m.BuildVertexFaces();
        var vertEdges = new List<EdgeKey>[nv];
        for (int i = 0; i < nv; i++) vertEdges[i] = new List<EdgeKey>();
        foreach (var e in edgeMap.Keys) { vertEdges[e.A].Add(e); vertEdges[e.B].Add(e); }
        for (int v = 0; v < nv; v++)
        {
            var p = m.Verts[v];
            if (smooth && vertEdges[v].Count > 0)
            {
                var border = vertEdges[v].Where(e => edgeMap[e].Count == 1).ToList();
                if (border.Count >= 2)
                {
                    var a = m.Verts[border[0].Other(v)]; var b = m.Verts[border[1].Other(v)];
                    p = p * 0.75f + (a + b) * 0.125f;
                }
                else if (border.Count == 0 && vertFaces[v].Count > 0)
                {
                    int k = vertEdges[v].Count;
                    var F = Vector3.Zero; foreach (var fi in vertFaces[v]) F += facePt[fi]; F /= vertFaces[v].Count;
                    var R = Vector3.Zero; foreach (var e in vertEdges[v]) R += (m.Verts[e.A] + m.Verts[e.B]) * 0.5f; R /= k;
                    p = (F + 2 * R + (k - 3) * p) / k;
                }
            }
            res.AddVertex(p);
        }
        // edge points
        var edgeIdx = new Dictionary<EdgeKey, int>();
        foreach (var (e, faces) in edgeMap)
        {
            var mid = (m.Verts[e.A] + m.Verts[e.B]) * 0.5f;
            if (smooth && faces.Count == 2) mid = (m.Verts[e.A] + m.Verts[e.B] + facePt[faces[0]] + facePt[faces[1]]) * 0.25f;
            edgeIdx[e] = res.AddVertex(mid);
        }
        // face points and quads
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            int c = res.AddVertex(facePt[fi]);
            int n = f.Count;
            var uvC = Vector2.Zero; var colC = Vector4.Zero;
            for (int i = 0; i < n; i++) { uvC += f.UV[i]; colC += f.Col[i]; }
            uvC /= n; colC /= n;
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n, next = (i + 1) % n;
                int ePrev = edgeIdx[EdgeKey.Make(f.V[prev], f.V[i])], eNext = edgeIdx[EdgeKey.Make(f.V[i], f.V[next])];
                var q = res.AddFace(new[] { f.V[i], eNext, c, ePrev }, f.MatId, f.Smooth == 0 && smooth ? 1u : f.Smooth);
                q.UV = new[] { f.UV[i], (f.UV[i] + f.UV[next]) * 0.5f, uvC, (f.UV[prev] + f.UV[i]) * 0.5f };
                q.Col = new[] { f.Col[i], (f.Col[i] + f.Col[next]) * 0.5f, colC, (f.Col[prev] + f.Col[i]) * 0.5f };
            }
        }
        return res;
    }
}

public sealed class TriangulateModifier : Modifier
{
    public int MinVertices = 4;
    public TriangulateModifier() { Name = "Triangulate"; }
    public override string TypeName => "Triangulate";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        var faces = new List<Face>();
        foreach (var f in m.Faces)
        {
            if (f.Count < Math.Max(4, MinVertices)) { faces.Add(f); continue; }
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                faces.Add(new Face(new[] { f.V[a], f.V[b], f.V[c] }, f.MatId, f.Smooth) { UV = new[] { f.UV[a], f.UV[b], f.UV[c] }, Col = new[] { f.Col[a], f.Col[b], f.Col[c] } });
        }
        m.Faces = faces;
        return m;
    }
}

// ------------------------------------------------------------------------------------------- Edit

public sealed class WeldModifier : Modifier
{
    public float Distance = 0.001f;
    public WeldModifier() { Name = "Weld"; }
    public override string TypeName => "Weld";
    public override ModifierCategory Category => ModifierCategory.Edit;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        MeshOps.WeldByDistance(m, MathF.Max(Distance, 1e-7f));
        m.CleanFaces();
        m.RemoveUnusedVertices();
        return m;
    }
}

/// <summary>Clean Up: fewer edges, same shape. Flat areas cut in many faces become one polygon, points in the middle
/// of straight edges go, duplicates are joined. UV seams, materials, colours and smoothing are kept.</summary>
public sealed class CleanUpModifier : Modifier
{
    public float Angle = 0.5f;
    public bool KeepUvSeams = true, StraightPoints = true, Quads;
    public CleanUpModifier() { Name = "Clean Up"; }
    public override string TypeName => "Clean Up";
    public override ModifierCategory Category => ModifierCategory.Edit;
    [System.Text.Json.Serialization.JsonIgnore] public CleanReport LastReport;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        var r = MeshClean.Run(m, new CleanOptions { Angle = Angle, KeepUvSeams = KeepUvSeams, StraightPoints = StraightPoints, Quads = Quads }, out var rep);
        LastReport = rep;
        return r;
    }
}

// ------------------------------------------------------------------------------------------- Deform

public sealed class SmoothModifier : Modifier
{
    public float Factor = 0.5f;
    public int Repeat = 1;
    public bool X = true, Y = true, Z = true;
    public SmoothModifier() { Name = "Smooth"; }
    public override string TypeName => "Smooth";
    public override ModifierCategory Category => ModifierCategory.Deform;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        var edges = m.BuildEdgeMap().Keys.ToList();
        for (int r = 0; r < Math.Clamp(Repeat, 0, 200); r++)
        {
            var sum = new Vector3[m.Verts.Count]; var cnt = new int[m.Verts.Count];
            foreach (var e in edges) { sum[e.A] += m.Verts[e.B]; cnt[e.A]++; sum[e.B] += m.Verts[e.A]; cnt[e.B]++; }
            for (int i = 0; i < m.Verts.Count; i++)
            {
                if (cnt[i] == 0) continue;
                var p = m.Verts[i]; var d = (sum[i] / cnt[i] - p) * Factor;
                m.Verts[i] = new Vector3(X ? p.X + d.X : p.X, Y ? p.Y + d.Y : p.Y, Z ? p.Z + d.Z : p.Z);
            }
        }
        return m;
    }
}

public sealed class CastModifier : Modifier
{
    public int Shape; // 0 sphere, 1 cylinder (Z), 2 cuboid
    public float Factor = 0.5f;
    public float Radius; // 0 = average distance
    public CastModifier() { Name = "Cast"; }
    public override string TypeName => "Cast";
    public override ModifierCategory Category => ModifierCategory.Deform;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        if (m.Verts.Count == 0) return m;
        var (mn, mx) = m.Bounds(); var c = (mn + mx) * 0.5f;
        Vector3 Flat(Vector3 d) => Shape == 1 ? new Vector3(d.X, d.Y, 0) : d;
        float r = Radius > 0 ? Radius : m.Verts.Average(p => Flat(p - c).Length());
        var half = (mx - mn) * 0.5f;
        for (int i = 0; i < m.Verts.Count; i++)
        {
            var d = m.Verts[i] - c; Vector3 target;
            if (Shape == 2)
            {
                // push each point to the box surface along its dominant axis
                var a = new Vector3(MathF.Abs(d.X) / MathF.Max(half.X, 1e-6f), MathF.Abs(d.Y) / MathF.Max(half.Y, 1e-6f), MathF.Abs(d.Z) / MathF.Max(half.Z, 1e-6f));
                float k = MathF.Max(a.X, MathF.Max(a.Y, a.Z));
                target = k > 1e-6f ? c + d / k : m.Verts[i];
            }
            else
            {
                var fd = Flat(d); float len = fd.Length();
                target = len > 1e-6f ? m.Verts[i] + fd / len * (r - len) : m.Verts[i];
            }
            m.Verts[i] = Vector3.Lerp(m.Verts[i], target, Factor);
        }
        return m;
    }
}

/// <summary>Displace with a procedural noise texture along the vertex normals (Blender Displace + Clouds texture).</summary>
public sealed class DisplaceNoiseModifier : Modifier
{
    public float Strength = 0.1f, Size = 1f, MidLevel = 0.5f;
    public int Seed;
    public DisplaceNoiseModifier() { Name = "Displace"; }
    public override string TypeName => "Displace";
    public override ModifierCategory Category => ModifierCategory.Deform;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        var vn = new Vector3[m.Verts.Count];
        foreach (var f in m.Faces) { var n = m.FaceNormal(f); foreach (var v in f.V) vn[v] += n; }
        float inv = 1f / MathF.Max(Size, 1e-4f);
        for (int i = 0; i < m.Verts.Count; i++)
        {
            if (vn[i].LengthSquared() < 1e-20f) continue;
            var p = m.Verts[i] * inv;
            float h = Noise3(p.X + Seed * 17.1f, p.Y, p.Z) * 0.5f + Noise3(p.X * 2 + 5, p.Y * 2, p.Z * 2) * 0.3f + Noise3(p.X * 4, p.Y * 4 + 9, p.Z * 4) * 0.2f;
            m.Verts[i] += Vector3.Normalize(vn[i]) * (h - MidLevel) * Strength;
        }
        return m;
    }

    static float Hash(int x, int y, int z)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }
    static float Noise3(float x, float y, float z)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), fx), L(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), fx), fy),
                 L(L(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), fx), L(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), fx), fy), fz);
    }
}

// ------------------------------------------------------------------------------------------- Normals

public sealed class SmoothByAngleModifier : Modifier
{
    public float Angle = 30f;
    public SmoothByAngleModifier() { Name = "Smooth by Angle"; }
    public override string TypeName => "Smooth by Angle";
    public override ModifierCategory Category => ModifierCategory.Normals;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        MeshOps.AutoSmooth(m, null, Math.Clamp(Angle, 0, 180));
        return m;
    }
}
