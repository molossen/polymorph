using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Gta.Export;

public sealed class MloRoom
{
    public string Name;
    public SceneObject Source;
    public Vector3 Min, Max;
    public string Timecycle = "";
    public uint Flags;
    public List<int> Entities = new();
    public int PortalCount;
    public bool Contains(Vector3 p, float pad = 0.05f) =>
        p.X >= Min.X - pad && p.Y >= Min.Y - pad && p.Z >= Min.Z - pad && p.X <= Max.X + pad && p.Y <= Max.Y + pad && p.Z <= Max.Z + pad;
    public float Volume => MathF.Max(0, Max.X - Min.X) * MathF.Max(0, Max.Y - Min.Y) * MathF.Max(0, Max.Z - Min.Z);
    public float Distance(Vector3 p) => (Vector3.Clamp(p, Min, Max) - p).Length();
}

public sealed class MloPortal
{
    public int RoomFrom, RoomTo;
    public Vector3[] Corners = new Vector3[4];
    public uint Flags;
    public List<int> AttachedEntities = new();
    public string Origin; // "auto", "auto (exterior)", "manual: name"
}

/// <summary>
/// Rooms and portals of an MLO, computed from the scene: each MLO Room object is a room (its bounds), the
/// openings cut in room boxes (open edge loops) facing each other across the gap give the portals between
/// rooms, an opening with no room in front is a portal to the outside. Portal objects are used as they are.
/// </summary>
public sealed class MloLayout
{
    public List<MloRoom> Rooms = new(); // index 0 is limbo
    public List<MloPortal> Portals = new();
    public List<string> Warnings = new();

    public int ExitPortals => Portals.Count(p => p.RoomTo == 0 || p.RoomFrom == 0);

    sealed class Opening
    {
        public int Room;
        public int Axis;         // normal axis 0 1 2
        public float Coord;      // plane position on that axis
        public Vector3 Min, Max; // bounds of the loop
        public bool Used;
    }

