using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Scenes;
using MoloMax.Core.Mesh;
using MoloMax.Core.Gta.Library;
using MoloMax.Core.Tools;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MLO room generator: draw the floor plan from above (rooms, doors, windows) and build it: closed inward rooms
/// with the Molo gap between them, openings cut through both walls, passages at inner doors. Rebuilding replaces
/// what the previous build made (the "MLO plan" folder).
/// </summary>
public sealed partial class EditorApp
{
    bool planOpen;
    int planTool; // 0 room, 1 door, 2 window, 3 select / move
    Vector2 planPan = new(40, 40); float planZoom = 32; // px per metre
    Vector2? planDragStart; int planSelRoom = -1, planMoveRoom = -1; Vector2 planMoveOff;
    float planDoorW = 1.2f, planDoorH = 2.3f, planWinW = 1.2f, planWinH = 1.2f, planWinSill = 0.9f, planRoomH = 3f;
    const string PlanFolder = "MLO plan";

    public void OpenRoomBuilder() => planOpen = true;
    Vector2 planCanvasC0, planCanvasSize;
    /// <summary>Screen point of a plan point (metres), as the canvas drew it last frame.</summary>
    Vector2 PlanToScreen(Vector2 w) => new(planCanvasC0.X + planPan.X + w.X * planZoom, planCanvasC0.Y + planCanvasSize.Y - planPan.Y - w.Y * planZoom);

