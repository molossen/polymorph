using System.Numerics;

namespace MoloMax.Editor;

public sealed partial class EditorApp
{
    /// <summary>MAP image: the sun's rays and the sharpness, on and off at the same place (pictures, frame cost).</summary>
    IEnumerable<object> SunRaysTest(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        yield return Until(() => !Settings.MapLodTree || worldEnts != null || worldEntsTask is { IsCompleted: true }, 300);
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; Maximized = true; cam.Ortho = false;
        bool rays0 = Settings.MapSunRays; float sharp0 = Settings.MapSharpen, hour0 = Hour;
        Hour = 18; Settings.MapWeather = "Clear";
        cam.Distance = 12; cam.Target = new Vector3(188, -927, 45f);
        yield return Frames(10);
        // look towards the sun: the yaw and pitch whose forward is the closest to it
        var sun = Vector3.Normalize(mapLight.SunDirection); float best = -2;
        float yawB = 0, pitchB = 0;
        for (int y = 0; y < 72; y++) for (int p = -6; p <= 6; p++)
        {
            cam.Yaw = y * MathF.PI / 36; cam.Pitch = p * 0.1f;
            float d = Vector3.Dot(Vector3.Normalize(cam.Forward), sun);
            if (d > best) { best = d; yawB = cam.Yaw; pitchB = cam.Pitch; }
        }
        cam.Yaw = yawB + 0.25f; cam.Pitch = pitchB;
        Note($"sun {sun}, camera forward . sun = {best:0.00}");
        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalSeconds < 25) yield return Frames(1);
        double Median(int n0) { var d = stDts.Skip(n0).Where(x => x > 0).OrderBy(x => x).ToList(); return d.Count > 0 ? d[d.Count / 2] * 1000 : 0; }
        Settings.MapSunRays = false; Settings.MapSharpen = 0; yield return Frames(20);
        int s0 = stDts.Count; yield return Frames(90); double off = Median(s0);
        Expect(!hdrImage.RaysDrawn, "off: no rays drawn");
        yield return Shot("rays_off");
        Settings.MapSunRays = true; Settings.MapSharpen = 0.35f; yield return Frames(20);
        int s1 = stDts.Count; yield return Frames(90); double on = Median(s1);
        Expect(hdrImage.RaysDrawn, $"on: rays drawn towards the sun (strength {hdrImage.SunRays.Z:0.00})");
        yield return Shot("rays_on");
        Note($"frame: without {off:0.0} ms, with sun rays and sharpness {on:0.0} ms");
        Expect(on < off + 2.5, $"sun rays and sharpness cost little ({off:0.0} -> {on:0.0} ms)");
        Hour = 13; yield return Frames(30);
        Settings.MapSunRays = false; Settings.MapSharpen = 0; yield return Frames(20); yield return Shot("sharp_off_noon");
        Settings.MapSunRays = true; Settings.MapSharpen = 0.35f; yield return Frames(20); yield return Shot("sharp_on_noon");
        Settings.MapSunRays = rays0; Settings.MapSharpen = sharp0; Hour = hour0;
        SetMapMode(false);
    }
}
