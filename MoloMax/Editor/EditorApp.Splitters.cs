using System.Numerics;
using ImGuiNET;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>Drag the borders of the Scene Explorer and of the right panel to make them wider or narrower (remembered).</summary>
public sealed partial class EditorApp
{
    bool splitLoaded;

    void DrawSplitters()
    {
        if (!splitLoaded)
        {
            splitLoaded = true;
            if (Settings.ExplorerWidth >= 160) explorerW = Settings.ExplorerWidth;
            if (Settings.PanelWidth >= 220) panelW = Settings.PanelWidth;
        }
        float y = menuH + toolbarH, h = gpu.Height - y - statusH;
        if (ShowExplorer && Splitter("##splitL", explorerW, y, h, out float dx)) { explorerW = Math.Clamp(explorerW + dx, 160, gpu.Width * 0.45f); Settings.ExplorerWidth = explorerW; }
        if (ShowPanel && Splitter("##splitR", gpu.Width - panelW, y, h, out float dx2)) { panelW = Math.Clamp(panelW - dx2, 220, gpu.Width * 0.5f); Settings.PanelWidth = panelW; }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && splitDragging) { splitDragging = false; Settings.Save(); }
    }
    bool splitDragging;

    /// <summary>A thin vertical grip on the border x; returns the drag this frame.</summary>
    bool Splitter(string id, float x, float y, float h, out float dx)
    {
        dx = 0;
        const float w = 6;
        ImGui.SetNextWindowPos(new Vector2(x - w / 2, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        // ImGui widens every window to WindowMinSize (32 px): the grip then covered the viewport's [+] menu
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1, 1));
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
        ImGui.InvisibleButton(id + "b", new Vector2(w, h));
        bool hover = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        if (hover || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            var p = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddRectFilled(p + new Vector2(w / 2 - 1, 0), p + new Vector2(w / 2 + 1, h), ImGui.ColorConvertFloat4ToU32(Theme.ToggleOnHover));
        }
        if (active) { dx = ImGui.GetIO().MouseDelta.X; splitDragging = true; }
        ImGui.End();
        ImGui.PopStyleVar(3);
        return active && dx != 0;
    }
}
