using System.Runtime.CompilerServices;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>The paint tools (left strip, Tools tab).</summary>
public enum PaintTool { Brush, Eraser, Decal, Fill, Clone, Smudge, VertexColour }

/// <summary>
/// Molo Paint, the session: texture sets (one per material), live upload, GTA textures, viewport strokes
/// (symmetry, lazy mouse, Shift lines, jitter, clone source, fills, vertex colours) and undo.
/// The panel lives in EditorApp.PaintPanel.cs, the shelf and tool strip in EditorApp.PaintShelf.cs.
/// </summary>
public sealed partial class EditorApp
{
    bool paintMode;
    readonly PaintBrush brush = new();
    PaintTool paintTool = PaintTool.Brush;
    FillScope fillScope = FillScope.Element;
    Vector4 vertexColour = new(0, 1, 0, 1);
    int paintRes = 1024;
    bool painting;
    Vector3 lastStamp, lazyPos; Vector2 lastStampScreen; bool lastStampValid;
    readonly List<(SceneObject O, TextureSet Set, int Layer, PaintLayer Snap)> paintUndo = new();
    /// <summary>When each paint stroke was made, on the scene undo's clock (Ctrl+Z undoes whichever came last).</summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<PaintLayer, StrongBox<long>> paintUndoStamp = new();
    (Vector3 P, Vector3 N, float R, bool Hit) paintHover;
    readonly Dictionary<int, int> paintMeshVersion = new();
    int aoRays = 48;
    readonly Random paintRnd = new();
    /// <summary>Clicks on the model pick the parts of the selected layer's mask instead of painting.</summary>
    bool partPick;
    Vector3? cloneSource;
    ShadingMode? shadingBeforeVertex;
    static readonly int[] PaintSizes = { 512, 1024, 2048, 4096 };

    /// <summary>The texture set being painted (the active material of the active object).</summary>
    TextureSet CurSet => Current?.Paint?.Cur;

