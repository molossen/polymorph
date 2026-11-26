using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Paint;

/// <summary>
/// Vertex colour painting (the GTA vertex colour 0 of each face corner): brush strokes in object space and fills.
/// Rules from the game: green on everything that is not emissive, white on emissive parts, red on exterior props.
/// </summary>
public static class VertexPaint
{
    public static readonly (string Name, Vector4 Colour, string Tip)[] Presets =
    {
        ("Green", new(0, 1, 0, 1), "Everything that is not emissive (interiors)"),
        ("Dark green", new(0, 0.45f, 0, 1), "Darker ambient, still correct for GTA"),
        ("White", new(1, 1, 1, 1), "Emissive parts: signs, screens, adverts"),
        ("Red", new(1, 0, 0, 1), "Exterior props (trees...), otherwise black in game"),
    };

    /// <summary>One print: every corner within the radius moves towards the colour. Returns the number of corners changed.</summary>
    public static int Stamp(PolyMesh m, Vector3 center, Vector3 normal, float radius, Vector4 colour, float strength, float hardness, bool backfaceCull = true)
    {
        int changed = 0; float inner = radius * Math.Clamp(hardness, 0, 0.99f);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            if (backfaceCull && Vector3.Dot(m.FaceNormal(f), normal) < 0.05f) continue;
            for (int i = 0; i < f.V.Length; i++)
            {
                float d = Vector3.Distance(m.Verts[f.V[i]], center);
                if (d > radius) continue;
                float a = (d <= inner ? 1 : 1 - Smooth((d - inner) / MathF.Max(radius - inner, 1e-6f))) * strength;
                if (a <= 0) continue;
                f.Col[i] = Vector4.Lerp(f.Col[i], colour, a);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Colour 1 that shows one terrain layer alone (blue: odd layers, green: layers 2 and 3).</summary>
    public static Vector4 LayerColour(int layer) => new(0, layer >= 2 ? 1 : 0, (layer & 1) == 1 ? 1 : 0, 1);

    /// <summary>Weights of the 4 terrain layers for a colour 1 (the game's blend: b mixes 0-1 and 2-3, g mixes the two pairs).</summary>
    public static Vector4 LayerWeights(Vector4 c1) => new((1 - c1.Z) * (1 - c1.Y), c1.Z * (1 - c1.Y), (1 - c1.Z) * c1.Y, c1.Z * c1.Y);

    /// <summary>Terrain: paints one of the 4 layers (moves vertex colour 1 of the corners in the brush towards that layer).</summary>
    public static int StampLayer(PolyMesh m, Vector3 center, Vector3 normal, float radius, int layer, float strength, float hardness, bool backfaceCull = true, Func<int, bool> faceOk = null)
    {
        int changed = 0; float inner = radius * Math.Clamp(hardness, 0, 0.99f);
        var target = LayerColour(layer);
        int nearV = -1; float nearD = float.MaxValue;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            if (faceOk != null && !faceOk(f.MatId)) continue; // terrain layers: only the terrain faces (not the roads, walls, decals of the model)
            if (backfaceCull && Vector3.Dot(m.FaceNormal(f), normal) < 0.05f) continue;
            for (int i = 0; i < f.V.Length; i++)
            {
                float d = Vector3.Distance(m.Verts[f.V[i]], center);
                if (d < nearD) { nearD = d; nearV = f.V[i]; }
                if (d > radius) continue;
                float a = (d <= inner ? 1 : 1 - Smooth((d - inner) / MathF.Max(radius - inner, 1e-6f))) * strength;
                if (a <= 0) continue;
                Blend(f, i, target, a);
                changed++;
            }
        }
        // big terrain triangles (a map's ground has corners metres apart): a brush smaller than them reaches no corner and
        // nothing happened. Then the brush grows to the nearest corners (the blend spreads over their triangles, as in game).
        if (changed == 0 && nearV >= 0 && nearD < MathF.Max(radius * 8, 16f))
        {
            float r2 = nearD * 1.35f + 0.01f;
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi];
                if (faceOk != null && !faceOk(f.MatId)) continue;
                if (backfaceCull && Vector3.Dot(m.FaceNormal(f), normal) < 0.05f) continue;
                for (int i = 0; i < f.V.Length; i++)
                {
                    float d = Vector3.Distance(m.Verts[f.V[i]], center);
                    if (d > r2) continue;
                    float a = (d <= nearD * 1.05f ? 1 : 1 - Smooth((d - nearD * 1.05f) / MathF.Max(r2 - nearD * 1.05f, 1e-6f))) * strength;
                    if (a <= 0) continue;
                    Blend(f, i, target, a); changed++;
                }
            }
        }
        return changed;
    }

    static void Blend(Face f, int i, Vector4 target, float a)
    {
        f.Col1 ??= new Vector4[f.V.Length];
        if (f.Col1[i].W == 0) f.Col1[i].W = 1;
        var c = f.Col1[i];
        f.Col1[i] = new Vector4(c.X, c.Y + (target.Y - c.Y) * a, c.Z + (target.Z - c.Z) * a, 1);
    }

    /// <summary>Terrain: one layer on whole faces.</summary>
    public static void FillLayer(PolyMesh m, IEnumerable<int> faces, int layer)
    {
        var c = LayerColour(layer);
        foreach (var fi in faces) { var f = m.Faces[fi]; f.Col1 = new Vector4[f.V.Length]; for (int i = 0; i < f.V.Length; i++) f.Col1[i] = c; }
    }

    /// <summary>Fills faces (all corners) with a colour.</summary>
    public static void Fill(PolyMesh m, IEnumerable<int> faces, Vector4 colour)
    {
        foreach (var fi in faces) { var f = m.Faces[fi]; for (int i = 0; i < f.Col.Length; i++) f.Col[i] = colour; }
    }

    /// <summary>Faces of the connected element of a face.</summary>
    public static IEnumerable<int> ElementOf(PolyMesh m, int face)
    {
        var el = TexelMap.MeshElements(m);
        if (face < 0 || face >= el.Length) yield break;
        for (int i = 0; i < el.Length; i++) if (el[i] == el[face]) yield return i;
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
}
