using System.Numerics;

namespace MoloMax.Editor;

public sealed partial class EditorApp
{
    /// <summary>The flight of the side-by-side video: 10 s still over Legion Square, 8 s forward at 50 m/s, a quarter turn
    /// to the left in 2 s, 6 s forward, 4 s still. Eye and yaw (reference camera convention) at t seconds.</summary>
    static (Vector3 Eye, float Yaw, float Pitch) FlightAt(float t, float speed, bool shore)
    {
        // over Legion Square, or along Vespucci beach (the sea on the right) then inland
        float pitch = shore ? 0f : -0.1f, yaw0 = shore ? -1.0f : 2.356f;
        var p = shore ? new Vector3(-1750, -1000, 25) : new Vector3(330, -1050, 120);
        static Vector3 F(float yaw, float pitch) => new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        p += F(yaw0, pitch) * speed * Math.Clamp(t - 10, 0, 8);
        float yaw = yaw0 + MathF.PI / 2 * Math.Clamp((t - 18) / 2, 0, 1);
        p += F(yaw0 + MathF.PI / 2, pitch) * speed * Math.Clamp(t - 20, 0, 6);
        return (p, yaw, pitch);
    }

    /// <summary>FLIGHT: the same flight as the reference video, filmed from outside (a file says when it starts); frame times of each part.</summary>
    IEnumerable<object> FlightTest(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        yield return Until(() => !Settings.MapLodTree || worldEnts != null || worldEntsTask is { IsCompleted: true }, 300);
        var cam = Viewports[ActiveViewport].Camera; Maximized = true; cam.Ortho = false;
        float fov0 = cam.Fov; cam.Fov = MathF.PI / 3;
        Hour = 13; Settings.MapWeather = "Clear";
        float speed = float.TryParse(Environment.GetEnvironmentVariable("POLYMORPH_FLIGHT_SPEED"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 50;
        bool shore = Environment.GetEnvironmentVariable("POLYMORPH_FLIGHT_ROUTE") == "shore";
        Note(shore ? "route: along Vespucci beach" : "route: over Legion Square");
        void Place(float t) { var (eye, yaw, pitch) = FlightAt(t, speed, shore); cam.Yaw = yaw + MathF.PI; cam.Pitch = -pitch; cam.Distance = 20; cam.Target = eye + cam.Forward * 20; }
        // what is switched off for this run (finding what costs): POLYMORPH_FLIGHT_VARIANT=noshadow,nohdr,...
        var off = (Environment.GetEnvironmentVariable("POLYMORPH_FLIGHT_VARIANT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var keep = (Settings.SunShadows, Settings.MapHdr, Settings.MapClouds, Settings.MapOcclusion, Settings.MapBloom, Settings.MapWater, Settings.MapSunRays, MapStream, Settings.MapViewDistance);
        if (off.Contains("noshadow")) Settings.SunShadows = false;
        if (off.Contains("nohdr")) Settings.MapHdr = false;
        if (off.Contains("noclouds")) Settings.MapClouds = false;
        if (off.Contains("noao")) Settings.MapOcclusion = false;
        if (off.Contains("nobloom")) Settings.MapBloom = false;
        if (off.Contains("nowater")) Settings.MapWater = false;
        if (off.Contains("norays")) Settings.MapSunRays = false;
        if (off.Contains("nostream")) MapStream = false;
        if (off.Contains("view1")) Settings.MapViewDistance = 1;
        bool engine0 = Settings.MapEngine; Settings.MapEngine = !off.Contains("noengine");
        Note("variant: " + (off.Length == 0 ? "everything on" : string.Join(", ", off)));
        // far away first, so the flight starts on a place not read yet (like a fresh start)
        Place(0); yield return Frames(3); SlowLog.Clear();
        var marker = Environment.GetEnvironmentVariable("POLYMORPH_FLIGHT_MARKER");
        if (!string.IsNullOrEmpty(marker)) File.WriteAllText(marker, DateTime.Now.ToString("O"));
        var t0 = DateTime.Now;
        var parts = new[] { ("still at the start", 0f, 10f), ("forward", 10f, 18f), ("turn", 18f, 20f), ("forward again", 20f, 26f), ("still at the end", 26f, 30f) };
        foreach (var (name, a, b) in parts)
        {
            int s0 = stDts.Count; int loadMax = 0, texMax = 0; int up0 = renderer.Uploads; double upMs0 = renderer.UploadMs; renderer.SlowestUploadMs = 0; renderer.SlowestUpload = null; long draws0 = renderer.StatDraws;
            while (true)
            {
                float t = (float)(DateTime.Now - t0).TotalSeconds;
                if (t >= b) break;
                Place(t); loadMax = Math.Max(loadMax, archLoading); texMax = Math.Max(texMax, renderer.Textures.Pending);
                yield return Frames(1);
            }
            var d = stDts.Skip(s0).Where(x => x > 0).Select(x => x * 1000).OrderBy(x => x).ToList();
            if (d.Count == 0) continue;
            Note($"{name} ({a:0}-{b:0} s): {d.Count / (b - a):0} frames a second; frame median {d[d.Count / 2]:0.0} ms, 95% under {d[(int)(d.Count * 0.95)]:0.0}, worst {d[^1]:0.0}; frames over 33 ms: {d.Count(x => x > 33)}, over 50 ms: {d.Count(x => x > 50)}, over 100 ms: {d.Count(x => x > 100)}; models loading up to {loadMax}, textures waiting up to {texMax}, drawn {MapVisible:N0}, editable objects {MapLive().Count:N0}; meshes sent to the graphics card {renderer.Uploads - up0} in {renderer.UploadMs - upMs0:0} ms (slowest: {renderer.SlowestUpload}); draws a frame {(renderer.StatDraws - draws0) / Math.Max(1, d.Count):N0}; profile {ProfileText(7)}");
        }
        Note($"end: models waiting {archSlots.Values.Count(s => s.State == 0 && s.Priority < float.MaxValue)}, reading {archLoading}; {LoadTimesText()}");
        foreach (var l in SlowLog) Note("slow: " + l);
        yield return Shot("flight_end");
        Note($"map engine: on {MapEngineOn}, ready {MapEngineReady}, status '{MapEngineStatus}', on the graphics card {MapEngineGpuBytes / 1048576:N0} MB" + (MapEngineError != null ? ", ERROR " + MapEngineError[..Math.Min(600, MapEngineError.Length)] : ""));
        Settings.MapEngine = engine0;
        cam.Fov = fov0;
        (Settings.SunShadows, Settings.MapHdr, Settings.MapClouds, Settings.MapOcclusion, Settings.MapBloom, Settings.MapWater, Settings.MapSunRays, MapStream, Settings.MapViewDistance) = keep;
        SetMapMode(false);
    }

    /// <summary>MAP ENGINE: the game's map drawn by the map engine; a click on one of its models opens it for editing; night; pictures.</summary>
    /// <summary>Megabytes this program holds on the graphics card right now (what Windows counts).</summary>
    long GpuUsedMb()
    {
        try
        {
            using var dx = gpu.Device.QueryInterface<SharpDX.DXGI.Device>(); using var ad = dx.Adapter; using var a3 = ad.QueryInterfaceOrNull<SharpDX.DXGI.Adapter3>();
            if (a3 == null) return -1;
            return a3.QueryVideoMemoryInfo(0, SharpDX.DXGI.MemorySegmentGroup.Local).CurrentUsage / 1048576;
        }
        catch { return -1; }
    }

    /// <summary>Where the memory goes in MAP mode: the same steps, the figures after each (graphics card, process, map
    /// engine, own textures), then the map engine closed to see what it held.</summary>
    IEnumerable<object> MemoryDiag(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; Maximized = true; cam.Ortho = false;
        float hour0 = Hour; Hour = 13;
        void Log(string what) => Note($"{what}: graphics card {GpuUsedMb():N0} MB, process {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576:N0} MB, managed {GC.GetTotalMemory(false) / 1048576:N0} MB, map engine {MapEngineGpuBytes / 1048576:N0} MB (on {MapEngineOn}), own textures {renderer.Textures.GpuBytes / 1048576:N0} MB, own meshes {renderer.MeshCount}, models known {archSlots.Count:N0} (with mesh {archSlots.Values.Count(x => x.Mesh != null):N0}), reads running {archLoading}");
        IEnumerable<object> Wait(double sec) { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < sec) yield return Frames(1); }
        var spots = new[] { ("legion", new Vector3(188, -927, 30.7f)), ("tataviam", new Vector3(2461, -706, 115)), ("del perro", new Vector3(-1800, -1200, 20)), ("shore", new Vector3(-1463, -1406, 5)) };
        cam.Yaw = 2.2f + MathF.PI; cam.Pitch = 0.45f; cam.Distance = 60; cam.Target = spots[0].Item2;
        Log("start");
        yield return Until(() => !MapEngineOn || MapEngineReady, 600);
        foreach (var x in Wait(15)) yield return x;
        Log("map engine, Legion, 15 s");
        for (int round = 0; round < 2; round++)
            foreach (var (name, p) in spots)
            {
                cam.Target = p; foreach (var x in Wait(12)) yield return x;
                Log($"map engine, tour {round + 1}, {name}");
            }
        cam.Target = spots[0].Item2;
        for (int k = 0; k < 3; k++)
        {
            Settings.MapEngine = false; foreach (var x in Wait(25)) yield return x; Log($"own map path {k + 1} (25 s)");
            Settings.MapEngine = true; foreach (var x in Wait(20)) yield return x; Log($"map engine again {k + 1} (20 s)");
        }
        bool sh = Settings.SunShadows;
        for (int k = 0; k < 5; k++) { Settings.SunShadows = false; foreach (var x in Wait(2)) yield return x; Settings.SunShadows = true; foreach (var x in Wait(2)) yield return x; }
        Settings.SunShadows = sh; Log("sun shadows off and on 5 times");
        bool hd = Settings.MapHdr;
        for (int k = 0; k < 3; k++) { Settings.MapHdr = false; foreach (var x in Wait(5)) yield return x; Settings.MapHdr = true; foreach (var x in Wait(5)) yield return x; }
        Settings.MapHdr = hd; Log("HDR off and on 3 times");
        foreach (var x in Wait(40)) yield return x; Log("still, 40 s later");
        Settings.MapEngine = false; MapEngineClose(); foreach (var x in Wait(6)) yield return x; Log("map engine closed");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); foreach (var x in Wait(3)) yield return x; Log("after a full collection");
        foreach (var x in Wait(30)) yield return x; Log("own map path, 30 s later");
        Settings.MapEngine = true; Hour = hour0; SetMapMode(false);
        foreach (var x in Wait(5)) yield return x; Log("back in MODEL mode");
    }

    IEnumerable<object> EngineTest(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; Maximized = true; cam.Ortho = false;
        float hour0 = Hour; Hour = 13; Settings.MapWeather = "Clear";
        cam.Yaw = 2.2f + MathF.PI; cam.Pitch = 0.45f; cam.Distance = 60; cam.Target = new Vector3(188, -927, 30.7f);
        // maps an earlier test opened or changed stay open by design: this test starts with none
        if (mapDocs.Count > 0) { mapPinned.Clear(); UnloadMapDocs(mapDocs.Values.ToList()); yield return Frames(5); }
        Expect(MapEngineOn, $"the map engine is on in this build ({MapEngineStatus})");
        if (!MapEngineOn) { Hour = hour0; SetMapMode(false); yield break; }
        yield return Until(() => MapEngineReady, 600);
        var t0 = DateTime.Now; while ((DateTime.Now - t0).TotalSeconds < 15) yield return Frames(1);
        Note($"textures of Polymorph's own drawing on the graphics card: {renderer.Textures.GpuBytes / 1048576:N0} MB (budget {MapTextureBudget() / 1048576:N0} MB), let go so far {renderer.Textures.Trimmed}; process {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576:N0} MB");
        Expect(MapEngineReady && MapEngineGpuBytes > 50 << 20, $"the game's map is on the graphics card ({MapEngineGpuBytes / 1048576:N0} MB, '{MapEngineStatus}')");
        Expect(MapLive().Count < 50, $"no editable copies loaded around the camera on their own ({MapLive().Count})");
        int s0 = stDts.Count; yield return Frames(90);
        var d = stDts.Skip(s0).Where(x => x > 0).Select(x => x * 1000).OrderBy(x => x).ToList();
        Note($"Legion Square by day: frame median {(d.Count > 0 ? d[d.Count / 2] : 0):0.0} ms");
        yield return Shot("engine_legion_day");
        // click the model in the middle of the view: its map opens, the model is selected
        bool clicked = MapEngineClick(vp, new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2));
        Note($"click: {clicked}, picked {MapEngineLastPick}" + (MapEngineError != null ? " ERROR " + MapEngineError[..Math.Min(500, MapEngineError.Length)] : ""));
        Expect(clicked, "a click on a model of the game's map is taken");
        yield return Until(() => Selected.Count > 0, 600);
        yield return Frames(30);
        var sel = Selected.Count > 0 ? Scene.Find(Selected.First()) : null;
        Expect(sel != null && MapGameModel(sel), $"the clicked model is selected as a model of its map ({sel?.Name}, {MapLive().Count} objects of its map opened)");
        yield return Shot("engine_clicked");
        Hour = 22; yield return Frames(120);
        yield return Shot("engine_legion_night");
        Hour = hour0;
        SetMapMode(false);
    }
}
