using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Splats;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Gaussian splat scans (.ply / .spz / .splat), like Blender 5.3: imported as one object, drawn in every view, used as a
/// reference to model over. The object's mesh is only the 8 corners of its box (framing, brackets): it has no face, is never
/// exported to GTA and is picked from the Scene Explorer.
/// </summary>
public sealed partial class EditorApp
{
    SplatRenderer splats;
    readonly Dictionary<string, Task<SplatCloud>> splatLoads = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> splatErrors = new(StringComparer.OrdinalIgnoreCase);
    readonly List<SceneObject> splatDraws = new();
    sealed class PendingSplat { public string Path; public Vector3? At; public Task<SplatCloud> Task; }
    readonly List<PendingSplat> pendingSplats = new();

    public static bool IsSplat(SceneObject o) => o?.SplatFile != null;

    /// <summary>File > Import / drop / poly.import_file: a splat file becomes a scan object (loaded in the background).</summary>
    bool TryImportSplat(string path, Vector3? at, bool wait = false)
    {
        if (!SplatCloud.IsSplatFile(path)) return false;
        var t = Task.Run(() => SplatCloud.Load(path));
        if (wait) { try { t.Wait(); } catch { } }
        pendingSplats.Add(new PendingSplat { Path = path, At = at, Task = t });
        if (wait) PumpSplats();
        return true;
    }

    /// <summary>Adds the finished splat imports, and loads the captures of opened scenes.</summary>
    void PumpSplats()
    {
        for (int i = 0; i < pendingSplats.Count; i++)
        {
            var p = pendingSplats[i];
            if (!p.Task.IsCompleted) continue;
            pendingSplats.RemoveAt(i--);
            if (p.Task.IsFaulted) { ImportFailed(p.Path, p.Task.Exception?.InnerException ?? p.Task.Exception); continue; }
            AddSplatObject(p.Path, p.Task.Result, p.At);
        }
        // scenes opened from disk: the capture is read again from its file
        foreach (var o in Scene.Objects)
        {
            if (o.SplatFile == null || o.SplatCloud != null || splatErrors.ContainsKey(o.SplatFile)) continue;
            // the scene moved to another PC / folder: look for the capture next to the scene
            if (!File.Exists(o.SplatFile) && CurrentFile != null)
            {
                var near = Path.Combine(Path.GetDirectoryName(CurrentFile) ?? "", Path.GetFileName(o.SplatFile));
                if (File.Exists(near)) o.SplatFile = near;
            }
            if (!splatLoads.TryGetValue(o.SplatFile, out var t)) { var f = o.SplatFile; splatLoads[f] = Task.Run(() => SplatCloud.Load(f)); continue; }
            if (!t.IsCompleted) continue;
            if (t.IsFaulted) { splatErrors[o.SplatFile] = (t.Exception?.InnerException ?? t.Exception)?.Message; splatLoads.Remove(o.SplatFile); continue; }
            foreach (var x in Scene.Objects.Where(x => string.Equals(x.SplatFile, o.SplatFile, StringComparison.OrdinalIgnoreCase)))
            {
                x.SplatCloud = t.Result;
                if (x.Mesh == null || x.Mesh.Verts.Count == 0) { x.Mesh = SplatBoxMesh(t.Result); x.Touch(); }
                x.Splat ??= new SplatLook { CropMin = t.Result.Min, CropMax = t.Result.Max };
            }
            splatLoads.Remove(o.SplatFile);
        }
    }

    static PolyMesh SplatBoxMesh(SplatCloud c)
    {
        var m = new PolyMesh();
        for (int k = 0; k < 8; k++) m.AddVertex(new Vector3((k & 1) != 0 ? c.Max.X : c.Min.X, (k & 2) != 0 ? c.Max.Y : c.Min.Y, (k & 4) != 0 ? c.Max.Z : c.Min.Z));
        return m;
    }

