using MoloMax.Core.Mesh;
namespace MoloMax.Core.Scenes;

/// <summary>
/// Snapshot undo: before an edit the scene is copied. Meshes that did not change since the last snapshot
/// (same object version) are shared between snapshots instead of cloned again, so a 1M-vertex scene
/// costs one copy, not one per edit. A restored snapshot is deep-copied before it becomes the live scene.
/// </summary>
public sealed class UndoStack
{
    readonly LinkedList<(string Name, Scene State)> undo = new();
    /// <summary>When each undo step was recorded (a shared clock: other undo lists, the paint strokes, compare with it).</summary>
    readonly LinkedList<long> undoStamp = new();
    long clock;
    /// <summary>A new time stamp on the same clock as the recorded steps.</summary>
    public long Tick() => ++clock;
    /// <summary>Stamp of the step Ctrl+Z would undo now (0: none).</summary>
    public long LastStamp => undoStamp.Last?.Value ?? 0;
    readonly Stack<(string Name, Scene State)> redo = new();
    public int Capacity = 60;
    /// <summary>
    /// Meshes are never edited in place (map mode: entities only move, a model load swaps the mesh reference):
    /// snapshots share them as they are, nothing is copied, an undo of a big map is instant.
    /// </summary>
    public bool ImmutableMeshes;
    /// <summary>With <see cref="ImmutableMeshes"/>: the objects whose mesh is edited in place all the same (map mode: what you
    /// model right on the map). Their meshes are copied like in a normal scene; every other mesh stays shared.</summary>
    public Func<SceneObject, bool> MutableMesh;
    bool Shared(SceneObject o) => ImmutableMeshes && (MutableMesh == null || !MutableMesh(o));

    // ---- big scenes: copy-on-write history. A full copy of a huge custom map (millions of faces) doubled the memory and
    // made the first Delete / Ctrl+Z take seconds. On such scenes the history shares every mesh with the live scene and
    // only the meshes about to be edited (the selection) get a fresh copy for the live scene before the edit: the stored
    // states keep the untouched originals. A step that did not change a copied mesh gives the original back (no copy kept).
    /// <summary>On (sticky until <see cref="Clear"/>) once the scene is big: see <see cref="BigFaces"/> / <see cref="BigObjects"/>.</summary>
    public bool CopyOnWrite { get; private set; }
    public long BigFaces = 2_500_000; public int BigObjects = 15000;
    /// <summary>The objects an edit may change in place (the editor: selection, object being edited, its children).</summary>
    public Func<Scene, IEnumerable<SceneObject>> Editable;
    /// <summary>Memory the history may keep (mesh copies + object copies); the oldest steps go past it.</summary>
    public long BudgetBytes = 1536L * 1024 * 1024;
    readonly LinkedList<long> undoBytes = new();
    readonly List<(int Id, PolyMesh Original, PolyMesh Copy, int Ver, long Sig, long Bytes)> pendingCopies = new();
    LinkedListNode<long> pendingStep;

    public static long MeshBytes(PolyMesh m) => m == null ? 0 : m.Faces.Count * 280L + m.Verts.Count * 16L + 200;
    static long Sig(PolyMesh m)
    {
        unchecked
        {
            long h = m.Verts.Count * 31L + m.Faces.Count;
            int step = Math.Max(1, m.Verts.Count / 4096);
            for (int i = 0; i < m.Verts.Count; i += step) { var v = m.Verts[i]; h = h * 1000003 ^ (v.X.GetHashCode() * 3 + v.Y.GetHashCode() * 7 + v.Z.GetHashCode()); }
            int fs = Math.Max(1, m.Faces.Count / 4096);
            for (int i = 0; i < m.Faces.Count; i += fs) { var f = m.Faces[i]; h = h * 1000003 ^ (f.V.Length * 17 + f.V[0] + f.MatId * 131); }
            return h;
        }
    }

    /// <summary>Big scene from now on: the full copies are dropped (a background copy still running is left to finish and forgotten).</summary>
    void GoCopyOnWrite() { prefreeze = null; frozen.Clear(); frozenByLiveT.Clear(); CopyOnWrite = true; }

    bool IsBig(Scene s)
    {
        if (s.Objects.Count >= BigObjects) return true;
        long f = 0; foreach (var o in s.Objects) { f += o.Mesh?.Faces.Count ?? 0; if (f >= BigFaces) return true; }
        return false;
    }

