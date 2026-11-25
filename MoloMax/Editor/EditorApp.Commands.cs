using System.Numerics;
using System.Windows.Forms;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>Every edit goes through Do() so it lands on the undo stack.</summary>
public sealed partial class EditorApp
{
    void Do(string name, Action action)
    {
        Undo.Record(name, Scene);
        lastAdd = null;
        try { action(); }
        catch (Exception e) { SetStatus($"{name} failed: {e.Message}"); }
        Undo.EditDone(Scene); // big scenes: a copy the edit did not need is given back at once
        Dirty = true;
    }

    /// <summary>Edit the current Editable Poly; bumps its version so the viewport re-uploads.</summary>
    void DoMesh(string name, Action<SceneObject, PolyMesh, PolySelection> action)
    {
        var o = Current;
        if (o == null) { SetStatus("Select one object first."); return; }
        // tools that never open a closed solid: if one does on an odd shape, it is cancelled instead of leaving holes
        bool guard = KeepsSolidsClosed.Contains(name) && o.Mesh.Faces.Count <= 200000 && o.Mesh.IsClosedManifold(out _);
        bool broke = false;
        Do(name, () =>
        {
            var s = SubSel(o);
            action(o, o.Mesh, s);
            o.Mesh.Validate();
            s.Sanitise(o.Mesh);
            o.Touch();
            broke = guard && !o.Mesh.IsClosedManifold(out _);
        });
        if (broke && Undo.PopLast(Scene) is { } back)
        {
            Scene = back; AfterSceneSwap();
            SetStatus($"{name} cancelled: on this shape it would have left holes in the mesh. Try it on fewer edges / faces at once.");
        }
    }

    /// <summary>Tools that keep a closed solid closed (checked after they run, see <see cref="DoMesh"/>).</summary>
    static readonly HashSet<string> KeepsSolidsClosed = new() { "Extrude", "Inset", "Bevel", "Tessellate", "Connect", "Knife", "Loop Cut", "Chamfer" };

    public void DoUndo()
    {
        if (UndoSelection()) return; // the last thing done was a selection change: bring the previous selection back
        var name = Undo.UndoName;
        var s = Undo.Undo(Scene);
        if (s == null) { SetStatus("Nothing to undo"); return; }
        Scene = s; AfterSceneSwap(); selRedo.Clear(); ResetSelectionBaseline(); SetStatus("Undo " + name);
    }

    public void DoRedo()
    {
        if (RedoSelection()) return;
        var name = Undo.RedoName;
        var s = Undo.Redo(Scene);
        if (s == null) { SetStatus("Nothing to redo"); return; }
        Scene = s; AfterSceneSwap(); ResetSelectionBaseline(); SetStatus("Redo " + name);
    }

    void AfterSceneSwap()
    {
        // versions are unique, so GPU meshes and edge caches of unchanged objects stay valid (no 1M-vertex re-upload on undo)
        Selected.RemoveWhere(id => Scene.Find(id) == null);
        foreach (var id in subSel.Keys.ToList())
        {
            var o = Scene.Find(id);
            if (o == null) subSel.Remove(id); else subSel[id].Sanitise(o.Mesh);
        }
        SelectionChanged();
        Dirty = true;
        if (MapMode) MapAfterSceneSwap();
    }

    // ---------------------------------------------------------------- object level

    public SceneObject AddObject(string baseName, PolyMesh mesh, Vector3 pos, GtaObjectType type = GtaObjectType.Drawable, Quaternion? rot = null)
    {
        SceneObject created = null;
        Do("Create " + baseName, () =>
        {
            created = Scene.Add(baseName, mesh, pos, type);
            subSel.Remove(created.Id);
            ActiveId = created.Id;
            if (rot.HasValue) created.Rotation = rot.Value;
            Selected.Clear(); Selected.Add(created.Id);
            Level = SubObject.Object;
        });
        if (created != null) { MapAdopt(created); SetStatus($"Created {created.Name}"); }
        return created;
    }

