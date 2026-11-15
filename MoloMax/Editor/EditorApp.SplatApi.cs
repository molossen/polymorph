using System.Numerics;
using System.Text.Json.Nodes;
using MoloMax.Core.Scenes;
using MoloMax.Core.Splats;
using P = MoloMax.Editor.ApiServer.Param;

namespace MoloMax.Editor;

/// <summary>
/// Scripting / MCP tools to model a block-out over a splat capture: look at it from any camera with the block-out drawn
/// over it, cut it in plan at a height, measure floor and ceiling heights in a box, cast rays to the first wall, and find its
/// big flat planes (to stand it upright). They only read the capture; the modelling itself is done with the usual
/// object / mesh calls, by whoever looks at these results (a person or Claude through MCP).
/// </summary>
public sealed partial class EditorApp
{
    readonly Dictionary<int, (SplatCloud Cloud, Matrix4x4 World, SplatScan Scan)> splatScans = new();

    SceneObject SplatObj(JsonObject p)
    {
        SceneObject o = null;
        if (Arg(p, "id") != null || Str(p, "name") != null) o = Obj(p);
        o ??= Scene.Objects.FirstOrDefault(x => IsSplat(x) && Selected.Contains(x.Id)) ?? Scene.Objects.FirstOrDefault(IsSplat);
        if (o == null || !IsSplat(o)) throw new InvalidOperationException("No splat capture in the scene (import a .ply / .spz / .splat first).");
        if (o.SplatCloud == null) throw new InvalidOperationException($"The capture '{o.Name}' is still loading: try again in a moment.");
        return o;
    }

    SplatScan ScanOf(SceneObject o)
    {
        var w = o.World;
        if (splatScans.TryGetValue(o.Id, out var c) && c.Cloud == o.SplatCloud && c.World == w) return c.Scan;
        var s = new SplatScan(o.SplatCloud, w, Math.Max(0.25f, o.Splat?.MinOpacity ?? 0.25f));
        splatScans[o.Id] = (o.SplatCloud, w, s);
        return s;
    }

