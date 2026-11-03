using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Paint;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Molo Paint starter pack: 80 brush alphas and 80 relief textures (CC0, from the SubCraft starter pack, the relief
/// rebuilt as height maps from its normal maps), the user's own alphas, and the eyedropper.
/// </summary>
public sealed partial class EditorApp
{
    string alphaSearch = "", reliefSearch = "";
    List<(string Key, string Label, string Path)> packAlphas, userAlphas;
    List<(string Name, string Label, string Path, string Thumb)> reliefs;
    /// <summary>Eyedropper armed (I): the next click on the model picks the colour, roughness, metal and relief under it.</summary>
    bool eyedropper;

    static string PaintAssets => Path.Combine(AppContext.BaseDirectory, "Assets", "paint");
    static string ReliefFolder => Path.Combine(PaintAssets, "relief");

    void EnsurePaintPack()
    {
        BrushTips.PackFolder ??= Path.Combine(PaintAssets, "alphas");
        packAlphas ??= BrushTips.Images(false);
        userAlphas ??= BrushTips.Images(true);
        if (reliefs == null)
        {
            reliefs = new();
            if (Directory.Exists(ReliefFolder))
                foreach (var f in Directory.GetFiles(ReliefFolder, "*.png").Where(f => !f.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    var th = Path.Combine(ReliefFolder, n + ".thumb.png");
                    reliefs.Add((n, BrushTips.Spaced(n), f, File.Exists(th) ? th : f));
                }
        }
    }

    static bool Matches(string label, string search) => string.IsNullOrWhiteSpace(search) || label.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    void PickTip(string key)
    {
        brush.TipName = key; brush.Tip = BrushTips.Get(key);
        if (key != "Round" && brush.Tip == null) { SetStatus("This alpha could not be read."); brush.TipName = "Round"; return; }
        if (paintTool is not (PaintTool.Brush or PaintTool.Eraser or PaintTool.Smudge or PaintTool.Clone)) SetPaintTool(PaintTool.Brush);
        SetStatus("Brush tip: " + BrushTips.LabelOf(key));
    }

    /// <summary>Shelf, Alphas tab: the drawn tips, the starter pack and the user's own.</summary>
    void AlphaGrid(int cols, float cell, float gap)
    {
        EnsurePaintPack();
        var dl = ImGui.GetWindowDrawList();
        int k = 0;
        void Cell(string key, string label, string thumbPath)
        {
            if (k % cols != 0) ImGui.SameLine(0, gap);
            ImGui.BeginGroup();
            var p = ImGui.GetCursorScreenPos();
            ImGui.PushID(key);
            bool click = ImGui.InvisibleButton("##tip", new Vector2(cell, cell));
            bool hov = ImGui.IsItemHovered();
            ImGui.PopID();
            dl.AddRectFilled(p, p + new Vector2(cell, cell), 0xFF1E1E1E, 4);
            if (thumbPath != null)
            {
                var srv = renderer.Textures.Thumb(thumbPath);
                if (srv != null) dl.AddImage(imgui.IdOf(srv), p + new Vector2(3), p + new Vector2(cell - 3));
            }
            else TipPreview(dl, key, p, cell);
            bool active = (brush.TipName ?? "Round") == key && brush.Stamp == null;
            dl.AddRect(p, p + new Vector2(cell, cell), active ? 0xFF30B0FF : hov ? 0xFFFFFFFF : 0xFF202020, 4, ImDrawFlags.None, active ? 3 : 1);
            Ui.TextDisabled(Clip(label, cell));
            if (hov) ImGui.SetTooltip(label);
            ImGui.EndGroup();
            k++;
            if (click) PickTip(key);
        }
        void Title(string t) { if (k % cols != 0) ImGui.NewLine(); k = 0; Ui.TextDisabled(t); }

        Title("Drawn");
        foreach (var n in BrushTips.Names) if (Matches(n, alphaSearch)) Cell(n, n, null);
        Title($"Starter pack ({packAlphas.Count}, CC0)");
        foreach (var (key, label, path) in packAlphas) if (Matches(label, alphaSearch)) Cell(key, label, path);
        Title($"Mine ({userAlphas.Count})");
        foreach (var (key, label, path) in userAlphas) if (Matches(label, alphaSearch)) Cell(key, label, path);
        if (k % cols != 0) ImGui.NewLine();
        if (userAlphas.Count == 0) Ui.TextDisabled("Import your own with the button above.");
    }

    /// <summary>Copies images into Documents\Polymorph\alphas and lists them.</summary>
    void ImportAlphas()
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog { Title = "Brush alphas (white = paint)", Filter = "Images|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds", Multiselect = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Directory.CreateDirectory(BrushTips.UserFolder);
        int n = 0;
        foreach (var f in dlg.FileNames)
        {
            try { File.Copy(f, Path.Combine(BrushTips.UserFolder, Path.GetFileName(f)), true); n++; } catch { }
        }
        userAlphas = null; EnsurePaintPack();
        SetStatus($"{n} alpha(s) added to Mine.");
    }

    /// <summary>Shelf, Relief tab: tileable height textures; a click adds a relief layer.</summary>
    void ReliefGrid(int cols, float cell, float gap)
    {
        EnsurePaintPack();
        if (reliefs.Count == 0) { Ui.TextDisabled("Relief pack not found next to Polymorph.exe."); return; }
        var dl = ImGui.GetWindowDrawList();
        int k = 0;
        foreach (var (name, label, path, thumb) in reliefs)
        {
            if (!Matches(label, reliefSearch)) continue;
            if (k % cols != 0) ImGui.SameLine(0, gap);
            ImGui.BeginGroup();
            var p = ImGui.GetCursorScreenPos();
            ImGui.PushID(name);
            bool click = ImGui.InvisibleButton("##rl", new Vector2(cell, cell));
            bool hov = ImGui.IsItemHovered();
            ImGui.PopID();
            var srv = renderer.Textures.Thumb(thumb);
            if (srv != null) dl.AddImage(imgui.IdOf(srv), p, p + new Vector2(cell, cell));
            else dl.AddRectFilled(p, p + new Vector2(cell, cell), 0xFF3A3A3A);
            dl.AddRect(p, p + new Vector2(cell, cell), hov ? 0xFFFFFFFF : 0xFF202020);
            Ui.TextDisabled(Clip(label, cell));
            if (hov) ImGui.SetTooltip(label + "\nclick: new relief layer on the selected texture set");
            ImGui.EndGroup();
            k++;
            if (click) AddReliefLayer(name, label, path);
        }
        if (k == 0) Ui.TextDisabled("Nothing found.");
    }

    void AddReliefLayer(string name, string label, string path)
    {
        var ts = CurSet; if (ts == null) return;
        var cur = ts.Current;
        int folder = cur == null ? -1 : cur.IsFolder ? cur.Id : cur.Folder;
        AddLayer(ts, new PaintLayer
        {
            Name = "Relief: " + label, Kind = LayerKind.Fill, Folder = folder,
            UseColor = false, UseRough = false, UseHeight = true,
            TexHeight = path, TileMetres = 1f, TexHeightAmount = 0.35f,
        });
        paintUndo.Clear();
        SetStatus($"Relief '{label}' added (Layers tab: tile size, strength, mask to limit it).");
    }

    /// <summary>Eyedropper click: the brush takes the painted values under the mouse.</summary>
    void EyedropAt(TextureSet ts, Vector3 pl, float r)
    {
        eyedropper = false;
        if (ts?.Map == null) return;
        int t = ts.Map.Nearest(pl, MathF.Max(r, 1e-3f) * 2);
        if (t < 0) { SetStatus("Nothing painted there."); return; }
        brush.ColorValue = ts.CColor[t]; brush.Color = true;
        brush.RoughValue = ts.CRough[t]; brush.MetalValue = ts.CMetal[t]; brush.HeightValue = ts.CHeight[t];
        var c = brush.ColorValue;
        SetStatus($"Picked colour {(int)(c.X * 255)}, {(int)(c.Y * 255)}, {(int)(c.Z * 255)}, roughness {brush.RoughValue:0.00}, metal {brush.MetalValue:0.00}.");
    }
}
