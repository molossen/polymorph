using System.Numerics;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// Map mode drawing: frustum and distance culling, then one instanced draw per archetype (thousands of entities,
/// a few hundred models). Selected / frozen objects and objects without an archetype are drawn one by one as usual.
/// </summary>
public sealed partial class EditorApp
{
    sealed class InstGroup { public SceneObject Template; public Matrix4x4[] Worlds = new Matrix4x4[64]; public SceneObject[] Objs = new SceneObject[64]; public int[] Ents = new int[64]; public int Count; }
    readonly Dictionary<string, InstGroup> instGroups = new();
    readonly List<SceneObject> mapSingles = new();
    public int MapDrawCalls, MapVisible;

    static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41); var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43); var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        var p = new[] { c3 + c0, c3 - c0, c3 + c1, c3 - c1, c2, c3 - c2 };
        for (int i = 0; i < 6; i++) { float l = new Vector3(p[i].X, p[i].Y, p[i].Z).Length(); if (l > 0) p[i] /= l; }
        return p;
    }

    static bool BoxVisible(Vector4[] planes, Vector3 mn, Vector3 mx)
    {
        foreach (var p in planes)
        {
            var v = new Vector3(p.X >= 0 ? mx.X : mn.X, p.Y >= 0 ? mx.Y : mn.Y, p.Z >= 0 ? mx.Z : mn.Z);
            if (p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W < 0) return false;
        }
        return true;
    }

    void MapGatherDraws(Viewport vp, Matrix4x4 viewProj)
    {
        foreach (var g in instGroups.Values) { g.Count = 0; g.Template = null; }
        mapSingles.Clear();
        var planes = FrustumPlanes(viewProj);
        var eye = vp.Camera.Eye;
        float pix = vp.Camera.Ortho ? 0 : 1.6f / MathF.Max(vp.H, 1); // ~ angular size of one pixel at fov 60
        MapVisible = 0;
        renderer.HideWaterShaders = Settings.MapWater; // the game's water surface draws the sea, rivers and pools: their mesh copies (untextured) stay hidden
        SyncInteriorHidden();
        UpdateHiddenClip(eye);
        var live = MapLive();
        float vr2 = Settings.MapViewRadiusOn ? MathF.Max(20, Settings.MapViewRadius) * MathF.Max(20, Settings.MapViewRadius) : 0;
        GatherWorld(planes, eye, pix, vr2);
        for (int i = 0, n = live.Count + (LodTreeOn ? 0 : backdrop.Count); i < n; i++)
        {
            var o = i < live.Count ? live[i] : backdrop[i - live.Count];
            if (o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0) continue;
            if (o.Id < 0 && (FarSteppedAside(o, eye) || !FarLodVisible(o, eye))) continue; // far model: area loaded for editing, or another LOD level shows here
            if (i < live.Count && WorldHidesEditable(o)) continue; // level-of-detail world: another level of it shows here
            var (mn, mx) = o.FastWorldBounds();
            // view radius: the game's far models (LOD / SLOD) go too, not only the editable entities
            if (vr2 > 0 && i >= live.Count && Vector3.DistanceSquared(Vector3.Clamp(eye, mn, mx), eye) > vr2) continue;
            if (!BoxVisible(planes, mn, mx)) continue;
            // smaller than ~1.5 px on screen: skip (far props)
            if (pix > 0)
            {
                float r = (mx - mn).Length() * 0.5f, d = Vector3.Distance((mn + mx) * 0.5f, eye);
                if (d > r && r / d < pix * 1.5f) continue;
            }
            MapVisible++;
            if (o.InstanceKey == null || (o.Frozen && o.Id >= 0) || Selected.Contains(o.Id)) { mapSingles.Add(o); continue; }
            if (!instGroups.TryGetValue(o.InstanceKey, out var grp)) instGroups[o.InstanceKey] = grp = new InstGroup();
            if (grp.Template == null) grp.Template = o;
            else if (!ReferenceEquals(grp.Template.Mesh, o.Mesh)) { mapSingles.Add(o); continue; } // not synced yet this frame
            if (grp.Count == grp.Worlds.Length) { Array.Resize(ref grp.Worlds, grp.Worlds.Length * 2); Array.Resize(ref grp.Objs, grp.Worlds.Length); Array.Resize(ref grp.Ents, grp.Worlds.Length); }
            grp.Objs[grp.Count] = o; grp.Ents[grp.Count] = -1;
            grp.Worlds[grp.Count++] = o.World;
        }
    }

    void MapDraw(bool alphaPass)
    {
        if (!alphaPass) MapDrawCalls = 0;
        // the far models are cut away over the models the user hid (else the far copy shows where the model was)
        foreach (var g in instGroups.Values)
        {
            if (g.Count == 0) continue;
            renderer.SetClip(g.Template.Id < 0 ? hiddenClip : null);
            if (g.Count == 1 && g.Ents[0] < 0) { renderer.DrawObject(Scene, g.Template, alphaPass, Vector4.Zero, BackfaceCull); MapDrawCalls++; continue; }
            renderer.DrawInstanced(Scene, g.Template, g.Worlds, g.Count, alphaPass, BackfaceCull);
            if (!alphaPass) MapDrawCalls++;
        }
        foreach (var o in mapSingles)
        {
            renderer.SetClip(o.Id < 0 ? hiddenClip : null);
            var hl = o.Frozen ? new Vector4(0.55f, 0.55f, 0.6f, 0.6f) : Vector4.Zero;
            renderer.DrawObject(Scene, o, alphaPass, alphaPass ? Vector4.Zero : hl, BackfaceCull);
            MapDrawCalls++;
        }
        renderer.SetClip(null);
    }

    /// <summary>Footprints of the map models the user hid near the camera (grown a little), refreshed with the draw list.</summary>
    readonly List<(Vector3 Min, Vector3 Max)> hiddenClip = new();
    void UpdateHiddenClip(Vector3 eye)
    {
        hiddenClip.Clear();
        foreach (var o in MapLive())
        {
            if (!o.Hidden || o.Id < 0 || !entOf.TryGetValue(o.Id, out var de) || !de.Ent.IsHd || !MapLevelOn[de.Ent.LodLevel]) continue;
            var (mn, mx) = o.FastWorldBounds();
            hiddenClip.Add((mn - new Vector3(0.05f, 0.05f, 1.5f), mx + new Vector3(0.05f, 0.05f, 0.5f)));
            if (hiddenClip.Count >= MoloMax.Rendering.SceneRenderer.ClipMax) break;
        }
        // nearest first when there are more than the shader takes
        hiddenClip.Sort((a, b) => Vector3.DistanceSquared((a.Min + a.Max) * 0.5f, eye).CompareTo(Vector3.DistanceSquared((b.Min + b.Max) * 0.5f, eye)));
    }
}
