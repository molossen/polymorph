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
    HdrImage hdrImage;
    CloudRenderer clouds; bool cloudsFailed;
    /// <summary>The MAP view lit like NodeRunner (its light of the hour, sky and haze).</summary>
    bool MapNrLookOn => MapMode && Settings.MapNrLook;
    MoloMax.Core.Gta.Look.MapLighting mapLight = MoloMax.Core.Gta.Look.MapLighting.Neutral;
    WaterRenderer waterR;
    NrWaterRenderer nrWater; bool nrWaterFailed;
    MoloMax.Core.Gta.Water.WaterMap waterMap;
    System.Threading.Tasks.Task<MoloMax.Core.Gta.Water.WaterMap> waterTask;
    /// <summary>Water surfaces read from the game (for the tests).</summary>
    internal int WaterQuads => waterMap?.Quads.Count ?? 0;

    DistantLightsRenderer lightsR;
    System.Threading.Tasks.Task<MoloMax.Core.Gta.Map.DistantLights> lightsTask;
    /// <summary>Distant lights read from the game (for the tests).</summary>
    internal int DistantLightCount => lightsR?.Count ?? 0;

    /// <summary>How much of the night the lights show (0 by day, 1 at night), from the sun's height.</summary>
    static float NightOf(float hour) { var (_, _, sun, _) = GtaLighting.SkyOf(hour); return Math.Clamp((0.12f - sun.Z) * 5f, 0, 1); }

    /// <summary>The city's lights at night, seen from far (the game's distant lights), added over the map.</summary>
    void DrawMapLights(Viewport vp, Matrix4x4 viewProj)
    {
        if (!Settings.MapNightLights || world == null) return;
        float night = MapNrLookOn ? mapLight.NightLights : NightOf(Hour);
        if (night <= 0.01f) return;
        if (lightsR == null)
        {
            var w = world;
            lightsTask ??= System.Threading.Tasks.Task.Run(() => MoloMax.Core.Gta.Map.DistantLights.Load(w));
            if (!lightsTask.IsCompleted) return;
            lightsR = new DistantLightsRenderer(gpu.Device);
            if (!lightsTask.IsFaulted) lightsR.SetLights(gpu.Device, lightsTask.Result.Positions, lightsTask.Result.Colours);
        }
        var cam = vp.Camera;
        var v = new DistantLightsRenderer.Vars
        {
            ViewProj = viewProj, Eye = new Vector4(cam.Eye, 1.15f / MathF.Max(vp.H, 1)),
            Right = new Vector4(cam.Right, MapNrLookOn && mapLight.IsHdr ? 1 : 0), Up = new Vector4(Vector3.Normalize(Vector3.Cross(cam.Right, cam.Forward)), night),
        };
        lightsR.Draw(v);
        renderer.RebindScene();
    }

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
        if (MapNrLookOn && mapLight.IsHdr && hdrImage != null && gpu.FeatureLevel11)
        {
            // NodeRunner's water: what it mirrors and what shows through it from the scene drawn so far
            try { nrWater ??= new NrWaterRenderer(gpu.Device); } catch { nrWaterFailed = true; }
            if (nrWater != null && !nrWaterFailed)
            {
                if (!nrWater.HasSurfaces) nrWater.SetSurfaces(gpu.Device, waterMap.Triangles());
                var (col, dep) = hdrImage.Snapshot(gpu);
                var ml = mapLight; var c = vp.Camera;
                var nv = new NrWaterRenderer.Vars
                {
                    ViewProjection = viewProj, CameraTime = new Vector4(c.Eye, (float)(ImGui.GetTime() % 3600)),
                    SunDirection = new Vector4(ml.SunDirection, c.Near), SunColor = new Vector4(ml.SunColor, ml.SunDisc), SkyAmbient = new Vector4(ml.SkyAmbient, c.Far),
                    Zenith = new Vector4(ml.Zenith, 0), Horizon = new Vector4(ml.Horizon, 0),
                    Settings = new Vector4(Settings.MapFog ? 1 : 0, MathF.Max(ml.FogDistance, 1), dep != null ? 1 : 0, 0),
                    Viewport = new Vector4(vp.X, vp.Y, vp.W, vp.H), Options = new Vector4(1, 0, 0, 0),
                };
                nrWater.Draw(nv, col, dep);
                gpu.Context.OutputMerger.SetBlendState(renderer.Alpha, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
                renderer.RebindScene();
                return;
            }
        }
        var (z, h, sun, disc) = GtaLighting.SkyOf(Hour);
        if (MapNrLookOn)
        {
            static Vector3 D(Vector3 c, bool hdr) => hdr ? new Vector3(MathF.Pow(MathF.Max(c.X, 0), 1 / 2.2f), MathF.Pow(MathF.Max(c.Y, 0), 1 / 2.2f), MathF.Pow(MathF.Max(c.Z, 0), 1 / 2.2f)) : c;
            (z, h, sun, disc) = (D(mapLight.Zenith, mapLight.IsHdr), D(mapLight.Horizon, mapLight.IsHdr), mapLight.Sun, mapLight.SunDisc);
        }
        // body colour: the sea's blue-green, lit by the sky (dark at night)
        float light = MathF.Max(0.08f, MathF.Min(1, (z.X + z.Y + z.Z) / 1.4f));
        var deep = new Vector3(0.05f, 0.16f, 0.19f) * light;
        var (fc, fr) = renderer.Fog;
        if (MapNrLookOn) { var hz = mapLight.IsHdr ? new Vector3(MathF.Pow(mapLight.Horizon.X, 1 / 2.2f), MathF.Pow(mapLight.Horizon.Y, 1 / 2.2f), MathF.Pow(mapLight.Horizon.Z, 1 / 2.2f)) : mapLight.Horizon; fc = new Vector4(hz, Settings.MapFog ? 1 : 0); fr = new Vector4(mapLight.FogDistance * 0.25f, mapLight.FogDistance, mapLight.IsHdr ? 1 : 0, 0); }
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
        if (MapNrLookOn)
        {
            // NodeRunner's sky: below the horizon the horizon colour darkened
            var ml = mapLight;
            if (ml.PhysicalSky) sky.PrepareTable(ml.Sun.Z, MoloMax.Core.Gta.Look.MapLighting.SkyBalance);
            // NodeRunner's clouds in volume (HDR image), drawn into their own image first
            SharpDX.Direct3D11.ShaderResourceView cloudImg = null;
            if (ml.IsHdr && Settings.MapClouds && gpu.FeatureLevel11 && !cloudsFailed && drawViewport is { } dv)
            {
                try
                {
                    clouds ??= new CloudRenderer(gpu.Device);
                    var weather = MoloMax.Core.Gta.Look.CloudWeather.For(Enum.TryParse<MoloMax.Core.Gta.Look.Weather>(Settings.MapWeather, out var wt) ? wt : MoloMax.Core.Gta.Look.Weather.Clear);
                    cloudImg = clouds.Render(viewProj, dv.Camera.Eye, ml, weather, (int)dv.W, (int)dv.H, 1);
                }
                catch { cloudsFailed = true; }
                gpu.Context.Rasterizer.SetViewport(dv.X, dv.Y, dv.W, dv.H);
                gpu.Context.Rasterizer.SetScissorRectangle((int)dv.X, (int)dv.Y, (int)(dv.X + dv.W), (int)(dv.Y + dv.H));
                hdrImage?.Rebind(gpu);
                gpu.Context.OutputMerger.SetDepthStencilState(renderer.DepthOff);
                gpu.Context.Rasterizer.State = renderer.CullNone;
            }
            sky.Draw(viewProj, ml.Zenith, ml.Horizon, ml.SunDirection, ml.SunDisc, ml.IsHdr, ml.PhysicalSky, ml.NightLights, ml.SunTint, ml.Sun, 0, cloudImg);
            return;
        }
        var (z, h, sun, disc) = GtaLighting.SkyOf(Hour);
        sky.Draw(viewProj, z, h, sun, disc, h * 0.55f + new Vector3(0.02f));
    }

    void SetMapFog(Camera cam)
    {
        if (MapNrLookOn)
        {
            float fd = MathF.Min(MathF.Max(MathF.Max(Settings.MapFogDistance, 300), cam.Eye.Z * 2.5f), cam.Far * 0.95f);
            mapLight = mapLight with { FogDistance = fd };
            renderer.SetMapLook(mapLight, cam.Eye, Settings.MapFog);
            renderer.FogOff(); // the look's own haze
            return;
        }
        var (_, h, sun, disc) = GtaLighting.SkyOf(Hour);
        // seen from high above the haze starts further (the overview of the whole map stays clear)
        float full = MathF.Min(MathF.Max(MathF.Max(Settings.MapFogDistance, 300), cam.Eye.Z * 2.5f), cam.Far * 0.95f);
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
        bool nr = Settings.MapNrLook;
        if (ImGui.Checkbox("NodeRunner light##maplook", ref nr)) { Settings.MapNrLook = nr; Settings.Save(); }
        Theme.Tip("Light the map like NodeRunner: sun or moon of the hour, light from the sky and the ground, sky on shiny surfaces.");
        ImGui.SameLine();
        bool w = Settings.MapWater;
        if (ImGui.Checkbox("Water##maplook", ref w)) { Settings.MapWater = w; Settings.Save(); }
        Theme.Tip("The game's sea, lakes and rivers (water.xml), with small waves and the sky's reflection.");
        ImGui.SameLine();
        bool nl = Settings.MapNightLights;
        if (ImGui.Checkbox("Night lights##maplook", ref nl)) { Settings.MapNightLights = nl; Settings.Save(); }
        Theme.Tip("At night, the lights of the city and the roads seen from far away (the game's distant lights).");
        bool px = renderer.Parallax;
        if (ImGui.Checkbox("Relief##maplook", ref px)) renderer.Parallax = px;
        Theme.Tip("Parallax (PXM): ground, stones and walls that have a height map look raised up close, like in the game.");
        if (Settings.MapFog)
        {
            float d = Settings.MapFogDistance;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##fogdist", ref d, 500, 8000, "haze hides all at %.0f m")) { Settings.MapFogDistance = d; }
            if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
        }
    }
}
