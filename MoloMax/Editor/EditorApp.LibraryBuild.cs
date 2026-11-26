using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Gta.Library;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>"Build from my GTA V": anyone gets the Molo texture library from their own game (nothing shipped).</summary>
public sealed partial class EditorApp
{
    System.Threading.Tasks.Task libBuildTask;
    System.Threading.CancellationTokenSource libBuildCancel;
    string libBuildStatus; float libBuildProgress; string libBuildError; LibraryBuilder.Result libBuildResult;

    /// <summary>Where a library built from the game goes (never over a library made elsewhere).</summary>
    static string BuiltLibraryFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "TextureLibrary");

    public bool LibraryBuilding => libBuildTask is { IsCompleted: false };

    /// <summary>Starts the build in the background (loads the game files first when needed).</summary>
    public void StartLibraryBuild()
    {
        if (LibraryBuilding) return;
        string gta = Settings.GtaFolder;
        if (!GameFiles.LooksLikeGta(gta, out _)) { libBuildError = "Set the GTA V folder first (the one with GTA5.exe)."; return; }
        libBuildError = null; libBuildResult = null; libBuildProgress = 0; libBuildStatus = "Starting...";
        libBuildCancel = new System.Threading.CancellationTokenSource();
        var ct = libBuildCancel.Token; var target = BuiltLibraryFolder;
        libBuildTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var g = game;
                if (g == null)
                {
                    libBuildStatus = "Opening the game files...";
                    g = GameFiles.Open(gta, s => libBuildStatus = s, Settings.GtaKey);
                    Settings.GtaKey = GameFiles.CurrentKey; Settings.Save();
                    game ??= g;
                }
                var r = LibraryBuilder.Build(g, target, (s, p) => { libBuildStatus = s; libBuildProgress = p; }, ct);
                libBuildResult = r;
                Settings.LibraryFolder = target; Settings.Save();
                library = null; // reloaded on the next frame from the new folder
            }
            catch (OperationCanceledException) { libBuildError = "Stopped. Click again to start over."; }
            catch (Exception e) { libBuildError = e.Message; }
        });
    }

    /// <summary>The block shown in the preferences and in the Molo Materials window when there is no library.</summary>
    void LibraryBuildPanel()
    {
        Ui.TextColored(Theme.Gold, "Build from my GTA V");
        Ui.TextWrapped("Reads every interior of your own game and makes the texture library from it: each material with its normal / spec maps, sorted into floors, walls, ceilings... Nothing is downloaded, nothing from the game is shared.");
        string gta = Settings.GtaFolder ?? "";
        bool ok = GameFiles.LooksLikeGta(gta, out bool gen9);
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputTextWithHint("##libgta", @"C:\Program Files\Rockstar Games\Grand Theft Auto V", ref gta, 400)) { Settings.GtaFolder = gta; Settings.Save(); }
        ImGui.SameLine();
        if (ImGui.Button("Browse##libgta", new Vector2(-1, 0)) && PickFolder("GTA V folder (with GTA5.exe)") is { } pf) { Settings.GtaFolder = pf; Settings.Save(); }
        Ui.TextDisabled(ok ? (gen9 ? "GTA V Enhanced found." : "GTA V found.") : "Pick the folder that contains GTA5.exe.");
        if (LibraryBuilding)
        {
            ImGui.ProgressBar(libBuildProgress, new Vector2(-90, 0), $"{libBuildProgress * 100:0}%");
            ImGui.SameLine();
            if (ImGui.Button("Stop", new Vector2(-1, 0))) libBuildCancel?.Cancel();
            Ui.TextDisabled(libBuildStatus ?? "");
        }
        else
        {
            if (!ok) ImGui.BeginDisabled();
            if (ImGui.Button(libBuildResult != null ? "Build again" : "Build the library from my game", new Vector2(260, 30))) StartLibraryBuild();
            TutRect("lib_build");
            if (!ok) ImGui.EndDisabled();
            Ui.TextDisabled($"Goes into {BuiltLibraryFolder}. About 15-20 minutes and 4-5 GB of disk (every interior of the game is read). You can keep working meanwhile.");
        }
        if (libBuildResult != null) Ui.TextColored(Theme.Gold, "Done: " + libBuildResult);
        if (libBuildError != null) Ui.TextColored(new Vector4(1, 0.45f, 0.4f, 1), libBuildError);
    }

    IEnumerable<object> LibraryBuildTest(string gtaFolder)
    {
        Fresh();
        var keepLibrary = Settings.LibraryFolder; // put back at the end: the tests after this one use the real library
        Settings.GtaFolder = gtaFolder; Settings.LibraryFolder = Path.Combine(Path.GetTempPath(), "no_library_here"); library = null;
        prefsOpen = true; prefsSection = Array.IndexOf(PrefSections, "Texture library");
        yield return Frames(6);
        yield return Shot("library_missing");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var x in ClickAt(TutCentre("lib_build"), false, 10)) yield return x;
        Expect(LibraryBuilding, "the build starts from the button");
        yield return Frames(30);
        yield return Shot("library_building");
        yield return Until(() => !LibraryBuilding, 3600);
        Note($"build: {libBuildResult} {libBuildError} ({sw.Elapsed.TotalMinutes:0.0} min)");
        yield return Frames(4);
        EnsureLibrary();
        Expect(library != null && library.Materials.Count > 500, $"library loaded from {Settings.LibraryFolder}: {library?.Materials.Count ?? 0} materials");
        if (library == null) { Settings.LibraryFolder = keepLibrary; library = null; prefsOpen = false; yield break; }
        var cats = MaterialLibrary.Categories.Skip(1).Select(c => $"{c} {library.Materials.Count(m => MaterialLibrary.InCategory(m, c))}");
        Note("categories: " + string.Join(", ", cats));
        int withTex = library.Materials.Count(m => library.TexturePath(m.Diffuse) != null);
        Expect(withTex > library.Materials.Count * 0.8, $"{withTex} / {library.Materials.Count} materials have their diffuse .dds");
        yield return Shot("library_done_prefs");
        prefsOpen = false;
        libraryOpen = true; libCategory = "Floors"; libSearch = ""; libResultsKey = null;
        yield return Frames(40);
        yield return Shot("library_gallery_floors");
        libCategory = "Walls"; libResultsKey = null; yield return Frames(40);
        yield return Shot("library_gallery_walls");
        libraryOpen = false;
        if (string.IsNullOrEmpty(libBuildResult)) { Settings.LibraryFolder = keepLibrary; library = null; }
    }
}
