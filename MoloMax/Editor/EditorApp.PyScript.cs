using System.Numerics;
using ImGuiNET;
using MoloMax.UI;
using Python.Runtime;

namespace MoloMax.Editor;

/// <summary>
/// Python inside Polymorph, like Blender: an embedded CPython (folder "pyembed" next to the exe, nothing to install) runs
/// scripts and add-ons in the app itself. The "poly" module (python/inapp/poly.py) is the bpy of Polymorph: scene, objects,
/// meshes, materials, lights and any editor member. Add-ons: .py files with register() in %APPDATA%\Polymorph\addons
/// (and python/addons next to the exe), shown in the Add-ons menu. A script run or an add-on click is one undo step.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>What poly.py talks to (the editor and its helpers).</summary>
    public static ScriptGlobals PyContext { get; private set; }
    static EditorApp pyApp;
    bool pyReady; string pyError;
    readonly List<string> pyMenu = new();
    bool pyMenuDirty = true;
    public static string AddonsFolder => Path.Combine(AppSettings.DataDir, "addons");
    static string PyEmbedFolder => Path.Combine(AppContext.BaseDirectory, "pyembed");
    static string PyInAppFolder => Path.Combine(AppContext.BaseDirectory, "python", "inapp");
    static string PyBundledAddons => Path.Combine(AppContext.BaseDirectory, "python", "addons");

    /// <summary>poly.register_menu() calls this: the Add-ons menu is read again.</summary>
    public static void PyMenuChanged() { if (pyApp != null) pyApp.pyMenuDirty = true; }

    bool EnsurePython()
    {
        if (pyReady) return true;
        if (pyError != null) return false;
        try
        {
            var dll = Path.Combine(PyEmbedFolder, "python312.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("The embedded Python is missing (pyembed\\python312.dll).");
            Runtime.PythonDLL = dll;
            PythonEngine.PythonHome = PyEmbedFolder;
            PythonEngine.PythonPath = string.Join(";", new[]
            {
                Path.Combine(PyEmbedFolder, "python312.zip"), PyEmbedFolder, Path.Combine(PyEmbedFolder, "Lib", "site-packages"),
                PyInAppFolder, Path.Combine(AppContext.BaseDirectory, "python"),
            });
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
            pyApp = this; PyContext = new ScriptGlobals(this);
            using (Py.GIL())
            {
                // the embeddable package's ._pth file fixes sys.path: our folders are added here
                var dirs = new[] { PyInAppFolder, Path.Combine(AppContext.BaseDirectory, "python"), Path.Combine(PyEmbedFolder, "Lib", "site-packages") };
                PythonEngine.Exec("import sys\nfor p in " + "[" + string.Join(", ", dirs.Select(d => "r'" + d + "'")) + "]" + ":\n    if p not in sys.path: sys.path.insert(0, p)\n");
                Py.Import("poly");
            }
            pyReady = true;
            return true;
        }
        catch (Exception e)
        {
            pyError = e.GetType().Name + ": " + e.Message;
            SetStatus("Python could not start: " + pyError);
            return false;
        }
    }

    /// <summary>Runs Python code in the app (one undo step). Output = what print() wrote, Result = a last bare expression.</summary>
    public ScriptRun RunPython(string code)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = new ScriptRun();
        if (!EnsurePython()) { run.Error = "Python is not available: " + pyError; return run; }
        Undo.Record("Python script", Scene, anyObject: true);
        long scriptStamp = Undo.LastStamp;
        long sig0 = ScriptSceneSig();
        try
        {
            using (Py.GIL())
            {
                using var poly = Py.Import("poly");
                using var res = poly.InvokeMethod("_run", new PyString(code ?? ""));
                run.Output = res[0].As<string>();
                run.Result = res[1].IsNone() ? null : res[1].As<string>();
                run.Error = res[2].IsNone() ? null : res[2].As<string>();
                run.Ok = run.Error == null;
            }
        }
        catch (Exception e) { run.Error = e.GetType().Name + ": " + e.Message; }
        // steps recorded inside (create, add light...) fold into the one "Python script" step
        while (Undo.CanUndo && Undo.LastStamp > scriptStamp) Undo.DropLastKeepRedo();
        if (!run.Ok && ScriptSceneSig() == sig0) Undo.DropLast();
        else { Dirty = true; AfterSceneSwap(); ResetSelectionBaseline(); }
        run.Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        run.Objects = Scene.Objects.Count;
        pyLog.Add(run);
        if (pyLog.Count > 30) pyLog.RemoveAt(0);
        return run;
    }

    readonly List<ScriptRun> pyLog = new();

    /// <summary>Loads (or reloads) every add-on: the bundled ones, then the user's folder.</summary>
    public string LoadPyAddons()
    {
        if (!EnsurePython()) return "Python is not available: " + pyError;
        var sb = new System.Text.StringBuilder();
        try
        {
            Directory.CreateDirectory(AddonsFolder);
            using (Py.GIL())
            {
                using var poly = Py.Import("poly");
                foreach (var folder in new[] { PyBundledAddons, AddonsFolder })
                {
                    using var rep = poly.InvokeMethod("_load_addons", new PyString(folder));
                    for (long k = 0, n = rep.Length(); k < n; k++)
                    {
                        using var item = rep[(int)k];
                        string name = item[0].As<string>();
                        string err = item[1].IsNone() ? null : item[1].As<string>();
                        sb.AppendLine(err == null ? "loaded " + name : "FAILED " + name + "\n" + err);
                    }
                }
            }
        }
        catch (Exception e) { sb.AppendLine(e.Message); }
        pyMenuDirty = true;
        addonReport = sb.ToString();
        return addonReport;
    }
    string addonReport = "";

    void RefreshPyMenu()
    {
        if (!pyMenuDirty || !pyReady) return;
        pyMenuDirty = false; pyMenu.Clear();
        try { using (Py.GIL()) { using var poly = Py.Import("poly"); using var l = poly.InvokeMethod("_menu_labels"); for (int k = 0, n = (int)l.Length(); k < n; k++) pyMenu.Add(l[k].As<string>()); } }
        catch { }
    }

    /// <summary>At start: add-ons load like in Blender (Python starts only when there are some).</summary>
    void StartupAddons()
    {
        if (SelfTestDir != null) return;
        bool any = new[] { PyBundledAddons, AddonsFolder }.Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.py").Any(f => !Path.GetFileName(f).StartsWith("_")));
        if (any) LoadPyAddons();
    }

    /// <summary>Runs an add-on menu entry (one undo step).</summary>
    public ScriptRun RunAddonMenu(int index)
    {
        var run = new ScriptRun();
        if (!EnsurePython()) { run.Error = pyError; return run; }
        Undo.Record(index < pyMenu.Count ? pyMenu[index] : "Add-on", Scene, anyObject: true);
        long stamp = Undo.LastStamp;
        try
        {
            using (Py.GIL())
            {
                using var poly = Py.Import("poly");
                using var res = poly.InvokeMethod("_menu_run", new PyInt(index));
                run.Output = res[0].As<string>();
                run.Error = res[1].IsNone() ? null : res[1].As<string>();
                run.Ok = run.Error == null;
            }
        }
        catch (Exception e) { run.Error = e.Message; }
        while (Undo.CanUndo && Undo.LastStamp > stamp) Undo.DropLastKeepRedo();
        Dirty = true; AfterSceneSwap(); ResetSelectionBaseline();
        SetStatus(run.Ok ? (index < pyMenu.Count ? pyMenu[index] : "Add-on") + " done." + (string.IsNullOrWhiteSpace(run.Output) ? "" : " " + run.Output.Trim().Split('\n').Last())
                         : "Add-on error: " + run.Error?.Trim().Split('\n').Last());
        if (!run.Ok || !string.IsNullOrWhiteSpace(run.Output)) { pyEditorOpen = true; pyEditorOut = (run.Output ?? "") + (run.Error ?? ""); }
        return run;
    }

    // ---------------------------------------------------------------- Add-ons menu and the script editor window

    bool pyEditorOpen; string pyEditorCode = "import poly\n\n# a box, a room with its faces inside, a material and a light\nbox = poly.create(\"box\", (0, 0, 0), \"crate\")\nroom = poly.add(\"room_01\", poly.Primitives.Box(6, 4, 3), (8, 0, 0))\npoly.MeshOps.FlipAll(room.Mesh); room.Touch()\npoly.set_material(room, poly.new_material(\"wall_paint\"))\npoly.add_light(\"spot\", (8, 0, 2.5))\nprint(len(poly.objects()), \"objects\")\n";
    string pyEditorOut = "";

    void AddonsMenu()
    {
        if (!ImGui.BeginMenu("Add-ons")) return;
        if (!pyReady && pyError == null && ImGui.MenuItem("Load add-ons")) LoadPyAddons();
        RefreshPyMenu();
        for (int i = 0; i < pyMenu.Count; i++) if (ImGui.MenuItem(pyMenu[i] + "##pym" + i)) RunAddonMenu(i);
        if (pyMenu.Count > 0) ImGui.Separator();
        if (ImGui.MenuItem("Python Script Editor...")) pyEditorOpen = true;
        if (ImGui.MenuItem("Reload add-ons")) { LoadPyAddons(); pyEditorOut = addonReport; }
        if (ImGui.MenuItem("Open add-ons folder")) { Directory.CreateDirectory(AddonsFolder); OpenUrl(AddonsFolder); }
        if (pyError != null) Ui.TextDisabled("Python: " + pyError);
        ImGui.EndMenu();
    }

    void DrawPyEditor()
    {
        if (!pyEditorOpen) return;
        ImGui.SetNextWindowSize(new Vector2(640, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Python Script Editor", ref pyEditorOpen)) { ImGui.End(); return; }
        bool run = ImGui.Button("Run  (Ctrl+Enter)"); TutRect("py_run");
        ImGui.SameLine(); if (ImGui.Button("Clear output")) pyEditorOut = "";
        ImGui.SameLine(); Ui.TextDisabled("import poly  -  like bpy. One run = one Ctrl+Z.");
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InputTextMultiline("##pycode", ref pyEditorCode, 200000, new Vector2(-1, avail.Y * 0.62f), ImGuiInputTextFlags.AllowTabInput);
        if (ImGui.IsItemFocused() && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Enter)) run = true;
        if (run)
        {
            var r = RunPython(pyEditorCode);
            pyEditorOut = (r.Output ?? "") + (r.Result != null ? "-> " + r.Result + "\n" : "") + (r.Error ?? "") + $"({r.Ms} ms)\n";
        }
        ImGui.InputTextMultiline("##pyout", ref pyEditorOut, 200000, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        ImGui.End();
    }
}
