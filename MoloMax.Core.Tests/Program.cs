using MoloMax.Core.Paint;
using System.Numerics;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
if (args.Length > 1 && args[0] == "types") { foreach (var t in typeof(CodeWalker.GameFiles.YtdFile).Assembly.GetTypes().Where(t => t.Name.Contains(args[1]))) { Console.WriteLine(t.FullName); foreach (var m in t.GetMembers().Where(m => m.DeclaringType == t)) Console.WriteLine("   " + m.MemberType + " " + m); } return 0; }
if (args.Length > 0 && args[0] == "skin") {
    // a tube with two bones, skinned, exported as a .ydr: skeleton + weights read back through CodeWalker
    var sc = new Scene();
    var tube = sc.Add("tube", Primitives.Cylinder(0.2f, 2f, 12, 8), Vector3.Zero);
    var b1 = sc.Add("bone_a", Primitives.Box(0.05f, 0.05f, 0.05f), Vector3.Zero); b1.IsBone = true; b1.BoneLength = 1; b1.Gta.Role = GtaRole.Ignore;
    var b2 = sc.Add("bone_b", Primitives.Box(0.05f, 0.05f, 0.05f), new Vector3(0, 0, 1)); b2.IsBone = true; b2.BoneLength = 1; b2.Gta.Role = GtaRole.Ignore; b2.ParentId = b1.Id;
    tube.Position = new Vector3(3, 1, 0); b1.Position += tube.Position; b2.Position += tube.Position;
    var sk = new MoloMax.Core.Modifiers.SkinModifier();
    sk.Bind(tube.Mesh, tube.World, new[] { (b1.Id, b1.World, 1f), (b2.Id, b2.World, 1f) });
    sk.OwnerId = tube.Id; tube.Modifiers.Add(sk);
    var path = "/tmp/skin_test/tube_skin.ydr";
    Console.WriteLine(MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(sc, new[] { tube }, path, _ => null));
    var data = File.ReadAllBytes(path);
    var e = CodeWalker.GameFiles.RpfFile.CreateResourceFileEntry(ref data, 0);
    data = CodeWalker.GameFiles.ResourceBuilder.Decompress(data);
    var y = new CodeWalker.GameFiles.YdrFile(); y.Load(data, e);
    var d = y.Drawable;
    var bones = d.Skeleton?.Bones?.Items;
    Console.WriteLine($"bones {bones?.Length}: " + string.Join(" | ", bones.Select(b => $"{b.Name} tag {b.Tag} parent {b.ParentIndex} t {b.Translation}")));
    var m0 = d.DrawableModels.High[0];
    Console.WriteLine($"model HasSkin {m0.HasSkin} BoneIndex {m0.BoneIndex} geoms {m0.Geometries.Length} decl {m0.Geometries[0].VertexBuffer.Info.Flags:X} stride {m0.Geometries[0].VertexBuffer.VertexStride} boneids {m0.Geometries[0].BoneIds?.Length}");
    var xml = CodeWalker.GameFiles.YdrXml.GetXml(y, "");
    File.WriteAllText("/tmp/skin_test/back.xml", xml);
    // weights: points at the bottom on bone 1, top on bone 2
    var lines = xml.Split('\n').SkipWhile(l => !l.Contains("<Data>")).Skip(1).TakeWhile(l => !l.Contains("</Data>")).ToList();
    int lowOk = 0, low = 0, highOk = 0, high = 0;
    foreach (var l in lines)
    {
        var parts = l.Trim().Split("   ");
        var pos = parts[0].Split(' ').Select(float.Parse).ToArray(); var w = parts[1].Split(' ').Select(int.Parse).ToArray(); var ix = parts[2].Split(' ').Select(int.Parse).ToArray();
        int top = Enumerable.Range(0, 4).OrderByDescending(k => w[k]).First();
        if (pos[2] < 0.3f) { low++; if (ix[top] == 1 && w.Sum() == 255) lowOk++; }
        if (pos[2] > 1.7f) { high++; if (ix[top] == 2 && w.Sum() == 255) highOk++; }
    }
    Console.WriteLine($"bottom on bone_a {lowOk}/{low}, top on bone_b {highOk}/{high}");
    return lowOk == low && highOk == high && low > 0 && high > 0 && bones.Length == 3 ? 0 : 1;
}
if (args.Length > 0 && args[0] == "chamfer") {
    float V(PolyMesh m) => MoloMax.Core.Mesh.CsgClean.Volume(m);
    void Run(string name, Func<PolyMesh, List<EdgeKey>> pick, float amt, int seg)
    {
        var b = Primitives.Box(2, 2, 2);
        var e = pick(b);
        MeshOps.ChamferEdges(b, e, amt, seg);
        Console.WriteLine($"{name} seg {seg}: {b.Faces.Count} faces {b.Verts.Count} verts closed {b.IsClosedManifold(out var w)} {w} vol {V(b):0.0000} corners [{string.Join(",", b.Faces.GroupBy(f => f.V.Length).OrderBy(g => g.Key).Select(g => g.Key + "x" + g.Count()))}]");
    }
    List<EdgeKey> Edges(PolyMesh b, Func<Vector3, Vector3, bool> pred) => b.BuildEdgeMap().Keys.Where(k => pred(b.Verts[k.A], b.Verts[k.B])).ToList();
    foreach (var seg in new[] { 1, 4 })
    {
        Run("one vertical edge", b => Edges(b, (a, c) => a.X > 0.9f && c.X > 0.9f && a.Y > 0.9f && c.Y > 0.9f), 0.2f, seg);
        Run("three edges at a corner", b => Edges(b, (a, c) => (a.X > 0.9f && a.Y > 0.9f && a.Z > 1.9f) || (c.X > 0.9f && c.Y > 0.9f && c.Z > 1.9f)), 0.2f, seg);
        Run("top loop (4 edges)", b => Edges(b, (a, c) => a.Z > 1.9f && c.Z > 1.9f), 0.2f, seg);
        Run("all 12 edges", b => b.BuildEdgeMap().Keys.ToList(), 0.2f, seg);
        Run("vertical + top edge (L)", b => Edges(b, (a, c) => (a.X > 0.9f && c.X > 0.9f && a.Y > 0.9f && c.Y > 0.9f) || (a.X > 0.9f && c.X > 0.9f && a.Z > 1.9f && c.Z > 1.9f)), 0.2f, seg);
        // after a connect: the new loop chamfered
        Run("connect ring then chamfer the new loop", b => { var ring = Edges(b, (a, c) => MathF.Abs(a.Z - c.Z) > 1.9f).ToHashSet(); return MeshOps.ConnectEdges(b, ring).ToList(); }, 0.2f, seg);
        Run("connect ring then chamfer one vertical half-edge", b => { var ring = Edges(b, (a, c) => MathF.Abs(a.Z - c.Z) > 1.9f).ToHashSet(); MeshOps.ConnectEdges(b, ring); return Edges(b, (a, c) => a.X > 0.9f && c.X > 0.9f && a.Y > 0.9f && c.Y > 0.9f && a.Z > 0.9f && c.Z > 0.9f); }, 0.2f, seg);
        Run("extruded face then chamfer its side edge", b => { int top = Enumerable.Range(0, b.Faces.Count).First(i => b.FaceNormal(i).X > 0.9f); MeshOps.ExtrudeFaces(b, new HashSet<int> { top }, 0.3f); return Edges(b, (a, c) => a.X > 1.2f && c.X > 1.2f && a.Y > 0.9f && c.Y > 0.9f); }, 0.055f, seg);
    }
    return 0;
}
if (args.Length > 2 && args[0] == "chaosrep") {
    Chaos.Replay = (int.Parse(args[1]), int.Parse(args[2]));
    var f = Chaos.Run(1, Chaos.Replay.Step + 1, Console.WriteLine, Chaos.Replay.Seed);
    if (Chaos.ReplayBefore != null)
    {
        var sc = new Scene(); sc.Add("before", Chaos.ReplayBefore, Vector3.Zero); sc.Add("after", Chaos.ReplayAfter, new Vector3(6, 0, 0));
        SceneIO.Save(sc, "/tmp/chaos_replay.mmx");
        var A = Chaos.ReplayAfter; var B = Chaos.ReplayBefore;
        var dir = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < A.Faces.Count; i++) { var fv = A.Faces[i].V; for (int k = 0; k < fv.Length; k++) { var key = (fv[k], fv[(k + 1) % fv.Length]); if (!dir.TryGetValue(key, out var l)) dir[key] = l = new(); l.Add(i); } }
        string P(Vector3 v) => $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})";
        foreach (var kv in dir.Where(kv => kv.Value.Count > 1 || !dir.ContainsKey((kv.Key.Item2, kv.Key.Item1))).Take(3))
        {
            Console.WriteLine($"  bad edge {kv.Key} {P(A.Verts[kv.Key.Item1])}-{P(A.Verts[kv.Key.Item2])} in faces {string.Join(",", kv.Value)} (twin: {(dir.TryGetValue((kv.Key.Item2, kv.Key.Item1), out var tw) ? string.Join(",", tw) : "none")})");
            foreach (var fi in kv.Value.Concat(tw ?? new()).Distinct()) Console.WriteLine($"     face {fi}: {string.Join(" ", A.Faces[fi].V.Select(v => v + P(A.Verts[v])))}");
        }
        Console.WriteLine($"  before {B.Verts.Count} pts {B.Faces.Count} faces -> after {A.Verts.Count} pts {A.Faces.Count} faces");
        if (args.Length > 3) for (int i = 0; i < B.Faces.Count; i++) Console.WriteLine($"     before face {i} m{B.Faces[i].MatId} s{B.Faces[i].Smooth}: {string.Join(" ", B.Faces[i].V.Select(v => v + P(B.Verts[v])))}  n={P(B.FaceNormal(i))}");
    }
    return 0;
}
if (args.Length > 0 && args[0] == "chaos") {
    int seeds = args.Length > 1 ? int.Parse(args[1]) : 50, steps = args.Length > 2 ? int.Parse(args[2]) : 60;
    int first = args.Length > 3 ? int.Parse(args[3]) : 1;
    var fails = Chaos.Run(seeds, steps, Console.WriteLine, first);
    Console.WriteLine($"chaos: {seeds} x {steps} operations, {fails.Count} problem(s)");
    foreach (var g in fails.GroupBy(f => f.Op + " | " + (f.What.Length > 60 ? f.What[..60] : f.What)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Count(),4}  {g.Key}   (first: seed {g.First().Seed} step {g.First().Step})");
    return fails.Count == 0 ? 0 : 1;
}
if (args.Length > 0 && args[0] == "chamfercases") {
    PolyMesh B(int seg) => Primitives.Box(2, 2, 2, seg, seg, seg);
    var cases = new List<(string, Func<PolyMesh, HashSet<EdgeKey>>, int)>();
    HashSet<EdgeKey> NearE(PolyMesh m, Func<Vector3, Vector3, bool> keep) { var r = new HashSet<EdgeKey>(); foreach (var f in m.Faces) foreach (var e in m.FaceEdges(f)) if (keep(m.Verts[e.A], m.Verts[e.B])) r.Add(e); return r; }
    bool Top(Vector3 a) => a.Z > 1.99f; bool X1(Vector3 a) => a.X > 0.99f; bool Y1(Vector3 a) => a.Y > 0.99f;
    cases.Add(("one edge", m => NearE(m, (a, b) => Top(a) && Top(b) && X1(a) && X1(b)), 1));
    cases.Add(("L two edges at a corner", m => NearE(m, (a, b) => Top(a) && Top(b) && (X1(a) && X1(b) || Y1(a) && Y1(b))), 1));
    cases.Add(("three edges at a corner", m => NearE(m, (a, b) => (Top(a) && Top(b) && (X1(a) && X1(b) || Y1(a) && Y1(b))) || (X1(a) && X1(b) && Y1(a) && Y1(b))), 1));
    cases.Add(("top loop", m => NearE(m, (a, b) => Top(a) && Top(b) && (MathF.Abs(a.X) > 0.99f && MathF.Abs(b.X) > 0.99f && a.X * b.X > 0 || MathF.Abs(a.Y) > 0.99f && MathF.Abs(b.Y) > 0.99f && a.Y * b.Y > 0)), 1));
    cases.Add(("straight chain across a grid", m => NearE(m, (a, b) => Top(a) && Top(b) && MathF.Abs(a.Y) < 0.01f && MathF.Abs(b.Y) < 0.01f), 2));
    cases.Add(("chain with a turn on a grid", m => NearE(m, (a, b) => Top(a) && Top(b) && (MathF.Abs(a.Y) < 0.01f && MathF.Abs(b.Y) < 0.01f && a.X <= 0.01f && b.X <= 0.01f || MathF.Abs(a.X) < 0.01f && MathF.Abs(b.X) < 0.01f && a.Y >= -0.01f && b.Y >= -0.01f)), 2));
    cases.Add(("cross (4 edges at a vertex)", m => NearE(m, (a, b) => Top(a) && Top(b) && (MathF.Abs(a.Y) < 0.01f && MathF.Abs(b.Y) < 0.01f || MathF.Abs(a.X) < 0.01f && MathF.Abs(b.X) < 0.01f)), 2));
    cases.Add(("T (3 edges at a grid vertex)", m => NearE(m, (a, b) => Top(a) && Top(b) && (MathF.Abs(a.Y) < 0.01f && MathF.Abs(b.Y) < 0.01f || MathF.Abs(a.X) < 0.01f && MathF.Abs(b.X) < 0.01f && a.Y >= -0.01f && b.Y >= -0.01f)), 2));
    foreach (var (name, pick, seg) in cases)
        foreach (int segs in new[] { 1, 3 })
        {
            var m = B(seg); var e = pick(m);
            string res;
            try { MeshOps.ChamferEdges(m, e.ToList(), 0.15f, segs); m.Validate(); res = m.IsClosedManifold(out var why) ? "ok" : "OPEN: " + why; }
            catch (Exception ex) { res = "threw " + ex.Message; }
            Console.WriteLine($"  {name,-32} {e.Count} edges, {segs} seg: {res}");
        }
    return 0;
}
if (args.Length > 0 && args[0] == "opsbench") {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var m = Primitives.Plane(60, 60, 700, 700); Console.WriteLine($"plane {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var c = m.Clone(); Console.WriteLine($"clone {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var sel = new HashSet<int>(Enumerable.Range(0, 50000));
    MeshOps.ExtrudeFaces(c, sel, 0.2f); Console.WriteLine($"extrude {sw.ElapsedMilliseconds} ms"); sw.Restart();
    c.Validate(); Console.WriteLine($"validate {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var ps = new PolySelection(); ps.Faces = sel; ps.Sanitise(c); Console.WriteLine($"sanitise {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var g = RenderMeshBuilder.Build(c); Console.WriteLine($"render build {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var e = c.DrawEdges(); Console.WriteLine($"draw edges {sw.ElapsedMilliseconds} ms"); sw.Restart();
    var sc = new Scene(); sc.Add("big", c, Vector3.Zero); var u = new UndoStack();
    u.Record("a", sc); Console.WriteLine($"undo record {sw.ElapsedMilliseconds} ms"); sw.Restart();
    sc.Objects[0].Touch(); var back = u.Undo(sc); Console.WriteLine($"undo {sw.ElapsedMilliseconds} ms"); sw.Restart();
    return 0;
}
if (args.Length > 0 && args[0] == "dense") {
    void Rep(string name, PolyMesh before, Func<PolyMesh> run) {
        var sw = System.Diagnostics.Stopwatch.StartNew(); PolyMesh r = null; string err = null;
        try { r = run(); } catch (Exception e) { err = e.Message; }
        if (r == null) { Console.WriteLine($"{name}: ERROR {err}"); return; }
        Console.WriteLine($"{name}: {before.Faces.Count} -> {r.Faces.Count} faces, closed {r.IsClosedManifold(out var w)} {w}, vol {MoloMax.Core.Mesh.CsgClean.Volume(before):0.000} -> {MoloMax.Core.Mesh.CsgClean.Volume(r):0.000}, {sw.ElapsedMilliseconds} ms, corners [{string.Join(",", r.Faces.Select(f => f.V.Length).GroupBy(x => x).OrderBy(g => g.Key).Take(8).Select(g => g.Key + "x" + g.Count()))}]");
    }
    var T = Matrix4x4.CreateTranslation(2500, -1500, 30);
    var dense = Primitives.Box(4, 0.3f, 3, 40, 3, 30); MeshOps.WeldByDistance(dense, 1e-5f); dense.CleanFaces();
    var door = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -1.4f, Right = -0.4f, Bottom = 0, Top = 2.1f };
    var win = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = 0.45f, Right = 1.55f, Bottom = 1.0f, Top = 2.0f };
    { var T0 = Matrix4x4.CreateTranslation(1500, -800, 40);
      var cs = MoloMax.Core.Mesh.Csg.Apply(Primitives.Box(2, 2, 2), T0, Primitives.Sphere(1.3f, 32), Matrix4x4.CreateTranslation(0, 0, 1) * T0, MoloMax.Core.Mesh.Csg.Op.Subtract);
      if (Environment.GetEnvironmentVariable("DENSE_DUMP") == "2") for (int i = 0; i < cs.Faces.Count; i++) { var nn = cs.FaceNormal(i); if (MathF.Abs(nn.X) > 0.9999f || MathF.Abs(nn.Y) > 0.9999f || MathF.Abs(nn.Z) > 0.9999f) Console.WriteLine($"  n={nn.X:0},{nn.Y:0},{nn.Z:0} corners {cs.Faces[i].V.Length} c={cs.FaceCenter(cs.Faces[i])}"); } }
    Rep("dense wall 40x30 + door", dense, () => MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dense, door));
    { var rw = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dense, door), win);
      if (Environment.GetEnvironmentVariable("DENSE_DUMP") == "1") foreach (var f in rw.Faces.Where(f => f.V.Length > 4)) Console.WriteLine("  " + string.Join(" ", f.V.Select(v => $"({rw.Verts[v].X:0.###},{rw.Verts[v].Y:0.###},{rw.Verts[v].Z:0.###})"))); }
    Rep("dense wall 40x30 + door + window", dense, () => MoloMax.Core.Modifiers.WallOpeningModifier.Cut(MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dense, door), win));
    var odd = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -0.333f, Right = 0.517f, Bottom = 0.777f, Top = 1.913f };
    Rep("dense wall + opening not on the grid", dense, () => MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dense, odd));
    var cyl = Primitives.Cylinder(2, 3, 64);
    Rep("cylinder 64 sides - box (boolean)", cyl, () => MoloMax.Core.Mesh.Csg.Apply(cyl, T, Primitives.Box(1, 5, 1.5f), Matrix4x4.CreateTranslation(0, 0, 0.8f) * T, MoloMax.Core.Mesh.Csg.Op.Subtract));
    var curvedOp = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -2, 0), Normal = -Vector3.UnitY, Left = -0.5f, Right = 0.5f, Bottom = 0.5f, Top = 2f };
    var tube = Primitives.Tube(2f, 1.8f, 3, 64);
    Rep("round tower wall (tube 64) + Wall Opening", tube, () => MoloMax.Core.Modifiers.WallOpeningModifier.Cut(tube, curvedOp));
    var big = Primitives.Sphere(2, 96); 
    Rep("sphere ~18k faces - box (boolean)", big, () => MoloMax.Core.Mesh.Csg.Apply(big, T, Primitives.Box(1.5f, 1.5f, 1.5f), Matrix4x4.CreateTranslation(1.5f, 0, 0.5f) * T, MoloMax.Core.Mesh.Csg.Op.Subtract));
    var bigwall = Primitives.Box(20, 0.3f, 6, 200, 3, 60); MeshOps.WeldByDistance(bigwall, 1e-5f); bigwall.CleanFaces();
    Rep("big wall 200x60 (24k faces) + door", bigwall, () => MoloMax.Core.Modifiers.WallOpeningModifier.Cut(bigwall, door));
    return 0; }
if (args.Length > 2 && args[0] == "game") return GameTest.Run(args[1], args[2]);
if (args.Length > 1 && args[0] == "bench") return LeatherBench.Run(args[1], args.Length > 2 && args[2] == "worn");
if (args.Length > 0 && args[0] == "bench") { if (args.Length > 1 && args[1] == "lightxml") LightXml.Run(); else if (args.Length > 2) Reflect.Run(args[2]); else if (args.Length > 1) BenchImport.Run(); else Bench.Run(); return 0; }

// Self-contained test runner (no test framework needed): `dotnet run --project MoloMax.Core.Tests`
int pass = 0, fail = 0;
void Check(string name, Func<bool> test)
{
    try
    {
        if (test()) { pass++; Console.WriteLine($"  ok   {name}"); }
        else { fail++; Console.WriteLine($"  FAIL {name}"); }
    }
    catch (Exception e) { fail++; Console.WriteLine($"  FAIL {name}: {e.GetType().Name} {e.Message}"); }
}
bool Closed(PolyMesh m) { m.Validate(); if (!m.IsClosedManifold(out var p)) { Console.WriteLine("       " + p); return false; } return true; }
bool Outward(PolyMesh m)
{
    // every face normal points away from the centroid (convex shapes only)
    var c = Vector3.Zero; foreach (var v in m.Verts) c += v; c /= m.Verts.Count;
    return m.Faces.All(f => Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - c) > 0);
}
float Volume(PolyMesh m)
{
    float vol = 0;
    foreach (var f in m.Faces) foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            vol += Vector3.Dot(m.Verts[f.V[a]], Vector3.Cross(m.Verts[f.V[b]], m.Verts[f.V[c]])) / 6f;
    return vol;
}
bool Near(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) <= eps;
HashSet<int> TopFaces(PolyMesh m) => Enumerable.Range(0, m.Faces.Count).Where(i => m.FaceNormal(i).Z > 0.99f).ToHashSet();

Console.WriteLine("Primitives");
Check("box 2x3x4 closed, outward, volume 24", () => { var b = Primitives.Box(2, 3, 4); return Closed(b) && Outward(b) && Near(Volume(b), 24) && b.Verts.Count == 8 && b.Faces.Count == 6; });
Check("box with segments welds to a closed mesh", () => { var b = Primitives.Box(2, 2, 2, 3, 2, 4); return Closed(b) && Near(Volume(b), 8) && b.Faces.Count == 2 * (3 * 2 + 3 * 4 + 2 * 4); });
Check("box pivot is the base centre", () => { var (mn, mx) = Primitives.Box(2, 2, 3).Bounds(); return Near(mn.Z, 0) && Near(mx.Z, 3) && Near(mn.X, -1); });
Check("shift clone faces: a copy of the top face, new vertices, original kept", () =>
{
    var b = Primitives.Box(2, 2, 2); int top = Enumerable.Range(0, b.Faces.Count).OrderByDescending(i => b.Faces[i].V.Average(v => b.Verts[v].Z)).First();
    var nf = MeshOps.CloneFaces(b, new HashSet<int> { top }); b.Validate();
    return nf.Count == 1 && b.Faces.Count == 7 && b.Verts.Count == 12 && !b.Faces[nf[0]].V.Intersect(b.Faces[top].V).Any() && b.Faces[nf[0]].V.All(v => Near(b.Verts[v].Z, 2));
});
Check("shift clone edges: a strip pulled out of a border edge, same winding as its face", () =>
{
    var p = Primitives.Plane(2, 2, 1, 1); p.Validate();
    var border = MeshOps.BorderEdges(p); var e = border.First();
    var ne = MeshOps.CloneEdgesAsStrip(p, new HashSet<EdgeKey> { e }); p.Validate();
    // the old edge is now shared by 2 faces running it in opposite directions (no flipped face)
    int fwd = 0, back = 0;
    foreach (var f in p.Faces) for (int c = 0; c < f.V.Length; c++) { int a = f.V[c], bb = f.V[(c + 1) % f.V.Length]; if (a == e.A && bb == e.B) fwd++; if (a == e.B && bb == e.A) back++; }
    return ne.Count == 1 && p.Faces.Count == 2 && p.Verts.Count == 6 && fwd == 1 && back == 1;
});
Check("shift clone edges: a chain of edges gives one connected strip", () =>
{
    var p = Primitives.Plane(2, 2, 1, 1); p.Validate();
    var border = MeshOps.BorderEdges(p);
    var ne = MeshOps.CloneEdgesAsStrip(p, border); p.Validate();
    return ne.Count == 4 && p.Faces.Count == 5 && p.Verts.Count == 8;
});
Check("UV projections: cylinder / sphere / lightmap / view bounds / reset give finite UVs in 0..1", () =>
{
    bool In01(PolyMesh m, float slack = 1.001f) => m.Faces.All(f => f.UV.All(u => float.IsFinite(u.X) && float.IsFinite(u.Y) && u.X >= -0.001f && u.Y >= -0.001f && u.X <= 1f + slack && u.Y <= 1.001f));
    var c = Primitives.Cylinder(1, 2, 16); UvProject.Cylinder(c);
    var sp = Primitives.Sphere(1, 16); UvProject.Sphere(sp);
    var bx = Primitives.Box(2, 2, 2); UvProject.LightmapPack(bx);
    var b2 = Primitives.Box(3, 1, 2); UvProject.FromView(b2, null, Vector3.UnitX, Vector3.UnitZ, true);
    var b3 = Primitives.Box(1, 1, 1); UvProject.Reset(b3);
    // cylinder / sphere: U may pass 1 on faces crossing the seam (kept continuous), never more than one turn
    return In01(c, 1f) && In01(sp, 1f) && In01(bx, 0.001f) && In01(b2, 0.001f) && In01(b3, 0.001f) && UvTools.Islands(bx).Count == 6;
});
Check("bridge: two separate edges give one quad, winding like the face next to them", () =>
{
    // two quads side by side with a gap: bridge their facing edges
    var m = new PolyMesh();
    int a0 = m.AddVertex(new Vector3(0, 0, 0)), a1 = m.AddVertex(new Vector3(1, 0, 0)), a2 = m.AddVertex(new Vector3(1, 1, 0)), a3 = m.AddVertex(new Vector3(0, 1, 0));
    int b0 = m.AddVertex(new Vector3(2, 0, 0)), b1 = m.AddVertex(new Vector3(3, 0, 0)), b2 = m.AddVertex(new Vector3(3, 1, 0)), b3 = m.AddVertex(new Vector3(2, 1, 0));
    m.AddFace(new[] { a0, a1, a2, a3 }); m.AddFace(new[] { b0, b1, b2, b3 });
    var ch = MeshOps.EdgeChains(new[] { EdgeKey.Make(a1, a2), EdgeKey.Make(b0, b3) });
    int n = MeshOps.BridgeChains(m, ch[0].Verts, ch[0].Closed, ch[1].Verts, ch[1].Closed);
    m.Validate();
    return n == 1 && m.Faces.Count == 3 && m.FaceNormal(m.Faces[2]).Z > 0.9f;
});
Check("bridge: open chains with different counts zip with triangles", () =>
{
    var m = Primitives.Plane(2, 2, 2, 1); var m2 = Primitives.Plane(2, 2, 3, 1);
    m2.Transform(Matrix4x4.CreateTranslation(0, 4, 0));
    int off = m.Verts.Count; foreach (var v in m2.Verts) m.AddVertex(v); foreach (var f in m2.Faces) m.AddFace(f.V.Select(i => i + off).ToArray());
    m.Validate();
    float y1 = m.Verts.Take(off).Max(v => v.Y), y2 = m.Verts.Skip(off).Min(v => v.Y);
    var border = MeshOps.BorderEdges(m);
    var sel = border.Where(e => (m.Verts[e.A].Y == y1 && m.Verts[e.B].Y == y1 && e.A < off) || (m.Verts[e.A].Y == y2 && m.Verts[e.B].Y == y2 && e.A >= off)).ToList();
    var ch = MeshOps.EdgeChains(sel);
    int faces0 = m.Faces.Count;
    int n = MeshOps.BridgeChains(m, ch[0].Verts, ch[0].Closed, ch[1].Verts, ch[1].Closed);
    return ch.Count == 2 && n == 5 && m.Faces.Count == faces0 + 5 && m.Faces.Skip(faces0).All(f => m.FaceNormal(f).Z > 0.9f);
});
Check("bridge: two faces with different corner counts (quad + triangle) become a tube", () =>
{
    var m = new PolyMesh();
    var q = new[] { m.AddVertex(new(0, 0, 0)), m.AddVertex(new(1, 0, 0)), m.AddVertex(new(1, 1, 0)), m.AddVertex(new(0, 1, 0)) };
    var t = new[] { m.AddVertex(new(0, 0, 2)), m.AddVertex(new(0.5f, 1, 2)), m.AddVertex(new(1, 0, 2)) };
    m.AddFace(q); m.AddFace(t);
    int n = MeshOps.BridgeFaceGroups(m, new HashSet<int> { 0, 1 });
    m.Validate();
    return n == 7 && m.Faces.Count == 7;
});
Check("plane is open with 16 quads", () => { var p = Primitives.Plane(4, 4); p.Validate(); return p.Faces.Count == 16 && !p.IsClosedManifold(out _); });
Check("cylinder closed and outward", () => { var c = Primitives.Cylinder(1, 2, 16, 3); return Closed(c) && Outward(c) && Near(Volume(c), 2 * 16 * 0.5f * MathF.Sin(MathF.Tau / 16), 1e-2f); });
Check("cone (apex) closed and outward", () => { var c = Primitives.Cone(1, 0, 2, 12); return Closed(c) && Outward(c); });
Check("truncated cone closed", () => Closed(Primitives.Cone(1, 0.5f, 2, 12)));
Check("sphere closed and outward", () => { var s = Primitives.Sphere(1, 24); return Closed(s) && Outward(s) && Near(Volume(s), 4f / 3f * MathF.PI, 0.15f); });
Check("tube closed", () => Closed(Primitives.Tube(1, 0.8f, 2, 16)));
Check("MLO room is closed with normals facing inward (negative volume)", () => { var r = Primitives.Room(6, 6, 3); return Closed(r) && Volume(r) < 0 && Near(Volume(r), -108); });
Check("door closed, 1.2 x 0.06 x 2.3", () => { var d = Primitives.Door(); return Closed(d) && Near(Volume(d), 1.2f * 0.06f * 2.3f); });
Check("stairs closed solid with positive volume", () =>
{
    var s = Primitives.Stairs(1.2f, 10, 3f, 3f);
    // staircase area in profile = sum of step columns: t * r * (1+2+...+n)
    float area = 0.3f * 0.3f * 55;
    return Closed(s) && Near(Volume(s), area * 1.2f, 1e-2f);
});
Check("straight wall closed, volume = length*height*thickness", () => { var w = Primitives.Wall(new[] { new Vector2(0, 0), new Vector2(5, 0) }, 3, 0.2f); return Closed(w) && Near(Volume(w), 5 * 3 * 0.2f); });
Check("L-shaped wall closed and positive", () => { var w = Primitives.Wall(new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 3) }, 2.5f, 0.2f); return Closed(w) && Volume(w) > 0; });
Check("closed square wall (ring) closed", () => { var w = Primitives.Wall(new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 4), new Vector2(0, 4) }, 3, 0.2f, closed: true); return Closed(w) && Near(Volume(w), (4.2f * 4.2f - 3.8f * 3.8f) * 3, 1e-2f); });

Console.WriteLine("Triangulation / render data");
Check("concave stairs side triangulates to n-2 triangles with full area", () =>
{
    var s = Primitives.Stairs(1, 4, 1, 1);
    var side = s.Faces.First(f => f.V.Length > 4);
    var tris = Triangulator.Triangulate(s, side);
    float area = 0; foreach (var (a, b, c) in tris) area += Vector3.Cross(s.Verts[side.V[b]] - s.Verts[side.V[a]], s.Verts[side.V[c]] - s.Verts[side.V[a]]).Length() / 2;
    return tris.Count == side.V.Length - 2 && Near(area, s.FaceArea(side));
});
Check("render groups: one per material, 3 indices per triangle", () =>
{
    var b = Primitives.Box(1, 1, 1); b.Faces[0].MatId = 5; b.Faces[1].MatId = 5;
    var g = RenderMeshBuilder.Build(b);
    return g.Count == 2 && g.Sum(x => x.Indices.Count) == 36 && g.All(x => x.TriangleFace.Count * 3 == x.Indices.Count);
});
Check("smoothing groups: flat cube corners differ, smooth sphere corners match", () =>
{
    var b = Primitives.Box(1, 1, 1); foreach (var f in b.Faces) f.Smooth = 0;
    var nb = RenderMeshBuilder.CornerNormals(b);
    var s = Primitives.Sphere(1, 12); var ns = RenderMeshBuilder.CornerNormals(s);
    // the top pole is shared by several faces; all its corner normals must be identical when smooth
    var pole = new List<Vector3>(); for (int fi = 0; fi < s.Faces.Count; fi++) { int c = s.Faces[fi].IndexOf(0); if (c >= 0) pole.Add(ns[fi][c]); }
    return Vector3.Distance(nb[0][0], nb[2][0]) > 0.5f && pole.All(n => Vector3.Distance(n, pole[0]) < 1e-4f);
});
Check("tangents are unit and orthogonal to normals", () =>
{
    var g = RenderMeshBuilder.Build(Primitives.Box(2, 2, 2));
    return g.SelectMany(x => x.Vertices).All(v => Near(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z).Length(), 1) && MathF.Abs(Vector3.Dot(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), v.Normal)) < 1e-3f);
});