    public void DeleteSelection()
    {
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            long stamp = Undo.LastStamp;
            switch (Level)
            {
                case SubObject.Vertex: DoMesh("Delete Vertices", (o, m, s) => { MeshOps.DeleteVertices(m, s.Verts); s.Clear(); }); break;
                case SubObject.Edge: case SubObject.Border: RemoveEdges(false); break;
                default: DoMesh("Delete Polygons", (o, m, s) => { MeshOps.DeleteFaces(m, s.Faces); s.Clear(); }); break;
            }
            if (cur.Mesh.Faces.Count == 0)
            {
                // nothing left: the object goes too, in the same undo step (one Ctrl+Z brings the faces and the object back)
                void Drop() { Scene.RemoveObjects(o => o == cur); Selected.Clear(); Level = SubObject.Object; }
                if (Undo.LastStamp != stamp) { Drop(); Dirty = true; }
                else Do("Delete empty object", Drop);
            }
            return;
        }
        if (Selected.Count == 0) return;
        if (MapMode) mapDeleted.UnionWith(Selected); // map entities: removed from their .ymap at the next save
        Do("Delete", () => { Scene.RemoveObjects(o => Selected.Contains(o.Id)); Selected.Clear(); });
    }

    /// <summary>Clone Options default (classic modeller): Shift+drag and Ctrl+V clone as a copy or as an instance.</summary>
    public bool CloneAsInstance;
    /// <summary>Source -> clone ids of the last clone (Shift+drag uses it to switch copy / instance and add more copies).</summary>
    readonly List<(int Src, int Dst)> lastCloneMap = new();
    bool lastCloneInstance;

    /// <summary>
    /// Copies or instances the selection. A selected Drawable node brings its whole hierarchy (meshes, collision group, lights);
    /// an instance shares the mesh with its source, so editing one edits all (GTA: one model placed several times).
    /// </summary>
    /// <summary>
    /// Edit mode, Shift + move: copies what is selected and the move carries the copy (the original stays).
    /// Polygons / elements: the faces are copied. Edges: a strip of new faces is pulled out of them. Vertices: the faces
    /// they fully hold are copied. Returns false when there is nothing to copy (the move then moves the selection).
    /// </summary>
    public bool SubShiftClone()
    {
        var o = Current;
        if (o == null || Level == SubObject.Object) return false;
        var s = SubSel(o);
        bool ok = false; string what = "";
        Do("Shift clone", () =>
        {
            var m = o.Mesh;
            switch (Level)
            {
                case SubObject.Polygon or SubObject.Element when s.Faces.Count > 0:
                    {
                        var nf = MeshOps.CloneFaces(m, s.Faces);
                        s.Faces = nf.ToHashSet(); ok = nf.Count > 0; what = $"{nf.Count} face(s) copied";
                        break;
                    }
                case SubObject.Edge or SubObject.Border when s.Edges.Count > 0:
                    {
                        var ne = MeshOps.CloneEdgesAsStrip(m, s.Edges);
                        s.Edges = ne; ok = ne.Count > 0; what = $"{ne.Count} edge(s) pulled out";
                        break;
                    }
                case SubObject.Vertex when s.Verts.Count > 0:
                    {
                        var inside = MeshOps.FacesInside(m, s.Verts);
                        if (inside.Count == 0) break;
                        var nf = MeshOps.CloneFaces(m, inside);
                        s.Verts = nf.SelectMany(fi => m.Faces[fi].V).ToHashSet(); ok = true; what = $"{nf.Count} face(s) copied";
                        break;
                    }
            }
            if (!ok) return;
            m.Validate();
            s.Sanitise(m);
            o.Touch();
        });
        if (!ok) { Undo.DropLast(); SetStatus(Level == SubObject.Vertex ? "Shift + move on vertices copies the faces they fully hold: none here, the vertices just move." : "Nothing selected to copy."); return false; }
        SetStatus("Shift clone: " + what + ", move the copy.");
        return true;
    }

    public void CloneSelection(bool? instance = null)
    {
        if (Selected.Count == 0 || Level != SubObject.Object) return;
        bool inst = instance ?? CloneAsInstance;
        var news = new List<int>();
        lastCloneMap.Clear(); lastCloneInstance = inst;
        Do(inst ? "Instance" : "Clone", () =>
        {
            var sel = SelObjects().ToList();
            var selSet = sel.Select(o => o.Id).ToHashSet();
            bool HasSelectedAncestor(SceneObject o) { var p = Scene.Parent(o); int g = 0; while (p != null && g++ < 64) { if (selSet.Contains(p.Id)) return true; p = Scene.Parent(p); } return false; }
            int activeIndex = -1;
            foreach (var r in sel.Where(o => !HasSelectedAncestor(o)))
            {
                var list = new List<SceneObject> { r };
                bool node = r.GtaType is GtaObjectType.Model or GtaObjectType.CollisionGroup;
                list.AddRange(Scene.Descendants(r).Where(d => node || selSet.Contains(d.Id)));
                var map = new Dictionary<int, int>();
                foreach (var o in list)
                {
                    SceneObject c;
                    if (inst) c = Scene.AddInstance(o);
                    else
                    {
                        c = o.Clone();
                        c.Id = Scene.NextObjectId++; c.Name = Scene.UniqueName(Scene.BaseName(o.Name));
                        c.InstanceKey = null; c.Paint = null; c.PaintFile = null; c.Touch();
                        Scene.Objects.Add(c);
                    }
                    c.ParentId = o == r ? o.ParentId : map.TryGetValue(o.ParentId, out var np) ? np : o.ParentId;
                    map[o.Id] = c.Id;
                    lastCloneMap.Add((o.Id, c.Id));
                }
                if (r.Id == ActiveId) activeIndex = news.Count;
                news.Add(map[r.Id]);
            }
            Selected.Clear(); foreach (var n in news) Selected.Add(n);
            ActiveId = activeIndex >= 0 ? news[activeIndex] : news.FirstOrDefault(-1);
        });
        Scene.SyncInstances();
        SelectionChanged();
        if (Tool == Tool.Select) Tool = Tool.Move;
        SetStatus(inst ? $"{news.Count} instance(s): they share the mesh with the original. Move them with the gizmo." : "Cloned. Move the copy with the gizmo.");
    }

    /// <summary>Shift+drag clone confirmed: switch copy / instance if asked, then the extra copies along the same move.</summary>
    void FinishShiftClone()
    {
        bool wantInst = shiftCloneMode == 1;
        CloneAsInstance = wantInst;
        var map = lastCloneMap.ToList();
        if (wantInst != lastCloneInstance)
            Do(wantInst ? "Clones to instances" : "Clones to copies", () =>
            {
                foreach (var (src, dst) in map)
                {
                    var s = Scene.Find(src); var d = Scene.Find(dst); if (s == null || d == null) continue;
                    if (wantInst) { s.InstanceKey ??= Scene.NewInstanceKey(); d.InstanceKey = s.InstanceKey; d.Mesh = s.Mesh; d.Modifiers = s.Modifiers; d.Paint = s.Paint; d.Touch(); }
                    else Scene.MakeUnique(d);
                }
            });
        for (int k = 2; k <= shiftCloneCount; k++)
        {
            CloneSelection(wantInst);
            var roots = SelObjects().ToList();
            foreach (var r in roots) { r.Position += shiftCloneDelta; foreach (var d in Scene.Descendants(r)) d.Position += shiftCloneDelta; }
        }
        SetStatus($"{shiftCloneCount} {(wantInst ? "instance(s)" : "copy(ies)")} made.");
    }

    /// <summary>Shift+drag clone cancelled: the clones go away, the originals are selected again.</summary>
    void CancelShiftClone()
    {
        var map = lastCloneMap.ToList();
        var dst = map.Select(m => m.Dst).ToHashSet();
        Do("Cancel clone", () => Scene.RemoveObjects(o => dst.Contains(o.Id)));
        var srcs = map.Select(m => m.Src).ToHashSet();
        Selected.Clear();
        foreach (var id in srcs) if (Scene.Find(id) is { } so && !srcs.Contains(so.ParentId)) Selected.Add(id);
        SelectionChanged();
        SetStatus("Clone cancelled.");
    }

    /// <summary>Selects every object that shares the active object's mesh.</summary>
    public void SelectInstances(SceneObject o = null)
    {
        o ??= Current; if (o == null) return;
        Selected.Clear(); foreach (var x in Scene.InstancesOf(o)) Selected.Add(x.Id); ActiveId = o.Id;
        SelectionChanged();
        SetStatus($"{Selected.Count} instance(s) selected.");
    }

    /// <summary>3ds Max "Make Unique": the selected instances get their own mesh.</summary>
    public void MakeUniqueSelection()
    {
        var objs = SelObjects().Where(o => o.InstanceKey != null).ToList();
        if (objs.Count == 0) { SetStatus("Nothing to make unique: the selection has no instances."); return; }
        Do("Make Unique", () => { foreach (var o in objs) Scene.MakeUnique(o); });
        SetStatus($"{objs.Count} object(s) now have their own mesh.");
    }

    /// <summary>Changes that bake one object's own transform into the mesh can't be shared: those instances become unique first.</summary>
    int UniqueBeforeBake(IEnumerable<SceneObject> objs)
    {
        int n = 0;
        foreach (var o in objs) if (o.InstanceKey != null && Scene.InstanceCount(o) > 1) { Scene.MakeUnique(o); n++; }
        return n;
    }

    public void SelectAll()
    {
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            var s = SubSel(cur);
            switch (Level)
            {
                case SubObject.Vertex: s.Verts = new HashSet<int>(Enumerable.Range(0, cur.Mesh.Verts.Count)); break;
                case SubObject.Edge: s.Edges = Edges(cur).ToHashSet(); break;
                case SubObject.Border: s.Edges = MeshOps.BorderEdges(cur.Mesh); break;
                default: s.Faces = new HashSet<int>(Enumerable.Range(0, cur.Mesh.Faces.Count)); break;
            }
            return;
        }
        Selected.Clear(); foreach (var o in Scene.Objects) if (!o.Hidden && !o.Frozen) Selected.Add(o.Id);
        SelectionChanged();
    }

    public void SelectNone()
    {
        var cur = Current;
        if (Level != SubObject.Object && cur != null) { SubSel(cur).Clear(); return; }
        Selected.Clear(); SelectionChanged();
    }

    public void SelectInvert()
    {
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            var s = SubSel(cur);
            switch (Level)
            {
                case SubObject.Vertex: s.Verts = Enumerable.Range(0, cur.Mesh.Verts.Count).Where(i => !s.Verts.Contains(i)).ToHashSet(); break;
                case SubObject.Edge: case SubObject.Border: s.Edges = Edges(cur).Where(e => !s.Edges.Contains(e)).ToHashSet(); break;
                default: s.Faces = Enumerable.Range(0, cur.Mesh.Faces.Count).Where(i => !s.Faces.Contains(i)).ToHashSet(); break;
            }
            return;
        }
        var all = Scene.Objects.Where(o => !o.Hidden && !o.Frozen).Select(o => o.Id).ToHashSet();
        all.ExceptWith(Selected); Selected.Clear(); Selected.UnionWith(all); SelectionChanged();
    }

    bool isolated;
    Dictionary<int, bool> isolateHidden;
    /// <summary>Isolate: only the selection (with its children and parents) stays visible; End Isolate puts every eye back as it was.</summary>
    public void IsolateSelection(bool forceOn = false)
    {
        if (isolated && !forceOn) { EndIsolate(); return; }
        if (Selected.Count == 0) return;
        if (!isolated) isolateHidden = Scene.Objects.ToDictionary(o => o.Id, o => o.Hidden);
        var keep = new HashSet<int>();
        foreach (var o in SelObjects())
        {
            keep.Add(o.Id);
            foreach (var d in Scene.Descendants(o)) keep.Add(d.Id);
            for (var p = Scene.Parent(o); p != null && keep.Add(p.Id); p = Scene.Parent(p)) { }
        }
        foreach (var o in Scene.Objects) o.Hidden = !keep.Contains(o.Id) || (isolateHidden.TryGetValue(o.Id, out var h) && h && !Selected.Contains(o.Id));
        isolated = true; FrameSelection();
        SetStatus($"Isolated {Selected.Count} object(s). Right-click > End Isolate (or {K("/", "Shift+I", "Alt+Q")}) to see everything again.");
    }

    public void EndIsolate()
    {
        if (!isolated) return;
        foreach (var o in Scene.Objects) o.Hidden = isolateHidden != null && isolateHidden.TryGetValue(o.Id, out var h) && h;
        isolated = false; isolateHidden = null;
        SetStatus("Isolation ended.");
    }

    public void HideSelection()
    {
        // a Drawable node hides with its meshes (it has no geometry of its own: hiding the node alone showed no change)
        foreach (var id in Selected) { var o = Scene.Find(id); if (o == null) continue; o.Hidden = true; foreach (var d in Scene.Descendants(o)) d.Hidden = true; }
        Selected.Clear(); Level = SubObject.Object; SelectionChanged();
    }
    public void UnhideAll()
    {
        foreach (var o in Scene.Objects) o.Hidden = false;
        isolated = false; isolateHidden = null; // everything shown: an isolation in progress is over too
    }

    /// <summary>Hide / show entries of the right-click menus (viewport, quad menu, Scene Explorer).</summary>
    void HideMenuItems()
    {
        bool any = Selected.Count > 0, hiddenSome = Scene.Objects.Any(o => o.Hidden);
        if (ImGuiNET.ImGui.MenuItem("Isolate Selection", K("/", "Shift+I", "Alt+Q"), false, any)) IsolateSelection(forceOn: true);
        if (isolated && ImGuiNET.ImGui.MenuItem("End Isolate", K("/", "Shift+I", "Alt+Q"))) EndIsolate();
        // Hide Selection / Hide Unselected left the menus (the eye and Isolate do it; their keys still work)
        if (ImGuiNET.ImGui.MenuItem("Unhide All", K("Alt+H", "Alt+H", ""), false, hiddenSome)) UnhideAll();
    }

    public void SetLevel(SubObject level)
    {
        if (MapMode && level != SubObject.Object && Level == SubObject.Object && !MapCanEdit()) return;
        if (level != SubObject.Object && Current == null)
        {
            SetStatus("Select a single object to edit its vertices, edges or polygons.");
            return;
        }
        var cur = Current;
        if (cur != null && level != SubObject.Object && Level != SubObject.Object && Level != level && ImGuiNET.ImGui.GetIO().KeyCtrl)
            SubSel(cur).Convert(cur.Mesh, Level, level); // Ctrl+level converts the selection, like Max
        bool wasObject = Level == SubObject.Object;
        Level = Level == level && level != SubObject.Object ? SubObject.Object : level;
        if (ModalActive) CancelModal();
        if (wasObject && Level != SubObject.Object && cur != null && Scene.InstanceCount(cur) is int n && n > 1)
            SetStatus($"Editing an instance: the {n} objects that share this mesh change together (Make Unique to edit only this one).");
    }

    public void ResetXForm()
    {
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        int u = 0;
        Do("Reset XForm", () => { u = UniqueBeforeBake(objs); foreach (var o in objs) o.ResetXForm(); });
        if (u > 0) SetStatus($"Reset XForm: {u} instance(s) made unique first (their rotation / scale is their own).");
    }
    // pivot changes on an instance move the shared mesh once and every instance keeps its place
    public void CenterPivot() => PivotOp("Center Pivot", m => { var (mn, mx) = m.Bounds(); return (mn + mx) * 0.5f; });
    public void PivotToBase() => PivotOp("Pivot to Base", m => { var (mn, mx) = m.Bounds(); return new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, mn.Z); });
    void PivotOp(string name, Func<PolyMesh, Vector3> where)
    {
        var objs = SelObjects().Where(o => o.Mesh.Verts.Count > 0).ToList(); if (objs.Count == 0) return;
        Do(name, () =>
        {
            var done = new HashSet<PolyMesh>(ReferenceEqualityComparer.Instance);
            foreach (var o in objs) if (done.Add(o.Mesh)) Scene.MovePivotLocal(o, where(o.Mesh));
        });
    }

    public void MirrorObjects(int axis)
    {
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        Do("Mirror", () =>
        {
            foreach (var o in objs)
            {
                var s = Vector3.One; if (axis == 0) s.X = -1; else if (axis == 1) s.Y = -1; else s.Z = -1;
                o.Mesh.Transform(Matrix4x4.CreateScale(s));
                o.Touch();
            }
        });
    }

    public void SetGtaType(SceneObject o, GtaObjectType t)
    {
        if (o.GtaType == t) return;
        Do("GTA type", () =>
        {
            // rooms must face inward, everything else outward: flip when crossing that line
            bool wasRoom = o.GtaType == GtaObjectType.MloRoom, isRoom = t == GtaObjectType.MloRoom;
            if (wasRoom != isRoom) MeshOps.FlipAll(o.Mesh);
            o.GtaType = t; o.Touch();
        });
    }

    IEnumerable<SceneObject> SelObjects() => Selected.Select(Scene.Find).Where(o => o != null);

    public void AttachObject(SceneObject target, SceneObject other)
    {
        if (target == null || other == null || target == other) return;
        Do("Attach", () =>
        {
            Matrix4x4.Invert(target.World, out var inv);
            var copy = other.Mesh.Clone();
            copy.Transform(other.World * inv);
            target.Mesh.Append(copy);
            Scene.RemoveObjects(x => x == other);
            Selected.Remove(other.Id);
            target.Touch();
        });
        SetStatus($"Attached {other.Name} to {target.Name}");
    }

    /// <summary>The faces Separate takes: the selected faces, or in vertex / edge mode the faces whose points are all selected.</summary>
    HashSet<int> DetachSet(SceneObject o)
    {
        var s = SubSel(o); var m = o.Mesh;
        HashSet<int> verts;
        switch (Level)
        {
            case SubObject.Vertex: verts = s.Verts; break;
            case SubObject.Edge: case SubObject.Border: verts = new HashSet<int>(s.Edges.SelectMany(e => new[] { e.A, e.B })); break;
            default: return new HashSet<int>(s.Faces.Where(f => f < m.Faces.Count));
        }
        var res = new HashSet<int>();
        if (verts.Count == 0) return res;
        for (int i = 0; i < m.Faces.Count; i++) if (m.Faces[i].V.All(verts.Contains)) res.Add(i);
        return res;
    }

    public void DetachFaces(bool asClone)
    {
        var o = Current; if (o == null) return;
        var detach = DetachSet(o);
        if (detach.Count == 0) { SetStatus(Level == SubObject.Polygon || Level == SubObject.Element ? "Select polygons to detach." : "Separate takes the faces whose points are all selected: none here."); return; }
        Do("Detach", () =>
        {
            var faces = detach;
            PolyMesh part;
            if (asClone)
            {
                var tmp = o.Mesh.Clone();
                part = MeshOps.Detach(tmp, faces);
            }
            else part = MeshOps.Detach(o.Mesh, faces);
            var n = Scene.Add(o.Name + "_detached", part, o.Position, o.GtaType);
            n.Rotation = o.Rotation; n.Scale = o.Scale;
            SubSel(o).Clear(); o.Touch();
        });
    }

    // ---------------------------------------------------------------- Editable Poly

    public void ExtrudeFaces(float h, bool byPoly) => DoMesh("Extrude", (o, m, s) => MeshOps.ExtrudeFaces(m, s.Faces, h, byPoly));
    public void InsetFaces(float a, bool byPoly) => DoMesh("Inset", (o, m, s) => MeshOps.InsetFaces(m, s.Faces, a, byPoly));
    public void BevelFaces(float h, float outline, bool byPoly) => DoMesh("Bevel", (o, m, s) => MeshOps.BevelFaces(m, s.Faces, h, outline, byPoly));
    public void FlipFaces() => DoMesh("Flip", (o, m, s) => { if (s.Faces.Count == 0) MeshOps.FlipAll(m); else MeshOps.FlipFaces(m, s.Faces); });
    public void Tessellate() => DoMesh("Tessellate", (o, m, s) => { var nf = MeshOps.Tessellate(m, s.Faces.Count > 0 ? s.Faces : null); if (s.Faces.Count > 0) s.Faces = nf; });

    public void Cap() => DoMesh("Cap", (o, m, s) =>
    {
        var touching = Level == SubObject.Border || Level == SubObject.Edge ? s.VerticesOf(m, Level) : null;
        var caps = MeshOps.CapBorders(m, touching != null && touching.Count > 0 ? touching : null);
        SetStatus(caps.Count > 0 ? $"Capped {caps.Count} hole(s)" : "No open border to cap");
    });

    public void Weld(float threshold) => DoMesh("Weld", (o, m, s) =>
    {
        int n = MeshOps.WeldByDistance(m, threshold, s.Verts.Count > 0 ? s.Verts : null);
        s.Clear(); SetStatus($"Welded {n} vertices");
    });

    public void Collapse() => DoMesh("Collapse", (o, m, s) => { MeshOps.Collapse(m, s.VerticesOf(m, Level)); s.Clear(); });

    public void ConnectEdges()
    {
        var o = Current; if (o == null) return;
        var baseMesh = o.Mesh.Clone(); var baseSel = SubSel(o).Clone();
        DoMesh("Connect", (o, m, s) =>
        {
            var e = Level == SubObject.Polygon ? new HashSet<EdgeKey>() : s.Edges;
            var added = MeshOps.ConnectEdges(m, e, connectSegments);
            s.Edges = added; SetStatus($"Connected {added.Count} edge(s) - Segments in the Adjust Last Operation panel (F9)");
        });
        // segments can be changed afterwards in the Adjust Last Operation panel
        if (Level != SubObject.Polygon && baseSel.Edges.Count > 0)
            lastMeshOp = new LastMeshOp { Kind = ModalKind.Connect, Obj = o, BaseMesh = baseMesh, BaseSel = baseSel, Segments = connectSegments, Version = o.Version };
    }

    public void RemoveEdges(bool withVerts) => DoMesh("Remove Edges", (o, m, s) => { MeshOps.RemoveEdges(m, s.Edges, withVerts); s.Edges.Clear(); });

    public void Loop() { var o = Current; if (o == null) return; var s = SubSel(o); s.Edges = s.Edges.SelectMany(e => MeshOps.EdgeLoop(o.Mesh, e)).ToHashSet(); }
    public void Ring() { var o = Current; if (o == null) return; var s = SubSel(o); s.Edges = s.Edges.SelectMany(e => MeshOps.EdgeRing(o.Mesh, e)).ToHashSet(); }

    /// <summary>What each Grow replaced, so Shrink right after gives back exactly the selection before (Up then Down = no change).</summary>
    readonly List<(int Obj, int Version, SubObject Level, PolySelection Before, PolySelection After)> growSteps = new();

    static bool SameSel(PolySelection a, PolySelection b) => a.Faces.SetEquals(b.Faces) && a.Edges.SetEquals(b.Edges) && a.Verts.SetEquals(b.Verts);

    public void Grow()
    {
        var o = Current; if (o == null) return; var s = SubSel(o);
        var before = s.Clone();
        switch (Level)
        {
            case SubObject.Vertex: s.Verts = MeshSelect.GrowVerts(o.Mesh, s.Verts); break;
            case SubObject.Edge: case SubObject.Border: s.Edges = MeshSelect.GrowEdges(o.Mesh, s.Edges); break;
            default: s.Faces = MeshSelect.GrowFaces(o.Mesh, s.Faces); break;
        }
        if (growSteps.Count > 0 && (growSteps[^1].Obj != o.Id || growSteps[^1].Version != o.Version || growSteps[^1].Level != Level || !SameSel(growSteps[^1].After, before))) growSteps.Clear();
        growSteps.Add((o.Id, o.Version, Level, before, s.Clone()));
    }

    public void Shrink()
    {
        var o = Current; if (o == null) return; var s = SubSel(o);
        // right after Grow: back to the selection it started from
        if (growSteps.Count > 0 && growSteps[^1].Obj == o.Id && growSteps[^1].Version == o.Version && growSteps[^1].Level == Level && SameSel(growSteps[^1].After, s))
        {
            var b = growSteps[^1].Before; growSteps.RemoveAt(growSteps.Count - 1);
            s.Faces = new HashSet<int>(b.Faces); s.Edges = new HashSet<EdgeKey>(b.Edges); s.Verts = new HashSet<int>(b.Verts);
            return;
        }
        growSteps.Clear();
        switch (Level)
        {
            case SubObject.Vertex: s.Verts = MeshSelect.ShrinkVerts(o.Mesh, s.Verts); break;
            case SubObject.Edge: case SubObject.Border: s.Edges = MeshSelect.ShrinkEdges(o.Mesh, s.Edges); break;
            default: s.Faces = MeshSelect.ShrinkFaces(o.Mesh, s.Faces); break;
        }
    }

    // ---------------------------------------------------------------- material / smoothing / colour / UV

    /// <summary>Assigns a material to the selected polygons, or to whole selected objects at object level.</summary>
    public void AssignMaterial(int matId)
    {
        var cur = Current;
        if (Level is SubObject.Polygon or SubObject.Element && cur != null)
        {
            DoMesh("Set Material", (o, m, s) => MeshOps.SetMaterial(m, s.Faces.Count > 0 ? s.Faces : Enumerable.Range(0, m.Faces.Count), matId));
            return;
        }
        var objs = SelObjects().ToList();
        if (objs.Count == 0) { SetStatus("Select objects or polygons to assign the material."); return; }
        Do("Assign Material", () => { foreach (var o in objs) { o.Mesh.SetAllMaterial(matId); o.Touch(); } });
        SetStatus($"Assigned {Scene.Material(matId).Name} to {objs.Count} object(s)");
    }

    /// <summary>
    /// A material dropped on the viewport (dragged from the Material Editor list): object mode, the object under the mouse
    /// takes it on every face; polygon mode, the face under the mouse takes it (the whole selected group when that face
    /// is part of the selection). Returns false when nothing is under the mouse.
    /// </summary>
    public bool DropMaterial(Viewport vp, Vector2 screen, int matId)
    {
        if (vp == null || Scene.Materials.All(x => x.Id != matId)) return false;
        var ray = RayAt(vp, screen);
        var cur = Current;
        string matName = Scene.Material(matId).Name;
        if (Level is SubObject.Polygon or SubObject.Element && cur != null && Picking.PickFace(cur, ray) is { } fh && fh.Face >= 0)
        {
            var s = SubSel(cur);
            var faces = s.Faces.Contains(fh.Face) ? new HashSet<int>(s.Faces) : new HashSet<int> { fh.Face };
            Do("Drop Material", () => { MeshOps.SetMaterial(cur.Mesh, faces, matId); cur.Touch(); });
            SetStatus($"{matName} on {faces.Count} face(s) of {cur.Name}");
            return true;
        }
        var hit = Picking.PickNearest(Scene.Objects, ray, o => o.Hidden || o.Frozen || (MapMode && entOf.ContainsKey(o.Id)));
        if (hit is not { } h || h.Object == null) { SetStatus("Drop the material on an object (or on a face in Polygon mode)."); return false; }
        var obj = h.Object;
        Do("Drop Material", () => { obj.Mesh.SetAllMaterial(matId); obj.Touch(); });
        SetStatus($"{matName} on {obj.Name}");
        return true;
    }

    public void SelectByMaterial(int matId)
    {
        var o = Current; if (o == null) return;
        Level = SubObject.Polygon;
        SubSel(o).Faces = MeshSelect.FacesByMaterial(o.Mesh, matId);
    }

    IEnumerable<int> FacesInScope(SceneObject o) =>
        Level is SubObject.Polygon or SubObject.Element && SubSel(o).Faces.Count > 0 ? SubSel(o).Faces : Enumerable.Range(0, o.Mesh.Faces.Count);

    public void ToggleSmoothing(int bit)
    {
        var o = Current; if (o == null) return;
        var faces = FacesInScope(o).ToList();
        bool allOn = faces.All(f => (o.Mesh.Faces[f].Smooth & (1u << bit)) != 0);
        DoMesh("Smoothing Group", (oo, m, s) => MeshOps.SetSmoothing(m, faces, 1u << bit, !allOn));
    }

    public void AutoSmooth(float angle) { var o = Current; if (o == null) return; var f = FacesInScope(o).ToHashSet(); DoMesh("Auto Smooth", (oo, m, s) => MeshOps.AutoSmooth(m, f, angle)); }
    public void ClearSmoothing() { var o = Current; if (o == null) return; var f = FacesInScope(o).ToList(); DoMesh("Clear Smoothing", (oo, m, s) => MeshOps.ClearSmoothing(m, f)); }

    /// <summary>GTA vertex colour: green (not emissive), white (emissive), red (exterior props), or any colour.</summary>
    public void PaintVertexColour(Vector4 c)
    {
        var cur = Current;
        if (cur != null && Level != SubObject.Object)
        {
            DoMesh("Vertex Paint", (o, m, s) =>
            {
                if (Level == SubObject.Vertex) MeshOps.SetVertexColour(m, s.Verts, c);
                else if (Level is SubObject.Edge or SubObject.Border) MeshOps.SetVertexColour(m, s.VerticesOf(m, Level), c);
                else MeshOps.SetFaceColour(m, s.Faces, c);
            });
            return;
        }
        var objs = SelObjects().ToList();
        if (objs.Count == 0) { SetStatus("Select something to paint."); return; }
        Do("Vertex Paint", () => { foreach (var o in objs) { o.Mesh.SetAllColour(c); o.Touch(); } });
    }

    public void BoxMap(float tile, bool worldSpace)
    {
        var cur = Current;
        if (cur != null && Level is SubObject.Polygon or SubObject.Element && SubSel(cur).Faces.Count > 0)
        {
            DoMesh("UVW Box Map", (o, m, s) => MeshOps.BoxMap(m, s.Faces, tile, worldSpace ? o.World : null));
            return;
        }
        var objs = SelObjects().ToList();
        Do("UVW Box Map", () => { foreach (var o in objs) { MeshOps.BoxMap(o.Mesh, null, tile, worldSpace ? o.World : null); o.Touch(); } });
    }
}

