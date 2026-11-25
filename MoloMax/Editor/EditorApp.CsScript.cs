using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// Scripting inside Polymorph, like Python in Blender (bpy): C# code runs in the app with the whole scene, the mesh tools and
/// the editor at hand. Reached from the API ("script.run", the MCP tool script_run).
/// A run is one undo step. Globals: app, scene, sel, active, print(...), Do(...), Create(...), Import(...) and more (see ScriptGlobals).
/// </summary>
public sealed partial class EditorApp
{
    static ScriptOptions scriptOptions;

    static ScriptOptions ScriptOpts() => scriptOptions ??= ScriptOptions.Default
        .WithReferences(typeof(EditorApp).Assembly, typeof(PolyMesh).Assembly, typeof(Vector3).Assembly, typeof(Enumerable).Assembly,
                        typeof(System.Text.Json.JsonSerializer).Assembly, typeof(System.Collections.Generic.List<>).Assembly,
                        typeof(System.Text.RegularExpressions.Regex).Assembly)
        .WithImports("System", "System.IO", "System.Linq", "System.Numerics", "System.Collections.Generic", "System.Text",
                     "MoloMax.Core.Mesh", "MoloMax.Core.Scenes", "MoloMax.Core.Gta", "MoloMax.Core.Modifiers", "MoloMax.Editor")
        .WithEmitDebugInformation(false);

    public sealed class ScriptRun
    {
        public bool Ok { get; set; }
        public string Output { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public double Ms { get; set; }
        public int Objects { get; set; }
    }

    /// <summary>Compiles and runs C# code on the UI thread (one undo step). Returns what it printed, its last value or the error.</summary>
    public ScriptRun RunCSharp(string code)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = new ScriptGlobals(this);
        var run = new ScriptRun();
        long sig0 = ScriptSceneSig();
        Undo.Record("Script", Scene, anyObject: true);
        long scriptStamp = Undo.LastStamp;
        try
        {
            var st = CSharpScript.RunAsync(code ?? "", ScriptOpts(), g).GetAwaiter().GetResult();
            run.Ok = true;
            run.Result = st.ReturnValue switch { null => null, string s => s, var v => FormatValue(v) };
        }
        catch (CompilationErrorException ce)
        {
            run.Error = "Compile error:\n" + string.Join("\n", ce.Diagnostics.Take(12).Select(d => d.ToString()));
            Undo.DropLast();
        }
        catch (Exception e)
        {
            var ex = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            run.Error = ex.GetType().Name + ": " + ex.Message + "\n" + string.Join("\n", (ex.StackTrace ?? "").Split('\n').Where(l => l.Contains("Submission") || l.Contains("ScriptGlobals")).Take(5));
        }
        // what the script did through the editor (Create, AddLight...) recorded its own steps: they all fold into the one "Script" step
        while (Undo.CanUndo && Undo.LastStamp != scriptStamp && Undo.LastStamp > scriptStamp) Undo.DropLastKeepRedo();
        // a run that failed before changing anything leaves no undo step; a run that worked keeps one (it may have changed
        // things the quick signature does not see, like a light's settings)
        if (run.Error?.StartsWith("Compile error") != true)
        {
            // a script that only read (nothing changed, no editor step) leaves the scene as it was: no undo step, not "modified"
            // (it marked the scene dirty, so closing afterwards waited on a "save changes?" dialog, and swapped the scene objects)
            if (ScriptSceneSig() == sig0 && Undo.LastStamp == scriptStamp) Undo.DropLast();
            else { Dirty = true; AfterSceneSwap(); ResetSelectionBaseline(); }
        }
        run.Output = g.Out.ToString();
        run.Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        run.Objects = Scene.Objects.Count;
        csLog.Add((code, run));
        if (csLog.Count > 50) csLog.RemoveAt(0);
        return run;
    }

