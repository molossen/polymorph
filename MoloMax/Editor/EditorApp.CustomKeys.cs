using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Your own shortcuts: any command of the search list (F3 / X) can get a key, with Ctrl / Shift / Alt.
/// A custom key goes before the keymap's own keys, so it can also replace one; "(nothing)" frees a key.
/// Kept in the settings, and can be exported / imported as a .json file to share a keymap.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>Recording target meaning "free this key": the key then does nothing (a keymap key you don't want).</summary>
    public const string NoCommand = "(free a key)";

    /// <summary>The command whose key is being recorded (next key pressed), or null.</summary>
    string keyCapture;
    string keyFilter = "";

    Dictionary<string, string> CustomKeys => Settings.CustomKeys ??= new();
    List<string> FreeKeys => Settings.FreeKeys ??= new();

    static readonly ImGuiKey[] BindableKeys = Enum.GetValues<ImGuiKey>().Distinct().Where(k =>
    {
        if (k == ImGuiKey.None) return false;
        if ((int)k < (int)ImGuiKey.Tab || (int)k >= (int)ImGuiKey.GamepadStart) return false;
        var n = k.ToString();
        return !(n.EndsWith("Ctrl") || n.EndsWith("Shift") || n.EndsWith("Alt") || n.EndsWith("Super") || n.StartsWith("Reserved") || n.StartsWith("Mod") || n.StartsWith("Named"));
    }).ToArray();

    /// <summary>"Ctrl+Shift+D", "Alt+F5", "Numpad 7"... from the key and the modifiers.</summary>
    public static string ChordText(ImGuiKey k, bool ctrl, bool shift, bool alt)
        => (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + KeyName(k);

    static string KeyName(ImGuiKey k)
    {
        var n = k.ToString();
        if (n.Length == 2 && n[0] == '_' && char.IsDigit(n[1])) return n[1..];
        if (n.StartsWith("Keypad")) return "Numpad " + n[6..] switch { "Add" => "+", "Subtract" => "-", "Multiply" => "*", "Divide" => "/", "Decimal" => ".", var r => r };
        return n switch { "GraveAccent" => "`", "Minus" => "-", "Equal" => "=", "Comma" => ",", "Period" => ".", "Slash" => "/", "Semicolon" => ";", "Apostrophe" => "'", "LeftBracket" => "[", "RightBracket" => "]", "Backslash" => "\\", _ => n };
    }

    static readonly Dictionary<string, ImGuiKey> KeyByName = BindableKeys.GroupBy(KeyName).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static bool ParseChord(string s, out ImGuiKey key, out bool ctrl, out bool shift, out bool alt)
    {
        key = ImGuiKey.None; ctrl = shift = alt = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var rest = s.Trim();
        while (true)
        {
            if (rest.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase)) { ctrl = true; rest = rest[5..]; }
            else if (rest.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase)) { shift = true; rest = rest[6..]; }
            else if (rest.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase)) { alt = true; rest = rest[4..]; }
            else break;
        }
        return KeyByName.TryGetValue(rest, out key);
    }

    /// <summary>The custom key of a command, or null.</summary>
    string CustomKeyFor(string command) => CustomKeys.TryGetValue(command, out var k) && !string.IsNullOrEmpty(k) ? k : null;

    /// <summary>Runs the command bound to the key just pressed. True when a custom key took it.</summary>
    bool CustomKeyPressed(bool ctrl, bool shift, bool alt)
    {
        if (CustomKeys.Count == 0 && FreeKeys.Count == 0) return false;
        bool Hit(string chord) => ParseChord(chord, out var k, out var c, out var s, out var a) && c == ctrl && s == shift && a == alt && ImGui.IsKeyPressed(k, false);
        foreach (var (cmdName, chord) in CustomKeys)
        {
            if (!Hit(chord)) continue;
            var cmd = Commands().FirstOrDefault(x => x.Name == cmdName);
            if (cmd == null) continue;
            if (!(cmd.Enabled?.Invoke() ?? true)) { SetStatus($"{cmd.Name} is not available here (select an object / go to Edit Mode)."); return true; }
            try { cmd.Run(); } catch (Exception e) { SetStatus($"{cmd.Name}: {e.Message}"); }
            return true;
        }
        foreach (var f in FreeKeys) if (Hit(f)) return true;
        return false;
    }

    /// <summary>Sets (or clears with null) the key of a command; a key is used by one command only.</summary>
    public void SetCustomKey(string command, string chord)
    {
        if (command == NoCommand) { if (!string.IsNullOrEmpty(chord) && !FreeKeys.Contains(chord, StringComparer.OrdinalIgnoreCase)) FreeKeys.Add(chord); }
        else if (string.IsNullOrEmpty(chord)) CustomKeys.Remove(command);
        else
        {
            foreach (var other in CustomKeys.Where(p => p.Key != command && string.Equals(p.Value, chord, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
            { CustomKeys.Remove(other); SetStatus($"{chord} was on \"{other}\": moved to \"{command}\"."); }
            CustomKeys[command] = chord;
        }
        Settings.Save();
    }

    /// <summary>Starts recording the next key for a command (Preferences > Keymap, or right-click in the search).</summary>
    void StartKeyCapture(string command) { keyCapture = command; keyFilter = command; prefsOpen = true; prefsSection = 0; }

    /// <summary>While recording: the next key with its modifiers becomes the shortcut. Esc cancels, Backspace clears.</summary>
    void KeyCaptureFrame()
    {
        if (keyCapture == null) return;
        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { keyCapture = null; return; }
        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, false) && !io.KeyCtrl && !io.KeyShift && !io.KeyAlt) { SetCustomKey(keyCapture, null); keyCapture = null; return; }
        foreach (var k in BindableKeys)
        {
            if (!ImGui.IsKeyPressed(k, false)) continue;
            var chord = ChordText(k, io.KeyCtrl, io.KeyShift, io.KeyAlt);
            SetCustomKey(keyCapture, chord);
            SetStatus($"{keyCapture}: {chord}");
            keyCapture = null;
            return;
        }
    }

    public string ExportKeymap(string path)
    {
        var data = new KeymapFile { Base = Settings.Keymap.ToString(), Keys = new(CustomKeys), Free = new(FreeKeys) };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public int ImportKeymap(string path, bool replace = true)
    {
        var data = JsonSerializer.Deserialize<KeymapFile>(File.ReadAllText(path));
        if (data?.Keys == null) return 0;
        if (replace) { CustomKeys.Clear(); FreeKeys.Clear(); }
        int n = 0;
        foreach (var (c, k) in data.Keys)
            if (Commands().Any(x => x.Name == c) && ParseChord(k, out _, out _, out _, out _)) { CustomKeys[c] = k; n++; }
        foreach (var f in data.Free ?? new()) if (ParseChord(f, out _, out _, out _, out _) && !FreeKeys.Contains(f)) { FreeKeys.Add(f); n++; }
        if (Enum.TryParse<Keymap>(data.Base, out var b) && (int)b < KeymapNames.Length && b != Settings.Keymap) SetKeymap(b);
        Settings.Save();
        return n;
    }

    public sealed class KeymapFile
    {
        public string Base { get; set; }
        public Dictionary<string, string> Keys { get; set; }
        public List<string> Free { get; set; }
    }

    /// <summary>Preferences > Keymap: your own shortcuts.</summary>
    void PrefsCustomKeys()
    {
        Ui.TextColored(Theme.Gold, "My shortcuts");
        Ui.TextDisabled("Give any command your own key. Your keys go before the keymap's, so they can also replace one.");
        if (ImGui.Button("Export my keymap..."))
        {
            using var d = new System.Windows.Forms.SaveFileDialog { Filter = "Polymorph keymap (*.json)|*.json", Title = "Export my keymap", FileName = "my_keymap.json" };
            bool ok = ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK; Input.ReleaseAllKeys();
            if (ok) { try { SetStatus($"Keymap saved: {ExportKeymap(d.FileName)}"); } catch (Exception e) { SetStatus("Keymap: " + e.Message); } }
        }
        Theme.Tip("A .json file with your keys, to keep or to give to other people.");
        ImGui.SameLine();
        if (ImGui.Button("Import a keymap..."))
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Filter = "Polymorph keymap (*.json)|*.json", Title = "Import a keymap" };
            bool ok = ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK; Input.ReleaseAllKeys();
            if (ok) { try { SetStatus($"Keymap imported: {ImportKeymap(d.FileName)} shortcuts"); } catch (Exception e) { SetStatus("Keymap: " + e.Message); } }
        }
        int mineCount = CustomKeys.Count + FreeKeys.Count;
        if (mineCount > 0) { ImGui.SameLine(); if (ImGui.Button($"Reset my {mineCount} key(s)")) { CustomKeys.Clear(); FreeKeys.Clear(); Settings.Save(); } }
        if (ImGui.Button("Free a key...")) keyCapture = NoCommand;
        Theme.Tip("Press a key: the keymap's own action on it stops, the key does nothing (until you give it a command).");
        foreach (var f in FreeKeys.ToList())
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{f}  x##free{f}")) { FreeKeys.Remove(f); Settings.Save(); }
            Theme.Tip($"{f} is free (does nothing). Click to give it back to the keymap.");
        }
        if (keyCapture != null)
        {
            ImGui.SameLine();
            Ui.TextColored(Theme.Gold, $"Press the key for \"{keyCapture}\"  (Esc: cancel, Backspace: no key)");
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##keyfilter", "Find a command...", ref keyFilter, 100);
        var words = keyFilter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = Commands().Where(c => words.All(w => (c.Name + " " + c.Category).ToLowerInvariant().Contains(w)))
            .OrderBy(c => CustomKeyFor(c.Name) == null ? 1 : 0).ThenBy(c => c.Category).ThenBy(c => c.Name).ToList();
        if (ImGui.BeginTable("##mykeys", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new Vector2(0, 260)))
        {
            ImGui.TableSetupColumn("Command");
            ImGui.TableSetupColumn("Default", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("My key", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableHeadersRow();
            foreach (var c in list.Take(400)) Row(c.Name, c.Key?.Invoke() ?? "", c.Category);
            ImGui.EndTable();
        }

        void Row(string name, string def, string cat)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            Ui.Text(name); ImGui.SameLine(); Ui.TextDisabled(cat);
            ImGui.TableNextColumn(); Ui.TextDisabled(def);
            ImGui.TableNextColumn();
            var mine = CustomKeyFor(name);
            bool rec = keyCapture == name;
            if (rec) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Gold * new Vector4(1, 1, 1, 0.5f));
            if (ImGui.Button((rec ? "press a key..." : mine ?? "Set key") + "##k" + name, new Vector2(-1, 0))) keyCapture = rec ? null : name;
            if (rec) ImGui.PopStyleColor();
            ImGui.TableNextColumn();
            if (mine != null && ImGui.SmallButton("x##x" + name)) SetCustomKey(name, null);
        }
    }
}