    /// <summary>Meshes copied for the last step that the edit left unchanged go back to their original (shared with the history).</summary>
    void ReleaseUnchanged(Scene live)
    {
        if (pendingCopies.Count == 0) return;
        var byId = live?.Objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        long freed = 0;
        foreach (var (id, orig, copy, ver, sig, bytes) in pendingCopies)
        {
            if (byId == null || !byId.TryGetValue(id, out var o) || !ReferenceEquals(o.Mesh, copy)) continue;
            if (o.Version != ver || copy.Verts.Count != orig.Verts.Count || copy.Faces.Count != orig.Faces.Count || Sig(copy) != sig) continue;
            foreach (var other in live.Objects) if (ReferenceEquals(other.Mesh, copy)) other.Mesh = orig; // instances too
            freed += bytes;
        }
        if (pendingStep != null && pendingStep.List != null) pendingStep.Value = Math.Max(0, pendingStep.Value - freed);
        pendingCopies.Clear(); pendingStep = null;
    }

    /// <summary>The live scene gets its own copy of each mesh the edit may change; the stored state keeps the original.</summary>
    long ProtectForEdit(Scene live, IEnumerable<SceneObject> objs)
    {
        long bytes = 0;
        var done = new HashSet<PolyMesh>(ReferenceEqualityComparer.Instance);
        foreach (var o in objs)
        {
            if (o?.Mesh == null || !done.Add(o.Mesh)) continue;
            var orig = o.Mesh; var copy = orig.Clone(); long b = MeshBytes(orig);
            if (o.InstanceKey != null) { foreach (var other in live.Objects) if (ReferenceEquals(other.Mesh, orig)) other.Mesh = copy; }
            else o.Mesh = copy;
            pendingCopies.Add((o.Id, orig, copy, o.Version, Sig(orig), b));
            bytes += b;
        }
        return bytes;
    }

    void TrimToBudget()
    {
        long total = 0; foreach (var b in undoBytes) total += b;
        while (undo.Count > 1 && total > BudgetBytes) { total -= undoBytes.First.Value; undo.RemoveFirst(); undoStamp.RemoveFirst(); undoBytes.RemoveFirst(); }
    }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoName => undo.Last?.Value.Name;
    public string RedoName => redo.Count > 0 ? redo.Peek().Name : null;

    /// <summary>Call before changing the scene.</summary>
    (string Name, Scene State)[] redoBackup;

    readonly Dictionary<int, (int Ver, PolyMesh Live, PolyMesh Frozen)> frozen = new();
    /// <summary>The same copies by live mesh (thousands of instances of one model find theirs at once).</summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, Tuple3> frozenByLiveT = new();
    sealed class Tuple3 { public (int Ver, PolyMesh Live, PolyMesh Frozen) V; }
    sealed class ByLive
    {
        readonly UndoStack u; public ByLive(UndoStack u) => this.u = u;
        public bool TryGetValue(PolyMesh m, out (int Ver, PolyMesh Live, PolyMesh Frozen) v) { if (u.frozenByLiveT.TryGetValue(m, out var t)) { v = t.V; return true; } v = default; return false; }
        public (int Ver, PolyMesh Live, PolyMesh Frozen) this[PolyMesh m] { set { u.frozenByLiveT.AddOrUpdate(m, new Tuple3 { V = value }); } }
    }
    ByLive frozenByLiveW; ByLive frozenByLive => frozenByLiveW ??= new ByLive(this);

    bool Fresh(SceneObject o) => frozen.TryGetValue(o.Id, out var f) && f.Ver == o.Version && ReferenceEquals(f.Live, o.Mesh)
                                 && f.Frozen.Verts.Count == o.Mesh.Verts.Count && f.Frozen.Faces.Count == o.Mesh.Faces.Count;

    PolyMesh FrozenMesh(SceneObject o)
    {
        if (Shared(o)) return o.Mesh;
        if (Fresh(o)) return frozen[o.Id].Frozen;
        // instances share one mesh: one frozen copy serves them all
        if (o.InstanceKey != null && frozenByLive.TryGetValue(o.Mesh, out var f)
            && f.Ver == o.Version && f.Frozen.Verts.Count == o.Mesh.Verts.Count && f.Frozen.Faces.Count == o.Mesh.Faces.Count)
        { frozen[o.Id] = f; return f.Frozen; }
        var m = o.Mesh.Clone();
        frozen[o.Id] = (o.Version, o.Mesh, m); frozenByLive[o.Mesh] = (o.Version, o.Mesh, m);
        return m;
    }

    // ---- background pre-copy: after a big import the first edit would copy every new mesh (≈1 s for 1M vertices);
    // copies are made on worker threads while the user looks at the result, and merged on the UI thread.
    System.Threading.Tasks.Task<List<(int Id, int Ver, PolyMesh Live, PolyMesh Copy)>> prefreeze;

