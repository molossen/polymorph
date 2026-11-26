using System.Numerics;

namespace MoloMax.Rendering;

/// <summary>
/// Sun shadows of the MAP look, as NodeRunner places them: three depth images seen from the sun, each covering a slice
/// of the camera's view (near: sharp, far: coarse), kept from frame to frame while they still cover their slice and
/// nothing casting a shadow changed. Ported with the author's agreement.
/// </summary>
public sealed class MapShadowSlices
{
    public const int Count = 3;
    static readonly float[] SplitShares = { 0.07f, 0.27f, 1f };
    const float Margin = 1.15f;

    public readonly Matrix4x4[] Cascades = new Matrix4x4[Count];
    public readonly float[] Splits = new float[Count];
    public readonly float[] TexelSizes = new float[Count];
    public readonly Vector3[] Centres = new Vector3[Count];
    public readonly float[] Radii = new float[Count];
    readonly bool[] stale = { true, true, true };
    readonly bool[] draw = new bool[Count];
    Vector3 sun;
    int frame;
    public Vector3 CameraForward { get; private set; }
    public int Size { get; }

    public MapShadowSlices(int size) { Size = size; }

    public bool MustDraw(int i) => draw[i];
    public void Invalidate() => Array.Fill(stale, true);
    /// <summary>A slice could not be drawn whole this frame (models still arriving): drawn again next frame.</summary>
    public void Unfinished(int i) => stale[i] = true;

    /// <summary>Places the slices for this camera (eye, forward, right, vertical field of view, aspect), the sun towards
    /// <paramref name="sunDirection"/>, shadows up to <paramref name="distance"/> metres.</summary>
    public void Update(Vector3 eye, Vector3 forward, Vector3 right, float fovY, float nearPlane, float aspect, Vector3 sunDirection, float distance, bool castersChanged)
    {
        forward = Vector3.Normalize(forward);
        var up = Vector3.Cross(right, forward);
        CameraForward = forward;
        float tanY = MathF.Tan(fovY * 0.5f), tanX = tanY * aspect;
        var s = Vector3.Normalize(sunDirection);
        if (s != sun || castersChanged) Invalidate();
        sun = s; frame++;
        var lightUp = MathF.Abs(s.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        var lightRotation = Matrix4x4.CreateLookAt(Vector3.Zero, -s, lightUp);
        Matrix4x4.Invert(lightRotation, out var inverseRotation);
        int farBudget = 1; // the far images only for a change of the scene: one per frame, each in turn
        float near = nearPlane;
        for (int n = 0; n < Count; n++)
        {
            int i = n == 0 ? 0 : 1 + (n - 1 + frame) % (Count - 1);
            float far = MathF.Max(distance * SplitShares[i], near + 1f);
            Splits[i] = far;
            float k = 1f + tanX * tanX + tanY * tanY;
            var centreOffset = forward * MathF.Min(far, k * (far + near) * 0.5f);
            float needed = MathF.Max(
                Vector3.Distance(centreOffset, forward * far + right * (far * tanX) + up * (far * tanY)),
                Vector3.Distance(centreOffset, forward * near + right * (near * tanX) + up * (near * tanY))) + 1f;
            var neededCentre = eye + centreOffset;
            bool covers = Radii[i] > 0f && Vector3.Distance(neededCentre, Centres[i]) + needed <= Radii[i] && Radii[i] <= needed * Margin * 1.3f;
            bool d = !covers || stale[i] && (i == 0 || farBudget-- > 0);
            draw[i] = d;
            if (!d) continue;
            stale[i] = false;
            float radius = MathF.Ceiling(needed * Margin);
            float texel = 2f * radius / Size;
            TexelSizes[i] = texel;
            var c = Vector3.Transform(neededCentre, lightRotation);
            c.X = MathF.Round(c.X / texel) * texel; c.Y = MathF.Round(c.Y / texel) * texel;
            c = Vector3.Transform(c, inverseRotation);
            Centres[i] = c; Radii[i] = radius;
            // things between the sun and the sphere still cast: flattened onto depth 0 (no depth clipping in that pass)
            var view = Matrix4x4.CreateLookAt(c + s * radius, c, lightUp);
            var proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 0f, 2f * radius);
            Cascades[i] = view * proj;
        }
    }
}