    void DrawRoomBuilder()
    {
        if (!planOpen) return;
        var plan = Scene.Plan ??= new RoomPlanData();
        ImGui.SetNextWindowSize(new Vector2(820, 600), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(80, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MLO Room Builder###planner", ref planOpen, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { ImGui.End(); return; }

        // ---- left column: tools and room list
        ImGui.BeginChild("##plantools", new Vector2(220, 0), ImGuiChildFlags.Borders);
        string[] tools = { "Room", "Door", "Window", "Select", "Sizes", "Photo" };
        for (int i = 0; i < tools.Length; i++) { if (i % 2 == 1) ImGui.SameLine(0, 4); if (Theme.Toggle(tools[i], planTool == i, new Vector2(98, 26))) planTool = i; }
        Ui.TextDisabled(planTool switch
        {
            0 => "Drag to draw a room (0.5 m grid).", 1 => "Click on a wall: door (between two rooms or to the street).", 2 => "Click on a wall: window.",
            4 => "Click to drop the size reference, drag to move, R rotates, Del removes.", 5 => "Floor plan picture under the plan: trace it, or read the rooms from it.",
            _ => "Drag a room to move it, drag its sides to resize, drag a door / window along the walls (Shift+click removes it). Del removes the room.",
        });
        if (planTool == 4) PlanSizesPanel(plan);
        if (planTool == 5) PlanPhotoPanel(plan);
        ImGui.Separator();
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Room height", ref planRoomH, 0.05f, 2, 20, "%.2f m");
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Door width", ref planDoorW, 0.05f, 0.5f, 6, "%.2f m");
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Door height", ref planDoorH, 0.05f, 1.5f, 6, "%.2f m");
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Window width", ref planWinW, 0.05f, 0.3f, 6, "%.2f m");
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Window height", ref planWinH, 0.05f, 0.3f, 4, "%.2f m");
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Window sill", ref planWinSill, 0.05f, 0.1f, 3, "%.2f m");
        float gap = plan.Gap; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Room gap", ref gap, 0.005f, 0.02f, 0.5f, "%.3f m")) plan.Gap = gap;
        Theme.Tip("Molo rule: a small gap between two rooms, joined by a floor at the doors.");
        ImGui.Separator();
        Ui.Text($"Rooms ({plan.Rooms.Count})");
        for (int i = 0; i < plan.Rooms.Count; i++)
        {
            var r = plan.Rooms[i];
            ImGui.PushID(i);
            if (ImGui.Selectable($"{r.Name}  {r.Max.X - r.Min.X:0.#} x {r.Max.Y - r.Min.Y:0.#} m", planSelRoom == i)) planSelRoom = i;
            ImGui.PopID();
        }
        if (planSelRoom >= 0 && planSelRoom < plan.Rooms.Count)
        {
            var r = plan.Rooms[planSelRoom];
            string n = r.Name; ImGui.SetNextItemWidth(-1); if (ImGui.InputText("##rn", ref n, 40)) r.Name = n;
            float h = r.Height; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Height", ref h, 0.05f, 2, 20, "%.2f m")) r.Height = h;
            float fz = r.FloorZ; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Floor Z", ref fz, 0.05f, -50, 50, "%.2f m")) r.FloorZ = fz;
            if (ImGui.Button("Delete room", new Vector2(-1, 0))) { plan.Rooms.RemoveAt(planSelRoom); planSelRoom = -1; }
        }
        ImGui.Separator();
        RoomMaterialOptions();
        ImGui.Separator();
        RoomFinishOptions();
        ImGui.Separator();
        ImGui.Checkbox("Live in the viewport", ref PlanLive);
        Theme.Tip("Every change (rooms, doors, heights, materials, skirting, dirt...) rebuilds the rooms in 3D at once. One undo per drag.");
        if (PlanLive) { if (ImGui.Button("Rebuild now", new Vector2(-1, 0))) BuildPlan(); }
        else if (ImGui.Button("BUILD IN THE SCENE", new Vector2(-1, 34))) BuildPlan();
        Theme.Tip("Makes the room boxes, cuts the openings, adds the passages. Building again replaces the previous result.");
        if (ImGui.Button("Clear plan", new Vector2(-1, 0))) { plan.Rooms.Clear(); plan.Openings.Clear(); plan.Props.Clear(); planSelRoom = -1; planSelProp = -1; }
        ImGui.EndChild();
        ImGui.SameLine();

        // ---- plan canvas (X right, Y up like the top view)
        var c0 = ImGui.GetCursorScreenPos(); var size = ImGui.GetContentRegionAvail();
        planCanvasC0 = c0; planCanvasSize = size;
        if (planCenterNext && size.X > 10) { planCenterNext = false; planPan = new Vector2(size.X * 0.5f, size.Y * 0.5f); }
        ImGui.InvisibleButton("##plancanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool hov = ImGui.IsItemHovered(), act = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList(); var io = ImGui.GetIO();
        dl.PushClipRect(c0, c0 + size, true);
        dl.AddRectFilled(c0, c0 + size, 0xFF262626);
        Vector2 ToS(Vector2 w) => new(c0.X + planPan.X + w.X * planZoom, c0.Y + size.Y - planPan.Y - w.Y * planZoom);
        Vector2 ToW(Vector2 s) => new((s.X - c0.X - planPan.X) / planZoom, (c0.Y + size.Y - planPan.Y - s.Y) / planZoom);
        Vector2 Snap05(Vector2 w) => new(MathF.Round(w.X * 2) / 2, MathF.Round(w.Y * 2) / 2);
        if (hov && io.MouseWheel != 0)
        {
            var w = ToW(io.MousePos);
            planZoom = Math.Clamp(planZoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 4, 400);
            planPan = new Vector2(io.MousePos.X - c0.X - w.X * planZoom, c0.Y + size.Y - io.MousePos.Y - w.Y * planZoom); // keep the point under the mouse
        }
        if (act && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))) planPan += new Vector2(io.MouseDelta.X, -io.MouseDelta.Y);
        // floor plan picture under the grid
        if (!string.IsNullOrEmpty(plan.Photo) && renderer.Textures.Get(plan.Photo) is { } photoSrv && planPhotoSize.X > 0)
        {
            float pw = plan.PhotoWidth, ph = pw * planPhotoSize.Y / planPhotoSize.X;
            var a = ToS(new Vector2(plan.PhotoX, plan.PhotoY + ph)); var b = ToS(new Vector2(plan.PhotoX + pw, plan.PhotoY));
            dl.AddImage(imgui.IdOf(photoSrv), a, b, Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, plan.PhotoOpacity)));
        }
        // on the map: the buildings and props around, seen from above, to draw the rooms where they belong
        if (MapMode) DrawPlanMapUnderlay(plan, dl, ToS, hov ? ToW(io.MousePos) : (Vector2?)null);
        // grid: 1 m lines, 5 m darker
        var wmn = ToW(c0 + new Vector2(0, size.Y)); var wmx = ToW(c0 + new Vector2(size.X, 0));
        for (int x = (int)MathF.Floor(wmn.X); x <= wmx.X; x++) dl.AddLine(ToS(new Vector2(x, wmn.Y)), ToS(new Vector2(x, wmx.Y)), x == 0 ? 0xFF606060u : x % 5 == 0 ? 0xFF404040u : 0xFF303030u);
        for (int y = (int)MathF.Floor(wmn.Y); y <= wmx.Y; y++) dl.AddLine(ToS(new Vector2(wmn.X, y)), ToS(new Vector2(wmx.X, y)), y == 0 ? 0xFF606060u : y % 5 == 0 ? 0xFF404040u : 0xFF303030u);
        // rooms
        for (int i = 0; i < plan.Rooms.Count; i++)
        {
            var r = plan.Rooms[i];
            var a = ToS(new Vector2(r.Min.X, r.Max.Y)); var b = ToS(new Vector2(r.Max.X, r.Min.Y));
            dl.AddRectFilled(a, b, i == planSelRoom ? 0x50D0A040u : 0x40A07040u);
            dl.AddRect(a, b, i == planSelRoom ? 0xFF40C0FFu : 0xFFD0D0D0u, 0, ImDrawFlags.None, 2);
            dl.AddText(a + new Vector2(6, 4), 0xFFFFFFFF, r.Name);
            dl.AddText(a + new Vector2(6, 20), 0xFFB0B0B0, $"{r.Max.X - r.Min.X:0.#} x {r.Max.Y - r.Min.Y:0.#} x {r.Height:0.#} m");
        }
        // openings
        foreach (var op in plan.Openings)
        {
            var p = ToS(new Vector2(op.X, op.Y)); float hw = op.Width * 0.5f * planZoom;
            bool vertical = plan.Rooms.Any(r => MathF.Abs(op.X - r.Min.X) < 0.3f || MathF.Abs(op.X - r.Max.X) < 0.3f);
            var d = vertical ? new Vector2(0, hw) : new Vector2(hw, 0);
            dl.AddLine(p - d, p + d, op.Window ? 0xFFFFC040u : 0xFF40FF80u, 5);
        }
        // size references
        for (int i = 0; i < plan.Props.Count; i++)
        {
            var pp = plan.Props[i]; var rf = PlanReferences.Of(pp.Kind);
            float an = pp.Rotation * MathF.PI / 180f; var ax = new Vector2(MathF.Cos(an), MathF.Sin(an)); var ay = new Vector2(-ax.Y, ax.X);
            var c = new Vector2(pp.X, pp.Y); var hx = ax * rf.W * 0.5f; var hy = ay * rf.D * 0.5f;
            var q = new[] { ToS(c - hx - hy), ToS(c + hx - hy), ToS(c + hx + hy), ToS(c - hx + hy) };
            uint col = rf.Colour;
            dl.AddQuadFilled(q[0], q[1], q[2], q[3], (col & 0x00FFFFFF) | 0x50000000);
            dl.AddQuad(q[0], q[1], q[2], q[3], i == planSelProp ? 0xFFFFFFFFu : col, i == planSelProp ? 2.5f : 1.5f);
            if (pp.Kind == "ped") dl.AddCircle(ToS(c), rf.W * 0.5f * planZoom, col, 16, 1.5f);
            if (planZoom > 25) dl.AddText(ToS(c) + new Vector2(-20, -6), 0xFFFFFFFF, rf.Kind);
        }
        // interaction
        var mw = ToW(io.MousePos); var ms = Snap05(mw);
        if (planTool == 4 && hov)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                planSelProp = plan.Props.FindLastIndex(pp => { var rf = PlanReferences.Of(pp.Kind); return Vector2.Distance(new Vector2(pp.X, pp.Y), mw) < MathF.Max(rf.W, rf.D) * 0.5f; });
                if (planSelProp < 0) { plan.Props.Add(new PlanProp { Kind = planPropKind, X = MathF.Round(mw.X * 10) / 10, Y = MathF.Round(mw.Y * 10) / 10 }); planSelProp = plan.Props.Count - 1; }
                planPropDrag = planSelProp;
            }
            if (planPropDrag >= 0 && planPropDrag < plan.Props.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left)) { plan.Props[planPropDrag].X = MathF.Round(mw.X * 20) / 20; plan.Props[planPropDrag].Y = MathF.Round(mw.Y * 20) / 20; }
            else planPropDrag = -1;
            if (planSelProp >= 0 && planSelProp < plan.Props.Count)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.R, false)) plan.Props[planSelProp].Rotation = (plan.Props[planSelProp].Rotation + (io.KeyShift ? -45 : 90)) % 360;
                if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) { plan.Props.RemoveAt(planSelProp); planSelProp = -1; }
            }
        }
        if (hov && planTool <= 3 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            switch (planTool)
            {
                case 0: planDragStart = ms; break;
                case 1: case 2:
                    {
                        var on = NearestWallPoint(plan, mw);
                        if (on.HasValue)
                            plan.Openings.Add(planTool == 1 ? new PlanOpening { X = on.Value.X, Y = on.Value.Y, Width = planDoorW, Height = planDoorH }
                                                            : new PlanOpening { X = on.Value.X, Y = on.Value.Y, Width = planWinW, Height = planWinH, Sill = planWinSill });
                        break;
                    }
                case 3:
                    {
                        // click near an opening: Shift removes it, else drag it along the walls
                        var near = plan.Openings.FirstOrDefault(o => Vector2.Distance(new Vector2(o.X, o.Y), mw) < 0.4f);
                        if (near != null) { if (io.KeyShift) plan.Openings.Remove(near); else planMoveOpening = near; break; }
                        // near a room border: resize that side
                        float tol = 6f / planZoom; planResize = (-1, 0);
                        for (int i = plan.Rooms.Count - 1; i >= 0 && planResize.Room < 0; i--)
                        {
                            var r = plan.Rooms[i]; var mn = r.Min; var mx = r.Max;
                            bool inY = mw.Y > mn.Y - tol && mw.Y < mx.Y + tol, inX = mw.X > mn.X - tol && mw.X < mx.X + tol;
                            int side = MathF.Abs(mw.X - mn.X) < tol && inY ? 1 : MathF.Abs(mw.X - mx.X) < tol && inY ? 2 : MathF.Abs(mw.Y - mn.Y) < tol && inX ? 3 : MathF.Abs(mw.Y - mx.Y) < tol && inX ? 4 : 0;
                            if (side > 0) { planResize = (i, side); planSelRoom = i; }
                        }
                        if (planResize.Room >= 0) break;
                        planSelRoom = plan.Rooms.FindLastIndex(r => mw.X >= r.Min.X && mw.X <= r.Max.X && mw.Y >= r.Min.Y && mw.Y <= r.Max.Y);
                        if (planSelRoom >= 0)
                        {
                            planMoveRoom = planSelRoom; planMoveOff = new Vector2(plan.Rooms[planSelRoom].Min.X, plan.Rooms[planSelRoom].Min.Y) - ms;
                            // openings on this room's walls only (a door shared with a neighbour stays with the neighbour unless that one moves)
                            var rr = plan.Rooms[planSelRoom];
                            bool OnWalls(PlanRoom q, PlanOpening o) { var mn = q.Min; var mx = q.Max; const float t = 0.3f;
                                return ((MathF.Abs(o.X - mn.X) < t || MathF.Abs(o.X - mx.X) < t) && o.Y > mn.Y - t && o.Y < mx.Y + t) || ((MathF.Abs(o.Y - mn.Y) < t || MathF.Abs(o.Y - mx.Y) < t) && o.X > mn.X - t && o.X < mx.X + t); }
                            planMoveOpenings = plan.Openings.Where(o => OnWalls(rr, o) && !plan.Rooms.Any(q => q != rr && OnWalls(q, o))).ToList();
                        }
                        break;
                    }
            }
        }
        if (planDragStart.HasValue)
        {
            var a = planDragStart.Value;
            dl.AddRect(ToS(new Vector2(MathF.Min(a.X, ms.X), MathF.Max(a.Y, ms.Y))), ToS(new Vector2(MathF.Max(a.X, ms.X), MathF.Min(a.Y, ms.Y))), 0xFF40C0FF, 0, ImDrawFlags.None, 2);
            dl.AddText(io.MousePos + new Vector2(14, 0), 0xFFFFFFFF, $"{MathF.Abs(ms.X - a.X):0.0} x {MathF.Abs(ms.Y - a.Y):0.0} m");
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (MathF.Abs(ms.X - a.X) >= 1 && MathF.Abs(ms.Y - a.Y) >= 1)
                {
                    plan.Rooms.Add(new PlanRoom { Name = "room_" + (plan.Rooms.Count + 1), X0 = MathF.Min(a.X, ms.X), Y0 = MathF.Min(a.Y, ms.Y), X1 = MathF.Max(a.X, ms.X), Y1 = MathF.Max(a.Y, ms.Y), Height = planRoomH });
                    planSelRoom = plan.Rooms.Count - 1;
                }
                planDragStart = null;
            }
        }
        // resize a room side (0.5 m grid, at least 1 m)
        if (planResize.Room >= 0)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && planResize.Room < plan.Rooms.Count)
            {
                var r = plan.Rooms[planResize.Room]; var mn = r.Min; var mx = r.Max;
                switch (planResize.Side)
                {
                    case 1: mn.X = MathF.Min(ms.X, mx.X - 1); break;
                    case 2: mx.X = MathF.Max(ms.X, mn.X + 1); break;
                    case 3: mn.Y = MathF.Min(ms.Y, mx.Y - 1); break;
                    case 4: mx.Y = MathF.Max(ms.Y, mn.Y + 1); break;
                }
                r.X0 = mn.X; r.Y0 = mn.Y; r.X1 = mx.X; r.Y1 = mx.Y;
                dl.AddText(io.MousePos + new Vector2(14, 0), 0xFFFFFFFF, $"{mx.X - mn.X:0.0} x {mx.Y - mn.Y:0.0} m");
            }
            else planResize = (-1, 0);
        }
        // drag a door / window along the walls
        if (planMoveOpening != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && plan.Openings.Contains(planMoveOpening))
            {
                var on = NearestWallPoint(plan, mw);
                if (on.HasValue) { planMoveOpening.X = on.Value.X; planMoveOpening.Y = on.Value.Y; }
            }
            else planMoveOpening = null;
        }
        // cursor hint over a room side in Select mode
        if (hov && planTool == 3 && planResize.Room < 0 && planMoveRoom < 0)
        {
            float tol = 6f / planZoom;
            foreach (var r in plan.Rooms)
            {
                var mn = r.Min; var mx = r.Max;
                bool inY = mw.Y > mn.Y && mw.Y < mx.Y, inX = mw.X > mn.X && mw.X < mx.X;
                if ((MathF.Abs(mw.X - mn.X) < tol || MathF.Abs(mw.X - mx.X) < tol) && inY) { ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW); break; }
                if ((MathF.Abs(mw.Y - mn.Y) < tol || MathF.Abs(mw.Y - mx.Y) < tol) && inX) { ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS); break; }
            }
        }
        if (planMoveRoom >= 0)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && planMoveRoom < plan.Rooms.Count)
            {
                var r = plan.Rooms[planMoveRoom]; var sz = r.Max - r.Min; var nmin = ms + planMoveOff;
                var delta = nmin - r.Min;
                r.X0 = nmin.X; r.Y0 = nmin.Y; r.X1 = nmin.X + sz.X; r.Y1 = nmin.Y + sz.Y;
                // the doors and windows of this room's walls follow it
                if (delta != Vector2.Zero) foreach (var op in planMoveOpenings) { op.X += delta.X; op.Y += delta.Y; }
            }
            else planMoveRoom = -1;
        }
        if (hov && planTool != 4 && planSelRoom >= 0 && ImGui.IsKeyPressed(ImGuiKey.Delete, false)) { plan.Rooms.RemoveAt(planSelRoom); planSelRoom = -1; }
        dl.AddText(c0 + new Vector2(8, 6), 0xFFB0B0B0, (hov ? $"{ms.X:0.0}, {ms.Y:0.0} m   " : "") + "wheel zoom, right / middle drag pan   (top view, X right, Y up)");
        dl.PopClipRect();
        ImGui.End();
        PlanLiveUpdate(plan);
    }

    static Vector2? NearestWallPoint(RoomPlanData plan, Vector2 p)
    {
        Vector2? best = null; float bd = 0.6f;
        foreach (var r in plan.Rooms)
        {
            var mn = r.Min; var mx = r.Max;
            foreach (var (a, b) in new[] { (mn, new Vector2(mx.X, mn.Y)), (new Vector2(mx.X, mn.Y), mx), (mx, new Vector2(mn.X, mx.Y)), (new Vector2(mn.X, mx.Y), mn) })
            {
                var ab = b - a; float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
                var q = a + ab * t; float d = Vector2.Distance(p, q);
                if (d < bd) { bd = d; best = new Vector2(MathF.Round(q.X * 4) / 4, MathF.Round(q.Y * 4) / 4); }
            }
        }
        return best;
    }

    void BuildPlan() => BuildPlan(false);

    /// <summary>Builds the plan in the scene. Live = while editing the plan: no undo step of its own, no framing, materials reused.</summary>
    void BuildPlan(bool live)
    {
        var plan = Scene.Plan;
        if (plan == null || (plan.Rooms.Count == 0 && !live)) { SetStatus("Draw at least one room first."); return; }
        var built = RoomPlanBuilder.Build(plan);
        void Make()
        {
            Scene.RemoveObjects(o => o.Folder == PlanFolder);
            if (plan.Rooms.Count == 0) return;
            if (!Scene.Folders.Contains(PlanFolder)) Scene.Folders.Add(PlanFolder);
            foreach (var (n, m, p) in built.Rooms) { var o = Scene.Add(n, m, p, GtaObjectType.MloRoom); o.Name = n; o.Folder = PlanFolder; }
            foreach (var (n, m, p) in built.Links) { var o = Scene.Add(n, m, p); o.Name = n; o.Folder = PlanFolder; o.Gta.Role = GtaRole.Shell; ApplyRoomMaterials(o); }
            foreach (var pp in plan.Props)
            {
                var rf = PlanReferences.Of(pp.Kind);
                var mesh = pp.Kind == "ped" ? Primitives.Cylinder(rf.W * 0.5f, rf.H, 12) : pp.Kind == "stairs" ? Primitives.Stairs(rf.W, 17, rf.H, rf.D) : Primitives.Box(rf.W, rf.D, rf.H);
                var o = Scene.Add("ref_" + pp.Kind, mesh, new Vector3(pp.X + plan.OriginX, pp.Y + plan.OriginY, pp.FloorZ + plan.OriginZ));
                o.Name = NextName("ref_" + pp.Kind); o.Folder = PlanFolder;
                o.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, pp.Rotation * MathF.PI / 180f);
                o.Gta.Role = GtaRole.Ignore; o.Gta.NoAutoCollision = true; o.Frozen = true;
                o.WireColour = new Vector3((rf.Colour & 0xFF) / 255f, ((rf.Colour >> 8) & 0xFF) / 255f, ((rf.Colour >> 16) & 0xFF) / 255f);
            }
            var rooms = Scene.Objects.Where(o => o.Folder == PlanFolder && o.GtaType == GtaObjectType.MloRoom).ToList();
            finishLinks = Scene.Objects.Where(o => o.Folder == PlanFolder && o.Name.StartsWith("link_") && !o.Name.Contains("_lining")).ToList();
            try { finishMade = FinishRooms(rooms, PlanFolder, live); } finally { finishLinks = new(); }
        }
        if (live)
        {
            try { Make(); } catch (Exception e) { SetStatus("Live build failed: " + e.Message); }
            Dirty = true;
            Selected.RemoveWhere(id => Scene.Find(id) == null);
        }
        else Do("Build MLO plan", Make);
        hubLayout = null;
        planBuiltJson = PlanJson(plan); planBuiltOpts = PlanOptions(); planSceneRef = Scene;
        if (live) { if (built.Warnings.Count > 0) SetStatus(string.Join(" ", built.Warnings)); return; }
        SetStatus($"Built {built.Rooms.Count} room(s) and {built.Links.Count} passage(s)" + (finishMade > 0 ? $", {finishMade} finishing piece(s)." : ".") + (built.Warnings.Count > 0 ? " " + string.Join(" ", built.Warnings) : ""));
        if (!MapMode) FrameAll();
    }

    // ---- live: every change of the plan shows in the viewport at once (one undo step per drag / edit)
    public bool PlanLive = true;
    string planBuiltJson;
    Scene planSceneRef;
    bool planEditing;
    double planLastBuild;

    static string PlanJson(RoomPlanData p) => System.Text.Json.JsonSerializer.Serialize(p);
    /// <summary>The builder options outside the plan (materials and their tiling, finishing): a change rebuilds live too.</summary>
    string PlanOptions() => string.Join(",", RoomMat) + "|" + string.Join(",", RoomMatTile.Select(t => t.ToString("0.###"))) + "|"
        + $"{FinishSkirting}{FinishFloorDirt}{FinishCeilingDirt}{FinishCornerWear}{FinishCornice}{FinishFrames}{FinishFrameStyle}{FinishJoinDoors}|{FinishSkirtingHeight:0.####}|{FinishDirtWidth:0.####}";
    string planBuiltOpts;

    void PlanLiveUpdate(RoomPlanData plan)
    {
        string now = PlanJson(plan);
        // a new scene, an undo or a redo: the scene already matches its plan
        string opts = PlanOptions();
        if (!ReferenceEquals(planSceneRef, Scene) || planBuiltJson == null)
        {
            planSceneRef = Scene; planBuiltJson = now; planBuiltOpts = opts;
            // first plan of this scene with no build yet: nothing to sync
            return;
        }
        bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (!PlanLive) { if (!mouseDown) planEditing = false; return; }
        if ((now != planBuiltJson || opts != planBuiltOpts) && plan.Rooms.Count + (now != planBuiltJson ? 1 : 0) > 0 && ImGui.GetTime() - planLastBuild > 0.05)
        {
            if (!planEditing)
            {
                // one undo step for the whole drag: the snapshot holds the plan as it was before
                var cur = Scene.Plan;
                try { Scene.Plan = System.Text.Json.JsonSerializer.Deserialize<RoomPlanData>(planBuiltJson); } catch { }
                Undo.Record("Edit MLO plan", Scene);
                Scene.Plan = cur;
                planEditing = true;
            }
            BuildPlan(true);
            planLastBuild = ImGui.GetTime();
        }
        if (!mouseDown && ImGui.GetTime() - planLastBuild > 0.3) planEditing = false;
    }

    int planSelProp = -1, planPropDrag = -1;
    (int Room, int Side) planResize = (-1, 0);
    PlanOpening planMoveOpening;
    List<PlanOpening> planMoveOpenings = new();
    string planPropKind = "ped";
    Vector2 planPhotoSize;
    float planPhotoThreshold;
    string planPhotoNote;

    void PlanSizesPanel(RoomPlanData plan)
    {
        foreach (var rf in PlanReferences.All)
            if (ImGui.Selectable($"{rf.Label}   {rf.W:0.##} x {rf.D:0.##} x {rf.H:0.##} m", planPropKind == rf.Kind)) planPropKind = rf.Kind;
        Ui.TextDisabled("Built as frozen reference boxes (not exported).");
        if (planSelProp >= 0 && planSelProp < plan.Props.Count)
        {
            var pp = plan.Props[planSelProp];
            float r = pp.Rotation; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Rotation", ref r, 1, -360, 360, "%.0f°")) pp.Rotation = r;
            float z = pp.FloorZ; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Floor Z##pz", ref z, 0.05f, -50, 50, "%.2f m")) pp.FloorZ = z;
        }
        if (plan.Props.Count > 0 && ImGui.Button("Remove all references", new Vector2(-1, 0))) { plan.Props.Clear(); planSelProp = -1; }
        ImGui.Separator();
    }

    // ---- MAP mode: footprints of the map around the plan (light: four corners per model, refreshed now and then)
    readonly Dictionary<PolyMesh, (Vector3 Min, Vector3 Max)> planMeshBounds = new();
    bool planCenterNext;
    List<(Vector2 A, Vector2 B, Vector2 C, Vector2 D, float Top, string Name)> planFoot;
    Vector2 planFootOrigin; double planFootTime;

    void DrawPlanMapUnderlay(RoomPlanData plan, ImDrawListPtr dl, Func<Vector2, Vector2> toS, Vector2? mouse)
    {
        var org = new Vector2(plan.OriginX, plan.OriginY);
        if (planFoot == null || Vector2.Distance(org, planFootOrigin) > 1 || ImGui.GetTime() - planFootTime > 3)
        {
            planFootOrigin = org; planFootTime = ImGui.GetTime();
            planFoot = new();
            const float R = 140;
            foreach (var o in Scene.Objects)
            {
                if (o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0 || o.Gta.MapArchetype == null || MapEditable(o)) continue;
                if (MathF.Abs(o.Position.X - org.X) > R + 60 || MathF.Abs(o.Position.Y - org.Y) > R + 60) continue;
                if (!planMeshBounds.TryGetValue(o.Mesh, out var lb)) planMeshBounds[o.Mesh] = lb = o.Mesh.Bounds();
                var sz = lb.Max - lb.Min;
                if (MathF.Max(sz.X, sz.Y) < 1.2f || MathF.Max(sz.X, sz.Y) > 160 || sz.Z < 0.6f) continue; // decals, kerbs, terrain
                var w = o.World;
                Vector2 P(float x, float y) { var v = Vector3.Transform(new Vector3(x, y, lb.Min.Z), w); return new Vector2(v.X, v.Y) - org; }
                float top = Vector3.Transform(lb.Max, w).Z - plan.OriginZ;
                planFoot.Add((P(lb.Min.X, lb.Min.Y), P(lb.Max.X, lb.Min.Y), P(lb.Max.X, lb.Max.Y), P(lb.Min.X, lb.Max.Y), top, o.Gta.MapArchetype));
            }
            if (LodTreeOn)
            {
                // the level-of-detail world: the objects chosen around (read only, not scene objects)
                foreach (var i in worldSel)
                {
                    var e = worldEnts.E[i];
                    if (e.Mlo || worldOpenMaps.Contains(e.Ymap) || MathF.Abs(e.Pos.X - org.X) > R + 60 || MathF.Abs(e.Pos.Y - org.Y) > R + 60) continue;
                    if (!archSlots.TryGetValue(e.Arch, out var sl) || !(sl.State == 2 && sl.Mesh != null || sl.GpuOnly)) continue;
                    var sz = sl.Max - sl.Min;
                    if (MathF.Max(sz.X, sz.Y) < 1.2f || MathF.Max(sz.X, sz.Y) > 160 || sz.Z < 0.6f) continue;
                    var w = e.World; var lb = (Min: sl.Min, Max: sl.Max);
                    Vector2 P(float x, float y) { var v = Vector3.Transform(new Vector3(x, y, lb.Min.Z), w); return new Vector2(v.X, v.Y) - org; }
                    float top = Vector3.Transform(lb.Max, w).Z - plan.OriginZ;
                    planFoot.Add((P(lb.Min.X, lb.Min.Y), P(lb.Max.X, lb.Min.Y), P(lb.Max.X, lb.Max.Y), P(lb.Min.X, lb.Max.Y), top, sl.Name ?? e.Arch.ToString("X8")));
                }
            }
            planFoot.Sort((a, b) => a.Top.CompareTo(b.Top)); // tall last, on top
        }
        string hoverName = null;
        foreach (var f in planFoot)
        {
            // low things light, buildings darker and outlined
            bool tall = f.Top > 3;
            uint fill = tall ? 0xD8907A68u : 0x90685E56u, line = tall ? 0xFFE0D2C4u : 0xA0908070u;
            dl.AddQuadFilled(toS(f.A), toS(f.B), toS(f.C), toS(f.D), fill);
            dl.AddQuad(toS(f.A), toS(f.B), toS(f.C), toS(f.D), line, tall ? 1.5f : 1f);
            if (mouse is Vector2 m && tall && Inside(m, f.A, f.B, f.C, f.D)) hoverName = $"{f.Name}  ({f.Top:0.#} m high)";
        }
        if (hoverName != null) ImGui.SetTooltip(hoverName);
        static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float S(Vector2 u, Vector2 v) => (v.X - u.X) * (p.Y - u.Y) - (v.Y - u.Y) * (p.X - u.X);
            float s1 = S(a, b), s2 = S(b, c), s3 = S(c, d), s4 = S(d, a);
            return s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0 || s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0;
        }
    }

    void PlanPhotoPanel(RoomPlanData plan)
    {
        if (ImGui.Button(string.IsNullOrEmpty(plan.Photo) ? "Open a plan picture..." : "Change picture...", new Vector2(-1, 0)))
        {
            var f = PickTexture();
            if (f != null) { plan.Photo = f; planPhotoSize = Vector2.Zero; }
        }
        if (string.IsNullOrEmpty(plan.Photo)) { Ui.TextDisabled("A scan, a photo of a plan or a screenshot: dark walls on a light background."); ImGui.Separator(); return; }
        if (planPhotoSize.X <= 0)
        {
            try { using var bmp = new System.Drawing.Bitmap(plan.Photo); planPhotoSize = new Vector2(bmp.Width, bmp.Height); } catch { planPhotoSize = new Vector2(1, 1); }
        }
        Ui.TextDisabled(Path.GetFileName(plan.Photo) + $"  ({planPhotoSize.X:0} x {planPhotoSize.Y:0} px)");
        float w = plan.PhotoWidth; ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Real width", ref w, 0.05f, 1, 500, "%.2f m")) plan.PhotoWidth = w;
        Theme.Tip("The real width of the whole picture: sets the scale. Read a known wall length on the plan and adjust until it matches the grid.");
        float x = plan.PhotoX, y = plan.PhotoY;
        ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Offset X", ref x, 0.05f)) plan.PhotoX = x;
        ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Offset Y", ref y, 0.05f)) plan.PhotoY = y;
        float op = plan.PhotoOpacity; ImGui.SetNextItemWidth(-90); if (ImGui.SliderFloat("Opacity", ref op, 0, 1)) plan.PhotoOpacity = op;
        ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Threshold", ref planPhotoThreshold, 0, 0.9f, planPhotoThreshold <= 0 ? "auto" : "%.2f");
        Theme.Tip("What counts as a wall (darker than this). Auto works on clean plans.");
        if (ImGui.Button("READ ROOMS FROM THE PICTURE", new Vector2(-1, 28))) ReadPlanPhoto(plan);
        Theme.Tip("Finds the closed rooms and the doors between them. Replaces the rooms and doors of the plan.");
        if (planPhotoNote != null) Ui.TextWrapped(planPhotoNote);
        if (ImGui.Button("Remove picture", new Vector2(-1, 0))) { plan.Photo = null; planPhotoNote = null; }
        ImGui.Separator();
    }

    void ReadPlanPhoto(RoomPlanData plan)
    {
        var img = TextureReader.Load(plan.Photo, 2048);
        if (img == null) { planPhotoNote = "Could not read the picture."; return; }
        var r = FloorPlanReader.Read(img, new FloorPlanReader.Settings { WidthMetres = plan.PhotoWidth, Threshold = planPhotoThreshold, RoomHeight = planRoomH });
        if (r.Rooms.Count == 0) { planPhotoNote = string.Join(" ", r.Notes); return; }
        foreach (var room in r.Rooms) { room.X0 += plan.PhotoX; room.X1 += plan.PhotoX; room.Y0 += plan.PhotoY; room.Y1 += plan.PhotoY; }
        foreach (var d in r.Doors) { d.X += plan.PhotoX; d.Y += plan.PhotoY; d.Height = planDoorH; }
        plan.Rooms.Clear(); plan.Rooms.AddRange(r.Rooms);
        plan.Openings.RemoveAll(o => !o.Window); plan.Openings.AddRange(r.Doors);
        planSelRoom = -1;
        planPhotoNote = $"{r.Rooms.Count} room(s), {r.Doors.Count} door(s) found." + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : "") + " Check them, add the street doors and windows, then build.";
        SetStatus(planPhotoNote);
    }
}
