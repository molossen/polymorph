using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode, Play: walk on the map as a player (eye height, gravity, steps, walls, jump, swimming), to see the map the way
/// a player will. ZQSD / WASD move, Shift runs, Ctrl walks, Space jumps, hold the right button to look around, Esc leaves.
/// The ground and the walls are the models drawn around the player (leaves, glass and decals let you through).
/// </summary>
public sealed partial class EditorApp
{
    public bool PlayOn { get; private set; }
    Vector3 playPos, playVel; float playYaw, playPitch; bool playGrounded, playSwimming; Vector3 playStart;
    double playStepTime; internal string playStartedBy;
    /// <summary>Self test: keys held (forward / right, -1..1) and a jump, in place of the keyboard.</summary>
    internal Vector2? playTestWish; internal bool playTestJump, playTestSprint;
    internal Vector3 PlayPos => playPos; internal bool PlayGrounded => playGrounded; internal bool PlaySwimming => playSwimming;

    const float PlayGravity = 9.81f, PlayJump = 4.6f, PlayStepUp = 0.45f, PlayStepDown = 0.6f, PlayRadius = 0.32f, PlayEye = 1.62f;
    const float PlayRun = 5.2f, PlaySprint = 7.6f, PlayWalk = 1.7f, PlaySwim = 1.6f, PlaySwimDepth = 1.36f, PlayFallBack = 60f;

