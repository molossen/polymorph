using System.Numerics;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

public enum ViewKind { Perspective, Top, Bottom, Front, Back, Left, Right, User }

/// <summary>Orbit camera with 3ds Max navigation (Z up). Works in perspective and orthographic.</summary>
public sealed class Camera
{
    public Vector3 Target = new(0, 0, 1);
    public float Distance = 18f;
    public float Yaw = -2.2f;    // radians around Z
    public float Pitch = 0.45f;  // radians above the horizon
    public float Fov = 45f * MathF.PI / 180f;
    public bool Ortho;
    public float OrthoHeight = 14f;
    public ViewKind Kind = ViewKind.Perspective;
    public float Near = 0.05f, Far = 20000f;
    /// <summary>Reversed depth (MAP views, see DepthMode): 1 at the near plane, 0 at the far one.</summary>
    public bool ReverseZ;

    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return -new Vector3(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch));
        }
    }

    public Vector3 Eye => Target - Forward * Distance;

    public Vector3 Up => MathF.Abs(Pitch) > 1.5607f ? (Pitch > 0 ? new Vector3(-MathF.Cos(Yaw), -MathF.Sin(Yaw), 0) : new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0)) : Vector3.UnitZ;

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));
    public Vector3 CamUp => Vector3.Normalize(Vector3.Cross(Right, Forward));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Up);

    public Matrix4x4 Projection(float aspect)
    {
        var p = Ortho ? Matrix4x4.CreateOrthographic(OrthoHeight * aspect, OrthoHeight, -Far * 0.5f, Far * 0.5f)
            : Matrix4x4.CreatePerspectiveFieldOfView(Fov, MathF.Max(aspect, 1e-3f), Near, Far);
        return ReverseZ ? MoloMax.Rendering.DepthMode.Reverse(p) : p;
    }

    public Matrix4x4 ViewProj(float aspect)
    {
        // cached: Project() runs for thousands of points per frame and rebuilding the matrix each time cost more than the projection
        var k = (Target, Distance, Yaw, Pitch, Fov, Ortho, OrthoHeight, Near, Far, aspect, ReverseZ);
        if (vpKey.Equals(k) && vpValid) return vpCache;
        vpCache = View * Projection(aspect); vpKey = k; vpValid = true;
        return vpCache;
    }
    (Vector3, float, float, float, float, bool, float, float, float, float, bool) vpKey; Matrix4x4 vpCache; bool vpValid;

    /// <summary>World → viewport-local pixel with a matrix computed once (loops over many points: pass World * ViewProj and object-space points).</summary>
    public static bool Project(in Matrix4x4 m, Vector3 p, float w, float h, out Vector2 screen)
    {
        float cx = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
        float cy = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
        float cw = p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;
        if (cw <= 1e-5f) { screen = default; return false; }
        float iw = 1f / cw;
        screen = new Vector2((cx * iw * 0.5f + 0.5f) * w, (0.5f - cy * iw * 0.5f) * h);
        return true;
    }

    /// <summary>World units per screen pixel at the target distance.</summary>
    public float PixelSize(float viewportHeight, float atDistance = -1)
    {
        if (Ortho) return OrthoHeight / MathF.Max(viewportHeight, 1);
        float d = atDistance > 0 ? atDistance : Distance;
        return 2f * d * MathF.Tan(Fov / 2) / MathF.Max(viewportHeight, 1);
    }

    public void SetView(ViewKind k)
    {
        Kind = k;
        Ortho = k != ViewKind.Perspective && k != ViewKind.User;
        if (Ortho) OrthoHeight = MathF.Max(OrthoHeight, Distance * 0.8f);
        switch (k)
        {
            case ViewKind.Top: Yaw = -MathF.PI / 2; Pitch = MathF.PI / 2 - 0.0001f; break;
            case ViewKind.Bottom: Yaw = MathF.PI / 2; Pitch = -MathF.PI / 2 + 0.0001f; break;
            case ViewKind.Front: Yaw = -MathF.PI / 2; Pitch = 0; break;
            case ViewKind.Back: Yaw = MathF.PI / 2; Pitch = 0; break;
            case ViewKind.Left: Yaw = MathF.PI; Pitch = 0; break;
            case ViewKind.Right: Yaw = 0; Pitch = 0; break;
            case ViewKind.Perspective: Yaw = -2.2f; Pitch = 0.45f; break;
        }
    }

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.006f;
        Pitch = Math.Clamp(Pitch + dy * 0.006f, -MathF.PI / 2 + 0.0001f, MathF.PI / 2 - 0.0001f);
        if (Ortho) { Ortho = false; Distance = MathF.Max(Distance, OrthoHeight); }
        Kind = ViewKind.Perspective;
    }

    public void Pan(float dx, float dy, float viewportHeight)
    {
        float s = PixelSize(viewportHeight);
        Target += (-Right * dx + CamUp * dy) * s;
    }

    public void Zoom(float steps)
    {
        float f = MathF.Pow(0.88f, steps);
        if (Ortho) OrthoHeight = Math.Clamp(OrthoHeight * f, 0.05f, 50000f);
        else Distance = Math.Clamp(Distance * f, 0.05f, 50000f);
    }

    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) * 0.5f;
        float r = MathF.Max((max - min).Length() * 0.5f, 0.25f);
        Distance = r / MathF.Sin(Fov / 2) * 1.05f;
        OrthoHeight = r * 2.3f;
    }

    /// <summary>Ray through a pixel (viewport-local coordinates).</summary>
    public Ray ScreenRay(float x, float y, float w, float h)
    {
        var vp = ViewProj(w / h);
        Matrix4x4.Invert(vp, out var inv);
        float nx = x / w * 2 - 1, ny = 1 - y / h * 2;
        float zNear = ReverseZ ? 1 : 0; // reversed depth: 1 at the near plane
        var a = Vector4.Transform(new Vector4(nx, ny, zNear, 1), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 1 - zNear, 1), inv);
        var p0 = new Vector3(a.X, a.Y, a.Z) / a.W; var p1 = new Vector3(b.X, b.Y, b.Z) / b.W;
        return new Ray(p0, Vector3.Normalize(p1 - p0));
    }

    /// <summary>World → viewport-local pixel. Returns false when behind the camera.</summary>
    public bool Project(Vector3 p, float w, float h, out Vector2 screen, out float depth)
    {
        var c = Vector4.Transform(new Vector4(p, 1), ViewProj(w / h));
        screen = default; depth = c.W;
        if (c.W <= 1e-5f) return false;
        screen = new Vector2((c.X / c.W * 0.5f + 0.5f) * w, (0.5f - c.Y / c.W * 0.5f) * h);
        return true;
    }

    public string Label => Kind switch
    {
        ViewKind.Perspective => "Perspective",
        ViewKind.User => "Orthographic",
        _ => Kind.ToString(),
    };

    public Camera Clone() { var c = (Camera)MemberwiseClone(); c.vpValid = false; return c; }
}
