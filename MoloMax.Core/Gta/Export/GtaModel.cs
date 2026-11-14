using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Gta.Export;

/// <summary>Triangles of one material in model space, welded, at most 65k vertices (16-bit index buffer).</summary>
public sealed class ModelPart
{
    public GtaMaterial Material;
    public List<RenderVertex> Verts = new();
    public List<int> Indices = new();
    /// <summary>Skinned models: per vertex 4 weights (bytes adding up to 255) then 4 bone indices.</summary>
    public byte[] Blend;
}

/// <summary>A model to export as one .ydr: its parts, bounds, LOD and optional embedded collision.</summary>
/// <summary>A light placed in model space.</summary>
public sealed class ModelLight
{
    public Vector3 Position, Direction = -Vector3.UnitZ, Tangent = Vector3.UnitX;
    public GtaLight L;

    public static ModelLight From(SceneObject o, Matrix4x4 toModel)
    {
        var m = o.World * toModel;
        return new ModelLight
        {
            Position = Vector3.Transform(Vector3.Zero, m),
            Direction = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, m)),
            Tangent = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, m)),
            L = o.Gta.Light ?? new GtaLight(),
        };
    }
}

public sealed class GtaModel
{
    public string Name;
    public List<ModelPart> Parts = new();
    public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    public float LodDist = 100;
    public CollisionMesh EmbeddedCollision;
    /// <summary>Lower detail versions (Med, Low, Very low) in the same drawable.</summary>
    public List<GtaModel> Lods = new();
    /// <summary>Where High, Med, Low, Very low stop (metres).</summary>
    public float[] LodDists;
    /// <summary>Lights carried by this drawable (model space).</summary>
    public List<ModelLight> Lights = new();
    /// <summary>Skeleton and weights when the model is skinned (Skin modifier), else null.</summary>
    public ModelSkin Skin;
    public int VertexCount => Parts.Sum(p => p.Verts.Count);
    public bool IsEmpty => Parts.Count == 0;
    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;
    public float Radius => IsEmpty ? 0 : (Max - Min).Length() * 0.5f;

    const int MaxPartVerts = 65000;

    /// <summary>Builds a model from objects; each item gives the matrix from object space to model space.</summary>
    public static GtaModel Build(string name, Scene scene, IEnumerable<(SceneObject Obj, Matrix4x4 ToModel)> items)
    {
        var model = new GtaModel { Name = name };
        var open = new Dictionary<int, ModelPart>();      // material id -> part being filled
        var weld = new Dictionary<int, Dictionary<VKey, int>>();
        foreach (var (o, toModel) in items)
        {
            var mesh = o.Mesh.Clone();
            mesh.Transform(toModel); // also flips the faces back when the matrix mirrors
            foreach (var g in RenderMeshBuilder.Build(mesh))
            {
                var mat = scene.Material(g.MaterialId);
                for (int t = 0; t + 2 < g.Indices.Count; t += 3)
                {
                    if (!open.TryGetValue(mat.Id, out var part) || part.Verts.Count > MaxPartVerts - 3)
                    {
                        part = new ModelPart { Material = mat };
                        model.Parts.Add(part);
                        open[mat.Id] = part;
                        weld[mat.Id] = new Dictionary<VKey, int>();
                    }
                    var map = weld[mat.Id];
                    for (int k = 0; k < 3; k++)
                    {
                        var v = g.Vertices[g.Indices[t + k]];
                        var key = new VKey(v);
                        if (!map.TryGetValue(key, out int idx))
                        {
                            idx = part.Verts.Count;
                            part.Verts.Add(v);
                            map[key] = idx;
                            model.Min = Vector3.Min(model.Min, v.Position);
                            model.Max = Vector3.Max(model.Max, v.Position);
                        }
                        part.Indices.Add(idx);
                    }
                }
            }
        }
        return model;
    }

    /// <summary>Vertex identity for welding (quantised so float noise does not split vertices).</summary>
    readonly record struct VKey(int Px, int Py, int Pz, int Nx, int Ny, int Nz, int U, int V, uint C, int Tw, uint C1)
    {
        public VKey(RenderVertex v) : this(
            Q(v.Position.X, 1e4f), Q(v.Position.Y, 1e4f), Q(v.Position.Z, 1e4f),
            Q(v.Normal.X, 1e3f), Q(v.Normal.Y, 1e3f), Q(v.Normal.Z, 1e3f),
            Q(v.UV.X, 1e5f), Q(v.UV.Y, 1e5f),
            (uint)(B(v.Colour.X) | B(v.Colour.Y) << 8 | B(v.Colour.Z) << 16 | B(v.Colour.W) << 24),
            v.Tangent.W < 0 ? -1 : 1, v.Colour1) { }
        static int Q(float f, float s) => (int)MathF.Round(f * s);
        static int B(float f) => (int)Math.Clamp(f * 255f + 0.5f, 0, 255);
    }
}