    public static MloLayout Build(Scene scene, GtaProjectSettings settings)
    {
        var lay = new MloLayout();
        var roomObjs = scene.Objects.Where(o => o.GtaType == GtaObjectType.MloRoom && o.Gta.Role != GtaRole.Ignore).ToList();
        var limbo = new MloRoom { Name = "limbo", Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        lay.Rooms.Add(limbo);
        foreach (var o in roomObjs)
        {
            var (mn, mx) = o.WorldBounds();
            lay.Rooms.Add(new MloRoom
            {
                Name = SafeRoomName(o.Name, lay.Rooms.Select(r => r.Name)),
                Source = o, Min = mn, Max = mx, Timecycle = o.Gta.RoomTimecycle ?? "", Flags = o.Gta.RoomFlags,
            });
            limbo.Min = Vector3.Min(limbo.Min, mn); limbo.Max = Vector3.Max(limbo.Max, mx);
        }
        if (lay.Rooms.Count == 1) { limbo.Min = limbo.Max = Vector3.Zero; return lay; }

        // ---- manual portals first
        foreach (var p in scene.Objects.Where(o => o.GtaType == GtaObjectType.Portal && o.Gta.Role != GtaRole.Ignore))
        {
            var (mn, mx) = p.WorldBounds();
            int axis = ThinAxis(mn, mx);
            var mp = MakePortal(axis, (mn[axis] + mx[axis]) * 0.5f, mn, mx);
            var c = (mn + mx) * 0.5f;
            var off = new Vector3(); off[axis] = settings.PortalMaxGap * 0.5f + 0.25f;
            int a = lay.RoomAt(c - off), b = lay.RoomAt(c + off);
            if (a == b) { lay.Warnings.Add($"Portal '{p.Name}' does not stand between two rooms (or a room and the outside)."); continue; }
            mp.RoomFrom = a == 0 ? b : a; mp.RoomTo = a == 0 ? 0 : b;
            mp.Flags = p.Gta.PortalFlags;
            mp.Origin = "manual: " + p.Name;
            lay.Portals.Add(mp);
        }

        // ---- openings cut in the room boxes
        var openings = new List<Opening>();
        for (int r = 1; r < lay.Rooms.Count; r++)
        {
            var o = lay.Rooms[r].Source;
            var rm = o.Evaluated() ?? o.Mesh; // openings made by modifiers (Wall Opening: arches, round windows) count too
            foreach (var loop in BorderLoops(rm))
            {
                var w = o.World;
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var v in loop) { var p = Vector3.Transform(rm.Verts[v], w); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                int axis = ThinAxis(mn, mx);
                var size = mx - mn;
                float a1 = size[(axis + 1) % 3], a2 = size[(axis + 2) % 3];
                if (a1 < 0.3f || a2 < 0.3f) continue; // slivers, not doorways
                if (size[axis] > 0.25f * MathF.Min(a1, a2)) { lay.Warnings.Add($"Room '{o.Name}' has an opening that is not flat; add a Portal object there."); continue; }
                openings.Add(new Opening { Room = r, Axis = axis, Coord = (mn[axis] + mx[axis]) * 0.5f, Min = mn, Max = mx });
            }
        }
        foreach (var a in openings)
        {
            if (a.Used) continue;
            Opening best = null; float bestScore = 0;
            foreach (var b in openings)
            {
                if (b == a || b.Used || b.Room == a.Room || b.Axis != a.Axis) continue;
                if (MathF.Abs(a.Coord - b.Coord) > settings.PortalMaxGap) continue;
                float ov = Overlap2D(a, b);
                if (ov > bestScore) { bestScore = ov; best = b; }
            }
            if (lay.PortalExists(a)) { a.Used = true; continue; }
            if (best != null)
            {
                best.Used = a.Used = true;
                var mn = Vector3.Min(a.Min, best.Min); var mx = Vector3.Max(a.Max, best.Max);
                var p = MakePortal(a.Axis, (a.Coord + best.Coord) * 0.5f, mn, mx);
                p.RoomFrom = a.Room; p.RoomTo = best.Room; p.Origin = "auto";
                lay.Portals.Add(p);
            }
        }
        foreach (var a in openings.Where(o => !o.Used))
        {
            if (lay.PortalExists(a)) continue;
            // an opening with no room in front: a portal to the outside
            var c = (a.Min + a.Max) * 0.5f;
            var dir = new Vector3(); dir[a.Axis] = 1;
            var room = lay.Rooms[a.Room];
            if (room.Contains(c + dir * 0.3f, -0.01f)) dir = -dir; // point away from the room
            // look further than the portal gap: a room facing this opening means the gap is too wide, not an exit
            int facing = 0; float facingDist = 0;
            for (float d = 0.1f; d <= 3f; d += 0.1f)
            {
                int r = lay.RoomAt(c + dir * d);
                if (r != 0 && r != a.Room) { facing = r; facingDist = d; break; }
            }
            if (facing != 0)
            {
                if (facingDist > settings.PortalMaxGap)
                    lay.Warnings.Add($"The doorway of '{room.Name}' faces '{lay.Rooms[facing].Name}' {facingDist:0.0} m away: more than the portal max gap ({settings.PortalMaxGap:0.0} m). Bring the rooms closer or raise the max gap.");
                else
                    lay.Warnings.Add($"The doorway of '{room.Name}' faces '{lay.Rooms[facing].Name}' but that room has no opening there: cut the doorway in both boxes.");
                continue;
            }
            var p = MakePortal(a.Axis, a.Coord, a.Min, a.Max);
            p.RoomFrom = a.Room; p.RoomTo = 0; p.Origin = "auto (exterior)";
            lay.Portals.Add(p);
        }

        // ---- rooms touching with no portal at all
        for (int i = 1; i < lay.Rooms.Count; i++)
            for (int j = i + 1; j < lay.Rooms.Count; j++)
                if (Adjacent(lay.Rooms[i], lay.Rooms[j], settings.PortalMaxGap) && !lay.Portals.Any(p => (p.RoomFrom == i && p.RoomTo == j) || (p.RoomFrom == j && p.RoomTo == i)))
                    lay.Warnings.Add($"Rooms '{lay.Rooms[i].Name}' and '{lay.Rooms[j].Name}' are next to each other but have no opening between them: cut a doorway in both boxes or add a Portal object.");
        if (lay.ExitPortals == 0) lay.Warnings.Add("The MLO has no portal to the outside: the interior will not show from the street. Cut the entrance in a room box or add a Portal object.");

        // ---- rooms with no portal path to the outside: the game hides them (and the limbo shells seen from inside)
        {
            var seen = new HashSet<int> { 0 }; bool grew = true;
            while (grew) { grew = false; foreach (var q in lay.Portals) { if (seen.Contains(q.RoomFrom) != seen.Contains(q.RoomTo)) { seen.Add(q.RoomFrom); seen.Add(q.RoomTo); grew = true; } } }
            for (int i2 = 1; i2 < lay.Rooms.Count; i2++)
                if (!seen.Contains(i2)) lay.Warnings.Add($"Room '{lay.Rooms[i2].Name}' is not linked to the outside by any portal: it will be invisible in game. Add a Portal object to the outside (limbo).");
        }
        if (lay.Rooms.Count > 32) lay.Warnings.Add($"{lay.Rooms.Count - 1} rooms: collision RoomID only goes up to 31.");
        foreach (var p in lay.Portals)
        {
            lay.Rooms[p.RoomFrom].PortalCount++; lay.Rooms[p.RoomTo].PortalCount++;
            lay.OrientPortal(p);
        }
        return lay;
    }

    /// <summary>Winds the corners so the portal normal points from roomFrom to roomTo (as in game files).</summary>
    void OrientPortal(MloPortal p)
    {
        var c = p.Corners;
        var n = Vector3.Cross(c[1] - c[0], c[2] - c[0]);
        var center = (c[0] + c[2]) * 0.5f;
        var from = Rooms[p.RoomFrom];
        var fromCenter = (from.Min + from.Max) * 0.5f;
        if (Vector3.Dot(n, center - fromCenter) < 0) p.Corners = new[] { c[0], c[3], c[2], c[1] };
    }

    bool PortalExists(Opening a) => Portals.Any(p =>
    {
        if (p.RoomFrom != a.Room && p.RoomTo != a.Room) return false;
        var mn = Vector3.Min(Vector3.Min(p.Corners[0], p.Corners[1]), Vector3.Min(p.Corners[2], p.Corners[3]));
        var mx = Vector3.Max(Vector3.Max(p.Corners[0], p.Corners[1]), Vector3.Max(p.Corners[2], p.Corners[3]));
        var c = (a.Min + a.Max) * 0.5f;
        return c.X >= mn.X - 0.6f && c.Y >= mn.Y - 0.6f && c.Z >= mn.Z - 0.6f && c.X <= mx.X + 0.6f && c.Y <= mx.Y + 0.6f && c.Z <= mx.Z + 0.6f;
    });

    /// <summary>The smallest room containing the point (0 = limbo / outside).</summary>
    public int RoomAt(Vector3 p)
    {
        int best = 0; float bv = float.MaxValue;
        for (int i = 1; i < Rooms.Count; i++)
            if (Rooms[i].Contains(p) && Rooms[i].Volume < bv) { bv = Rooms[i].Volume; best = i; }
        return best;
    }

    /// <summary>The room for a collision triangle: the one containing it, else the nearest within a metre.</summary>
    public int RoomForCollision(Vector3 p)
    {
        int r = RoomAt(p);
        if (r != 0) return r;
        float bd = 1.0f;
        for (int i = 1; i < Rooms.Count; i++) { float d = Rooms[i].Distance(p); if (d < bd) { bd = d; r = i; } }
        return r;
    }

    static bool Adjacent(MloRoom a, MloRoom b, float gap)
    {
        for (int ax = 0; ax < 3; ax++)
        {
            float d = MathF.Max(b.Min[ax] - a.Max[ax], a.Min[ax] - b.Max[ax]);
            if (d < -0.01f || d > gap) continue;
            int u = (ax + 1) % 3, v = (ax + 2) % 3;
            float ou = MathF.Min(a.Max[u], b.Max[u]) - MathF.Max(a.Min[u], b.Min[u]);
            float ov = MathF.Min(a.Max[v], b.Max[v]) - MathF.Max(a.Min[v], b.Min[v]);
            if (ou > 0.5f && ov > 0.5f) return true;
        }
        return false;
    }

    static float Overlap2D(Opening a, Opening b)
    {
        int u = (a.Axis + 1) % 3, v = (a.Axis + 2) % 3;
        float ou = MathF.Min(a.Max[u], b.Max[u]) - MathF.Max(a.Min[u], b.Min[u]);
        float ov = MathF.Min(a.Max[v], b.Max[v]) - MathF.Max(a.Min[v], b.Min[v]);
        if (ou <= 0 || ov <= 0) return 0;
        float area = ou * ov;
        float sa = (a.Max[u] - a.Min[u]) * (a.Max[v] - a.Min[v]), sb = (b.Max[u] - b.Min[u]) * (b.Max[v] - b.Min[v]);
        float r = area / MathF.Max(1e-6f, MathF.Min(sa, sb));
        return r > 0.25f ? r : 0;
    }

    static int ThinAxis(Vector3 mn, Vector3 mx)
    {
        var s = mx - mn;
        return s.X <= s.Y && s.X <= s.Z ? 0 : s.Y <= s.Z ? 1 : 2;
    }

    /// <summary>Rectangle portal on the plane axis = coord, corners ordered like CodeWalker writes them.</summary>
    static MloPortal MakePortal(int axis, float coord, Vector3 mn, Vector3 mx)
    {
        // v = vertical axis (Z) unless the portal is horizontal
        int v = axis == 2 ? 1 : 2;
        int u = 3 - axis - v;
        Vector3 P(float uu, float vv) { var p = new Vector3(); p[axis] = coord; p[u] = uu; p[v] = vv; return p; }
        return new MloPortal
        {
            Corners = new[] { P(mx[u], mn[v]), P(mx[u], mx[v]), P(mn[u], mx[v]), P(mn[u], mn[v]) },
        };
    }

    /// <summary>Open edge loops of a mesh (holes such as doorways).</summary>
    public static List<List<int>> BorderLoops(PolyMesh m)
    {
        var next = new Dictionary<int, List<int>>();
        foreach (var (e, faces) in m.BuildEdgeMap())
        {
            if (faces.Count != 1) continue;
            // keep the face's winding so loops are walked consistently
            var f = m.Faces[faces[0]];
            int i = Array.IndexOf(f.V, e.A);
            bool forward = f.V[(i + 1) % f.V.Length] == e.B;
            int from = forward ? e.A : e.B, to = forward ? e.B : e.A;
            if (!next.TryGetValue(from, out var l)) next[from] = l = new List<int>();
            l.Add(to);
        }
        var loops = new List<List<int>>();
        var used = new HashSet<(int, int)>();
        foreach (var start in next.Keys.ToList())
        {
            foreach (var first in next[start])
            {
                if (used.Contains((start, first))) continue;
                var loop = new List<int> { start };
                int prev = start, cur = first;
                used.Add((start, first));
                int guard = 0;
                while (cur != start && guard++ < 100000)
                {
                    loop.Add(cur);
                    if (!next.TryGetValue(cur, out var outs)) break;
                    int nx = outs.FirstOrDefault(n => !used.Contains((cur, n)), -1);
                    if (nx < 0) break;
                    used.Add((cur, nx));
                    prev = cur; cur = nx;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
        }
        return loops;
    }

    static string SafeRoomName(string n, IEnumerable<string> existing)
    {
        var s = TextureBank.SafeName(n);
        var set = new HashSet<string>(existing);
        if (!set.Contains(s)) return s;
        for (int i = 2; ; i++) if (!set.Contains($"{s}_{i}")) return $"{s}_{i}";
    }
}
