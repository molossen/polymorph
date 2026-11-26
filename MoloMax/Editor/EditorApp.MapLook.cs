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
    WaterRenderer waterR;
    MoloMax.Core.Gta.Water.WaterMap waterMap;
    System.Threading.Tasks.Task<MoloMax.Core.Gta.Water.WaterMap> waterTask;
    /// <summary>Water surfaces read from the game (for the tests).</summary>
    internal int WaterQuads => waterMap?.Quads.Count ?? 0;

    /// <summary>The game's water (water.xml) under the map: read once in the background, then drawn blended over the opaque map.</summary>
    void DrawMapWater(Viewport vp, Matrix4x4 viewProj)
    {
        if (!Settings.MapWater || game == null) return;
        if (waterMap == null)
        {
            var g = game;
            waterTask ??= System.Threading.Tasks.Task.Run(() => MoloMax.Core.Gta.Water.WaterMap.Load(g.ReadPath));
            if (!waterTask.IsCompleted) return;
            waterMap = waterTask.IsFaulted ? new MoloMax.Core.Gta.Water.WaterMap() : waterTask.Result;
            waterR ??= new WaterRenderer(gpu.Device);
            waterR.SetSurfaces(gpu.Device, waterMap.Triangles());
        }
        if (waterR == null || !waterR.HasSurfaces) return;
        var (z, h, sun, disc) = GtaLighting.SkyOf(Hour);
        // body colour: the sea's blue-green, lit by the sky (dark at night)
        float light = MathF.Max(0.08f, MathF.Min(1, (z.X + z.Y + z.Z) / 1.4f));
        var deep = new Vector3(0.05f, 0.16f, 0.19f) * light;
        var (fc, fr) = renderer.Fog;
        var v = new WaterRenderer.Vars
        {
            ViewProj = viewProj, Eye = new Vector4(vp.Camera.Eye, (float)(ImGui.GetTime() % 10000)),
            SunDir = new Vector4(sun, disc * MathF.Max(0, MathF.Min(1, sun.Z * 4))), Zenith = new Vector4(z, 1), Horizon = new Vector4(h, 1),
            Deep = new Vector4(deep, 0.82f), FogColour = fc, FogRange = fr,
        };
        waterR.Draw(v);
        renderer.RebindScene();
    }


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
        renderer.SetFog(h, 0.9f, full * 0.35f, full, new Vector3(0.35f, 0.2f, 0.08f) * low * disc, sun);
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
        ImGui.SameLine();
        bool w = Settings.MapWater;
        if (ImGui.Checkbox("Water##maplook", ref w)) { Settings.MapWater = w; Settings.Save(); }
        Theme.Tip("The game's sea, lakes and rivers (water.xml), with small waves and the sky's reflection.");
        if (Settings.MapFog)
        {
            float d = Settings.MapFogDistance;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##fogdist", ref d, 500, 8000, "haze hides all at %.0f m")) { Settings.MapFogDistance = d; }
            if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
        }
    }
}