/// <summary>Collision triangles grouped into bound children (one per room, 32k vertices max each).</summary>
public sealed class CollisionMesh
{
    public sealed class Child
    {
        public List<Vector3> Verts = new();
        public List<(int A, int B, int C, int Mat)> Tris = new();
        /// <summary>(material type, room id, material flags) per material slot.</summary>
        public List<(int Type, int Room, uint Flags)> Materials = new();
        public Dictionary<(int, int, int), int> Weld = new();
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    }

    public List<Child> Children = new();
    readonly Dictionary<int, Child> openByRoom = new();
    const int MaxChildVerts = 32000, MaxChildPolys = 32000;

    public bool IsEmpty => Children.All(c => c.Tris.Count == 0);
    public int TriangleCount => Children.Sum(c => c.Tris.Count);

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, int materialType, int roomId, uint flags = 0)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-14f) return; // degenerate
        if (!openByRoom.TryGetValue(roomId, out var ch) || ch.Verts.Count > MaxChildVerts - 3 || ch.Tris.Count >= MaxChildPolys)
        {
            ch = new Child();
            Children.Add(ch);
            openByRoom[roomId] = ch;
        }
        int mi = ch.Materials.IndexOf((materialType, roomId, flags));
        if (mi < 0) { mi = ch.Materials.Count; ch.Materials.Add((materialType, roomId, flags)); }
        ch.Tris.Add((V(ch, a), V(ch, b), V(ch, c), mi));
    }

    static int V(Child ch, Vector3 p)
    {
        var key = ((int)MathF.Round(p.X * 1000), (int)MathF.Round(p.Y * 1000), (int)MathF.Round(p.Z * 1000));
        if (ch.Weld.TryGetValue(key, out int i)) return i;
        i = ch.Verts.Count; ch.Verts.Add(p); ch.Weld[key] = i;
        ch.Min = Vector3.Min(ch.Min, p); ch.Max = Vector3.Max(ch.Max, p);
        return i;
    }

    /// <summary>Every triangle of an object through a matrix, wound outward even when the matrix mirrors.</summary>
    public static void ForEachTriangle(SceneObject o, Matrix4x4 toSpace, Action<Vector3, Vector3, Vector3> visit)
    {
        var m = o.Mesh;
        bool mirror = toSpace.GetDeterminant() < 0;
        foreach (var f in m.Faces)
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                var pa = Vector3.Transform(m.Verts[f.V[a]], toSpace);
                var pb = Vector3.Transform(m.Verts[f.V[b]], toSpace);
                var pc = Vector3.Transform(m.Verts[f.V[c]], toSpace);
                if (mirror) visit(pa, pc, pb); else visit(pa, pb, pc);
            }
    }

    /// <summary>Adds every triangle of an object (through a matrix).</summary>
    public void AddObject(SceneObject o, Matrix4x4 toSpace, int materialType, Func<Vector3, int> roomOf, uint flags = 0) =>
        ForEachTriangle(o, toSpace, (a, b, c) => AddTriangle(a, b, c, materialType, roomOf((a + b + c) / 3f), flags));

    /// <summary>A Collision object with its own material, flags (auto or set) and forced RoomID.</summary>
    public void AddCollisionObject(SceneObject o, Matrix4x4 toSpace, int defaultRoom = 0)
    {
        int type = Math.Clamp(o.Gta.CollisionMaterial, 0, CollisionMaterials.Names.Length - 1);
        int room = o.Gta.RoomIdOverride >= 0 ? Math.Min(31, o.Gta.RoomIdOverride) : defaultRoom;
        AddObject(o, toSpace, type, _ => room, CollisionFlags.Resolve(o));
    }

    /// <summary>A box collision (prop default).</summary>
    public static CollisionMesh Box(Vector3 min, Vector3 max, int materialType)
    {
        var c = new CollisionMesh();
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++) p[i] = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        int[][] quads = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
        foreach (var q in quads)
        {
            c.AddTriangle(p[q[0]], p[q[1]], p[q[2]], materialType, 0);
            c.AddTriangle(p[q[0]], p[q[2]], p[q[3]], materialType, 0);
        }
        return c;
    }
}
