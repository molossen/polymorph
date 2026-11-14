using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Modifiers;

/// <summary>
/// 3ds Max FFD (free-form deformation) 2x2x2 / 3x3x3 / 4x4x4, Blender Lattice: a cage of control points around the
/// object; moving the points bends the mesh smoothly inside the cage (Bernstein / Bezier volume).
/// The cage is fitted to the mesh bounds when the modifier is added (or with Reset / Conform).
/// </summary>
public sealed class FfdModifier : Modifier
{
    /// <summary>Points per axis (2 to 4, as FFD 2x2x2 / 3x3x3 / 4x4x4; each axis can differ like FFD (box)).</summary>
    public int Nx = 3, Ny = 3, Nz = 3;
    /// <summary>The cage box in the object's local space. Empty (Max &lt; Min) = fitted to the mesh at the next evaluation.</summary>
    public float MinX, MinY, MinZ, MaxX = -1, MaxY = -1, MaxZ = -1;
    /// <summary>Offset of every control point from its rest position, x fastest then y then z (Nx*Ny*Nz entries).</summary>
    public float[] Offsets = Array.Empty<float>();
    /// <summary>3ds Max "Only In Volume": points outside the cage are not moved. Off = "All Vertices" (the cage field extends).</summary>
    public bool InsideOnly = true;

    public FfdModifier() { Name = "FFD 3x3x3"; }
    public override string TypeName => "FFD";
    public override ModifierCategory Category => ModifierCategory.Deform;

    public int Count => Nx * Ny * Nz;
    public int Index(int i, int j, int k) => i + Nx * (j + Ny * k);
    public bool HasBox => MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ;
    public Vector3 Min => new(MinX, MinY, MinZ);
    public Vector3 Max => new(MaxX, MaxY, MaxZ);

    public void SetResolution(int nx, int ny, int nz)
    {
        nx = Math.Clamp(nx, 2, 8); ny = Math.Clamp(ny, 2, 8); nz = Math.Clamp(nz, 2, 8);
        if (nx == Nx && ny == Ny && nz == Nz && Offsets.Length == Count * 3) return;
        Nx = nx; Ny = ny; Nz = nz;
        Offsets = new float[Count * 3];
        Name = Nx == Ny && Ny == Nz ? $"FFD {Nx}x{Nx}x{Nx}" : $"FFD (box) {Nx}x{Ny}x{Nz}";
    }

    /// <summary>Fits the cage to the mesh bounds (3ds Max Conform to Shape / Reset). Keeps the point offsets unless reset.</summary>
    public void Fit(PolyMesh m, bool reset)
    {
        if (m == null || m.Verts.Count == 0) return;
        var (mn, mx) = m.Bounds();
        // a flat object still gets a cage with some depth
        var size = mx - mn; float pad = MathF.Max(MathF.Max(size.X, size.Y), size.Z) * 0.01f + 1e-3f;
        if (size.X < pad) { mn.X -= pad; mx.X += pad; }
        if (size.Y < pad) { mn.Y -= pad; mx.Y += pad; }
        if (size.Z < pad) { mn.Z -= pad; mx.Z += pad; }
        MinX = mn.X; MinY = mn.Y; MinZ = mn.Z; MaxX = mx.X; MaxY = mx.Y; MaxZ = mx.Z;
        if (reset || Offsets.Length != Count * 3) Offsets = new float[Count * 3];
    }

    public Vector3 Offset(int idx) => Offsets.Length >= (idx + 1) * 3 ? new Vector3(Offsets[idx * 3], Offsets[idx * 3 + 1], Offsets[idx * 3 + 2]) : Vector3.Zero;
    public void SetOffset(int idx, Vector3 v)
    {
        if (Offsets.Length != Count * 3) Offsets = new float[Count * 3];
        Offsets[idx * 3] = v.X; Offsets[idx * 3 + 1] = v.Y; Offsets[idx * 3 + 2] = v.Z;
    }

    /// <summary>Rest position of a control point (local space).</summary>
    public Vector3 RestPoint(int i, int j, int k)
    {
        var mn = Min; var mx = Max;
        return new Vector3(
            mn.X + (mx.X - mn.X) * i / (Nx - 1f),
            mn.Y + (mx.Y - mn.Y) * j / (Ny - 1f),
            mn.Z + (mx.Z - mn.Z) * k / (Nz - 1f));
    }
    public Vector3 Point(int idx)
    {
        int i = idx % Nx, j = idx / Nx % Ny, k = idx / (Nx * Ny);
        return RestPoint(i, j, k) + Offset(idx);
    }

