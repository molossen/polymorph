using System.Windows.Forms;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using MoloMax.Core.Addons;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Preferences window (Edit > Preferences, Ctrl+, like Blender): keymap, import, texture library, Tripo,
/// and Add-ons — .dll add-ons in %APPDATA%\MoloMax\addons, loaded at start, switched on and off here.
/// </summary>
public sealed partial class EditorApp : IAddonHost
{
    bool prefsOpen;
    int prefsSection;
    static readonly string[] PrefSections = { "Keymap", "Interface", "Import", "Texture library", "Tripo", "Add-ons", "Files & autosave", "GTA & FiveM", "Python & MCP" };

    // ---------------------------------------------------------------------------------- add-ons

    sealed class LoadedAddon
    {
        public string File, Name, Version, Author, Description, Error;
        public IMoloAddon Instance;
        public bool Registered;
        public readonly List<string> Commands = new();
        public readonly List<(string Title, Action Draw)> Panels = new();
    }
    readonly List<LoadedAddon> addons = new();
    LoadedAddon registering;
    bool addonsLoaded;

    public static string AddonFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Polymorph", "addons");

    void LoadAddons()
    {
        if (addonsLoaded) return;
        addonsLoaded = true;
        Directory.CreateDirectory(AddonFolder);
        foreach (var dll in Directory.GetFiles(AddonFolder, "*.dll", SearchOption.AllDirectories))
        {
            // only assemblies with an add-on class (helper dlls next to them are skipped)
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var t in asm.GetExportedTypes().Where(t => typeof(IMoloAddon).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                {
                    var inst = (IMoloAddon)Activator.CreateInstance(t);
                    var a = new LoadedAddon { File = dll, Instance = inst, Name = inst.Name, Version = inst.Version, Author = inst.Author, Description = inst.Description };
                    addons.Add(a);
                    if (!Settings.DisabledAddons.Contains(a.Name)) EnableAddon(a);
                }
            }
            catch (Exception e)
            {
                if (e is BadImageFormatException) continue; // native dll
                addons.Add(new LoadedAddon { File = dll, Name = Path.GetFileNameWithoutExtension(dll), Error = e.Message });
            }
        }
    }

    void EnableAddon(LoadedAddon a)
    {
        if (a.Registered || a.Instance == null) return;
        registering = a;
        try { a.Instance.Register(this); a.Registered = true; a.Error = null; }
        catch (Exception e) { a.Error = e.Message; }
        finally { registering = null; }
    }

    void DisableAddon(LoadedAddon a)
    {
        if (!a.Registered) return;
        try { a.Instance.Unregister(); } catch { }
        Commands().RemoveAll(c => a.Commands.Contains(c.Name) && c.Category != "File");
        a.Commands.Clear(); a.Panels.Clear(); a.Registered = false;
    }

    // IAddonHost
    Scene IAddonHost.Scene => Scene;
    IReadOnlyList<SceneObject> IAddonHost.Selection => SelObjects().OrderBy(o => o.Id == ActiveId ? 0 : 1).ToList();
    SceneObject IAddonHost.Active => Current;
    void IAddonHost.AddCommand(string name, string category, Action run, string description)
    {
        AddAddonCommand(name, category, run, description);
        registering?.Commands.Add(name);
    }
    void IAddonHost.AddPanel(string title, Action draw) => registering?.Panels.Add((title, draw));
    void IAddonHost.Undoable(string name, Action change) => Do(name, change);
    SceneObject IAddonHost.AddObject(string name, PolyMesh mesh, Vector3 position)
    {
        SceneObject o = null;
        Do("Add " + name, () => { o = Scene.Add(name, mesh, position); Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; });
        return o;
    }
    void IAddonHost.Status(string message) => SetStatus(message);
    string IAddonHost.DataFolder(string addonName)
    {
        var d = Path.Combine(AddonFolder, "_data", Core.Gta.Export.TextureBank.SafeName(addonName));
        Directory.CreateDirectory(d);
        return d;
    }

    bool AnyAddonPanels => addons.Any(a => a.Registered && a.Panels.Count > 0);

