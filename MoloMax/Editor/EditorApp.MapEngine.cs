using System.Numerics;
#if MAP_ENGINE
using Polymorph.MapEngine;
#endif

namespace MoloMax.Editor;

/// <summary>
/// MAP mode: the game's map (everything not added to the project) read and drawn by the map engine library, straight
/// from the game's files to the graphics card. Polymorph's own objects keep their own path.
/// </summary>
public sealed partial class EditorApp
{
#if MAP_ENGINE
    MapEngineHost mapEngine; bool mapEngineFailed; string mapEngineFolder; MapEngineFrame mapEngineFrame; bool mapEngineFrameSet;
    internal string MapEngineError;
    /// <summary>The map engine draws the game's map this frame.</summary>
    internal bool MapEngineAvailable => !mapEngineFailed && gpu.FeatureLevel11;
    internal bool MapEngineOn => MapMode && Settings.MapEngine && !mapEngineFailed && gpu.FeatureLevel11 && MapNrLookOn && Settings.MapHdr && !PlayOn; // Play walks on the models of the old path
    internal bool MapEngineReady => mapEngine is { HasMap: true };
    internal string MapEngineStatus => mapEngine == null ? "not started" : mapEngine.Error ?? mapEngine.Status;
    internal long MapEngineGpuBytes => mapEngine?.GpuBytes ?? 0;

    MapEngineFrame EngineFrame(Viewport vp, Matrix4x4 viewProj)
    {
        var cam = vp.Camera;
        return new MapEngineFrame
        {
            Eye = cam.Eye, Yaw = cam.Yaw - MathF.PI, Pitch = -cam.Pitch, FieldOfView = cam.Fov, NearPlane = MathF.Max(cam.Near, 0.05f), AspectRatio = vp.Aspect,
            ViewProjection = viewProj, Hour = Hour, FogDistance = mapLight.FogDistance, ViewDistance = Settings.MapViewDistance,
            ShadowDistance = MathF.Max(50, Settings.MapShadowDistance), Hdr = Settings.MapHdr, PhysicalSky = Settings.MapPhysicalSky,
            Shadows = Settings.SunShadows, SurfaceDetail = renderer.Parallax, Weather = Settings.MapWeather, Anisotropy = Math.Max(1, Settings.Anisotropy),
            ShadowSize = MoloMax.Rendering.SceneRenderer.ShadowSize,
        };
    }

    /// <summary>Streaming and sun shadows of the map engine for this frame (before the views are drawn: it binds its own
    /// shadow images, the window's targets are bound again after).</summary>
    void MapEngineUpdate()
    {
        mapEngineFrameSet = false;
        WorldDrawOff = MapEngineOn;
        if (!MapEngineOn)
        {
            // off for 20 s (MODEL mode, Play, no HDR): its models and textures leave the graphics card; the game stays open
            if (mapEngine != null && engineHolds && ImGuiNET.ImGui.GetTime() - engineLastOn > 20)
            {
                try { mapEngine.Update(mapEngineFrame, false); } catch { }
                if (++engineIdleFrames > 120) { engineHolds = false; engineIdleFrames = 0; }
            }
            engineWasOn = false;
            return;
        }
        if (!engineWasOn)
        {
            // the map engine takes over (MAP mode entered, Play left, HDR back on): the editable copies Polymorph's own map
            // path loaded around the camera by itself go; the maps you opened, changed or work in stay
            engineWasOn = true;
            try
            {
                var ch = ChangedMapDocs();
                var gone = mapDocs.Values.Where(d => d.FromGame && !d.Modified && !ch.Contains(d.Name) && d.Name != mapActive && !mapPinned.Contains(d.Name)).ToList();
                if (gone.Count > 0) UnloadMapDocs(gone);
            }
            catch { }
        }
        engineLastOn = ImGuiNET.ImGui.GetTime(); engineHolds = true; engineIdleFrames = 0;
        try
        {
            string folder = Settings.GtaFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            if (mapEngine == null || mapEngineFolder != folder)
            {
                mapEngine?.Dispose();
                mapEngine = new MapEngineHost(gpu.Device.NativePointer, gpu.Context.NativePointer);
                mapEngineFolder = folder;
                bool enhanced = File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"));
                mapEngine.LoadGame(folder, enhanced);
            }
            // the maps opened for editing are drawn by Polymorph itself: the engine leaves their objects out
            if (mapDocs.Count != engineHiddenCount || mapEngine.HasMap != engineHiddenReady || (world != null) != engineHiddenWorld)
            {
                engineHiddenCount = mapDocs.Count; engineHiddenReady = mapEngine.HasMap; engineHiddenWorld = world != null;
                var hidden = mapDocs.Keys.Select(k => MoloMax.Core.Gta.Map.MapDocument.Hash(k)).ToHashSet();
                // North Yankton (the prologue) is not part of the free-roam map
                if (world != null) foreach (var m in world.Ymaps) if (MoloMax.Core.Gta.Map.ScriptedMaps.NeverInFreeRoam(m.Name)) hidden.Add(MoloMax.Core.Gta.Map.MapDocument.Hash(m.Name));
                mapEngine.SetHiddenMaps(hidden);
            }
            var vp = Viewports[ActiveViewport];
            if (wireOnlyView(vp)) return;
            mapEngineFrame = EngineFrame(vp, vp.Camera.ViewProj(vp.Aspect));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            mapEngine.Update(mapEngineFrame);
            Slow("map engine: streaming + shadows", t0, 40);
            mapEngineFrameSet = true;
        }
        catch (Exception e) { mapEngineFailed = true; MapEngineError = e.ToString(); SetStatus("Map engine off: " + e.Message); WorldDrawOff = false; }
    }

