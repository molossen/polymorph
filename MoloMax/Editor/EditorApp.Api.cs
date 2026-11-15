using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using P = MoloMax.Editor.ApiServer.Param;

namespace MoloMax.Editor;

/// <summary>
/// The scripting API (Python package, MCP server): scene, objects, instances, mesh data, modifier stack, UVs, GTA check and export.
/// Each call is one undo step, like a click in the UI.
/// </summary>
public sealed partial class EditorApp
{
    public readonly ApiServer Api = new();
    public static string ApiInfoFile => Path.Combine(AppSettings.DataDir, "api.json");
    string apiShotPath;

    void StartApi()
    {
        RegisterApi();
        if (!Settings.ApiEnabled) return;
        if (!Api.Start(Settings.ApiPort, ApiInfoFile) && !Api.Start(0, ApiInfoFile)) SetStatus("Scripting API could not start: " + Api.Error);
    }

    // ------------------------------------------------------------------ parameter helpers
    static JsonNode Arg(JsonObject p, string k) => p.TryGetPropertyValue(k, out var v) ? v : null;
    static string Str(JsonObject p, string k, string def = null) => Arg(p, k) is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
    static float Num(JsonObject p, string k, float def = 0) => Arg(p, k) is JsonValue v ? (v.TryGetValue<double>(out var d) ? (float)d : v.TryGetValue<string>(out var s) && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def) : def;
    static int Int(JsonObject p, string k, int def = 0) => Arg(p, k) is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : def;
    static bool Bool(JsonObject p, string k, bool def = false) => Arg(p, k) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    static Vector3? Vec(JsonObject p, string k) => Arg(p, k) is JsonArray a && a.Count >= 3 ? new Vector3((float)a[0].GetValue<double>(), (float)a[1].GetValue<double>(), (float)a[2].GetValue<double>()) : null;
    static float[] V3(Vector3 v) => new[] { MathF.Round(v.X, 5), MathF.Round(v.Y, 5), MathF.Round(v.Z, 5) };

    SceneObject Obj(JsonObject p, bool required = true)
    {
        SceneObject o = null;
        if (Arg(p, "id") is JsonValue iv && iv.TryGetValue<double>(out var id)) o = Scene.Find((int)id);
        else if (Str(p, "name") is { } n) o = Scene.Objects.FirstOrDefault(x => x.Name == n) ?? Scene.Objects.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
        else if (!required) return null;
        else o = Current;
        if (o == null && required) throw new InvalidOperationException("Object not found (give 'id' or 'name').");
        return o;
    }