    /// <summary>Starts walking at the point of the map under the middle of the view (or where the camera looks).</summary>
    public void StartPlay()
    {
        if (!MapMode || PlayOn) return;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
        var start = cam.Target;
        if (GpuPickMap(vp, new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2), null) is { } h) start = h.Point;
        playStart = start; playStartedBy = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(1).Take(5).Select(f => f.GetMethod()?.Name));
        playPos = start + new Vector3(0, 0, 0.05f); playVel = Vector3.Zero; playGrounded = false; playSwimming = false;
        var f = cam.Forward; playYaw = MathF.Atan2(f.Y, f.X); playPitch = 0;
        PlayOn = true; SculptMode = false; paintMode = false; nav = NavMode.None;
        cam.Ortho = false; cam.Kind = ViewKind.Perspective;
        SetStatus("Play: ZQSD / WASD walk, Shift run, Ctrl slow, Space jump, right button look around, Esc leaves.");
    }

    public void StopPlay()
    {
        if (!PlayOn) return;
        PlayOn = false;
        var cam = Viewports[ActiveViewport].Camera;
        // the view stays where the player looked from
        cam.Distance = 6; cam.Target = cam.Eye + cam.Forward * 6;
        SetStatus("Play stopped.");
    }

    /// <summary>One frame of play: keys, look, physics, camera. Returns true when it used the frame's input.</summary>
    bool PlayFrame(float dt)
    {
        if (!PlayOn) return false;
        if (!MapMode) { PlayOn = false; return false; }
        var io = ImGui.GetIO();
        dt = Math.Clamp(dt, 0f, 0.05f);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { StopPlay(); return true; }
        bool K(ImGuiKey k) => !io.WantTextInput && ImGui.IsKeyDown(k);
        // look: right button held (the cursor stays usable for the panels otherwise)
        if (Input.Down(System.Windows.Forms.MouseButtons.Right))
        {
            var d = Input.MouseDelta;
            playYaw -= d.X * 0.0035f; playPitch = Math.Clamp(playPitch - d.Y * 0.0035f, -1.4f, 1.4f);
        }
        var fwd = new Vector3(MathF.Cos(playYaw), MathF.Sin(playYaw), 0); var side = new Vector3(MathF.Sin(playYaw), -MathF.Cos(playYaw), 0);
        var wish = Vector3.Zero;
        if (K(ImGuiKey.Z) || K(ImGuiKey.W) || K(ImGuiKey.UpArrow)) wish += fwd;
        if (K(ImGuiKey.S) || K(ImGuiKey.DownArrow)) wish -= fwd;
        if (K(ImGuiKey.D) || K(ImGuiKey.RightArrow)) wish += side;
        if (K(ImGuiKey.Q) || K(ImGuiKey.A) || K(ImGuiKey.LeftArrow)) wish -= side;
        if (playTestWish is { } tw) wish = fwd * tw.X + side * tw.Y;
        float speed = playSwimming ? PlaySwim : io.KeyShift || playTestSprint ? PlaySprint : io.KeyCtrl ? PlayWalk : PlayRun;
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * speed : Vector3.Zero;
        // on the ground the speed follows the keys at once; in the air only a little steering
        float grip = playGrounded || playSwimming ? 14f : 1.5f;
        var hv = new Vector3(playVel.X, playVel.Y, 0);
        hv = Vector3.Lerp(hv, target, 1 - MathF.Exp(-grip * dt));
        playVel = new Vector3(hv.X, hv.Y, playVel.Z);
        if ((K(ImGuiKey.Space) || playTestJump) && playGrounded && !playSwimming) { playVel.Z = PlayJump; playGrounded = false; playTestJump = false; }

        PlayPhysics(dt);
        // camera at the eyes
        var cam = Viewports[ActiveViewport].Camera;
        var look = new Vector3(MathF.Cos(playPitch) * MathF.Cos(playYaw), MathF.Cos(playPitch) * MathF.Sin(playYaw), MathF.Sin(playPitch));
        var eye = playPos + new Vector3(0, 0, playSwimming ? PlaySwimDepth + 0.15f : PlayEye);
        cam.Yaw = MathF.Atan2(-look.Y, -look.X); cam.Pitch = MathF.Asin(-look.Z);
        cam.Distance = 0.5f; cam.Target = eye + cam.Forward * 0.5f;
        return true;
    }

    void PlayPhysics(float dt)
    {
        // water: swim with the head above the surface
        float? water = gameWaterData?.HeightAt(playPos.X, playPos.Y);
        playSwimming = water is { } wz && playPos.Z < wz - PlaySwimDepth + 0.05f;
        if (playSwimming)
        {
            playVel.Z = 0;
            playPos.Z += ((water.Value - PlaySwimDepth) - playPos.Z) * MathF.Min(1, 4f * dt);
        }
        else if (!playGrounded) playVel.Z -= PlayGravity * dt;

        // horizontal move in short steps (less than the body's radius), sliding along the walls
        var move = new Vector3(playVel.X, playVel.Y, 0) * dt;
        int steps = Math.Max(1, (int)MathF.Ceiling(move.Length() / 0.12f));
        var stepMove = move / steps;
        var cand = PlayColliders(playPos, 6f + move.Length());
        for (int s = 0; s < steps; s++)
        {
            var dir = stepMove.LengthSquared() > 1e-10f ? Vector3.Normalize(stepMove) : Vector3.Zero;
            if (dir == Vector3.Zero) break;
            float len = stepMove.Length();
            // walls: rays at knee, chest and head height
            Vector3? wallN = null;
            foreach (float hgt in new[] { PlayStepUp + 0.05f, 1.0f, 1.6f })
            {
                var o = playPos + new Vector3(0, 0, hgt);
                if (PlayRay(cand, o, dir, len + PlayRadius, out _, out var n)) { wallN = n; break; }
            }
            if (wallN is { } wn)
            {
                // slide: what is left of the step along the wall
                var flatN = new Vector3(wn.X, wn.Y, 0);
                if (flatN.LengthSquared() > 1e-6f)
                {
                    flatN = Vector3.Normalize(flatN);
                    var slide = stepMove - flatN * Vector3.Dot(stepMove, flatN);
                    var sdir = slide.LengthSquared() > 1e-10f ? Vector3.Normalize(slide) : Vector3.Zero;
                    bool blocked = sdir == Vector3.Zero;
                    if (!blocked)
                        foreach (float hgt in new[] { PlayStepUp + 0.05f, 1.0f, 1.6f })
                            if (PlayRay(cand, playPos + new Vector3(0, 0, hgt), sdir, slide.Length() + PlayRadius, out _, out _)) { blocked = true; break; }
                    if (!blocked) playPos += slide;
                }
                playVel.X *= 0.5f; playVel.Y *= 0.5f;
            }
            else playPos += stepMove;
            // ground under the new place: steps up and down are followed
            if (playGrounded && !playSwimming)
            {
                var from = playPos + new Vector3(0, 0, PlayStepUp + 0.05f);
                if (PlayRay(cand, from, -Vector3.UnitZ, PlayStepUp + 0.05f + PlayStepDown, out float t, out _)) playPos.Z = from.Z - t;
                else playGrounded = false; // walked off an edge: falls
            }
        }
        // vertical: falling or jumping
        if (!playGrounded && !playSwimming)
        {
            float dz = playVel.Z * dt;
            if (dz < 0)
            {
                var from = playPos + new Vector3(0, 0, 0.5f);
                if (PlayRay(cand, from, -Vector3.UnitZ, 0.5f - dz, out float t, out _)) { playPos.Z = from.Z - t; playVel.Z = 0; playGrounded = true; }
                else playPos.Z += dz;
            }
            else
            {
                // jumping: a ceiling stops the rise
                var from = playPos + new Vector3(0, 0, PlayEye + 0.1f);
                if (PlayRay(cand, from, Vector3.UnitZ, dz, out _, out _)) playVel.Z = 0; else playPos.Z += dz;
            }
            if (playStart.Z - playPos.Z > PlayFallBack) { playPos = playStart + new Vector3(0, 0, 1); playVel = Vector3.Zero; SetStatus("Play: fell through the map, back to the start."); }
        }
        else if (playSwimming) playGrounded = false;
    }

    // ------------------------------------------------------------------------------------------- what the player hits

    /// <summary>Triangles of one model for walking: positions only, without leaves, glass, decals and water.</summary>
    sealed class PlayShape { public Vector3[] Tris; public Vector3 Min, Max; public Vector3[] ChunkMin, ChunkMax; }
    readonly Dictionary<uint, PlayShape> playShapes = new();
    readonly Dictionary<PolyMesh, PlayShape> playSceneShapes = new();
    readonly List<(PlayShape S, Matrix4x4 W, Matrix4x4 Inv)> playCand = new();
    int playLoadsThisFrame;

    static bool WalkThrough(GtaMaterial m)
    {
        if (m == null) return false;
        var s = m.Shader ?? "";
        return s.Contains("decal", StringComparison.OrdinalIgnoreCase) || s.Contains("glass", StringComparison.OrdinalIgnoreCase) || s.StartsWith("trees", StringComparison.OrdinalIgnoreCase)
            || s.Contains("grass", StringComparison.OrdinalIgnoreCase) || s.Contains("water", StringComparison.OrdinalIgnoreCase) || s.Contains("cutout", StringComparison.OrdinalIgnoreCase) && s.Contains("tree", StringComparison.OrdinalIgnoreCase);
    }

    static PlayShape MakeShape(PolyMesh m, Func<int, GtaMaterial> mat)
    {
        var tris = new List<Vector3>(m.Faces.Count * 3);
        foreach (var f in m.Faces)
        {
            if (WalkThrough(mat(f.MatId))) continue;
            var v = f.V;
            for (int i = 1; i + 1 < v.Length; i++) { tris.Add(m.Verts[v[0]]); tris.Add(m.Verts[v[i]]); tris.Add(m.Verts[v[i + 1]]); }
        }
        var s = new PlayShape { Tris = tris.ToArray() };
        if (s.Tris.Length == 0) { s.Min = s.Max = Vector3.Zero; return s; }
        s.Min = new Vector3(float.MaxValue); s.Max = new Vector3(float.MinValue);
        foreach (var p in s.Tris) { s.Min = Vector3.Min(s.Min, p); s.Max = Vector3.Max(s.Max, p); }
        // boxes of 256 triangles, so a ray only tests what is around it
        int n = (s.Tris.Length / 3 + 255) / 256;
        s.ChunkMin = new Vector3[n]; s.ChunkMax = new Vector3[n];
        for (int k = 0; k < n; k++)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = k * 768; i < Math.Min(s.Tris.Length, (k + 1) * 768); i++) { mn = Vector3.Min(mn, s.Tris[i]); mx = Vector3.Max(mx, s.Tris[i]); }
            s.ChunkMin[k] = mn - new Vector3(0.01f); s.ChunkMax[k] = mx + new Vector3(0.01f);
        }
        return s;
    }

    PlayShape ShapeOfArch(uint hash, ArchSlot s)
    {
        if (playShapes.TryGetValue(hash, out var sh)) return sh;
        if (s.Mesh != null && s.State == 2) sh = MakeShape(s.Mesh, id => Scene.Material(id));
        else
        {
            if (playLoadsThisFrame >= 3) return null; // read from the cache a few per frame
            playLoadsThisFrame++;
            uint asset = game?.AssetOf(hash) ?? hash;
            var ld = FromCache(hash) ?? (asset != hash ? FromCache(asset) : null);
            if (ld?.Mesh == null) { playShapes[hash] = null; return null; }
            var mats = ld.R.Materials;
            sh = MakeShape(ld.Mesh, id => id >= 0 && id < mats.Count ? mats[id] : null);
        }
        playShapes[hash] = sh;
        return sh;
    }

    /// <summary>Models around the player (world objects chosen by the LOD tree, and the editable ones), with their placement.</summary>
    List<(PlayShape S, Matrix4x4 W, Matrix4x4 Inv)> PlayColliders(Vector3 p, float reach)
    {
        playCand.Clear(); playLoadsThisFrame = 0;
        if (ImGui.GetTime() - playStepTime > 30) { playShapes.Clear(); playSceneShapes.Clear(); playStepTime = ImGui.GetTime(); }
        if (worldEnts != null)
        {
            var we = worldEnts;
            foreach (var i in worldSel)
            {
                ref var e = ref we.E[i];
                if (e.Mlo || worldOpenMaps.Contains(e.Ymap)) continue;
                if (MathF.Abs(e.Pos.X - p.X) > reach + 60 || MathF.Abs(e.Pos.Y - p.Y) > reach + 60) continue;
                if (!archSlots.TryGetValue(e.Arch, out var s)) continue;
                var sh = ShapeOfArch(e.Arch, s);
                if (sh == null || sh.Tris.Length == 0) continue;
                if (!Matrix4x4.Invert(e.World, out var inv)) continue;
                var lp = Vector3.Transform(p, inv);
                float lr = reach / MathF.Max(0.01f, MathF.Min(e.ScaleXY, e.ScaleZ));
                if (lp.X < sh.Min.X - lr || lp.Y < sh.Min.Y - lr || lp.Z < sh.Min.Z - lr || lp.X > sh.Max.X + lr || lp.Y > sh.Max.Y + lr || lp.Z > sh.Max.Z + lr) continue;
                playCand.Add((sh, e.World, inv));
            }
        }
        foreach (var o in MapLive())
        {
            if (o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0 || o.Mesh == GpuOnlyStub) continue;
            if (Vector3.DistanceSquared(o.Position, p) > (reach + 200) * (reach + 200)) continue;
            if (!playSceneShapes.TryGetValue(o.Mesh, out var sh)) playSceneShapes[o.Mesh] = sh = MakeShape(o.Mesh, id => Scene.Material(id));
            if (sh.Tris.Length == 0) continue;
            var w = o.World;
            if (!Matrix4x4.Invert(w, out var inv)) continue;
            var lp = Vector3.Transform(p, inv);
            if (lp.X < sh.Min.X - reach || lp.Y < sh.Min.Y - reach || lp.Z < sh.Min.Z - reach || lp.X > sh.Max.X + reach || lp.Y > sh.Max.Y + reach || lp.Z > sh.Max.Z + reach) continue;
            playCand.Add((sh, w, inv));
        }
        return playCand;
    }

    /// <summary>Nearest hit of a ray (world) on the colliders within maxT; n: the hit triangle's normal, facing the ray.</summary>
    static bool PlayRay(List<(PlayShape S, Matrix4x4 W, Matrix4x4 Inv)> cand, Vector3 o, Vector3 d, float maxT, out float bestT, out Vector3 n)
    {
        bestT = maxT; n = Vector3.UnitZ; bool hit = false;
        foreach (var (s, w, inv) in cand)
        {
            var lo = Vector3.Transform(o, inv); var ld = Vector3.TransformNormal(d, inv);
            float scale = ld.Length(); if (scale < 1e-8f) continue;
            // t along the local ray equals t along the world ray (same parameter)
            var r = new Ray(lo, ld);
            if (!Picking.RayBox(r, s.Min - new Vector3(0.05f), s.Max + new Vector3(0.05f), out float tb) || tb > bestT) continue;
            for (int k = 0; k < s.ChunkMin.Length; k++)
            {
                if (!Picking.RayBox(r, s.ChunkMin[k], s.ChunkMax[k], out float tc) || tc > bestT) continue;
                int end = Math.Min(s.Tris.Length, (k + 1) * 768);
                for (int i = k * 768; i < end; i += 3)
                {
                    float t = Picking.RayTriangle(r, s.Tris[i], s.Tris[i + 1], s.Tris[i + 2]);
                    if (t > 0 && t < bestT)
                    {
                        bestT = t; hit = true;
                        var ln = Vector3.Cross(s.Tris[i + 1] - s.Tris[i], s.Tris[i + 2] - s.Tris[i]);
                        var wn = Vector3.TransformNormal(ln, w);
                        n = wn.LengthSquared() > 1e-12f ? Vector3.Normalize(wn) : Vector3.UnitZ;
                        if (Vector3.Dot(n, d) > 0) n = -n;
                    }
                }
            }
        }
        return hit;
    }

    /// <summary>The Play hint over the view.</summary>
    void PlayOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (!PlayOn) return;
        string txt = $"PLAY  ·  ZQSD / WASD walk, Shift run, Ctrl slow, Space jump, right button look, Esc leaves" + (playSwimming ? "  ·  swimming" : "");
        var sz = ImGui.CalcTextSize(txt);
        var p = new Vector2(vp.X + (vp.W - sz.X) / 2, vp.Y + vp.H - sz.Y - 24);
        dl.AddRectFilled(p - new Vector2(10, 6), p + sz + new Vector2(10, 6), 0xB0101010, 6);
        dl.AddText(p, 0xFFFFFFFF, txt);
        // a small cross in the middle
        var c = new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2);
        dl.AddLine(c - new Vector2(6, 0), c + new Vector2(6, 0), 0xC0FFFFFF, 1.5f); dl.AddLine(c - new Vector2(0, 6), c + new Vector2(0, 6), 0xC0FFFFFF, 1.5f);
    }
}