/// <summary>Commands added for the Blender-style workflow.</summary>
public sealed partial class EditorApp
{
    public void HideUnselected()
    {
        // what is selected stays, with its meshes (a selected Drawable) and the nodes above it (a selected mesh inside a Drawable)
        var keep = new HashSet<int>();
        foreach (var o in SelObjects())
        {
            keep.Add(o.Id);
            foreach (var d in Scene.Descendants(o)) keep.Add(d.Id);
            for (var p = Scene.Parent(o); p != null && keep.Add(p.Id); p = Scene.Parent(p)) { }
        }
        foreach (var o in Scene.Objects) if (!keep.Contains(o.Id)) o.Hidden = true;
    }

    /// <summary>Ctrl+J: attach every selected object into the active one. Works on game meshes only: a Drawable node in the
    /// selection brings its meshes (the node itself has no mesh and never receives one: the active node gives its first mesh,
    /// or a new "_high_0" mesh under it). Hidden objects, collisions and lights are left out. A Drawable whose meshes were all
    /// joined away is removed.</summary>
    public void JoinSelected()
    {
        static bool Joinable(SceneObject o) => o.Mesh != null && o.GtaType is not (GtaObjectType.Model or GtaObjectType.Collision or GtaObjectType.CollisionGroup or GtaObjectType.Light);
        IEnumerable<SceneObject> MeshesOf(SceneObject o) => o.GtaType == GtaObjectType.Model ? Scene.Descendants(o).Where(d => Joinable(d) && !d.Hidden) : Joinable(o) && !o.Hidden ? new[] { o } : Array.Empty<SceneObject>();
        var active = Current ?? SelObjects().FirstOrDefault();
        if (active == null) { SetStatus("Select two or more objects to join (the active one receives them)."); return; }
        var pieces = new List<SceneObject>();
        foreach (var o in SelObjects()) foreach (var m in MeshesOf(o)) if (!pieces.Contains(m)) pieces.Add(m);
        int skippedHidden = SelObjects().Count(o => o.Hidden && Joinable(o));
        SceneObject target = Joinable(active) && !active.Hidden ? active : active.GtaType == GtaObjectType.Model ? Scene.Descendants(active).FirstOrDefault(d => Joinable(d) && !d.Hidden) : null;
        bool newTarget = target == null && active.GtaType == GtaObjectType.Model;
        if (target == null && !newTarget) target = pieces.FirstOrDefault();
        var others = pieces.Where(o => o != target).ToList();
        if ((target == null && !newTarget) || others.Count == 0 || newTarget && others.Count < 1)
        { SetStatus("Select two or more visible meshes to join (the active one receives them)."); return; }
        var touchedNodes = new HashSet<int>();
        Do("Join", () =>
        {
            if (newTarget)
            {
                target = Scene.Add(active.Name + "_high_0", new PolyMesh(), Vector3.Zero, GtaObjectType.Drawable);
                Scene.SetParent(target, active); target.Folder = active.Folder;
                target.Position = Vector3.Zero; target.Rotation = Quaternion.Identity; target.Scale = Vector3.One;
            }
            Matrix4x4.Invert(target.World, out var inv);
            foreach (var other in others)
            {
                var copy = other.Mesh.Clone();
                copy.Transform(other.World * inv);
                target.Mesh.Append(copy);
                if (Scene.Parent(other) is { GtaType: GtaObjectType.Model } pn) touchedNodes.Add(pn.Id);
                foreach (var k in Scene.Objects.Where(k => Scene.Parent(k) == other).ToList()) Scene.SetParent(k, Scene.Parent(other)); // its children stay
                Scene.RemoveObjects(x => x == other);
                Selected.Remove(other.Id);
            }
            // Drawable nodes left with nothing in them go too (not the one that received the join)
            var targetNode = Scene.Parent(target);
            foreach (var id in touchedNodes)
                if (Scene.Find(id) is { } node && node != targetNode && !Scene.Descendants(node).Any())
                { Scene.RemoveObjects(x => x == node); Selected.Remove(node.Id); }
            target.Touch();
            Selected.Clear(); Selected.Add(target.Id); ActiveId = target.Id;
        });
        SelectionChanged();
        SetStatus($"Joined {others.Count} mesh(es) into {target.Name}" + (skippedHidden > 0 ? $" ({skippedHidden} hidden left out)" : ""));
    }