    SceneObject AddSplatObject(string path, SplatCloud c, Vector3? at)
    {
        SceneObject o = null;
        Do("Import scan " + Path.GetFileName(path), () =>
        {
            var name = MakeUniqueName(Path.GetFileNameWithoutExtension(path));
            o = Scene.Add(name, SplatBoxMesh(c), Vector3.Zero);
            o.SplatFile = path; o.SplatCloud = c;
            o.Splat = new SplatLook { CropMin = c.Min, CropMax = c.Max };
            o.Gta.Role = GtaRole.Ignore;
            o.WireColour = new Vector3(0.95f, 0.7f, 0.3f);
            o.Rotation = c.SuggestedRotation;
            // its bottom centre lands on the 3D cursor
            var w = Matrix4x4.CreateFromQuaternion(o.Rotation);
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int k = 0; k < 8; k++) { var p = Vector3.Transform(o.Mesh.Verts[k], w); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var target = at ?? Cursor3D;
            o.Position = target - new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn.Z);
            Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged();
        });
        propTab = PropTab.Object; // the scan's panel (and its block-out button) shows right away
        SetStatus($"Scan {o.Name}: {c.Count:N0} splats ({c.Format}). Object tab: Copy the block-out prompt for AI (MCP).");
        return o;
    }

    string MakeUniqueName(string baseName)
    {
        var name = baseName; int n = 2;
        while (Scene.Objects.Any(x => x.Name == name)) name = $"{baseName}_{n++}";
        return name;
    }

    /// <summary>Called by RenderViewport after the transparent pass.</summary>
    void DrawSplats(Viewport vp)
    {
        if (splatDraws.Count == 0) return;
        splats ??= new SplatRenderer(gpu.Device);
        var cam = vp.Camera;
        int idx = Array.IndexOf(Viewports, vp);
        foreach (var o in splatDraws)
            splats.Draw(o, cam.View, cam.Projection(vp.Aspect), idx < 0 ? 0 : idx, vp.W, vp.H, cam.Ortho, cam.Near);
        splatDraws.Clear();
        // put back the states the rest of the viewport expects
        var ctx = gpu.Context;
        ctx.OutputMerger.SetBlendState(renderer.Alpha, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
    }

    /// <summary>The Object tab of a scan: simple, explicit settings.</summary>
    void SplatPanel(SceneObject o)
    {
        if (!Ui.Header("Scan (Gaussian splats)", Open)) return;
        var c = o.SplatCloud;
        var file = Path.GetFileName(o.SplatFile);
        if (c != null) Ui.TextDisabled($"{file}\n{c.Count:N0} splats · {c.Format}");
        else if (splatErrors.TryGetValue(o.SplatFile, out var err)) Ui.TextColored(new Vector4(1f, 0.5f, 0.35f, 1), $"{file}: {err}");
        else Ui.TextDisabled($"{file}: loading...");
        var look = o.Splat ??= new SplatLook();
        ImGui.Spacing();
        float size = look.Size * 100, op = look.Opacity * 100, br = look.Brightness * 100, haze = look.MinOpacity * 100;
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Blob size", ref size, 20, 300, "%.0f %%")) look.Size = size / 100;
        Theme.Tip("Smaller = sharper but with holes. Bigger = smoother. 100 % = as captured.");
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Opacity", ref op, 5, 100, "%.0f %%")) look.Opacity = op / 100;
        Theme.Tip("Lower it to see your model through the scan.");
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Brightness", ref br, 25, 300, "%.0f %%")) look.Brightness = br / 100;
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Clean haze", ref haze, 0, 60, "%.0f %%")) look.MinOpacity = haze / 100;
        Theme.Tip("Hides the faint blobs floating around a scan (fog, sky, stray points).");
        TutRect("splat_haze");
        ImGui.Spacing();
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Turn upright", new Vector2(w2, 0))) Do("Turn scan", () => o.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2) * o.Rotation));
        Theme.Tip("Turns the scan 90° (forward). Click again until the floor is down.");
        ImGui.SameLine();
        if (ImGui.Button("Upside down", new Vector2(w2, 0))) Do("Flip scan", () => o.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) * o.Rotation));
        if (ImGui.Button("Put on the ground", new Vector2(w2, 0)))
            Do("Scan on the ground", () => { var (mn, _) = o.WorldBounds(); o.Position -= new Vector3(0, 0, mn.Z); });
        ImGui.SameLine();
        if (ImGui.Button("Reset look", new Vector2(w2, 0))) { var keep = look; o.Splat = new SplatLook { CropMin = keep.CropMin, CropMax = keep.CropMax, Crop = keep.Crop }; }
        ImGui.Spacing();
        bool crop = look.Crop; if (ImGui.Checkbox("Keep only a box of the scan", ref crop)) look.Crop = crop;
        Theme.Tip("Cuts away everything outside the box (the street around a room, the sky...). The box is in the scan's own axes; the orange brackets show the scan.");
        if (look.Crop && c != null)
        {
            var span = c.Max - c.Min;
            var lo = look.CropMin; var hi = look.CropMax;
            ImGui.SetNextItemWidth(-110); ImGui.DragFloat3("From", ref lo, MathF.Max(span.Length(), 1) * 0.002f);
            ImGui.SetNextItemWidth(-110); ImGui.DragFloat3("To", ref hi, MathF.Max(span.Length(), 1) * 0.002f);
            look.CropMin = Vector3.Min(lo, hi); look.CropMax = Vector3.Max(lo, hi);
            if (ImGui.Button("Whole scan", new Vector2(w2, 0))) { look.CropMin = c.Min; look.CropMax = c.Max; }
        }
        if (c != null) SplatBlockoutPanel(o, w2);
        ImGui.Spacing();
        if (ImGui.Button("Reload the file", new Vector2(w2, 0))) { splatErrors.Remove(o.SplatFile); o.SplatCloud = null; }
        ImGui.SameLine();
        if (ImGui.Button("Show the file", new Vector2(w2, 0))) OpenUrl(Path.GetDirectoryName(o.SplatFile));
        Ui.TextDisabled("A scan is a reference to model over.\nIt is never exported to GTA.");
    }
}
