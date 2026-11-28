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

    /// <summary>Short film of what is new in 2.1: the map engine over Legion Square, a flight, a click on a model, the
    /// sea, the night. Run with the filter "TUTO 31" (frames in the test folder, video/31_new_210).</summary>
    IEnumerable<object> TutNew210()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        bool exp0 = ShowExplorer, stats0 = ShowStats, panel0 = ShowPanel;
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = MoloMax.Rendering.ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (mapDocs.Count > 0) { mapPinned.Clear(); UnloadMapDocs(mapDocs.Values.ToList()); yield return Frames(5); }
        var cam = vp.Camera; float hour0 = Hour; Hour = 13; Settings.MapWeather = "Clear";
        ShowExplorer = false; ShowPanel = false; ShowStats = false; tutHideCursor = true;
        cam.Yaw = 2.2f + MathF.PI; cam.Pitch = 0.5f; cam.Distance = 160; cam.Target = new Vector3(188, -927, 30.7f);
        yield return Until(() => !MapEngineOn || MapEngineReady, 600);
        { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < 12) yield return Frames(1); }
        foreach (var x in StartFilm("31_new_210", 5)) yield return x;
        ShowPanel = false; tutHideCursor = true;
        // 1. the city, sharp at once
        tutStep = 1; tutCaption = "Polymorph 2.1  -  a new map engine"; tutSub = "The whole city, sharp from the first second";
        for (int i = 0; i < 300; i++) { cam.Orbit(0.25f, 0); yield return Frames(1); }
        // 2. a flight
        tutStep = 2; tutCaption = "Smooth while you fly"; tutSub = "60 frames a second over Legion Square";
        cam.Distance = 40; cam.Pitch = 0.25f;
        var from = new Vector3(60, -1100, 60); var to = new Vector3(330, -700, 75);
        for (int i = 0; i < 420; i++) { float k = Ease(i / 419f); cam.Target = Vector3.Lerp(from, to, k); cam.Orbit(0.08f, 0); yield return Frames(1); }
        // 3. click a model
        cam.Yaw = 2.2f + MathF.PI; cam.Pitch = 0.45f; cam.Distance = 60; cam.Target = new Vector3(188, -927, 30.7f);
        tutStep = 3; tutCaption = "Click any model of the map"; tutSub = "Its map opens for editing, the model is selected";
        yield return Frames(90);
        MapEngineClick(vp, new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2));
        { var t = DateTime.Now; while (Selected.Count == 0 && (DateTime.Now - t).TotalSeconds < 40) yield return Frames(1); }
        for (int i = 0; i < 180; i++) { cam.Orbit(0.15f, 0); yield return Frames(1); }
        Selected.Clear(); SelectionChanged();
        // 4. the sea
        tutStep = 4; tutCaption = "The sea, the sky, the light"; tutSub = "Waves, foam, clouds and sun shadows as before";
        cam.Target = new Vector3(-1792, -1572, 6); cam.Distance = 30; cam.Pitch = 0.12f; cam.Yaw = 0.6f;
        { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < 5) yield return Frames(1); }
        for (int i = 0; i < 300; i++) { cam.Orbit(0.1f, 0); yield return Frames(1); }
        // 5. the night
        tutStep = 5; tutCaption = "Day and night"; tutSub = "Free download: molossen.github.io/polymorph";
        cam.Yaw = 2.2f + MathF.PI; cam.Pitch = 0.35f; cam.Distance = 90; cam.Target = new Vector3(188, -927, 30.7f);
        { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < 5) yield return Frames(1); }
        for (int i = 0; i < 240; i++) { Hour = 17 + i / 240f * 6; cam.Orbit(0.2f, 0); yield return Frames(1); }
        for (int i = 0; i < 120; i++) { cam.Orbit(0.2f, 0); yield return Frames(1); }
        foreach (var x in EndFilm()) yield return x;
        Hour = hour0; ShowExplorer = exp0; ShowStats = stats0; ShowPanel = panel0; tutHideCursor = false; Maximized = false;
        SetMapMode(false);
    }

    /// <summary>Opens the stream folder named by POLYMORPH_STREAM_DIR in MAP mode and says which textures do not show
    /// (not found, failed, let go), with the map engine on then off.</summary>
    IEnumerable<object> StreamTexDiag(string gtaFolder)
    {
        var dir = Environment.GetEnvironmentVariable("POLYMORPH_STREAM_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Expect(false, "POLYMORPH_STREAM_DIR names a folder"); yield break; }
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var vp = Viewports[ActiveViewport]; Maximized = true; vp.Camera.Ortho = false; vp.Shading = MoloMax.Rendering.ShadingMode.Realistic; Hour = 13;
        OpenStreamFolder(dir);
        IEnumerable<object> Wait(double sec) { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < sec) yield return Frames(1); }
        void Report(string what)
        {
            var objs = MapLive().Where(o => o.Mesh != null && o.Mesh.Faces.Count > 0).ToList();
            var mats = objs.SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)).Distinct().Select(id => Scene.Material(id)).Where(m => m != null).ToList();
            int with = 0, shown = 0; var bad = new List<string>();
            foreach (var m in mats)
            {
                if (string.IsNullOrEmpty(m.DiffuseTexture)) continue;
                with++;
                var path = renderer.Textures.Resolve(m.DiffuseTexture);
                bool ok = renderer.Textures.Get(m.DiffuseTexture) != null;
                if (ok) shown++; else if (bad.Count < 14) bad.Add($"{m.Name}: {m.DiffuseTexture} -> {(path == null ? "NOT FOUND" : Path.GetFileName(path) + (File.Exists(path) ? " (file there, " + new FileInfo(path).Length + " bytes)" : " (no file)"))}");
            }
            Note($"{what}: map engine on {MapEngineOn}; {objs.Count} objects with a mesh ({MapLive().Count()} in all, {placeholderOf.Count} boxes made), {mats.Count} materials, {with} with a diffuse texture, {shown} shown, {with - shown} NOT shown; textures waiting {renderer.Textures.Pending}, let go {renderer.Textures.Trimmed}, on the card {renderer.Textures.GpuBytes / 1048576:N0} MB (budget {MapTextureBudget() / 1048576:N0} MB); errors: {string.Join(" | ", renderer.Textures.Errors.TakeLast(6))}");
            foreach (var b in bad) Note("   " + b);
            var boxes = MapLive().Where(o => o.Mesh != null && placeholderOf.TryGetValue(o.Mesh, out _)).ToList();
            var byState = boxes.GroupBy(o => placeholderOf[o.Mesh].State).Select(g => $"state {g.Key}: {g.Count()}");
            Note($"   still shown as a box: {boxes.Count} ({string.Join(", ", byState)}); biggest: {string.Join(" | ", boxes.OrderByDescending(o => { var (mn, mx) = o.Mesh.Bounds(); return (mx - mn).Length() * MathF.Max(o.Scale.X, 0.01f); }).Take(12).Select(o => { var sl = placeholderOf[o.Mesh]; var (mn, mx) = o.Mesh.Bounds(); return $"{o.Name} {(mx - mn).Length():0} m state {sl.State} {sl.Error}"; }))}");
        }
        foreach (var x in Wait(45)) yield return x;
        Report("map engine on, 45 s"); yield return Shot("stream_engine_on");
        foreach (var x in Wait(40)) yield return x;
        Report("map engine on, 85 s"); yield return Shot("stream_engine_on_later");
        Settings.MapEngine = false;
        foreach (var x in Wait(45)) yield return x;
        Report("map engine off, 45 s"); yield return Shot("stream_engine_off");
        Settings.MapEngine = true;
        // close views: the middle of a few of the folder's maps, from 35 m, to see the props placed there
        var cam = vp.Camera; int shots = 0;
        foreach (var want in (Environment.GetEnvironmentVariable("POLYMORPH_STREAM_CLOSE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pts = Scene.Objects.Where(o => entOf.TryGetValue(o.Id, out var de) && de.Item1.Name.Contains(want, StringComparison.OrdinalIgnoreCase)).Select(o => o.Position).ToList();
            if (pts.Count == 0) { Note($"close view '{want}': no map of that name"); continue; }
            var mid = new Vector3(pts.Select(p => p.X).OrderBy(x => x).ElementAt(pts.Count / 2), pts.Select(p => p.Y).OrderBy(x => x).ElementAt(pts.Count / 2), pts.Select(p => p.Z).OrderBy(x => x).ElementAt(pts.Count / 2));
            cam.Target = mid + new Vector3(0, 0, 1.5f); cam.Distance = 35; cam.Pitch = 0.3f; cam.Yaw = 0.8f + shots * 1.3f;
            foreach (var x in Wait(28)) yield return x;
            Note($"close view '{want}': {pts.Count} objects, camera on {mid.X:0}, {mid.Y:0}, {mid.Z:0}; textures waiting {renderer.Textures.Pending}");
            yield return Shot("close_" + want); shots++;
            cam.Distance = 14; cam.Pitch = 0.18f;
            foreach (var x in Wait(10)) yield return x;
            yield return Shot("closer_" + want);
        }
        CloseStreamFolder(); SetMapMode(false);
    }

    // Decals seen from far away: the same view four times with a hair of camera turn, with and without the pull towards
    // the eye. Photos only: they are compared outside.
    IEnumerable<object> DecalFarDiag(string gtaFolder)
    {
#if MAP_ENGINE
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var vp = Viewports[ActiveViewport]; Maximized = true; vp.Camera.Ortho = false; vp.Shading = MoloMax.Rendering.ShadingMode.Realistic; Hour = 13;
        Settings.MapEngine = true;
        var cam = vp.Camera;
        IEnumerable<object> Wait(double sec) { var t = DateTime.Now; while ((DateTime.Now - t).TotalSeconds < sec) yield return Frames(1); }
        int view = 0;
        foreach (var (dist, pitch) in new[] { (260f, 0.75f), (600f, 0.6f) })
        {
            cam.Target = new Vector3(215, -900, 30); cam.Distance = dist; cam.Pitch = pitch; cam.Yaw = 0.6f;
            foreach (var x in Wait(view == 0 ? 60 : 25)) yield return x;
            foreach (var on in new[] { false, true })
            {
                Polymorph.MapEngine.MapEngineHost.DecalOffset = on;
                for (int i = 0; i < 4; i++)
                {
                    cam.Yaw = 0.6f + i * 0.0004f;
                    yield return Frames(4);
                    yield return Shot($"decal_v{view}_{(on ? "on" : "off")}_{i}");
                }
            }
            view++;
        }
        Polymorph.MapEngine.MapEngineHost.DecalOffset = true;
        Note($"map engine on {MapEngineOn}");
        SetMapMode(false);
#else
        yield break;
#endif
    }
}
