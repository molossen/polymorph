using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Scenes;
using MoloMax.Core.Splats;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Getting a scan ready for a block-out: stand it on its floor, square its walls to X / Y, give it its real size from the
/// ceiling height, then hand the modelling to an AI through MCP (the splat.* tools) with a ready-made prompt.
/// </summary>
public sealed partial class EditorApp
{
    float splatCeiling = 2.6f;

    const string SplatBlockoutPrompt =
@"Model a block-out of the scan that is open in Polymorph, through the Polymorph MCP tools.

1. splat.info, then splat.planes: check the floor is at z = 0 and the walls run along X / Y (the scan is already levelled, squared and to scale).
2. Read the room: splat.section at z = 1.0 and z = 2.3 (plan cuts with a 1 m grid), straight elevations of each wall with splat.view (ortho = true, clip_min / clip_max to cut away the near half), splat.measure for floor, ceiling and furniture heights, splat.ray for distances.
3. Build with object.create_mesh, one closed box per piece, names starting with bo_: floor and ceiling slabs, walls 0.12 m thick split around every door, window and opening, one box per door leaf, stairs as one closed stepped mesh, the main furniture as simple boxes. Real-world sizes (door about 2.0-2.1 m).
4. Check every step with splat.view and splat.section (the block-out is drawn over the scan in colour) and move what does not line up.
5. Finish with gta.check and gta.fix_all.";

    void SplatBlockoutPanel(SceneObject o, float w2)
    {
        ImGui.Spacing();
        Ui.TextDisabled("Block-out");
        if (ImGui.Button("Level on the floor", new Vector2(w2, 0))) SplatLevel(o);
        Theme.Tip("Finds the floor of the scan, makes it flat and puts it at height 0. Turn the scan roughly upright first.");
        ImGui.SameLine();
        if (ImGui.Button("Square the walls", new Vector2(w2, 0))) SplatSquare(o);
        Theme.Tip("Turns the scan so its biggest wall runs along X or Y.");
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##ceil", ref splatCeiling, 0.01f, 1.8f, 12f, "Ceiling %.2f m");
        Theme.Tip("The real floor-to-ceiling height of the room (about 2.5 to 2.7 m in a house).");
        ImGui.SameLine();
        if (ImGui.Button("Real size", new Vector2(w2, 0))) SplatRealSize(o, splatCeiling);
        Theme.Tip("Scales the scan so its ceiling is at that height.");
        if (ImGui.Button("Copy the block-out prompt for AI (MCP)", new Vector2(-1, 0)))
        {
            ImGui.SetClipboardText(SplatBlockoutPrompt);
            SetStatus("Block-out prompt copied: paste it to your AI assistant connected to Polymorph (Preferences > Scripting > MCP server).");
        }
        Theme.Tip("The AI looks at the scan through the splat tools (views, plan cuts, measures) and models the walls, doors, stairs and furniture.");
    }

    /// <summary>Rotates the scan so its floor plane is level, then moves it so the floor is at z = 0.</summary>
    void SplatLevel(SceneObject o)
    {
        var s = ScanOf(o);
        // big horizontal planes (normal turned up); the floor is the lowest of them, not the biggest (a ceiling often has more blobs)
        var flat = s.Planes(8).Where(p => MathF.Abs(p.Normal.Z) > 0.7f)
            .Select(p => p.Normal.Z < 0 ? new SplatScan.Plane { Normal = -p.Normal, D = -p.D, Points = p.Points, Min = p.Min, Max = p.Max, Kind = p.Kind } : p).ToList();
        int most = flat.Count == 0 ? 0 : flat.Max(p => p.Points);
        var floor = flat.Where(p => p.Points >= most * 0.3f).OrderBy(p => p.D / p.Normal.Z).FirstOrDefault();
        if (floor == null) { SetStatus("No floor found: turn the scan upright first (Turn upright)."); return; }
        var n = floor.Normal; float d = floor.D;
        // the floor is the lowest big horizontal plane; its normal must end up pointing up
        if (n.Z < 0) { n = -n; d = -d; }
        var c = (floor.Min + floor.Max) * 0.5f;
        var onFloor = c - n * (Vector3.Dot(n, c) - d);
        var q = RotationBetween(n, Vector3.UnitZ);
        Do("Level scan", () =>
        {
            var pivot = o.Position;
            o.Rotation = Quaternion.Normalize(q * o.Rotation);
            var p = pivot + Vector3.Transform(onFloor - pivot, q);
            o.Position -= new Vector3(0, 0, p.Z);
        });
        SetStatus($"Scan levelled ({MathF.Acos(Math.Clamp(n.Z, -1, 1)) * 180 / MathF.PI:0.0}° corrected), floor at 0.");
    }

    /// <summary>Turns the scan around Z so its biggest wall lines up with X or Y.</summary>
    void SplatSquare(SceneObject o)
    {
        var s = ScanOf(o);
        var wall = s.Planes(8).Where(p => MathF.Abs(p.Normal.Z) < 0.3f).OrderByDescending(p => p.Points).FirstOrDefault();
        if (wall == null) { SetStatus("No wall found in the scan."); return; }
        float a = MathF.Atan2(wall.Normal.Y, wall.Normal.X);
        float snap = MathF.Round(a / (MathF.PI / 2)) * (MathF.PI / 2);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, snap - a);
        var (mn, mx) = o.WorldBounds(); var centre = (mn + mx) * 0.5f;
        Do("Square scan", () =>
        {
            o.Rotation = Quaternion.Normalize(q * o.Rotation);
            o.Position = centre + Vector3.Transform(o.Position - centre, q);
        });
        SetStatus($"Scan turned {(snap - a) * 180 / MathF.PI:0.0}° so its walls run along X / Y.");
    }

    /// <summary>Scales the scan around its floor centre so the ceiling of the middle of the room is at the given height.</summary>
    void SplatRealSize(SceneObject o, float ceiling)
    {
        var s = ScanOf(o);
        var size = s.Max - s.Min; var mid = (s.Min + s.Max) * 0.5f;
        var half = new Vector3(size.X * 0.2f, size.Y * 0.2f, 0);
        var m = s.MeasureBox(new Vector3(mid.X - half.X, mid.Y - half.Y, s.Min.Z), new Vector3(mid.X + half.X, mid.Y + half.Y, s.Max.Z));
        float? floor = m.Floor, ceil = m.Ceiling;
        if (floor != null)
        {
            // the ceiling is often thin in a scan: look again in the top half only
            float zTop = floor.Value + (s.Max.Z - floor.Value) * 0.5f;
            var top = s.MeasureBox(new Vector3(mid.X - half.X, mid.Y - half.Y, zTop), new Vector3(mid.X + half.X, mid.Y + half.Y, s.Max.Z));
            if (top.Layers.Count > 0) ceil = top.Layers[^1].Z + 0.05f;
        }
        if (floor == null || ceil == null || ceil - floor < 0.1f) { SetStatus("No ceiling found in the middle of the scan: set the size by hand (Scale)."); return; }
        float f = ceiling / (ceil.Value - floor.Value);
        var anchor = new Vector3(mid.X, mid.Y, floor.Value);
        Do("Scan real size", () =>
        {
            o.Scale *= f;
            o.Position = anchor + (o.Position - anchor) * f;
        });
        SetStatus($"Scan scaled x{f:0.###}: ceiling {ceiling:0.00} m.");
    }

    static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(from, to));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }
}