    static void Bernstein(int n, float t, Span<float> b)
    {
        // degree n-1 Bernstein basis
        int d = n - 1;
        for (int i = 0; i < n; i++)
        {
            float c = Binom(d, i);
            b[i] = c * MathF.Pow(t, i) * MathF.Pow(1 - t, d - i);
        }
    }
    static float Binom(int n, int k) { float r = 1; for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; return r; }

    public override bool IsNoOp(PolyMesh m, bool render)
    {
        if (!HasBox) return false;
        foreach (var o in Offsets) if (o != 0) return false;
        return true;
    }

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        if (m.Verts.Count == 0) return m;
        if (!HasBox || Offsets.Length != Count * 3) { Fit(m, Offsets.Length != Count * 3); }
        var mn = Min; var size = Max - Min;
        var inv = new Vector3(1 / MathF.Max(size.X, 1e-9f), 1 / MathF.Max(size.Y, 1e-9f), 1 / MathF.Max(size.Z, 1e-9f));
        var offs = new Vector3[Count];
        bool any = false;
        for (int q = 0; q < Count; q++) { offs[q] = Offset(q); any |= offs[q] != Vector3.Zero; }
        if (!any) return m;
        int nx = Nx, ny = Ny, nz = Nz;
        var verts = m.Verts;
        Parallel.For(0, (verts.Count + 4095) / 4096, blk =>
        {
            Span<float> bu = stackalloc float[8], bv = stackalloc float[8], bw = stackalloc float[8];
            int e = Math.Min(verts.Count, (blk + 1) * 4096);
            for (int vi = blk * 4096; vi < e; vi++)
            {
                var p = verts[vi];
                var t = (p - mn) * inv;
                if (InsideOnly && (t.X < -1e-5f || t.Y < -1e-5f || t.Z < -1e-5f || t.X > 1 + 1e-5f || t.Y > 1 + 1e-5f || t.Z > 1 + 1e-5f)) continue;
                Bernstein(nx, Math.Clamp(t.X, 0, 1), bu); Bernstein(ny, Math.Clamp(t.Y, 0, 1), bv); Bernstein(nz, Math.Clamp(t.Z, 0, 1), bw);
                var d = Vector3.Zero;
                for (int k = 0; k < nz; k++)
                    for (int j = 0; j < ny; j++)
                    {
                        float wjk = bv[j] * bw[k];
                        if (wjk == 0) continue;
                        int baseIdx = nx * (j + ny * k);
                        for (int i = 0; i < nx; i++) d += offs[baseIdx + i] * (bu[i] * wjk);
                    }
                verts[vi] = p + d;
            }
        });
        return m;
    }

    public override Modifier Clone()
    {
        var c = (FfdModifier)MemberwiseClone();
        c.Offsets = (float[])Offsets.Clone();
        return c;
    }
}

/// <summary>
/// 3ds Max ProOptimizer / Blender Decimate: fewer polygons, same look. Keeps UVs, vertex colours, materials and
/// smoothing exact (it never invents new UVs), and by default never moves open borders, UV seams or material borders.
/// Made for GTA LODs: put it on the HD model, lower the %, export as the LOD.
/// </summary>
public sealed class ProOptimizerModifier : Modifier
{
    /// <summary>Vertex % to keep (3ds Max "Vertex %").</summary>
    public float Percent = 50;
    public bool KeepBorders = true, KeepUvSeams = true, KeepMaterialBorders = true;
    public ProOptimizerModifier() { Name = "ProOptimizer"; }
    public override string TypeName => "ProOptimizer";
    public override ModifierCategory Category => ModifierCategory.Generate;
    public override bool IsNoOp(PolyMesh m, bool render) => Percent >= 99.95f;

    [System.Text.Json.Serialization.JsonIgnore] PolyMesh cacheIn, cacheOut; int cacheHash;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        // the same input and settings give the same result: the reduction is not redone while other things change
        int h = HashCode.Combine(m.Verts.Count, m.Faces.Count, Percent, KeepBorders, KeepUvSeams, KeepMaterialBorders, Fingerprint(m));
        if (cacheOut != null && h == cacheHash) return cacheOut.Clone();
        var r = Decimate.Run(m, new Decimate.Options { Ratio = Percent / 100f, KeepBorders = KeepBorders, KeepUvSeams = KeepUvSeams, KeepMaterialBorders = KeepMaterialBorders });
        cacheHash = h; cacheOut = r.Clone();
        return r;
    }

    static int Fingerprint(PolyMesh m)
    {
        var hc = new HashCode();
        int step = Math.Max(1, m.Verts.Count / 64);
        for (int i = 0; i < m.Verts.Count; i += step) hc.Add(m.Verts[i]);
        return hc.ToHashCode();
    }

    public override Modifier Clone() { var c = (ProOptimizerModifier)MemberwiseClone(); c.cacheOut = null; return c; }
}