    /// <summary>Call from the UI thread when idle: starts copying the meshes the next snapshot would copy.</summary>
    public void Prefreeze(Scene s, int minFaces = 50000)
    {
        if (ImmutableMeshes || CopyOnWrite) return;
        if (IsBig(s)) { GoCopyOnWrite(); return; } // a huge scene is never copied whole
        if (prefreeze != null) { if (prefreeze.IsCompleted) Merge(); else return; }
        var stale = s.Objects.Where(o => !Fresh(o) && o.Mesh.Faces.Count > 0).Select(o => (o.Id, o.Version, o.Mesh)).ToList();
        if (stale.Sum(x => (long)x.Mesh.Faces.Count) < minFaces) return;
        prefreeze = System.Threading.Tasks.Task.Run(() =>
        {
            // instances share a mesh: each distinct mesh copied once
            var distinct = stale.Select(x => x.Mesh).Distinct(ReferenceEqualityComparer.Instance).Cast<PolyMesh>().ToList();
            var copies = new PolyMesh[distinct.Count];
            System.Threading.Tasks.Parallel.For(0, distinct.Count, i => { try { copies[i] = distinct[i].Clone(); } catch { copies[i] = null; } });
            var copyOf = new Dictionary<PolyMesh, PolyMesh>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < distinct.Count; i++) copyOf[distinct[i]] = copies[i];
            return stale.Select(x => (x.Id, x.Version, x.Mesh, copyOf[x.Mesh])).ToList();
        });
    }

    /// <summary>Waits for the background copies and keeps those whose object did not change meanwhile (version / mesh / size).</summary>
    void Merge()
    {
        if (prefreeze == null) return;
        List<(int Id, int Ver, PolyMesh Live, PolyMesh Copy)> res;
        try { res = prefreeze.Result; } catch { res = new(); }
        prefreeze = null;
        foreach (var (id, ver, live, copy) in res)
            if (copy != null && copy.Verts.Count == live.Verts.Count && copy.Faces.Count == live.Faces.Count) { frozen[id] = (ver, live, copy); frozenByLive[live] = (ver, live, copy); }
    }

    Scene Snapshot(Scene s)
    {
        if (ImmutableMeshes) return s.Clone(FrozenMesh);
        if (CopyOnWrite) return s.Clone(o => o.Mesh);
        Merge();
        // meshes changed since the last snapshot (e.g. a big import): copied in parallel first
        var stale = s.Objects.Where(o => !Fresh(o)).ToList();
        if (stale.Count > 1 && stale.Sum(o => (long)o.Mesh.Faces.Count) > 20000)
        {
            // instances share a mesh: copy each distinct mesh once
            var distinct = stale.Select(o => o.Mesh).Distinct(ReferenceEqualityComparer.Instance).Cast<PolyMesh>().ToList();
            var copies = new PolyMesh[distinct.Count];
            System.Threading.Tasks.Parallel.For(0, distinct.Count, i => copies[i] = distinct[i].Clone());
            var copyOf = new Dictionary<PolyMesh, PolyMesh>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < distinct.Count; i++) copyOf[distinct[i]] = copies[i];
            foreach (var o in stale) { frozen[o.Id] = (o.Version, o.Mesh, copyOf[o.Mesh]); frozenByLive[o.Mesh] = (o.Version, o.Mesh, copyOf[o.Mesh]); }
        }
        return s.Clone(FrozenMesh);
    }

    /// <summary>Immutable copy for background saving (shares unchanged meshes with the undo history, so it is cheap).</summary>
    public Scene SnapshotForSave(Scene s) => Snapshot(s);

    /// <summary>
    /// A stored snapshot becomes live. Stored meshes are shared and must stay intact, so each one is copied,
    /// except when the live scene still holds the very mesh that snapshot was frozen from, unchanged: that one is reused.
    /// </summary>
    Scene Live(Scene state, Scene current)
    {
        if (CopyOnWrite && !ImmutableMeshes) { var cw = state.Clone(o => o.Mesh); cw.SyncInstances(); return cw; } // shared: the next edit copies what it changes
        if (ImmutableMeshes)
        {
            var im = state.Clone(o => Shared(o) ? o.Mesh : o.Mesh.Clone());
            // what is edited here comes back as a new version: views, picking and modifier caches rebuild from the restored mesh
            foreach (var o in im.Objects) if (!Shared(o)) o.Touch();
            im.SyncInstances(); return im;
        }
        var cur = current?.Objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var reused = new HashSet<int>();
        var copies = new Dictionary<PolyMesh, PolyMesh>(ReferenceEqualityComparer.Instance);
        var s = state.Clone(o =>
        {
            if (cur != null && cur.TryGetValue(o.Id, out var c) && frozen.TryGetValue(o.Id, out var f)
                && ReferenceEquals(o.Mesh, f.Frozen) && ReferenceEquals(c.Mesh, f.Live) && c.Version == f.Ver) { reused.Add(o.Id); return c.Mesh; }
            // instances stored on one frozen mesh come back on one live copy
            if (o.InstanceKey == null) return null;
            if (!copies.TryGetValue(o.Mesh, out var copy)) copies[o.Mesh] = copy = o.Mesh.Clone();
            return copy;
        });
        foreach (var o in s.Objects) if (!reused.Contains(o.Id)) o.Touch();
        s.SyncInstances();
        return s;
    }

    /// <summary>Records the state before an edit. <paramref name="anyObject"/>: the edit may change any mesh (scripts).</summary>
    public void Record(string name, Scene current, bool anyObject = false)
    {
        ReleaseUnchanged(current);
        if (!ImmutableMeshes && !CopyOnWrite && IsBig(current)) GoCopyOnWrite();
        long bytes = 0;
        var snap = Snapshot(current);
        if (CopyOnWrite && !ImmutableMeshes)
        {
            bytes = current.Objects.Count * 1500L; // the object copies of this step
            bytes += ProtectForEdit(current, anyObject || Editable == null ? current.Objects.ToList() : Editable(current).ToList());
        }
        undo.AddLast((name, snap)); undoStamp.AddLast(Tick()); undoBytes.AddLast(bytes);
        pendingStep = undoBytes.Last;
        while (undo.Count > Capacity) { undo.RemoveFirst(); undoStamp.RemoveFirst(); undoBytes.RemoveFirst(); }
        if (CopyOnWrite) TrimToBudget();
        redoBackup = redo.Reverse().ToArray();
        redo.Clear();
    }

    /// <summary>After an edit (end of a Do): the copies it did not need are given back at once.</summary>
    public void EditDone(Scene current) { if (CopyOnWrite) ReleaseUnchanged(current); }

    /// <summary>Memory the history keeps now (copy-on-write mode), for the status / tests.</summary>
    public long HistoryBytes { get { long t = 0; foreach (var b in undoBytes) t += b; return t; } }

    /// <summary>Returns the scene to show after undoing, or null if there is nothing to undo.</summary>
    public Scene Undo(Scene current)
    {
        if (undo.Count == 0) return null;
        ReleaseUnchanged(current);
        var (name, state) = undo.Last.Value; undo.RemoveLast(); undoStamp.RemoveLast(); if (undoBytes.Count > 0) undoBytes.RemoveLast();
        redo.Push((name, Snapshot(current)));
        return Live(state, current);
    }

    public Scene Redo(Scene current)
    {
        if (redo.Count == 0) return null;
        ReleaseUnchanged(current);
        var (name, state) = redo.Pop();
        undo.AddLast((name, Snapshot(current))); undoStamp.AddLast(Tick()); undoBytes.AddLast(current.Objects.Count * 1500L);
        return Live(state, current);
    }

    /// <summary>Forgets the last recorded state without restoring it (a cancelled modal tool restores by itself).</summary>
    public void DropLast()
    {
        if (undo.Count > 0) { undo.RemoveLast(); undoStamp.RemoveLast(); if (undoBytes.Count > 0) undoBytes.RemoveLast(); }
        if (redoBackup != null) { redo.Clear(); foreach (var r in redoBackup) redo.Push(r); redoBackup = null; }
    }

    /// <summary>Forgets the last recorded state, redo untouched (steps folded into an earlier one).</summary>
    public void DropLastKeepRedo() { if (undo.Count > 0) { undo.RemoveLast(); undoStamp.RemoveLast(); if (undoBytes.Count > 0) undoBytes.RemoveLast(); } }

    /// <summary>Returns the last recorded state and forgets it, without touching redo.</summary>
    public Scene PopLast(Scene current = null)
    {
        if (undo.Count == 0) return null;
        var s = undo.Last.Value.State; undo.RemoveLast(); undoStamp.RemoveLast(); if (undoBytes.Count > 0) undoBytes.RemoveLast();
        return Live(s, current);
    }

    public void Clear() { try { prefreeze?.Wait(); } catch { } prefreeze = null; undo.Clear(); undoStamp.Clear(); undoBytes.Clear(); redo.Clear(); frozen.Clear(); frozenByLiveT.Clear(); pendingCopies.Clear(); pendingStep = null; CopyOnWrite = false; }
}