    public void RecalcNormals(bool inside)
    {
        var o = Current; if (o == null) return;
        var faces = Level is SubObject.Polygon or SubObject.Element && SubSel(o).Faces.Count > 0 ? new HashSet<int>(SubSel(o).Faces) : null;
        DoMesh(inside ? "Recalculate Inside" : "Recalculate Outside", (oo, m, s) =>
        {
            int n = MeshOps.RecalcNormals(m, faces, inside);
            SetStatus($"Normals recalculated ({n} face(s) flipped)");
        });
    }

    /// <summary>F: a face from the selected vertices, or caps the selected border.</summary>
    public void MakeFace()
    {
        var o = Current; if (o == null || Level == SubObject.Object) return;
        DoMesh("Make Face", (oo, m, s) =>
        {
            var verts = s.VerticesOf(m, Level);
            if (Level is SubObject.Edge or SubObject.Border && verts.Count > 3 && s.Edges.All(e => MeshOps.BorderEdges(m).Contains(e)))
            {
                var caps = MeshOps.CapBorders(m, verts);
                if (caps.Count > 0) { SetStatus($"Filled {caps.Count} hole(s)"); return; }
            }
            int f = MeshOps.MakeFace(m, verts);
            if (f < 0) { SetStatus("Select 3 or more vertices that are not in a line."); return; }
            if (Level is SubObject.Polygon or SubObject.Element) s.Faces = new HashSet<int> { f };
            SetStatus("Face created");
        });
    }