    long ScriptSceneSig()
    {
        long h = Scene.Objects.Count * 7919L + Scene.Materials.Count * 104729L;
        foreach (var o in Scene.Objects)
            h = h * 31 + HashCode.Combine(o.Id, o.Version, o.Position, o.Rotation, o.Scale, o.Hidden, o.ParentId, o.Name);
        foreach (var m in Scene.Materials) h = h * 31 + HashCode.Combine(m.Name, m.Shader, m.DiffuseTexture);
        return h;
    }

    static string FormatValue(object v)
    {
        try
        {
            return v switch
            {
                SceneObject o => $"{o.Name} (id {o.Id}, {o.Mesh?.Verts.Count} verts, {o.Mesh?.Faces.Count} faces)",
                IEnumerable<SceneObject> os => string.Join("\n", os.Select(o => $"{o.Name} (id {o.Id})")),
                Vector3 p => $"[{p.X:0.###}, {p.Y:0.###}, {p.Z:0.###}]",
                System.Collections.IEnumerable e and not string => string.Join(", ", e.Cast<object>().Take(200)),
                _ => v.ToString(),
            };
        }
        catch { return v.ToString(); }
    }

    readonly List<(string Code, ScriptRun Run)> csLog = new();

    /// <summary>What a script sees as globals: the editor, the scene and short helpers (all public, so scripts can call them).</summary>
    public sealed class ScriptGlobals
    {
        readonly EditorApp a;
        internal readonly StringBuilder Out = new();
        internal bool Touched = true;
        public ScriptGlobals(EditorApp app) { a = app; }

        /// <summary>The editor itself (public members); private ones through Invoke / Get / Set.</summary>
        public EditorApp app => a;
        public Scene scene => a.Scene;
        /// <summary>Selected objects (in selection order of ids).</summary>
        public List<SceneObject> sel => a.Selected.Select(a.Scene.Find).Where(o => o != null).ToList();
        public SceneObject active => a.Current;
        public Vector3 cursor { get => a.Cursor3D; set => a.Cursor3D = value; }

        public void print(params object[] values) => Out.AppendLine(string.Join(" ", values.Select(v => v is null ? "null" : v is string s ? s : FormatValue(v))));

