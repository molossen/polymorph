using System.Numerics;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode picking on the graphics card: the visible map is drawn once more with each object's number instead of its
/// colour, only under the mouse pixel, and the number and depth there are read back. Exactly what is seen (through the
/// holes of foliage, never behind a wall), and no walk over the meshes of thousands of streamed objects.
/// </summary>
public sealed partial class EditorApp
{
    readonly List<SceneObject> pickIds = new();
    readonly List<int> pickEnts = new();
    /// <summary>World entity under the last GPU pick (-1: a scene object or nothing).</summary>
    int lastPickEnt = -1;
    Matrix4x4[] pickWorlds = new Matrix4x4[64];
    /// <summary>Last GPU pick time (ms), for the self tests.</summary>
    public double LastGpuPickMs;
    public string LastGpuPickInfo = "";

    /// <summary>Object and point under a screen position (the far models included, Id &lt; 0). Face is the face hit when the
    /// object's mesh is on the CPU (exact point and normal), else -1 with the point from the depth.</summary>
    PickHit? GpuPickMap(Viewport vp, Vector2 screen, Func<SceneObject, bool> skip)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var local = vp.Local(screen);
        if (local.X < 0 || local.Y < 0 || local.X >= vp.W || local.Y >= vp.H) return null;
        var cam = vp.Camera;
        var viewProj = cam.ViewProj(vp.Aspect);
        // the draw lists of the last frame are reused when the view has not moved since (a hover pick each frame while placing)
        if (!(ReferenceEquals(gatherVp, vp) && gatherViewProj == viewProj && ImGuiNET.ImGui.GetFrameCount() - gatherFrame <= 1)) MapGatherDraws(vp, viewProj);
        pickIds.Clear(); pickEnts.Clear(); lastPickEnt = -1;
        int draws0 = renderer.StatDraws;
        renderer.BeginPick((int)vp.W, (int)vp.H, (int)local.X, (int)local.Y);
        renderer.BeginScene(viewProj, cam.Eye, Lights, ShadingMode.Realistic, 1, 0);
        foreach (var g in instGroups.Values)
        {
            if (g.Count == 0) continue;
            int n = 0;
            if (pickWorlds.Length < g.Count) pickWorlds = new Matrix4x4[g.Count * 2];
            int first = pickIds.Count;
            for (int i = 0; i < g.Count; i++)
            {
                if (skip != null && skip(g.Objs[i])) continue;
                pickWorlds[n++] = g.Worlds[i]; pickIds.Add(g.Objs[i]); pickEnts.Add(g.Ents[i]);
            }
            if (n == 0) continue;
            renderer.SetClip(g.Template.Id < 0 ? hiddenClip : null);
            renderer.SetPickBase((uint)first);
            if (n == 1 && pickEnts[first] < 0) renderer.DrawObject(Scene, pickIds[first], false, Vector4.Zero, BackfaceCull); // a world object has no place of its own: always instanced
            else renderer.DrawInstanced(Scene, g.Template, pickWorlds, n, false, BackfaceCull);
        }
        foreach (var o in mapSingles)
        {
            if (skip != null && skip(o)) continue;
            renderer.SetClip(o.Id < 0 ? hiddenClip : null);
            renderer.SetPickBase((uint)pickIds.Count); pickIds.Add(o); pickEnts.Add(-1);
            renderer.DrawObject(Scene, o, false, Vector4.Zero, BackfaceCull);
        }
        renderer.SetClip(null);
        var (id, depth) = renderer.EndPick();
        gpu.BindBackBuffer();
        LastGpuPickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastGpuPickInfo = $"id {id} depth {depth:0.0000}, {pickIds.Count} objects, {renderer.StatDraws - draws0} draws, groups {instGroups.Values.Count(g => g.Count > 0)}, singles {mapSingles.Count}, pixel {local.X:0},{local.Y:0} of {vp.W:0}x{vp.H:0}";
        if (id == 0 || id > pickIds.Count || depth >= 1f) return null;
        var obj = pickIds[(int)id - 1]; lastPickEnt = pickEnts[(int)id - 1];
        // the point from the depth under the pixel
        Matrix4x4.Invert(viewProj, out var inv);
        float nx = (local.X + 0.5f) / vp.W * 2 - 1, ny = 1 - (local.Y + 0.5f) / vp.H * 2;
        var h = Vector4.Transform(new Vector4(nx, ny, depth, 1), inv);
        var p = new Vector3(h.X, h.Y, h.Z) / h.W;
        // exact face on that one object when its mesh is here (normals for placing on walls)
        var ray = vp.Camera.ScreenRay(local.X, local.Y, vp.W, vp.H);
        if (obj.Id >= 0 && lastPickEnt < 0 && obj.Mesh != null && obj.Mesh.Faces.Count > 0 && Picking.PickFace(obj, ray, evaluated: obj.Modifiers is { Count: > 0 }) is { } f && Vector3.Distance(f.Point, p) < MathF.Max(0.5f, Vector3.Distance(p, cam.Eye) * 0.01f))
            return f;
        return new PickHit(obj, -1, Vector3.Distance(cam.Eye, p), p);
    }
}