public sealed partial class EditorApp
{
    IEnumerable<object> PlayTest(string gtaFolder)
    {
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        yield return Until(() => !Settings.MapLodTree || worldEnts != null || worldEntsTask is { IsCompleted: true }, 300);
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; Maximized = true; cam.Ortho = false;
        Hour = 13; Settings.MapWeather = "Clear";
        // Legion Square (pavement, kerbs, benches), then the Vespucci beach and its sea
        foreach (var (name, spot, yaw, testWater) in new[] { ("legion", new Vector3(195, -935, 30.7f), 2.2f, false), ("beach", new Vector3(-1450, -1400, 4f), 3.6f, true) })
        {
            cam.Yaw = yaw + MathF.PI; cam.Pitch = 0.6f; cam.Distance = 12; cam.Target = spot;
            var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < 12) yield return Frames(1);
            cam.Pitch = 1.3f; yield return Frames(4);
            StartPlay();
            Expect(PlayOn, $"{name}: Play starts");
            if (!PlayOn) continue;
            playYaw = yaw;
            yield return Frames(60);
            var p0 = PlayPos;
            Note($"{name}: start {p0}, on the ground {PlayGrounded}, swimming {PlaySwimming}");
            Expect(PlayGrounded || PlaySwimming, $"{name}: lands on the ground");
            yield return Shot($"play_{name}_start");
            // walk forward 3 s
            playTestWish = new Vector2(1, 0);
            float minZ = PlayPos.Z, maxZ = PlayPos.Z; int air = 0, n = 0;
            var t1 = DateTime.Now;
            while ((DateTime.Now - t1).TotalSeconds < 3) { yield return Frames(1); minZ = MathF.Min(minZ, PlayPos.Z); maxZ = MathF.Max(maxZ, PlayPos.Z); n++; if (!PlayGrounded && !PlaySwimming) air++; }
            playTestWish = null; yield return Frames(10);
            var p1 = PlayPos; float moved = Vector2.Distance(new Vector2(p0.X, p0.Y), new Vector2(p1.X, p1.Y));
            Note($"{name}: walked {moved:0.0} m in 3 s, height {minZ:0.00}..{maxZ:0.00}, in the air {air}/{n} frames, now {p1}, swimming {PlaySwimming}");
            Expect(moved > 4, $"{name}: walking moves the player ({moved:0.0} m in 3 s)");
            Expect(p0.Z - minZ < 8, $"{name}: does not fall through the ground ({p0.Z - minZ:0.0} m down)");
            yield return Shot($"play_{name}_walked");
            // jump
            if (PlayGrounded)
            {
                float z0 = PlayPos.Z, top = z0; playTestJump = true;
                for (int i = 0; i < 90; i++) { yield return Frames(1); top = MathF.Max(top, PlayPos.Z); }
                Note($"{name}: jump {top - z0:0.00} m high, back on the ground {PlayGrounded}");
                Expect(top - z0 > 0.6f && top - z0 < 1.6f && PlayGrounded, $"{name}: jump about 1 m and lands ({top - z0:0.00} m)");
            }
            // a wall: run 6 s along the view, the player must stop or slide on buildings, never go through
            if (!testWater)
            {
                playTestWish = new Vector2(1, 0); playTestSprint = true;
                var t2 = DateTime.Now; while ((DateTime.Now - t2).TotalSeconds < 6) yield return Frames(1);
                playTestWish = null; playTestSprint = false; yield return Frames(5);
                Note($"{name}: after 6 s of running {PlayPos}, on the ground {PlayGrounded}");
                yield return Shot($"play_{name}_run");
            }
            else
            {
                // towards the sea: swim
                playYaw = MathF.Atan2(-0.55f, -0.83f); playTestWish = new Vector2(1, 0); playTestSprint = true;
                var t2 = DateTime.Now; bool swam = false;
                while ((DateTime.Now - t2).TotalSeconds < 25 && !swam) { yield return Frames(1); swam = PlaySwimming; }
                yield return Frames(30);
                playTestWish = null; playTestSprint = false;
                Note($"{name}: swimming {PlaySwimming} at {PlayPos}, water {gameWaterData?.HeightAt(PlayPos.X, PlayPos.Y)}");
                Expect(swam, $"{name}: the player swims in the sea");
                yield return Shot($"play_{name}_swim");
            }
            StopPlay();
            Expect(!PlayOn, $"{name}: Play stops");
        }
        SetMapMode(false);
    }
}
