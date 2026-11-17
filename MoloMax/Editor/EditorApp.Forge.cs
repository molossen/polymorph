using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Forge;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Weapon and Vehicle tabs: a model becomes a GTA weapon or a FiveM vehicle by cloning a game one (the donor):
/// the donor shows as a ghost with its bones, the model is fitted on it automatically, the export writes a ready resource.
/// </summary>
public sealed partial class EditorApp
{
    const string ForgeFolder = "Forge donor";
    int weaponDonor = 7; // carbine rifle
    string weaponName = "molo_rifle";
    bool weaponReplace, weaponMatchSize = true;
    List<ForgeBone> weaponBones;
    Vector3? weaponMuzzle;
    ForgeReport forgeReport; PropTab forgeReportTab;
    string forgeOut;

    string vehicleDonorName = "sultan2", vehicleName = "molo_car", vehicleLabel = "Molo car";
    VehicleForge.Donor vehicleDonor;
    bool vehicleFrontPositive = true, vehicleKeepProportions, vehicleDonorWheels = true;
    Dictionary<string, Vector3> vehicleWheels;
    List<(Vector3 Min, Vector3 Max)> vehicleWheelBoxes;

    string ForgeOutputFolder() => forgeOut ??= Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "base_para_testes", "resources"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "base_para_testes", "resources", "[polymorph]")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "FiveM");

    /// <summary>The objects the forge works on: the selection, without the donor ghost.</summary>
    List<SceneObject> ForgeSelection() => SelObjects().Where(o => o.Folder != ForgeFolder && o.Mesh.Faces.Count > 0 && o.GtaType != GtaObjectType.Collision).ToList();

    /// <summary>A new or opened scene: the donor, its bones, the fit and the last report belong to the old one.</summary>
    void ResetForge()
    {
        weaponBones = null; weaponMuzzle = null; forgeReport = null;
        vehicleDonor = null; vehicleWheels = null; vehicleWheelBoxes = null;
        pedBody = null;
    }

    bool ForgeNeedsGame()
    {
        if (game != null) return false;
        Ui.TextWrapped("The donors come from your GTA V files.");
        if (gameTask != null && !gameTask.IsCompleted) Ui.TextDisabled("Loading the game files...");
        else if (ImGui.Button("Load the game files", new Vector2(-1, 0)))
        {
            if (MoloMax.Core.Gta.Game.GameFiles.LooksLikeGta(Settings.GtaFolder, out _)) StartGameLoad(Settings.GtaFolder); else SetStatus("Set the GTA V folder in the Scene tab first.");
        }
        return true;
    }

    /// <summary>The donor model in the scene at the origin, as a frozen ghost (not exported).</summary>
    List<SceneObject> ImportDonorGhost(string model)
    {
        Scene.Objects.RemoveAll(o => o.Folder == ForgeFolder);
        var a = game.FindModel(MoloMax.Core.Gta.Map.MapDocument.Hash(model));
        if (a == null) { SetStatus(model + " is not in the game files."); return new(); }
        var dir = Path.Combine(Path.GetTempPath(), "polymorph_donor");
        var objs = ImportFile(game.Extract(a, dir), Vector3.Zero);
        importReportOpen = false; // the donor is a reference, not an import to review
        if (!Scene.Folders.Contains(ForgeFolder)) Scene.Folders.Add(ForgeFolder);
        foreach (var o in objs.Concat(objs.Select(Scene.Parent).Where(p => p != null)).Distinct().ToList())
        {
            o.Folder = ForgeFolder; o.Frozen = true; o.Gta.Role = GtaRole.Ignore; o.WireColour = new Vector3(1f, 0.55f, 0.15f);
            if (o.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup) o.Hidden = true;
        }
        Selected.Clear(); SelectionChanged();
        return objs;
    }

    static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> pts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    /// <summary>Applies a world transform to objects (their parts keep their place relative to each other).</summary>
    void ApplyWorld(List<SceneObject> objs, Matrix4x4 m, string undo)
    {
        Do(undo, () =>
        {
            foreach (var o in objs)
            {
                var w = o.World * m;
                if (Matrix4x4.Decompose(w, out var s, out var r, out var t)) { o.Position = t; o.Rotation = Quaternion.Normalize(r); o.Scale = s; }
                o.Touch();
            }
        });
    }

    // ------------------------------------------------------------------------------------------ weapons

    void WeaponProps()
    {
        Ui.Header("Weapon Forge");
        Ui.TextWrapped("Turn any 3D model into a GTA weapon: it takes the skeleton of a game weapon (grip, muzzle, attachments).");
        if (ForgeNeedsGame()) return;
        var labels = WeaponForge.Donors.Select(d => d.Label).ToArray();
        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Donor", ref weaponDonor, labels, labels.Length)) { weaponBones = null; weaponMuzzle = null; }
        var donor = WeaponForge.Donors[weaponDonor];
        if (ImGui.Button("1. Show the donor", new Vector2(-1, 0))) LoadWeaponDonor();
        TutRect("wf_donor");
        Theme.Tip("Brings the game weapon in as an orange ghost at the origin, with its bones.");
        var sel = ForgeSelection();
        ImGui.Checkbox("Same size as the donor", ref weaponMatchSize);
        ImGui.BeginDisabled(weaponBones == null || sel.Count == 0);
        if (ImGui.Button("2. Fit my model on it", new Vector2(-1, 0))) FitWeapon(sel);
        TutRect("wf_fit");
        ImGui.EndDisabled();
        Theme.Tip("Select your model: its barrel (the thin end) goes on the donor's muzzle, same length, centred. Fine-tune with the gizmo.");
        if (weaponMuzzle is { } mz)
        {
            var v = mz; ImGui.SetNextItemWidth(-90);
            if (ImGui.DragFloat3("Muzzle", ref v, 0.002f)) weaponMuzzle = v;
            Theme.Tip("Where the bullets and the flash come out (the gun_muzzle bone).");
        }
        ImGui.Separator();
        ImGui.RadioButton("Add-on weapon", !weaponReplace); if (ImGui.IsItemClicked()) weaponReplace = false;
        ImGui.SameLine(); ImGui.RadioButton($"Replace the {donor.Label.ToLowerInvariant()}", weaponReplace); if (ImGui.IsItemClicked()) weaponReplace = true;
        if (!weaponReplace) { ImGui.SetNextItemWidth(-90); ImGui.InputText("Name", ref weaponName, 40); }
        ImGui.BeginDisabled(sel.Count == 0);
        if (ImGui.Button("3. Make the weapon", new Vector2(-1, 30))) ExportWeapon(sel);
        TutRect("wf_make");
        ImGui.EndDisabled();
        Theme.Tip(weaponReplace ? "Streams your model under the donor's name: every one in game becomes yours." : "A new weapon with its metas cloned from the donor (stats, animations), fxmanifest and a /give command.");
        ForgeOutputUi();
        ForgeReportUi();
    }

    void LoadWeaponDonor()
    {
        var d = WeaponForge.Donors[weaponDonor];
        try { weaponBones = WeaponForge.DonorBones(game, d); } catch (Exception e) { SetStatus(e.Message); return; }
        ImportDonorGhost(d.Model);
        FrameAll();
        SetStatus($"{d.Label}: {weaponBones.Count} bones. Select your model, then Fit.");
    }

    void FitWeapon(List<SceneObject> sel)
    {
        var d = WeaponForge.Donors[weaponDonor];
        var donorObjs = Scene.Objects.Where(o => o.Folder == ForgeFolder && !o.Hidden && o.Mesh.Faces.Count > 0).ToList();
        var (dmn, dmx) = Bounds(donorObjs.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))));
        var muzzle = weaponBones.FirstOrDefault(b => b.Name != null && b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase))?.Position ?? new Vector3(dmx.X, (dmn.Y + dmx.Y) / 2, (dmn.Z + dmx.Z) / 2);
        var pts = sel.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))).ToList();
        var (fit, mz) = WeaponForge.Fit(pts, dmn, dmx, muzzle, weaponMatchSize);
        ApplyWorld(sel, fit, "Fit on the donor weapon");
        weaponMuzzle = mz;
        SetStatus($"Fitted on the {d.Label.ToLowerInvariant()}: muzzle at {mz.X:0.000}, {mz.Y:0.000}, {mz.Z:0.000}.");
    }

    void ExportWeapon(List<SceneObject> sel)
    {
        BakeAllPaint();
        var o = new WeaponForge.Options { Donor = WeaponForge.Donors[weaponDonor], Name = weaponName, Replace = weaponReplace, Muzzle = weaponMuzzle };
        forgeReportTab = PropTab.Weapon;
        forgeReport = WeaponForge.Export(game, Scene, sel, o, ForgeOutputFolder(), LoadImageRgba, renderer.Textures.SearchFolders.ToList());
        SetStatus(forgeReport.Ok ? "Weapon written to " + forgeReport.Folder : "Weapon: " + string.Join(" | ", forgeReport.Errors));
    }

    // ------------------------------------------------------------------------------------------ vehicles

    void VehicleProps()
    {
        Ui.Header("Vehicle Forge");
        Ui.TextWrapped("Turn a 3D car into a FiveM vehicle: it takes the skeleton, wheels, collision and handling of a game car.");
        if (ForgeNeedsGame()) return;
        ImGui.SetNextItemWidth(-90);
        if (ImGui.BeginCombo("Donor##2", vehicleDonorName))
        {
            foreach (var n in VehicleForge.Suggested) if (ImGui.Selectable(n, n == vehicleDonorName)) vehicleDonorName = n;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(-90); ImGui.InputText("Other", ref vehicleDonorName, 32);
        Theme.Tip("Any game vehicle name (the spawn name): pick one about the size and type of your car.");
        if (ImGui.Button("1. Show the donor##2", new Vector2(-1, 0))) LoadVehicleDonor();
        TutRect("vf_donor");
        var sel = ForgeSelection();
        ImGui.Checkbox("My car faces +X / +Y", ref vehicleFrontPositive);
        Theme.Tip("Which end of your model is the front, along its length (untick if it comes out backwards).");
        ImGui.BeginDisabled(sel.Count == 0);
        if (ImGui.Button("2. Find my wheels", new Vector2(-1, 0))) FindVehicleWheels(sel);
        TutRect("vf_wheels");
        ImGui.EndDisabled();
        Theme.Tip("Looks for four separate round parts low in the model, one per corner.");
        if (vehicleWheels != null) Ui.TextDisabled("Wheels: " + string.Join("  ", vehicleWheels.Select(w => $"{w.Key[6..]}")));
        ImGui.Checkbox("Keep the proportions", ref vehicleKeepProportions);
        Theme.Tip("Off: stretched a little so your wheels land exactly on the donor's. On: same scale on every axis.");
        ImGui.BeginDisabled(vehicleWheels == null || vehicleDonor == null);
        if (ImGui.Button("3. Fit on the donor's wheels", new Vector2(-1, 0))) FitVehicle(sel);
        TutRect("vf_fit");
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.Checkbox("Use the donor's wheels (they turn)", ref vehicleDonorWheels);
        Theme.Tip("Your wheels are left out of the body; the donor's steer and spin.");
        ImGui.SetNextItemWidth(-90); ImGui.InputText("Spawn name", ref vehicleName, 24);
        ImGui.SetNextItemWidth(-90); ImGui.InputText("Label", ref vehicleLabel, 40);
        ImGui.BeginDisabled(sel.Count == 0 || vehicleDonor == null);
        if (ImGui.Button("4. Make the vehicle", new Vector2(-1, 30))) ExportVehicle(sel);
        TutRect("vf_make");
        ImGui.EndDisabled();
        Theme.Tip("Writes name.yft (+ _hi), the donor's textures, vehicles / handling / carvariations metas cloned from the donor, fxmanifest and a /spawn command.");
        ForgeOutputUi();
        ForgeReportUi();
    }

    void LoadVehicleDonor()
    {
        try { vehicleDonor = VehicleForge.LoadDonor(game, vehicleDonorName); } catch (Exception e) { SetStatus(e.Message); vehicleDonor = null; return; }
        ImportDonorGhost(vehicleDonor.Name);
        FrameAll();
        SetStatus($"{vehicleDonor.Name}: {vehicleDonor.Bones.Count} bones, {vehicleDonor.Wheels.Count} wheels. Select your car, then Find my wheels.");
    }

    int VehicleLengthAxis(List<SceneObject> sel)
    {
        var (mn, mx) = Bounds(sel.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))));
        return mx.X - mn.X >= mx.Y - mn.Y ? 0 : 1;
    }

    void FindVehicleWheels(List<SceneObject> sel)
    {
        int ax = VehicleLengthAxis(sel);
        vehicleWheels = VehicleForge.FindWheels(sel.Select(o => (o.Mesh, o.World)).ToList(), ax, vehicleFrontPositive, out vehicleWheelBoxes);
        SetStatus(vehicleWheels == null ? "Could not find four separate wheels: split them from the body (Separate, P) and try again." : "Four wheels found. Fit on the donor's wheels next.");
    }

    void FitVehicle(List<SceneObject> sel)
    {
        int ax = VehicleLengthAxis(sel);
        try
        {
            var fit = VehicleForge.Fit(vehicleWheels, ax, vehicleFrontPositive, vehicleDonor, vehicleKeepProportions);
            ApplyWorld(sel, fit, "Fit on the donor car");
            // the found wheels move with the car
            vehicleWheels = vehicleWheels.ToDictionary(k => k.Key, k => Vector3.Transform(k.Value, fit));
            vehicleWheelBoxes = vehicleWheelBoxes?.Select(b => Bounds(new[] { Vector3.Transform(b.Min, fit), Vector3.Transform(b.Max, fit) })).ToList();
            vehicleFrontPositive = true; // now along +Y like every GTA vehicle
            SetStatus("Fitted on " + vehicleDonor.Name + "'s wheels.");
        }
        catch (Exception e) { SetStatus(e.Message); }
    }

    void ExportVehicle(List<SceneObject> sel)
    {
        BakeAllPaint();
        var body = new List<(SceneObject, Matrix4x4)>();
        var tmp = new List<SceneObject>();
        foreach (var o in sel)
        {
            var src = o;
            if (vehicleDonorWheels && vehicleWheelBoxes != null)
            {
                // our wheels out of the body: faces whose centre is inside a wheel box
                var m = o.Mesh.Clone(); var w = o.World; var keep = new List<Face>();
                foreach (var f in m.Faces)
                {
                    var c = f.V.Aggregate(Vector3.Zero, (a, v) => a + Vector3.Transform(m.Verts[v], w)) / f.V.Length;
                    if (!vehicleWheelBoxes.Any(b => c.X >= b.Min.X - 0.02f && c.X <= b.Max.X + 0.02f && c.Y >= b.Min.Y - 0.02f && c.Y <= b.Max.Y + 0.02f && c.Z >= b.Min.Z - 0.02f && c.Z <= b.Max.Z + 0.02f)) keep.Add(f);
                }
                m.Faces.Clear(); m.Faces.AddRange(keep);
                src = o.Clone(m); tmp.Add(src);
            }
            body.Add((src, src.World));
        }
        var opt = new VehicleForge.Options { Name = vehicleName, Label = vehicleLabel };
        forgeReportTab = PropTab.Vehicle;
        forgeReport = VehicleForge.Export(game, vehicleDonor, Scene, body, opt, ForgeOutputFolder(), LoadImageRgba, renderer.Textures.SearchFolders.ToList());
        SetStatus(forgeReport.Ok ? "Vehicle written to " + forgeReport.Folder : "Vehicle: " + string.Join(" | ", forgeReport.Errors));
    }

    // ------------------------------------------------------------------------------------------ peds

    int pedDonor = 3; // deer
    string pedName = "molo_ped";
    PedForge.Body pedBody;

    void PedProps()
    {
        Ui.Header("Ped Forge");
        Ui.TextWrapped("Turn any character or animal into a FiveM ped: it takes the skeleton, ragdoll and animations of a game ped, and is skinned on it automatically.");
        if (ForgeNeedsGame()) return;
        var labels = PedForge.Donors.Select(d => d.Label).ToArray();
        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Donor##ped", ref pedDonor, labels, labels.Length)) pedBody = null;
        if (ImGui.Button("1. Show the donor##ped", new Vector2(-1, 0))) LoadPedDonor();
        TutRect("pf_donor");
        Theme.Tip("Brings the game ped in as an orange ghost at the origin (its body in the bind pose, with its main bones).");
        var sel = ForgeSelection();
        ImGui.BeginDisabled(pedBody == null || sel.Count == 0);
        if (ImGui.Button("2. Fit my model on it##ped", new Vector2(-1, 0))) FitPed(sel);
        TutRect("pf_fit");
        ImGui.EndDisabled();
        Theme.Tip("Same height as the donor, feet on its feet, centred. Then pose-match by hand: arms, legs and head over the donor's (the weights come from the nearest point of its body).");
        ImGui.Separator();
        ImGui.SetNextItemWidth(-90); ImGui.InputText("Name##ped", ref pedName, 32);
        ImGui.BeginDisabled(sel.Count == 0);
        if (ImGui.Button("3. Make the ped", new Vector2(-1, 30))) ExportPed(sel);
        TutRect("pf_make");
        ImGui.EndDisabled();
        Theme.Tip("Writes name.ydd (your model, skinned), .ytd (its texture), .ymt (variations), .yft (the donor's skeleton and ragdoll), peds.meta cloned from the donor, fxmanifest and a /name command to become the ped.");
        ForgeOutputUi();
        ForgeReportUi();
    }

    void LoadPedDonor()
    {
        var d = PedForge.Donors[pedDonor];
        try { pedBody = PedForge.LoadDonor(game, d.Model); } catch (Exception e) { SetStatus(e.Message); pedBody = null; return; }
        Scene.Objects.RemoveAll(o => o.Folder == ForgeFolder);
        if (!Scene.Folders.Contains(ForgeFolder)) Scene.Folders.Add(ForgeFolder);
        var g = Scene.Add(d.Model, PedForge.GhostMesh(pedBody), Vector3.Zero);
        g.Folder = ForgeFolder; g.Frozen = true; g.Gta.Role = GtaRole.Ignore; g.WireColour = new Vector3(1f, 0.55f, 0.15f);
        Selected.Clear(); SelectionChanged();
        FrameAll();
        SetStatus($"{d.Label}: {pedBody.BoneCount} bones, {pedBody.Points.Count:N0} body points. Select your model, then Fit.");
    }

    void FitPed(List<SceneObject> sel)
    {
        var pts = sel.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))).ToList();
        ApplyWorld(sel, PedForge.Fit(pts, pedBody), "Fit on the donor ped");
        SetStatus("Fitted on " + pedBody.Model + ". Line up the arms and legs with the donor's.");
    }

    void ExportPed(List<SceneObject> sel)
    {
        BakeAllPaint();
        var o = new PedForge.Options { Donor = PedForge.Donors[pedDonor], Name = pedName };
        forgeReportTab = PropTab.Ped;
        forgeReport = PedForge.Export(game, Scene, sel, o, ForgeOutputFolder(), LoadImageRgba, renderer.Textures.SearchFolders.ToList(), pedBody?.Model == o.Donor.Model ? pedBody : null);
        SetStatus(forgeReport.Ok ? "Ped written to " + forgeReport.Folder : "Ped: " + string.Join(" | ", forgeReport.Errors));
    }

    // ------------------------------------------------------------------------------------------ shared UI and viewport marks

    void ForgeOutputUi()
    {
        var f = ForgeOutputFolder();
        ImGui.SetNextItemWidth(-90); ImGui.InputText("Output", ref f, 400); forgeOut = f;
        Theme.Tip("Where the resource folder is written (your FiveM server's resources folder).");
    }

    void ForgeReportUi()
    {
        if (forgeReport == null || forgeReportTab != propTab) return;
        ImGui.Separator();
        foreach (var e in forgeReport.Errors) { ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.4f, 0.35f, 1)); Ui.TextWrapped(e); ImGui.PopStyleColor(); }
        foreach (var w in forgeReport.Warnings) { ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.8f, 0.3f, 1)); Ui.TextWrapped(w); ImGui.PopStyleColor(); }
        foreach (var i in forgeReport.Info) Ui.TextWrapped(i);
        if (forgeReport.Folder != null && Directory.Exists(forgeReport.Folder) && ImGui.Button("Open the folder", new Vector2(-1, 0)))
            try { System.Diagnostics.Process.Start("explorer.exe", forgeReport.Folder); } catch { }
    }

    /// <summary>Bones of the weapon donor, the muzzle, the wheels found and the donor's wheels, drawn in the viewport.</summary>
    void ForgeOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (propTab is not (PropTab.Weapon or PropTab.Vehicle or PropTab.Ped) || MapMode) return;
        bool Screen(Vector3 p, out Vector2 s)
        {
            bool ok = vp.Camera.Project(p, vp.W, vp.H, out var sc, out _);
            s = new Vector2(vp.X + sc.X, vp.Y + sc.Y);
            return ok;
        }
        void Dot(Vector3 p, uint col, string label)
        {
            if (!Screen(p, out var s)) return;
            dl.AddCircleFilled(s, 4, col); dl.AddCircle(s, 6, 0xFF000000);
            if (label != null) dl.AddText(s + new Vector2(8, -8), col, label);
        }
        if (propTab == PropTab.Weapon)
        {
            if (weaponBones != null) foreach (var b in weaponBones) Dot(b.Position, 0xFF40A0FF, b.Name);
            if (weaponMuzzle is { } mz) Dot(mz, 0xFF30E0FF, "muzzle (yours)");
        }
        else if (propTab == PropTab.Ped)
        {
            if (pedBody != null) foreach (var b in pedBody.Bones.Where(b => b.Name != null && (b.Name.Contains("Head") || b.Name.Contains("Hand") || b.Name.Contains("Foot") || b.Name.Contains("Pelvis")))) Dot(b.Position, 0xFF40A0FF, b.Name);
        }
        else
        {
            if (vehicleDonor != null) foreach (var w in vehicleDonor.Wheels) Dot(w.Value, 0xFF40A0FF, w.Key);
            if (vehicleWheels != null) foreach (var w in vehicleWheels) Dot(w.Value, 0xFF60E060, w.Key[6..] + " (yours)");
        }
    }
}