    /// <summary>Properties tab "Add-ons": the panels of every enabled add-on.</summary>
    void AddonProps()
    {
        foreach (var a in addons.Where(a => a.Registered))
            foreach (var (title, draw) in a.Panels)
                if (Ui.Header($"{title}##{a.Name}", Open))
                {
                    try { draw(); } catch (Exception e) { Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), e.Message); }
                }
        if (!AnyAddonPanels) Ui.TextDisabled("No add-on panel. Install add-ons in Edit > Preferences > Add-ons.");
    }

    // ---------------------------------------------------------------------------------- window

    void DrawPreferences()
    {
        if (!prefsOpen) return;
        ImGui.SetNextWindowSize(new Vector2(720, 500), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f - 360, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Polymorph Preferences###prefs", ref prefsOpen, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        ImGui.BeginChild("##prefnav", new Vector2(150, 0), ImGuiChildFlags.Borders);
        for (int i = 0; i < PrefSections.Length; i++)
            if (ImGui.Selectable(PrefSections[i], prefsSection == i)) prefsSection = i;
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##prefbody", Vector2.Zero, ImGuiChildFlags.Borders);
        switch (prefsSection)
        {
            case 0: PrefsKeymap(); break;
            case 1: PrefsInterface(); break;
            case 2: PrefsImport(); break;
            case 3: PrefsLibrary(); break;
            case 4: PrefsTripo(); break;
            case 5: PrefsAddons(); break;
            case 6: PrefsFiles(); break;
            case 7: PrefsGta(); break;
            case 8: PrefsScripting(); break;
        }
        ImGui.EndChild();
        ImGui.End();
    }

    void PrefsKeymap()
    {
        Ui.TextColored(Theme.Gold, "Keymap");
        int km = Math.Min((int)Settings.Keymap, KeymapNames.Length - 1);
        for (int i = 0; i < KeymapNames.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.RadioButton(KeymapNames[i] + "##pk", ref km, i)) SetKeymap((Keymap)i);
        }
        Ui.TextDisabled(Settings.Keymap == Keymap.Blender
            ? "Standard keymap, left-click select. Search: F3."
            : "Industry Compatible keymap (Alt+mouse navigation). Tab: edit mode. Search: Space.");
        ImGui.Separator();
        PrefsCustomKeys();
        ImGui.Separator();
        Ui.TextColored(Theme.Gold, "Keymap keys");
        if (ImGui.BeginTable("##keys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new Vector2(0, 0)))
        {
            ImGui.TableSetupColumn("Keys", ImGuiTableColumnFlags.WidthFixed, 230);
            ImGui.TableSetupColumn("Does");
            ImGui.TableNextRow(); ImGui.TableNextColumn(); Ui.Text(SearchKey); ImGui.TableNextColumn(); Ui.TextDisabled("Search every command");
            ImGui.TableNextRow(); ImGui.TableNextColumn(); Ui.Text("Ctrl+,"); ImGui.TableNextColumn(); Ui.TextDisabled("Preferences");
            foreach (var (k, d) in ShortcutTable()) { ImGui.TableNextRow(); ImGui.TableNextColumn(); Ui.Text(k); ImGui.TableNextColumn(); Ui.TextDisabled(d); }
            ImGui.EndTable();
        }
    }

    void PrefsInterface()
    {
        Ui.TextColored(Theme.Gold, "Look (theme)");
        float lw = (ImGui.GetContentRegionAvail().X - 8) / 2;
        if (Theme.Toggle("Standard##look", Settings.Mode == UiMode.Blender, new Vector2(lw, 28))) SetMode(UiMode.Blender);
        Theme.Tip("Dark look, one big view.");
        ImGui.SameLine();
        if (Theme.Toggle("Classic (4 views)##look", Settings.Mode == UiMode.Max, new Vector2(lw, 28))) SetMode(UiMode.Max);
        Theme.Tip("Classic grey colours and 4 views. Your keys stay the same.");
        Ui.TextDisabled("Also in Edit > Look and Views > Look. Only the colours and views change, never the keys.");
        ImGui.Separator();
        Ui.TextColored(Theme.Gold, "Interface");
        ImGui.Checkbox("Outliner", ref ShowExplorer);
        ImGui.Checkbox("Properties", ref ShowPanel);
        ImGui.Checkbox("Show grid", ref ShowGrid);
        ImGui.Checkbox("Back-face culling in the viewport", ref BackfaceCull);
        ImGui.Checkbox("Face orientation (front / back colours)", ref FaceOrientation);
        bool ea = Settings.ExtrudeArrow;
        if (ImGui.Checkbox("Extrude arrow on selected polygons (yellow)", ref ea)) { Settings.ExtrudeArrow = ea; Settings.Save(); }
        Theme.Tip("Off: extrude with E or the Extrude button.");
        bool po = Settings.PanelsOutside;
        if (ImGui.Checkbox("Panels can leave the window (other monitors)", ref po)) { Settings.PanelsOutside = po; Settings.Save(); SetStatus("Applied at the next start of Polymorph."); }
        Theme.Tip("Drag a floating panel (Asset Browser, Material Editor...) out of Polymorph by its title: it becomes a window of its own, on another monitor too. Drag it back over Polymorph to put it inside again. Applied at the next start.");
        Ui.TextColored(Theme.Gold, "Normals colours");
        bool ColourPref(string label, float[] c, Action<float[]> set)
        {
            var v = new Vector3(c?.Length >= 3 ? c[0] : 1, c?.Length >= 3 ? c[1] : 1, c?.Length >= 3 ? c[2] : 1);
            ImGui.SetNextItemWidth(220);
            if (!ImGui.ColorEdit3(label, ref v)) return false;
            set(new[] { v.X, v.Y, v.Z }); Settings.Save(); return true;
        }
        ColourPref("Face orientation: front", Settings.FaceFrontColour, c => Settings.FaceFrontColour = c);
        ColourPref("Face orientation: back (flipped)", Settings.FaceBackColour, c => Settings.FaceBackColour = c);
        ColourPref("Selected polygon: seen from the front", Settings.SelFrontColour, c => Settings.SelFrontColour = c);
        ColourPref("Selected polygon: seen from behind", Settings.SelBackColour, c => Settings.SelBackColour = c);
        if (ImGui.Button("Default normals colours"))
        {
            var d = new AppSettings();
            Settings.FaceFrontColour = d.FaceFrontColour; Settings.FaceBackColour = d.FaceBackColour; Settings.SelFrontColour = d.SelFrontColour; Settings.SelBackColour = d.SelBackColour; Settings.Save();
        }
        Ui.TextDisabled("A selected polygon shown dark is seen from behind: its normal is flipped.");
        ImGui.Separator();
        Ui.TextDisabled("Snapping");
        ImGui.SetNextItemWidth(160); ImGui.DragFloat("Grid step", ref GridStep, 0.005f, 0.001f, 100, "%.3f m");
        ImGui.SetNextItemWidth(160); ImGui.DragFloat("Angle snap", ref AngleSnap, 0.5f, 0.5f, 90, "%.1f°");
    }

    void PrefsFiles()
    {
        Ui.TextColored(Theme.Gold, "Files & autosave");
        int m = Settings.AutosaveMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Autosave every", ref m, 0, 30, m == 0 ? "off" : "%d min")) { Settings.AutosaveMinutes = m; Settings.Save(); }
        Ui.TextDisabled("Saved in the background to %APPDATA%\\Polymorph\\autosave. After a crash Polymorph offers to restore it.");
        Ui.TextDisabled("Every save also keeps the previous version as scene.mmx.bak.");
        if (ImGui.Button("Open autosave folder")) try { Directory.CreateDirectory(AutosaveDir); System.Diagnostics.Process.Start("explorer.exe", AutosaveDir); } catch { }
    }

    void PrefsGta()
    {
        Ui.TextColored(Theme.Gold, "GTA V install (game files browser)");
        string g = Settings.GtaFolder ?? "";
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputTextWithHint("##gtaf", @"C:\Program Files\Rockstar Games\Grand Theft Auto V", ref g, 400)) { Settings.GtaFolder = g; Settings.Save(); }
        ImGui.SameLine(); if (ImGui.Button("Browse##g", new Vector2(80, 0)) && PickFolder("GTA V folder (with GTA5.exe)") is { } pg) { Settings.GtaFolder = pg; Settings.Save(); }
        Ui.TextDisabled(File.Exists(Path.Combine(g, "GTA5.exe")) || File.Exists(Path.Combine(g, "GTA5_Enhanced.exe")) ? "GTA V found." : "Pick the folder that contains GTA5.exe.");
        ImGui.Separator();
        Ui.TextColored(Theme.Gold, "Local FiveM server (Send to server)");
        string r = Settings.ServerResourcesFolder ?? "";
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputTextWithHint("##srvf", @"C:\FXServer\server-data\resources\[molo]", ref r, 400)) { Settings.ServerResourcesFolder = r; Settings.Save(); }
        ImGui.SameLine(); if (ImGui.Button("Browse##s", new Vector2(80, 0)) && PickFolder("Server resources folder") is { } ps) { Settings.ServerResourcesFolder = ps; Settings.Save(); }
        string u = Settings.ServerRestartUrl ?? "";
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputTextWithHint("##srvu", "optional: http://127.0.0.1:30120/molomax/ensure (from the molomax_bridge resource)", ref u, 400)) { Settings.ServerRestartUrl = u; Settings.Save(); }
        Ui.TextDisabled("Send to server writes the resource there. With the bridge URL it also restarts it in the running server.");
        if (ImGui.Button("Install the bridge resource in my server", new Vector2(-1, 0))) InstallBridge();
        Theme.Tip("Writes molomax_bridge (a tiny server script) and gives the 3 lines to add to server.cfg.");
    }

    string PickFolder(string title)
    {
        using var d = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        var r = d.ShowDialog(form) == DialogResult.OK ? d.SelectedPath : null;
        Input.ReleaseAllKeys();
        return r;
    }

    void PrefsImport()
    {
        Ui.TextColored(Theme.Gold, "Import (GLB, glTF, OBJ)");
        bool yup = Settings.ImportYUp; if (ImGui.Checkbox("Y-up file to Z-up", ref yup)) { Settings.ImportYUp = yup; Settings.Save(); }
        float sc = Settings.ImportScale; ImGui.SetNextItemWidth(160); if (ImGui.DragFloat("Scale", ref sc, 0.01f, 0.001f, 1000f, "%.3f")) { Settings.ImportScale = sc; Settings.Save(); }
        float sa = Settings.ImportSmoothAngle; ImGui.SetNextItemWidth(160); if (ImGui.SliderFloat("Smooth angle", ref sa, 0, 180, "%.0f°")) { Settings.ImportSmoothAngle = sa; Settings.Save(); }
        bool mg = Settings.ImportMerge; if (ImGui.Checkbox("Merge all meshes into one object", ref mg)) { Settings.ImportMerge = mg; Settings.Save(); }
        Ui.TextDisabled("Scale 0.01 for files modelled in centimetres. GTA files (ydr, yft, ybn) are always imported as they are.");
    }

    void PrefsLibrary()
    {
        Ui.TextColored(Theme.Gold, "Molo texture library");
        string folder = Settings.LibraryFolder ?? Core.Gta.Library.MaterialLibrary.DefaultFolder;
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputText("##plib", ref folder, 400, ImGuiInputTextFlags.EnterReturnsTrue)) { Settings.LibraryFolder = folder; Settings.Save(); library = null; }
        ImGui.SameLine();
        if (ImGui.Button("Browse...", new Vector2(-1, 0)))
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { Description = "MoloTextureLibrary folder" };
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) { Settings.LibraryFolder = d.SelectedPath; Settings.Save(); library = null; }
            Input.ReleaseAllKeys();
        }
        EnsureLibrary();
        if (library != null) Ui.TextDisabled($"{library.Materials.Count:N0} materials loaded.");
        else Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), libraryError ?? "");
        ImGui.Separator();
        Ui.TextDisabled("Extra texture folders (searched for bare texture names)");
        if (ImGui.Button("Add texture folder..."))
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog();
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) { renderer.Textures.SearchFolders.Add(d.SelectedPath); renderer.Textures.ClearResolveCache(); }
            Input.ReleaseAllKeys();
        }
        foreach (var f in renderer.Textures.SearchFolders) Ui.TextDisabled(f);
        ImGui.Separator();
        LibraryBuildPanel();
    }

    void PrefsTripo()
    {
        Ui.TextColored(Theme.Gold, "Tripo");
        Ui.TextWrapped("The API key is stored encrypted for your Windows account. Set it or change it on the Tripo tab.");
        if (ImGui.Button("Open the Tripo tab")) { propTab = PropTab.Tripo; ShowPanel = true; prefsOpen = false; }
        bool cn = Settings.TripoChina; if (ImGui.Checkbox("Use the China endpoint", ref cn)) { Settings.TripoChina = cn; Settings.Save(); }
    }

    void PrefsAddons()
    {
        Ui.TextColored(Theme.Gold, "Add-ons");
        float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
        if (ImGui.Button("Install from file...", new Vector2(w3, 0)))
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Filter = "Polymorph add-on (*.dll)|*.dll", Multiselect = true, Title = "Install add-on" };
            if (ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK)
            {
                Directory.CreateDirectory(AddonFolder);
                foreach (var f in d.FileNames) File.Copy(f, Path.Combine(AddonFolder, Path.GetFileName(f)), true);
                SetStatus("Add-on copied. Restart Polymorph to load it.");
            }
            Input.ReleaseAllKeys();
        }
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Open add-ons folder", new Vector2(w3, 0)))
        {
            Directory.CreateDirectory(AddonFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = AddonFolder, UseShellExecute = true });
        }
        ImGui.SameLine(0, 4);
        if (ImGui.Button("How to write one", new Vector2(w3, 0))) ImGui.OpenPopup("addonhelp");
        if (ImGui.BeginPopup("addonhelp"))
        {
            Ui.Text("A Polymorph add-on is a C# class library (.NET 8):");
            Ui.TextDisabled("1. Reference MoloMax.Core.dll (and ImGui.NET.dll for panels).");
            Ui.TextDisabled("2. Write a public class implementing MoloMax.Core.Addons.IMoloAddon.");
            Ui.TextDisabled("3. In Register(host): host.AddCommand(...), host.AddPanel(...).");
            Ui.TextDisabled("4. Put the .dll in the add-ons folder and restart Polymorph.");
            Ui.TextDisabled("Example project: MoloMax\\Addons\\MoloExampleAddon in the source folder.");
            ImGui.EndPopup();
        }
        Ui.TextDisabled(AddonFolder);
        ImGui.Separator();
        if (addons.Count == 0) Ui.TextDisabled("No add-on installed.");
        foreach (var a in addons.ToList())
        {
            ImGui.PushID(a.File + a.Name);
            bool on = a.Registered;
            if (a.Instance == null) ImGui.BeginDisabled();
            if (ImGui.Checkbox("##on", ref on))
            {
                if (on) { Settings.DisabledAddons.Remove(a.Name); EnableAddon(a); }
                else { if (!Settings.DisabledAddons.Contains(a.Name)) Settings.DisabledAddons.Add(a.Name); DisableAddon(a); }
                Settings.Save();
            }
            if (a.Instance == null) ImGui.EndDisabled();
            ImGui.SameLine();
            Ui.Text($"{a.Name}  {a.Version}");
            if (!string.IsNullOrEmpty(a.Author)) { ImGui.SameLine(); Ui.TextDisabled("by " + a.Author); }
            if (!string.IsNullOrEmpty(a.Description)) Ui.TextDisabled("      " + a.Description);
            if (a.Registered) Ui.TextDisabled($"      {a.Commands.Count} command(s), {a.Panels.Count} panel(s)");
            if (a.Error != null) Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), "      " + a.Error);
            ImGui.PopID();
        }
    }
}