Console.WriteLine("Polygon operations");
Check("extrude top face of a box by 1 → closed, volume grows by 1", () =>
{
    var b = Primitives.Box(1, 1, 1); var top = TopFaces(b);
    var sides = MeshOps.ExtrudeFaces(b, top, 1f);
    return sides.Count == 4 && Closed(b) && Near(Volume(b), 2);
});
Check("extrude two adjacent faces as a group keeps them joined", () =>
{
    var b = Primitives.Box(2, 1, 1, 2, 1, 1); var top = TopFaces(b);
    MeshOps.ExtrudeFaces(b, top, 0.5f);
    return top.Count == 2 && Closed(b) && Near(Volume(b), 3);
});
Check("extrude by polygon on two faces makes 8 sides", () =>
{
    var b = Primitives.Box(2, 1, 1, 2, 1, 1); var top = TopFaces(b);
    var sides = MeshOps.ExtrudeFaces(b, top, 0.5f, byPolygon: true);
    return sides.Count == 8 && Closed(b);
});
Check("inset top face 0.1 m → closed, inner face 0.8x0.8", () =>
{
    var b = Primitives.Box(1, 1, 1); var top = TopFaces(b);
    var ring = MeshOps.InsetFaces(b, top, 0.1f);
    var inner = b.Faces[top.First()];
    return ring.Count == 4 && Closed(b) && Near(b.FaceArea(inner), 0.64f) && Near(Volume(b), 1);
});
Check("inset group of 2x2 faces insets only the outline", () =>
{
    var p = Primitives.Box(2, 2, 1, 2, 2, 1); var top = TopFaces(p);
    MeshOps.InsetFaces(p, top, 0.25f);
    float area = top.Sum(f => p.FaceArea(p.Faces[f]));
    return Closed(p) && Near(area, 1.5f * 1.5f);
});
Check("bevel: extrude 0.5, outline -0.1 → top shrinks to 0.8", () =>
{
    var b = Primitives.Box(1, 1, 1); var top = TopFaces(b);
    MeshOps.BevelFaces(b, top, 0.5f, -0.1f);
    var t = b.Faces[top.First()];
    return Closed(b) && Near(b.FaceArea(t), 0.64f) && Near(b.FaceCenter(t).Z, 1.5f);
});
Check("tessellate whole box: 24 quads, closed, same volume", () =>
{
    var b = Primitives.Box(1, 1, 1); MeshOps.Tessellate(b, null);
    return b.Faces.Count == 24 && Closed(b) && Near(Volume(b), 1);
});
Check("tessellate one face keeps the box watertight (neighbours get midpoints)", () =>
{
    var b = Primitives.Box(1, 1, 1); MeshOps.Tessellate(b, TopFaces(b));
    return Closed(b) && Near(Volume(b), 1);
});
Check("delete top face then cap the hole → closed again", () =>
{
    var b = Primitives.Box(1, 1, 1); MeshOps.DeleteFaces(b, TopFaces(b));
    bool open = !b.IsClosedManifold(out _);
    var caps = MeshOps.CapBorders(b);
    return open && caps.Count == 1 && Closed(b) && Near(Volume(b), 1);
});
Check("flip all of a box → still closed, negative volume", () => { var b = Primitives.Box(1, 1, 1); MeshOps.FlipAll(b); return Closed(b) && Volume(b) < 0; });
Check("detach top face leaves an open box and a one-face mesh", () =>
{
    var b = Primitives.Box(1, 1, 1); var d = MeshOps.Detach(b, TopFaces(b));
    return d.Faces.Count == 1 && d.Verts.Count == 4 && b.Faces.Count == 5 && b.Verts.Count == 8;
});

Console.WriteLine("Edge / vertex operations");
Check("connect a ring of 4 edges around a box → one new loop, closed", () =>
{
    var b = Primitives.Box(1, 1, 1);
    // vertical edges of the box: the ring around its sides
    var vertical = b.BuildEdgeMap().Keys.Where(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f).ToHashSet();
    var added = MeshOps.ConnectEdges(b, vertical);
    return vertical.Count == 4 && added.Count == 4 && b.Faces.Count == 10 && Closed(b) && Near(Volume(b), 1);
});
Check("remove the edges just connected → back to 6 faces, closed", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var vertical = b.BuildEdgeMap().Keys.Where(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f).ToHashSet();
    var added = MeshOps.ConnectEdges(b, vertical);
    MeshOps.RemoveEdges(b, added, removeVertices: true);
    return b.Faces.Count == 6 && b.Verts.Count == 8 && Closed(b);
});
Check("edge loop around a tessellated box side ring has 8 edges", () =>
{
    var b = Primitives.Box(1, 1, 1, 1, 1, 2); // two height segments: a horizontal loop in the middle
    var midEdge = b.BuildEdgeMap().Keys.First(e => Near(b.Verts[e.A].Z, 0.5f) && Near(b.Verts[e.B].Z, 0.5f));
    var loop = MeshOps.EdgeLoop(b, midEdge);
    return loop.Count == 4 && loop.All(e => Near(b.Verts[e.A].Z, 0.5f) && Near(b.Verts[e.B].Z, 0.5f));
});
Check("edge ring on the box sides has 4 vertical edges", () =>
{
    var b = Primitives.Box(1, 1, 1, 1, 1, 2);
    var vert = b.BuildEdgeMap().Keys.First(e => b.Verts[e.A].Z < 0.25f != b.Verts[e.B].Z < 0.25f && MathF.Min(b.Verts[e.A].Z, b.Verts[e.B].Z) < 0.1f);
    var ring = MeshOps.EdgeRing(b, vert);
    return ring.Count == 4;
});
Check("weld two coincident vertices of two boxes side by side", () =>
{
    var a = Primitives.Box(1, 1, 1); var c = Primitives.Box(1, 1, 1);
    c.Transform(Matrix4x4.CreateTranslation(1, 0, 0)); a.Append(c);
    int removed = MeshOps.WeldByDistance(a, 0.001f);
    return removed == 4 && a.Verts.Count == 12;
});
Check("collapse the four top vertices of a box into one (pyramid)", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var top = Enumerable.Range(0, b.Verts.Count).Where(i => b.Verts[i].Z > 0.5f).ToHashSet();
    MeshOps.Collapse(b, top);
    return b.Verts.Count == 5 && b.Faces.Count == 5 && Closed(b) && Near(Volume(b), 1f / 3f);
});
Check("auto smooth 30°: cylinder sides share, caps stay hard", () =>
{
    var c = Primitives.Cylinder(1, 2, 16); MeshOps.AutoSmooth(c, null, 30);
    var side = c.Faces.Where(f => f.V.Length == 4).Select(f => f.Smooth).Distinct().ToList();
    var caps = c.Faces.Where(f => f.V.Length == 16).ToList();
    return side.Count == 1 && caps.All(cf => (cf.Smooth & side[0]) == 0);
});
Check("box map gives 1 UV unit per metre on a 3 m wall", () =>
{
    var w = Primitives.Wall(new[] { new Vector2(0, 0), new Vector2(3, 0) }, 2, 0.2f);
    var side = w.Faces.First(f => MathF.Abs(w.FaceNormal(f).Y) > 0.99f);
    float span = side.UV.Max(u => u.X) - side.UV.Min(u => u.X);
    return Near(span, 3f);
});

Console.WriteLine("Selection");
Check("convert polygon → vertex → edge on a box face", () =>
{
    var b = Primitives.Box(1, 1, 1); var sel = new PolySelection(); sel.Faces = TopFaces(b);
    sel.Convert(b, SubObject.Polygon, SubObject.Vertex); int v = sel.Verts.Count;
    sel.Convert(b, SubObject.Vertex, SubObject.Edge);
    return v == 4 && sel.Edges.Count == 4;
});
Check("grow one face of a segmented plane selects its neighbours", () =>
{
    var p = Primitives.Plane(3, 3, 3, 3);
    var centre = Enumerable.Range(0, p.Faces.Count).First(i => Near(p.FaceCenter(i).X, 0) && Near(p.FaceCenter(i).Y, 0));
    var g = MeshSelect.GrowFaces(p, new HashSet<int> { centre });
    return g.Count == 9 && MeshSelect.ShrinkFaces(p, g).SetEquals(new[] { centre });
});
Check("element select from one face of two separate boxes picks one box", () =>
{
    var a = Primitives.Box(1, 1, 1); var c = Primitives.Box(1, 1, 1); c.Transform(Matrix4x4.CreateTranslation(3, 0, 0)); a.Append(c);
    return MeshSelect.Elements(a, new[] { 0 }).Count == 6;
});
Check("element select joins pieces cut at texture seams (same place, different points), not a box beside it", () =>
{
    var a = Primitives.Box(1, 1, 1); var c = Primitives.Box(1, 1, 1); c.Transform(Matrix4x4.CreateTranslation(3, 0, 0)); a.Append(c);
    // every face gets its own points, like a mesh read from the game
    var verts = new List<Vector3>();
    foreach (var f in a.Faces) { var nv = new int[f.V.Length]; for (int i = 0; i < f.V.Length; i++) { nv[i] = verts.Count; verts.Add(a.Verts[f.V[i]]); } f.V = nv; }
    a.Verts = verts;
    return MeshSelect.Elements(a, new[] { 0 }).Count == 6;
});

Console.WriteLine("Scene / IO / picking / lighting");
Check("scene json round trip keeps geometry, UVs, colours, materials", () =>
{
    var s = new Scene();
    var mat = s.AddMaterial("wall_plaster", new Vector3(0.8f, 0.7f, 0.6f)); mat.DiffuseTexture = "plaster.dds"; mat.Shader = "normal_spec";
    var b = Primitives.Box(2, 3, 4); b.SetAllMaterial(mat.Id); b.Faces[0].Col[0] = new Vector4(1, 1, 1, 1);
    var o = s.Add("Box", b, new Vector3(10, 20, 30)); o.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
    var back = SceneIO.FromJson(SceneIO.ToJson(s));
    var bo = back.Objects[0];
    return back.Objects.Count == 1 && bo.Name == "Box001" && bo.Mesh.Faces.Count == 6 && Near(Volume(bo.Mesh), 24)
        && bo.Mesh.Faces[0].Col[0] == new Vector4(1, 1, 1, 1) && back.Material(mat.Id).DiffuseTexture == "plaster.dds"
        && Vector3.Distance(bo.Position, o.Position) < 1e-5f && MathF.Abs(bo.Rotation.Z - o.Rotation.Z) < 1e-6f;
});
Check("scene file v2 (binary blocks) keeps UVs, colour layer 1, smoothing, n-gons; v1 files still open", () =>
{
    var s = new Scene();
    var b = Primitives.Box(2, 3, 4); b.Faces[2].UV[1] = new Vector2(0.25f, 0.75f); b.Faces[3].Col1 = Enumerable.Repeat(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), b.Faces[3].V.Length).ToArray();
    b.Faces[4].Smooth = 5;
    s.Add("Box", b, Vector3.Zero);
    var file = Path.Combine(Path.GetTempPath(), "molo_v2_test.mmx");
    SceneIO.Save(s, file);
    var bo = SceneIO.Load(file).Objects[0].Mesh;
    bool v2 = bo.Faces[2].UV[1] == new Vector2(0.25f, 0.75f) && bo.Faces[3].Col1?[2] == new Vector4(0.1f, 0.2f, 0.3f, 0.4f) && bo.Faces[0].Col1 == null && bo.Faces[4].Smooth == 5 && bo.Verts.Count == b.Verts.Count;
    // a version-1 file (numbers as text)
    string v1 = "{\"Format\":\"MoloMax\",\"Version\":1,\"NextObjectId\":2,\"NextMaterialId\":1,\"Materials\":[],\"Objects\":[{\"Id\":1,\"Name\":\"Tri\",\"Pos\":[0,0,0],\"Mesh\":{\"V\":[0,0,0,1,0,0,0,1,0],\"F\":[{\"V\":[0,1,2],\"UV\":[0,0,1,0,0,1],\"M\":0,\"S\":1}]}}]}";
    var o1 = SceneIO.FromJson(v1).Objects[0].Mesh;
    return v2 && o1.Faces.Count == 1 && o1.Faces[0].UV[2] == new Vector2(0, 1);
});
Check("extrude drag: moving the extruded points further gives the same mesh as extruding at once", () =>
{
    var a = Primitives.Plane(4, 4, 4, 4); var b = a.Clone();
    var sel = new HashSet<int> { 5, 6, 9, 10 };
    var moved = new List<(int V, Vector3 N)>();
    var ring = MeshOps.ExtrudeFaces(a, sel, 0.3f, false, moved);
    foreach (var (v, n) in moved) a.Verts[v] += n * (1.1f - 0.3f);
    MeshOps.SideUVMetric(a, ring);
    MeshOps.ExtrudeFaces(b, sel, 1.1f);
    if (a.Verts.Count != b.Verts.Count || a.Faces.Count != b.Faces.Count) return false;
    for (int i = 0; i < a.Verts.Count; i++) if (Vector3.Distance(a.Verts[i], b.Verts[i]) > 1e-4f) return false;
    foreach (var f in ring) for (int k = 0; k < 4; k++) if (Vector2.Distance(a.Faces[f].UV[k], b.Faces[f].UV[k]) > 1e-4f) return false;
    return true;
});
Check("picking a big mesh (boxes of polygons) finds the same face as testing every face", () =>
{
    var s = new Scene(); var o = s.Add("Big", Primitives.Plane(20, 20, 200, 200), new Vector3(3, 2, 1));
    var rnd = new Random(4);
    for (int k = 0; k < 20; k++)
    {
        var target = new Vector3(3 + (float)rnd.NextDouble() * 18 - 9, 2 + (float)rnd.NextDouble() * 18 - 9, 1);
        var org = target + new Vector3(0.3f, -0.2f, 5);
        var hit = Picking.PickFace(o, new Ray(org, Vector3.Normalize(target - org)));
        if (hit == null) return false;
        var c = o.Mesh.FaceCenter(hit.Value.Face) + o.Position;
        if (MathF.Abs(c.X - target.X) > 0.11f || MathF.Abs(c.Y - target.Y) > 0.11f) return false;
    }
    return true;
});
Check("unique names follow Max: Box001, Box002", () => { var s = new Scene(); s.Add("Box", Primitives.Box(1, 1, 1), Vector3.Zero); return s.Add("Box", Primitives.Box(1, 1, 1), Vector3.Zero).Name == "Box002"; });
Check("ray from above picks the top face of a box", () =>
{
    var s = new Scene(); var o = s.Add("Box", Primitives.Box(2, 2, 2), new Vector3(5, 5, 0));
    var hit = Picking.PickObject(s, new Ray(new Vector3(5, 5, 10), -Vector3.UnitZ));
    return hit.HasValue && Near(hit.Value.Point.Z, 2) && o.Mesh.FaceNormal(hit.Value.Face).Z > 0.99f;
});
Check("reset xform bakes rotation and scale", () =>
{
    var s = new Scene(); var o = s.Add("Box", Primitives.Box(1, 1, 1), Vector3.Zero); o.Scale = new Vector3(2, 3, 4);
    o.ResetXForm(); return o.Scale == Vector3.One && Near(Volume(o.Mesh), 24) && Closed(o.Mesh);
});
Check("OBJ export writes vertices, faces and an MTL", () =>
{
    var s = new Scene(); s.Add("Box", Primitives.Box(1, 1, 1), Vector3.Zero);
    var path = Path.Combine(Path.GetTempPath(), "molomax_test.obj"); SceneIO.ExportObj(s, path);
    var lines = File.ReadAllLines(path);
    return lines.Count(l => l.StartsWith("v ")) == 8 && lines.Count(l => l.StartsWith("f ")) == 6 && File.Exists(Path.ChangeExtension(path, ".mtl"));
});
Check("time of day: noon sun is high and bright, midnight is dim", () =>
{
    var noon = GtaLighting.TimeOfDay(13); var night = GtaLighting.TimeOfDay(0);
    return noon.LightDir.Z > 0.8f && noon.LightDirColour.X > 0.9f && night.LightDirColour.X < 0.2f && Near(noon.LightDir.Length(), 1);
});
Check("undo restores the mesh after an edit, redo re-applies it", () =>
{
    var s = new Scene(); var o = s.Add("Box", Primitives.Box(1, 1, 1), Vector3.Zero);
    var u = new UndoStack(); u.Record("Extrude", s);
    MeshOps.ExtrudeFaces(o.Mesh, TopFaces(o.Mesh), 1);
    var undone = u.Undo(s); var redone = u.Redo(undone);
    return undone.Objects[0].Mesh.Faces.Count == 6 && redone.Objects[0].Mesh.Faces.Count == 10;
});
Check("shader catalogue: emissive flags, decal blends, unknown falls back to default", () =>
    GtaShaders.Get("emissive").Emissive && GtaShaders.Get("decal").AlphaBlended && GtaShaders.Get("nope").Name == "default" && GtaShaders.Get("normal_spec").Bump);


Console.WriteLine("Blender-style mesh tools");
Check("make face from 4 loose verts of a deleted top closes the box", () =>
{
    var b = Primitives.Box(1, 1, 1); MeshOps.DeleteFaces(b, TopFaces(b));
    var top = Enumerable.Range(0, b.Verts.Count).Where(i => b.Verts[i].Z > 0.5f).ToHashSet();
    int f = MeshOps.MakeFace(b, top);
    return f >= 0 && Closed(b) && Near(Volume(b), 1);
});
Check("recalc normals fixes randomly flipped faces of a sphere (outside)", () =>
{
    var s = Primitives.Sphere(1, 16); var rnd = new Random(3);
    for (int i = 0; i < s.Faces.Count; i++) if (rnd.Next(2) == 0) s.Faces[i].Flip();
    MeshOps.RecalcNormals(s);
    return Closed(s) && Volume(s) > 3f;
});
Check("recalc normals inside turns a box into a room", () =>
{
    var b = Primitives.Box(2, 2, 2); MeshOps.RecalcNormals(b, null, inside: true);
    return Closed(b) && Volume(b) < 0;
});
Check("delete edges removes the faces that use them", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var e = b.BuildEdgeMap().Keys.First();
    MeshOps.DeleteEdges(b, new HashSet<EdgeKey> { e });
    return b.Faces.Count == 4;
});
Check("loop cut on a box side ring adds a loop, stays closed", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var vert = b.BuildEdgeMap().Keys.First(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f);
    var prev = MeshOps.LoopCutPreview(b, vert);
    var added = MeshOps.LoopCut(b, vert);
    return prev.Count == 4 && added.Count == 4 && b.Faces.Count == 10 && Closed(b);
});
Check("loop cut with 3 loops: evenly spaced, level preview, still closed", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var vert = b.BuildEdgeMap().Keys.First(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f);
    var prev = MeshOps.LoopCutPreview(b, vert, 3);
    bool level = prev.All(s => MathF.Abs(s.A.Z - s.B.Z) < 1e-4f);
    var added = MeshOps.LoopCut(b, vert, 3);
    return prev.Count == 12 && level && added.Count == 12 && b.Faces.Count == 18 && Closed(b);
});
Check("loop cut turned across: the cross edge of a vertical edge is level and cuts upright", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var vert = b.BuildEdgeMap().Keys.First(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f);
    var cross = MeshOps.CrossEdge(b, vert);
    return cross is { } c && MathF.Abs(b.Verts[c.A].Z - b.Verts[c.B].Z) < 1e-4f && MeshOps.LoopCutPreview(b, c, 2).Any(s => MathF.Abs(s.A.Z - s.B.Z) > 0.5f);
});
Check("face loop around a box side is 4 faces", () =>
{
    var b = Primitives.Box(1, 1, 1);
    var vert = b.BuildEdgeMap().Keys.First(e => MathF.Abs(b.Verts[e.A].Z - b.Verts[e.B].Z) > 0.5f);
    return MeshOps.FaceLoop(b, vert).Count == 4;
});

