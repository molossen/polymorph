using System.Numerics;
using Keys = System.Windows.Forms.Keys;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>Short films of the Molo Paint 1.6 additions: alphas, reliefs, radial symmetry and the eyedropper.</summary>
public sealed partial class EditorApp
{
    /// <summary>A wall facing the camera, painted with a smart material, ready for a film.</summary>
    (SceneObject Wall, TextureSet Ts, Viewport Vp)? FilmWall(string smart, float w = 3.2f, float h = 2.4f)
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; vp.Shading = ShadingMode.Realistic; cam.Ortho = false;
        var wall = Prim(CreateKind.Box, Vector3.Zero); wall.Name = "wall";
        Do("wall", () => { wall.Mesh = Primitives.Box(w, 0.3f, h); wall.Touch(); });
        SelectOnly(wall);
        propTab = PropTab.Paint; paintTab = 0; paintRes = 1024;
        DoMesh("Smart UV Project", (oo, mm, s2) => UvUnwrap.SmartProject(mm));
        wall = Scene.Find(wall.Id);
        StartPaint(wall);
        var ts = wall.Paint?.Cur;
        if (ts == null) return null;
        ts.Layers.Clear(); foreach (var l in SmartMaterials.Make(smart, 4)) AddLayer(ts, l);
        ts.Refresh(true); UploadPaint(wall, ts, true);
        cam.Target = new Vector3(0, 0, h / 2); cam.Distance = 4.3f; cam.Pitch = 0.08f; cam.Yaw = -MathF.PI / 2;
        return (wall, ts, vp);
    }

    IEnumerable<object> TutPaintAlphas()
    {
        var fw = FilmWall("GTA wall paint");
        if (fw is not { } f) { Expect(false, "paint did not start"); yield break; }
        var (wall, ts, vp) = f;
        float front = wall.Mesh.Verts.Min(v => v.Y);
        Vector2 On(float x, float z) => ScreenOf(vp, new Vector3(x, front, z));
        yield return Frames(10);
        foreach (var x in StartFilm("24_paint_alphas", 4)) yield return x;
        paintMode = true; shelfTab = 3; alphaSearch = ""; SetPaintTool(PaintTool.Brush);
        brush.Stamp = null; brush.Erase = false; brush.MaskMode = false; brush.Scatter = 0; brush.Radial = 1; brush.SymX = brush.SymY = brush.SymZ = false;
        yield return Frames(10);
        foreach (var x in Say("Molo Paint 1.6: 80 new alphas", "Shelf > Alphas: scratches, cracks, impacts, grime, leaves...", 70)) yield return x;

        // 1. bullet impacts, one click = one print
        foreach (var x in Say("1. Bullet impacts", "Pick the alpha, then click the wall", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("alpha:pack:BulletImpactCluster"))) yield return x;
        brush.ColorValue = new Vector3(0.12f, 0.11f, 0.1f); brush.Radius = 0.32f; brush.Strength = 1f; brush.Height = true; brush.HeightValue = 0.15f;
        foreach (var (x0, z0) in new[] { (-1.0f, 1.6f), (0.2f, 1.1f), (1.1f, 1.7f) })
        {
            foreach (var x in ClickAt(On(x0, z0), false, 18)) yield return x;
            yield return Frames(14);
        }
        brush.Height = false;

        // 2. grime streaks along the top
        foreach (var x in Say("2. Grime streaks", "Drag along the top of the wall", 15)) yield return x;
        foreach (var x in ClickAt(TutCentre("alpha:pack:GrimeStreakDown"))) yield return x;
        brush.ColorValue = new Vector3(0.22f, 0.18f, 0.13f); brush.Radius = 0.45f; brush.Strength = 0.7f; brush.Spacing = 0.5f;
        foreach (var x in Stroke(On(-1.4f, 1.95f), On(1.4f, 1.95f), 70)) yield return x;
        yield return Frames(15);

        // 3. scatter
        foreach (var x in Say("3. Scatter", "Stroke > Scatter: each print lands somewhere inside the brush", 15)) yield return x;
        foreach (var x in ClickAt(TutCentre("alpha:pack:DustDirtPatch"))) yield return x;
        brush.ColorValue = new Vector3(0.3f, 0.26f, 0.2f); brush.Radius = 0.3f; brush.Strength = 0.6f; brush.Scatter = 0.9f; brush.SizeJitter = 0.5f; brush.AngleJitter = 180; brush.Spacing = 0.6f;
        foreach (var x in Stroke(On(-1.4f, 0.3f), On(1.4f, 0.45f), 80)) yield return x;
        brush.Scatter = 0; brush.SizeJitter = 0; brush.AngleJitter = 0; brush.Spacing = 0.15f;
        yield return Frames(20);
        foreach (var x in Say("Your own alphas too", "Import alphas... (PNG / JPG, white = paint)", 50)) yield return x;
        Expect(ts.Layers.Any(l => l.Kind == LayerKind.Paint && l.Cover != null && l.Cover.Any(c => c > 0.5f)), "the strokes painted the wall");
        foreach (var x in EndFilm()) yield return x;
        PickTip("Round");
        ShowPanel = false;
    }

    IEnumerable<object> TutPaintRelief()
    {
        var fw = FilmWall("Dirty concrete");
        if (fw is not { } f) { Expect(false, "paint did not start"); yield break; }
        var (wall, ts, vp) = f; var cam = vp.Camera;
        IEnumerable<object> Turn(int n, float speed = 0.3f) { for (int i = 0; i < n; i++) { cam.Orbit(MathF.Sin(cam.Yaw + MathF.PI / 2) > 0.3f ? -MathF.Abs(speed) : speed, 0); yield return Frames(1); } }
        void Up() { ts.Refresh(true); UploadPaint(wall, ts, true); }
        yield return Frames(10);
        foreach (var x in StartFilm("25_paint_relief", 4)) yield return x;
        paintMode = true; shelfTab = 4; reliefSearch = ""; Hour = 17; InteriorLight = false;
        yield return Frames(15);
        ts.NormalStrength = 8; cam.Pitch = 0.18f;
        foreach (var x in Say("Molo Paint 1.6: the Relief tab", "80 tileable reliefs: one click = a relief layer", 70)) yield return x;
        PaintLayer last = null;
        foreach (var (key, title) in new[] { ("Brick", "Bricks"), ("DiamondPlate", "Diamond plate"), ("CorrugatedMetal", "Corrugated metal"), ("Cobblestone", "Cobblestone") })
        {
            if (last != null) { ts.Layers.Remove(last); ts.SelectedLayer = 0; Up(); }
            tutSub = title;
            foreach (var x in ClickAt(TutCentre("relief:" + key))) yield return x;
            last = ts.Current; last.TexHeightAmount = 0.75f; last.TileMetres = 0.8f; last.TexturesChanged(); Up();
            foreach (var x in Turn(55)) yield return x;
        }
        foreach (var x in Say("Tile size and strength", "Layers tab: the relief layer's Tile size and Relief strength", 10)) yield return x;
        paintTab = 1;
        if (last != null)
            for (int i = 0; i <= 30; i++) { last.TexHeightAmount = 0.15f + 0.85f * i / 30f; last.TexturesChanged(); Up(); yield return Frames(2); }
        foreach (var x in Turn(50)) yield return x;
        foreach (var x in Say("Give it a mask to keep it where you want", "Then straight to the GTA normal map", 50)) yield return x;
        Expect(ts.CHeight.Max() - ts.CHeight.Min() > 0.2f, "the relief shows in the height");
        foreach (var x in EndFilm()) yield return x;
        paintTab = 0; ShowPanel = false; Hour = 13;
    }

    IEnumerable<object> TutPaintRadialPick()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; vp.Shading = ShadingMode.Realistic; cam.Ortho = false;
        var pole = Prim(CreateKind.Box, Vector3.Zero); pole.Name = "barrel";
        Do("barrel", () => { pole.Mesh = Primitives.Cylinder(0.55f, 1.4f, 32, 1, true); pole.Touch(); });
        SelectOnly(pole);
        propTab = PropTab.Paint; paintTab = 0; paintRes = 1024;
        DoMesh("Smart UV Project", (oo, mm, s2) => UvUnwrap.SmartProject(mm));
        pole = Scene.Find(pole.Id);
        StartPaint(pole);
        var ts = pole.Paint?.Cur;
        if (ts == null) { Expect(false, "paint did not start"); yield break; }
        ts.Layers.Clear(); foreach (var l in SmartMaterials.Make("Painted metal, worn", 6)) AddLayer(ts, l);
        ts.Refresh(true); UploadPaint(pole, ts, true);
        float zmin = pole.Mesh.Verts.Min(v => v.Z), zmax = pole.Mesh.Verts.Max(v => v.Z), r = 0.55f;
        cam.Target = new Vector3(0, 0, (zmin + zmax) / 2); cam.Distance = 3.2f; cam.Pitch = 0.3f; cam.Yaw = -MathF.PI / 2;
        Vector2 Side(float z, float ang = 0) => ScreenOf(vp, Vector3.Transform(new Vector3(0, -r, z), Matrix4x4.CreateRotationZ(ang)));
        yield return Frames(10);
        foreach (var x in StartFilm("26_paint_radial_pick", 3)) yield return x;
        paintMode = true; SetPaintTool(PaintTool.Brush); PickTip("Round");
        brush.Stamp = null; brush.Erase = false; brush.MaskMode = false; brush.Scatter = 0; brush.SymX = brush.SymY = brush.SymZ = false;
        yield return Frames(10);
        foreach (var x in Say("Molo Paint 1.6: radial symmetry", "Stroke > Radial: the stroke is repeated around the object", 60)) yield return x;

        // 1. radial: one vertical stripe becomes 8
        brush.Radial = 8; brush.ColorValue = new Vector3(0.85f, 0.65f, 0.08f); brush.Radius = 0.07f; brush.Strength = 1; brush.Hardness = 0.8f;
        foreach (var x in Say("1. Radial: 8 copies", "One stroke on the front, 8 stripes all around", 10)) yield return x;
        foreach (var x in Stroke(Side(zmin + 0.15f), Side(zmax - 0.15f), 50)) yield return x;
        for (int i = 0; i < 90; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }
        cam.Yaw = -MathF.PI / 2;
        brush.Radial = 1;

        // 2. eyedropper: pick the yellow, paint a ring with it
        brush.ColorValue = new Vector3(0.1f, 0.3f, 0.7f);
        foreach (var x in Say("2. Eyedropper", "Press I, click the model: the brush takes its colour", 20)) yield return x;
        foreach (var x in Tap(Keys.I)) yield return x;
        var stripe = Side((zmin + zmax) / 2);
        foreach (var x in ClickAt(stripe)) yield return x;
        Expect(brush.ColorValue.X > 0.6f && brush.ColorValue.Z < 0.4f, $"the eyedropper took the yellow ({brush.ColorValue})");
        yield return Frames(20);
        brush.Radial = 16; brush.Radius = 0.06f;
        foreach (var x in Say("Same colour, a ring with Radial 16", "Paint, roughness, metal and relief are picked together", 10)) yield return x;
        foreach (var x in Stroke(Side(zmax - 0.3f, -0.25f), Side(zmax - 0.3f, 0.25f), 30)) yield return x;
        for (int i = 0; i < 90; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }
        brush.Radial = 1;
        foreach (var x in Say("Molo Paint 1.6", "Update with the green Update button", 50)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        ShowPanel = false;
    }
}