    /// <summary>Every visible modelled object (not captures, bones or lights) to draw over the capture.</summary>
    List<SplatScan.Overlay> SplatOverlay(JsonObject p)
    {
        var res = new List<SplatScan.Overlay>();
        if (!Bool(p, "overlay", true)) return res;
        var only = Str(p, "only");
        var pal = new[] { new Vector3(1f, 0.4f, 0.1f), new Vector3(0.1f, 0.8f, 1f), new Vector3(0.95f, 0.2f, 0.6f), new Vector3(0.3f, 1f, 0.3f), new Vector3(1f, 0.9f, 0.2f) };
        int k = 0;
        foreach (var o in Scene.Objects)
        {
            if (IsSplat(o) || o.IsBone || o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0) continue;
            if (only != null && !o.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            res.Add(new SplatScan.Overlay { Mesh = o.Mesh, World = o.World, Colour = pal[k++ % pal.Length], Label = o.Name });
        }
        return res;
    }

    static string ShotPath(JsonObject p, string kind)
    {
        var path = Str(p, "path");
        if (string.IsNullOrEmpty(path))
        {
            var dir = Path.Combine(Path.GetTempPath(), "polymorph_splat");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{kind}_{DateTime.Now:HHmmss_fff}.png");
        }
        return path;
    }

    void RegisterSplatApi()
    {
        var A = Api;
        A.Add("splat.info", "The splat capture to model over: its box in world space (98% of the blobs), blob count, transform, and the floor / ceiling heights of the whole box. Stand it upright and to scale first (object.set rotation / scale) using splat.planes and a known size (a door is about 2.1 m, GTA doors 2.3 m).",
            new[] { new P("id", "int", "capture object id (default: the first capture)"), new P("name", "string", "or its name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o); var m = s.MeasureBox(s.Min, s.Max);
                return new { id = o.Id, name = o.Name, file = o.SplatFile, blobs = s.Count, min = V3(s.Min), max = V3(s.Max), size = V3(s.Max - s.Min),
                    position = V3(o.Position), rotation = V3(EulerDeg(o.Rotation)), scale = V3(o.Scale), floor = m.Floor, ceiling = m.Ceiling,
                    layers = m.Layers.Select(l => new { z = MathF.Round(l.Z, 3), points = l.Count }) };
            });
        A.Add("splat.view", "Picture (PNG, returned as an image through MCP) of the capture from a camera, with the block-out objects drawn over it (faces see-through, edges solid, one colour per object). Use clip_min / clip_max to cut away a ceiling or a wall (cut-away view). ortho=true with ortho_height for straight elevations or plans.",
            new[] { new P("eye", "vec3", "camera position", true), new P("target", "vec3", "point looked at", true), new P("fov", "float", "vertical field of view, degrees (default 70)"),
                new P("width", "int", "pixels (default 1280)"), new P("height", "int", "pixels (default 800)"), new P("ortho", "bool", "orthographic"), new P("ortho_height", "float", "metres seen top to bottom (ortho)"),
                new P("clip_min", "vec3", "only blobs inside this box (min corner)"), new P("clip_max", "vec3", "max corner"), new P("overlay", "bool", "draw the block-out (default true)"), new P("only", "string", "draw only objects whose name contains this"),
                new P("face_alpha", "float", "block-out faces opacity 0..1 (default 0.35)"), new P("path", "string", "PNG path (default: temp)"), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o);
                var img = s.View(Vec(p, "eye").Value, Vec(p, "target").Value, Num(p, "fov", 70), Math.Clamp(Int(p, "width", 1280), 64, 3000), Math.Clamp(Int(p, "height", 800), 64, 3000),
                    SplatOverlay(p), Num(p, "face_alpha", 0.35f), Bool(p, "ortho"), Num(p, "ortho_height", 10), 0.05f, Vec(p, "clip_min"), Vec(p, "clip_max"));
                var path = ShotPath(p, "view"); SplatScan.SavePng(img, path);
                return new { png = path, width = img.Width, height = img.Height };
            });
        A.Add("splat.section", "Plan cut seen from above (PNG, returned as an image): the capture between z-thick/2 and z+thick/2 drawn dark where dense (walls, furniture), a grid every metre (darker every 5 m, the X axis green and the Y axis red lines through 0), and the block-out cut at the same height in colour. Read wall positions from the grid.",
            new[] { new P("z", "float", "height of the cut", true), new P("thick", "float", "slice thickness (default 0.3)"), new P("min", "vec3", "x,y of the lower-left corner (default: capture box)"), new P("max", "vec3", "x,y of the upper-right corner"),
                new P("px_per_m", "float", "pixels per metre (default: fits 1000 px)"), new P("overlay", "bool", "draw the block-out (default true)"), new P("only", "string", "only objects whose name contains this"), new P("path", "string", "PNG path"), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o);
                var mn = Vec(p, "min") ?? s.Min; var mx = Vec(p, "max") ?? s.Max;
                float ppm = Num(p, "px_per_m", 0); if (ppm <= 0) ppm = 1000f / MathF.Max(0.1f, MathF.Max(mx.X - mn.X, mx.Y - mn.Y));
                var r = s.Section(Num(p, "z"), Num(p, "thick", 0.3f), new Vector2(mn.X, mn.Y), new Vector2(mx.X, mx.Y), ppm, SplatOverlay(p));
                var path = ShotPath(p, "section"); SplatScan.SavePng(r.Image, path);
                return new { png = path, width = r.Image.Width, height = r.Image.Height, world_min = new[] { r.Min.X, r.Min.Y }, world_max = new[] { r.Max.X, r.Max.Y }, px_per_m = ppm, note = "pixel (0,0) is the top-left = (min.x, max.y)" };
            });
        A.Add("splat.measure", "What the capture has inside a box: blob count, their real bounds, the floor and ceiling heights (densest bands at the bottom / top) and every dense horizontal layer (floors, table tops, shelves). Use thin boxes to read a wall or the top of a piece of furniture.",
            new[] { new P("min", "vec3", "box min corner", true), new P("max", "vec3", "box max corner", true), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o); var m = s.MeasureBox(Vec(p, "min").Value, Vec(p, "max").Value);
                return new { points = m.Points, bounds_min = m.Points > 0 ? V3(m.Min) : null, bounds_max = m.Points > 0 ? V3(m.Max) : null, floor = m.Floor, ceiling = m.Ceiling,
                    layers = m.Layers.Select(l => new { z = MathF.Round(l.Z, 3), points = l.Count }) };
            });
        A.Add("splat.ray", "Distance from a point to the first dense surface of the capture along a direction (a wall, the floor, furniture). Cast several to read a room's width, a door's height, a step.",
            new[] { new P("origin", "vec3", "start point", true), new P("dir", "vec3", "direction", true), new P("max", "float", "max distance (default 30)"), new P("radius", "float", "probe radius (default 0.06)"), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o);
                var h = s.Ray(Vec(p, "origin").Value, Vec(p, "dir").Value, Num(p, "max", 30), Num(p, "radius", 0.06f));
                if (h == null) return new { hit = false };
                var (d, pt, c) = h.Value;
                return (object)new { hit = true, distance = MathF.Round(d, 3), point = V3(pt), colour = new[] { (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255) } };
            });
        A.Add("splat.level", "Stands the capture on its floor: finds the lowest big flat plane, makes it level and puts it at z = 0. Turn the capture roughly upright first if it lies on its side.",
            new[] { new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            { var o = SplatObj(p); SplatLevel(o); return new { status, rotation = V3(EulerDeg(o.Rotation)), position = V3(o.Position) }; });
        A.Add("splat.square", "Turns the capture around Z so its biggest wall runs along X or Y.",
            new[] { new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            { var o = SplatObj(p); SplatSquare(o); return new { status, rotation = V3(EulerDeg(o.Rotation)) }; });
        A.Add("splat.real_size", "Scales the capture around its floor so the ceiling in the middle of the room is at the given height (metres).",
            new[] { new P("ceiling", "float", "real floor-to-ceiling height, e.g. 2.6", true), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            { var o = SplatObj(p); SplatRealSize(o, Num(p, "ceiling", 2.6f)); return new { status, scale = V3(o.Scale), position = V3(o.Position) }; });
        A.Add("splat.planes", "The biggest flat planes of the capture (floors, ceilings, walls) with their normal, offset, size and kind. Use them to stand the capture upright (floor normal straight up) and square (walls along X / Y) before modelling.",
            new[] { new P("count", "int", "how many planes (default 8)"), new P("id", "int", "capture object id"), new P("name", "string", "capture name") }, p =>
            {
                var o = SplatObj(p); var s = ScanOf(o);
                return s.Planes(Math.Clamp(Int(p, "count", 8), 1, 20)).Select(pl => new { kind = pl.Kind, normal = V3(pl.Normal), d = MathF.Round(pl.D, 4), points = pl.Points, min = V3(pl.Min), max = V3(pl.Max) }).ToList();
            });
    }
}