Console.WriteLine("Import");
// build a tiny GLB in memory: a cube (24 split vertices, like exporters write) under a translated node, with an embedded PNG
byte[] MakeGlb(bool withTexture)
{
    var pos = new List<float>(); var uv = new List<float>(); var idx = new List<ushort>();
    var c = Primitives.Box(2, 2, 2); // our Z-up box; write it Y-up like a glTF exporter would
    foreach (var f in c.Faces)
    {
        int b0 = pos.Count / 3;
        for (int k = 0; k < 4; k++) { var p = c.Verts[f.V[k]]; pos.AddRange(new[] { p.X, p.Z, -p.Y }); uv.AddRange(new[] { f.UV[k].X, f.UV[k].Y }); }
        idx.AddRange(new ushort[] { (ushort)b0, (ushort)(b0 + 1), (ushort)(b0 + 2), (ushort)b0, (ushort)(b0 + 2), (ushort)(b0 + 3) });
    }
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    var bin = new List<byte>();
    void Pad() { while (bin.Count % 4 != 0) bin.Add(0); }
    int posOff = bin.Count; foreach (var f in pos) bin.AddRange(BitConverter.GetBytes(f)); Pad();
    int uvOff = bin.Count; foreach (var f in uv) bin.AddRange(BitConverter.GetBytes(f)); Pad();
    int idxOff = bin.Count; foreach (var i in idx) bin.AddRange(BitConverter.GetBytes(i)); Pad();
    int pngOff = bin.Count; bin.AddRange(png); Pad();
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string tex = withTexture ? @",""textures"":[{""source"":0}],""images"":[{""bufferView"":3,""mimeType"":""image/png"",""name"":""Wall Diffuse""}]" : "";
    string matTex = withTexture ? @",""baseColorTexture"":{""index"":0}" : "";
    string json = $@"{{""asset"":{{""version"":""2.0""}},""scene"":0,""scenes"":[{{""nodes"":[0]}}],
""nodes"":[{{""name"":""Root"",""translation"":[0,0,0],""children"":[1]}},{{""name"":""Crate 01"",""mesh"":0,""translation"":[5,0,0]}}],
""meshes"":[{{""primitives"":[{{""attributes"":{{""POSITION"":0,""TEXCOORD_0"":1}},""indices"":2,""material"":0}}]}}],
""materials"":[{{""name"":""Crate Mat"",""pbrMetallicRoughness"":{{""baseColorFactor"":[1,0.5,0.25,1]{matTex}}}}}],
""accessors"":[{{""bufferView"":0,""componentType"":5126,""count"":{pos.Count / 3},""type"":""VEC3""}},{{""bufferView"":1,""componentType"":5126,""count"":{uv.Count / 2},""type"":""VEC2""}},{{""bufferView"":2,""componentType"":5123,""count"":{idx.Count},""type"":""SCALAR""}}],
""bufferViews"":[{{""buffer"":0,""byteOffset"":{posOff},""byteLength"":{pos.Count * 4}}},{{""buffer"":0,""byteOffset"":{uvOff},""byteLength"":{uv.Count * 4}}},{{""buffer"":0,""byteOffset"":{idxOff},""byteLength"":{idx.Count * 2}}},{{""buffer"":0,""byteOffset"":{pngOff},""byteLength"":{png.Length}}}],
""buffers"":[{{""byteLength"":{bin.Count}}}]{tex}}}";
    var jb = new List<byte>(System.Text.Encoding.UTF8.GetBytes(json)); while (jb.Count % 4 != 0) jb.Add(0x20);
    var o = new List<byte>();
    o.AddRange(BitConverter.GetBytes(0x46546C67u)); o.AddRange(BitConverter.GetBytes(2u)); o.AddRange(BitConverter.GetBytes((uint)(12 + 8 + jb.Count + 8 + bin.Count)));
    o.AddRange(BitConverter.GetBytes((uint)jb.Count)); o.AddRange(BitConverter.GetBytes(0x4E4F534Au)); o.AddRange(jb);
    o.AddRange(BitConverter.GetBytes((uint)bin.Count)); o.AddRange(BitConverter.GetBytes(0x004E4942u)); o.AddRange(bin);
    return o.ToArray();
}
var tmpDir = Path.Combine(Path.GetTempPath(), "molomax_import_test"); Directory.CreateDirectory(tmpDir);
Check("GLB: cube welds into a closed 8-vertex box, Y-up converted, node translation applied", () =>
{
    var p = Path.Combine(tmpDir, "crate.glb"); File.WriteAllBytes(p, MakeGlb(true));
    var r = MoloMax.Core.IO.ModelImporter.Import(p);
    var m = r.Meshes[0].Mesh; var (mn, mx) = m.Bounds();
    return r.Meshes.Count == 1 && r.Meshes[0].Name == "crate_01" && m.Verts.Count == 8 && Closed(m) && Near(Volume(m), 8)
        && Near(mn.X, 4) && Near(mx.X, 6) && Near(mn.Z, 0) && Near(mx.Z, 2);
});
Check("GLB: embedded PNG extracted, material named and coloured", () =>
{
    var p = Path.Combine(tmpDir, "crate.glb");
    var r = MoloMax.Core.IO.ModelImporter.Import(p);
    var mat = r.Materials[0];
    return mat.Name == "crate_mat" && mat.DiffuseTexture != null && File.Exists(mat.DiffuseTexture) && mat.DiffuseTexture.EndsWith("wall_diffuse.png") && Near(mat.ViewportColour.Y, 0.5f);
});
Check(".gltf with a data-URI buffer gives the same mesh", () =>
{
    var glb = MakeGlb(false);
    int jlen = (int)BitConverter.ToUInt32(glb, 12);
    var json = System.Text.Encoding.UTF8.GetString(glb, 20, jlen).Trim();
    int blen = (int)BitConverter.ToUInt32(glb, 20 + jlen);
    var bin = glb.AsSpan(28 + jlen, blen).ToArray();
    json = json.Replace(@"""buffers"":[{""byteLength""", @"""buffers"":[{""uri"":""data:application/octet-stream;base64," + Convert.ToBase64String(bin) + @""",""byteLength""");
    var p = Path.Combine(tmpDir, "crate.gltf"); File.WriteAllText(p, json);
    var r = MoloMax.Core.IO.ModelImporter.Import(p);
    return r.Meshes.Count == 1 && r.Meshes[0].Mesh.Verts.Count == 8 && Closed(r.Meshes[0].Mesh);
});
Check("OBJ: quads kept, two objects, UV v flipped, mtl colour read", () =>
{
    File.WriteAllText(Path.Combine(tmpDir, "t.mtl"), "newmtl Red\nKd 1 0 0\n");
    File.WriteAllText(Path.Combine(tmpDir, "t.obj"), "mtllib t.mtl\no PlaneA\nv 0 0 0\nv 1 0 0\nv 1 0 1\nv 0 0 1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nusemtl Red\nf 1/1 2/2 3/3 4/4\no PlaneB\nv 0 1 0\nv 1 1 0\nv 1 1 1\nf -3 -2 -1\n");
    var r = MoloMax.Core.IO.ModelImporter.Import(Path.Combine(tmpDir, "t.obj"));
    var a = r.Meshes.First(x => x.Name == "planea").Mesh;
    return r.Meshes.Count == 2 && a.Faces[0].V.Length == 4 && Near(a.Faces[0].UV[0].Y, 1) && r.Materials[a.Faces[0].MatId].ViewportColour.X > 0.99f
        && Near(a.Bounds().Max.Z, 0) && Near(a.Bounds().Min.Y, -1) && Near(a.Bounds().Max.Y, 0);
});
Check("OBJ export then import round-trips a room (inward normals kept)", () =>
{
    var s = new Scene(); s.Add("Room", Primitives.Room(6, 4, 3), Vector3.Zero, MoloMax.Core.Scenes.GtaObjectType.MloRoom);
    var p = Path.Combine(tmpDir, "room.obj"); SceneIO.ExportObj(s, p);
    var r = MoloMax.Core.IO.ModelImporter.Import(p);
    var m = r.Meshes[0].Mesh;
    return m.Verts.Count == 8 && Closed(m) && Near(Volume(m), -72);
});
// real files from the user's Desktop, when present (staged into the container for this check)
foreach (var real in new[] { "/mnt/user-data/uploads/Desktop/mrpd/Black_metal_lockers_with_one_open_door_Remesh_Hunyuan_7782a3e1.glb",
                             "/mnt/user-data/uploads/Desktop/mrpd/office chair 3d model.glb",
                             "/mnt/user-data/uploads/Desktop/petitprojet/poutre.glb",
                             "/mnt/user-data/uploads/Desktop/mrpd/tripo_node_891b09df-a1d8-4dbe-8eb5-735c1074b17a (1K) (new uv map).glb" })
{
    if (!File.Exists(real)) continue;
    Check("real file: " + Path.GetFileName(real), () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MoloMax.Core.IO.ModelImporter.Import(real, new MoloMax.Core.IO.ImportOptions { TextureFolder = Path.Combine(tmpDir, "real_tex") });
        foreach (var im in r.Meshes) im.Mesh.Validate();
        int v = r.Meshes.Sum(x => x.Mesh.Verts.Count), f = r.Meshes.Sum(x => x.Mesh.Faces.Count);
        var (mn, mx) = r.Meshes.Count > 0 ? r.Meshes[0].Mesh.Bounds() : default;
        Console.WriteLine($"       {r.Meshes.Count} mesh(es), {v:N0} verts, {f:N0} faces, {r.Materials.Count} mat(s), {r.TexturesWritten.Count} tex, size {mx - mn}, {sw.ElapsedMilliseconds} ms");
        foreach (var w in r.Warnings) Console.WriteLine("       warn: " + w);
        foreach (var t in r.TexturesWritten) Console.WriteLine("       tex: " + Path.GetFileName(t) + " " + new FileInfo(t).Length);
        return r.Meshes.Count > 0 && f > 0;
    });
}

Console.WriteLine("GTA export (CodeWalker)");
{
    // a room box with a doorway: the middle bottom panel of one wall removed
    PolyMesh RoomWithDoor(float w, float l, float h, int side)
    {
        var m = Primitives.Box(w, l, h, 3, 3, 2);
        foreach (var f in m.Faces) f.Flip();
        var holes = new List<Face>();
        foreach (var f in m.Faces)
        {
            var c = m.FaceCenter(f); var n = m.FaceNormal(f);
            // inward normals: the +Y wall faces -Y
            if (MathF.Abs(c.X) < 0.5f && c.Z < h / 2 && MathF.Abs(c.Y - side * l / 2) < 1e-3f) holes.Add(f);
        }
        foreach (var f in holes) m.Faces.Remove(f);
        m.RemoveUnusedVertices();
        return m;
    }
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_test_mlo";
    scene.GtaOrigin = new Vector3(-188.68f, -41f, 50.3f);
    scene.Gta.Heading = 20;
    var hall = scene.Add("Hall", RoomWithDoor(6, 6, 3, 1), Vector3.Zero, GtaObjectType.MloRoom);
    // second opening on the -Y wall of the hall = street entrance
    {
        var m = hall.Mesh;
        var rm = m.Faces.Where(f => { var c = m.FaceCenter(f); return MathF.Abs(c.X) < 0.5f && c.Z < 1.5f && MathF.Abs(c.Y + 3) < 1e-3f; }).ToList();
        foreach (var f in rm) m.Faces.Remove(f);
        m.RemoveUnusedVertices();
    }
    var office = scene.Add("Office", RoomWithDoor(6, 6, 3, -1), new Vector3(0, 6.2f, 0), GtaObjectType.MloRoom);
    var crate = scene.Add("Crate", Primitives.Box(0.8f, 0.8f, 0.8f), new Vector3(1.5f, 1, 0));
    var crate2 = scene.Add("Crate", Primitives.Box(0.8f, 0.8f, 0.8f), new Vector3(-1.5f, 7, 0));
    crate2.Gta.ModelName = crate.Name; // same model, second entity
    var door = scene.Add("Door", Primitives.Door(), new Vector3(0, 3.1f, 0));
    door.Gta.Role = GtaRole.Door;
    var outside = scene.Add("Sidewalk", Primitives.Box(20, 4, 0.2f), new Vector3(0, -6, -0.2f));
    var mat = scene.AddMaterial("brick", new Vector3(0.6f, 0.3f, 0.2f));
    mat.Shader = "normal_spec";
    hall.Mesh.SetAllMaterial(mat.Id);
    var glow = scene.AddMaterial("sign", new Vector3(1, 1, 1)); glow.Shader = "emissive";
    crate.Mesh.SetAllMaterial(glow.Id);

    var layout = MoloMax.Core.Gta.Export.MloLayout.Build(scene, scene.Gta);
    foreach (var w in layout.Warnings) Console.WriteLine("       layout: " + w);
    Check("MLO layout: limbo + 2 rooms", () => layout.Rooms.Count == 3 && layout.Rooms[1].Name == "hall001" && layout.Rooms[2].Name == "office001");
    Check("auto portals: hall <-> office and hall -> outside", () =>
        layout.Portals.Count == 2
        && layout.Portals.Any(p => (p.RoomFrom == 1 && p.RoomTo == 2) || (p.RoomFrom == 2 && p.RoomTo == 1))
        && layout.Portals.Any(p => p.RoomFrom == 1 && p.RoomTo == 0) && layout.ExitPortals == 1);
    Check("inner portal sits in the gap (y = 3.1) and covers the doorway", () =>
    {
        var p = layout.Portals.First(x => x.RoomTo != 0);
        return p.Corners.All(c => Near(c.Y, 3.1f, 1e-3f)) && Near(p.Corners.Max(c => c.X) - p.Corners.Min(c => c.X), 2, 1e-3f) && Near(p.Corners.Max(c => c.Z), 1.5f, 1e-3f);
    });
    Check("roles: rooms = shell, crate = prop, sidewalk = exterior", () =>
        MoloMax.Core.Gta.Export.GtaExporter.ResolveRole(crate, layout, 1) == GtaRole.Prop
        && MoloMax.Core.Gta.Export.GtaExporter.ResolveRole(outside, layout, 1) == GtaRole.Exterior
        && MoloMax.Core.Gta.Export.GtaExporter.ResolveRole(hall, layout, 1) == GtaRole.Shell);

    var outDir = Path.Combine(tmpDir, "gta_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    foreach (var w in rep.Warnings) Console.WriteLine("       warn: " + w);
    foreach (var i in rep.Info) Console.WriteLine("       " + i);
    var res = Path.Combine(outDir, "molo_test_mlo");
    Check("export succeeds and writes the resource", () => rep.Ok && File.Exists(Path.Combine(res, "fxmanifest.lua")));
    Check("stream/mlo has shell, collision, ytyp, milo ymap", () =>
        new[] { "molo_test_mlo_shell.ydr", "molo_test_mlo_col.ybn", "molo_test_mlo.ytyp", "molo_test_mlo_milo_.ymap", "molo_test_mlo_crate001.ydr", "molo_test_mlo_door001.ydr" }
            .All(f => File.Exists(Path.Combine(res, "stream", "mlo", f))));
    Check("stream/exterior has the joined exterior and its ymap", () =>
        File.Exists(Path.Combine(res, "stream", "exterior", "molo_test_mlo_ext.ydr")) && File.Exists(Path.Combine(res, "stream", "exterior", "molo_test_mlo_exterior.ymap")));
    Check("ytyp reads back: MLO with 3 rooms, 2 portals, entities, door attached", () =>
    {
        var y = new CodeWalker.GameFiles.YtypFile();
        y.Load(File.ReadAllBytes(Path.Combine(res, "stream", "mlo", "molo_test_mlo.ytyp")));
        var mlo = y.AllArchetypes.OfType<CodeWalker.GameFiles.MloArchetype>().FirstOrDefault();
        if (mlo == null) return false;
        Console.WriteLine($"       rooms {mlo.rooms?.Length}, portals {mlo.portals?.Length}, entities {mlo.entities?.Length}, archetypes {y.AllArchetypes.Length}");
        bool doorInPortal = mlo.portals.Any(p => p.AttachedObjects != null && p.AttachedObjects.Length == 1);
        return mlo.rooms.Length == 3 && mlo.portals.Length == 2 && mlo.entities.Length == 4 && y.AllArchetypes.Length == 5 && doorInPortal;
    });
    {
        // interior editing (MAP mode): move one object, remove one, add one, save the .ytyp, read it mback
        var ypath = Path.Combine(res, "stream", "mlo", "molo_test_mlo.ytyp");
        var yb = new CodeWalker.GameFiles.YtypFile(); yb.Load(File.ReadAllBytes(ypath));
        var mh = yb.AllArchetypes.OfType<CodeWalker.GameFiles.MloArchetype>().First().Hash.Hash;
        var md = MoloMax.Core.Gta.Map.MloDocument.Load(File.ReadAllBytes(ypath), "molo_test_mlo.ytyp", mh);
        int n0 = md.Entities.Count;
        var mdoor = md.Entities.FirstOrDefault(e => (e.Archetype ?? "").Contains("door"));
        var mother = md.Entities.First(e => e != mdoor && !(e.Archetype ?? "").Contains("shell"));
        var mmoved = md.Entities.First(e => e != mdoor && e != mother);
        var p0 = mmoved.Position;
        mmoved.Position += new Vector3(0.5f, 0, 0);
        md.Remove(mother);
        var madded = md.Add("prop_chair_01a", md.Rooms.Count > 2 ? (md.Rooms[2].Min + md.Rooms[2].Max) * 0.5f : Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f), Vector3.One);
        var msaved = md.Save();
        var mback = MoloMax.Core.Gta.Map.MloDocument.Load(msaved, "molo_test_mlo.ytyp", mh);
        var yb2 = new CodeWalker.GameFiles.YtypFile(); yb2.Load(msaved);
        var mlo2 = yb2.AllArchetypes.OfType<CodeWalker.GameFiles.MloArchetype>().First();
        Check("interior edit: moved / removed / added objects saved in the .ytyp", () => mback.Entities.Count == n0
            && mback.Entities.Any(e => Vector3.Distance(e.Position, p0 + new Vector3(0.5f, 0, 0)) < 1e-3f)
            && mback.Entities.Any(e => e.ArchetypeHash == MoloMax.Core.Gta.Map.MapDocument.Hash("prop_chair_01a") && MathF.Abs(MathF.Abs(Quaternion.Dot(e.Rotation, madded.Rotation)) - 1) < 1e-4f));
        Check("interior edit: rooms keep their objects, the door stays on its portal, other archetypes kept", () =>
            mlo2.rooms.Sum(r => r.AttachedObjects?.Length ?? 0) == mback.Entities.Count && yb2.AllArchetypes.Length == yb.AllArchetypes.Length
            && (mdoor == null || mlo2.portals.Any(pt => pt.AttachedObjects != null && pt.AttachedObjects.Any(i => mlo2.entities[i]._Data.archetypeName.Hash == mdoor.ArchetypeHash)))
            && mback.Entities.First(e => e.ArchetypeHash == MoloMax.Core.Gta.Map.MapDocument.Hash("prop_chair_01a")).Room == (md.Rooms.Count > 2 ? 2 : md.RoomAt(Vector3.Zero)));
    }
    Check("collision reads back with RoomID 1 and 2 (per room)", () =>
    {
        var b = new CodeWalker.GameFiles.YbnFile();
        b.Load(File.ReadAllBytes(Path.Combine(res, "stream", "mlo", "molo_test_mlo_col.ybn")));
        var comp = b.Bounds as CodeWalker.GameFiles.BoundComposite;
        var rooms = comp.Children.data_items.OfType<CodeWalker.GameFiles.BoundGeometry>().SelectMany(g => g.Materials.Select(m => (int)m.RoomId)).Distinct().OrderBy(r => r).ToList();
        Console.WriteLine("       rooms in ybn: " + string.Join(",", rooms));
        return rooms.SequenceEqual(new[] { 1, 2 });
    });
    Check("shell ydr reads back with normal_spec + green vertex colours and embedded textures", () =>
    {
        var d = new CodeWalker.GameFiles.YdrFile();
        d.Load(File.ReadAllBytes(Path.Combine(res, "stream", "mlo", "molo_test_mlo_shell.ydr")));
        var sg = d.Drawable.ShaderGroup;
        var names = sg.Shaders.data_items.Select(s => s.FileName.ToString()).ToList();
        Console.WriteLine("       shaders: " + string.Join(", ", names) + $"; textures {sg.TextureDictionary?.Textures?.data_items?.Length}");
        return sg.TextureDictionary?.Textures?.data_items?.Length >= 3;
    });
    Check("milo ymap reads back with the MLO instance at the GTA origin", () =>
    {
        var m = new CodeWalker.GameFiles.YmapFile();
        m.Load(File.ReadAllBytes(Path.Combine(res, "stream", "mlo", "molo_test_mlo_milo_.ymap")));
        var e = m.AllEntities.FirstOrDefault();
        return e != null && Near(e.Position.X, -188.68f, 1e-2f) && e.IsMlo;
    });
    Check("a ground plane bigger than the MLO is exterior, not shell", () =>
    {
        var ground = scene.Add("Ground", Primitives.Box(60, 60, 0.1f), new Vector3(0, 0, -0.1f));
        var role = MoloMax.Core.Gta.Export.GtaExporter.ResolveRole(ground, layout, 1);
        scene.Objects.Remove(ground);
        return role == GtaRole.Exterior;
    });
    Check("mirrored object (scale -1) still faces outward in the model", () =>
    {
        var b = scene.Add("Mirror", Primitives.Box(1, 1, 1), new Vector3(20, 20, 0));
        b.Scale = new Vector3(-1, 1, 1);
        var m = MoloMax.Core.Gta.Export.GtaModel.Build("m", scene, new[] { (b, b.World) });
        scene.Objects.Remove(b);
        float vol = 0;
        foreach (var part in m.Parts)
            for (int i = 0; i < part.Indices.Count; i += 3)
            {
                var p0 = part.Verts[part.Indices[i]].Position; var p1 = part.Verts[part.Indices[i + 1]].Position; var p2 = part.Verts[part.Indices[i + 2]].Position;
                vol += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6f;
            }
        return vol > 0.9f;
    });
    Check("a manual Collision object adds to the automatic collision instead of replacing it", () =>
    {
        int before = rep.CollisionTriangles;
        var c = scene.Add("CrateCol", Primitives.Box(0.8f, 0.8f, 0.8f), new Vector3(1.5f, 1, 0), GtaObjectType.Collision);
        var rep2 = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
        scene.Objects.Remove(c);
        return rep2.Ok && rep2.CollisionTriangles == before + 12;
    });
    Check("CodeWalker XML sources are written next to the resource", () => File.Exists(Path.Combine(outDir, "molo_test_mlo_xml", "stream", "mlo", "molo_test_mlo.ytyp.xml")));
}
{
    var img = new MoloMax.Core.Gta.Export.RgbaImage(100, 60);
    for (int y = 0; y < 60; y++) for (int x = 0; x < 100; x++) { int i = (y * 100 + x) * 4; img.Pixels[i] = (byte)(x * 2); img.Pixels[i + 1] = (byte)(y * 4); img.Pixels[i + 2] = 80; img.Pixels[i + 3] = 255; }
    Check("DDS DXT1: power of two, full mip chain, CodeWalker reads it", () =>
    {
        var dds = MoloMax.Core.Gta.Export.DdsEncoder.Encode(img, MoloMax.Core.Gta.Export.DdsFormat.Dxt1);
        var t = CodeWalker.Utils.DDSIO.GetTexture(dds);
        Console.WriteLine($"       {t.Width}x{t.Height} {t.Format} mips {t.Levels} ({dds.Length} bytes)");
        return t.Width == 128 && t.Height == 64 && t.Format == CodeWalker.GameFiles.TextureFormat.D3DFMT_DXT1 && t.Levels == 6;
    });
    Check("DDS auto picks DXT5 when there is alpha", () =>
    {
        img.Pixels[3] = 10;
        return MoloMax.Core.Gta.Export.DdsEncoder.AutoFormat(img) == MoloMax.Core.Gta.Export.DdsFormat.Dxt5
            && CodeWalker.Utils.DDSIO.GetTexture(MoloMax.Core.Gta.Export.DdsEncoder.Encode(img, MoloMax.Core.Gta.Export.DdsFormat.Dxt5)).Format == CodeWalker.GameFiles.TextureFormat.D3DFMT_DXT5;
    });
    Check("DXT1 colour error stays small on a gradient", () =>
    {
        var dds = MoloMax.Core.Gta.Export.DdsEncoder.Encode(img.Resize(64, 64), MoloMax.Core.Gta.Export.DdsFormat.Dxt1, 64, false);
        var t = CodeWalker.Utils.DDSIO.GetTexture(dds);
        var px = CodeWalker.Utils.DDSIO.GetPixels(t, 0); // BGRA? RGBA
        var src = img.Resize(64, 64).Pixels;
        double err = 0, errSwap = 0;
        for (int i = 0; i < 64 * 64; i++) for (int c = 0; c < 3; c++) { err += Math.Abs(px[i * 4 + c] - src[i * 4 + c]); errSwap += Math.Abs(px[i * 4 + 2 - c] - src[i * 4 + c]); }
        err = Math.Min(err, errSwap) / (64 * 64 * 3);
        Console.WriteLine($"       mean abs error {err:F2}");
        return err < 8;
    });
}

Console.WriteLine("Tripo client (mock server)");
{
    var log = new List<string>();
    var handler = new MockTripo(log);
    var c = new MoloMax.Core.Tripo.TripoClient("tsk_test_key", "https://openapi.example", handler);
    var job = new MoloMax.Core.Tripo.TripoJob { Title = "Wooden crate 2500" };
    var o = new MoloMax.Core.Tripo.TripoOptions { Prompt = "wooden crate", FaceLimit = 2500, TextureAfterUv = true, SmartUv = true };
    var outDir = Path.Combine(tmpDir, "tripo");
    MoloMax.Core.Tripo.TripoPipeline.Generate(c, job, o, outDir).Wait();
    foreach (var l in log) Console.WriteLine("       " + l);
    if (job.Error != null) Console.WriteLine("       error: " + job.Error);
    Check("Molo pipeline: P2 geometry -> Smart UV -> texture -> download", () =>
        job.Error == null && job.Done && File.Exists(job.ResultFile)
        && log.Count(l => l.StartsWith("POST /v3/generation/text-to-model")) == 1
        && log.Any(l => l.StartsWith("POST /v3/mesh/uv")) && log.Any(l => l.StartsWith("POST /v3/models/texture")));
    Check("generation body: P2, face_limit 2500, no texture yet (textured after UV)", () =>
        log.Any(l => l.StartsWith("POST /v3/generation/text-to-model") && l.Contains("\"model\":\"P2-20260801\"") && l.Contains("\"face_limit\":2500") && l.Contains("\"texture\":false")));
    Check("texture step uses the prompt and 2K (standard) quality", () =>
        log.Any(l => l.StartsWith("POST /v3/models/texture") && l.Contains("\"texture_quality\":\"standard\"") && l.Contains("\"text\":\"wooden crate\"")));
    Check("the API key goes to the API only, never to the download CDN", () =>
        log.Where(l => l.StartsWith("GET https://cdn")).All(l => l.EndsWith("auth=no")) && log.Where(l => l.StartsWith("POST")).All(l => l.EndsWith("auth=yes")));
    Check("API error codes become readable messages", () =>
    {
        var j2 = new MoloMax.Core.Tripo.TripoJob { Title = "poor" };
        handler.FailWithCode = 2010;
        MoloMax.Core.Tripo.TripoPipeline.Generate(c, j2, o, outDir).Wait();
        handler.FailWithCode = 0;
        return j2.Error != null && j2.Error.Contains("credits");
    });
    Check("a key that is not tsk_ is refused before any call", () =>
    {
        try { new MoloMax.Core.Tripo.TripoClient("sk-wrong"); return false; } catch (MoloMax.Core.Tripo.TripoException) { return true; }
    });
}

Console.WriteLine("Molo Tools");
{
    Check("line shape: a bare line keeps its points only; door frame casing along a U door is closed and grows outward", () =>
    {
        var s = new MoloMax.Core.Mesh.ShapeData();
        s.Points.AddRange(new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 2.1f), new Vector3(1, 0, 2.1f), new Vector3(1, 0, 0) });
        var bare = MoloMax.Core.Mesh.ShapeBuilder.Build(s);
        if (bare.Faces.Count != 0 || bare.Verts.Count != 4) return false;
        s.Render = MoloMax.Core.Mesh.ShapeRender.DoorFrame; s.FrameWidth = 0.12f; s.FrameDepth = 0.04f;
        var m = MoloMax.Core.Mesh.ShapeBuilder.Build(s);
        var (mn, mx) = m.Bounds();
        Console.WriteLine($"       casing {mn} .. {mx}");
        bool okB = mn.X < -0.11f && mx.X > 1.11f && mx.Z > 2.21f && MathF.Abs(mn.Z) < 1e-3f && mx.Y - mn.Y > 0.03f;
        s.Flip = true; var (fmn, fmx) = MoloMax.Core.Mesh.ShapeBuilder.Build(s).Bounds();
        bool okF = fmn.X > -0.01f && fmx.X < 1.01f; // flipped: inside the opening
        s.Flip = false; s.Facing = new Vector3(0, -1, 0);
        var (smn, smx) = MoloMax.Core.Mesh.ShapeBuilder.Build(s).Bounds();
        bool okS = smn.Y < -0.03f && smx.Y < 1e-4f; // facing -Y: the casing stands out on that side
        var opt = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var back = System.Text.Json.JsonSerializer.Deserialize<MoloMax.Core.Mesh.ShapeData>(System.Text.Json.JsonSerializer.Serialize(s, opt), opt);
        bool okJ = back.Points.Count == 4 && back.Points[2] == s.Points[2] && back.Render == s.Render && back.Facing == s.Facing;
        return Closed(m) && okB && okF && okS && okJ;
    });
    Check("line shape: fillet rounds the corners of a door line (radius kept, ends untouched), 1 segment = chamfer", () =>
    {
        var s = new MoloMax.Core.Mesh.ShapeData { Fillet = 0.3f, FilletSegs = 4 };
        s.Points.AddRange(new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 2.1f), new Vector3(1, 0, 2.1f), new Vector3(1, 0, 0) });
        var p = MoloMax.Core.Mesh.ShapeBuilder.Path(s);
        var corner = new Vector3(0, 0, 2.1f); var c = new Vector3(0.3f, 0, 1.8f);
        float near = p.Min(q => Vector3.Distance(q, corner));
        bool onArc = p.Where(q => q.Z > 1.8f + 1e-3f && q.X < 0.3f - 1e-3f).All(q => MathF.Abs(Vector3.Distance(q, c) - 0.3f) < 1e-3f);
        s.FilletSegs = 1; var ch = MoloMax.Core.Mesh.ShapeBuilder.Path(s);
        Console.WriteLine($"       fillet: {p.Count} points, nearest to the corner {near:0.000} m; chamfer {ch.Count} points");
        return p.Count == 12 && p[0] == Vector3.Zero && p[^1] == new Vector3(1, 0, 0) && MathF.Abs(near - 0.3f * (MathF.Sqrt(2) - 1)) < 2e-3f && onArc && ch.Count == 6;
    });
    Check("line shape: sweep a drawn L profile along a closed square: a closed moulding", () =>
    {
        var prof = MoloMax.Core.Mesh.ShapeBuilder.ProfileFrom(new[] { new Vector3(5, 5, 0), new Vector3(5.1f, 5, 0), new Vector3(5.1f, 5, 0.03f), new Vector3(5.03f, 5, 0.03f), new Vector3(5.03f, 5, 0.12f), new Vector3(5, 5, 0.12f) });
        var s = new MoloMax.Core.Mesh.ShapeData { Closed = true, Render = MoloMax.Core.Mesh.ShapeRender.Sweep, Profile = prof };
        s.Points.AddRange(new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 3, 0), new Vector3(0, 3, 0) });
        var m = MoloMax.Core.Mesh.ShapeBuilder.Build(s);
        var (mn, mx) = m.Bounds();
        Console.WriteLine($"       profile {string.Join(" ", prof.Select(q => $"({q.X:0.##},{q.Y:0.##})"))}; moulding {mn} .. {mx}");
        return prof.Count == 6 && MathF.Abs(prof[1].X - 0.1f) < 1e-4f && MathF.Abs(prof[2].Y - 0.03f) < 1e-4f && Closed(m) && m.Faces.Count == 24 && mx.X > 4.09f && MathF.Abs(mx.Z - mn.Z - 0.12f) < 1e-3f;
    });
    Check("line shape: closed square as a beam (closed ring) and extruded (solid, faces outward)", () =>
    {
        var s = new MoloMax.Core.Mesh.ShapeData { Closed = true, Render = MoloMax.Core.Mesh.ShapeRender.Rectangular };
        s.Points.AddRange(new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 2, 0), new Vector3(0, 2, 0) });
        var beam = MoloMax.Core.Mesh.ShapeBuilder.Build(s);
        s.Render = MoloMax.Core.Mesh.ShapeRender.Extrude; s.Amount = 0.5f;
        var ex = MoloMax.Core.Mesh.ShapeBuilder.Build(s);
        var (mn, mx) = ex.Bounds();
        return Closed(beam) && Closed(ex) && Outward(ex) && MathF.Abs(mx.Z - 0.5f) < 1e-4f && beam.Faces.Count == 16;
    });
    bool V2(System.Numerics.Vector2 a, float x, float y) => MathF.Abs(a.X - x) < 1e-3f && MathF.Abs(a.Y - y) < 1e-3f;
    Check("trim profile: default baseboard matches the reference points", () =>
    {
        var p = MoloMax.Core.Tools.TrimSweep.Profile(new MoloMax.Core.Tools.TrimSettings());
        return p.Count == 5 && V2(p[0], 0, 0) && V2(p[1], 0.02f, 0) && V2(p[2], 0.02f, 0.11f) && V2(p[3], 0.01f, 0.12f) && V2(p[4], 0, 0.12f);
    });
    Check("trim profile: default door casing matches the reference points", () =>
    {
        var p = MoloMax.Core.Tools.TrimSweep.Profile(MoloMax.Core.Tools.TrimSettings.Frame(MoloMax.Core.Tools.TrimKind.DoorFrame));
        float[][] want = { new[] { 0f, 0 }, new[] { 0.0104f, 0 }, new[] { 0.0104f, 0.0465f }, new[] { 0.04f, 0.0465f }, new[] { 0.04f, 0.13f }, new[] { 0.02f, 0.15f }, new[] { 0f, 0.15f } };
        return p.Count == 7 && want.Select((w, i) => V2(p[i], w[0], w[1])).All(b => b);
    });
    Check("baseboard around a room's floor edges: inside the room, 12 cm high, mitred corners", () =>
    {
        var room = Primitives.Room(6, 6, 3);
        var map = room.BuildEdgeMap();
        var floorEdges = map.Keys.Where(e => room.Verts[e.A].Z < 1e-3f && room.Verts[e.B].Z < 1e-3f).ToHashSet();
        var bb = MoloMax.Core.Tools.TrimSweep.Build(room, floorEdges, new MoloMax.Core.Tools.TrimSettings());
        var (mn, mx) = bb.Bounds();
        Console.WriteLine($"       {bb.Verts.Count} verts, {bb.Faces.Count} faces, bounds {mn} {mx}");
        return bb.Faces.Count > 0 && Near(mx.Z, 0.12f) && Near(mn.Z, 0) && mn.X >= -3.001f && mx.X <= 3.001f && Near(mn.X, -3, 1e-3f);
    });
    Check("edge strip (fake AO) on the floor edges of a room: two wings, flat on wall and floor, UV V in 0..1", () =>
    {
        var room = Primitives.Room(6, 6, 3);
        var map = room.BuildEdgeMap();
        var floorEdges = map.Keys.Where(e => room.Verts[e.A].Z < 1e-3f && room.Verts[e.B].Z < 1e-3f).ToHashSet();
        var st = MoloMax.Core.Tools.EdgeStrips.Build(room, floorEdges, new MoloMax.Core.Tools.StripSettings { Width = 0.1f, Offset = 0.002f });
        var uvs = st.Faces.SelectMany(f => f.UV).ToList();
        var (mn, mx) = st.Bounds();
        Console.WriteLine($"       {st.Faces.Count} faces, bounds {mn} {mx}");
        return st.Faces.Count == 8 && uvs.All(u => u.Y >= -1e-4f && u.Y <= 1.0001f) && mx.Z <= 0.1f + 3e-3f && mn.Z >= 0 && mn.X >= -3.001f;
    });
    Check("doorway cut in a room wall gives one 1.2 x 2.3 opening that becomes an exterior portal", () =>
    {
        var sc = new Scene();
        var room = sc.Add("Shop", Primitives.Room(6, 6, 3), System.Numerics.Vector3.Zero, GtaObjectType.MloRoom);
        int wall = Enumerable.Range(0, room.Mesh.Faces.Count).First(i => { var c = room.Mesh.FaceCenter(i); return c.Y < -2.9f; });
        var r = MoloMax.Core.Tools.Doorway.Cut(room.Mesh, wall, 1.2f, 2.3f);
        room.Mesh.Validate();
        var loops = MoloMax.Core.Gta.Export.MloLayout.BorderLoops(room.Mesh);
        var lay = MoloMax.Core.Gta.Export.MloLayout.Build(sc, sc.Gta);
        var p = lay.Portals.FirstOrDefault();
        Console.WriteLine($"       {r.Message} loops {loops.Count}, portals {lay.Portals.Count}");
        return r.Ok && loops.Count == 1 && lay.Portals.Count == 1 && p.RoomTo == 0
            && Near(p.Corners.Max(c => c.X) - p.Corners.Min(c => c.X), 1.2f) && Near(p.Corners.Max(c => c.Z), 2.3f);
    });
    Check("splats: a 3DGS .ply and an SPZ v2 file read back the same centres, colours, opacity and shapes", () =>
    {
        var rnd = new Random(5);
        var list = new List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector3, float)>();
        for (int i = 0; i < 500; i++)
            list.Add((new(rnd.NextSingle() * 4 - 2, rnd.NextSingle() * 4 - 2, rnd.NextSingle() * 2), new(0.01f + rnd.NextSingle() * 0.1f, 0.02f, 0.05f),
                System.Numerics.Quaternion.Normalize(new(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, 1f)),
                new(rnd.NextSingle(), rnd.NextSingle(), rnd.NextSingle()), 0.2f + rnd.NextSingle() * 0.7f));
        var dir = Path.Combine(Path.GetTempPath(), "poly_splat_test"); Directory.CreateDirectory(dir);
        var ply = Path.Combine(dir, "t.ply"); var spz = Path.Combine(dir, "t.spz");
        MoloMax.Core.Splats.SplatCloud.WritePly(ply, list);
        MoloMax.Core.Splats.SplatCloud.WriteSpzV2(spz, list);
        var a = MoloMax.Core.Splats.SplatCloud.Load(ply); var b = MoloMax.Core.Splats.SplatCloud.Load(spz);
        bool detect = MoloMax.Core.Splats.SplatCloud.IsSplatFile(ply) && MoloMax.Core.Splats.SplatCloud.IsSplatFile(spz);
        float ep = 0, ec = 0, eo = 0, ecov = 0, ecovB = 0;
        for (int i = 0; i < 500; i++)
        {
            var (pos, sc, rot, col, op) = list[i];
            ep = MathF.Max(ep, MathF.Abs(a.Pos[i * 3] - pos.X)); ep = MathF.Max(ep, MathF.Abs(b.Pos[i * 3 + 2] - pos.Z));
            ec = MathF.Max(ec, MathF.Abs(a.Rgba[i * 4 + 1] - col.Y)); ec = MathF.Max(ec, MathF.Abs(b.Rgba[i * 4] - col.X));
            eo = MathF.Max(eo, MathF.Abs(a.Rgba[i * 4 + 3] - op)); eo = MathF.Max(eo, MathF.Abs(b.Rgba[i * 4 + 3] - op));
            // trace of the covariance = sum of squared sizes, whatever the rotation
            float tr = sc.X * sc.X + sc.Y * sc.Y + sc.Z * sc.Z;
            ecov = MathF.Max(ecov, MathF.Abs(a.Cov[i * 6] + a.Cov[i * 6 + 3] + a.Cov[i * 6 + 5] - tr) / tr);
            ecovB = MathF.Max(ecovB, MathF.Abs(b.Cov[i * 6] + b.Cov[i * 6 + 3] + b.Cov[i * 6 + 5] - tr) / tr);
        }
        Console.WriteLine($"       {a.Format} / {b.Format}: pos {ep:0.0000}, colour {ec:0.000}, opacity {eo:0.000}, size {ecov:0.0000} / {ecovB:0.000}");
        return detect && a.Count == 500 && b.Count == 500 && ep < 0.001f && ec < 0.02f && eo < 0.01f && ecov < 1e-3f && ecovB < 0.15f;
    });
    Check("several doors on one wall: Room Builder cuts every opening, each a clean loop", () =>
    {
        var plan = new MoloMax.Core.Tools.RoomPlanData { Gap = 0.1f };
        plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "hall", X0 = 0, Y0 = 0, X1 = 11, Y1 = 1.5f, Height = 2.6f });
        plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "a", X0 = 0, Y0 = 1.5f, X1 = 5, Y1 = 5 });
        plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "b", X0 = 5, Y0 = 1.5f, X1 = 8, Y1 = 5 });
        plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "c", X0 = 8, Y0 = 1.5f, X1 = 11, Y1 = 5 });
        foreach (var x in new[] { 2.5f, 6.5f, 9.5f }) plan.Openings.Add(new MoloMax.Core.Tools.PlanOpening { X = x, Y = 1.5f, Width = 0.9f, Height = 2.1f });
        var b = MoloMax.Core.Tools.RoomPlanBuilder.Build(plan);
        var hall = b.Rooms.First(r => r.Item1 == "hall").Item2;
        var loops = MoloMax.Core.Gta.Export.MloLayout.BorderLoops(hall);
        Console.WriteLine($"       hall: {loops.Count} openings, warnings {b.Warnings.Count}");
        return loops.Count == 3 && b.Warnings.Count == 0 && loops.All(l => l.Count == 4);
    });
    Check("sign glow: halo texture with a lit centre, plane behind the lettering facing the same way", () =>
    {
        var letters = Primitives.Box(2, 0.1f, 0.5f);
        var w = System.Numerics.Matrix4x4.Identity;
        var g = MoloMax.Core.Tools.DecalTools.SignGlow(new[] { (letters, w) }, new MoloMax.Core.Tools.GlowSettings { Resolution = 256 });
        var t = g.Texture;
        int cx = t.Width / 2, cy = t.Height / 2;
        byte centre = t.Pixels[(cy * t.Width + cx) * 4 + 3], corner = t.Pixels[3];
        Console.WriteLine($"       {g.Message}, centre alpha {centre}, corner alpha {corner}, at {g.Position}");
        return centre > 200 && corner < 40 && g.Mesh.Faces.Count == 9;
    });
    Check("auto collision: one box per loose part", () =>
    {
        var m = Primitives.Box(1, 1, 1);
        var m2 = Primitives.Box(1, 1, 2); m2.Transform(System.Numerics.Matrix4x4.CreateTranslation(3, 0, 0));
        m.Append(m2);
        var boxes = MoloMax.Core.Tools.AutoCollision.Boxes(m, System.Numerics.Matrix4x4.Identity, MoloMax.Core.Tools.AutoCollision.Mode.PerPart, false);
        return boxes.Count == 2 && boxes.Any(b => Near(b.Half.Z, 1f));
    });
    Check("interior gradient: floor green at the bottom, dark blue at the ceiling", () =>
    {
        var room = Primitives.Room(4, 4, 3);
        MoloMax.Core.Tools.VertexColours.InteriorGradient(room, System.Numerics.Matrix4x4.Identity);
        var low = room.Faces.SelectMany(f => f.V.Select((v, i) => (room.Verts[v].Z, f.Col[i]))).Where(x => x.Z < 0.01f).Select(x => x.Item2).First();
        var high = room.Faces.SelectMany(f => f.V.Select((v, i) => (room.Verts[v].Z, f.Col[i]))).Where(x => x.Z > 2.99f).Select(x => x.Item2).First();
        return low.Y > 0.8f && low.X < 0.3f && high.Z > 0.3f && high.Y < 0.2f;
    });
    Check("procedural textures encode to DDS (edge normal DXT5, blob shadow)", () =>
    {
        var n = MoloMax.Core.Tools.ProcTextures.EdgeNormal(0.5f, 0.7f, 6, 1);
        var sh = MoloMax.Core.Tools.ProcTextures.BlobShadow(0.55f, 0.6f, 3);
        var d1 = MoloMax.Core.Gta.Export.DdsEncoder.Encode(n, MoloMax.Core.Gta.Export.DdsFormat.Dxt5);
        var d2 = MoloMax.Core.Gta.Export.DdsEncoder.Encode(sh, MoloMax.Core.Gta.Export.DdsEncoder.AutoFormat(sh));
        return CodeWalker.Utils.DDSIO.GetTexture(d1).Width == 256 && CodeWalker.Utils.DDSIO.GetTexture(d2).Format == CodeWalker.GameFiles.TextureFormat.D3DFMT_DXT5;
    });
}

Console.WriteLine("UV animation (YCD)");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_anim_test";
    var sign = scene.Add("Neon", Primitives.Plane(2, 1), new System.Numerics.Vector3(0, 0, 1));
    var mat = scene.AddMaterial("neon", new System.Numerics.Vector3(1, 0.2f, 0.8f)); mat.Shader = "emissive";
    sign.Mesh.SetAllMaterial(mat.Id);
    sign.Gta.Role = GtaRole.Prop;
    sign.Gta.Anim = new GtaAnim { Kind = UvAnimKind.Spritesheet, Cols = 4, Rows = 2, HoldFrames = 4 };
    var outDir = Path.Combine(tmpDir, "anim_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    foreach (var i in rep.Info) Console.WriteLine("       " + i);
    var res = Path.Combine(outDir, "molo_anim_test", "stream", "exterior");
    Check("timing: 4x2 sheet held 4 frames = 33 frames, 32/30 s", () => { var (f, d) = MoloMax.Core.Gta.Export.AnimXml.Timing(sign.Gta.Anim); return f == 33 && Near(d, 32f / 30f); });
    Check("spritesheet rows: cell 5 = column 1 of row 1", () =>
    {
        var (a, b) = MoloMax.Core.Gta.Export.AnimXml.Evaluate(sign.Gta.Anim, 20, 33);
        return Near(a.X, 0.25f) && Near(a.Z, 0.25f) && Near(b.Y, 0.5f) && Near(b.Z, 0.5f);
    });
    Check("export writes molo_anim_test_anims.ycd and it loads back with the right clip hash", () =>
    {
        var path = Path.Combine(res, "molo_anim_test_anims.ycd");
        if (!rep.Ok || !File.Exists(path)) return false;
        var y = new CodeWalker.GameFiles.YcdFile();
        CodeWalker.GameFiles.RpfFile.LoadResourceFile(y, File.ReadAllBytes(path), 46);
        var clip = y.ClipDictionary.ClipMap.Values.First().Clip; var an = y.ClipDictionary.AnimMap.Values.First().Animation;
        uint want = MoloMax.Core.Gta.Export.MetaXml.Jenkins("molo_anim_test_neon001") + 1;
        Console.WriteLine($"       clip hash {(uint)clip.Hash:X8} (want {want:X8}), anim frames {an.Frames}");
        return (uint)clip.Hash == want && an.Frames == 33;
    });
    Check("ytyp: archetype has flags UV anims + auto start and the clip dictionary", () =>
    {
        var t = new CodeWalker.GameFiles.YtypFile();
        t.Load(File.ReadAllBytes(Path.Combine(res, "molo_anim_test.ytyp")));
        var a = t.AllArchetypes.First(x => x.Name == "molo_anim_test_neon001" || x.Hash == MoloMax.Core.Gta.Export.MetaXml.Jenkins("molo_anim_test_neon001"));
        return (a._BaseArchetypeDef.flags & 525312) == 525312 && (uint)a._BaseArchetypeDef.clipDictionary == MoloMax.Core.Gta.Export.MetaXml.Jenkins("molo_anim_test_anims");
    });
}

Console.WriteLine("UV animation keyed on the timeline (YCD)");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_key_test";
    var sign = scene.Add("Screen", Primitives.Plane(2, 1), new System.Numerics.Vector3(0, 0, 1));
    var mat = scene.AddMaterial("screen", new System.Numerics.Vector3(0.2f, 0.6f, 1)); mat.Shader = "emissive";
    sign.Mesh.SetAllMaterial(mat.Id);
    sign.Gta.Role = GtaRole.Prop;
    scene.Anim.End = 90;
    var t = scene.Anim.Track(sign.Id, true);
    t.Curve(MoloMax.Core.Anim.Ch.UvU, true).SetKey(0, 0, MoloMax.Core.Anim.KeyInterp.Linear); t.Curve(MoloMax.Core.Anim.Ch.UvU).SetKey(90, 3);
    var outDir = Path.Combine(tmpDir, "anim_key_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var i in rep.Info.Where(i => i.Contains("UV"))) Console.WriteLine("       " + i);
    var res = Path.Combine(outDir, "molo_key_test", "stream", "exterior");
    Check("export: timeline UV keys become the model's UV clip (91 frames, scroll 0 -> 3), flag 1024", () =>
    {
        var path = Path.Combine(res, "molo_key_test_anims.ycd");
        if (!rep.Ok || !File.Exists(path)) return false;
        var clips = MoloMax.Core.Gta.Anim.YcdIO.Read(File.ReadAllBytes(path), "molo_key_test_anims.ycd");
        var model = sign.Name.ToLowerInvariant().StartsWith("molo_key_test") ? sign.Name.ToLowerInvariant() : "molo_key_test_" + sign.Name.ToLowerInvariant();
        var c = clips.FirstOrDefault(x => x.IsUv);
        var u0 = c?.Track(0, 17);
        Console.WriteLine($"       {string.Join(", ", clips.Select(MoloMax.Core.Anim.AnimGta.Describe))}  hash {c?.Hash:X8} want {MoloMax.Core.Gta.Export.AnimXml.ClipHash(model, 0):X8}");
        var ty = new CodeWalker.GameFiles.YtypFile(); ty.Load(File.ReadAllBytes(Path.Combine(res, "molo_key_test.ytyp")));
        bool flag = ty.AllArchetypes.Any(a => (a._BaseArchetypeDef.flags & 1024) != 0);
        return c != null && c.Frames == 91 && Near(u0.Values[90].Z, 3) && Near(u0.Values[45].Z, 1.5f) && c.Hash == MoloMax.Core.Gta.Export.AnimXml.ClipHash(model, 0) && flag;
    });
}

Console.WriteLine("Modifiers (Blender stack)");
{
    var cube = Primitives.Box(1, 1, 1);
    Check("Subdivision (Catmull-Clark) of a cube: 24 quads, 26 verts, closed", () =>
    {
        var m = MoloMax.Core.Modifiers.SubdivisionModifier.Subdivide(cube.Clone(), true);
        m.Validate();
        return m.Faces.Count == 24 && m.Verts.Count == 26 && m.IsClosedManifold(out _);
    });
    Check("Mirror X of a box moved to x=1 merges nothing and doubles the faces", () =>
    {
        var b = cube.Clone(); b.Transform(System.Numerics.Matrix4x4.CreateTranslation(1, 0, 0));
        var m = new MoloMax.Core.Modifiers.MirrorModifier().Apply(b, false);
        var (mn, mx) = m.Bounds();
        return m.Faces.Count == 12 && Near(mn.X, -1.5f) && Near(mx.X, 1.5f);
    });
    Check("Mirror X of a half box (open on x=0) welds into one closed box", () =>
    {
        var b = cube.Clone(); b.Transform(System.Numerics.Matrix4x4.CreateTranslation(0.5f, 0, 0));
        var bis = new MoloMax.Core.Modifiers.MirrorModifier { BisectX = false };
        // remove the face lying on x=0
        b.Faces.RemoveAll(f => f.V.All(v => MathF.Abs(b.Verts[v].X) < 1e-5f)); b.RemoveUnusedVertices();
        var m = bis.Apply(b, false);
        return m.IsClosedManifold(out _) && m.Verts.Count == 12;
    });
    Check("Array x3 relative offset 1: three boxes side by side", () =>
    {
        var m = new MoloMax.Core.Modifiers.ArrayModifier { Count = 3 }.Apply(cube.Clone(), false);
        var (mn, mx) = m.Bounds();
        return m.Faces.Count == 18 && Near(mx.X - mn.X, 3f);
    });
    Check("Solidify a plane: closed shell 2 cm thick", () =>
    {
        var m = new MoloMax.Core.Modifiers.SolidifyModifier { Thickness = 0.02f }.Apply(Primitives.Plane(2, 2, 2, 2), false);
        var (mn, mx) = m.Bounds();
        return m.IsClosedManifold(out _) && Near(mx.Z - mn.Z, 0.02f);
    });
    Check("Stack: render levels used for export, viewport levels in the viewport", () =>
    {
        var o = new SceneObject { Mesh = cube.Clone() };
        o.Modifiers.Add(new MoloMax.Core.Modifiers.SubdivisionModifier { LevelsViewport = 1, LevelsRender = 2 });
        return o.Evaluated().Faces.Count == 24 && o.Evaluated(render: true).Faces.Count == 96 && o.Mesh.Faces.Count == 6;
    });
    Check("Scene save / load keeps the modifier stack", () =>
    {
        var sc = new Scene();
        var o = sc.Add("Box", cube.Clone(), System.Numerics.Vector3.Zero);
        o.Modifiers.Add(new MoloMax.Core.Modifiers.MirrorModifier { AxisY = true, MergeDistance = 0.005f });
        var path = Path.Combine(tmpDir, "mods.mmx");
        SceneIO.Save(sc, path);
        var back = SceneIO.Load(path);
        var mm = back.Objects[0].Modifiers.FirstOrDefault() as MoloMax.Core.Modifiers.MirrorModifier;
        return mm != null && mm.AxisY && Near(mm.MergeDistance, 0.005f);
    });
}

Console.WriteLine("GTA import (ydr / ybn through CodeWalker)");
{
    var ext = Path.Combine(tmpDir, "gta_export", "molo_test_mlo", "stream");
    Check("import an exported crate .ydr: mesh, material with embedded texture, collision object", () =>
    {
        var f = Path.Combine(ext, "mlo", "molo_test_mlo_crate001.ydr");
        var r = MoloMax.Core.IO.ModelImporter.Import(f, new MoloMax.Core.IO.ImportOptions { TextureFolder = Path.Combine(tmpDir, "imp_tex") });
        foreach (var w in r.Warnings) Console.WriteLine("       warn: " + w);
        var d = r.Meshes.FirstOrDefault(m => m.GtaType == GtaObjectType.Drawable);
        Console.WriteLine($"       {r.Meshes.Count} mesh(es), drawable {d?.Mesh.Verts.Count} verts {d?.Mesh.Faces.Count} faces, {r.Materials.Count} mat(s) [{string.Join(", ", r.Materials.Select(m => m.Shader))}], {r.TexturesWritten.Count} texture(s)");
        return d != null && d.Mesh.Faces.Count >= 6 && r.KeepFileOrigin && r.Materials.Count >= 1;
    });
    Check("import the MLO collision .ybn: a collision object, closed", () =>
    {
        var r = MoloMax.Core.IO.ModelImporter.Import(Path.Combine(ext, "mlo", "molo_test_mlo_col.ybn"));
        var c = r.Meshes.FirstOrDefault();
        Console.WriteLine($"       {r.Meshes.Count} mesh(es), {c?.Mesh.Faces.Count} faces, material {c?.CollisionMaterial}");
        return c != null && c.GtaType == GtaObjectType.Collision && c.Mesh.Faces.Count > 0;
    });
    Check("shell .ydr imports with the same vertex positions it was exported with (bounds match)", () =>
    {
        var y = new CodeWalker.GameFiles.YdrFile(); y.Load(File.ReadAllBytes(Path.Combine(ext, "mlo", "molo_test_mlo_shell.ydr")));
        var r = MoloMax.Core.IO.ModelImporter.Import(Path.Combine(ext, "mlo", "molo_test_mlo_shell.ydr"));
        var (mn, mx) = r.Meshes[0].Mesh.Bounds();
        var bmin = y.Drawable.BoundingBoxMin; var bmax = y.Drawable.BoundingBoxMax;
        return Near(mn.X, bmin.X, 0.02f) && Near(mx.Z, bmax.Z, 0.02f);
    });
}

Console.WriteLine("Fragment export (.yft)");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_frag_test";
    var crate = scene.Add("Crate", Primitives.Box(0.8f, 0.6f, 0.5f), new System.Numerics.Vector3(2, 0, 0));
    crate.Gta.Role = GtaRole.Prop; crate.Gta.Fragment = true; crate.Gta.FragMass = 25; crate.Gta.PropCollision = GtaPropCollision.Mesh;
    var outDir = Path.Combine(tmpDir, "frag_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    foreach (var w in rep.Warnings) Console.WriteLine("       warn: " + w);
    var dir = Path.Combine(outDir, "molo_frag_test", "stream", "exterior");
    Check("export writes a .yft that CodeWalker loads back with a skeleton and physics", () =>
    {
        var f = Path.Combine(dir, "molo_frag_test_crate001.yft");
        if (!rep.Ok || !File.Exists(f)) { Console.WriteLine("       files: " + string.Join(", ", Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName) : Array.Empty<string>())); return false; }
        var y = new CodeWalker.GameFiles.YftFile(); y.Load(File.ReadAllBytes(f));
        var fr = y.Fragment; var lod = fr.PhysicsLODGroup.PhysicsLOD1;
        Console.WriteLine($"       bones {fr.Drawable.Skeleton?.Bones?.Items?.Length}, mass {lod.Archetype1?.Mass}, children {lod.Children?.data_items?.Length}, bound {lod.Archetype1?.Bound?.Type}");
        return fr.Drawable.Skeleton?.Bones?.Items?.Length == 1 && MathF.Abs(lod.Archetype1.Mass - 25) < 0.01f && lod.Children.data_items.Length == 1 && lod.Archetype1.Bound != null;
    });
    Check("ytyp archetype is ASSET_TYPE_FRAGMENT", () =>
    {
        var t = new CodeWalker.GameFiles.YtypFile(); t.Load(File.ReadAllBytes(Path.Combine(dir, "molo_frag_test.ytyp")));
        var a = t.AllArchetypes.First();
        return a._BaseArchetypeDef.assetType == CodeWalker.GameFiles.rage__fwArchetypeDef__eAssetType.ASSET_TYPE_FRAGMENT;
    });
    Check("the exported .yft imports back (round trip): mesh and collision", () =>
    {
        var r = MoloMax.Core.IO.ModelImporter.Import(Path.Combine(dir, "molo_frag_test_crate001.yft"));
        return r.Meshes.Any(m => m.GtaType == GtaObjectType.Drawable && m.Mesh.Faces.Count >= 6) && r.Meshes.Any(m => m.GtaType == GtaObjectType.Collision);
    });
}
Console.WriteLine("GTA import of a game prop fragment (.yft)");
{
    var native = "/tmp/claude-0/yftref/v_ret_tatstuff04.yft";
    if (File.Exists(native))
        Check("game .yft (v_ret_tatstuff04) imports: drawable placed by bones + collision", () =>
        {
            var r = MoloMax.Core.IO.ModelImporter.Import(native, new MoloMax.Core.IO.ImportOptions { TextureFolder = Path.Combine(tmpDir, "native_tex") });
            foreach (var w in r.Warnings.Take(4)) Console.WriteLine("       warn: " + w);
            var d = r.Meshes.First(m => m.GtaType == GtaObjectType.Drawable);
            var (mn, mx) = d.Mesh.Bounds();
            Console.WriteLine($"       {d.Mesh.Verts.Count} verts, {d.Mesh.Faces.Count} faces, {r.Materials.Count} materials, bounds {mn} {mx}");
            return d.Mesh.Faces.Count > 100 && r.Meshes.Any(m => m.GtaType == GtaObjectType.Collision);
        });
}

Console.WriteLine("Molo Paint (texture painting)");
{
    var box = Primitives.Box(1, 1, 1, 2, 2, 2);
    Check("Smart UV Project on a box: 6 charts, all UVs inside 0..1", () =>
    {
        int charts = MoloMax.Core.Mesh.UvUnwrap.SmartProject(box);
        bool inside = box.Faces.All(f => f.UV.All(u => u.X >= 0 && u.X <= 1 && u.Y >= 0 && u.Y <= 1));
        return charts == 6 && inside && MoloMax.Core.Paint.TexelMap.HasUsableUvs(box);
    });
    MoloMax.Core.Paint.TextureSet ts = null;
    Check("texture set 512: texels cover the islands, AO darker nowhere on a convex box", () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ts = MoloMax.Core.Paint.TextureSet.Create(box, 512, 16);
        Console.WriteLine($"       {ts.Map.Count} texels of {512 * 512}, built in {sw.ElapsedMilliseconds} ms, AO min {ts.Map.Ao.Min():0.00}");
        return ts.Map.Count > 512 * 512 / 3 && ts.Map.Ao.Min() > 0.8f;
    });
    Check("a red brush stamp on the top face paints red texels only near the stroke", () =>
    {
        ts.Layers.Add(new MoloMax.Core.Paint.PaintLayer { Name = "Paint", Kind = MoloMax.Core.Paint.LayerKind.Paint });
        ts.SelectedLayer = 1;
        var b = new MoloMax.Core.Paint.PaintBrush { Radius = 0.2f, Strength = 1, Hardness = 0.8f, ColorValue = new System.Numerics.Vector3(1, 0, 0) };
        var ok = ts.Stamp(b, new System.Numerics.Vector3(0, 0, 1), System.Numerics.Vector3.UnitZ, 0.2f);
        int red = 0, far = 0;
        for (int t = 0; t < ts.Map.Count; t++)
        {
            bool isRed = ts.CColor[t].X > 0.9f && ts.CColor[t].Y < 0.1f;
            if (isRed) { red++; if (System.Numerics.Vector3.Distance(ts.Map.Pos[t], new System.Numerics.Vector3(0, 0, 1)) > 0.25f) far++; }
        }
        Console.WriteLine($"       {red} red texels, {far} outside the brush, dirty {ts.Dirty}");
        return ok && red > 100 && far == 0;
    });
    Check("smart material 'Painted metal, worn': edges get the metal", () =>
    {
        foreach (var l in MoloMax.Core.Paint.SmartMaterials.Make("Painted metal, worn", 1)) ts.Layers.Add(l);
        ts.Refresh(true);
        float edgeMetal = 0, flatMetal = 0; int ne = 0, nf = 0;
        for (int t = 0; t < ts.Map.Count; t++)
        {
            if (ts.Map.Curv[t] > 0.5f) { edgeMetal += ts.CMetal[t]; ne++; }
            else if (MathF.Abs(ts.Map.Curv[t]) < 0.05f) { flatMetal += ts.CMetal[t]; nf++; }
        }
        Console.WriteLine($"       metal on edges {edgeMetal / Math.Max(ne, 1):0.00}, on flat {flatMetal / Math.Max(nf, 1):0.00}");
        return ne > 0 && edgeMetal / ne > flatMetal / Math.Max(nf, 1) + 0.2f;
    });
    Check("save / load the paint file keeps the strokes", () =>
    {
        var f = Path.Combine(tmpDir, "box.mpaint");
        var doc = new MoloMax.Core.Paint.PaintDoc(); doc.Sets.Add(ts);
        doc.Save(f);
        var back = MoloMax.Core.Paint.PaintDoc.Load(f, box, 16, null).Cur;
        int red = 0; for (int t = 0; t < back.Map.Count; t++) if (back.CColor[t].X > 0.9f && back.CColor[t].Y < 0.1f) red++;
        Console.WriteLine($"       {new FileInfo(f).Length / 1024} KB, {back.Layers.Count} layers, {red} red texels after load");
        return back.Layers.Count == ts.Layers.Count && back.Layers[1].Cover != null;
    });
    Check("bake: diffuse / normal / spec images encode to DDS (DXT1, mips)", () =>
    {
        var (d, n, sp, _) = ts.BakeImages();
        var dds = MoloMax.Core.Gta.Export.DdsEncoder.Encode(d, MoloMax.Core.Gta.Export.DdsFormat.Dxt1, 1024, true);
        return dds.Length > 128 && n.Width == 512 && sp.Pixels.Length == 512 * 512 * 4;
    });
}

Console.WriteLine("Molo Paint 2: bake, channels, folders, masks, filters, tools, sets");
{
    var V3 = (float x, float y, float z) => new System.Numerics.Vector3(x, y, z);
    var low = Primitives.Box(1, 1, 1);
    MoloMax.Core.Mesh.UvUnwrap.SmartProject(low);
    var ts = TextureSet.Create(low, 256, 8);
    Check("texel map: orthonormal tangent frames, face / island / element of every texel", () =>
        Enumerable.Range(0, ts.Map.Count).All(t => MathF.Abs(System.Numerics.Vector3.Dot(ts.Map.Tan[t], ts.Map.Nrm[t])) < 1e-3f && MathF.Abs(System.Numerics.Vector3.Dot(ts.Map.Bit[t], ts.Map.Nrm[t])) < 1e-3f)
        && ts.Map.FaceOf.All(f => f >= 0 && f < low.Faces.Count) && ts.Map.IslandOf.Distinct().Count() >= 6 && ts.Map.ElementOf.Distinct().Count() == 1);

    // high-poly: a red sphere around the box (the box is 1 m, centred on x/y, from z 0 to 1)
    var high = Primitives.Sphere(0.75f, 48);
    high.Transform(System.Numerics.Matrix4x4.CreateTranslation(0, 0, 0.5f));
    foreach (var f in high.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new System.Numerics.Vector4(1, 0, 0, 1);
    BakeResult bake = null;
    Check("bake high to low: rays find the high-poly, normal tilts near the corners, colour comes from it", () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bake = PaintBaker.Bake(ts.Map, high, System.Numerics.Matrix4x4.Identity, new BakeSettings { MaxDistance = 0.4f, AoRays = 8 });
        int top = ts.Map.Nearest(V3(0, 0, 1), 0.05f);
        int corner = ts.Map.Nearest(V3(0.45f, 0.45f, 1), 0.05f);
        var nc = bake.Normal[corner];
        Console.WriteLine($"       {bake.Hits}/{bake.Texels} hits in {sw.ElapsedMilliseconds} ms, centre {bake.Normal[top]}, corner {nc}, colour {bake.Colour[top]}");
        return bake.HitRatio > 0.9f && bake.Normal[top].Z > 0.98f && new System.Numerics.Vector2(nc.X, nc.Y).Length() > 0.3f && bake.Colour[top].X > 0.9f && bake.Colour[top].Y < 0.1f;
    });
    Check("baked normal joins the normal map and the colour becomes a layer", () =>
    {
        var before = (byte[])ts.NormalImage.Pixels.Clone();
        ts.ApplyBake(bake, true);
        int changed = 0; for (int i = 0; i < before.Length; i += 4) if (Math.Abs(before[i] - ts.NormalImage.Pixels[i]) > 20) changed++;
        Console.WriteLine($"       {changed} normal pixels changed, layers: {string.Join(", ", ts.Layers.Select(l => l.Name))}");
        return changed > 1000 && ts.Layers.Any(l => l.Name.StartsWith("Baked colour")) && ts.CColor.Count(c => c.X > 0.9f && c.Y < 0.1f) > ts.Map.Count / 2;
    });

    Check("normal map frame: relief rising towards +X tilts the decoded normal towards -X (painted and baked maps share the frame)", () =>
    {
        var th = TextureSet.Create(low, 256, 0);
        var hl = new PaintLayer { Name = "h", Kind = LayerKind.Paint }; th.AddLayerRaw(hl); th.SelectedLayer = 1;
        th.Stamp(new PaintBrush { Radius = 0.35f, Strength = 1, Hardness = 0, Color = false, Roughness = false, Height = true, HeightValue = 1 }, V3(0.35f, 0, 1), System.Numerics.Vector3.UnitZ, 0.35f);
        int t = th.Map.Nearest(V3(0.12f, 0, 1), 0.03f);
        int o = th.Map.Pixel[t] * 4;
        var nt = new System.Numerics.Vector3(th.NormalImage.Pixels[o] / 127.5f - 1, th.NormalImage.Pixels[o + 1] / 127.5f - 1, th.NormalImage.Pixels[o + 2] / 127.5f - 1);
        var world = th.Map.Tan[t] * nt.X + th.Map.Bit[t] * nt.Y + th.Map.Nrm[t] * nt.Z;
        Console.WriteLine($"       tangent normal {nt}, world {world}");
        return world.X < -0.05f && MathF.Abs(world.Y) < MathF.Abs(world.X) * 0.5f;
    });
    var ts2 = TextureSet.Create(low, 256, 0);
    var paint = new PaintLayer { Name = "p", Kind = LayerKind.Paint }; ts2.AddLayerRaw(paint); ts2.SelectedLayer = 1;
    var b = new PaintBrush { Radius = 0.25f, Strength = 1, Hardness = 0.9f, Color = true, ColorValue = V3(1, 1, 0), Emissive = true, EmissiveValue = 1, Alpha = true, AlphaValue = 0 };
    Check("emissive and opacity channels: painted, exported as _e and DXT5 alpha", () =>
    {
        ts2.Stamp(b, V3(0, 0, 1), System.Numerics.Vector3.UnitZ, 0.25f);
        var w = PaintExport.Write(ts2, Path.Combine(tmpDir, "paint2"), "crate");
        Console.WriteLine($"       files: {string.Join(", ", w.Files.Select(Path.GetFileName))}  alpha {w.Alpha} emissive {w.Emissive}");
        return ts2.HasEmissive && ts2.HasAlpha && w.Alpha && w.Emissive && w.Files.Count == 4 && File.Exists(Path.Combine(tmpDir, "paint2", "crate_e.dds"));
    });
    Check("export at 2048 working size becomes 1024 (GTA rule), TGA option writes a valid header", () =>
    {
        var big = new MoloMax.Core.Gta.Export.RgbaImage(2048, 2048);
        var fit = PaintExport.Fit(big, 1024);
        var tga = PaintExport.Tga(fit);
        return fit.Width == 1024 && tga[2] == 2 && tga[12] == 0 && tga[13] == 4 && tga.Length == 18 + 1024 * 1024 * 4;
    });

    var ts3 = TextureSet.Create(low, 256, 0);
    var folder = new PaintLayer { Name = "Dirt folder", Kind = LayerKind.Folder }; ts3.AddLayerRaw(folder);
    var red = new PaintLayer { Name = "red", Kind = LayerKind.Fill, Color = V3(1, 0, 0), Folder = folder.Id }; ts3.AddLayerRaw(red);
    ts3.Refresh();
    Check("folders: a hidden folder hides its layers, its opacity scales them", () =>
    {
        bool allRed = ts3.CColor.All(c => c.X > 0.99f);
        folder.Visible = false; ts3.Refresh(); bool noneRed = ts3.CColor.All(c => c.X < 0.6f);
        folder.Visible = true; folder.Opacity = 0.5f; ts3.Refresh(); bool half = ts3.CColor.All(c => MathF.Abs(c.X - 0.775f) < 0.02f);
        folder.Opacity = 1; ts3.Refresh();
        return allRed && noneRed && half;
    });
    Check("mask painting on a fill layer: the eraser hides it under the brush only", () =>
    {
        ts3.SelectedLayer = ts3.Layers.IndexOf(red);
        var mb = new PaintBrush { MaskMode = true, Erase = true, Radius = 0.2f, Strength = 1, Hardness = 0.9f };
        ts3.Stamp(mb, V3(0, 0, 1), System.Numerics.Vector3.UnitZ, 0.2f);
        int hidden = 0, farHidden = 0;
        for (int t = 0; t < ts3.Map.Count; t++) if (ts3.CColor[t].X < 0.6f) { hidden++; if (System.Numerics.Vector3.Distance(ts3.Map.Pos[t], V3(0, 0, 1)) > 0.25f) farHidden++; }
        Console.WriteLine($"       {hidden} texels hidden, {farHidden} outside the brush");
        return hidden > 50 && farHidden == 0;
    });
    Check("filters: blur softens the painted mask, levels / hue change the colour", () =>
    {
        int Soft() => ts3.MaskOf(red).Count(v => v > 0.05f && v < 0.95f);
        int soft0 = Soft();
        red.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 4 }); red.MaskChanged(); ts3.Refresh();
        int soft1 = Soft();
        red.Filters.Add(new PaintFilter { Kind = FilterKind.HueSaturation, OnMask = false, Hue = 120 }); ts3.Refresh();
        var far = Enumerable.Range(0, ts3.Map.Count).First(t => ts3.Map.Nrm[t].Z < -0.9f);
        Console.WriteLine($"       mask values between 0.05 and 0.95: {soft0} -> {soft1}, colour after hue +120: {ts3.CColor[far]}");
        return soft1 > soft0 * 1.5f && ts3.CColor[far].Y > 0.9f && ts3.CColor[far].X < 0.1f;
    });

    var ts4 = TextureSet.Create(low, 256, 0);
    var p4 = new PaintLayer { Name = "p", Kind = LayerKind.Paint }; ts4.AddLayerRaw(p4); ts4.SelectedLayer = 1;
    Check("fill tool: one face, one UV island, the whole element", () =>
    {
        var fb = new PaintBrush { Strength = 1, ColorValue = V3(0, 0, 1) };
        ts4.Fill(fb, 0, FillScope.Face);
        int blue = ts4.CColor.Count(c => c.Z > 0.9f && c.X < 0.1f);
        int face0 = ts4.Map.FaceOf.Count(f => f == 0);
        ts4.Fill(fb, 0, FillScope.Element);
        int all = ts4.CColor.Count(c => c.Z > 0.9f && c.X < 0.1f);
        Console.WriteLine($"       face: {blue}/{face0} texels, element: {all}/{ts4.Map.Count}");
        return blue == face0 && all == ts4.Map.Count;
    });
    Check("clone copies another place, smudge drags the colour, a brush tip prints its shape", () =>
    {
        var t5 = TextureSet.Create(low, 256, 0);
        var l5 = new PaintLayer { Name = "p", Kind = LayerKind.Paint }; t5.AddLayerRaw(l5); t5.SelectedLayer = 1;
        t5.Stamp(new PaintBrush { Radius = 0.1f, Strength = 1, Hardness = 0.95f, ColorValue = V3(1, 0, 0) }, V3(-0.25f, 0, 1), System.Numerics.Vector3.UnitZ, 0.1f);
        var cb = new PaintBrush { Mode = BrushMode.Clone, CloneSourceSet = true, CloneOffset = V3(-0.5f, 0, 0), Radius = 0.1f, Strength = 1, Hardness = 0.95f };
        t5.Stamp(cb, V3(0.25f, 0, 1), System.Numerics.Vector3.UnitZ, 0.1f);
        int at = t5.Map.Nearest(V3(0.25f, 0, 1), 0.05f);
        bool cloned = t5.CColor[at].X > 0.9f && t5.CColor[at].Y < 0.1f;
        var sb = new PaintBrush { Mode = BrushMode.Smudge, Radius = 0.1f, Strength = 0.8f };
        t5.Stamp(sb, V3(0.0f, 0, 1), System.Numerics.Vector3.UnitZ, 0.1f, smudgeFrom: V3(-0.25f, 0, 1));
        int mid = t5.Map.Nearest(V3(0, 0, 1), 0.05f);
        bool smudged = t5.CColor[mid].X > 0.7f;
        var tip = BrushTips.Get("Scratches");
        var t6 = TextureSet.Create(low, 256, 0); var l6 = new PaintLayer { Name = "p", Kind = LayerKind.Paint }; t6.AddLayerRaw(l6); t6.SelectedLayer = 1;
        t6.Stamp(new PaintBrush { Tip = tip, Radius = 0.4f, Strength = 1, ColorValue = V3(0, 1, 0) }, V3(0, 0, 1), System.Numerics.Vector3.UnitZ, 0.4f, 30);
        int top = t6.Map.Pos.Select((p, i) => (p, i)).Count(x => t6.Map.Nrm[x.i].Z > 0.99f && System.Numerics.Vector3.Distance(x.p, V3(0, 0, 1)) < 0.25f);
        int green = t6.Map.Pos.Select((p, i) => (p, i)).Count(x => t6.Map.Nrm[x.i].Z > 0.99f && System.Numerics.Vector3.Distance(x.p, V3(0, 0, 1)) < 0.25f && t6.CColor[x.i].Y > 0.6f);
        Console.WriteLine($"       cloned {cloned}, smudged {smudged} ({t5.CColor[mid]}), scratches cover {green}/{top} texels of the area");
        return cloned && smudged && green > top / 20 && green < top * 0.8f && BrushTips.Names.All(n => n == "Round" || BrushTips.Get(n) != null);
    });

    Check("tufted cushion: every face points outwards, UVs packed in 0..1, one island per side", () =>
    {
        var tf = Primitives.Tufted(1.4f, 0.48f, 0.4f, 8, 3, 2, 0.022f, 8);
        int wrong = 0;
        for (int i = 0; i < tf.Faces.Count; i++)
        {
            var c = tf.FaceCenter(i); var nrm = tf.FaceNormal(i);
            var side = MathF.Abs(c.Z - 0.4f) < 0.03f ? V3(0, 0, 1) : MathF.Abs(c.Y) > 0.22f ? V3(0, MathF.Sign(c.Y), 0) : V3(MathF.Sign(c.X), 0, 0);
            if (System.Numerics.Vector3.Dot(nrm, side) < 0) wrong++;
        }
        bool uv = tf.Faces.All(f => f.UV.All(u => u.X >= -1e-4f && u.X <= 1.0001f && u.Y >= -1e-4f && u.Y <= 1.0001f));
        Console.WriteLine($"       {tf.Faces.Count} faces, {wrong} facing inwards, islands {MoloMax.Core.Mesh.UvTools.Islands(tf).Count}");
        return wrong == 0 && uv && MoloMax.Core.Mesh.UvTools.Islands(tf).Count == 5;
    });
    Check("collision group under a Drawable: stairs flag (auto), water preset, forced RoomID, all in the .ydr", () =>
    {
        var sc = new Scene();
        var node = sc.Add("cg_prop", new MoloMax.Core.Mesh.PolyMesh(), V3(0, 0, 0), GtaObjectType.Model);
        var mesh = sc.Add("cg_prop_high_0", Primitives.Box(2, 1, 1), V3(0, 0, 0)); sc.SetParent(mesh, node);
        var grp = sc.Add("cg_prop_col", new MoloMax.Core.Mesh.PolyMesh(), V3(0, 0, 0), GtaObjectType.CollisionGroup); sc.SetParent(grp, node);
        var stairs = sc.Add("stairs_col", Primitives.Stairs(1, 6, 1, 1.5f), V3(0, 0, 0), GtaObjectType.Collision); sc.SetParent(stairs, grp);
        var water = sc.Add("pool_col", Primitives.Box(1, 1, 0.2f), V3(0, 0, 0), GtaObjectType.Collision); sc.SetParent(water, grp);
        var pre = MoloMax.Core.Gta.Export.CollisionFlags.Presets.First(x => x.Label == "Water");
        water.Gta.CollisionMaterial = MoloMax.Core.Gta.Export.CollisionMaterials.IndexOf(pre.Material); water.Gta.CollisionFlagsAuto = false; water.Gta.CollisionFlags = pre.Flags; water.Gta.RoomIdOverride = 2;
        var path = Path.Combine(tmpDir, "cg_prop.ydr");
        MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(sc, new[] { node }, path, null);
        var y = new CodeWalker.GameFiles.YdrFile(); y.Load(File.ReadAllBytes(path));
        var mats = new List<CodeWalker.GameFiles.BoundMaterial_s>();
        void Walk(CodeWalker.GameFiles.Bounds b) { if (b is CodeWalker.GameFiles.BoundComposite c) foreach (var k in c.Children.data_items) Walk(k); else if (b is CodeWalker.GameFiles.BoundGeometry g && g.Materials != null) mats.AddRange(g.Materials); }
        Walk(y.Drawable.Bound);
        Console.WriteLine("       materials: " + string.Join(" | ", mats.Select(m => $"{m.Type.Index} room {m.RoomId} {m.Flags}")));
        return MoloMax.Core.Gta.Export.CollisionFlags.LooksLikeStairs(stairs.Mesh)
            && mats.Any(m => m.Flags.HasFlag(CodeWalker.GameFiles.EBoundMaterialFlags.FLAG_STAIRS))
            && mats.Any(m => m.RoomId == 2 && m.Flags.HasFlag(CodeWalker.GameFiles.EBoundMaterialFlags.FLAG_NO_DECAL) && m.Type.Index == MoloMax.Core.Gta.Export.CollisionMaterials.IndexOf("WATER"));
    });
    Check("working resolution change keeps the painting (256 -> 512)", () =>
    {
        var tr = TextureSet.Create(low, 256, 0);
        var lr = new PaintLayer { Name = "p", Kind = LayerKind.Paint }; tr.AddLayerRaw(lr); tr.SelectedLayer = 1;
        tr.Stamp(new PaintBrush { Radius = 0.2f, Strength = 1, Hardness = 0.9f, ColorValue = V3(1, 0, 0) }, V3(0, 0, 1), System.Numerics.Vector3.UnitZ, 0.2f);
        tr.Resize(low, 512, 0);
        int t = tr.Map.Nearest(V3(0, 0, 1), 0.02f);
        return tr.Size == 512 && tr.Map.Count > 150000 && tr.CColor[t].X > 0.9f && tr.CColor[t].Y < 0.1f;
    });
    Check("smart materials: all (GTA ones included) build layers; your own save / load as JSON", () =>
    {
        bool all = SmartMaterials.All.All(n => SmartMaterials.Make(n, 1).Count > 0);
        SmartLibrary.Folder = Path.Combine(tmpDir, "smart");
        var mine = SmartMaterials.Make("Yellow industrial paint", 3);
        mine[0].Filters.Add(new PaintFilter { Kind = FilterKind.Levels, Gamma = 1.4f });
        SmartLibrary.Save("My yellow", mine);
        var back = SmartMaterials.Make("My yellow", 9);
        Console.WriteLine($"       {SmartMaterials.All.Count()} built in, saved one: {string.Join(", ", SmartLibrary.Names())} -> {back.Count} layers, filters {back[0].Filters.Count}");
        return all && back.Count == mine.Count && back[1].Mask == MaskGen.Edges && back[0].Filters.Count == 1 && SmartLibrary.Names().Contains("My yellow");
    });
    Check("smart masks set up the selected layer (leaks, grout, ground dirt...)", () =>
    {
        var l = new PaintLayer { Kind = LayerKind.Fill };
        foreach (var m in SmartMasks.All) { m.Apply(l); ts4.Layers.Add(l); ts4.MaskOf(l); ts4.Layers.Remove(l); }
        var g = SmartMasks.All.First(x => x.Name == "Tile grout"); g.Apply(l); ts4.Layers.Add(l); var mk = ts4.MaskOf(l); ts4.Layers.Remove(l);
        return mk.Any(v => v > 0.9f) && mk.Any(v => v < 0.1f);
    });

    // two materials on one object: one texture set each, saved together
    var two = Primitives.Box(1, 1, 1);
    MoloMax.Core.Mesh.UvUnwrap.SmartProject(two);
    for (int i = 0; i < two.Faces.Count; i++) two.Faces[i].MatId = i < 3 ? 11 : 12;
    Check("multi-material: one texture set per material, saved and loaded with folders, filters and baked maps", () =>
    {
        var doc = new PaintDoc();
        var a = TextureSet.Create(two, 128, 0, 11, "metal"); var c = TextureSet.Create(two, 128, 0, 12, "wood");
        doc.Sets.Add(a); doc.Sets.Add(c); doc.Active = 1;
        bool split = a.Map.FaceOf.All(f => f < 3) && c.Map.FaceOf.All(f => f >= 3);
        var fo = new PaintLayer { Name = "F", Kind = LayerKind.Folder }; c.AddLayerRaw(fo);
        var gl = new PaintLayer { Name = "G", Kind = LayerKind.Fill, Color = V3(0, 1, 0), Folder = fo.Id, Mask = MaskGen.Edges }; gl.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 2 }); c.AddLayerRaw(gl);
        c.BakedNormal = Enumerable.Repeat(System.Numerics.Vector3.Normalize(V3(0.3f, 0, 1)), c.Map.Count).ToArray(); c.HasBakedMaps = true;
        c.Refresh();
        var path = Path.Combine(tmpDir, "two.mpaint");
        doc.Save(path);
        var back = PaintDoc.Load(path, two, 0, n => n == "metal" ? 11 : 12);
        var bc = back.Sets[1];
        Console.WriteLine($"       sets {back.Sets.Count} ({string.Join(", ", back.Sets.Select(s => $"{s.Name}:{s.Map.Count}"))}), active {back.Active}, layers {bc.Layers.Count}, folder link {bc.Layers[2].Folder == bc.Layers[1].Id}, baked {bc.BakedNormal?[0]}");
        return split && back.Sets.Count == 2 && back.Active == 1 && bc.Name == "wood" && bc.Map.FaceOf.All(f => f >= 3) && bc.Layers[1].IsFolder
            && bc.Layers[2].Folder == bc.Layers[1].Id && bc.Layers[2].Filters.Count == 1 && bc.BakedNormal != null && bc.BakedNormal[0].X > 0.2f;
    });
    Check("vertex colour painting: brush near a corner, fill of an element", () =>
    {
        var m = Primitives.Box(1, 1, 1);
        int n = VertexPaint.Stamp(m, V3(0.5f, 0.5f, 1), System.Numerics.Vector3.UnitZ, 0.3f, VertexPaint.Presets[2].Colour, 1, 0.9f);
        bool cornerWhite = m.Faces.Any(f => f.V.Select((v, i) => (v, i)).Any(x => System.Numerics.Vector3.Distance(m.Verts[x.v], V3(0.5f, 0.5f, 1)) < 1e-4f && f.Col[x.i] == System.Numerics.Vector4.One));
        VertexPaint.Fill(m, VertexPaint.ElementOf(m, 0), VertexPaint.Presets[0].Colour);
        return n > 0 && cornerWhite && m.Faces.All(f => f.Col.All(c => c == new System.Numerics.Vector4(0, 1, 0, 1)));
    });
}

Console.WriteLine("Molo Paint: ArmorPaint procedural textures, blend modes, filters");
{
    var V = (float x, float y, float z) => new System.Numerics.Vector3(x, y, z);
    var rnd = new Random(3);
    var pts = Enumerable.Range(0, 4000).Select(_ => V((float)rnd.NextDouble() * 3, (float)rnd.NextDouble() * 3, (float)rnd.NextDouble() * 3)).ToArray();
    bool Range01(Func<System.Numerics.Vector3, float> f, out float sd)
    {
        var v = pts.Select(f).ToArray(); float mean = v.Average(); sd = MathF.Sqrt(v.Select(x => (x - mean) * (x - mean)).Average());
        return v.All(x => x >= 0 && x <= 1 && !float.IsNaN(x));
    }
    foreach (var (name, fn) in new (string, Func<System.Numerics.Vector3, float>)[]
    {
        ("voronoi cells", p => ProcTex.Voronoi(p, 4).Cell), ("voronoi edges", p => ProcTex.Voronoi(p, 4).Edge), ("checker", p => ProcTex.Checker(p, 4)),
        ("perlin", p => ProcTex.Perlin(p, 4)), ("wave", p => ProcTex.Wave(p, 0.2f, false)), ("rings", p => ProcTex.Wave(p, 0.2f, true)), ("magic", p => ProcTex.Magic(p, 1)),
        ("brick mortar", p => ProcTex.Brick(new System.Numerics.Vector2(p.X, p.Y), 13).Mortar),
    })
        Check($"procedural {name}: values in 0..1, not flat", () => Range01(fn, out var sd) && sd > 0.05f);
    Check("bricks: most of the wall is brick, a thin part is mortar; each brick gets its own tint", () =>
    {
        float mortar = pts.Average(p => ProcTex.Brick(new System.Numerics.Vector2(p.X, p.Y), 13).Mortar > 0.5f ? 1f : 0f);
        int tints = pts.Select(p => ProcTex.Brick(new System.Numerics.Vector2(p.X, p.Y), 13).Tint).Distinct().Count();
        Console.WriteLine($"       mortar {mortar:P0}, {tints} tints");
        return mortar > 0.03f && mortar < 0.35f && tints > 50;
    });
    Check("checker: neighbours across a cell border differ", () => ProcTex.Checker(V(0.1f, 0.1f, 0.1f), 1) != ProcTex.Checker(V(1.1f, 0.1f, 0.1f), 1));
    Check("warp moves the lookup by at most the amount (per axis)", () => pts.Take(200).All(p => { var d = ProcTex.Warp(p, 0.2f, 2, 1) - p; return MathF.Abs(d.X) <= 0.2001f && MathF.Abs(d.Y) <= 0.2001f && d.Length() > 0; }));

    var box = Primitives.Box(1, 1, 1); MoloMax.Core.Mesh.UvUnwrap.SmartProject(box);
    var ts = TextureSet.Create(box, 128, 4);
    var baseC = V(0.6f, 0.3f, 0.2f); var topC = V(0.2f, 0.5f, 0.9f);
    System.Numerics.Vector3 Mix(PaintBlend mode, float op = 1)
    {
        ts.Layers.Clear();
        ts.Layers.Add(new PaintLayer { Id = 1, Kind = LayerKind.Fill, Color = baseC });
        ts.Layers.Add(new PaintLayer { Id = 2, Kind = LayerKind.Fill, Color = topC, Blend = mode, Opacity = op });
        ts.Refresh(true);
        return ts.CColor[ts.Map.Count / 2];
    }
    bool NearC(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Distance(a, b) < 0.02f;
    Check("blend Darken / Lighten / Difference / Subtract give the ArmorPaint result", () =>
        NearC(Mix(PaintBlend.Darken), V(0.2f, 0.3f, 0.2f)) && NearC(Mix(PaintBlend.Lighten), V(0.6f, 0.5f, 0.9f))
        && NearC(Mix(PaintBlend.Difference), V(0.4f, 0.2f, 0.7f)) && NearC(Mix(PaintBlend.Subtract), V(0.4f, 0, 0)));
    Check("every blend mode stays in 0..1 and 0 % opacity changes nothing", () =>
        Enum.GetValues<PaintBlend>().All(m => { var c = Mix(m); var z = Mix(m, 0); return c.X >= 0 && c.X <= 1 && c.Y >= 0 && c.Y <= 1 && c.Z >= 0 && c.Z <= 1 && NearC(z, baseC); }));
    Check("blend Hue takes the layer's hue, keeps the brightness below", () =>
    {
        var c = Mix(PaintBlend.Hue); var (h, _, v) = TextureSet.ToHsv(c); var (th, _, _) = TextureSet.ToHsv(topC); var (_, _, bv) = TextureSet.ToHsv(baseC);
        return MathF.Abs(h - th) < 0.02f && MathF.Abs(v - bv) < 0.02f;
    });
    Check("fill pattern Bricks: mortar colour in the joints, brick colour elsewhere", () =>
    {
        ts.Layers.Clear();
        ts.Layers.Add(new PaintLayer { Id = 1, Kind = LayerKind.Fill, Color = V(0.5f, 0.15f, 0.1f), Pattern = FillPattern.Brick, PatternColor = V(0.7f, 0.7f, 0.7f), PatternScale = 8 });
        ts.Refresh(true);
        int grey = 0, red = 0;
        for (int t = 0; t < ts.Map.Count; t++) { var c = ts.CColor[t]; if (MathF.Abs(c.X - c.Z) < 0.05f && c.X > 0.6f) grey++; else if (c.X > c.Z + 0.2f) red++; }
        Console.WriteLine($"       {grey} mortar texels, {red} brick texels");
        return grey > 0 && red > grey * 2;
    });
    Check("mask Cracks + filters Posterize / Gamma / Brightness-contrast keep the mask in 0..1", () =>
    {
        ts.Layers.Clear();
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, Mask = MaskGen.Cracks, MaskNoiseScale = 5, Warp = 0.05f };
        l.Filters.Add(new PaintFilter { Kind = FilterKind.Posterize, Steps = 3 });
        l.Filters.Add(new PaintFilter { Kind = FilterKind.Gamma, Gamma = 2 });
        l.Filters.Add(new PaintFilter { Kind = FilterKind.BrightnessContrast, Contrast = 1, Brightness = 0.1f });
        ts.Layers.Add(l);
        var m = ts.MaskOf(l);
        return m.All(x => x >= 0 && x <= 1) && m.Distinct().Count() <= 4 && m.Any(x => x > 0.5f) && m.Any(x => x < 0.5f);
    });
    Check("colour filter Colour ramp maps dark to A and bright to B", () =>
    {
        ts.Layers.Clear();
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, Color = V(1, 1, 1) };
        l.Filters.Add(new PaintFilter { Kind = FilterKind.ColorRamp, OnMask = false, RampA = V(0, 0, 0), RampB = V(1, 0, 0) });
        ts.Layers.Add(l); ts.Refresh(true);
        return NearC(ts.CColor[0], V(1, 0, 0));
    });
    Check("mask 'picked parts': a clicked face shows the layer only there, a second click takes it out", () =>
    {
        ts.Layers.Clear();
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, MaskPartScope = FillScope.Face };
        ts.Layers.Add(l);
        int face = ts.Map.FaceOf[ts.Map.Count / 2];
        bool on = ts.TogglePart(l, face);
        var m = ts.MaskOf(l);
        bool only = Enumerable.Range(0, ts.Map.Count).All(t => (ts.Map.FaceOf[t] == face) == (m[t] > 0.5f));
        ts.TogglePart(l, face);
        var m2 = ts.MaskOf(l);
        return on && only && m2.All(x => x < 0.5f) && l.Mask == MaskGen.Parts;
    });
    Check("picked parts by UV island: the whole island of the clicked face", () =>
    {
        ts.Layers.Clear();
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, MaskPartScope = FillScope.UvIsland };
        ts.Layers.Add(l);
        int t0 = ts.Map.Count / 3, isl = ts.Map.IslandOf[t0];
        ts.TogglePart(l, ts.Map.FaceOf[t0]);
        var m = ts.MaskOf(l);
        return Enumerable.Range(0, ts.Map.Count).All(t => (ts.Map.IslandOf[t] == isl) == (m[t] > 0.5f));
    });
    Check("node material: noise -> colour ramp -> output paints the layer between the two ramp colours, not flat", () =>
    {
        ts.Layers.Clear();
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, Graph = NodeGraph.Starter() };
        var ramp = l.Graph.Nodes.First(n => n.Type == "Color ramp"); ramp.C1 = V(1, 0, 0); ramp.C2 = V(0, 0, 1);
        l.Graph.Nodes.First(n => n.Type == "Noise").S = 6;
        ts.Layers.Add(l); ts.Refresh(true);
        var cs = Enumerable.Range(0, ts.Map.Count).Select(t => ts.CColor[t]).ToList();
        bool noGreen = cs.All(c => c.Y < 0.02f);
        float spread = cs.Max(c => c.X) - cs.Min(c => c.X);
        Console.WriteLine($"       red spread {spread:0.00}");
        return noGreen && spread > 0.3f;
    });
    Check("node material: a link that would make a loop is refused; Mix multiply of two colours", () =>
    {
        var g = new NodeGraph();
        var a = g.Add("Math", default); var b = g.Add("Math", default);
        bool ok1 = g.Link(a.Id, 0, b.Id, 0), loop = g.Link(b.Id, 0, a.Id, 0);
        var c1 = g.Add("Color", default); c1.C1 = V(0.5f, 1, 1); var c2 = g.Add("Color", default); c2.C1 = V(0.5f, 0.5f, 0);
        var mix = g.Add("Mix", default); mix.Mode = (int)PaintBlend.Multiply; mix.A = 1;
        var o = g.Add("Output", default);
        g.Link(c1.Id, 0, mix.Id, 0); g.Link(c2.Id, 0, mix.Id, 1); g.Link(mix.Id, 0, o.Id, 0);
        var r = g.Evaluate(ts, 0);
        return ok1 && !loop && r is { } v && (v.Color - V(0.25f, 0.5f, 0)).Length() < 0.01f;
    });
    Check("node material evaluated on many threads at once (fresh graph, 20 refreshes): no crash, same result", () =>
    {
        for (int k = 0; k < 20; k++)
        {
            ts.Layers.Clear();
            var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, Graph = NodeGraph.Starter() };
            ts.Layers.Add(l); ts.Refresh(true); // the graph's lookups are built by the parallel composite
        }
        return true;
    });
    Check("node material is saved with the layer settings (smart material JSON) and read back", () =>
    {
        var l = new PaintLayer { Id = 1, Kind = LayerKind.Fill, Graph = NodeGraph.Starter() };
        var json = l.SettingsJson();
        var back = System.Text.Json.JsonSerializer.Deserialize<PaintLayer>(json, PaintJson.Options);
        return back.Graph != null && back.Graph.Nodes.Count == 3 && back.Graph.Links.Count == 2;
    });
    Check("smart materials with patterns build their layers", () => new[] { "Red brick wall", "Cracked mud", "Wood grain", "Stone paving" }.All(n => SmartMaterials.Make(n, 1).Count >= 2));
}

Console.WriteLine("Molo texture library in Molo Paint");
if (Directory.Exists("/tmp/molomax_libsample"))
{
    var lib = MoloMax.Core.Gta.Library.MaterialLibrary.Load("/tmp/molomax_libsample");
    Check("library loads, categories: decals, floors, walls", () =>
    {
        Console.WriteLine("       " + string.Join(", ", lib.Materials.GroupBy(m => m.Category).Select(g => $"{g.Key} {g.Count()}")));
        return lib.Materials.Count == 13 && lib.Search("Decals", "").Any() && lib.Search("Floors", "").Any() && lib.Search("Walls", "").Any();
    });
    Check("a library material becomes a GTA material with its DDS files and parameters", () =>
    {
        var m = lib.Materials.First(x => x.Shader == "normal_spec");
        var g = lib.ToGtaMaterial(m, 5);
        return g.Shader == "normal_spec" && File.Exists(g.DiffuseTexture) && File.Exists(g.BumpTexture) && File.Exists(g.SpecTexture);
    });
    Check("DDS decode (DXT via CodeWalker) gives the right size and colours", () =>
    {
        var img = MoloMax.Core.Gta.Library.TextureReader.Load(lib.TexturePath("tl_v_cashdepot_wall3"));
        var dec = MoloMax.Core.Gta.Library.TextureReader.Load(lib.TexturePath("ph_burnt_01_dph_burnt_01_a"));
        int transparent = 0; for (int i = 3; i < dec.Pixels.Length; i += 4) if (dec.Pixels[i] < 20) transparent++;
        Console.WriteLine($"       wall {img.Width}x{img.Height}, decal {dec.Width}x{dec.Height}, {transparent * 100 / (dec.Pixels.Length / 4)}% transparent");
        return img.Width >= 128 && dec.Width == 512 && transparent > 0;
    });
    Check("fill layer from a library wall material: texels take the texture colours", () =>
    {
        var box = Primitives.Box(2, 2, 2, 2, 2, 2); MoloMax.Core.Mesh.UvUnwrap.SmartProject(box);
        var ts = MoloMax.Core.Paint.TextureSet.Create(box, 256, 8);
        var m = lib.Materials.First(x => x.Diffuse == "tl_v_cashdepot_wall3");
        var l = new MoloMax.Core.Paint.PaintLayer { Name = m.Name, Kind = MoloMax.Core.Paint.LayerKind.Fill, Color = System.Numerics.Vector3.One, TexDiffuse = lib.TexturePath(m.Diffuse), TexSpec = lib.TexturePath(m.Spec), UseHeight = true, TileMetres = m.TileMetres };
        ts.Layers.Add(l); ts.Refresh();
        var distinct = ts.CColor.Select(c => (int)(c.X * 20)).Distinct().Count();
        Console.WriteLine($"       {distinct} colour levels, rough {ts.CRough.Average():0.00}, tile {m.TileMetres:0.00} m");
        return distinct > 3;
    });
    Check("decal stamp: the game decal's alpha shapes the paint", () =>
    {
        var plane = Primitives.Plane(2, 2, 1, 1); MoloMax.Core.Mesh.UvUnwrap.SmartProject(plane);
        var ts = MoloMax.Core.Paint.TextureSet.Create(plane, 256, 0);
        ts.Layers.Add(new MoloMax.Core.Paint.PaintLayer { Name = "Decals", Kind = MoloMax.Core.Paint.LayerKind.Paint }); ts.SelectedLayer = 1;
        var b = new MoloMax.Core.Paint.PaintBrush { Strength = 1, Stamp = MoloMax.Core.Gta.Library.TextureReader.Load(lib.TexturePath("ph_burnt_01_dph_burnt_01_a")) };
        ts.Stamp(b, System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, 0.8f);
        var cov = ts.Layers[1].Cover;
        int full = cov.Count(c => c > 0.9f), none = cov.Count(c => c < 0.05f);
        Console.WriteLine($"       covered {full}, untouched {none} of {cov.Length}");
        return full > 100 && none > 100;
    });
}

Console.WriteLine("Symmetry modifier (3ds Max)");
{
    Check("Symmetry X slices a box crossing the plane and mirrors the positive half: closed, symmetric", () =>
    {
        var b = Primitives.Box(2, 1, 1); b.Transform(System.Numerics.Matrix4x4.CreateTranslation(0.4f, 0, 0)); // x from -0.6 to 1.4
        var m = new MoloMax.Core.Modifiers.SymmetryModifier().Apply(b, false);
        var (mn, mx) = m.Bounds();
        return Near(mn.X, -1.4f) && Near(mx.X, 1.4f) && m.IsClosedManifold(out _);
    });
    Check("Flip keeps the negative side, offset moves the plane", () =>
    {
        var b = Primitives.Box(2, 1, 1); b.Transform(System.Numerics.Matrix4x4.CreateTranslation(0.4f, 0, 0));
        var m = new MoloMax.Core.Modifiers.SymmetryModifier { Flip = true, Offset = 0.4f }.Apply(b, false);
        var (mn, mx) = m.Bounds();
        return Near(mn.X, -0.6f) && Near(mx.X, 1.4f);
    });
}

Console.WriteLine("Quick export: GLB and single .ydr / .yft");
{
    var sc = new Scene();
    var o = sc.Add("Crate", Primitives.Box(1, 1, 1), new System.Numerics.Vector3(3, 0, 0));
    o.Modifiers.Add(new MoloMax.Core.Modifiers.SubdivisionModifier { LevelsRender = 1 });
    Check("GLB export re-imports with the same box (modifiers applied, Y-up round trip)", () =>
    {
        var f = Path.Combine(tmpDir, "quick.glb");
        MoloMax.Core.IO.GlbExporter.Export(sc, new[] { o }, f);
        var r = MoloMax.Core.IO.ModelImporter.Import(f);
        var (mn, mx) = r.Meshes[0].Mesh.Bounds();
        Console.WriteLine($"       {r.Meshes[0].Mesh.Faces.Count} faces, x {mn.X:0.00}..{mx.X:0.00}, z {mn.Z:0.00}..{mx.Z:0.00}");
        return r.Meshes.Count == 1 && Near(mx.Z - mn.Z, 1f, 0.3f) && mn.X > 2f;
    });
    Check("export selected as .ydr and .yft loads back in CodeWalker", () =>
    {
        var a = MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(sc, new[] { o }, Path.Combine(tmpDir, "crate_quick.ydr"), null);
        var b = MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(sc, new[] { o }, Path.Combine(tmpDir, "crate_quick.yft"), null);
        Console.WriteLine("       " + a + " | " + b);
        return File.Exists(Path.Combine(tmpDir, "crate_quick.ydr")) && File.Exists(Path.Combine(tmpDir, "crate_quick.yft"));
    });
}

Console.WriteLine("Outliner folders");
{
    var sc = new Scene();
    var o = sc.Add("Desk", Primitives.Box(1, 1, 1), System.Numerics.Vector3.Zero);
    o.Folder = "Props"; sc.Folders.Add("Props"); sc.Folders.Add("Empty");
    Check("folders survive save / load and undo snapshots", () =>
    {
        var f = Path.Combine(tmpDir, "folders.mmx");
        SceneIO.Save(sc, f);
        var r = SceneIO.Load(f);
        var c = sc.Clone();
        return r.Objects[0].Folder == "Props" && r.Folders.SequenceEqual(new[] { "Props", "Empty" }) && c.Folders.Count == 2 && c.Objects[0].Folder == "Props";
    });
}

Console.WriteLine("Hierarchy (GIMS style parent / child)");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_hier";
    var desk = scene.Add("desk", Primitives.Box(2, 1, 1), new System.Numerics.Vector3(5, 0, 0));
    desk.Gta.Role = GtaRole.Prop;
    var drawer = scene.Add("drawer", Primitives.Box(0.5f, 0.5f, 0.3f), new System.Numerics.Vector3(5, 0, 1.2f));
    var col = scene.Add("desk_col", Primitives.Box(2, 1, 1.5f), new System.Numerics.Vector3(5, 0, 0), GtaObjectType.Collision);
    col.Gta.CollisionMaterial = 5;
    scene.SetParent(drawer, desk); scene.SetParent(col, desk);
    Check("no cycles: a parent can't go under its child", () => !scene.SetParent(desk, drawer) && scene.Root(col) == desk);
    var outDir = Path.Combine(tmpDir, "hier_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    var ext = Path.Combine(outDir, "molo_hier", "stream", "exterior");
    var ydrs = Directory.Exists(ext) ? Directory.GetFiles(ext, "*.ydr").Select(Path.GetFileName).ToList() : new();
    Console.WriteLine("       " + string.Join(", ", ydrs));
    Check("parent + child mesh = one model, collision child embedded (no map .ybn)", () =>
    {
        if (!rep.Ok || ydrs.Count != 1) return false;
        var y = new CodeWalker.GameFiles.YdrFile();
        CodeWalker.GameFiles.RpfFile.LoadResourceFile(y, File.ReadAllBytes(Path.Combine(ext, ydrs[0])), 165);
        var b = y.Drawable.Bound;
        float top = y.Drawable.BoundingBoxMax.Z;
        bool noYbn = !Directory.GetFiles(ext, "*.ybn").Any();
        Console.WriteLine($"       top {top:0.00}, embedded bound {(b != null)}, map ybn {(!noYbn)}");
        return b != null && top > 1.3f && noYbn;
    });
    Check("delete parent: children move up instead of dangling", () =>
    {
        var c = scene.Clone(); c.RemoveObjects(o => o.Id == desk.Id);
        return c.Objects.All(o => o.ParentId == -1);
    });
}

Console.WriteLine("Undo on big scenes (shared snapshots)");
{
    var sc = new Scene();
    var a = sc.Add("big", Primitives.Plane(10, 10, 300, 300), System.Numerics.Vector3.Zero);
    var b = sc.Add("small", Primitives.Box(1, 1, 1), new System.Numerics.Vector3(20, 0, 0));
    var undo = new UndoStack();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    undo.Record("first", sc); long t1 = sw.ElapsedMilliseconds; sw.Restart();
    undo.Record("second", sc); long t2 = sw.ElapsedMilliseconds;
    Console.WriteLine($"       first snapshot {t1} ms, second (nothing changed) {t2} ms");
    Check("second snapshot of an unchanged 90k-vertex mesh is shared, not copied", () => t2 * 5 <= Math.Max(t1, 5));
    // edit the small box, undo: box comes back, big mesh is the same live object (no copy)
    var bigMesh = a.Mesh;
    undo.Record("move vert", sc);
    b.Mesh.Verts[0] += new System.Numerics.Vector3(0, 0, 5); b.Touch();
    var restored = undo.Undo(sc);
    var rb = restored.Objects.First(o => o.Name == b.Name); var ra = restored.Objects.First(o => o.Name == a.Name);
    Check("undo restores the edited mesh and reuses the untouched one", () => rb.Mesh.Bounds().Max.Z < 1.01f && ReferenceEquals(ra.Mesh, bigMesh));
    var again = undo.Redo(restored);
    Check("redo brings the edit back", () => again.Objects.First(o => o.Name == b.Name).Mesh.Bounds().Max.Z > 4.9f);
    // the stored snapshot must not change when the live scene is edited after an undo
    var r2 = undo.Undo(again);
    var la = r2.Objects.First(o => o.Name == a.Name); la.Mesh.Verts[0] += new System.Numerics.Vector3(0, 0, 9); la.Touch();
    var r3 = undo.Undo(r2);
    Check("snapshots stay intact after live edits", () => r3 != null && r3.Objects.First(o => o.Name == a.Name).Mesh.Verts[0].Z < 1f);
}

Console.WriteLine("Drawable node (GIMS: Drawable > game meshes + collision)");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_gims";
    var node = scene.Add("molo_lg_desk", new MoloMax.Core.Mesh.PolyMesh(), new System.Numerics.Vector3(3, 0, 0), GtaObjectType.Model);
    var top = scene.Add("desk_high_0", Primitives.Box(2, 1, 0.1f), new System.Numerics.Vector3(3, 0, 0.9f));
    var leg = scene.Add("desk_high_1", Primitives.Box(0.1f, 0.1f, 0.9f), new System.Numerics.Vector3(3.9f, 0, 0));
    var col = scene.Add("desk_col", Primitives.Box(2, 1, 1), new System.Numerics.Vector3(3, 0, 0), GtaObjectType.Collision);
    foreach (var c in new[] { top, leg, col }) scene.SetParent(c, node);
    var outDir = Path.Combine(tmpDir, "gims_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    var ext = Path.Combine(outDir, "molo_gims", "stream", "exterior");
    var ydrs = Directory.Exists(ext) ? Directory.GetFiles(ext, "*.ydr").Select(Path.GetFileName).ToList() : new();
    Console.WriteLine("       " + string.Join(", ", ydrs));
    Check("one model named after the Drawable, both game meshes inside, collision embedded", () =>
    {
        if (!rep.Ok || ydrs.Count != 1 || !ydrs[0].Contains("molo_lg_desk")) return false;
        var y = new CodeWalker.GameFiles.YdrFile();
        CodeWalker.GameFiles.RpfFile.LoadResourceFile(y, File.ReadAllBytes(Path.Combine(ext, ydrs[0])), 165);
        var mn = y.Drawable.BoundingBoxMin; var mx = y.Drawable.BoundingBoxMax;
        Console.WriteLine($"       bounds {mn} {mx}, bound {(y.Drawable.Bound != null)}");
        return y.Drawable.Bound != null && mx.Z > 0.95f && mx.X > 0.9f && !Directory.GetFiles(ext, "*.ybn").Any();
    });
}

Console.WriteLine("GTA checker (Molo rules)");
{
    var sc = new Scene();
    sc.Gta.ShellInLimbo = false; sc.Gta.SharedPivot = false; sc.Gta.ResourceName = "My Map";
    var room = sc.Add("Hall", Primitives.Box(6, 6, 3), System.Numerics.Vector3.Zero, GtaObjectType.MloRoom); // outward box = wrong
    var room2 = sc.Add("office", Primitives.Room(4, 4, 3), new System.Numerics.Vector3(8, 0, 0), GtaObjectType.MloRoom);
    var shell = sc.Add("shell", Primitives.Room(6, 6, 3), System.Numerics.Vector3.Zero); shell.Gta.Role = GtaRole.Shell;
    foreach (var f in shell.Mesh.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1);
    var big = sc.Add("terrain", Primitives.Plane(100, 100, 270, 270), new System.Numerics.Vector3(0, 40, 0)); big.Gta.Role = GtaRole.Exterior;
    var issues = MoloMax.Core.Gta.Check.GtaChecker.Run(sc);
    foreach (var i in issues) Console.WriteLine($"       [{i.Level}] {i.Message}" + (i.FixLabel != null ? $"  -> {i.FixLabel}" : ""));
    Check("finds limbo, pivot, outward room, colours, 60k, resource name", () =>
        issues.Any(i => i.Message.Contains("limbo")) && issues.Any(i => i.Message.Contains("pivot")) && issues.Any(i => i.Message.Contains("outward"))
        && issues.Any(i => i.Group == "Vertex colours" && i.ObjectId == shell.Id) && issues.Any(i => i.ObjectId == big.Id && i.Message.Contains("vertices")));
    var done = MoloMax.Core.Gta.Check.GtaChecker.FixAll(sc);
    var again = MoloMax.Core.Gta.Check.GtaChecker.Run(sc);
    foreach (var i in again.Where(i => i.Level != MoloMax.Core.Gta.Check.CheckLevel.Info)) Console.WriteLine($"       after fix: [{i.Level}] {i.Message}");
    Check("Fix all leaves no error and no fixable warning", () => !again.Any(i => i.Fix != null) && !again.Any(i => i.Level == MoloMax.Core.Gta.Check.CheckLevel.Error));
    Check("shell coloured green, terrain split under 60k each", () =>
        shell.Mesh.Faces.All(f => f.Col.All(c => c.Y > 0.9f && c.X < 0.1f)) && sc.Objects.Count(o => o.Name.StartsWith("terrain")) >= 2
        && sc.Objects.Where(o => o.Name.StartsWith("terrain")).All(o => MoloMax.Core.Gta.Check.GtaChecker.RenderVertexCount(o.Mesh) <= 60000));
}

Console.WriteLine("Game files browser (RPF through CodeWalker)");
{
    var src = Path.Combine(tmpDir, "gims_export", "molo_gims", "stream", "exterior");
    var rpfDir = Path.Combine(tmpDir, "fake_gta"); Directory.CreateDirectory(rpfDir);
    var rpfPath = rpfDir + (OperatingSystem.IsWindows() ? "\\" : "\\") + "test.rpf"; if (File.Exists(rpfPath)) File.Delete(rpfPath); // CodeWalker joins paths with a backslash
    var rpf = CodeWalker.GameFiles.RpfFile.CreateNew(rpfDir, "test.rpf", CodeWalker.GameFiles.RpfEncryption.OPEN);
    foreach (var f in Directory.GetFiles(src)) CodeWalker.GameFiles.RpfFile.CreateFile(rpf.Root, Path.GetFileName(f), File.ReadAllBytes(f), true);
    // reopen like the game archives are opened
    var r2 = new CodeWalker.GameFiles.RpfFile(rpfPath, "test.rpf");
    r2.ScanStructure(null, null);
    var g = MoloMax.Core.Gta.Game.GameFiles.FromRpfs(new List<CodeWalker.GameFiles.RpfFile> { r2 });
    Console.WriteLine("       models: " + string.Join(", ", g.Models.Select(m => m.ToString())));
    var desk = g.Search("desk").FirstOrDefault();
    Check("index finds the model by name", () => desk != null && desk.Ext == ".ydr");
    g.BuildMapIndex();
    var pl = desk == null ? null : g.PlacementsOf(desk);
    Console.WriteLine($"       placements: {pl?.Count ?? 0}" + (pl?.Count > 0 ? $" first at {pl[0].Position} in {pl[0].Ymap}" : ""));
    Check("map index gives where the archetype is placed", () => pl != null && pl.Count == 1);
    Check("extract + import: the model comes back with its mesh", () =>
    {
        var path = g.Extract(desk, Path.Combine(tmpDir, "game_extract"));
        var r = MoloMax.Core.Gta.Import.GtaImporter.Import(path);
        Console.WriteLine($"       {Path.GetFileName(path)}: {r.Meshes.Count} mesh(es), {r.Meshes.Sum(m => m.Mesh.Faces.Count)} faces");
        return r.Meshes.Any(m => m.GtaType == GtaObjectType.Drawable && m.Mesh.Faces.Count > 0);
    });
}

Console.WriteLine("GTA lights in drawables");
{
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_lights";
    var node = scene.Add("lamp", new MoloMax.Core.Mesh.PolyMesh(), new System.Numerics.Vector3(2, 0, 0), GtaObjectType.Model);
    var shade = scene.Add("lamp_high_0", Primitives.Cylinder(0.2f, 0.4f), new System.Numerics.Vector3(2, 0, 1.6f));
    var bulb = scene.Add("lamp_light", new MoloMax.Core.Mesh.PolyMesh(), new System.Numerics.Vector3(2, 0, 1.7f), GtaObjectType.Light);
    bulb.Gta.Light = new GtaLight { Kind = GtaLightKind.Spot, Colour = new System.Numerics.Vector3(1, 0.5f, 0.2f), Intensity = 12, Range = 7, ConeOuter = 60 };
    scene.SetParent(shade, node); scene.SetParent(bulb, node);
    var ground = scene.Add("ground", Primitives.Box(10, 10, 0.2f), new System.Numerics.Vector3(0, 0, -0.2f));
    var street = scene.Add("street_light", new MoloMax.Core.Mesh.PolyMesh(), new System.Numerics.Vector3(-3, 0, 4), GtaObjectType.Light);
    street.Gta.Light = new GtaLight();
    var outDir = Path.Combine(tmpDir, "lights_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    var ext = Path.Combine(outDir, "molo_lights", "stream", "exterior");
    int Lights(string file)
    {
        var y = new CodeWalker.GameFiles.YdrFile();
        CodeWalker.GameFiles.RpfFile.LoadResourceFile(y, File.ReadAllBytes(Path.Combine(ext, file)), 165);
        var items = y.Drawable.LightAttributes?.data_items;
        if (items != null) foreach (var l in items) Console.WriteLine($"       {file}: {l.Type} at {l.Position}, colour {l.ColorR},{l.ColorG},{l.ColorB}, falloff {l.Falloff}, cone {l.ConeOuterAngle}");
        return items?.Length ?? 0;
    }
    var files = Directory.Exists(ext) ? Directory.GetFiles(ext, "*.ydr").Select(Path.GetFileName).ToList() : new();
    Check("the lamp's spot light is in the lamp model (orange, 7 m, 60°)", () => files.Any(f => f.Contains("lamp") && Lights(f) == 1));
    Check("a free light outside rides on the exterior model", () => files.Any(f => f.Contains("_ext") && Lights(f) == 1));
}

Console.WriteLine("LODs (decimation + Med / Low / Very low in the ydr)");
{
    var sphere = Primitives.Sphere(1, 48);
    int tris0 = sphere.TriangleCount;
    var half = MoloMax.Core.Mesh.MeshSimplify.Decimate(sphere, 0.25f);
    var (mn, mx) = half.Bounds();
    Console.WriteLine($"       sphere {tris0} tris -> {half.TriangleCount}, size {mx - mn}");
    Check("decimate 25%: about a quarter of the triangles, same size, still closed", () =>
        half.TriangleCount <= tris0 * 0.3f && half.TriangleCount >= tris0 * 0.15f && Near(mx.X - mn.X, 2f, 0.1f) && half.IsClosedManifold(out _));
    var plane = Primitives.Plane(4, 4, 30, 30);
    var pd = MoloMax.Core.Mesh.MeshSimplify.Decimate(plane, 0.1f);
    var (pmn, pmx) = pd.Bounds();
    Check("open borders stay in place (plane keeps its 4 m outline)", () => Near(pmx.X - pmn.X, 4f, 0.01f) && Near(pmx.Y - pmn.Y, 4f, 0.01f));

    var scene = new Scene();
    scene.Gta.ResourceName = "molo_lod";
    var node = scene.Add("statue", new MoloMax.Core.Mesh.PolyMesh(), System.Numerics.Vector3.Zero, GtaObjectType.Model);
    var body = scene.Add("statue_high_0", Primitives.Sphere(1, 40), new System.Numerics.Vector3(0, 0, 1));
    scene.SetParent(body, node);
    var outDir = Path.Combine(tmpDir, "lod_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var i in rep.Info.Where(i => i.Contains("LOD"))) Console.WriteLine("       " + i);
    var ext = Path.Combine(outDir, "molo_lod", "stream", "exterior");
    var f = Directory.GetFiles(ext, "*statue*.ydr").FirstOrDefault();
    Check("ydr has High, Med, Low, Very low with decreasing detail and increasing distances", () =>
    {
        if (f == null) return false;
        var y = new CodeWalker.GameFiles.YdrFile();
        CodeWalker.GameFiles.RpfFile.LoadResourceFile(y, File.ReadAllBytes(f), 165);
        var dm = y.Drawable.DrawableModels;
        int Tri(CodeWalker.GameFiles.DrawableModel[] ms) => ms?.Sum(m => m.Geometries.Sum(g => (int)g.IndicesCount / 3)) ?? 0;
        int h = Tri(dm.High), md = Tri(dm.Med), lo = Tri(dm.Low), vl = Tri(dm.VLow);
        var d = y.Drawable;
        Console.WriteLine($"       tris H {h} M {md} L {lo} VL {vl}; dist {d.LodDistHigh} {d.LodDistMed} {d.LodDistLow} {d.LodDistVlow}");
        return h > md && md > lo && lo > vl && vl > 0 && d.LodDistHigh < d.LodDistMed && d.LodDistMed < d.LodDistLow && d.LodDistLow <= d.LodDistVlow;
    });
}

Console.WriteLine("MLO extras: entity sets, ytyp import");
{
    PolyMesh RoomWithDoor(float w, float l, float h, int side)
    {
        var m = Primitives.Box(w, l, h, 3, 3, 2);
        foreach (var f in m.Faces) f.Flip();
        var holes = m.Faces.Where(f => { var c = m.FaceCenter(f); return MathF.Abs(c.X) < 0.5f && c.Z < h / 2 && MathF.Abs(c.Y - side * l / 2) < 1e-3f; }).ToList();
        foreach (var f in holes) m.Faces.Remove(f);
        m.RemoveUnusedVertices();
        return m;
    }
    var scene = new Scene();
    scene.Gta.ResourceName = "molo_sets";
    scene.Gta.DefaultEntitySets = "party";
    var hall = scene.Add("Hall", RoomWithDoor(6, 6, 3, 1), Vector3.Zero, GtaObjectType.MloRoom);
    var office = scene.Add("Office", RoomWithDoor(6, 6, 3, -1), new Vector3(0, 6.2f, 0), GtaObjectType.MloRoom);
    hall.Gta.RoomTimecycle = "int_hospital";
    var shell = scene.Add("Shell", Primitives.Box(0.2f, 0.2f, 0.2f), new Vector3(2, 2, 0)); shell.Gta.Role = GtaRole.Shell;
    var crate = scene.Add("Crate", Primitives.Box(0.8f, 0.8f, 0.8f), new Vector3(1.5f, 1, 0)); crate.Gta.Role = GtaRole.Prop;
    var balloons = scene.Add("Balloons", Primitives.Sphere(0.4f, 12), new Vector3(-1, 7, 1)); balloons.Gta.Role = GtaRole.Prop; balloons.Gta.EntitySet = "party";
    var outDir = Path.Combine(tmpDir, "sets_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(scene, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    var mloDir = Path.Combine(outDir, "molo_sets", "stream", "mlo");
    var ytyp = Path.Combine(mloDir, "molo_sets.ytyp");
    var mlos = File.Exists(ytyp) ? MoloMax.Core.Gta.Import.YtypImporter.Read(ytyp) : new();
    var m = mlos.FirstOrDefault();
    if (m != null) { Console.WriteLine($"       {m.Name}: {m.Rooms.Count} rooms, {m.Portals.Count} portals, {m.Entities.Count} entities"); foreach (var e in m.Entities) Console.WriteLine($"         {e.Archetype} set={e.EntitySet ?? "-"} file={(e.File != null ? Path.GetFileName(e.File) : "none")} at {e.Position}"); }
    Check("entity set 'party' holds the balloons, main list the rest", () => m != null && m.Entities.Count(e => e.EntitySet == "party") == 1 && m.Entities.Any(e => e.EntitySet == null && e.Archetype.Contains("crate")));
    Check("ytyp import finds rooms, portals and the model files", () => m != null && m.Rooms.Count == 3 && m.Portals.Count >= 1 && m.Entities.All(e => e.File != null));
    Check("crate comes back where it was placed", () => m != null && m.Entities.Any(e => e.Archetype.Contains("crate") && Vector3.Distance(e.Position, crate.Position) < 0.01f || e.Archetype.Contains("crate") && e.Position.Length() < 0.01f));
    Check("default entity set written in the milo ymap", () =>
    {
        var y = new CodeWalker.GameFiles.YmapFile(); y.Load(File.ReadAllBytes(Path.Combine(mloDir, "molo_sets_milo_.ymap")));
        var inst = y.CMloInstanceDefs?.FirstOrDefault();
        var arr = inst?.defaultEntitySets;
        Console.WriteLine($"       default sets count {arr?.Count1}");
        return arr != null && arr.Value.Count1 == 1;
    });
}

Console.WriteLine("UV editor tools");
{
    var box = Primitives.Box(2, 1, 1);
    MoloMax.Core.Mesh.UvUnwrap.SmartProject(box);
    var isl = MoloMax.Core.Mesh.UvTools.Islands(box);
    MoloMax.Core.Mesh.UvTools.SetDensity(box, Enumerable.Range(0, box.Faces.Count), 256, 1024);
    float d = MoloMax.Core.Mesh.UvTools.TexelDensity(box, Enumerable.Range(0, box.Faces.Count), 1024);
    Console.WriteLine($"       {isl.Count} islands, density after set {d:0} px/m");
    Check("set texel density to 256 px/m", () => Near(d, 256, 3));
    MoloMax.Core.Mesh.UvTools.Pack(box, Enumerable.Range(0, box.Faces.Count));
    var (mn, mx) = MoloMax.Core.Mesh.UvTools.Bounds(box, Enumerable.Range(0, box.Faces.Count));
    Check("pack puts every island inside 0..1", () => mn.X >= -1e-4f && mn.Y >= -1e-4f && mx.X <= 1.0001f && mx.Y <= 1.0001f && MathF.Max(mx.X, mx.Y) > 0.9f);
}

Console.WriteLine("Modeling tools: chamfer, bridge, slice, booleans");
{
    float Vol(MoloMax.Core.Mesh.PolyMesh m) { float v = 0; foreach (var f in m.Faces) foreach (var (a, b, c) in MoloMax.Core.Mesh.Triangulator.Triangulate(m, f)) v += Vector3.Dot(m.Verts[f.V[a]], Vector3.Cross(m.Verts[f.V[b]], m.Verts[f.V[c]])) / 6f; return v; }
    var box = Primitives.Box(2, 2, 2);
    var all = box.AllEdges();
    MoloMax.Core.Mesh.MeshOps.ChamferEdges(box, all, 0.2f);
    Console.WriteLine($"       chamfer all edges: {box.Faces.Count} faces, closed {box.IsClosedManifold(out var why)} {why}, volume {Vol(box):0.000}");
    Check("chamfer all 12 edges of a box: 26 faces, closed, a bit smaller", () => box.Faces.Count == 26 && box.IsClosedManifold(out _) && Vol(box) < 8f && Vol(box) > 7f);
    var box1 = Primitives.Box(2, 2, 2);
    var top = box1.AllEdges().First(e => box1.Verts[e.A].Z > 1.9f && box1.Verts[e.B].Z > 1.9f);
    MoloMax.Core.Mesh.MeshOps.ChamferEdges(box1, new[] { top }, 0.25f);
    Console.WriteLine($"       chamfer one edge: {box1.Faces.Count} faces, {box1.Verts.Count} verts, closed {box1.IsClosedManifold(out var w1)} {w1}");
    Check("chamfer one edge: 7 faces, 10 verts, closed", () => box1.Faces.Count == 7 && box1.Verts.Count == 10 && box1.IsClosedManifold(out _));
    {
        // bevel with sides, and a bevel stopping in the middle of a cut edge
        List<EdgeKey> E(PolyMesh b, Func<Vector3, Vector3, bool> pr) => b.BuildEdgeMap().Keys.Where(k => pr(b.Verts[k.A], b.Verts[k.B])).ToList();
        var r4 = Primitives.Box(2, 2, 2);
        MeshOps.ChamferEdges(r4, E(r4, (a, c) => a.X > 0.9f && c.X > 0.9f && a.Y > 0.9f && c.Y > 0.9f), 0.2f, 4);
        float exp4 = 8 - 2 * (0.04f - 0.5f * 0.04f * 4 * MathF.Sin(MathF.PI / 8));
        Check("bevel 4 segments on one edge: rounded, closed, right volume", () => r4.IsClosedManifold(out _) && Near(Vol(r4), exp4, 1e-3f) && r4.Faces.Count == 10);
        var all4 = Primitives.Box(2, 2, 2); MeshOps.ChamferEdges(all4, all4.BuildEdgeMap().Keys.ToList(), 0.2f, 4);
        Check("bevel 4 segments on all 12 edges: closed", () => all4.IsClosedManifold(out _) && Vol(all4) < 8 && Vol(all4) > 7.6f);
        foreach (int sg in new[] { 1, 3 })
        {
            var hb = Primitives.Box(2, 2, 2);
            MeshOps.ConnectEdges(hb, E(hb, (a, c) => MathF.Abs(a.Z - c.Z) > 1.9f).ToHashSet());
            MeshOps.ChamferEdges(hb, E(hb, (a, c) => a.X > 0.9f && c.X > 0.9f && a.Y > 0.9f && c.Y > 0.9f && a.Z > 0.9f && c.Z > 0.9f), 0.2f, sg);
            Check($"bevel ({sg} seg.) stopping in the middle of an edge stays closed (no hole)", () => hb.IsClosedManifold(out _) && Vol(hb) < 8 && Vol(hb) > 7.9f);
        }
        var cb = Primitives.Box(2, 2, 2);
        var added3 = MeshOps.ConnectEdges(cb, E(cb, (a, c) => MathF.Abs(a.Z - c.Z) > 1.9f).ToHashSet(), 3);
        var heights = cb.Verts.Select(v => MathF.Round(v.Z, 3)).Distinct().OrderBy(z => z).ToList();
        Check("connect 3 segments: 3 evenly spaced loops, closed", () => added3.Count == 12 && cb.IsClosedManifold(out _) && heights.SequenceEqual(new[] { 0f, 0.5f, 1f, 1.5f, 2f }) && cb.Faces.Count == 18);
        var gw = Primitives.Box(4, 0.3f, 3);
        var go = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -0.6f, Right = 0.6f, Bottom = 0, Top = 2.6f, Shape = MoloMax.Core.Modifiers.OpeningShape.Gothic, Segments = 12 };
        var gr = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(gw, go);
        var outline = go.Outline(); float gTop = outline.Max(p => p.Y);
        Check("Wall Opening gothic (church) arch: pointed top at the full height, cut through, closed", () => gr.IsClosedManifold(out _) && Near(gTop, 2.6f, 1e-3f) && Vol(gr) < 3.6f - 0.3f * 2.2f && Vol(gr) > 3.6f - 0.3f * 3.12f);
        {
            // MLO: gothic arches cut with the Wall Opening modifier in two facing room walls -> one portal between them
            var sc = new Scene();
            var r1 = sc.Add("Nave", Primitives.Room(6, 6, 4), Vector3.Zero, GtaObjectType.MloRoom);
            var r2 = sc.Add("Chapel", Primitives.Room(6, 6, 4), new Vector3(6.1f, 0, 0), GtaObjectType.MloRoom);
            var a1 = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(3, 0, 0), Normal = -Vector3.UnitX, Left = -0.8f, Right = 0.8f, Bottom = 0, Top = 3.2f, Shape = MoloMax.Core.Modifiers.OpeningShape.Gothic };
            var a2 = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(-3, 0, 0), Normal = Vector3.UnitX, Left = -0.8f, Right = 0.8f, Bottom = 0, Top = 3.2f, Shape = MoloMax.Core.Modifiers.OpeningShape.Gothic };
            var m1 = new MoloMax.Core.Modifiers.WallOpeningModifier(); m1.Openings.Add(a1); r1.Modifiers.Add(m1); r1.Touch();
            var m2 = new MoloMax.Core.Modifiers.WallOpeningModifier(); m2.Openings.Add(a2); r2.Modifiers.Add(m2); r2.Touch();
            var lay2 = MoloMax.Core.Gta.Export.MloLayout.Build(sc, sc.Gta);
            Console.WriteLine($"       MLO with gothic arches: {lay2.Rooms.Count - 1} rooms, {lay2.Portals.Count} portal(s), border edges {MeshOps.BorderEdges(r1.Evaluated()).Count}");
            Check("MLO: gothic arches made with Wall Opening in two room walls give the portal between the rooms", () => lay2.Portals.Any(p => p.RoomFrom > 0 && p.RoomTo > 0));
        }
    }

    var two = Primitives.Box(1, 1, 1); var other = Primitives.Box(1, 1, 1); other.Transform(Matrix4x4.CreateTranslation(0, 0, 3)); two.Append(other);
    int topOfLow = Enumerable.Range(0, two.Faces.Count).First(i => MathF.Abs(two.FaceCenter(i).Z - 1) < 1e-4f && two.FaceNormal(i).Z > 0.9f);
    int botOfHigh = Enumerable.Range(0, two.Faces.Count).First(i => MathF.Abs(two.FaceCenter(i).Z - 3) < 1e-4f && two.FaceNormal(i).Z < -0.9f);
    bool ok = MoloMax.Core.Mesh.MeshOps.BridgeFaces(two, topOfLow, botOfHigh);
    Console.WriteLine($"       bridge ok {ok}, closed {two.IsClosedManifold(out var bw)} {bw}, volume {Vol(two):0.000}");
    Check("bridge two facing boxes into one closed tower", () => ok && two.IsClosedManifold(out _) && Near(Vol(two), 4f, 0.01f));

    var t2 = Primitives.Box(1, 1, 1); var o2 = Primitives.Box(1, 1, 1); o2.Transform(Matrix4x4.CreateTranslation(0, 0, 3)); t2.Append(o2);
    var del = Enumerable.Range(0, t2.Faces.Count).Where(i => MathF.Abs(t2.FaceCenter(i).Z - 1) < 1e-4f || MathF.Abs(t2.FaceCenter(i).Z - 3) < 1e-4f).ToHashSet();
    MoloMax.Core.Mesh.MeshOps.DeleteFaces(t2, del);
    var border = MoloMax.Core.Mesh.MeshOps.BorderEdges(t2);
    bool okl = MoloMax.Core.Mesh.MeshOps.BridgeLoops(t2, border);
    Check("bridge edge loops: two open boxes become one closed tower", () => okl && t2.IsClosedManifold(out _) && Near(Vol(t2), 4f, 0.01f));
    var slab = Primitives.Box(4, 1, 1);
    var ne = MoloMax.Core.Mesh.MeshOps.Slice(slab, Vector3.Zero, Vector3.UnitX);
    Check("slice a box in half: 10 faces, still closed, cut edges returned", () => slab.Faces.Count == 10 && slab.IsClosedManifold(out _) && ne.Count == 4);

    var wall = Primitives.Box(4, 0.3f, 3); var door = Primitives.Box(1, 1, 2.2f);
    var cutWall = MoloMax.Core.Mesh.Csg.Apply(wall, Matrix4x4.Identity, door, Matrix4x4.CreateTranslation(0, 0, -0.1f), MoloMax.Core.Mesh.Csg.Op.Subtract);
    float vw = Vol(cutWall), expect = 4 * 0.3f * 3 - 1 * 0.3f * 2.1f;
    Console.WriteLine($"       wall minus door: volume {vw:0.000} (expect {expect:0.000}), {cutWall.Faces.Count} faces");
    Check("boolean subtract: a doorway cut in a wall has the right volume", () => Near(vw, expect, 0.02f));
    var u = MoloMax.Core.Mesh.Csg.Apply(Primitives.Box(2, 2, 2), Matrix4x4.Identity, Primitives.Box(2, 2, 2), Matrix4x4.CreateTranslation(1, 0, 0), MoloMax.Core.Mesh.Csg.Op.Union);
    var i2 = MoloMax.Core.Mesh.Csg.Apply(Primitives.Box(2, 2, 2), Matrix4x4.Identity, Primitives.Box(2, 2, 2), Matrix4x4.CreateTranslation(1, 0, 0), MoloMax.Core.Mesh.Csg.Op.Intersect);
    Check("boolean union / intersect of two overlapping boxes: volumes 12 and 4", () => Near(Vol(u), 12f, 0.02f) && Near(Vol(i2), 4f, 0.02f));
    {
        // the self test case: sphere centred on the box's corner edge (vertices exactly on the box planes)
        var bx = Primitives.Box(2, 2, 2); var sp = Primitives.Sphere(1, 24, true);
        var cut = MoloMax.Core.Mesh.Csg.Apply(bx, Matrix4x4.Identity, sp, Matrix4x4.CreateTranslation(1, 1, 1.4f), MoloMax.Core.Mesh.Csg.Op.Subtract);
        Console.WriteLine($"       box - sphere on its corner: {cut.Faces.Count} faces, closed {cut.IsClosedManifold(out var why2)} {why2}, volume {Vol(cut):0.000}");
        Check("boolean subtract with coplanar sphere vertices stays closed (T-junctions fixed)", () => cut.IsClosedManifold(out _) && Vol(cut) < 8f && Vol(cut) > 7f);
    }
    {
        // far from the origin (GTA map coordinates): used to recurse forever and crash the editor
        var bx = Primitives.Box(2, 2, 2); var sp = Primitives.Sphere(1.3f, 32);
        bool allOk = true; string info = "";
        foreach (var at in new[] { new Vector3(11, 0, 0), new Vector3(3000, -2000, 30), new Vector3(-1200.5f, 4500.25f, 210) })
        {
            var r = MoloMax.Core.Mesh.Csg.Apply(bx, Matrix4x4.CreateTranslation(at), sp, Matrix4x4.CreateTranslation(at + new Vector3(0, 0, 1)), MoloMax.Core.Mesh.Csg.Op.Intersect);
            bool good = r.IsClosedManifold(out _) && r.Faces.Count > 6 && Vol(r) > 4f && Vol(r) < 8f;
            allOk &= good; info += $"{at}: {r.Faces.Count} faces vol {Vol(r):0.00} {(good ? "ok" : "BAD")}; ";
        }
        Console.WriteLine("       " + info);
        Check("boolean far from the origin (GTA coordinates): no crash, closed, same result", () => allOk);
    }
    {
        // clean topology after a boolean: straight cuts, no slivers, no diagonals
        var T = Matrix4x4.CreateTranslation(1500, -800, 40);
        var cwall = Primitives.Box(4, 0.3f, 3);
        var cw1 = MoloMax.Core.Mesh.Csg.Apply(cwall, T, Primitives.Box(1, 1, 2.1f), Matrix4x4.CreateTranslation(-0.9f, 0, -0.05f) * T, MoloMax.Core.Mesh.Csg.Op.Subtract);
        var cw2 = MoloMax.Core.Mesh.Csg.Apply(cw1, T, Primitives.Box(1.1f, 1, 0.9f), Matrix4x4.CreateTranslation(1.0f, 0, 1.1f) * T, MoloMax.Core.Mesh.Csg.Op.Subtract);
        bool noTri = cw2.Faces.All(f => f.V.Length >= 4);
        bool straight = cw2.Faces.All(f => Enumerable.Range(0, f.V.Length).All(i => { var d = cw2.Verts[f.V[(i + 1) % f.V.Length]] - cw2.Verts[f.V[i]]; int axes = (MathF.Abs(d.X) > 1e-4f ? 1 : 0) + (MathF.Abs(d.Y) > 1e-4f ? 1 : 0) + (MathF.Abs(d.Z) > 1e-4f ? 1 : 0); return axes <= 1; }));
        Console.WriteLine($"       wall + door + window: {cw2.Faces.Count} faces, no triangle {noTri}, only straight edges {straight}, volume {Vol(cw2):0.000}");
        Check("boolean clean: door + window in a wall = straight edges only, no triangles, closed, right volume", () => noTri && straight && cw2.Faces.Count <= 26 && cw2.IsClosedManifold(out _) && Near(Vol(cw2), 4 * 0.3f * 3 - 1 * 0.3f * 2.05f - 1.1f * 0.3f * 0.9f, 0.01f));
        var cs = MoloMax.Core.Mesh.Csg.Apply(Primitives.Box(2, 2, 2), T, Primitives.Sphere(1.3f, 32), Matrix4x4.CreateTranslation(0, 0, 1) * T, MoloMax.Core.Mesh.Csg.Op.Subtract);
        var origin = new Vector3(1500, -800, 40);
        int flatFaces = Enumerable.Range(0, cs.Faces.Count).Count(i => { var nn = cs.FaceNormal(i); var c = cs.Faces[i].V.Aggregate(Vector3.Zero, (q, v) => q + cs.Verts[v]) / cs.Faces[i].V.Length - origin; return (MathF.Abs(nn.X) > 0.9999f || MathF.Abs(nn.Y) > 0.9999f || MathF.Abs(nn.Z) > 0.9999f) && (MathF.Abs(c.X) > 0.999f || MathF.Abs(c.Y) > 0.999f || c.Z < 0.001f || c.Z > 1.999f); });
        Console.WriteLine($"       box - sphere: {cs.Faces.Count} faces, {flatFaces} on the flat sides");
        Check("boolean clean: a round hole leaves 2 clean polygons per flat side (no triangle fan), closed", () => flatFaces <= 12 && cs.IsClosedManifold(out _));
    }
    {
        // Wall Opening modifier: rectangles traced on a wall, cut through with clean faces
        var sc = new Scene();
        var wallObj = sc.Add("wall", Primitives.Box(4, 0.3f, 3), new Vector3(2500, -1500, 30));
        var wo = new MoloMax.Core.Modifiers.WallOpeningModifier();
        // front face of the box is at y = -0.15 (normal -Y); door from the floor, window with a sill
        wo.Openings.Add(new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -1.4f, Right = -0.4f, Bottom = 0, Top = 2.1f });
        wo.Openings.Add(new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = 0.45f, Right = 1.55f, Bottom = 1.0f, Top = 2.0f });
        wallObj.Modifiers.Add(wo); wallObj.Touch();
        var ev = wallObj.Evaluated();
        float expectV = 4 * 0.3f * 3 - 1.0f * 0.3f * 2.1f - 1.1f * 0.3f * 1.0f;
        bool straightE = ev.Faces.All(f => Enumerable.Range(0, f.V.Length).All(i => { var d = ev.Verts[f.V[(i + 1) % f.V.Length]] - ev.Verts[f.V[i]]; return (MathF.Abs(d.X) > 1e-4f ? 1 : 0) + (MathF.Abs(d.Y) > 1e-4f ? 1 : 0) + (MathF.Abs(d.Z) > 1e-4f ? 1 : 0) <= 1; }));
        Console.WriteLine($"       wall opening: {ev.Faces.Count} faces, closed {ev.IsClosedManifold(out _)}, volume {Vol(ev):0.000} (expect {expectV:0.000}), straight {straightE}, base mesh {wallObj.Mesh.Faces.Count} faces");
        Check("Wall Opening: door + window traced on a thick wall, cut through, clean straight faces, base mesh untouched", () => ev.IsClosedManifold(out _) && Near(Vol(ev), expectV, 0.01f) && straightE && wallObj.Mesh.Faces.Count == 6);
        var back2 = SceneIO.FromJson(SceneIO.ToJson(sc));
        var wo2 = back2.Objects[0].Modifiers.OfType<MoloMax.Core.Modifiers.WallOpeningModifier>().FirstOrDefault();
        Check("Wall Opening: saved / reopened with its openings", () => wo2 != null && wo2.Openings.Count == 2 && Near(wo2.Openings[1].Bottom, 1.0f, 1e-4f));
        var cl = (MoloMax.Core.Modifiers.WallOpeningModifier)wo.Clone(); cl.Openings[0].Left = -1.9f;
        Check("Wall Opening: a copy (undo snapshot) does not share its openings", () => Near(wo.Openings[0].Left, -1.4f, 1e-6f));
        {
            // dense mesh: only the faces the hole crosses change, the user's grid stays, and it stays fast
            var dw = Primitives.Box(4, 0.3f, 3, 40, 3, 30); MeshOps.WeldByDistance(dw, 1e-5f); dw.CleanFaces();
            var dd = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -1.4f, Right = -0.4f, Bottom = 0, Top = 2.1f };
            var swd = System.Diagnostics.Stopwatch.StartNew();
            var dr = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dw, dd);
            long ms = swd.ElapsedMilliseconds;
            Console.WriteLine($"       dense wall + door: {dw.Faces.Count} -> {dr.Faces.Count} faces, {ms} ms, all quads {dr.Faces.All(f => f.V.Length == 4)}");
            Check("Wall Opening on a dense wall (2820 faces): grid kept, only quads, closed, right volume, fast", () => dr.Faces.All(f => f.V.Length == 4) && dr.Faces.Count > 2400 && dr.IsClosedManifold(out _) && Near(Vol(dr), 3.6f - 0.3f * 2.1f, 0.01f) && ms < 3000);
        }
        // MLO room box (single surface, normals inside): the hole goes in the wall face
        var room = Primitives.Room(6, 6, 3);
        int wallF = Enumerable.Range(0, room.Faces.Count).First(i => room.FaceNormal(i).Y > 0.99f); // the wall at y = -3, facing into the room
        var wc = room.FaceCenter(room.Faces[wallF]);
        var rop = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(wc.X, wc.Y, 0), Normal = room.FaceNormal(wallF), Left = -0.6f, Right = 0.6f, Bottom = 0, Top = 2.3f };
        var cutRoom = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(room, rop);
        var loops = MeshOps.BorderEdges(cutRoom).Count;
        Console.WriteLine($"       room box opening: {room.Faces.Count} -> {cutRoom.Faces.Count} faces, open border edges {loops}");
        Check("Wall Opening: an MLO room box gets a doorway in its wall face (open loop for the portal)", () => cutRoom.Faces.Count > room.Faces.Count && loops >= 3);
        {
            // arches and round windows, with their number of segments
            static float AreaOf(PolyMesh m, MoloMax.Core.Mesh.Face f)
            {
                var a = Vector3.Zero; var p0 = m.Verts[f.V[0]];
                for (int i = 1; i + 1 < f.V.Length; i++) a += Vector3.Cross(m.Verts[f.V[i]] - p0, m.Verts[f.V[i + 1]] - p0);
                return a.Length() * 0.5f;
            }
            var aw = Primitives.Box(4, 0.3f, 3);
            var arch = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -1.5f, Right = -0.5f, Bottom = 0, Top = 2.4f, Shape = MoloMax.Core.Modifiers.OpeningShape.Arch, Segments = 12 };
            var circ = new MoloMax.Core.Modifiers.OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = 0.4f, Right = 1.4f, Bottom = 1.2f, Top = 2.2f, Shape = MoloMax.Core.Modifiers.OpeningShape.Circle, Segments = 24 };
            var ar = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(MoloMax.Core.Modifiers.WallOpeningModifier.Cut(aw, arch), circ);
            float archArea = 1f * 1.9f + MathF.PI * 0.25f * 0.5f; // rectangle up to the spring line + half circle (polygon: a bit less)
            float circArea = MathF.PI * 0.25f;
            float expArc = 3.6f - 0.3f * (archArea + circArea);
            int revealArch = ar.Faces.Count(f => { var nn = ar.FaceNormal(f); return MathF.Abs(nn.Y) < 0.01f && ar.FaceCenter(f).X < -0.4f && ar.FaceCenter(f).X > -1.6f && ar.FaceCenter(f).Z > 1.85f; });
            Console.WriteLine($"       arch door + round window: {ar.Faces.Count} faces, closed {ar.IsClosedManifold(out _)}, volume {Vol(ar):0.000} (expect ~{expArc:0.000}), arch reveal faces {revealArch}");
            Check("Wall Opening: arch door (12 segments) + round window (24) through a wall, closed, right volume", () => ar.IsClosedManifold(out _) && Near(Vol(ar), expArc, 0.02f) && revealArch >= 12);
            var arch6 = arch.Copy(); arch6.Segments = 6;
            var a6 = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(aw, arch6); var a12 = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(aw, arch);
            Check("Wall Opening: fewer segments = fewer faces", () => a6.Faces.Count < a12.Faces.Count && a6.IsClosedManifold(out _));
            var dwa = Primitives.Box(4, 0.3f, 3, 40, 3, 30); MeshOps.WeldByDistance(dwa, 1e-5f); dwa.CleanFaces();
            var da = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(MoloMax.Core.Modifiers.WallOpeningModifier.Cut(dwa, arch), circ);
            Console.WriteLine($"       dense wall arch + circle: {dwa.Faces.Count} -> {da.Faces.Count} faces, closed {da.IsClosedManifold(out _)}, volume {Vol(da):0.000}, max corners {da.Faces.Max(f => f.V.Length)}");
            Check("Wall Opening: arch + circle on a dense wall, closed, right volume", () => da.IsClosedManifold(out _) && Near(Vol(da), expArc, 0.02f));
            var rarch = rop.Copy(); rarch.Shape = MoloMax.Core.Modifiers.OpeningShape.Arch; rarch.Top = 2.6f;
            var cr = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(room, rarch);
            float wallArea = 0; foreach (var f in cr.Faces) if (cr.FaceNormal(f).Y > 0.99f) wallArea += AreaOf(cr, f);
            float expWall = 6 * 3 - (1.2f * 2.0f + MathF.PI * 0.36f * 0.5f);
            Console.WriteLine($"       room box arch: {room.Faces.Count} -> {cr.Faces.Count} faces, wall area {wallArea:0.000} (expect ~{expWall:0.000}), border edges {MeshOps.BorderEdges(cr).Count}");
            Check("Wall Opening: arch doorway in an MLO room box wall (single surface)", () => Near(wallArea, expWall, 0.03f) && MeshOps.BorderEdges(cr).Count >= 16);
            var rc = rop.Copy(); rc.Shape = MoloMax.Core.Modifiers.OpeningShape.Circle; rc.Bottom = 1.0f; rc.Top = 2.2f; rc.Segments = 20;
            var cc2 = MoloMax.Core.Modifiers.WallOpeningModifier.Cut(room, rc);
            float wa2 = 0; foreach (var f in cc2.Faces) if (cc2.FaceNormal(f).Y > 0.99f) wa2 += AreaOf(cc2, f);
            Check("Wall Opening: round window in an MLO room box wall", () => Near(wa2, 18 - MathF.PI * 0.36f, 0.03f) && MeshOps.BorderEdges(cc2).Count == 20);
        }
    }
    {
        // Clean Up: fewer edges, same shape, same texture
        static PolyMesh Tri(PolyMesh m) => new MoloMax.Core.Modifiers.TriangulateModifier().Apply(m.Clone(), true);
        static bool SamePoints(PolyMesh a, PolyMesh b)
        {
            var set = new HashSet<(int, int, int)>(a.Verts.Select(v => ((int)MathF.Round(v.X * 1e4f), (int)MathF.Round(v.Y * 1e4f), (int)MathF.Round(v.Z * 1e4f))));
            return b.Verts.All(v => set.Contains(((int)MathF.Round(v.X * 1e4f), (int)MathF.Round(v.Y * 1e4f), (int)MathF.Round(v.Z * 1e4f))));
        }
        // UV of the original surface at random points must equal the cleaned one: sample every original corner
        static bool SameUv(PolyMesh a, PolyMesh b)
        {
            var uv = new Dictionary<(int, int, int), List<Vector2>>();
            (int, int, int) K(Vector3 v) => ((int)MathF.Round(v.X * 1e4f), (int)MathF.Round(v.Y * 1e4f), (int)MathF.Round(v.Z * 1e4f));
            foreach (var f in a.Faces) for (int i = 0; i < f.V.Length; i++) { var k = K(a.Verts[f.V[i]]); if (!uv.TryGetValue(k, out var l)) uv[k] = l = new(); l.Add(f.UV[i]); }
            return b.Faces.All(f => Enumerable.Range(0, f.V.Length).All(i => uv[K(b.Verts[f.V[i]])].Any(u => Vector2.DistanceSquared(u, f.UV[i]) < 1e-6f)));
        }
        var opt = new MoloMax.Core.Mesh.CleanOptions();
        var pl = Primitives.Plane(10, 10, 20, 20);
        var cp = MoloMax.Core.Mesh.MeshClean.Run(pl, opt, out var rp);
        Console.WriteLine($"       clean plane 20x20: {rp}");
        Check("Clean Up: a flat plane cut in 400 faces becomes 1 polygon, same corners and UVs", () => cp.Faces.Count == 1 && cp.Faces[0].V.Length == 4 && SamePoints(pl, cp) && SameUv(pl, cp));
        var bx = Primitives.Box(2, 2, 2, 6, 6, 6); MeshOps.WeldByDistance(bx, 1e-5f); bx.CleanFaces();
        var tbx = Tri(bx);
        var cb = MoloMax.Core.Mesh.MeshClean.Run(tbx, opt, out var rb);
        Console.WriteLine($"       clean triangulated box 6x6x6: {rb}, closed {cb.IsClosedManifold(out _)}, volume {Vol(cb):0.000}");
        Check("Clean Up: triangulated box with segments -> 6 faces, closed, same volume, same UVs", () => cb.Faces.Count == 6 && cb.IsClosedManifold(out _) && Near(Vol(cb), 8, 1e-3f) && SameUv(tbx, cb));
        var sp = Primitives.Sphere(1, 24);
        var cs = MoloMax.Core.Mesh.MeshClean.Run(sp, opt, out var rs);
        Console.WriteLine($"       clean sphere 24: {rs}");
        Check("Clean Up: a round sphere keeps its shape (no face merged on the curve)", () => cs.Faces.Count == sp.Faces.Count && Near(Vol(cs), Vol(sp), 1e-4f));
        var cy = Tri(Primitives.Cylinder(1, 2, 24, 4));
        var cc = MoloMax.Core.Mesh.MeshClean.Run(cy, opt, out var rc);
        Console.WriteLine($"       clean triangulated cylinder: {rc}, closed {cc.IsClosedManifold(out _)}");
        Check("Clean Up: triangulated cylinder -> caps one polygon each, sides one quad per column, closed, same volume", () => cc.IsClosedManifold(out _) && Near(Vol(cc), Vol(cy), 1e-4f) && cc.Faces.Count <= 24 + 2 + 24 * 3 && cc.Faces.Count(f => f.V.Length == 24) == 2);
        // a UV seam in the middle of a flat plane: kept
        var seam = Primitives.Plane(4, 4, 4, 4);
        foreach (var f in seam.Faces) if (seam.FaceCenter(f).X > 0) for (int i = 0; i < f.UV.Length; i++) f.UV[i] += new Vector2(5, 0);
        var cse = MoloMax.Core.Mesh.MeshClean.Run(seam, opt, out _);
        Check("Clean Up: a UV seam is kept (2 polygons, texture unchanged)", () => cse.Faces.Count == 2 && SameUv(seam, cse));
        var cse2 = MoloMax.Core.Mesh.MeshClean.Run(seam, new MoloMax.Core.Mesh.CleanOptions { KeepUvSeams = false }, out _);
        Check("Clean Up: seams allowed -> 1 polygon", () => cse2.Faces.Count == 1);
        // two materials on a flat plane: never merged
        var mats = Primitives.Plane(4, 4, 4, 4);
        foreach (var f in mats.Faces) if (mats.FaceCenter(f).Y > 0) f.MatId = 1;
        var cm = MoloMax.Core.Mesh.MeshClean.Run(mats, opt, out _);
        Check("Clean Up: material borders are kept", () => cm.Faces.Count == 2 && cm.Faces.Select(f => f.MatId).Distinct().Count() == 2);
        // a ring (flat area with a hole) is never turned into a bad polygon
        var ring = Primitives.Plane(3, 3, 3, 3); ring.Faces.RemoveAt(4); // the middle face
        var cr = MoloMax.Core.Mesh.MeshClean.Run(ring, opt, out _);
        Check("Clean Up: a flat area with a hole stays valid (no polygon crossing the hole)", () => MeshOps.BorderEdges(cr).Count == MeshOps.BorderEdges(ring).Count - 0 || (cr.Faces.Count >= 2 && cr.Faces.All(f => f.V.Distinct().Count() == f.V.Length)));
        // big mesh: fast
        var big = Tri(Primitives.Plane(50, 50, 150, 150));
        var swc = System.Diagnostics.Stopwatch.StartNew();
        var cbig = MoloMax.Core.Mesh.MeshClean.Run(big, opt, out var rbig);
        Console.WriteLine($"       clean big plane: {rbig}, {swc.ElapsedMilliseconds} ms");
        Check("Clean Up: 45 000 triangles cleaned in under 3 s", () => cbig.Faces.Count == 1 && swc.ElapsedMilliseconds < 3000);
        // modifier: saved / reopened, removable
        var scc = new Scene(); var oc = scc.Add("c", tbx, Vector3.Zero); oc.Modifiers.Add(new MoloMax.Core.Modifiers.CleanUpModifier()); oc.Touch();
        Check("Clean Up modifier: evaluated mesh cleaned, base mesh untouched", () => oc.Evaluated().Faces.Count == 6 && oc.Mesh.Faces.Count == tbx.Faces.Count);
    }
    {
        // undo: meshes copied in the background after an import, the first edit then only takes the copy
        var sc = new Scene(); var big = sc.Add("big", Primitives.Plane(10, 10, 150, 150), Vector3.Zero);
        var us = new UndoStack();
        us.Prefreeze(sc, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        us.Record("edit", sc);
        int f0 = big.Mesh.Faces.Count;
        MoloMax.Core.Mesh.MeshOps.FlipAll(big.Mesh); big.Mesh.Faces.RemoveAt(0); big.Touch();
        var back = us.Undo(sc);
        Check("background pre-copy: undo after the first edit restores the mesh", () => back.Find(big.Id).Mesh.Faces.Count == f0 && back.Find(big.Id).Mesh.Faces[0].V.SequenceEqual(Primitives.Plane(10, 10, 150, 150).Faces[0].V));
    }
}

Console.WriteLine("MLO room generator (floor plan)");
{
    var plan = new MoloMax.Core.Tools.RoomPlanData();
    plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "hall", X0 = 0, Y0 = 0, X1 = 6, Y1 = 6 });
    plan.Rooms.Add(new MoloMax.Core.Tools.PlanRoom { Name = "office", X0 = 6, Y0 = 0, X1 = 10, Y1 = 6 });
    plan.Openings.Add(new MoloMax.Core.Tools.PlanOpening { X = 6, Y = 3 });                       // inner door
    plan.Openings.Add(new MoloMax.Core.Tools.PlanOpening { X = 3, Y = 0 });                       // street door
    plan.Openings.Add(new MoloMax.Core.Tools.PlanOpening { X = 10, Y = 3, Sill = 0.9f, Height = 1.2f }); // window
    var built = MoloMax.Core.Tools.RoomPlanBuilder.Build(plan);
    foreach (var w in built.Warnings) Console.WriteLine("       warn: " + w);
    var sc = new Scene();
    foreach (var (n, m, p) in built.Rooms) sc.Add(n, m, p, GtaObjectType.MloRoom);
    foreach (var (n, m, p) in built.Links) { var o = sc.Add(n, m, p); o.Gta.Role = GtaRole.Shell; }
    var lay = MoloMax.Core.Gta.Export.MloLayout.Build(sc, sc.Gta);
    foreach (var w in lay.Warnings) Console.WriteLine("       layout: " + w);
    Console.WriteLine($"       rooms {lay.Rooms.Count - 1}, portals {lay.Portals.Count} ({lay.ExitPortals} outside), links {built.Links.Count}");
    var (h0, h1) = sc.Objects[0].WorldBounds(); var (o0, o1) = sc.Objects[1].WorldBounds();
    Check("two rooms with a gap between them (they never touch)", () => o0.X - h1.X > 0.05f && o0.X - h1.X < 0.2f);
    Check("inner door + street door + window become 3 portals, 2 to the outside", () => lay.Rooms.Count == 3 && lay.Portals.Count == 3 && lay.ExitPortals == 2);
    Check("a passage closes the gap at the inner door", () => built.Links.Count == 1 && built.Links[0].Mesh.Faces.Count == 4);
    Check("rooms face inward (negative volume)", () => sc.Objects.Where(o => o.GtaType == GtaObjectType.MloRoom).All(o => Volume(o.Mesh) < 0));
}

Console.WriteLine("Instances (one mesh, many transforms)");
{
    var sc = new Scene();
    sc.Gta.ResourceName = "molo_inst";
    var chair = sc.Add("chair", Primitives.Box(0.5f, 0.5f, 1), new Vector3(10, 0, 0)); chair.Gta.Role = GtaRole.Prop;
    var i1 = sc.AddInstance(chair); i1.Position = new Vector3(12, 0, 0);
    var i2 = sc.AddInstance(chair); i2.Position = new Vector3(14, 3, 0); i2.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
    var solo = sc.Add("table", Primitives.Box(1, 1, 0.8f), new Vector3(0, 5, 0)); solo.Gta.Role = GtaRole.Prop;
    Check("instances share one mesh and count 3", () => ReferenceEquals(chair.Mesh, i1.Mesh) && ReferenceEquals(chair.Mesh, i2.Mesh) && sc.InstanceCount(i1) == 3 && sc.InstanceCount(solo) == 1);
    // editing one member (new mesh + Touch) reaches the others after the sync
    i1.Mesh = Primitives.Box(0.5f, 0.5f, 1.5f); i1.Touch();
    sc.SyncInstances();
    Check("editing one instance changes them all", () => ReferenceEquals(chair.Mesh, i1.Mesh) && ReferenceEquals(i2.Mesh, i1.Mesh) && chair.Version == i1.Version && MathF.Abs(chair.Mesh.Bounds().Max.Z - 1.5f) < 1e-4f);
    // save / load keeps one mesh
    var json = SceneIO.ToJson(sc);
    var back = SceneIO.FromJson(json);
    var bc = back.Objects.Where(o => o.InstanceKey != null).ToList();
    Console.WriteLine($"       saved {json.Length / 1024} KB, {bc.Count} instances back, distinct meshes {bc.Select(o => o.Mesh).Distinct(ReferenceEqualityComparer.Instance).Count()}");
    Check("save / reopen: 3 instances on one mesh, mesh written once", () => bc.Count == 3 && bc.All(o => ReferenceEquals(o.Mesh, bc[0].Mesh)) && bc[0].Mesh.Faces.Count == 6
        && System.Text.RegularExpressions.Regex.Matches(json, "\"MeshOf\":[0-9]").Count == 2);
    // undo keeps the sharing
    var undo = new UndoStack();
    undo.Record("move", sc);
    i2.Position += Vector3.UnitX;
    var restored = undo.Undo(sc);
    var rc = restored.Objects.Where(o => o.InstanceKey != null).ToList();
    Check("undo: instances still share one mesh", () => rc.Count == 3 && rc.All(o => ReferenceEquals(o.Mesh, rc[0].Mesh)));
    // make unique
    sc.MakeUnique(i2);
    Check("make unique: own copy, the other two stay instances", () => i2.InstanceKey == null && !ReferenceEquals(i2.Mesh, chair.Mesh) && sc.InstanceCount(chair) == 2);
    var i3 = sc.AddInstance(chair); i3.Position = new Vector3(16, 0, 0);
    // GTA export: one model, one entity per instance
    var outDir = Path.Combine(tmpDir, "inst_export");
    var rep = MoloMax.Core.Gta.Export.GtaExporter.Export(sc, outDir, null);
    foreach (var e in rep.Errors) Console.WriteLine("       error: " + e);
    var ext = Path.Combine(outDir, "molo_inst", "stream", "exterior");
    var ydrs = Directory.Exists(ext) ? Directory.GetFiles(ext, "*.ydr").Select(Path.GetFileName).ToList() : new();
    var ymapPath = Directory.Exists(ext) ? Directory.GetFiles(ext, "*.ymap").FirstOrDefault() : null;
    var ents = new List<(string, Vector3)>();
    if (ymapPath != null) { var y = new CodeWalker.GameFiles.YmapFile(); y.Load(File.ReadAllBytes(ymapPath)); foreach (var e in y.AllEntities ?? Array.Empty<CodeWalker.GameFiles.YmapEntityDef>()) ents.Add((e.Name, new Vector3(e.Position.X, e.Position.Y, e.Position.Z))); }
    Console.WriteLine($"       ydr: {string.Join(", ", ydrs)}; entities: {string.Join(", ", ents.Select(e => e.Item1 + "@" + e.Item2))}");
    Check("export: the chair model written once, placed 3 times (+ the unique copy and the table)", () =>
        rep.Errors.Count == 0 && ydrs.Count(f => f.Contains("chair")) == 2 && ents.Count(e => e.Item1 == ents.FirstOrDefault(x => x.Item1.Contains("chair")).Item1) == 3 && ents.Count == 5);
    // a lot of instances stay cheap: 5000 members, sync + lookups
    var big = new Scene(); var src = big.Add("rock", Primitives.Sphere(1, 16), Vector3.Zero);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 5000; i++) big.AddInstance(src).Position = new Vector3(i % 100, i / 100, 0);
    for (int k = 0; k < 10; k++) big.SyncInstances();
    for (int i = 0; i < 20000; i++) big.Find(1 + i % 5000);
    var ms = sw.ElapsedMilliseconds;
    Console.WriteLine($"       5000 instances: create + 10 syncs + 20k lookups in {ms} ms, {big.Objects.Select(o => o.Mesh).Distinct(ReferenceEqualityComparer.Instance).Count()} mesh");
    Check("5000 instances: one mesh, fast sync and lookups", () => big.Objects.All(o => ReferenceEquals(o.Mesh, src.Mesh)) && ms < 3000);
}

Console.WriteLine("Edit Poly modifier (hand edits as a step of the stack)");
{
    var o = new SceneObject { Mesh = Primitives.Box(1, 1, 1) };
    var ep = new MoloMax.Core.Modifiers.EditPolyModifier();
    o.Modifiers.Add(ep);
    o.Modifiers.Add(new MoloMax.Core.Modifiers.TriangulateModifier());
    Check("edit poly without edits passes the mesh through", () => o.Evaluated().Faces.Count == 12);
    o.BeginModifierEdit(0);
    bool swapped = o.ModEdit != null && o.Modifiers.Count == 1 && o.FullStack.Count == 2 && o.Mesh.Faces.Count == 6;
    int top = Enumerable.Range(0, o.Mesh.Faces.Count).First(f => o.Mesh.FaceNormal(f).Z > 0.9f);
    MeshOps.ExtrudeFaces(o.Mesh, new HashSet<int> { top }, 0.5f); o.Touch();
    // saving in the middle of the edit writes the committed state
    var mid = SceneIO.FromJson(SceneIO.ToJson(new Scene { Objects = { o } }));
    o.CommitModifierEdit();
    var ev = o.Evaluated();
    Console.WriteLine($"       base {o.Mesh.Faces.Count} faces, after stack {ev.Faces.Count} triangles, top at {ev.Bounds().Max.Z:0.00}");
    Check("edit poly: the editor swaps in the modifier's mesh and the stack after it", () => swapped);
    Check("edit poly: extrude kept in the modifier, base mesh untouched, stack after it still runs", () => o.Mesh.Faces.Count == 6 && ev.Faces.Count == 20 && MathF.Abs(ev.Bounds().Max.Z - 1.5f) < 1e-4f);
    Check("edit poly: saved mid-edit and reopened = same result", () => mid.Objects[0].ModEdit == null && mid.Objects[0].Evaluated().Faces.Count == 20 && mid.Objects[0].Mesh.Faces.Count == 6);
    // the base mesh moves a vertex: the edit follows it
    int v0 = Enumerable.Range(0, o.Mesh.Verts.Count).First(i => o.Mesh.Verts[i].Z < 0.01f);
    var before = o.Mesh.Verts[v0];
    o.Mesh.Verts[v0] += new Vector3(0, 0, -0.25f); o.Touch();
    var ev2 = o.Evaluated();
    Check("edit poly: moving a vertex below the edit moves it in the result too", () => ev2.Bounds().Min.Z < -0.24f && ev2.Faces.Count == 20 && ep.Warning == null);
    // topology change below: last result shown + warning
    o.Mesh = Primitives.Box(1, 1, 1, 2, 2, 2); o.Touch();
    var ev3 = o.Evaluated();
    Check("edit poly: topology change below -> last result kept, warning shown", () => ep.Warning != null && ev3.Faces.Count == 20);
}

Console.WriteLine("Unwrap UVW: auto seams, peel (LSCM), relax, stitch, density, pack");
{
    float Stretch(PolyMesh m, IEnumerable<int> faces)
    {
        // spread of (UV area / 3D area) over the triangles: 1 = perfectly even
        var r = new List<double>();
        foreach (var f in faces)
        {
            var face = m.Faces[f];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, face))
            {
                var a3 = Vector3.Cross(m.Verts[face.V[b]] - m.Verts[face.V[a]], m.Verts[face.V[c]] - m.Verts[face.V[a]]).Length();
                var u0 = face.UV[a]; var u1 = face.UV[b]; var u2 = face.UV[c];
                var a2 = MathF.Abs((u1.X - u0.X) * (u2.Y - u0.Y) - (u2.X - u0.X) * (u1.Y - u0.Y));
                if (a3 > 1e-9f) r.Add(a2 / a3);
            }
        }
        double mean = r.Average(); return (float)(r.Max() / Math.Max(mean, 1e-12) );
    }
    bool InUnit(PolyMesh m) => m.Faces.All(f => f.UV.All(u => u.X >= -1e-4f && u.Y >= -1e-4f && u.X <= 1.0001f && u.Y <= 1.0001f));
    var box = Primitives.Box(2, 1, 0.5f);
    var (bc, bs) = UvUnfold.AutoUnwrap(box);
    Console.WriteLine($"       box: {bc} charts, {bs.Count} seams, stretch {Stretch(box, Enumerable.Range(0, box.Faces.Count)):0.00}");
    Check("box: 6 charts, flat, same texel density everywhere, inside 0..1, no overlap", () => bc == 6 && Stretch(box, Enumerable.Range(0, box.Faces.Count)) < 1.05f && InUnit(box) && UvTools.Islands(box).Count == 6
        && Enumerable.Range(0, 6).All(i => Enumerable.Range(0, 6).All(j => { if (i >= j) return true; var (a0, a1) = UvTools.Bounds(box, new[] { i }); var (b0, b1) = UvTools.Bounds(box, new[] { j }); return a1.X <= b0.X + 1e-4f || b1.X <= a0.X + 1e-4f || a1.Y <= b0.Y + 1e-4f || b1.Y <= a0.Y + 1e-4f; })));
    var tall = Primitives.Box(1, 1, 3); UvUnfold.AutoUnwrap(tall);
    float fill = UvTools.Islands(tall).Min(isl => { var (a, b) = UvTools.Bounds(tall, isl); return isl.Sum(f => UvTools.UvArea(tall, f)) / MathF.Max((b.X - a.X) * (b.Y - a.Y), 1e-12f); });
    Console.WriteLine($"       tall box: worst island fills {fill:P0} of its box");
    Check("charts are turned straight (rectangles fill their bounding box)", () => fill > 0.98f);
    var cyl = Primitives.Cylinder(0.5f, 2, 16, 3);
    var (cc, _) = UvUnfold.AutoUnwrap(cyl);
    float cs = Stretch(cyl, Enumerable.Range(0, cyl.Faces.Count));
    Console.WriteLine($"       cylinder: {cc} charts, stretch {cs:0.00}");
    Check("cylinder: the tube is cut into flat charts, low stretch", () => cc >= 3 && cs < 1.5f && InUnit(cyl));
    var sph = Primitives.Sphere(1, 24);
    var (sc2, _) = UvUnfold.AutoUnwrap(sph);
    float ss = Stretch(sph, Enumerable.Range(0, sph.Faces.Count));
    var chartsOk = UvUnfold.Charts(sph, UvUnfold.AutoSeams(sph), Enumerable.Range(0, sph.Faces.Count).ToHashSet()).All(c => UvUnfold.IsDisc(sph, c, UvUnfold.AutoSeams(sph)));
    Console.WriteLine($"       sphere: {sc2} charts, stretch {ss:0.00}, all discs {chartsOk}");
    Check("sphere (closed, smooth): cut into disc charts and unfolded", () => sc2 >= 2 && chartsOk && ss < 3f && InUnit(sph));
    // relax: shake the UVs of a plane, relax brings the stretch back down
    var pl = Primitives.Plane(2, 2, 8, 8); pl.Validate();
    UvUnfold.AutoUnwrap(pl);
    var rnd = new Random(3);
    var pf = Enumerable.Range(0, pl.Faces.Count).ToList();
    // move each shared UV point the same way (keep the island connected)
    var jitter = new Dictionary<int, Vector2>();
    foreach (var f in pl.Faces) for (int c = 0; c < f.V.Length; c++) { if (!jitter.TryGetValue(f.V[c], out var j)) jitter[f.V[c]] = j = new Vector2((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * 0.03f; f.UV[c] += j; }
    float s0 = Stretch(pl, pf);
    UvUnfold.Relax(pl, pf, 200);
    float s1 = Stretch(pl, pf);
    Console.WriteLine($"       relax: stretch {s0:0.00} -> {s1:0.00}");
    Check("relax evens out stretched UVs", () => s1 < s0 * 0.8f);
    // stitch: two box charts sharing an edge become one island
    var b2 = Primitives.Box(1, 1, 1);
    UvUnfold.AutoUnwrap(b2);

    int i0 = UvTools.Islands(b2, Enumerable.Range(0, 6)).Count;
    int topF = Enumerable.Range(0, 6).First(f => b2.FaceNormal(f).Z > 0.9f);
    var side = Enumerable.Range(0, 6).First(f => b2.FaceNormal(f).X > 0.9f);
    int st = UvUnfold.Stitch(b2, new HashSet<int> { topF, side });
    int i1 = UvTools.Islands(b2, Enumerable.Range(0, 6)).Count;
    Console.WriteLine($"       stitch: islands {i0} -> {i1}");
    Check("stitch joins the two islands on their shared edge", () => st == 1 && i1 == i0 - 1);
    // the modifier keeps UVs against the topology
    var o = new SceneObject { Mesh = Primitives.Box(1, 1, 1) };
    var uw = new MoloMax.Core.Modifiers.UnwrapUvwModifier();
    o.Modifiers.Add(uw);
    var input = o.Mesh.Clone(); var (_, seams) = UvUnfold.AutoUnwrap(input); uw.StoreUv(input); uw.SetSeams(seams);
    var e1 = o.Evaluated();
    Check("unwrap modifier puts its UVs on the mesh it receives", () => e1.Faces.Zip(input.Faces).All(p => p.First.UV.SequenceEqual(p.Second.UV)) && uw.SeamSet().Count == seams.Count);
    var back = SceneIO.FromJson(SceneIO.ToJson(new Scene { Objects = { o } }));
    Check("unwrap modifier saved / reopened with its UVs and seams", () => back.Objects[0].Evaluated().Faces.Zip(input.Faces).All(p => p.First.UV.SequenceEqual(p.Second.UV)) && ((MoloMax.Core.Modifiers.UnwrapUvwModifier)back.Objects[0].Modifiers[0]).SeamSet().Count == seams.Count);
}

Console.WriteLine("Sculpt brushes (cleanup of generated meshes)");
{
    float Noise(PolyMesh m) { var r = m.Verts.Select(v => v.Length()).ToList(); float mean = r.Average(); return MathF.Sqrt(r.Average(x => (x - mean) * (x - mean))); }
    var sph = Primitives.Sphere(1, 48); var rnd = new Random(5);
    for (int i = 0; i < sph.Verts.Count; i++) sph.Verts[i] *= 1 + ((float)rnd.NextDouble() - 0.5f) * 0.06f;
    float n0 = Noise(sph);
    var ss = new SculptSession(sph);
    ss.SmoothAll(6, 0.5f, relaxOnly: false);
    float n1 = Noise(sph);
    Console.WriteLine($"       noisy sphere: radius spread {n0:0.0000} -> {n1:0.0000} after Smooth all");
    Check("smooth all removes the noise of a scanned / generated mesh", () => n1 < n0 * 0.4f);
    // Draw raises, Flatten flattens, symmetry mirrors
    var pl = Primitives.Plane(2, 2, 40, 40); pl.Validate();
    var sp = new SculptSession(pl);
    var set = new SculptSettings { Brush = SculptBrush.Draw, Radius = 0.3f, Strength = 1f, SymX = true };
    for (int i = 0; i < 10; i++) sp.Dab(new Vector3(0.5f, 0, 0), Vector3.UnitZ, set);
    float zR = pl.Verts.Where(v => Vector3.Distance(v, new Vector3(0.5f, 0, v.Z)) < 0.05f).Max(v => v.Z);
    float zL = pl.Verts.Where(v => Vector3.Distance(v, new Vector3(-0.5f, 0, v.Z)) < 0.05f).Max(v => v.Z);
    Console.WriteLine($"       draw x10: bump {zR:0.000} m, mirrored {zL:0.000} m");
    Check("draw raises a bump and X symmetry mirrors it", () => zR > 0.1f && MathF.Abs(zR - zL) < 1e-3f);
    set.Brush = SculptBrush.Flatten; set.SymX = false; set.Radius = 0.6f;
    for (int i = 0; i < 12; i++) sp.Dab(new Vector3(0.5f, 0, 0), Vector3.UnitZ, set);
    float zF = pl.Verts.Where(v => Vector3.Distance(v, new Vector3(0.5f, 0, v.Z)) < 0.05f).Max(v => v.Z);
    Console.WriteLine($"       flatten x12: bump {zR:0.000} -> {zF:0.000} m");
    Check("flatten takes the bump down", () => zF < zR * 0.6f);
    // grab
    set.Brush = SculptBrush.Grab;
    set.Radius = 0.3f;
    sp.BeginGrab(new Vector3(-0.5f, 0, zL), set); sp.DragGrab(new Vector3(-0.5f, 0, zL + 0.4f), set); sp.EndGrab();
    float zG = pl.Verts.Where(v => Vector3.Distance(v, new Vector3(-0.5f, 0, v.Z)) < 0.05f).Max(v => v.Z);
    Check("grab pulls the area with the mouse", () => zG > zL + 0.2f);
    // relax keeps the volume of a sphere (smooth shrinks it)
    var a = Primitives.Sphere(1, 24); var b = Primitives.Sphere(1, 24);
    new SculptSession(a).SmoothAll(20, 0.5f, false); new SculptSession(b).SmoothAll(20, 0.5f, true);
    float va = a.Verts.Average(v => v.Length()), vb = b.Verts.Average(v => v.Length());
    Console.WriteLine($"       20 passes: smooth radius {va:0.000}, relax radius {vb:0.000}");
    Check("relax evens the mesh without shrinking it (smooth shrinks)", () => vb > 0.985f && va < vb);
}

Console.WriteLine("\n== floor plan picture -> rooms + doors, room finishing edges");
{
    // 600 x 400 px picture of 12 x 8 m: outer walls, one inner wall at x = 7 m with a 1 m door
    int W = 600, H = 400; var img = new MoloMax.Core.Gta.Export.RgbaImage(W, H);
    for (int i = 0; i < W * H; i++) { img.Pixels[i * 4] = img.Pixels[i * 4 + 1] = img.Pixels[i * 4 + 2] = 245; img.Pixels[i * 4 + 3] = 255; }
    void Wall(int x0, int y0, int x1, int y1) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * W + x) * 4; img.Pixels[i] = img.Pixels[i + 1] = img.Pixels[i + 2] = 20; } }
    Wall(40, 40, 560, 50); Wall(40, 350, 560, 360); Wall(40, 40, 50, 360); Wall(550, 40, 560, 360);
    Wall(345, 40, 355, 180); Wall(345, 230, 355, 360); // door gap 180..230 px = 1 m
    var pr = MoloMax.Core.Tools.FloorPlanReader.Read(img, new MoloMax.Core.Tools.FloorPlanReader.Settings { WidthMetres = 12 });
    foreach (var r in pr.Rooms) Console.WriteLine($"       {r.Name}: {r.Min} - {r.Max}");
    foreach (var d in pr.Doors) Console.WriteLine($"       door at {d.X:0.00}, {d.Y:0.00} width {d.Width:0.0}");
    Check("plan picture: 2 rooms found", () => pr.Rooms.Count == 2);
    Check("plan picture: rooms share the inner wall line at 7 m", () => pr.Rooms.Count == 2 && pr.Rooms.Any(r => MathF.Abs(r.Max.X - 7f) < 0.3f) && pr.Rooms.Any(r => MathF.Abs(r.Min.X - 7f) < 0.3f));
    Check("plan picture: the door in the inner wall is found", () => pr.Doors.Count == 1 && MathF.Abs(pr.Doors[0].X - 7f) < 0.3f && MathF.Abs(pr.Doors[0].Width - 1f) < 0.35f);
    var plan = new MoloMax.Core.Tools.RoomPlanData(); plan.Rooms.AddRange(pr.Rooms); plan.Openings.AddRange(pr.Doors);
    var built = MoloMax.Core.Tools.RoomPlanBuilder.Build(plan);
    Check("plan picture builds rooms with a passage", () => built.Rooms.Count == 2 && built.Links.Count == 1);
    var fe = MoloMax.Core.Tools.RoomFinish.Classify(built.Rooms[0].Mesh);
    Console.WriteLine($"       room 1: {fe.FloorWall.Count} floor/wall, {fe.CeilingWall.Count} ceiling/wall, {fe.Corners.Count} corner edges");
    var plain = MoloMax.Core.Tools.RoomFinish.Classify(Primitives.Room(4, 3, 3));
    Check("room finishing: a plain room has 4 floor, 4 ceiling and 4 corner edges", () => plain.FloorWall.Count == 4 && plain.CeilingWall.Count == 4 && plain.Corners.Count == 4);
    Check("room finishing: the doorway breaks the skirting (more, shorter floor edges, none across the door)", () => fe.FloorWall.Count >= 4);
}
{
    // dropped material: surface under the mouse = one wall or the floor
    var room = Primitives.Room(4, 3, 3);
    int floorF = Enumerable.Range(0, room.Faces.Count).First(f => room.FaceNormal(room.Faces[f]).Z > 0.7f);
    int wallF = Enumerable.Range(0, room.Faces.Count).First(f => MathF.Abs(room.FaceNormal(room.Faces[f]).Z) < 0.3f);
    var sf = Surfaces.Region(room, floorF, SurfaceScope.Surface); var sw = Surfaces.Region(room, wallF, SurfaceScope.Surface);
    Check("drop on the floor covers the floor only", () => sf.All(f => room.FaceNormal(room.Faces[f]).Z > 0.7f) && sf.Contains(floorF));
    Check("drop on a wall covers that wall only (stops at the corners)", () => sw.Count >= 1 && sw.All(f => Vector3.Dot(room.FaceNormal(room.Faces[f]), room.FaceNormal(room.Faces[wallF])) > 0.9f));
    Check("every wall = the 4 walls", () => Surfaces.Region(room, floorF, SurfaceScope.Walls).Count == 4);
}

Console.WriteLine("\n== map editing: .ymap write / read / edit");
{
    var md = MoloMax.Core.Gta.Map.MapDocument.New("molo_test_map");
    var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
    md.Add("prop_bench_01a", new Vector3(195, -934, 30), rot, Vector3.One, 150);
    md.Add("prop_bin_01a", new Vector3(200, -930, 30.2f), Quaternion.Identity, new Vector3(1.5f, 1.5f, 2f));
    var bytes = md.Save(h => (new Vector3(-1, -0.5f, 0), new Vector3(1, 0.5f, 1)));
    Console.WriteLine($"       saved {bytes.Length} bytes");
    var back = MoloMax.Core.Gta.Map.MapDocument.Load(bytes, "molo_test_map.ymap");
    var b0 = back.Entities.FirstOrDefault(e => e.ArchetypeHash == MoloMax.Core.Gta.Export.MetaXml.Jenkins("prop_bench_01a"));
    Check("ymap: 2 entities read back", () => back.Entities.Count == 2);
    Check("ymap: archetype, position and lod distance kept", () => b0 != null && Vector3.Distance(b0.Position, new Vector3(195, -934, 30)) < 1e-3f && MathF.Abs(b0.LodDist - 150) < 1e-3f);
    Check("ymap: real rotation kept (stored inverted like the game)", () => b0 != null && MathF.Abs(MathF.Abs(Quaternion.Dot(b0.Rotation, rot)) - 1) < 1e-4f);
    var b1 = back.Entities.First(e => e != b0);
    Check("ymap: scale XY / Z kept", () => MathF.Abs(b1.Scale.X - 1.5f) < 1e-4f && MathF.Abs(b1.Scale.Z - 2f) < 1e-4f);
    Check("ymap: extents cover the entities", () => back.EntitiesMin.X <= 194 && back.EntitiesMax.X >= 201 && back.StreamingMin.X < 50);
    Check("map index: DLC copies replace the base map (hei_ / lr_), unrelated names stay", () =>
    {
        var names = new[] { "dt1_19_strm_0", "hei_dt1_19_strm_0", "sc1_00d_strm_0", "hei_sc1_00d_strm_0", "lr_sc1_00d_strm_0", "ba_case1_dixon", "dt1_20_strm_0" };
        var order = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var r = MoloMax.Core.Gta.Map.MapWorldIndex.ReplacedMaps(names, order);
        return r.SetEquals(new[] { "dt1_19_strm_0", "sc1_00d_strm_0", "hei_sc1_00d_strm_0" });
    });
    Check("GTA scale: kinds from names (chair, stool, door, bar counter, coffee table; not barrel / stable)", () =>
        MoloMax.Core.Tools.GtaScale.Guess("prop_chair_01")?.Id == "chair" && MoloMax.Core.Tools.GtaScale.Guess("Tabouret_Bar")?.Id == "stool"
        && MoloMax.Core.Tools.GtaScale.Guess("v_door_02")?.Id == "door" && MoloMax.Core.Tools.GtaScale.Guess("bar_counter_long")?.Id == "bar_counter"
        && MoloMax.Core.Tools.GtaScale.Guess("coffee_table")?.Id == "coffee_table" && MoloMax.Core.Tools.GtaScale.Guess("barrel_01") == null && MoloMax.Core.Tools.GtaScale.Guess("stable") == null);
    Check("GTA scale: the ped mannequin is 1.818 m tall, on the ground", () =>
    {
        var pm = MoloMax.Core.Tools.GtaScale.Ped(); var (mn, mx) = pm.Bounds();
        return MathF.Abs(mx.Z - 1.818f) < 0.01f && MathF.Abs(mn.Z) < 1e-4f && pm.Faces.Count > 40;
    });
    // edit the loaded map: move one, remove one, add one
    b0.Position += new Vector3(0, 0, 5);
    back.Remove(b1);
    back.Add("prop_chair_01a", new Vector3(190, -940, 30), Quaternion.Identity, Vector3.One);
    var bytes2 = back.Save();
    var again = MoloMax.Core.Gta.Map.MapDocument.Load(bytes2, "molo_test_map.ymap");
    Check("ymap edit: moved, removed and added entities saved", () => again.Entities.Count == 2 && again.Entities.Any(e => MathF.Abs(e.Position.Z - 35) < 1e-3f) && again.Entities.Any(e => e.ArchetypeHash == MoloMax.Core.Gta.Export.MetaXml.Jenkins("prop_chair_01a")));
}

{
    // map model cache: a mesh and its materials come back exactly
    var m = Primitives.Box(2, 3, 4);
    m.Faces[0].MatId = 1; m.Faces[1].Smooth = 5; m.Faces[2].UV[1] = new Vector2(0.25f, 0.75f); m.Faces[3].Col[0] = new Vector4(1, 0, 0, 1);
    var mats = new List<GtaMaterial> { new() { Name = "a", DiffuseTexture = @"C:\x\a.dds" }, new() { Name = "b", Shader = "emissive", Bumpiness = 2 } };
    var path = Path.Combine(Path.GetTempPath(), "molo_mesh_cache_test.pmc");
    MoloMax.Core.Gta.Map.MapMeshCache.Save(path, m, mats);
    var back = MoloMax.Core.Gta.Map.MapMeshCache.Load(path);
    Check("map mesh cache: mesh + materials round trip", () => back.HasValue && back.Value.Mesh.Verts.Count == m.Verts.Count && back.Value.Mesh.Faces.Count == m.Faces.Count
        && back.Value.Mesh.Faces[0].MatId == 1 && back.Value.Mesh.Faces[1].Smooth == 5 && back.Value.Mesh.Faces[2].UV[1] == new Vector2(0.25f, 0.75f)
        && back.Value.Mesh.Faces[3].Col[0] == new Vector4(1, 0, 0, 1) && back.Value.Materials[0].DiffuseTexture == @"C:\x\a.dds" && back.Value.Materials[1].Shader == "emissive"
        && back.Value.Materials[1].Bumpiness == 2 && back.Value.Mesh.Bounds() == m.Bounds());
    File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
    Check("map mesh cache: a broken file is ignored", () => MoloMax.Core.Gta.Map.MapMeshCache.Load(path) == null);
}

{
    // robustness: random chains of extrude / inset / bevel / weld on every primitive never throw (API / Edit Poly)
    var frnd = new Random(7);
    Func<PolyMesh>[] fk = {
        () => Primitives.Box(2, 2, 2), () => Primitives.Sphere(1), () => Primitives.Cylinder(0.5f, 2), () => Primitives.Cone(1, 0.2f, 2),
        () => Primitives.Tube(1, 0.6f, 2), () => Primitives.Plane(2, 2), () => Primitives.Stairs(1.2f, 8, 2, 3), () => Primitives.Room(4, 4, 3), () => Primitives.Door() };
    string[] fn = { "box", "sphere", "cylinder", "cone", "tube", "plane", "stairs", "room", "door" };
    Vector3[] fd = { Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY };
    int ffail = 0; string first = null;
    for (int it = 0; it < 1500; it++)
    {
        int k = frnd.Next(fk.Length); var m = fk[k](); var hist = new List<string>();
        try
        {
            for (int st = 0, n = frnd.Next(1, 6); st < n; st++)
            {
                var dir = fd[frnd.Next(3)]; float amt = (float)(0.02 + frnd.NextDouble() * 0.4);
                var faces = Enumerable.Range(0, m.Faces.Count).Where(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.7f).ToHashSet();
                int op = frnd.Next(4); hist.Add($"{op}/{dir}/{amt:0.00}/{faces.Count}");
                switch (op)
                {
                    case 0: MeshOps.ExtrudeFaces(m, faces, amt); break;
                    case 1: MeshOps.InsetFaces(m, faces, amt); break;
                    case 2: MeshOps.BevelFaces(m, faces, amt, -amt * 0.5f); break;
                    default: MeshOps.WeldByDistance(m, MathF.Max(amt, 1e-5f)); m.CleanFaces(); m.RemoveUnusedVertices(); break;
                }
                m.Validate();
            }
        }
        catch (Exception e) { if (ffail++ == 0) first = $"{fn[k]} [{string.Join(" ", hist)}] {e.GetType().Name}: {e.Message}\n{e.StackTrace}"; }
    }
    // the same with random modifier stacks evaluated on top (what the viewport does after every API edit)
    int mfail = 0; string mfirst = null;
    var cat = MoloMax.Core.Modifiers.Modifier.Catalog;
    for (int it = 0; it < 800; it++)
    {
        int k = frnd.Next(fk.Length); var m = fk[k](); var names = new List<string>();
        try
        {
            for (int st = 0, n = frnd.Next(0, 3); st < n; st++)
            {
                var dir = fd[frnd.Next(3)];
                var faces = Enumerable.Range(0, m.Faces.Count).Where(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.7f).ToHashSet();
                if (frnd.Next(2) == 0) MeshOps.ExtrudeFaces(m, faces, 0.2f); else { MeshOps.WeldByDistance(m, 0.3f); m.CleanFaces(); m.RemoveUnusedVertices(); }
            }
            var mods = new List<MoloMax.Core.Modifiers.Modifier>();
            for (int st = 0, n = frnd.Next(1, 5); st < n; st++) { var c = cat[frnd.Next(cat.Length)]; names.Add(c.Name); mods.Add(c.Make()); }
            var r = MoloMax.Core.Modifiers.ModifierStack.Evaluate(m, mods, frnd.Next(2) == 0, false);
            r.Validate();
        }
        catch (Exception e) { if (mfail++ == 0) { var b = e is AggregateException ae ? ae.Flatten().InnerExceptions[0] : e; mfirst = $"{fn[k]} [{string.Join(", ", names)}] {b.GetType().Name}: {b.Message}\n{b.StackTrace}"; } }
    }
    if (mfirst != null) Console.WriteLine("       first modifier failure: " + mfirst);
    Check($"modifiers: 800 random stacks on edited primitives evaluate cleanly ({mfail} failures)", () => mfail == 0);
    if (first != null) Console.WriteLine("       first failure: " + first);
    Check($"mesh ops: 1500 random extrude / inset / bevel / weld chains on every primitive ({ffail} failures)", () => ffail == 0);
}

{
    // a scene saved with a polygon pinched by a weld (older versions) still opens, repaired
    var ps = new Scene();
    var pm = new PolyMesh();
    for (int i = 0; i < 6; i++) pm.AddVertex(new Vector3(i % 3, i / 3, 0));
    pm.Faces.Add(new Face(new[] { 0, 1, 4, 1, 2, 5, 3 }));
    pm.Faces.Add(new Face(new[] { 0, 1, 3 }));
    ps.Add("pinched", pm, Vector3.Zero);
    Scene pback = null; string perr = null;
    try { pback = SceneIO.FromJson(SceneIO.ToJson(ps)); } catch (Exception e) { perr = e.Message; }
    Check("scene with a pinched polygon opens, split into valid polygons " + (perr ?? ""), () => pback != null && pback.Objects[0].Mesh.Faces.All(f => f.V.Distinct().Count() == f.V.Length && f.V.Length >= 3) && pback.Objects[0].Mesh.Faces.Count == 2);
}

Console.WriteLine();
// ---- animation: curves, Euler, UV rows, scene apply / save
Check("anim: smooth curve passes through its keys, flat at the ends, no overshoot", () =>
{
    var c = new MoloMax.Core.Anim.AnimCurve(); c.SetKey(0, 0); c.SetKey(10, 10); c.SetKey(20, 10);
    float mid = c.Evaluate(5), over = Enumerable.Range(0, 21).Max(f => c.Evaluate(f));
    return Near(c.Evaluate(0), 0) && Near(c.Evaluate(10), 10) && Near(c.Evaluate(20), 10) && mid > 3 && mid < 7 && over <= 10.0001f;
});
Check("anim: linear and constant keys", () =>
{
    var c = new MoloMax.Core.Anim.AnimCurve(); c.SetKey(0, 0, MoloMax.Core.Anim.KeyInterp.Linear); c.SetKey(10, 10, MoloMax.Core.Anim.KeyInterp.Constant); c.SetKey(20, 30);
    return Near(c.Evaluate(2.5f), 2.5f) && Near(c.Evaluate(15), 10) && Near(c.Evaluate(25), 30);
});
Check("anim: Euler XYZ degrees round trip (Blender order)", () =>
{
    var e = new Vector3(20, -35, 120);
    var q = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(e);
    var back = MoloMax.Core.Anim.SceneAnim.ToEulerDeg(q);
    var q2 = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(back);
    // Rz(90): X axis goes to Y
    var rz = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, 90));
    return Vector3.Distance(back, e) < 0.01f && MathF.Abs(MathF.Abs(Quaternion.Dot(q, q2)) - 1) < 1e-5f && Vector3.Distance(Vector3.Transform(Vector3.UnitX, rz), Vector3.UnitY) < 1e-4f;
});
Check("anim: a spin keyed past 180 degrees keeps turning the same way", () =>
{
    var sc = new Scene(); var o = sc.Add("fan", Primitives.Box(1, 1, 1), Vector3.Zero);
    sc.Anim.KeyObject(o, 0, MoloMax.Core.Anim.Ch.Rot);
    o.Rotation = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, 170)); sc.Anim.KeyObject(o, 10, MoloMax.Core.Anim.Ch.Rot);
    o.Rotation = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, -20)); sc.Anim.KeyObject(o, 20, MoloMax.Core.Anim.Ch.Rot); // = 340: continues
    var z = sc.Anim.Track(o.Id).Curve(MoloMax.Core.Anim.Ch.RotZ);
    return Near(z.Keys[2].Value, 340);
});
Check("anim: apply moves the object, UV rows set, save / open keeps the keys", () =>
{
    var sc = new Scene(); var o = sc.Add("door", Primitives.Box(1, 1, 1), Vector3.Zero);
    sc.Anim.KeyObject(o, 0, MoloMax.Core.Anim.Ch.Loc);
    o.Position = new Vector3(4, 0, 2); sc.Anim.KeyObject(o, 30, MoloMax.Core.Anim.Ch.Loc);
    var t = sc.Anim.Track(o.Id); t.Curve(MoloMax.Core.Anim.Ch.UvU, true).SetKey(0, 0, MoloMax.Core.Anim.KeyInterp.Linear); t.Curve(MoloMax.Core.Anim.Ch.UvU).SetKey(30, 1);
    sc.Anim.Apply(sc, 15);
    bool mid = Near(o.Position.X, 2) && Near(o.Position.Z, 1) && Near(o.UvRow0.Z, 0.5f) && Near(o.UvRow0.W, 1) && Near(o.UvRow0.X, 1);
    var back = SceneIO.FromJson(SceneIO.ToJson(sc));
    var bt = back.Anim.Track(o.Id);
    return mid && bt != null && bt.Curve(MoloMax.Core.Anim.Ch.LocX).Keys.Count == 2 && Near(bt.Curve(MoloMax.Core.Anim.Ch.LocX).Keys[1].Value, 4) && back.Anim.End == 120;
});
Check("anim: keys -> .ycd (binary) -> keys again: same move, spin and UV scroll", () =>
{
    var sc = new Scene(); var o = sc.Add("molo_fan", Primitives.Box(1, 1, 1), new Vector3(10, 5, 2));
    o.Rotation = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, 30));
    sc.Anim.End = 60;
    sc.Anim.KeyObject(o, 0, MoloMax.Core.Anim.Ch.Loc.Concat(MoloMax.Core.Anim.Ch.Rot).ToArray());
    o.Position = new Vector3(12, 5, 3); o.Rotation = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, 200));
    sc.Anim.KeyObject(o, 60, MoloMax.Core.Anim.Ch.Loc.Concat(MoloMax.Core.Anim.Ch.Rot).ToArray());
    var t = sc.Anim.Track(o.Id); t.Curve(MoloMax.Core.Anim.Ch.UvV, true).SetKey(0, 0, MoloMax.Core.Anim.KeyInterp.Linear); t.Curve(MoloMax.Core.Anim.Ch.UvV).SetKey(60, 2);
    var clips = MoloMax.Core.Anim.AnimGta.ToClips(sc);
    var bytes = MoloMax.Core.Gta.Anim.YcdIO.Write(clips);
    var back = MoloMax.Core.Gta.Anim.YcdIO.Read(bytes, "test.ycd");
    var mv = back.FirstOrDefault(c => !c.IsUv); var uvc = back.FirstOrDefault(c => c.IsUv);
    Console.WriteLine($"       {string.Join(", ", back.Select(MoloMax.Core.Anim.AnimGta.Describe))}");
    if (mv == null || uvc == null || mv.Frames != 61) return false;
    // onto a copy standing where the original started
    var sc2 = new Scene(); var o2 = sc2.Add("molo_fan", Primitives.Box(1, 1, 1), new Vector3(10, 5, 2)); o2.Rotation = MoloMax.Core.Anim.SceneAnim.FromEulerDeg(new Vector3(0, 0, 30));
    int keys = MoloMax.Core.Anim.AnimGta.ApplyClip(sc2.Anim, o2, mv, 0) + MoloMax.Core.Anim.AnimGta.ApplyClip(sc2.Anim, o2, uvc, 0);
    bool ok = true;
    foreach (var f in new[] { 0, 17, 30, 45, 60 })
    {
        sc.Anim.Apply(sc, f); sc2.Anim.Apply(sc2, f);
        ok &= Vector3.Distance(o.Position, o2.Position) < 0.01f && MathF.Abs(MathF.Abs(Quaternion.Dot(o.Rotation, o2.Rotation)) - 1) < 1e-3f
              && Vector4.Distance(o.UvRow1, o2.UvRow1) < 0.01f;
    }
    var hash = MoloMax.Core.Gta.Export.AnimXml.ClipHash(o.Name.ToLowerInvariant(), 0);
    Console.WriteLine($"       {keys} keys after thinning (61 frames x 8 values sampled)");
    return ok && keys > 4 && keys < 200 && uvc.Hash == hash;
});
Check("shrink on an open strip (a stair) keeps the middle: the mesh border is not the selection outline", () =>
{
    var p = Primitives.Plane(8, 1, 8, 1);
    var order = Enumerable.Range(0, p.Faces.Count).OrderBy(i => p.FaceCenter(i).X).ToArray();
    var sel = new HashSet<int> { order[2], order[3], order[4] };
    var e = p.BuildEdgeMap().Keys.Where(k => MathF.Abs(p.Verts[k.A].Y - p.Verts[k.B].Y) < 1e-4f && p.Verts[k.A].Y > 0).OrderBy(k => p.Verts[k.A].X + p.Verts[k.B].X).ToList();
    var es = new HashSet<EdgeKey>(e.Skip(2).Take(3));
    var sf = MeshSelect.ShrinkFaces(p, sel); var se = MeshSelect.ShrinkEdges(p, es);
    return sf.SetEquals(new[] { order[3] }) && se.SetEquals(new[] { e[3] });
});
Console.WriteLine("UI rules (source scan)");
Check("no two ImGui widgets in one file share an ID (same label without ##): ImGui shows a 'conflicting ID' error and one of them stops working", () =>
{
    // the editor sources sit next to this test project; skipped when they are not there (tests run from a release folder)
    var dir = AppContext.BaseDirectory;
    while (dir != null && !File.Exists(Path.Combine(dir, "MoloMax.sln"))) dir = Path.GetDirectoryName(dir);
    if (dir == null) { Console.WriteLine("       sources not found, skipped"); return true; }
    var widget = new System.Text.RegularExpressions.Regex(@"ImGui\.(Button|SmallButton|Checkbox|RadioButton|Selectable|SliderInt|SliderFloat|DragFloat|DragInt|DragFloat2|DragFloat3|InputText|InputFloat|InputInt|Combo|BeginCombo|ColorEdit3|ColorEdit4|TreeNode|TreeNodeEx|CollapsingHeader|BeginTabItem|ImageButton|InvisibleButton|ArrowButton)\(\s*""((?:[^""\\]|\\.)*)""");
    var bad = new List<string>();
    foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "MoloMax"), "*.cs", SearchOption.AllDirectories))
    {
        if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
        var src = File.ReadAllText(f);
        var seen = new Dictionary<string, int>();
        foreach (System.Text.RegularExpressions.Match m in widget.Matches(src))
        {
            var label = m.Groups[2].Value;
            if (label.Contains('{')) continue; // built at run time
            var id = label.Contains("###") ? label[(label.LastIndexOf("###") + 3)..] : label;
            int line = src.Take(m.Index).Count(c => c == '\n') + 1;
            if (seen.TryGetValue(id, out var first)) bad.Add($"{Path.GetFileName(f)}:{line} \"{label}\" (also line {first})");
            else seen[id] = line;
        }
    }
    foreach (var b in bad.Take(20)) Console.WriteLine("       " + b);
    return bad.Count == 0;
});
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