    string PaintDir => CurrentFile != null
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CurrentFile)), Path.GetFileNameWithoutExtension(CurrentFile) + "_paint")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "paint");

    /// <summary>Texture base name of a set: object name, plus the material when the object has several.</summary>
    static string PaintBase(SceneObject o, TextureSet ts) => TextureBank.SafeName(o.Name) + (string.IsNullOrEmpty(ts.Name) ? "" : "_" + TextureBank.SafeName(ts.Name));

    /// <summary>GTA naming, like the game's own textures: name.dds, name_n.dds, name_s.dds.</summary>
    string PaintTexPath(SceneObject o, TextureSet ts, string suffix) => Path.Combine(PaintDir, suffix == "d" ? $"{PaintBase(o, ts)}.dds" : $"{PaintBase(o, ts)}_{suffix}.dds");

    string PaintMaterialName(SceneObject o, TextureSet ts) => PaintBase(o, ts) + "_paint";

    // ------------------------------------------------------------------------------ setup

    /// <summary>One texture set per material of the object (each gets its own "_paint" material and textures).</summary>
    /// <summary>The toolbar Paint button: paint mode on the selected model (UVs made when it has none), or off.</summary>
    public void TogglePaintMode()
    {
        if (paintMode) { paintMode = false; SetStatus("Paint mode off"); return; }
        var o = Current;
        if (o == null || o.Mesh == null || o.Mesh.Faces.Count == 0) { SetStatus("Select one model to paint."); return; }
        if (SculptMode) ToggleSculptMode();
        if (Level != SubObject.Object) Level = SubObject.Object;
        if (o.Paint == null)
        {
            if (!TexelMap.HasUsableUvs(o.Mesh)) DoMesh("Smart UV Project", (oo, m, s) => UvUnwrap.SmartProject(m));
            StartPaint(o);
        }
        else paintMode = true;
        if (paintMode) { propTab = PropTab.Paint; ShowPanel = true; SetStatus("Paint mode: LMB paints, [ ] brush size, B brush, E eraser, F fill, I eyedropper. Esc or Paint to leave."); }
    }

    void StartPaint(SceneObject o)
    {
        if (!TexelMap.HasUsableUvs(o.Mesh)) { SetStatus("This object has no UVs to paint on: use Smart UV Project first."); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mats = o.Mesh.Faces.Select(f => f.MatId).Distinct().OrderBy(x => x).ToList();
        var doc = new PaintDoc();
        Do("Start painting", () =>
        {
            foreach (var mid in mats)
            {
                var src = Scene.Materials.FirstOrDefault(m => m.Id == mid);
                var ts = new TextureSet { Size = paintRes, Name = mats.Count > 1 ? TextureBank.SafeName(src?.Name ?? "mat" + mid) : "" };
                var pm = EnsurePaintMaterial(o, ts);
                foreach (var f in o.Mesh.Faces) if (f.MatId == mid) f.MatId = pm.Id;
                ts.MatId = mats.Count > 1 ? pm.Id : -1;
                doc.Sets.Add(ts);
            }
            o.Touch();
        });
        for (int i = 0; i < doc.Sets.Count; i++)
        {
            var info = doc.Sets[i];
            var ts = TextureSet.Create(o.Mesh, paintRes, aoRays, info.MatId, info.Name);
            doc.Sets[i] = ts;
        }
        o.Paint = doc;
        paintMeshVersion[o.Id] = o.Version;
        foreach (var ts in doc.Sets) UploadPaint(o, ts, true);
        paintMode = true;
        SetStatus($"Molo Paint ready: {doc.Sets.Count} texture set(s), {doc.Sets.Sum(s => s.Map.Count):N0} texels at {paintRes}, mesh maps in {sw.ElapsedMilliseconds} ms.");
    }

    /// <summary>Material "&lt;object&gt;[_set]_paint" (normal_spec) whose textures are the painted DDS files, shown live while painting.</summary>
    GtaMaterial EnsurePaintMaterial(SceneObject o, TextureSet ts)
    {
        var name = PaintMaterialName(o, ts);
        var m = Scene.Materials.FirstOrDefault(x => x.Name == name) ?? Scene.AddMaterial(name, new Vector3(0.7f));
        m.Shader = ts.HasAlpha ? ts.AlphaShader : "normal_spec";
        m.DiffuseTexture = PaintTexPath(o, ts, "d"); m.BumpTexture = PaintTexPath(o, ts, "n"); m.SpecTexture = PaintTexPath(o, ts, "s");
        m.SpecMapIntMask = new Vector3(1, 0, 0); m.Bumpiness = 1f; m.SpecularIntensity = 1f; m.SpecularFalloff = 100f;
        return m;
    }

    void UploadPaint(SceneObject o, TextureSet ts, bool full = false)
    {
        if (ts == null) return;
        if (!full && ts.Dirty == null) return;
        var rect = full ? null : ts.Dirty;
        renderer.Textures.SetDynamic(PaintTexPath(o, ts, "d"), ts.ColorImage, rect);
        renderer.Textures.SetDynamic(PaintTexPath(o, ts, "n"), ts.NormalImage, rect);
        renderer.Textures.SetDynamic(PaintTexPath(o, ts, "s"), ts.SpecImage, rect);
        renderer.Textures.SetDynamic(PaintTexPath(o, ts, "e"), ts.EmissiveImage, rect);
        ts.Dirty = null;
    }

    /// <summary>Called every frame: pushes the changed part of the painted textures to the GPU, finishes background bakes.</summary>
    void PaintFrame()
    {
        renderer.Textures.BeginFrameThumbs();
        foreach (var o in Scene.Objects)
            if (o.Paint != null) foreach (var ts in o.Paint.Sets) if (ts.Dirty != null) UploadPaint(o, ts);
        PumpBake();
        // vertex colour tool: the viewport shows the vertex colours while it is active
        bool vtool = paintMode && paintTool == PaintTool.VertexColour && !(terrainPaintLayer >= 0 && Current is { } tc && TerrainMaterialOf(tc) != null); // terrain: the real textures stay on screen
        var vp = Viewports[ActiveViewport];
        if (vtool && shadingBeforeVertex == null) { shadingBeforeVertex = vp.Shading; vp.Shading = ShadingMode.VertexColour; }
        else if (!vtool && shadingBeforeVertex != null) { vp.Shading = shadingBeforeVertex.Value; shadingBeforeVertex = null; }
    }

    /// <summary>Writes the GTA textures (DDS with mips at the export size, DXT1 / DXT5 when there is alpha), extra PNG / TGA / Sollumz copies, and the layer file.</summary>
    bool BakePaint(SceneObject o, bool quiet = false)
    {
        var doc = o.Paint; if (doc == null) return false;
        try
        {
            Directory.CreateDirectory(PaintDir);
            var notes = new List<string>();
            foreach (var ts in doc.Sets)
            {
                var keep = ts.ExportFormat;
                ts.ExportFormat = PaintExportFormat.Dds; // the scene and the GTA export always use the DDS files
                var w = PaintExport.Write(ts, PaintDir, PaintBase(o, ts));
                ts.ExportFormat = keep;
                if (keep != PaintExportFormat.Dds) w.Files.AddRange(PaintExport.Write(ts, PaintDir, PaintBase(o, ts), PngBytes).Files.Where(f => !w.Files.Contains(f)));
                var m = EnsurePaintMaterial(o, ts);
                notes.Add($"{PaintBase(o, ts)}: {w.Files.Count} files" + (w.Alpha ? $", alpha ({m.Shader})" : "") + (w.Emissive ? ", emissive" : ""));
            }
            var file = Path.Combine(PaintDir, TextureBank.SafeName(o.Name) + ".mpaint");
            doc.Save(file);
            o.PaintFile = file;
            if (!quiet) SetStatus("Saved " + string.Join("; ", notes) + " in " + PaintDir);
            return true;
        }
        catch (Exception e) { SetStatus("Saving the textures failed: " + e.Message); return false; }
    }

    static byte[] PngBytes(RgbaImage img)
    {
        using var bmp = new System.Drawing.Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, img.Width, img.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var bgra = new byte[img.Pixels.Length];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = img.Pixels[i + 2]; bgra[i + 1] = img.Pixels[i + 1]; bgra[i + 2] = img.Pixels[i]; bgra[i + 3] = img.Pixels[i + 3]; }
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        bmp.UnlockBits(bd);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Before saving or exporting: every painted object gets its DDS and layer file written.</summary>
    void BakeAllPaint()
    {
        foreach (var o in Scene.Objects.Where(x => x.Paint != null)) BakePaint(o, true);
    }

    /// <summary>After opening a scene: painted objects get their layers back.</summary>
    void LoadPaintSets()
    {
        foreach (var o in Scene.Objects)
        {
            if (string.IsNullOrEmpty(o.PaintFile) || !File.Exists(o.PaintFile)) continue;
            try
            {
                var oo = o;
                string Base(string set) => TextureBank.SafeName(oo.Name) + "_" + set;
                o.Paint = PaintDoc.Load(o.PaintFile, o.Mesh, aoRays, set => Scene.Materials.FirstOrDefault(m => m.Name == Base(set) + "_paint")?.Id ?? -1,
                    set => Scene.Materials.FirstOrDefault(m => m.Name == Base(set) + "_emissive")?.Id ?? -1);
                paintMeshVersion[o.Id] = o.Version;
                foreach (var ts in o.Paint.Sets) UploadPaint(o, ts, true);
            }
            catch (Exception e) { SetStatus($"{o.Name}: paint layers not loaded ({e.Message})"); }
        }
    }

    /// <summary>GTA emissive: faces mostly painted emissive get an "emissive" material (same textures) and white vertex colours.</summary>
    void ApplyEmissiveFaces(SceneObject o, TextureSet ts)
    {
        if (!ts.HasEmissive) { SetStatus("Nothing is painted emissive in this texture set."); return; }
        var per = new Dictionary<int, (float Sum, int N)>();
        for (int t = 0; t < ts.Map.Count; t++) { int f = ts.Map.FaceOf[t]; var (s0, n0) = per.TryGetValue(f, out var v) ? v : (0, 0); per[f] = (s0 + ts.CEmis[t], n0 + 1); }
        var faces = per.Where(kv => kv.Value.Sum / kv.Value.N > 0.5f).Select(kv => kv.Key).ToList();
        if (faces.Count == 0) { SetStatus("No face is mostly emissive (paint more of the face, or use a separate mesh part)."); return; }
        BakePaint(o, true);
        Do("Emissive faces", () =>
        {
            var name = PaintBase(o, ts) + "_emissive";
            var m = Scene.Materials.FirstOrDefault(x => x.Name == name) ?? Scene.AddMaterial(name, new Vector3(1));
            m.Shader = "emissive"; m.DiffuseTexture = PaintTexPath(o, ts, "d");
            foreach (var f in faces) o.Mesh.Faces[f].MatId = m.Id;
            if (ts.MatId >= 0) ts.AltMatId = m.Id; // the faces stay in this texture set
            VertexPaint.Fill(o.Mesh, faces, VertexPaint.Presets[2].Colour); // white on emissive (GTA rule)
            o.Touch();
        });
        paintMeshVersion[o.Id] = o.Version; // same faces, same UVs: the texel maps stay valid
        SetStatus($"{faces.Count} face(s) now use the GTA emissive shader, with white vertex colours.");
    }

    // ------------------------------------------------------------------------------ viewport input

    /// <summary>Left button in paint mode paints instead of selecting. Returns true when it used the mouse.</summary>
    bool PaintInput(Viewport hv, bool uiMouse)
    {
        var o = Current;
        paintHover.Hit = false;
        // terrain layers paint vertex colour 1 only: no texture set needed (its paint material would hide the terrain shader)
        bool terrainOnly = o != null && o.Paint == null && paintTool == PaintTool.VertexColour && terrainPaintLayer >= 0;
        if (!paintMode || o == null || (o.Paint == null && !terrainOnly) || Level != SubObject.Object) { painting = false; return false; }
        if (hv == null || uiMouse) { if (!Input.Down(System.Windows.Forms.MouseButtons.Left)) painting = false; return painting; }
        var hit = HitLocal(o, hv, Input.Mouse, out var pl, out var nl, out int face);
        float rLocal = brush.Radius / ObjScale(o);
        if (hit) paintHover = (Vector3.Transform(pl, o.World), Vector3.Normalize(Vector3.TransformNormal(nl, o.World)), brush.Radius, true);
        bool alt = Input.Alt;
        var ts = o.Paint?.Cur;
        bool pressed = Input.Pressed(System.Windows.Forms.MouseButtons.Left) && !alt;
        // in paint mode a click next to the model does nothing (it would deselect the object being painted)
        if (pressed && !hit) return true;
        if (pressed)
        {
            // Ctrl+click with the clone tool: the source
            // eyedropper (I): the brush takes the colour, roughness, metal and relief under the click
            if (paintPick) { PaintEyedrop(ts, pl, rLocal); return true; }
            if (paintTool == PaintTool.Clone && Input.Ctrl) { cloneSource = pl; brush.CloneSourceSet = false; SetStatus("Clone source set. Paint where the copy goes."); return true; }
            if (partPick && ts?.Current is { } pl0)
            {
                // picking the parts of a "picked parts" mask: a click adds or removes the part under the mouse
                if (ts.TogglePart(pl0, face)) { ts.Refresh(); Dirty = true; SetStatus($"{pl0.MaskParts.Count} part(s) picked for '{pl0.Name}'. Click again to take one out."); }
                else SetStatus("That face belongs to another texture set.");
                return true;
            }
            if (paintTool == PaintTool.Fill) { PaintFillClick(o, ts, face); return true; }
            if (paintTool == PaintTool.VertexColour)
            {
                if (o.Id != terrainLayerObj) { terrainLayerObj = o.Id; if (TerrainMaterialOf(o) == null) terrainPaintLayer = -1; }
                Do("Vertex colour stroke", () => { });
                VertexStamp(o, pl, nl, rLocal);
                lastStamp = pl; painting = true; return true;
            }
            if (ts == null || !PrepareStroke(o, ts)) return true;
            if (paintTool == PaintTool.Clone)
            {
                if (cloneSource == null) { SetStatus("Ctrl+click on the model first to pick what to copy."); return true; }
                brush.CloneOffset = cloneSource.Value - pl; brush.CloneSourceSet = true;
            }
            // Shift+click: straight line from the last print
            if (Input.Shift && lastStampValid) StrokeLine(o, hv, lastStampScreen, Input.Mouse, rLocal);
            else StampAll(o, ts, pl, nl, rLocal, pl);
            lastStamp = pl; lazyPos = pl; lastStampScreen = Input.Mouse; lastStampValid = true;
            painting = true; Dirty = true;
            return true;
        }
        if (painting)
        {
            if (!Input.Down(System.Windows.Forms.MouseButtons.Left)) { painting = false; return true; }
            if (!hit) return true;
            if (paintTool == PaintTool.VertexColour) { if (Vector3.Distance(lastStamp, pl) > rLocal * 0.3f) { VertexStamp(o, pl, nl, rLocal); lastStamp = pl; } return true; }
            if (ts == null) return true;
            if (brush.Stamp != null && brush.StampOnce) return true;
            // lazy mouse: the brush trails behind the cursor
            var target = pl;
            if (brush.Lazy > 0)
            {
                float lazyLocal = brush.Lazy / ObjScale(o), d = Vector3.Distance(lazyPos, pl);
                if (d <= lazyLocal) return true;
                target = lazyPos + (pl - lazyPos) * ((d - lazyLocal) / d);
                lazyPos = target;
            }
            float step = MathF.Max(rLocal * MathF.Max(brush.Spacing, 0.02f), 1e-5f);
            float dist = Vector3.Distance(lastStamp, target);
            if (dist > rLocal * 3 && brush.Lazy <= 0) { StampAll(o, ts, target, nl, rLocal, lastStamp); lastStamp = target; } // jumped to another surface
            else
            {
                int steps = (int)(dist / step);
                for (int i = 1; i <= steps; i++)
                {
                    var p = Vector3.Lerp(lastStamp, target, i / (float)steps);
                    StampAll(o, ts, p, nl, rLocal, Vector3.Lerp(lastStamp, target, (i - 1) / (float)steps));
                }
                if (steps > 0) lastStamp = target;
            }
            lastStampScreen = Input.Mouse;
            return true;
        }
        return false;
    }

    static float ObjScale(SceneObject o) => MathF.Max(1e-4f, (MathF.Abs(o.Scale.X) + MathF.Abs(o.Scale.Y) + MathF.Abs(o.Scale.Z)) / 3f);

    bool HitLocal(SceneObject o, Viewport vp, Vector2 mouse, out Vector3 pl, out Vector3 nl, out int face)
    {
        pl = nl = default; face = -1;
        var h = Picking.PickFace(o, RayAt(vp, mouse));
        if (!h.HasValue) return false;
        Matrix4x4.Invert(o.World, out var inv);
        pl = Vector3.Transform(h.Value.Point, inv);
        nl = Vector3.Normalize(o.Mesh.FaceNormal(h.Value.Face));
        face = h.Value.Face;
        return true;
    }

    /// <summary>Snapshot for undo, and the layer the stroke goes to (a new paint layer above a material layer).</summary>
    bool PrepareStroke(SceneObject o, TextureSet ts)
    {
        var layer = ts.Current;
        if (layer == null) { SetStatus("Add a layer first."); return false; }
        bool maskStroke = brush.MaskMode || layer.IsFolder || (layer.Kind == LayerKind.Fill && layer.Mask == MaskGen.Painted);
        if (layer.Kind == LayerKind.Fill && !maskStroke)
        {
            // painting on a material layer: strokes go to a new paint layer on top, nothing to set up
            AddLayer(ts, new PaintLayer { Name = "Paint " + ts.Layers.Count, Kind = LayerKind.Paint, Folder = layer.Folder });
        }
        paintUndo.Add((o, ts, ts.SelectedLayer, ts.Snapshot(ts.SelectedLayer)));
        paintUndoStamp.AddOrUpdate(paintUndo[^1].Snap, new StrongBox<long>(Undo.Tick()));
        if (paintUndo.Count > 30) paintUndo.RemoveAt(0);
        return true;
    }

    /// <summary>One print, mirrored by the symmetry axes, with the tip jitter.</summary>
    void StampAll(SceneObject o, TextureSet ts, Vector3 p, Vector3 n, float r, Vector3 prev)
    {
        float rr = r, ang = brush.StampAngle;
        if (brush.Tip != null || brush.Stamp != null)
        {
            if (brush.SizeJitter > 0) rr = r * MathF.Max(0.1f, 1 + ((float)paintRnd.NextDouble() - 0.5f) * 2 * brush.SizeJitter);
            if (brush.AngleJitter > 0) ang += ((float)paintRnd.NextDouble() - 0.5f) * 2 * brush.AngleJitter;
        }
        if (brush.Scatter > 0)
        {
            // scatter: the print lands somewhere inside the brush
            Triangulator.Basis(Vector3.Normalize(n), out var su, out var sv);
            float a = (float)paintRnd.NextDouble() * MathF.Tau, d = MathF.Sqrt((float)paintRnd.NextDouble()) * brush.Scatter * r;
            var off = (su * MathF.Cos(a) + sv * MathF.Sin(a)) * d;
            p += off; prev += off;
        }
        foreach (var (mp, mn, mprev) in Mirrors(p, n, prev))
        {
            ts.Stamp(brush, mp, mn, rr, ang, mprev);
            if (brush.Drips > 0 && !brush.Erase && paintTool == PaintTool.Brush && paintRnd.NextDouble() < brush.Drips * 0.25) Drip(o, ts, mp, mn, rr);
        }
    }

    /// <summary>A drop that runs down the surface from a print (gravity laid on the surface), thinner as it goes, until the
    /// surface turns flat or ends. Object space; the surface is found again under the drop at every step.</summary>
    void Drip(SceneObject o, TextureSet ts, Vector3 p, Vector3 n, float r)
    {
        var down = Vector3.TransformNormal(-Vector3.UnitZ, InverseOf(o.World));
        if (down.LengthSquared() < 1e-10f) return;
        down = Vector3.Normalize(down);
        float len = brush.DripLength / ObjScale(o) * (0.4f + 0.6f * (float)paintRnd.NextDouble());
        float rad = r * (0.18f + 0.12f * (float)paintRnd.NextDouble());
        float step = MathF.Max(rad * 0.5f, 1e-4f), run = 0;
        var prev = p; var b0 = brush.Strength; brush.Strength = MathF.Min(1, b0 * 1.2f);
        try
        {
            while (run < len && rad > r * 0.03f)
            {
                var d = down - n * Vector3.Dot(down, n);
                if (d.LengthSquared() < 0.04f) break; // a flat top: the drop stops
                d = Vector3.Normalize(d);
                var side = Vector3.Cross(n, d); // a drop wanders a little, it never runs perfectly straight
                var q = p + Vector3.Normalize(d + side * (((float)paintRnd.NextDouble() - 0.5f) * 0.35f)) * step;
                // back onto the surface: a short ray from above the new point, along the normal
                var wo = Vector3.Transform(q + n * step * 2, o.World); var wd = Vector3.Normalize(Vector3.TransformNormal(-n, o.World));
                var h = Picking.PickFace(o, new Ray(wo, wd));
                if (h is not { } hit) break;
                var ql = Vector3.Transform(hit.Point, InverseOf(o.World));
                if (Vector3.Distance(ql, q) > step * 4) break; // fell off an edge
                var nn = Vector3.Normalize(o.Mesh.FaceNormal(hit.Face));
                if (Vector3.Dot(nn, n) < 0.2f) break;         // around a sharp corner
                ts.Stamp(brush, ql, nn, rad, brush.StampAngle, p);
                run += Vector3.Distance(p, ql); prev = p; p = ql; n = nn;
                rad *= 0.985f;
            }
            if (run > step * 3) ts.Stamp(brush, p, n, rad * 1.6f, brush.StampAngle, prev); // the bead at the end
        }
        finally { brush.Strength = b0; }
    }

    static Matrix4x4 InverseOf(Matrix4x4 m) { Matrix4x4.Invert(m, out var inv); return inv; }

    IEnumerable<(Vector3 P, Vector3 N, Vector3 Prev)> Mirrors(Vector3 p, Vector3 n, Vector3 prev)
    {
        foreach (var m in AxisMirrors(p, n, prev))
        {
            yield return m;
            // radial symmetry: copies turned around the object's vertical axis (through its pivot)
            for (int k = 1; k < Math.Max(1, brush.Radial); k++)
            {
                var rot = Matrix4x4.CreateRotationZ(MathF.Tau * k / brush.Radial);
                yield return (Vector3.Transform(m.P, rot), Vector3.TransformNormal(m.N, rot), Vector3.Transform(m.Prev, rot));
            }
        }
    }

    IEnumerable<(Vector3 P, Vector3 N, Vector3 Prev)> AxisMirrors(Vector3 p, Vector3 n, Vector3 prev)
    {
        yield return (p, n, prev);
        var axes = new List<Vector3>();
        if (brush.SymX) axes.Add(new Vector3(-1, 1, 1));
        if (brush.SymY) axes.Add(new Vector3(1, -1, 1));
        if (brush.SymZ) axes.Add(new Vector3(1, 1, -1));
        // every combination of the chosen axes (X, Y, XY...)
        for (int mask = 1; mask < 1 << axes.Count; mask++)
        {
            var s = Vector3.One;
            for (int i = 0; i < axes.Count; i++) if ((mask & 1 << i) != 0) s *= axes[i];
            yield return (p * s, n * s, prev * s);
        }
    }

    /// <summary>Shift+click: prints along the screen line, each one projected on the model.</summary>
    void StrokeLine(SceneObject o, Viewport vp, Vector2 a, Vector2 b, float r)
    {
        var ts = o.Paint.Cur;
        int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 3));
        Vector3 prev = lastStamp, lastP = lastStamp;
        for (int i = 1; i <= steps; i++)
        {
            if (!HitLocal(o, vp, Vector2.Lerp(a, b, i / (float)steps), out var p, out var n, out _)) continue;
            if (Vector3.Distance(p, lastP) < r * MathF.Max(brush.Spacing, 0.02f) && i < steps) continue;
            StampAll(o, ts, p, n, r, prev);
            prev = lastP; lastP = p;
        }
        lastStamp = lastP;
    }

    void PaintFillClick(SceneObject o, TextureSet ts, int face)
    {
        if (!PrepareStroke(o, ts)) return;
        if (!ts.Fill(brush, face, fillScope)) { paintUndo.RemoveAt(paintUndo.Count - 1); SetStatus("That face belongs to another texture set: pick it in the set list."); return; }
        if (brush.SymX || brush.SymY || brush.SymZ) SetStatus("Fill done (symmetry does not apply to fills).");
        Dirty = true;
    }

    void VertexStamp(SceneObject o, Vector3 p, Vector3 n, float r)
    {
        int c = 0;
        foreach (var (mp, mn, _) in Mirrors(p, n, p))
            c += terrainPaintLayer >= 0 ? VertexPaint.StampLayer(o.Mesh, mp, mn, r, terrainPaintLayer, brush.Strength, brush.Hardness, brush.BackfaceCull)
                                        : VertexPaint.Stamp(o.Mesh, mp, mn, r, vertexColour, brush.Strength, brush.Hardness, brush.BackfaceCull);
        if (c > 0) { o.Touch(); paintMeshVersion[o.Id] = o.Version; Dirty = true; } // colours only: the texel maps stay valid
    }

    bool PaintUndo()
    {
        if (paintUndo.Count == 0) return false;
        var (o, ts, layer, snap) = paintUndo[^1];
        paintUndo.RemoveAt(paintUndo.Count - 1);
        if (o.Paint == null || !o.Paint.Sets.Contains(ts) || layer >= ts.Layers.Count) return true;
        ts.Restore(layer, snap);
        SetStatus("Undo paint stroke");
        return true;
    }

    /// <summary>Paint-mode keys: [ ] brush size, B / E / F tools, I eyedropper, X mirror, Ctrl+Z undo stroke, Esc leaves paint mode.</summary>
    bool PaintKeys(bool ctrl)
    {
        if (!paintMode) return false;
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        if (P(ImGuiKey.LeftBracket)) { brush.Radius = MathF.Max(0.002f, brush.Radius / 1.15f); return true; }
        if (P(ImGuiKey.RightBracket)) { brush.Radius = MathF.Min(20f, brush.Radius * 1.15f); return true; }
        // Ctrl+Z undoes what was done last: a paint stroke, or a scene edit made in between (a move, a vertex colour stroke...)
        if (ctrl && P(ImGuiKey.Z) && !ImGui.GetIO().KeyShift && paintUndo.Count > 0
            && (paintUndoStamp.TryGetValue(paintUndo[^1].Snap, out var st) ? st.Value : 0) > Undo.LastStamp && PaintUndo()) return true;
        if (!ctrl)
        {
            if (P(ImGuiKey.B)) { SetPaintTool(PaintTool.Brush); return true; }
            if (P(ImGuiKey.E)) { SetPaintTool(PaintTool.Eraser); return true; }
            if (P(ImGuiKey.F)) { SetPaintTool(PaintTool.Fill); return true; }
            if (P(ImGuiKey.I)) { paintPick = !paintPick; SetStatus(paintPick ? "Eyedropper: click the model to pick its colour." : "Eyedropper off"); return true; }
        }
        if (P(ImGuiKey.Escape)) { paintMode = false; SetStatus("Paint mode off"); return true; }
        return false;
    }

    void SetPaintTool(PaintTool t)
    {
        paintTool = t;
        brush.Erase = t == PaintTool.Eraser;
        brush.Mode = t switch { PaintTool.Clone => BrushMode.Clone, PaintTool.Smudge => BrushMode.Smudge, _ => BrushMode.Paint };
        if (t != PaintTool.Decal) { brush.Stamp = null; brush.StampName = null; }
        if (t == PaintTool.Decal) { shelfTab = 1; if (brush.Stamp == null) SetStatus("Pick a decal in the shelf on the right."); }
        if (t == PaintTool.Clone && cloneSource == null) SetStatus("Clone: Ctrl+click on the model to pick the source, then paint.");
        if (t == PaintTool.VertexColour) SetStatus("Vertex colours: green = not emissive, white = emissive, red = exterior props. The view shows the vertex colours.");
    }

    /// <summary>Brush circle on the surface under the mouse (and its mirrors), plus the clone source.</summary>
    void PaintCursorLines()
    {
        if (!paintMode || !paintHover.Hit) return;
        var (p, n, r, _) = paintHover;
        var col = paintPick ? new Vector4(0.3f, 1, 0.6f, 1) : brush.Erase ? new Vector4(1, 0.4f, 0.3f, 1) : brush.MaskMode ? new Vector4(0.5f, 0.8f, 1, 1) : paintTool == PaintTool.VertexColour ? new Vector4(vertexColour.X, vertexColour.Y, vertexColour.Z, 1) : new Vector4(1, 1, 1, 1);
        var o = Current;
        Circle(p, n, r, col);
        if (o != null && (brush.SymX || brush.SymY || brush.SymZ || brush.Radial > 1))
        {
            Matrix4x4.Invert(o.World, out var inv);
            var pl = Vector3.Transform(p, inv); var nl = Vector3.TransformNormal(n, inv);
            foreach (var (mp, mn, _) in Mirrors(pl, nl, pl).Skip(1))
                Circle(Vector3.Transform(mp, o.World), Vector3.Normalize(Vector3.TransformNormal(mn, o.World)), r, col * 0.6f);
        }
        if (paintTool == PaintTool.Clone && cloneSource is { } cs && o != null)
        {
            var w = Vector3.Transform(cs, o.World); float s = r * 0.4f;
            lines.Line(w - Vector3.UnitX * s, w + Vector3.UnitX * s, new Vector4(0.3f, 1, 0.4f, 1));
            lines.Line(w - Vector3.UnitY * s, w + Vector3.UnitY * s, new Vector4(0.3f, 1, 0.4f, 1));
            lines.Line(w - Vector3.UnitZ * s, w + Vector3.UnitZ * s, new Vector4(0.3f, 1, 0.4f, 1));
        }
    }

    void Circle(Vector3 p, Vector3 n, float r, Vector4 col)
    {
        Triangulator.Basis(n, out var u, out var v);
        var off = n * r * 0.02f;
        const int seg = 40;
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            lines.Line(p + off + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * r, p + off + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * r, col);
        }
        float h = r * Math.Clamp(brush.Hardness, 0, 1);
        if (h > r * 0.05f)
            for (int i = 0; i < seg; i += 2)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                lines.Line(p + off + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * h, p + off + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * h, col * 0.7f);
            }
        lines.Line(p, p + n * r * 0.5f, col);
    }

    // ------------------------------------------------------------------------------ high-poly bake (background)

    readonly BakeSettings bakeSettings = new();
    int bakeHighId = -1;
    bool bakeColourLayer = true, bakeHideHigh = true;
    System.Threading.Tasks.Task<BakeResult> bakeTask;
    (SceneObject O, TextureSet Ts) bakeTarget;
    float bakeProgress;
    string bakeInfo;

    void StartBake(SceneObject low, TextureSet ts)
    {
        var high = Scene.Find(bakeHighId);
        if (high == null || high == low) { SetStatus("Pick the high-poly model first."); return; }
        if (bakeTask is { IsCompleted: false }) return;
        Matrix4x4.Invert(low.World, out var inv);
        var xf = high.World * inv;
        var mesh = high.Evaluated().Clone();
        var map = ts.Map;
        var s = bakeSettings;
        // textures of the high-poly materials (Tripo models come with their texture)
        var tex = new Dictionary<int, RgbaImage>();
        foreach (var mid in mesh.Faces.Select(f => f.MatId).Distinct())
        {
            var m = Scene.Materials.FirstOrDefault(x => x.Id == mid);
            var path = m?.DiffuseTexture != null ? renderer.Textures.Resolve(m.DiffuseTexture) : null;
            if (path != null) try { tex[mid] = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? MoloMax.Core.Gta.Library.TextureReader.Load(path) : LoadImageRgba(path); } catch { }
        }
        bakeTarget = (low, ts); bakeProgress = 0; bakeInfo = null;
        bakeTask = System.Threading.Tasks.Task.Run(() => PaintBaker.Bake(map, mesh, xf, s, id => tex.TryGetValue(id, out var i) ? i : null, p => bakeProgress = p));
        SetStatus($"Baking {high.Name} onto {low.Name}...");
    }

    void PumpBake()
    {
        if (bakeTask == null || !bakeTask.IsCompleted) return;
        var (o, ts) = bakeTarget;
        try
        {
            var r = bakeTask.Result;
            if (o.Paint != null && o.Paint.Sets.Contains(ts))
            {
                paintUndo.Clear();
                ts.ApplyBake(r, bakeColourLayer && bakeSettings.Colour);
                UploadPaint(o, ts, true);
                if (bakeHideHigh && Scene.Find(bakeHighId) is { } h) h.Hidden = true;
            }
            bakeInfo = $"{r.HitRatio * 100:0}% of the texels found the high-poly" + (r.HitRatio < 0.9f ? " - raise the distance if parts are missing" : "");
            SetStatus("Bake done: " + bakeInfo);
        }
        catch (Exception e) { bakeInfo = "Bake failed: " + (e.InnerException?.Message ?? e.Message); SetStatus(bakeInfo); }
        bakeTask = null;
    }
}
