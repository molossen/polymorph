using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Map;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MAP: a FiveM resource's stream folder opened like a CodeWalker project. Its .ymap files load where they stand, its
/// models (.ydr / .yft / .ydd) replace the game's, its interiors open from its .ytyp; saving writes every changed file
/// back in place (new maps go into the folder).
/// </summary>
public sealed partial class EditorApp
{
    StreamFolder stream;

    /// <summary>The name of an archetype: the stream folder's .ytyp / files first, then the game's.</summary>
    string StreamName(uint h) => stream != null && stream.Names.TryGetValue(h, out var n) ? n : game?.FindModel(h)?.Name;

    /// <summary>The resource holding the stream folder (its parent when the folder is called "stream").</summary>
    string StreamResourceDir()
    {
        var f = stream.Folder.TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(f), "stream", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(f) : f;
    }

    void OpenStreamFolderDialog()
    {
        var dir = PickFolder("Map folder: a FiveM map resource or its stream folder (.ymap, .ytyp, .ydr...)");
        Input.ReleaseAllKeys();
        if (dir != null) OpenStreamFolder(dir);
    }

    public void OpenStreamFolder(string dir)
    {
        if (!Directory.Exists(dir)) { SetStatus("Folder not found: " + dir); return; }
        // the resource folder itself (fxmanifest.lua next to "stream"): its stream folder
        if (Directory.Exists(Path.Combine(dir, "stream")) && !string.Equals(Path.GetFileName(dir.TrimEnd('\\', '/')), "stream", StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(dir, "stream");
        if (!MapMode) SetMapMode(true);
        StreamFolder sf;
        try { sf = StreamFolder.Read(dir); }
        catch (Exception e) { SetStatus("Could not read the folder: " + e.Message); return; }
        if (stream != null) CloseStreamFolder();
        stream = sf;
        // models of the folder: forget what was read for these names (the folder's own files are used now)
        foreach (var h in sf.Models.Keys.Concat(sf.DictionaryOf.Keys))
            if (archSlots.Remove(h)) mapSlotOf.Remove("map:" + h.ToString("X8"));
        foreach (var y in sf.Ymaps) OpenYmapFile(y);
        // its interiors open at once (kept open: they are your work)
        foreach (var (id, (doc, e)) in entOf.ToList())
            if (e.IsMlo && sf.Interiors.ContainsKey(e.ArchetypeHash) && sf.Contains(doc.FilePath) && Scene.Find(id) is { } inst && EditInterior(inst))
                if (interiors.Values.FirstOrDefault(i => i.InstanceId == id) is { } oi) oi.Auto = false;
        var docs = mapDocs.Values.Where(d => sf.Contains(d.FilePath)).ToList();
        // their folders start closed in the Scene Explorer (hundreds of rows each); a pick opens the one it is in
        foreach (var d in docs) closedFolders.Add(d.Name);
        foreach (var oi in interiors.Values.Where(i => i.FilePath != null)) closedFolders.Add(oi.Folder);
        if (docs.Count > 0)
        {
            mapActive = docs[0].Name;
            // the camera goes to the interiors of the folder, else to the middle of its entities (median: a stray far entity
            // or a broken position does not throw the view to the other side of the world)
            var mlo = docs.SelectMany(d => d.Entities).Where(x => x.IsMlo && sf.Interiors.ContainsKey(x.ArchetypeHash)).Select(x => x.Position).ToList();
            var all = mlo.Count > 0 ? mlo : docs.SelectMany(d => d.Entities).Where(x => x.IsHd).Select(x => x.Position).Where(p => MathF.Abs(p.X) < 20000 && MathF.Abs(p.Y) < 20000 && MathF.Abs(p.Z) < 5000).ToList();
            if (all.Count > 0)
            {
                float Med(Func<Vector3, float> f) { var l = all.Select(f).OrderBy(v => v).ToList(); return l[l.Count / 2]; }
                float Spread(Func<Vector3, float> f) { var l = all.Select(f).OrderBy(v => v).ToList(); return l[(int)(l.Count * 0.9f)] - l[(int)(l.Count * 0.1f)]; }
                var cam = Viewports[ActiveViewport].Camera;
                if (cam.Ortho) { cam.Kind = ViewKind.Perspective; cam.Ortho = false; }
                cam.Target = new Vector3(Med(p => p.X), Med(p => p.Y), Med(p => p.Z));
                cam.Distance = Math.Clamp(MathF.Max(Spread(p => p.X), Spread(p => p.Y)) * 1.2f, 120, 600); cam.Pitch = 0.6f;
            }
        }
        SetStatus($"{Path.GetFileName(dir.TrimEnd('\\', '/'))}: {sf.Ymaps.Count} .ymap, {sf.Ytyps.Count} .ytyp ({sf.Interiors.Count} interior(s)), {sf.Models.Count + sf.Dictionaries.Count} model file(s)"
                  + (sf.Errors.Count > 0 ? " | " + string.Join(" | ", sf.Errors.Take(3)) : "") + (game == null ? " | load the game to see its own models around" : ""));
    }

    void CloseStreamFolder()
    {
        if (stream == null) return;
        var sf = stream;
        foreach (var oi in interiors.Values.Where(i => i.FilePath != null).ToList()) CloseInterior(oi);
        foreach (var d in mapDocs.Values.Where(d => sf.Contains(d.FilePath)).ToList()) UnloadMapDoc(d);
        foreach (var h in sf.Models.Keys.Concat(sf.DictionaryOf.Keys))
            if (archSlots.Remove(h)) mapSlotOf.Remove("map:" + h.ToString("X8"));
        stream = null;
        SetStatus("Stream folder closed.");
    }

    /// <summary>The .ymap of the selected map object (else of the active map).</summary>
    MapDocument SelectedMapDoc()
    {
        foreach (var id in Selected) if (entOf.TryGetValue(id, out var de)) return de.Doc;
        foreach (var id in Selected) if (Scene.Find(id) is { } o && o.Folder != null && mapDocs.TryGetValue(o.Folder, out var d)) return d;
        return null;
    }

    /// <summary>Writes one .ymap: where it was opened from, else (a game map, Save as) where you pick.</summary>
    public bool SaveMapDoc(MapDocument d, bool saveAs = false, string path = null)
    {
        SyncMapDoc(d, apply: true);
        path ??= !saveAs && d.FilePath != null && File.Exists(d.FilePath) ? d.FilePath : null;
        if (path == null)
        {
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "GTA map (*.ymap)|*.ymap", Title = "Save " + d.Name + ".ymap", FileName = d.Name + ".ymap",
                InitialDirectory = d.FilePath != null ? Path.GetDirectoryName(d.FilePath) : stream?.Folder ?? Path.Combine(MapOutputDir(), "stream"),
            };
            if (dlg.InitialDirectory != null) Directory.CreateDirectory(dlg.InitialDirectory);
            var ok = ShowFileDialog(dlg) == System.Windows.Forms.DialogResult.OK;
            Input.ReleaseAllKeys();
            if (!ok) return false;
            path = dlg.FileName;
        }
        try
        {
            var bytes = d.Save(h => archSlots.TryGetValue(h, out var s) ? (s.Min, s.Max) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            d.FilePath = path; d.Modified = false;
            foreach (var id in mapDeleted.Where(id => !entOf.ContainsKey(id)).ToList()) mapDeleted.Remove(id);
            mapChanged.Remove(d.Name);
            SetStatus($"{Path.GetFileName(path)} saved ({d.Entities.Count} entities) in {Path.GetDirectoryName(path)}");
            return true;
        }
        catch (Exception e) { SetStatus($"Could not save {d.Name}.ymap: {e.Message}"); return false; }
    }

    /// <summary>MAP Ctrl+S: the changed .ymap files (and interiors) go back where they came from, the others into the resource.</summary>
    void MapQuickSave() => SaveMaps();

    /// <summary>Map panel part: open / save / close a stream folder.</summary>
    void StreamFolderPanel(float w2)
    {
        if (stream == null)
        {
            if (ImGui.Button("Open stream folder...", new Vector2(-1, 26))) OpenStreamFolderDialog();
            Theme.Tip("Like a CodeWalker project: every .ymap of the folder loads where it stands, its .ydr / .yft / .ydd are drawn, its interiors open from its .ytyp. SAVE writes the changed files back into the folder.");
            return;
        }
        Ui.TextColored(Theme.Gold, "Stream folder: " + Path.GetFileName(stream.Folder.TrimEnd('\\', '/')));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(stream.Folder);
        Ui.TextDisabled($"{stream.Ymaps.Count} .ymap · {stream.Ytyps.Count} .ytyp · {stream.Interiors.Count} interior(s) · {stream.Models.Count + stream.Dictionaries.Count} models");
        if (ImGui.Button("Reload##sf", new Vector2(w2, 0))) { var f = stream.Folder; CloseStreamFolder(); OpenStreamFolder(f); }
        Theme.Tip("Reads the folder again (unsaved changes in it are lost).");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Close##sf", new Vector2(w2, 0))) CloseStreamFolder();
    }
}
