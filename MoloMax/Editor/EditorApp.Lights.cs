using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Light tab: every light of the scene, a full light editor (type, colour or temperature, power, size, range, cone,
/// shadows, volume, corona, hours) and the world (sun, sky, interior, exposure, tone curve, shadows).
/// Also feeds the realistic viewport: up to 32 scene lights per frame and a sun shadow map.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>Face orientation view: front faces blue, back faces (flipped normals) red.</summary>
    public bool FaceOrientation;
    /// <summary>Sun and sky multipliers of the viewport lighting.</summary>
    public float SunStrength = 1f, SkyStrength = 1f, ShadowStrength = 0.85f;
    Camera drawCam;
    Viewport drawViewport;
    int shadowHash;
    Matrix4x4 shadowVP = Matrix4x4.Identity;
    float shadowTexel;
    readonly List<ViewLight> viewLights = new();
    public int ViewLightCount => viewLights.Count;
    public bool ShadowsRendered { get; private set; }

    static Vector3 SettingColour(float[] c, Vector3 def) => c is { Length: >= 3 } ? new Vector3(c[0], c[1], c[2]) : def;

    GtaGlobalLights ScaledLights(GtaGlobalLights l)
    {
        l.LightDirColour *= new Vector4(SunStrength, SunStrength, SunStrength, 1);
        var k = new Vector4(SkyStrength, SkyStrength, SkyStrength, 1);
        l.NaturalAmbUp *= k; l.NaturalAmbDown *= k; l.LightDirAmbColour *= k;
        return l;
    }

    IEnumerable<Viewport> VisibleViewports() => Maximized ? new[] { Viewports[ActiveViewport] } : Viewports.Take(4);

    static bool CastsShadow(SceneObject o) =>
        !o.Hidden && o.Mesh.Faces.Count > 0 && o.GtaType is not (GtaObjectType.Collision or GtaObjectType.Portal or GtaObjectType.Light or GtaObjectType.Model or GtaObjectType.CollisionGroup or GtaObjectType.MloRoom);

    /// <summary>Once per frame, before the viewports: scene lights, sun shadow map, flags.</summary>
    void PrepareLighting()
    {
        renderer.FaceFront = SettingColour(Settings.FaceFrontColour, new(0.19f, 0.36f, 1f));
        renderer.FaceBack = SettingColour(Settings.FaceBackColour, new(1f, 0.12f, 0.12f));
        renderer.NoCull = FaceOrientation;
        var flags = RenderFlags.None;
        if (Settings.Filmic) flags |= RenderFlags.Filmic;
        if (FaceOrientation) flags |= RenderFlags.FaceOrientation;
        bool realistic = VisibleViewports().Any(v => v.Shading == ShadingMode.Realistic && !v.WireframeOnly);

        // scene lights, nearest to the active camera first
        viewLights.Clear();
        if (Settings.ViewportLights && realistic)
        {
            int hour = Math.Clamp((int)Hour, 0, 23);
            var eye = Viewports[ActiveViewport].Camera.Eye;
            foreach (var o in Scene.Objects)
            {
                if (o.GtaType != GtaObjectType.Light || o.Hidden) continue;
                var L = o.Gta.Light ??= new GtaLight();
                if (InteriorLight ? L.ExteriorOnly : L.InteriorOnly) continue;
                if (!InteriorLight && (L.TimeFlags & (1u << hour)) == 0) continue;
                var w = o.World;
                var dir = Vector3.TransformNormal(-Vector3.UnitZ, w);
                dir = dir.LengthSquared() > 1e-12f ? Vector3.Normalize(dir) : -Vector3.UnitZ;
                float scale = MathF.Max(o.Scale.X, MathF.Max(o.Scale.Y, o.Scale.Z));
                viewLights.Add(new ViewLight
                {
                    Position = Vector3.Transform(Vector3.Zero, w), Direction = dir,
                    Colour = L.EffectiveColour * L.Intensity * 2.0f, // like the game: colour x 2 x intensity, linear (the view tone-maps)
                    Range = L.Range, HalfLength = L.CapsuleLength * 0.5f * scale,
                    CosInner = MathF.Cos(MathF.Min(L.ConeInner, L.ConeOuter) * MathF.PI / 180f), CosOuter = MathF.Cos(MathF.Max(L.ConeInner, L.ConeOuter) * MathF.PI / 180f),
                    FalloffExponent = L.FalloffExponent, Radius = L.Radius,
                    Diffuse = L.DiffuseFactor, Specular = L.NoSpecular ? 0 : L.SpecularFactor,
                    Kind = L.Kind == GtaLightKind.Spot ? 1 : L.Kind == GtaLightKind.Capsule ? 2 : 0,
                });
            }
            if (viewLights.Count > SceneRenderer.MaxLights)
            {
                // the ones that matter most on screen: bright, far-reaching and close (RAGE's score), not just the nearest
                float Score(ViewLight l) => (l.Colour.Length() + 0.05f) * l.Range / MathF.Max(Vector3.Distance(l.Position, eye) - l.Range, 1f);
                viewLights.Sort((a, b) => Score(b).CompareTo(Score(a)));
                viewLights.RemoveRange(SceneRenderer.MaxLights, viewLights.Count - SceneRenderer.MaxLights);
            }
            if (viewLights.Count > 0) flags |= RenderFlags.SceneLights;
        }

        // sun shadows: an orthographic depth map from the sun over the visible scene, re-rendered when something moved
        var gl = ScaledLights(Lights);
        ShadowsRendered = false;
        if (Settings.SunShadows && realistic && !InteriorLight && gl.LightDir.Z > 0.05f && ShadowStrength > 0)
        {
            if (MapMode) { MapShadows(gl); MapCascades(gl); flags |= RenderFlags.SunShadows; ShadowsRendered = true; renderer.SetLights(viewLights, flags, shadowVP, shadowTexel, ShadowStrength); return; }
            CascadesOff();
            var casters = Scene.Objects.Where(CastsShadow).ToList();
            if (casters.Count > 0)
            {
                var h = new HashCode();
                h.Add(gl.LightDir); h.Add(casters.Count); h.Add(renderer.EditObjectId);
                foreach (var o in casters) { var ow = o.World; h.Add(o.Version); h.Add(ow.Translation); h.Add(ow.M11); h.Add(ow.M22); h.Add(ow.M12); }
                int hash = h.ToHashCode();
                if (hash != shadowHash)
                {
                    var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                    foreach (var o in casters) { var (a, b) = o.FastWorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
                    var c = (mn + mx) * 0.5f; float r = MathF.Max((mx - mn).Length() * 0.5f, 1f);
                    if (r > 160) { c = Viewports[ActiveViewport].Camera.Target; r = 160; }
                    var up = MathF.Abs(gl.LightDir.Z) > 0.98f ? Vector3.UnitY : Vector3.UnitZ;
                    var view = Matrix4x4.CreateLookAt(c + gl.LightDir * r * 2, c, up);
                    var proj = Matrix4x4.CreateOrthographicOffCenter(-r, r, -r, r, 0.05f, r * 4);
                    shadowVP = view * proj;
                    shadowTexel = 2 * r / SceneRenderer.ShadowSize;
                    renderer.RenderShadows(Scene, casters, shadowVP, gl);
                    shadowHash = hash;
                }
                flags |= RenderFlags.SunShadows;
                ShadowsRendered = true;
            }
        }
        else shadowHash = 0;
        renderer.SetLights(viewLights, flags, shadowVP, shadowTexel, ShadowStrength);
    }

    // ---------------------------------------------------------------------------------- Light tab

    void LightTabProps()
    {
        var lights = Scene.Objects.Where(o => o.GtaType == GtaObjectType.Light).ToList();
        if (Ui.Header("Add light", Open))
        {
            float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
            if (ImGui.Button("Point", new Vector2(w3, 0))) AddLight(GtaLightKind.Point); ImGui.SameLine(0, 4);
            if (ImGui.Button("Spot", new Vector2(w3, 0))) AddLight(GtaLightKind.Spot); ImGui.SameLine(0, 4);
            if (ImGui.Button("Capsule", new Vector2(w3, 0))) AddLight(GtaLightKind.Capsule);
            Theme.Tip("GTA capsule light: a tube of light (neon, strip light).");
            Ui.TextDisabled("The three GTA / Sollumz light types, exported as they are (.ydr and CodeWalker XML for Sollumz).");
        }
        if (Ui.Header($"Scene lights ({lights.Count})", Open))
        {
            if (lights.Count == 0) Ui.TextDisabled("No light yet. Add one above: it goes under the selected Drawable, or at the 3D cursor.");
            else
            {
                ImGui.BeginChild("##lightlist", new Vector2(-1, MathF.Min(lights.Count, 7) * ImGui.GetTextLineHeightWithSpacing() + 8), ImGuiChildFlags.Borders);
                foreach (var l in lights)
                {
                    var L = l.Gta.Light ??= new GtaLight();
                    ImGui.PushID(l.Id);
                    bool vis = !l.Hidden;
                    if (ImGui.Checkbox("##vis", ref vis)) { l.Hidden = !vis; Dirty = true; }
                    Theme.Tip("Light on / off in the viewport (hidden lights are not exported)");
                    ImGui.SameLine();
                    var p = ImGui.GetCursorScreenPos();
                    ImGui.GetWindowDrawList().AddCircleFilled(p + new Vector2(6, 9), 5, ImGui.ColorConvertFloat4ToU32(new Vector4(L.EffectiveColour, 1)));
                    ImGui.Dummy(new Vector2(14, 0)); ImGui.SameLine();
                    string kind = L.Kind == GtaLightKind.Capsule ? "area" : L.Kind.ToString().ToLowerInvariant();
                    if (ImGui.Selectable($"{l.Name}  ·  {kind}  ·  {L.Intensity:0.#}", Selected.Contains(l.Id)))
                    {
                        if (!Input.Ctrl) Selected.Clear();
                        Selected.Add(l.Id); ActiveId = l.Id; SelectionChanged();
                    }
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) FrameSelection();
                    ImGui.PopID();
                }
                ImGui.EndChild();
                Ui.TextDisabled("Double-click a light to frame it. Ctrl+click adds to the selection.");
            }
        }
        var sel = SelObjects().Where(o => o.GtaType == GtaObjectType.Light).ToList();
        if (sel.Count > 0 && Ui.Header(sel.Count == 1 ? "Light: " + sel[0].Name : $"Light: {sel.Count} selected", Open)) LightEditor(sel);
        WorldSettings();
    }

    /// <summary>Full light settings, applied to every selected light (shared by the Light tab and the GTA tab).</summary>
    void LightEditor(List<SceneObject> lights)
    {
        if (lights.Count == 0) return;
        var main = Current is { GtaType: GtaObjectType.Light } c0 ? c0 : lights[0];
        var L = main.Gta.Light ??= new GtaLight();
        void All(Action<GtaLight> f) { foreach (var x in lights) f(x.Gta.Light ??= new GtaLight()); Dirty = true; }
        float lw = 118;
        void Row(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }

        // type
        float w3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
        var kinds = new[] { (GtaLightKind.Point, "Point"), (GtaLightKind.Spot, "Spot"), (GtaLightKind.Capsule, "Capsule") };
        foreach (var (k, name) in kinds)
        {
            if (k != GtaLightKind.Point) ImGui.SameLine(0, 4);
            if (Theme.Toggle(name + "##lk", L.Kind == k, new Vector2(w3, 0))) All(l => l.Kind = k);
        }

        // colour
        Ui.Label("Colour", Theme.TextDim);
        bool temp = L.UseTemperature;
        if (ImGui.Checkbox("Temperature", ref temp)) All(l => l.UseTemperature = temp);
        Theme.Tip("Blackbody colour: 1900 K candle, 2700 K warm bulb, 4000 K neutral, 5500 K daylight, 6500 K+ cold.");
        if (L.UseTemperature)
        {
            ImGui.SameLine();
            float t = L.Temperature; ImGui.SetNextItemWidth(-30);
            if (ImGui.SliderFloat("##kelvin", ref t, 1000, 12000, "%.0f K")) All(l => l.Temperature = t);
            ImGui.SameLine();
            var p = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(22, ImGui.GetFrameHeight()), ImGui.ColorConvertFloat4ToU32(new Vector4(GtaLight.Kelvin(L.Temperature), 1)), 3);
            ImGui.Dummy(new Vector2(22, ImGui.GetFrameHeight()));
            float bw = (ImGui.GetContentRegionAvail().X - 12) / 4;
            foreach (var (k, n) in new[] { (1900f, "Candle"), (2700f, "Bulb"), (4000f, "Neutral"), (6500f, "Cold") })
            {
                if (k != 1900f) ImGui.SameLine(0, 4);
                if (ImGui.Button(n + "##kp", new Vector2(bw, 0))) All(l => l.Temperature = k);
            }
        }
        else
        {
            var c = L.Colour; ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorEdit3("##lcol", ref c)) All(l => l.Colour = c);
        }

        // power
        Ui.Label("Power", Theme.TextDim);
        float v = L.Intensity; Row("Intensity"); if (ImGui.DragFloat("##lint", ref v, 0.1f, 0, 500, "%.2f")) All(l => l.Intensity = v);
        v = L.Radius; Row("Radius"); if (ImGui.DragFloat("##lrad", ref v, 0.005f, 0, 5, "%.3f m")) All(l => l.Radius = v);
        Theme.Tip("Size of the source: bigger = softer, wider highlights.");
        v = L.Range; Row("Range"); if (ImGui.DragFloat("##lrange", ref v, 0.05f, 0.1f, 200, "%.2f m")) All(l => l.Range = v);
        Theme.Tip("Where the light stops (GTA falloff distance).");
        v = L.FalloffExponent; Row("Falloff"); if (ImGui.DragFloat("##lfall", ref v, 0.1f, 0.1f, 128, "%.1f")) All(l => l.FalloffExponent = v);
        Theme.Tip("GTA falloff exponent. Higher = the light fades sooner, darker in the middle of its range.");

        if (L.Kind == GtaLightKind.Spot)
        {
            Ui.Label("Spot shape", Theme.TextDim);
            v = L.ConeOuter * 2; Row("Spot size"); if (ImGui.SliderFloat("##lsize", ref v, 2, 178, "%.0f°")) All(l => { float blend = l.SpotBlend; l.ConeOuter = v / 2; l.ConeInner = l.ConeOuter * (1 - blend); });
            v = L.SpotBlend; Row("Blend"); if (ImGui.SliderFloat("##lblend", ref v, 0, 1, "%.2f")) All(l => l.ConeInner = l.ConeOuter * (1 - v));
            Ui.TextDisabled("Spots shine along the light's -Z: rotate it to aim.");
        }
        if (L.Kind == GtaLightKind.Capsule)
        {
            v = L.CapsuleLength; Row("Length"); if (ImGui.DragFloat("##llen", ref v, 0.05f, 0.05f, 50, "%.2f m")) All(l => l.CapsuleLength = v);
        }

        Ui.Label("Contribution", Theme.TextDim);
        v = L.DiffuseFactor; Row("Diffuse"); if (ImGui.SliderFloat("##ldiff", ref v, 0, 2, "%.2f")) All(l => l.DiffuseFactor = v);
        v = L.SpecularFactor; Row("Specular"); if (ImGui.SliderFloat("##lspec", ref v, 0, 2, "%.2f")) All(l => l.SpecularFactor = v);
        Theme.Tip("Viewport preview only. In game, use No specular to switch the highlight off.");
        bool b = L.NoSpecular; if (ImGui.Checkbox("No specular (game)", ref b)) All(l => l.NoSpecular = b);

        Ui.Label("Shadows & volume", Theme.TextDim);
        b = L.CastShadows; if (ImGui.Checkbox("Cast shadows", ref b)) All(l => l.CastShadows = b);
        b = L.DrawVolume; if (ImGui.Checkbox("Volumetric (light shafts)", ref b)) All(l => l.DrawVolume = b);
        if (L.DrawVolume)
        {
            v = L.VolumeIntensity; Row("Volume"); if (ImGui.SliderFloat("##lvol", ref v, 0, 2, "%.2f")) All(l => l.VolumeIntensity = v);
            v = L.VolumeSize; Row("Volume size"); if (ImGui.SliderFloat("##lvols", ref v, 0.1f, 4, "%.2f")) All(l => l.VolumeSize = v);
        }

        Ui.Label("Corona & flicker", Theme.TextDim);
        v = L.CoronaSize; Row("Corona size"); if (ImGui.DragFloat("##lcs", ref v, 0.01f, 0, 10, "%.2f")) All(l => l.CoronaSize = v);
        v = L.CoronaIntensity; Row("Corona power"); if (ImGui.DragFloat("##lci", ref v, 0.05f, 0, 50, "%.2f")) All(l => l.CoronaIntensity = v);
        int fl = L.Flashiness; Row("Flashiness"); if (ImGui.SliderInt("##lfl", ref fl, 0, 22)) All(l => l.Flashiness = (byte)fl);
        Theme.Tip("0 = steady. Other values are GTA's flicker / strobe patterns.");

        Ui.Label("Where & when", Theme.TextDim);
        b = L.InteriorOnly; if (ImGui.Checkbox("Interior only", ref b)) All(l => { l.InteriorOnly = b; if (b) l.ExteriorOnly = false; });
        ImGui.SameLine();
        b = L.ExteriorOnly; if (ImGui.Checkbox("Exterior only", ref b)) All(l => { l.ExteriorOnly = b; if (b) l.InteriorOnly = false; });
        // 24 hour toggles
        float hw = (ImGui.GetContentRegionAvail().X - 11 * 2) / 12;
        for (int hr = 0; hr < 24; hr++)
        {
            if (hr % 12 != 0) ImGui.SameLine(0, 2);
            bool on = (L.TimeFlags & (1u << hr)) != 0;
            int hh = hr;
            if (Theme.Toggle($"{hr}##h{hr}", on, new Vector2(hw, 0))) All(l => l.TimeFlags ^= 1u << hh);
        }
        float b3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
        if (ImGui.Button("Always", new Vector2(b3, 0))) All(l => l.TimeFlags = 0xFFFFFF); ImGui.SameLine(0, 4);
        if (ImGui.Button("Night", new Vector2(b3, 0))) All(l => l.TimeFlags = NightHours()); ImGui.SameLine(0, 4);
        if (ImGui.Button("Day", new Vector2(b3, 0))) All(l => l.TimeFlags = 0xFFFFFFu & ~NightHours());
        Ui.TextDisabled("Hours the light is on in game (the viewport follows the Sun hour).");

        // everything else the game stores for a light, named like Sollumz (exported as is)
        if (ImGui.TreeNodeEx("Sollumz / game properties", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            int iv;
            iv = L.GroupId; Row("Group ID"); if (ImGui.InputInt("##lgid", ref iv)) All(l => l.GroupId = (byte)Math.Clamp(iv, 0, 255));
            iv = L.LightHash; Row("Light hash"); if (ImGui.InputInt("##lhash", ref iv)) All(l => l.LightHash = (byte)Math.Clamp(iv, 0, 255));
            Ui.Label("Fade distances (m, 0 = default)", Theme.TextDim);
            iv = L.LightFadeDistance; Row("Light"); if (ImGui.SliderInt("##lfd", ref iv, 0, 255)) All(l => l.LightFadeDistance = (byte)iv);
            iv = L.ShadowFadeDistance; Row("Shadow"); if (ImGui.SliderInt("##lsfd", ref iv, 0, 255)) All(l => l.ShadowFadeDistance = (byte)iv);
            iv = L.SpecularFadeDistance; Row("Specular"); if (ImGui.SliderInt("##lspfd", ref iv, 0, 255)) All(l => l.SpecularFadeDistance = (byte)iv);
            iv = L.VolumetricFadeDistance; Row("Volumetric"); if (ImGui.SliderInt("##lvfd", ref iv, 0, 255)) All(l => l.VolumetricFadeDistance = (byte)iv);
            Ui.Label("Shadow", Theme.TextDim);
            iv = L.ShadowBlur; Row("Shadow blur"); if (ImGui.SliderInt("##lsb", ref iv, 0, 255)) All(l => l.ShadowBlur = (byte)iv);
            v = L.ShadowNearClip; Row("Near clip"); if (ImGui.DragFloat("##lsnc", ref v, 0.005f, 0, 10, "%.3f m")) All(l => l.ShadowNearClip = v);
            Ui.Label("Corona / volume", Theme.TextDim);
            v = L.CoronaZBias; Row("Corona Z bias"); if (ImGui.DragFloat("##lczb", ref v, 0.01f, 0, 10, "%.2f")) All(l => l.CoronaZBias = v);
            var oc = L.VolumeOuterColour; Row("Volume outer"); if (ImGui.ColorEdit3("##lvoc", ref oc)) All(l => l.VolumeOuterColour = oc);
            v = L.VolumeOuterIntensity; Row("Outer intensity"); if (ImGui.DragFloat("##lvoi", ref v, 0.01f, 0, 10, "%.2f")) All(l => l.VolumeOuterIntensity = v);
            v = L.VolumeOuterExponent; Row("Outer exponent"); if (ImGui.DragFloat("##lvoe", ref v, 0.05f, 0, 64, "%.2f")) All(l => l.VolumeOuterExponent = v);
            Ui.Label("Culling plane", Theme.TextDim);
            var cn = L.CullingPlaneNormal; Row("Normal"); if (ImGui.DragFloat3("##lcpn", ref cn, 0.01f, -1, 1)) All(l => l.CullingPlaneNormal = cn);
            v = L.CullingPlaneOffset; Row("Offset"); if (ImGui.DragFloat("##lcpo", ref v, 0.01f, -100, 100, "%.2f")) All(l => l.CullingPlaneOffset = v);
            string pt = L.ProjectedTexture ?? ""; Row("Projected tex"); if (ImGui.InputText("##lpt", ref pt, 64)) All(l => l.ProjectedTexture = pt);
            Theme.Tip("Texture a spot projects (name in a .ytd), with the Texture projection flag");
            if (ImGui.TreeNodeEx("Flags", ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                uint fl0 = L.Flags;
                ImGui.TextDisabled($"value {fl0}");
                foreach (var (bit, name) in GtaLight.FlagNames)
                {
                    bool on = (fl0 & bit) != 0;
                    bool main2 = (bit & GtaLight.MainFlagBits) != 0;
                    if (main2) ImGui.BeginDisabled();
                    if (ImGui.Checkbox(name + "##lf" + bit, ref on)) All(l => l.ExtraFlags = on ? l.ExtraFlags | bit : l.ExtraFlags & ~bit);
                    if (main2) { ImGui.EndDisabled(); Theme.Tip("Set with the checkboxes above"); }
                }
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
    }

    /// <summary>Sun, sky, interior, exposure, tone curve and shadows of the realistic view.</summary>
    void WorldSettings()
    {
        if (!Ui.Header("World & rendering", Open)) return;
        float lw = 118;
        void Row(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }
        float b4 = (ImGui.GetContentRegionAvail().X - 12) / 4;
        if (ImGui.Button("Noon", new Vector2(b4, 0))) { InteriorLight = false; Hour = 13; } ImGui.SameLine(0, 4);
        if (ImGui.Button("Sunset", new Vector2(b4, 0))) { InteriorLight = false; Hour = 19.2f; } ImGui.SameLine(0, 4);
        if (ImGui.Button("Night##2", new Vector2(b4, 0))) { InteriorLight = false; Hour = 23; } ImGui.SameLine(0, 4);
        if (ImGui.Button("Interior", new Vector2(b4, 0))) InteriorLight = true;
        ImGui.Checkbox("Interior lighting (no sun)", ref InteriorLight);
        if (!InteriorLight)
        {
            Row("Sun hour"); ImGui.SliderFloat("##whour", ref Hour, 0, 24, "%.1f h");
            Row("Sun strength"); ImGui.SliderFloat("##wsun", ref SunStrength, 0, 3, "%.2f");
            Row("Sky strength"); ImGui.SliderFloat("##wsky", ref SkyStrength, 0, 3, "%.2f");
        }
        Row("Interior ambient"); ImGui.SliderFloat("##wint", ref InteriorLevel, 0, 1.5f, "%.2f");
        Theme.Tip("Artificial ambient: what the green vertex colour receives.");
        Row("Exposure"); ImGui.SliderFloat("##wexp", ref Exposure, 0.1f, 4, "%.2f");
        Row("Fill light"); ImGui.SliderFloat("##wfill", ref AmbientBoost, 0, 0.6f, "%.2f");
        Theme.Tip("Keeps the dark areas readable while modelling. 0 = what the game shows.");
        bool s = Settings.SunShadows;
        if (ImGui.Checkbox("Sun shadows", ref s)) { Settings.SunShadows = s; shadowHash = 0; Settings.Save(); }
        if (Settings.SunShadows) { ImGui.SameLine(); ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##wsh", ref ShadowStrength, 0, 1, "strength %.2f"); }
        s = Settings.ViewportLights;
        if (ImGui.Checkbox($"Scene lights in the view ({viewLights.Count} lit)", ref s)) { Settings.ViewportLights = s; Settings.Save(); }
        Theme.Tip($"Up to {SceneRenderer.MaxLights} lights at once (the nearest to the camera). Interior / exterior only and hours are respected.");
        s = Settings.Filmic;
        if (ImGui.Checkbox("Filmic tone curve", ref s)) { Settings.Filmic = s; Settings.Save(); }
        Theme.Tip("Soft highlights instead of a hard white clip, like a camera.");
        Ui.TextDisabled("These settings shape the Realistic view (the game uses its own timecycle).");
    }

    double mapShadowTime;
    /// <summary>Map mode: shadows of what is around the camera only (120 m), refreshed at most every 0.75 s.</summary>
    // MAP: two wider sun shadow maps around the near one (600 m and 2.5 km), refreshed when the focus moved far enough
    readonly int[] cascHash = new int[3];
    readonly Matrix4x4[] cascVP = new Matrix4x4[3];
    readonly float[] cascTexel = new float[3];
    int cascCount = -1; double cascCheck;
    static readonly float[] CascRadius = { 0, 600, 2500 };
    static readonly float[] CascSnap = { 0, 60, 250 };

    void CascadesOff()
    {
        if (cascCount == 0) return;
        cascCount = 0; Array.Clear(cascHash); renderer.SetCascades(Matrix4x4.Identity, 0, Matrix4x4.Identity, 0, 0);
    }

    void MapCascades(GtaGlobalLights gl)
    {
        if (!LodTreeOn) { CascadesOff(); return; }
        var c = MapFocus();
        double now = ImGui.GetTime();
        if (now - cascCheck < 0.4 && cascCount == 2) return;
        cascCheck = now;
        // one wide map refreshed per frame at most (each is a full pass over the casters around)
        for (int k = 1; k <= 2; k++)
        {
            float r = CascRadius[k], sn = CascSnap[k];
            var snapped = new Vector3(MathF.Round(c.X / sn) * sn, MathF.Round(c.Y / sn) * sn, MathF.Round(c.Z / sn) * sn);
            long ver = 0;
            var wl = WorldShadowCasters(snapped, r, ref ver, r / 400);
            int hash = HashCode.Combine(gl.LightDir, snapped, wl.Count, ver);
            if (hash == cascHash[k]) continue;
            var up = MathF.Abs(gl.LightDir.Z) > 0.98f ? Vector3.UnitY : Vector3.UnitZ;
            var view = Matrix4x4.CreateLookAt(snapped + gl.LightDir * r * 2, snapped, up);
            var proj = Matrix4x4.CreateOrthographicOffCenter(-r, r, -r, r, 0.05f, r * 4);
            cascVP[k] = view * proj; cascTexel[k] = 2 * r / SceneRenderer.ShadowSize;
            bool complete = renderer.RenderShadows(Scene, Array.Empty<SceneObject>(), cascVP[k], gl, 6, wl, k);
            cascHash[k] = complete ? hash : 0;
            break;
        }
        cascCount = 2;
        renderer.SetCascades(cascVP[1], cascTexel[1], cascVP[2], cascTexel[2], cascVP[2] != default ? 2 : cascVP[1] != default ? 1 : 0);
    }

    void MapShadows(GtaGlobalLights gl)
    {
        var c = MapFocus(); const float r = 120;
        var snapped = new Vector3(MathF.Round(c.X / 20) * 20, MathF.Round(c.Y / 20) * 20, MathF.Round(c.Z / 20) * 20);
        var casters = new List<SceneObject>();
        long ver = 0;
        foreach (var o in MapLive()) // within draw distance only (tens of thousands of streamed entities otherwise)
        {
            if (!CastsShadow(o)) continue;
            var (mn, mx) = o.FastWorldBounds();
            if (mx.X < snapped.X - r || mn.X > snapped.X + r || mx.Y < snapped.Y - r || mn.Y > snapped.Y + r) continue;
            casters.Add(o); ver += o.Version + (long)(o.Position.X * 7 + o.Position.Y * 13 + o.Position.Z * 17);
        }
        // the level-of-detail world: the chosen objects near the focus, grouped by model (one instanced draw each)
        var wl = WorldShadowCasters(snapped, r, ref ver);
        int hash = HashCode.Combine(gl.LightDir, casters.Count, ver, snapped, wl.Count);
        // while maps / models are still arriving the casters change all the time: refresh less often (each refresh is a heavy pass)
        bool streaming = mapStreamQueue.Count > 0 || mapLoadTask != null || mapLoadQueue.Count > 0 || archLoading > 0;
        if (hash == shadowHash || ImGui.GetTime() - mapShadowTime < (streaming ? 3.0 : 0.75)) return;
        var up = MathF.Abs(gl.LightDir.Z) > 0.98f ? Vector3.UnitY : Vector3.UnitZ;
        var view = Matrix4x4.CreateLookAt(snapped + gl.LightDir * r * 2, snapped, up);
        var proj = Matrix4x4.CreateOrthographicOffCenter(-r, r, -r, r, 0.05f, r * 4);
        shadowVP = view * proj;
        shadowTexel = 2 * r / SceneRenderer.ShadowSize;
        // at most ~10 ms of new meshes per refresh: the rest of the casters arrive over the next refreshes (no 400 ms hitch)
        bool complete = renderer.RenderShadows(Scene, casters, shadowVP, gl, 10, wl);
        shadowHash = complete ? hash : 0; mapShadowTime = complete ? ImGui.GetTime() : ImGui.GetTime() - 10;
    }
}
