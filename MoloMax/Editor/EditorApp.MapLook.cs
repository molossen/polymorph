using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode look: the sky of the hour behind the map (sun disc and glow), a distance haze in the sky's horizon colour
/// (warmer towards the sun) and the game's tint palettes on the buildings.
/// </summary>
public sealed partial class EditorApp
{
    SkyRenderer sky;

    bool MapSkyOn(Viewport vp) => MapMode && Settings.MapSky && !vp.Camera.Ortho && vp.Shading == ShadingMode.Realistic && !wireOnly(vp) && !InteriorLight;

    void DrawMapSky(Matrix4x4 viewProj)
    {
        sky ??= new SkyRenderer(gpu.Device);
        var (z, h, sun, disc) = GtaLighting.SkyOf(Hour);
        sky.Draw(viewProj, z, h, sun, disc, h * 0.55f + new Vector3(0.02f));
    }

    void SetMapFog(Camera cam)
    {
        var (_, h, sun, disc) = GtaLighting.SkyOf(Hour);
        float full = MathF.Min(MathF.Max(Settings.MapFogDistance, 300), cam.Far * 0.95f);
        // warm light towards the sun when it is low (sunrise / sunset), nothing at night
        float low = 1 - MathF.Min(MathF.Abs(sun.Z) * 3, 1);
        renderer.SetFog(h, 1f, full * 0.25f, full, new Vector3(0.35f, 0.2f, 0.08f) * low * disc, sun);
    }

    /// <summary>Look of the map (MAP panel, World section).</summary>
    void MapLookControls()
    {
        bool s = Settings.MapSky, f = Settings.MapFog, t = renderer.TintPalettes;
        if (ImGui.Checkbox("Sky##maplook", ref s)) { Settings.MapSky = s; Settings.Save(); }
        Theme.Tip("The sky of the hour behind the map (Sun slider in the toolbar), with the sun.");
        ImGui.SameLine();
        if (ImGui.Checkbox("Haze##maplook", ref f)) { Settings.MapFog = f; Settings.Save(); }
        Theme.Tip("Far away the map fades into the sky's colour, like in the game.");
        ImGui.SameLine();
        if (ImGui.Checkbox("Tints##maplook", ref t)) renderer.TintPalettes = t;
        Theme.Tip("Buildings with a tint shader take their real colours from the game's palette.");
        if (Settings.MapFog)
        {
            float d = Settings.MapFogDistance;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##fogdist", ref d, 500, 8000, "haze hides all at %.0f m")) { Settings.MapFogDistance = d; }
            if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
        }
    }
}
