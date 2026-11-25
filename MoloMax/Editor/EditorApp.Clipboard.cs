using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// Ctrl+C / Ctrl+V of objects, between scenes and between MAP and MODEL mode: the copies keep their place in the world,
/// their meshes and their materials. A game entity copied from the map becomes a plain editable model when pasted.
/// Ctrl+Shift+V pastes them around the 3D cursor instead.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>What Ctrl+C took: object copies (meshes copied) and the materials they use.</summary>
    List<SceneObject> clipObjects;
    List<Core.Gta.GtaMaterial> clipMaterials;

    public int ClipboardCount => clipObjects?.Count ?? 0;

    public void CopySelection()
    {
        if (Selected.Count == 0) { SetStatus("Select the objects to copy first."); return; }
        var roots = Selected.Select(Scene.Find).Where(o => o != null).ToList();
        var all = new List<SceneObject>();
        var seen = new HashSet<int>();
        foreach (var o in roots) foreach (var x in Scene.Descendants(o).Prepend(o)) if (seen.Add(x.Id)) all.Add(x);
        clipObjects = all.Select(o =>
        {
            var c = o.Clone(); // the mesh is copied: later edits of the original do not change the clipboard
            if (c.ParentId >= 0 && !seen.Contains(c.ParentId)) c.ParentId = -1;
            return c;
        }).ToList();
        var used = new HashSet<int>(all.SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)));
        clipMaterials = Scene.Materials.Where(m => used.Contains(m.Id)).Select(m => m.Clone()).ToList();
        SetStatus($"Copied {clipObjects.Count} object(s) - Ctrl+V pastes them (also in the other mode), Ctrl+Shift+V around the 3D cursor");
    }

    public void PasteClipboard(bool atCursor = false)
    {
        if (clipObjects is not { Count: > 0 }) { SetStatus("Nothing copied yet (Ctrl+C on selected objects)."); return; }
        var pasted = new List<SceneObject>();
        Do("Paste", () =>
        {
            var matMap = new Dictionary<int, int>();
            foreach (var m in clipMaterials)
            {
                var same = Scene.Materials.FirstOrDefault(x => x.Name == m.Name && x.Shader == m.Shader && x.DiffuseTexture == m.DiffuseTexture);
                if (same != null) { matMap[m.Id] = same.Id; continue; }
                var c = m.Clone(); c.Id = Scene.NextMaterialId++; Scene.Materials.Add(c); matMap[m.Id] = c.Id;
            }
            var idMap = new Dictionary<int, int>();
            foreach (var o in clipObjects) idMap[o.Id] = Scene.NextObjectId++;
            Vector3 shift = Vector3.Zero;
            if (atCursor)
            {
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var o in clipObjects) { mn = Vector3.Min(mn, o.Position); mx = Vector3.Max(mx, o.Position); }
                shift = Cursor3D - (mn + mx) * 0.5f;
            }
            Selected.Clear();
            foreach (var o in clipObjects)
            {
                var c = o.Clone(); c.Id = idMap[o.Id];
                c.ParentId = o.ParentId >= 0 && idMap.TryGetValue(o.ParentId, out var pid) ? pid : -1;
                foreach (var f in c.Mesh.Faces) f.MatId = matMap.TryGetValue(f.MatId, out var mid) ? mid : 0;
                // a game entity copied from the map is a model of its own here
                c.Gta.MapArchetype = null; c.InstanceKey = null;
                c.Position += shift;
                if (MapMode && string.IsNullOrEmpty(c.Folder)) c.Folder = MapBuildFolder;
                if (!string.IsNullOrEmpty(c.Folder) && !Scene.Folders.Contains(c.Folder)) Scene.Folders.Add(c.Folder);
                c.Name = Scene.UniqueName(c.Name);
                c.Touch();
                Scene.Objects.Add(c); pasted.Add(c);
                if (c.ParentId < 0) Selected.Add(c.Id);
            }
            Level = SubObject.Object;
            if (pasted.Count > 0) ActiveId = pasted[0].Id;
        });
        foreach (var c in pasted) MapAdopt(c);
        SetStatus($"Pasted {pasted.Count} object(s)" + (atCursor ? " around the 3D cursor" : " at their place"));
    }
}