        // ---- finding
        public SceneObject Find(string name) => a.Scene.Objects.FirstOrDefault(o => o.Name == name) ?? a.Scene.Objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        public SceneObject Find(int id) => a.Scene.Find(id);
        public List<SceneObject> FindAll(string contains) => a.Scene.Objects.Where(o => o.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
        public List<SceneObject> Objects => a.Scene.Objects.ToList();

        // ---- creating
        /// <summary>A primitive like Create menu: "box", "plane", "sphere", "cylinder", "cone", "tube", "room", "wall", "stairs", "door", "portal", "collisionbox".</summary>
        public SceneObject Create(string kind, Vector3 at = default, string name = null)
        {
            var k = Enum.GetValues<CreateKind>().FirstOrDefault(x => string.Equals(x.ToString(), kind.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
            if (k == CreateKind.None) throw new ArgumentException("Unknown kind: " + kind + " (" + string.Join(", ", Enum.GetNames<CreateKind>().Skip(1)) + ")");
            var o = a.Prim(k, at);
            if (name != null) o.Name = a.Scene.Objects.Any(x => x != o && x.Name == name) ? o.Name : name;
            return o;
        }
        /// <summary>Adds an object from a mesh you built (Primitives.Box(...), new PolyMesh(), MeshOps...).</summary>
        public SceneObject Add(string name, PolyMesh mesh, Vector3 at = default, GtaObjectType type = GtaObjectType.Drawable)
        {
            var o = a.Scene.Add(name, mesh, at, type);
            if (o.Name != name && !a.Scene.Objects.Any(x => x != o && x.Name == name)) o.Name = name; // the name asked for when it is free
            return o;
        }
        public void Delete(SceneObject o) { if (o != null) { var ids = a.Scene.Descendants(o).Select(d => d.Id).Append(o.Id).ToHashSet(); a.Scene.RemoveObjects(x => ids.Contains(x.Id)); a.Selected.RemoveWhere(ids.Contains); } }

        // ---- selection
        public void Select(params SceneObject[] objs) { if (objs.Length == 0) { a.SelectNone(); return; } a.SelectOnly(objs); }
        public void SelectNone() => a.SelectNone();
        /// <summary>"object", "vertex", "edge", "border", "polygon", "element".</summary>
        public void Level(string level) => a.SetLevel(Enum.Parse<SubObject>(level, true));
        public PolySelection Sub(SceneObject o) => a.SubSel(o);

        // ---- files
        public List<SceneObject> Import(string path, Vector3? at = null) => a.ImportFile(path, at);
        public void Open(string path) => a.OpenFile(path);
        public void SaveAs(string path) { a.CurrentFile = path; a.Save(false); }

        // ---- materials
        public GtaMaterial Material(string name) => a.Scene.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        public GtaMaterial NewMaterial(string name, string diffuseTexture = null, string shader = "normal_spec")
        {
            var m = a.Scene.AddMaterial(name, new Vector3(0.8f));
            m.Shader = shader; if (diffuseTexture != null) m.DiffuseTexture = diffuseTexture;
            return m;
        }
        /// <summary>Puts a material on every face of an object (or on the given faces).</summary>
        public void SetMaterial(SceneObject o, GtaMaterial m, IEnumerable<int> faces = null)
        {
            MeshOps.SetMaterial(o.Mesh, faces ?? Enumerable.Range(0, o.Mesh.Faces.Count), m.Id); o.Touch();
        }

        // ---- GTA
        public SceneObject AddLight(string kind = "point", Vector3? at = null)
        {
            var keep = a.Cursor3D;
            if (at is { } p) a.Cursor3D = p;
            a.AddLight(Enum.Parse<GtaLightKind>(kind, true));
            var l = a.Current;
            if (at is { } q && l != null) l.Position = q; // exactly where asked (the menu puts it above the cursor)
            a.Cursor3D = keep;
            return l;
        }

        // ---- view
        public void Frame() => a.FrameSelection();
        public void FrameAll() => a.FrameAll();
        public void Status(string text) => a.SetStatus(text);

        /// <summary>Any API method (as in python/README): Call("gta.export", new { folder = "C:/x" }).</summary>
        public object Call(string method, object args = null)
        {
            var json = args == null ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)) as System.Text.Json.Nodes.JsonObject;
            return a.Api.CallLocal(method, json);
        }

        // ---- the rest of the editor, private members included (reflection)
        public object Invoke(string method, params object[] args)
        {
            var ms = typeof(EditorApp).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == method && m.GetParameters().Length >= args.Length && m.GetParameters().Skip(args.Length).All(p => p.HasDefaultValue)).ToList();
            if (ms.Count == 0) throw new MissingMethodException("EditorApp." + method);
            var mi = ms.FirstOrDefault(m => m.GetParameters().Take(args.Length).Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x)) ?? ms[0];
            var full = mi.GetParameters().Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
            return mi.Invoke(mi.IsStatic ? null : a, full);
        }
        public object Get(string member)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return typeof(EditorApp).GetField(member, F)?.GetValue(a) ?? typeof(EditorApp).GetProperty(member, F)?.GetValue(a)
                   ?? throw new MissingMemberException("EditorApp." + member);
        }
        public void Set(string member, object value)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (typeof(EditorApp).GetField(member, F) is { } f) f.SetValue(a, value);
            else if (typeof(EditorApp).GetProperty(member, F) is { CanWrite: true } p) p.SetValue(a, value);
            else throw new MissingMemberException("EditorApp." + member);
        }
        /// <summary>Lists editor members whose name contains the text (to find what to Invoke).</summary>
        public string Members(string contains = "")
            => string.Join("\n", typeof(EditorApp).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains(contains, StringComparison.OrdinalIgnoreCase) && !m.Name.StartsWith("<") && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                .Select(m => m is MethodInfo mi ? $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})" : $"{m.MemberType} {m.Name}")
                .Distinct().OrderBy(s => s).Take(300));
    }
}