    int engineHiddenCount = -1; bool engineHiddenReady;
    bool engineHiddenWorld;
    double engineLastOn; bool engineHolds; int engineIdleFrames; bool engineWasOn;
    /// <summary>Lets the map engine go (its models and textures leave the graphics card); it starts again when needed.</summary>
    internal void MapEngineClose() { try { mapEngine?.Dispose(); } catch { } mapEngine = null; mapEngineFolder = null; engineHiddenCount = -1; mapEngineFrameSet = false; }
    static bool wireOnlyView(Viewport vp) => vp.WireframeOnly;

    /// <summary>A click on a model drawn by the map engine: its map opens for editing and the model is selected.</summary>
    bool MapEngineClick(Viewport hv, Vector2 mouse)
    {
        if (!MapEngineOn || mapEngine == null || world == null) return false;
        try
        {
            int x = (int)(mouse.X - hv.X), y = (int)(mouse.Y - hv.Y);
            if (x < 0 || y < 0 || x >= hv.W || y >= hv.H) return false;
            var hit = mapEngine.Pick(hv.Camera.ViewProj(hv.Aspect), (int)hv.W, (int)hv.H, x, y);
            gpu.BindBackBuffer();
            if (hit is not { } h || h.Index < 0) return false;
            var ym = world.Ymaps.FirstOrDefault(m => MoloMax.Core.Gta.Map.MapDocument.Hash(m.Name) == h.Map);
            if (ym == null) return false;
            MapEngineLastPick = $"{ym.Name} #{h.Index} model {h.Archetype:X8} at {h.Point}";
            if (mapDocs.TryGetValue(ym.Name, out var doc)) { SelectMapEntity(doc, h.Index); return true; }
            pendingWorldSelect = (ym.Name, h.Index);
            if (!mapLoadQueue.Contains(ym)) mapLoadQueue.Enqueue(ym);
            SetStatus($"Opening {ym.Name} for editing...");
            return true;
        }
        catch (Exception e) { MapEngineError = e.ToString(); return false; }
    }
    internal string MapEngineLastPick;

    /// <summary>Draws the game's map into the bound image; <paramref name="afterSolid"/> runs between its solid and its
    /// see-through models (lamps, sky, water of the view).</summary>
    bool MapEngineDraw(Viewport vp, Matrix4x4 viewProj, Action afterSolid)
    {
        if (!MapEngineOn || mapEngine == null || !mapEngineFrameSet) return false;
        try
        {
            var f = mapEngineFrame; f.ViewProjection = viewProj; f.Eye = vp.Camera.Eye;
            mapEngine.Draw(f, afterSolid);
            return true;
        }
        catch (Exception e) { mapEngineFailed = true; MapEngineError = e.ToString(); SetStatus("Map engine off: " + e.Message); WorldDrawOff = false; return false; }
    }
#else
    internal string MapEngineError;
    internal bool MapEngineOn => false;
    internal bool MapEngineReady => false;
    internal string MapEngineStatus => "not in this build";
    internal long MapEngineGpuBytes => 0;
    void MapEngineUpdate() { WorldDrawOff = false; }
    internal void MapEngineClose() { }
    internal bool MapEngineAvailable => false;
    bool MapEngineDraw(Viewport vp, Matrix4x4 viewProj, Action afterSolid) => false;
    bool MapEngineClick(Viewport hv, Vector2 mouse) => false;
    internal string MapEngineLastPick;
#endif
}
