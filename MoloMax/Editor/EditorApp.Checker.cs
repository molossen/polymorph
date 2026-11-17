using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Check;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>GTA Check window: Molo rules with one-click fixes (each fix is undoable).</summary>
public sealed partial class EditorApp
{
    List<CheckIssue> checkIssues = new();
    bool checkOpen;

    void DrawChecker()
    {
        if (!checkOpen) return;
        ImGui.SetNextWindowSize(new Vector2(640, 440), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f - 320, menuH + toolbarH + 60), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GTA Check###gtacheck", ref checkOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        int err = checkIssues.Count(i => i.Level == CheckLevel.Error), warn = checkIssues.Count(i => i.Level == CheckLevel.Warning);
        int fixable = checkIssues.Count(i => i.Fix != null);
        Ui.TextColored(err > 0 ? new Vector4(1, 0.4f, 0.35f, 1) : warn > 0 ? Theme.Gold : new Vector4(0.45f, 0.9f, 0.5f, 1),
            err + warn == 0 ? "Ready for GTA" : $"{err} error(s), {warn} warning(s)");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 230);
        if (ImGui.Button("Check again", new Vector2(100, 0))) RunGtaChecks();
        ImGui.SameLine();
        if (fixable == 0) ImGui.BeginDisabled();
        if (ImGui.Button($"Fix all ({fixable})", new Vector2(120, 0)))
        {
            List<string> done = null;
            Do("GTA fix all", () => { done = GtaChecker.FixAll(Scene); foreach (var o in Scene.Objects) o.Touch(); });
            RunGtaChecks();
            SetStatus($"{done?.Count ?? 0} fix(es) applied.");
        }
        if (fixable == 0) ImGui.EndDisabled();
        Ui.TextDisabled("Rules: shell in limbo, collision on the shell, DDS 1024 + mipmaps, shared pivot, vertex colours green / white / red.");
        ImGui.Separator();
        ImGui.BeginChild("##issues");
        foreach (var grp in checkIssues.GroupBy(i => i.Group ?? "General"))
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Once);
            if (!Ui.Header($"{grp.Key}  ({grp.Count()})")) continue;
            int n = 0;
            foreach (var i in grp)
            {
                ImGui.PushID(grp.Key + n++);
                var col = i.Level switch { CheckLevel.Error => new Vector4(1, 0.35f, 0.3f, 1), CheckLevel.Warning => Theme.Gold, _ => new Vector4(0.55f, 0.75f, 1f, 1) };
                var p = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddCircleFilled(p + new Vector2(6, ImGui.GetTextLineHeight() * 0.55f), 4, ImGui.ColorConvertFloat4ToU32(col));
                ImGui.Dummy(new Vector2(14, 0)); ImGui.SameLine();
                float bw = i.Fix != null ? 170 : 0;
                float rowRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw - 8);
                ImGui.TextUnformatted(i.Message);
                ImGui.PopTextWrapPos();
                if (i.ObjectId >= 0 && ImGui.IsItemClicked()) { Selected.Clear(); Selected.Add(i.ObjectId); ActiveId = i.ObjectId; SelectionChanged(); FrameSelection(); }
                if (i.ObjectId >= 0 && ImGui.IsItemHovered()) ImGui.SetTooltip("Click to select and frame it");
                if (i.Fix != null)
                {
                    ImGui.SameLine(rowRight - bw);
                    if (ImGui.Button(i.FixLabel, new Vector2(bw, 0)))
                    {
                        string r = null; var issue = i;
                        Do(i.FixLabel, () => { r = issue.Fix(); foreach (var o in Scene.Objects) o.Touch(); });
                        SetStatus(r);
                        RunGtaChecks();
                        ImGui.PopID();
                        break;
                    }
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