    /// <summary>Alt+G / Alt+R / Alt+S: clear location, rotation or scale.</summary>
    public void ClearTransform(int what)
    {
        if (Level != SubObject.Object) return;
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        Do(what == 0 ? "Clear Location" : what == 1 ? "Clear Rotation" : "Clear Scale", () =>
        {
            foreach (var o in objs)
            {
                if (what == 0) o.Position = Vector3.Zero;
                else if (what == 1) o.Rotation = Quaternion.Identity;
                else o.Scale = Vector3.One;
            }
        });
    }

    /// <summary>Shift+D: duplicate, then move the copy with the mouse.</summary>
    public void DuplicateAndGrab(bool linked = false)
    {
        if (Level != SubObject.Object) { SetStatus("Duplicate works in object mode (Tab)."); return; }
        if (Selected.Count == 0) return;
        CloneSelection(linked);
        Tool = Tool.Select;
        BeginModal(ModalKind.Translate);
    }

    public void ApplyAllTransforms()
    {
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        Do("Apply All Transforms", () =>
        {
            UniqueBeforeBake(objs);
            foreach (var o in objs) { o.Mesh.Transform(o.World); o.Position = Vector3.Zero; o.Rotation = Quaternion.Identity; o.Scale = Vector3.One; o.Touch(); }
        });
    }

    public void OriginToCursor()
    {
        var objs = SelObjects().ToList(); if (objs.Count == 0) return;
        Do("Origin to 3D Cursor", () =>
        {
            var done = new HashSet<PolyMesh>(ReferenceEqualityComparer.Instance);
            foreach (var o in objs)
            {
                if (!done.Add(o.Mesh)) continue;
                Matrix4x4.Invert(o.World, out var inv);
                Scene.MovePivotLocal(o, Vector3.Transform(Cursor3D, inv));
            }
        });
    }
}