    static Vector3 EulerDeg(Quaternion q)
    {
        // XYZ euler in degrees (the Transform panel's convention)
        var m = Matrix4x4.CreateFromQuaternion(q);
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1)), x, z;
        if (MathF.Abs(m.M13) < 0.9999f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); } else { x = MathF.Atan2(-m.M32, m.M22); z = 0; }
        return new Vector3(x, y, z) * (180f / MathF.PI);
    }
    static Quaternion FromEulerDeg(Vector3 d)
    {
        var r = d * (MathF.PI / 180f);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z);
    }

    object ObjInfo(SceneObject o, bool detail = false)
    {
        var d = new Dictionary<string, object>
        {
            ["id"] = o.Id, ["name"] = o.Name, ["type"] = o.GtaType.ToString(), ["parent"] = o.ParentId >= 0 ? o.ParentId : null, ["folder"] = o.Folder,
            ["position"] = V3(o.Position), ["rotation"] = V3(EulerDeg(o.Rotation)), ["scale"] = V3(o.Scale),
            ["verts"] = o.Mesh.Verts.Count, ["faces"] = o.Mesh.Faces.Count, ["hidden"] = o.Hidden,
            ["instances"] = Scene.InstanceCount(o), ["modifiers"] = o.FullStack.Select(m => m.TypeName).ToArray(),
        };
        if (detail)
        {
            var (mn, mx) = o.WorldBounds();
            d["bounds"] = new[] { V3(mn), V3(mx) };
            d["gta"] = JsonSerializer.SerializeToNode(o.Gta, new JsonSerializerOptions { IncludeFields = true });
            d["materials"] = o.Mesh.Faces.Select(f => f.MatId).Distinct().Select(id => Scene.Material(id).Name).ToArray();
        }
        return d;
    }

    /// <summary>Sets public fields of <paramref name="target"/> from a JSON object (case-insensitive names; numbers, bools, strings, [x,y,z]).</summary>
    static List<string> SetFields(object target, JsonObject values)
    {
        var done = new List<string>();
        if (values == null) return done;
        var t = target.GetType();
        foreach (var (k, v) in values)
        {
            var f = t.GetFields(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase));
            if (f == null || v == null) continue;
            object val = f.FieldType switch
            {
                var ft when ft == typeof(float) => (float)v.GetValue<double>(),
                var ft when ft == typeof(int) => (int)v.GetValue<double>(),
                var ft when ft == typeof(uint) => (uint)v.GetValue<double>(),
                var ft when ft == typeof(bool) => v.GetValue<bool>(),
                var ft when ft == typeof(string) => v.GetValue<string>(),
                var ft when ft == typeof(Vector3) && v is JsonArray a && a.Count >= 3 => new Vector3((float)a[0].GetValue<double>(), (float)a[1].GetValue<double>(), (float)a[2].GetValue<double>()),
                var ft when ft.IsEnum => v is JsonValue jv && jv.TryGetValue<string>(out var es) ? Enum.Parse(ft, es, true) : Enum.ToObject(ft, (int)v.GetValue<double>()),
                _ => null,
            };
            if (val == null) continue;
            f.SetValue(target, val); done.Add(f.Name);
        }
        return done;
    }

    static readonly HashSet<string> BigFields = new(StringComparer.OrdinalIgnoreCase) { "Result", "BasePos", "Origin", "UV", "Seams" };
    static object ModInfo(Modifier m, int i)
    {
        var props = new Dictionary<string, object>();
        foreach (var f in m.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.Name is "Name" or "Expanded") continue;
            var v = f.GetValue(m);
            if (BigFields.Contains(f.Name)) { props[f.Name] = v switch { null => null, Array a => $"{a.Length} values", MeshData md => $"mesh {md.FN.Length} polys", _ => "set" }; continue; }
            props[f.Name] = v is Vector3 vv ? V3(vv) : v is Enum e ? e.ToString() : v;
        }
        return new { index = i, type = m.TypeName, name = m.Name, warning = (m as IEditableModifier)?.Warning, props };
    }

    void RegisterApi()
    {
        var A = Api;
        A.Add("app.info", "Polymorph version, open file, object count, selection.", Array.Empty<P>(), _ => new
        {
            app = "Polymorph", version = AppInfo.Version, file = CurrentFile, dirty = Dirty, objects = Scene.Objects.Count, selected = Selected.ToArray(), active = ActiveId,
            mode = Level.ToString(), resource = Scene.Gta.ResourceName,
        });

        A.Add("app.quit", "Close Polymorph. A scene that has a file is saved to it first (no dialog left waiting for a click); discard=true closes without saving (unsaved changes are lost). A new scene never saved still asks.", new[] { new P("discard", "bool", "close without saving") }, p =>
        {
            if (Bool(p, "discard")) Dirty = false;
            else if (Dirty && CurrentFile != null) { BakeAllPaint(); SceneIO.Save(Scene, CurrentFile); Dirty = false; AddRecent(CurrentFile); }
            form.BeginInvoke(new Action(() => form.Close()));
            return true;
        });

        // ---- scene
        A.Add("scene.list", "Objects of the scene (id, name, type, parent, transform, counts, modifiers). Optional name filter / type.",
            new[] { new P("filter", "string", "part of the name"), new P("type", "string", "Drawable, Model, Collision, CollisionGroup, MloRoom, Portal, Light") },
            p =>
            {
                var f = Str(p, "filter"); var t = Str(p, "type");
                return Scene.Objects.Where(o => (f == null || o.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) && (t == null || string.Equals(o.GtaType.ToString(), t, StringComparison.OrdinalIgnoreCase))).Select(o => ObjInfo(o)).ToList();
            });
        A.Add("scene.new", "New empty scene (unsaved changes are lost).", Array.Empty<P>(), _ => { Dirty = false; NewScene(); return true; });
        A.Add("scene.open", "Open a .mmx scene.", new[] { new P("path", "string", "file path", true) }, p => { OpenFile(Str(p, "path")); return new { file = CurrentFile, objects = Scene.Objects.Count }; });
        A.Add("scene.save", "Save the scene (to path, or to the current file).", new[] { new P("path", "string", "file path (.mmx)") }, p =>
        {
            var path = Str(p, "path") ?? CurrentFile ?? throw new InvalidOperationException("The scene has no file yet: give 'path'.");
            BakeAllPaint(); SceneIO.Save(Scene, path); CurrentFile = path; Dirty = false; AddRecent(path);
            return new { file = path };
        });
        A.Add("script.run", "Run Python inside Polymorph, like Blender's bpy: 'import poly' gives the scene and the editor. " +
            "poly.context (scene, selected, active, cursor), poly.create(kind, (x,y,z), name), poly.add(name, PolyMesh, at), poly.delete(o), poly.find(name), " +
            "poly.find_all(text), poly.objects(), poly.select(*objs), poly.level('edge'), poly.sub(o), poly.import_file(path), poly.open_file / save_as, " +
            "poly.material(name), poly.new_material(name, diffuse, shader), poly.set_material(o, m, faces), poly.add_light(kind, at), poly.frame(), " +
            "poly.call(apiMethod, **args), poly.invoke / get / set (any editor member), poly.members(text), poly.register_menu(label, func) for add-ons. " +
            "Mesh tools: poly.Primitives, poly.MeshOps, poly.PolyMesh, poly.UvUnwrap, poly.Vector3. One run = one undo step; returns print() output and the value of a last bare expression.",
            new[] { new P("code", "string", "Python code", true) },
            p => RunPython(Str(p, "code")));
        A.Add("script.run_cs", "Run C# code inside Polymorph (same helpers as Python, for heavy loops).", new[] { new P("code", "string", "C# statements", true) },
            p => RunCSharp(Str(p, "code")));
        A.Add("addons.reload", "Reload the Python add-ons (bundled + %APPDATA%\\Polymorph\\addons). Returns the report.", Array.Empty<P>(), _ => LoadPyAddons());
        A.Add("scene.undo", "Undo the last step.", Array.Empty<P>(), _ => { DoUndo(); return Undo.UndoName; });
        A.Add("scene.redo", "Redo.", Array.Empty<P>(), _ => { DoRedo(); return Undo.RedoName; });

        // ---- objects
        A.Add("object.get", "One object with details (bounds, GTA settings, materials).", new[] { new P("id", "int", "object id"), new P("name", "string", "or its name") }, p => ObjInfo(Obj(p), true));
        A.Add("object.create", "Add a primitive: box, plane, sphere, cylinder, cone, tube, room, stairs, door, portal, collision_box, tufted.",
            new[] { new P("kind", "string", "primitive kind", true), new P("name", "string", "object name"), new P("position", "vec3", "[x,y,z] metres"), new P("rotation", "vec3", "[x,y,z] degrees"),
                    new P("params", "object", "size fields: Width, Length, Height, Radius, Radius2, WSegs, LSegs, HSegs, Sides, Segments, Steps, RoomWidth, RoomLength, RoomHeight, StairWidth, StairRise, StairRun...") },
            p =>
            {
                var kindName = (Str(p, "kind") ?? "box").Replace("_", "");
                if (!Enum.TryParse<CreateKind>(kindName, true, out var k) || k is CreateKind.None or CreateKind.Wall) throw new InvalidOperationException("Unknown kind " + kindName);
                var saved = JsonSerializer.Serialize(Params, new JsonSerializerOptions { IncludeFields = true });
                SetFields(Params, Arg(p, "params") as JsonObject);
                var (mesh, nm, type) = BuildPrimitive(k);
                var restore = JsonSerializer.Deserialize<CreateParams>(saved, new JsonSerializerOptions { IncludeFields = true });
                foreach (var f in typeof(CreateParams).GetFields()) f.SetValue(Params, f.GetValue(restore));
                var o = AddObject(Str(p, "name") ?? nm, mesh, Vec(p, "position") ?? Vector3.Zero, type, Vec(p, "rotation") is { } r ? FromEulerDeg(r) : null);
                if (Str(p, "name") is { } given) o.Name = given;
                Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged();
                return ObjInfo(o);
            });
        A.Add("object.create_mesh", "Add an object from raw geometry: verts [[x,y,z],...] and faces [[i,j,k(,l...)],...] (counter-clockwise seen from outside).",
            new[] { new P("verts", "array", "vertex positions", true), new P("faces", "array", "polygons as vertex index lists", true), new P("name", "string", "object name"), new P("position", "vec3", "pivot position"), new P("type", "string", "Drawable (default), Collision, MloRoom, Portal") },
            p =>
            {
                var m = new PolyMesh();
                foreach (var v in (JsonArray)Arg(p, "verts")) { var a = (JsonArray)v; m.Verts.Add(new Vector3((float)a[0].GetValue<double>(), (float)a[1].GetValue<double>(), (float)a[2].GetValue<double>())); }
                foreach (var f in (JsonArray)Arg(p, "faces")) { var idx = ((JsonArray)f).Select(x => (int)x.GetValue<double>()).ToArray(); if (idx.Length >= 3 && idx.All(i => i >= 0 && i < m.Verts.Count)) m.Faces.Add(new Face(idx)); }
                m.Validate();
                var type = Enum.TryParse<GtaObjectType>(Str(p, "type") ?? "Drawable", true, out var t) ? t : GtaObjectType.Drawable;
                var o = AddObject(Str(p, "name") ?? "Mesh", m, Vec(p, "position") ?? Vector3.Zero, type);
                if (Str(p, "name") is { } given) o.Name = given;
                return ObjInfo(o);
            });
        A.Add("object.set", "Change an object: name, position, rotation (deg), scale, hidden, frozen, folder, parent (id or -1), type, gta {Role, ModelName, LodDist, CollisionMaterial, RoomIdOverride, EntitySet, ...}.",
            new[] { new P("id", "int", "object id"), new P("name", "string", "object name (to find it)"), new P("rename", "string", "new name"), new P("position", "vec3", "[x,y,z]"), new P("rotation", "vec3", "degrees"), new P("scale", "vec3", "[x,y,z]"),
                    new P("hidden", "bool", ""), new P("frozen", "bool", ""), new P("folder", "string", "outliner folder"), new P("parent", "int", "parent id, -1 = none"), new P("type", "string", "GTA object type"), new P("gta", "object", "GTA fields") },
            p =>
            {
                var o = Obj(p);
                Do("API: set " + o.Name, () =>
                {
                    if (Str(p, "rename") is { } rn) o.Name = rn;
                    if (Vec(p, "position") is { } pos) o.Position = pos;
                    if (Vec(p, "rotation") is { } rot) o.Rotation = FromEulerDeg(rot);
                    if (Vec(p, "scale") is { } sc) o.Scale = sc;
                    if (Arg(p, "hidden") != null) o.Hidden = Bool(p, "hidden");
                    if (Arg(p, "frozen") != null) o.Frozen = Bool(p, "frozen");
                    if (Str(p, "folder") is { } fo) o.Folder = fo;
                    if (Arg(p, "parent") != null) { int pid = Int(p, "parent", -1); Scene.SetParent(o, pid >= 0 ? Scene.Find(pid) : null); }
                    if (Str(p, "type") is { } ty && Enum.TryParse<GtaObjectType>(ty, true, out var gt)) SetGtaType(o, gt);
                    SetFields(o.Gta, Arg(p, "gta") as JsonObject);
                });
                return ObjInfo(o, true);
            });
        A.Add("object.delete", "Delete objects (ids list, or id / name).", new[] { new P("ids", "array", "object ids"), new P("id", "int", ""), new P("name", "string", "") }, p =>
        {
            var ids = Arg(p, "ids") is JsonArray a ? a.Select(x => (int)x.GetValue<double>()).ToHashSet() : new HashSet<int> { Obj(p).Id };
            Do("API: delete", () => { Scene.RemoveObjects(o => ids.Contains(o.Id)); Selected.ExceptWith(ids); });
            return ids.Count;
        });
        A.Add("object.duplicate", "Copy or instance an object (a Drawable brings its hierarchy). count copies, each moved by offset.",
            new[] { new P("id", "int", ""), new P("name", "string", ""), new P("instance", "bool", "share the mesh (GTA: same model placed again)"), new P("count", "int", "how many (default 1)"), new P("offset", "vec3", "move of each copy from the previous one") },
            p =>
            {
                var src = Obj(p); bool inst = Bool(p, "instance"); int count = Math.Clamp(Int(p, "count", 1), 1, 10000); var off = Vec(p, "offset") ?? Vector3.Zero;
                var made = new List<object>(); var cur = src;
                for (int i = 0; i < count; i++)
                {
                    Selected.Clear(); Selected.Add(cur.Id); ActiveId = cur.Id;
                    var lvl = Level; Level = SubObject.Object;
                    CloneSelection(inst);
                    Level = lvl;
                    var c = Current;
                    if (off != Vector3.Zero) { foreach (var d in Scene.Descendants(c)) d.Position += off; c.Position += off; }
                    made.Add(ObjInfo(c)); cur = c;
                }
                return made;
            });
        A.Add("object.make_unique", "Give an instance its own mesh.", new[] { new P("id", "int", ""), new P("name", "string", "") }, p => { var o = Obj(p); Do("API: make unique", () => Scene.MakeUnique(o)); return ObjInfo(o); });
        A.Add("object.instances", "Objects sharing this object's mesh.", new[] { new P("id", "int", ""), new P("name", "string", "") }, p => Scene.InstancesOf(Obj(p)).Select(o => ObjInfo(o)).ToList());

        // ---- selection / view
        A.Add("selection.get", "Selected object ids (first = active).", Array.Empty<P>(), _ => Selected.OrderBy(i => i == ActiveId ? 0 : 1).ToArray());
        A.Add("selection.set", "Select objects by id (empty = none).", new[] { new P("ids", "array", "object ids", true) }, p =>
        {
            var ids = ((JsonArray)Arg(p, "ids")).Select(x => (int)x.GetValue<double>()).Where(i => Scene.Find(i) != null).ToList();
            Selected.Clear(); foreach (var i in ids) Selected.Add(i); ActiveId = ids.FirstOrDefault(-1); SelectionChanged();
            return ids;
        });
        A.Add("view.frame", "Frame the selection (or everything) in the views.", Array.Empty<P>(), _ => { foreach (var vp in Viewports) FrameSelection(vp); return true; });
        RegisterSplatApi();
        A.Add("view.screenshot", "Save a PNG of the window at the end of this frame.", new[] { new P("path", "string", "PNG path", true) }, p => { apiShotPath = Str(p, "path"); return new { path = apiShotPath, note = "written after this frame" }; });

        A.Add("render.photo", "Photo render of the active view (path traced in the background). quality: 0 quick, 1 good, 2 best. light: 0 like the view, 1 daylight, 2 evening, 3 night, 4 studio. size: 0 like the view, 1 HD, 2 4K, 3 square. Returns at once; poll render.status until done, then the PNG is at 'output'.",
            new[] { new P("quality", "number", "0 quick, 1 good, 2 best"), new P("light", "number", "0 like the view, 1 daylight, 2 evening, 3 night, 4 studio"), new P("size", "number", "0 like the view, 1 HD, 2 4K, 3 square"), new P("brightness", "number", "exposure, -3..3") },
            p =>
            {
                if (Arg(p, "quality") != null) Settings.PhotoQuality = Math.Clamp(Int(p, "quality"), 0, 2);
                if (Arg(p, "light") != null) Settings.PhotoLight = Math.Clamp(Int(p, "light"), 0, 4);
                if (Arg(p, "size") != null) Settings.PhotoSize = Math.Clamp(Int(p, "size"), 0, 3);
                if (Arg(p, "brightness") != null) Settings.PhotoExposure = Math.Clamp(Num(p, "brightness"), -3, 3);
                bool ok = StartPhotoRender();
                return new { started = ok, output = photoOutput, error = photoError };
            });
        A.Add("render.status", "Progress of the photo render: running, sample / samples, done, output PNG, error.", Array.Empty<P>(),
            _ => new { running = PhotoRendering, sample = photoSample, samples = photoSamples, status = photoStatus, device = photoDevice, output = photoOutput, done = !PhotoRendering && photoOutput != null && File.Exists(photoOutput), error = photoError });

        // ---- mesh data
        A.Add("mesh.get", "Mesh data of an object: verts (flat x,y,z), faces (index lists), materials per face. evaluated = after the modifier stack.",
            new[] { new P("id", "int", ""), new P("name", "string", ""), new P("evaluated", "bool", "after modifiers"), new P("uv", "bool", "include UVs per face corner") },
            p =>
            {
                var o = Obj(p); var m = Bool(p, "evaluated") ? o.Evaluated() : o.Mesh;
                if (m.Verts.Count > 300000) throw new InvalidOperationException("Mesh too big for the API (over 300k vertices).");
                return new
                {
                    verts = m.Verts.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), faces = m.Faces.Select(f => f.V).ToArray(), materials = m.Faces.Select(f => f.MatId).ToArray(),
                    uv = Bool(p, "uv") ? m.Faces.Select(f => f.UV.SelectMany(u => new[] { u.X, u.Y }).ToArray()).ToArray() : null,
                };
            });

        // ---- modifiers
        A.Add("modifier.types", "Modifier types you can add.", Array.Empty<P>(), _ => Modifier.Catalog.Select(c => new { name = c.Name, category = c.Cat.ToString(), fields = c.Make().GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !BigFields.Contains(f.Name) && f.Name is not ("Name" or "Expanded")).Select(f => f.Name + ":" + f.FieldType.Name).ToArray() }).ToList());
        A.Add("modifier.list", "The modifier stack of an object, bottom (first) to top.", new[] { new P("id", "int", ""), new P("name", "string", "") }, p => Obj(p).FullStack.Select((m, i) => ModInfo(m, i)).ToList());
        A.Add("modifier.add", "Add a modifier (type = name from modifier.types, e.g. 'Mirror', 'Edit Poly', 'Unwrap UVW') with optional props, at index (default top).",
            new[] { new P("id", "int", ""), new P("name", "string", ""), new P("type", "string", "modifier type", true), new P("props", "object", "fields to set"), new P("index", "int", "position in the stack") },
            p =>
            {
                var o = Obj(p); var tn = Str(p, "type") ?? "";
                var c = Modifier.Catalog.FirstOrDefault(x => string.Equals(x.Name, tn, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name.Replace(" ", ""), tn.Replace(" ", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                if (c.Make == null) throw new InvalidOperationException("Unknown modifier type " + tn);
                var mod = c.Make(); SetFields(mod, Arg(p, "props") as JsonObject);
                int at = 0;
                Do("API: add " + mod.TypeName, () => { EndModifierEdit(o, false); mod.Name = ModifierStack.UniqueName(o.Modifiers, mod.Name); at = Math.Clamp(Int(p, "index", o.Modifiers.Count), 0, o.Modifiers.Count); o.Modifiers.Insert(at, mod); o.Touch(); });
                return ModInfo(mod, at);
            });
        A.Add("modifier.set", "Change fields of a modifier.", new[] { new P("id", "int", ""), new P("name", "string", ""), new P("index", "int", "stack index", true), new P("props", "object", "fields", true) }, p =>
        {
            var o = Obj(p); int i = Int(p, "index"); var m = o.FullStack[i];
            Do("API: set " + m.Name, () => { SetFields(m, Arg(p, "props") as JsonObject); o.Touch(); });
            return ModInfo(m, i);
        });
        A.Add("modifier.remove", "Remove a modifier.", new[] { new P("id", "int", ""), new P("name", "string", ""), new P("index", "int", "stack index", true) }, p =>
        {
            var o = Obj(p); int i = Int(p, "index"); EndModifierEdit(o, false);
            Do("API: remove modifier", () => { o.Modifiers.RemoveAt(i); o.Touch(); });
            return o.Modifiers.Count;
        });
        A.Add("modifier.move", "Move a modifier in the stack.", new[] { new P("id", "int", ""), new P("name", "string", ""), new P("from", "int", "", true), new P("to", "int", "", true) }, p =>
        {
            var o = Obj(p); EndModifierEdit(o, false); int f = Int(p, "from"), t = Math.Clamp(Int(p, "to"), 0, o.Modifiers.Count - 1);
            Do("API: move modifier", () => { var m = o.Modifiers[f]; o.Modifiers.RemoveAt(f); o.Modifiers.Insert(t, m); o.Touch(); });
            return o.Modifiers.Select((m, i) => ModInfo(m, i)).ToList();
        });
        A.Add("modifier.apply", "Apply (bake) the modifiers up to and including index into the mesh.", new[] { new P("id", "int", ""), new P("name", "string", ""), new P("index", "int", "stack index", true) }, p =>
        {
            var o = Obj(p); int i = Int(p, "index"); EndModifierEdit(o);
            Do("API: apply modifier", () => { var keep = o.Modifiers.Skip(i + 1).ToList(); var take = o.Modifiers.Take(i + 1).ToList(); o.Mesh = ModifierStack.Evaluate(o.Mesh, take, false, true).Clone(); o.Modifiers = keep; subSel.Remove(o.Id); o.Touch(); });
            return ObjInfo(o);
        });
        A.Add("modifier.edit_poly", "Run an Edit Poly operation inside an Edit Poly modifier: extrude / inset / bevel the faces whose normal is close to 'direction', or weld everything.",
            new[] { new P("id", "int", ""), new P("name", "string", ""), new P("index", "int", "Edit Poly index (added on top when missing)"), new P("op", "string", "extrude, inset, bevel, weld", true),
                    new P("direction", "vec3", "pick faces facing this way (default [0,0,1])"), new P("amount", "float", "height / inset / weld distance"), new P("outline", "float", "bevel outline") },
            p =>
            {
                var o = Obj(p); EndModifierEdit(o, false);
                int i = Int(p, "index", -1);
                if (i < 0 || i >= o.Modifiers.Count || o.Modifiers[i] is not EditPolyModifier)
                {
                    i = o.Modifiers.FindIndex(m => m is EditPolyModifier);
                    if (i < 0) Do("API: add Edit Poly", () => { o.Modifiers.Add(new EditPolyModifier()); o.Touch(); i = o.Modifiers.Count - 1; });
                }
                var dir = Vector3.Normalize(Vec(p, "direction") ?? Vector3.UnitZ); float amount = Num(p, "amount", 0.2f);
                int idx = i;
                Do("API: Edit Poly " + Str(p, "op"), () =>
                {
                    o.BeginModifierEdit(idx);
                    var m = o.Mesh;
                    var faces = Enumerable.Range(0, m.Faces.Count).Where(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.7f).ToHashSet();
                    switch ((Str(p, "op") ?? "").ToLowerInvariant())
                    {
                        case "extrude": MeshOps.ExtrudeFaces(m, faces, amount); break;
                        case "inset": MeshOps.InsetFaces(m, faces, amount); break;
                        case "bevel": MeshOps.BevelFaces(m, faces, amount, Num(p, "outline", -amount * 0.5f)); break;
                        case "weld": MeshOps.WeldByDistance(m, MathF.Max(amount, 1e-5f)); m.CleanFaces(); m.RemoveUnusedVertices(); break;
                        default: throw new InvalidOperationException("op: extrude, inset, bevel or weld");
                    }
                    m.Validate(); o.Touch();
                    o.CommitModifierEdit();
                });
                return ModInfo(o.Modifiers[idx], idx);
            });

        // ---- UVs
        A.Add("uv.unwrap", "Automatic UVs (seams, unfold, texel density, pack) in an Unwrap UVW modifier (added on top when missing).",
            new[] { new P("id", "int", ""), new P("name", "string", ""), new P("angle", "float", "seam angle in degrees (default 66)"), new P("density", "float", "texel density px/m (default 512)"), new P("texture_size", "int", "texture size (default 1024)") },
            p =>
            {
                var o = Obj(p); EndModifierEdit(o, false);
                int i = o.Modifiers.FindLastIndex(m => m is UnwrapUvwModifier);
                if (i < 0) { Do("API: add Unwrap UVW", () => { o.Modifiers.Add(new UnwrapUvwModifier()); o.Touch(); }); i = o.Modifiers.Count - 1; }
                var uw = (UnwrapUvwModifier)o.Modifiers[i];
                uw.AngleLimit = Num(p, "angle", uw.AngleLimit); uw.Density = Num(p, "density", uw.Density); uw.TextureSize = Int(p, "texture_size", uw.TextureSize);
                int charts = 0;
                UnwrapOp(o, i, uw, "API: Auto Unwrap", (m, faces, mod) => { var (n, seams) = UvUnfold.AutoUnwrap(m, mod.AngleLimit, faces); charts = n; mod.SetSeams(seams); UvTools.SetDensity(m, faces, mod.Density, mod.TextureSize); UvTools.Pack(m, faces); });
                return new { modifier = i, charts, seams = uw.Seams?.Length / 2 ?? 0 };
            });

        // ---- GTA
        A.Add("gta.check", "GTA / FiveM checks of the scene (errors, warnings, notes).", Array.Empty<P>(), _ => MoloMax.Core.Gta.Check.GtaChecker.Run(Scene).Select(i => new { level = i.Level.ToString(), message = i.Message, @object = i.ObjectId }).ToList());
        A.Add("gta.fix_all", "Fix what the GTA check can fix by itself.", Array.Empty<P>(), _ => { List<string> r = null; Do("API: fix all", () => r = MoloMax.Core.Gta.Check.GtaChecker.FixAll(Scene)); return r; });
        A.Add("gta.settings", "Read or change the project settings (ResourceName, OutputFolder, Heading, SharedPivot, MaxTextureSize, ...).", new[] { new P("set", "object", "fields to change") }, p =>
        {
            if (Arg(p, "set") is JsonObject s) Do("API: GTA settings", () => SetFields(Scene.Gta, s));
            return JsonSerializer.SerializeToNode(Scene.Gta, new JsonSerializerOptions { IncludeFields = true });
        });
        A.Add("gta.export", "Export the FiveM resource (ydr / ybn / ytyp / ymap, textures embedded) to folder (default: the project output folder).",
            new[] { new P("folder", "string", "output folder") },
            p =>
            {
                var folder = Str(p, "folder") ?? Scene.Gta.OutputFolder;
                if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("No output folder: give 'folder' or set gta.settings OutputFolder.");
                BakeAllPaint();
                var rep = GtaExporter.Export(Undo.SnapshotForSave(Scene), folder, LoadImageRgba, renderer.Textures.SearchFolders.ToList());
                return new { folder = rep.ResourceFolder, files = rep.Files, models = rep.Models, entities = rep.Entities, errors = rep.Errors, warnings = rep.Warnings, info = rep.Info };
            });
    }
}