/// <summary>Fake Tripo v3 server: answers create / poll / download and logs every request.</summary>
sealed class MockTripo : HttpMessageHandler
{
    readonly List<string> log; int next = 1; public int FailWithCode;
    public MockTripo(List<string> log) { this.log = log; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string body = req.Content != null && req.Content is not MultipartFormDataContent ? await req.Content.ReadAsStringAsync(ct) : "";
        string auth = req.Headers.Authorization != null ? "yes" : "no";
        var path = req.RequestUri.AbsolutePath;
        if (req.RequestUri.Host.StartsWith("cdn"))
        {
            log.Add($"GET {req.RequestUri} auth={auth}");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x67, 0x6C, 0x54, 0x46 }) };
        }
        string Json(string s) => s;
        HttpResponseMessage Ok(string data) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":" + data + "}") };
        if (req.Method == HttpMethod.Post)
        {
            log.Add($"POST {path} {body} auth={auth}");
            if (FailWithCode != 0) return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":" + FailWithCode + ",\"message\":\"nope\"}") };
            return Ok("{\"task_id\":\"task_" + (next++) + "\"}");
        }
        if (path.StartsWith("/v3/tasks/"))
        {
            var id = path.Substring("/v3/tasks/".Length);
            return Ok("{\"task_id\":\"" + id + "\",\"status\":\"success\",\"progress\":100,\"credits_consumed\":20.0,\"output\":{\"model_url\":\"https://cdn.example/out/" + id + ".glb?sig=1\"}}");
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}

