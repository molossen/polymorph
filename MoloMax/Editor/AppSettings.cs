using System.Text.Json;

namespace MoloMax.Editor;

public enum Keymap { Blender, Industry, Max }

/// <summary>Blender mode (Blender look, Blender keys, single view) or 3ds Max mode (Max 2016 look, Max keys, 4 views, quad menu).</summary>
public enum UiMode { Blender, Max }

/// <summary>User preferences, kept in %APPDATA%\MoloMax\settings.json.</summary>
public sealed class AppSettings
{
    public UiMode Mode { get; set; } = UiMode.Blender;
    /// <summary>False until the user picked Blender or 3ds Max mode in the first-run dialog.</summary>
    public bool ModeChosen { get; set; }
    public Keymap Keymap { get; set; } = Keymap.Blender;
    /// <summary>False until the user picked a keymap in the first-run dialog.</summary>
    public bool KeymapChosen { get; set; }
    /// <summary>My shortcuts: command name (as in the search) -> key like "Ctrl+Shift+D". They go before the keymap's keys.</summary>
    public Dictionary<string, string> CustomKeys { get; set; } = new();
    /// <summary>Keymap keys switched off by the user (they do nothing).</summary>
    public List<string> FreeKeys { get; set; } = new();
    public float GridStep { get; set; } = 0.1f;
    public bool ImportYUp { get; set; } = true;
    public float ImportScale { get; set; } = 1f;
    public float ImportSmoothAngle { get; set; } = 40f;
    public bool ImportMerge { get; set; }
    /// <summary>Moving vertices / edges / polygons keeps the texture in place (3ds Max "Preserve UVs").</summary>
    public bool PreserveUVs { get; set; }
    /// <summary>The yellow drag arrow on selected polygons (extrude). Off by default: E or the Extrude button do it.</summary>
    public bool ExtrudeArrow { get; set; }
    /// <summary>Viewport lens like Blender's View panel: focal length in mm (0 = the default 45° view), clip start / end in metres.</summary>
    public float ViewFocalMm { get; set; }
    public float ViewClipStart { get; set; } = 0.05f;
    public float ViewClipEnd { get; set; } = 20000f;
    public string LastFolder { get; set; }
    public string LibraryFolder { get; set; }
    /// <summary>Tripo API key, encrypted for this Windows user (DPAPI), base64.</summary>
    public string TripoKey { get; set; }
    public bool TripoChina { get; set; }
    /// <summary>Add-ons (by name) the user switched off in Preferences.</summary>
    public List<string> DisabledAddons { get; set; } = new();
    /// <summary>Minutes between automatic saves (0 = off).</summary>
    public int AutosaveMinutes { get; set; } = 3;
    /// <summary>Look for a new version at start (once a day).</summary>
    public bool CheckUpdates { get; set; } = true;
    // ---- photo render (path traced)
    /// <summary>Path of the free path-tracing engine (empty = found automatically).</summary>
    public string RenderEngine { get; set; } = "";
    public int PhotoQuality { get; set; } = 1;
    public int PhotoSize { get; set; }
    public int PhotoLight { get; set; }
    public float PhotoExposure { get; set; }
    public float PhotoSky { get; set; } = 1f;
    public float PhotoLamps { get; set; } = 1f;
    public bool PhotoDenoise { get; set; } = true;
    public bool PhotoSoft { get; set; } = true;
    public bool PhotoTransparent { get; set; }
    public bool PhotoDof { get; set; }
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>The newer version found by the last check (offered again after a restart).</summary>
    public string KnownUpdateJson { get; set; }
    /// <summary>"Skip this version": not offered again until a newer one or a manual check.</summary>
    public string SkippedVersion { get; set; }
    /// <summary>Scripting API for Python and the MCP server (127.0.0.1 only, token protected).</summary>
    public bool ApiEnabled { get; set; } = true;
    /// <summary>Widths of the Scene Explorer and of the right panel (dragged by their borders).</summary>
    public float ExplorerWidth { get; set; }
    /// <summary>Face orientation colours (front / back) and the selected-polygon tint seen from the front / from behind (r, g, b).</summary>
    public float[] FaceFrontColour { get; set; } = { 0.19f, 0.36f, 1f };
    public float[] FaceBackColour { get; set; } = { 1f, 0.12f, 0.12f };
    public float[] SelFrontColour { get; set; } = { 1f, 0.05f, 0.05f };
    public float[] SelBackColour { get; set; } = { 0.32f, 0.02f, 0.02f };
    /// <summary>Lights tab: filmic tone curve, sun shadows, scene lights in the realistic view.</summary>
    public bool Filmic { get; set; }
    public bool SunShadows { get; set; } = true;
    public bool ViewportLights { get; set; } = true;
    public float PanelWidth { get; set; }
    public int ApiPort { get; set; } = 7820;
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>Folders pinned in every open / save / import window.</summary>
    public List<string> Bookmarks { get; set; } = new();
    /// <summary>Folders of your own models shown in the Asset Browser.</summary>
    public List<string> AssetFolders { get; set; } = new();
    /// <summary>The Asset Browser docked along the bottom of the views (instead of a floating window), and its height.</summary>
    public bool AssetsDocked { get; set; }
    public float AssetsDockHeight { get; set; } = 300;
    /// <summary>Panels dragged out of the main window become windows of their own (other monitors). Read at start.</summary>
    public bool PanelsOutside { get; set; } = true;
    /// <summary>MAP mode, realistic perspective views: sky of the hour behind the map, distance haze.</summary>
    public bool MapSky { get; set; } = true;
    /// <summary>MAP mode: the whole map read as light records and drawn along the game's level-of-detail tree.</summary>
    public bool MapLodTree { get; set; } = true;
    public bool MapFog { get; set; } = true;
    /// <summary>MAP: the game's water (sea, lakes, rivers) drawn.</summary>
    public bool MapWater { get; set; } = true;
    /// <summary>Distance (m) where the haze hides everything.</summary>
    public float MapFogDistance { get; set; } = 3000;
    /// <summary>MAP mode: only what is within this distance of the camera is drawn and picked (big custom maps).</summary>
    public bool MapViewRadiusOn { get; set; }
    public float MapViewRadius { get; set; } = 400;
    /// <summary>Game models put in other families by hand (right-click > Families): "name.ext" -> family bits.</summary>
    public Dictionary<string, long> FamilyOverrides { get; set; } = new();
    /// <summary>Last folder of each kind of window (by its title).</summary>
    public Dictionary<string, string> LastFolders { get; set; } = new();
    /// <summary>GTA V install folder (for the game files browser).</summary>
    public string GtaFolder { get; set; }
    /// <summary>Game archive key found the first time (saves the slow search in the exe).</summary>
    public string GtaKey { get; set; }
    /// <summary>FiveM server "resources" folder (Send to server).</summary>
    public string ServerResourcesFolder { get; set; }
    /// <summary>Optional txAdmin / server console URL used to restart the resource.</summary>
    public string ServerRestartUrl { get; set; }
    /// <summary>Shared secret of the molomax_bridge resource (only this app may restart resources).</summary>
    public string ServerKey { get; set; }

    /// <summary>Settings / autosave folder (%APPDATA%\MoloMax); the self test points it to its own folder.</summary>
    public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Polymorph");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Encrypts secrets for the current Windows user (DPAPI, crypt32) so the API key is not stored in clear text.</summary>
public static class SecretStore
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    static byte[] Run(byte[] data, bool protect)
    {
        var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Polymorph", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) return null;
            var r = new byte[output.Size];
            System.Runtime.InteropServices.Marshal.Copy(output.Data, r, 0, output.Size);
            LocalFree(output.Data);
            return r;
        }
        finally { h.Free(); }
    }

    public static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var r = Run(System.Text.Encoding.UTF8.GetBytes(s), true);
        return r == null ? null : Convert.ToBase64String(r);
    }

    public static string Unprotect(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try { var r = Run(Convert.FromBase64String(b64), false); return r == null ? null : System.Text.Encoding.UTF8.GetString(r); }
        catch { return null; }
    }
}
