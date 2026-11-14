using System.Numerics;
using MoloMax.Core.Gta.Anim;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Anim;

/// <summary>
/// Keyframes &lt;-&gt; GTA clips. An object's move becomes its root bone (bone 0: position / rotation / scale, relative to where it
/// stands at the first frame, like PlayEntityAnim plays it); its UV keys become the shader's UV clip (tracks 17 / 18, hash =
/// joaat(model) + shader index + 1). Clips are sampled at 30 fps.
/// </summary>
public static class AnimGta
{
    public static string ModelName(SceneObject o) => string.IsNullOrEmpty(o.Gta?.ModelName) ? o.Name : o.Gta.ModelName;

    /// <summary>The clips of these objects (every object with keys when null).</summary>
    public static List<YcdClip> ToClips(Scene s, IEnumerable<int> ids = null)
    {
        var a = s.Anim; var res = new List<YcdClip>();
        int frames = Math.Max(2, a.End - a.Start + 1);
        float dur = (frames - 1) / MathF.Max(1, a.Fps);
        var want = ids?.ToHashSet();
        // skinned objects: one clip with the root and every bone (bone tags), named like the model so it plays on its own
        var skinned = SkinnedClips(s, want);
        res.AddRange(skinned.Select(x => x.Clip));
        var skinnedIds = skinned.Select(x => x.ObjectId).ToHashSet();
        var boneIds = s.Objects.Where(x => skinnedIds.Contains(x.Id)).SelectMany(x => x.Modifiers.OfType<MoloMax.Core.Modifiers.SkinModifier>().SelectMany(m => m.BoneIds)).ToHashSet();
        foreach (var t in a.Tracks)
        {
            if (t.ObjectId < 0 || want != null && !want.Contains(t.ObjectId)) continue;
            if (boneIds.Contains(t.ObjectId)) continue;
            var o = s.Find(t.ObjectId); if (o == null) continue;
            var model = ModelName(o).ToLowerInvariant();
            // sample on a copy so the scene does not move
            var probe = o.Clone(o.Mesh);
            var one = new Scene(); one.Objects.Add(probe);
            var only = new SceneAnim { Start = a.Start, End = a.End, Fps = a.Fps, Tracks = { t.Clone() } };
            bool xf = Ch.Loc.Concat(Ch.Rot).Concat(Ch.Scale).Any(t.Has), uv = Ch.Uv.Any(t.Has);
            var pos = new Vector3[frames]; var rot = new Quaternion[frames]; var scl = new Vector3[frames];
            var r0 = new Vector3[frames]; var r1 = new Vector3[frames];
            for (int f = 0; f < frames; f++)
            {
                probe.UvRow0 = default; probe.UvRow1 = default;
                only.Apply(one, a.Start + f);
                pos[f] = probe.Position; rot[f] = probe.Rotation; scl[f] = probe.Scale;
                r0[f] = probe.UvRow0.W > 0 ? new Vector3(probe.UvRow0.X, probe.UvRow0.Y, probe.UvRow0.Z) : new Vector3(1, 0, 0);
                r1[f] = probe.UvRow1.W > 0 ? new Vector3(probe.UvRow1.X, probe.UvRow1.Y, probe.UvRow1.Z) : new Vector3(0, 1, 0);
            }
            if (xf && !skinnedIds.Contains(o.Id))
            {
                var inv0 = Quaternion.Conjugate(Quaternion.Normalize(rot[0]));
                var c = new YcdClip { Name = model + "_anim", Frames = frames, Duration = dur };
                c.Tracks.Add(new YcdTrack { Bone = 0, Track = 0, Values = pos.Select(p => { var l = Vector3.Transform(p - pos[0], inv0); return new Vector4(l, 0); }).ToArray() });
                c.Tracks.Add(new YcdTrack { Bone = 0, Track = 1, Values = rot.Select(q => { var l = Quaternion.Normalize(inv0 * q); return new Vector4(l.X, l.Y, l.Z, l.W); }).ToArray() });
                if (Ch.Scale.Any(t.Has)) c.Tracks.Add(new YcdTrack { Bone = 0, Track = 2, Values = scl.Select(v => new Vector4(v / Vector3.Max(scl[0], new Vector3(1e-4f)), 0)).ToArray() });
                res.Add(c);
            }
            if (uv)
            {
                int shaders = Math.Max(1, o.Mesh?.Faces.Select(f => f.MatId).Distinct().Count() ?? 1);
                for (int i = 0; i < shaders; i++)
                {
                    var c = new YcdClip { Name = $"{model}_uv_{i}", Hash = AnimXml.ClipHash(model, i), Frames = frames, Duration = dur };
                    c.Tracks.Add(new YcdTrack { Bone = 0, Track = 17, Values = r0.Select(v => new Vector4(v, 0)).ToArray() });
                    c.Tracks.Add(new YcdTrack { Bone = 0, Track = 18, Values = r1.Select(v => new Vector4(v, 0)).ToArray() });
                    res.Add(c);
                }
            }
        }
        return res;
    }

    /// <summary>
    /// Bone animation of skinned objects (Skin modifier) whose bones or self are keyed: the clip holds the root (bone 0, the
    /// object's own move from where it stands at the first frame) and each bone's local position / rotation under its parent,
    /// with the bone tags of the exported skeleton. Its hash is the model name: an archetype with this clip dictionary plays it
    /// by itself (like the game's animated props).
    /// </summary>
    public static List<(int ObjectId, YcdClip Clip)> SkinnedClips(Scene s, HashSet<int> want = null)
    {
        var a = s.Anim; var res = new List<(int, YcdClip)>();
        if (a == null || a.Tracks.Count == 0) return res;
        int frames = Math.Max(2, a.End - a.Start + 1);
        float dur = (frames - 1) / MathF.Max(1, a.Fps);
        bool Keyed(int id) => a.Track(id) is { } t && Ch.Loc.Concat(Ch.Rot).Concat(Ch.Scale).Any(t.Has);
        foreach (var o in s.Objects)
        {
            var sk = o.Modifiers?.OfType<MoloMax.Core.Modifiers.SkinModifier>().LastOrDefault(m => m.ShowRender);
            if (sk == null || sk.BoneIds.Count == 0 || sk.BindObject == null) continue;
            if (want != null && !want.Contains(o.Id) && !sk.BoneIds.Any(want.Contains)) continue;
            if (!Keyed(o.Id) && !sk.BoneIds.Any(Keyed)) continue;
            var model = ModelName(o).ToLowerInvariant();
            var copy = s.Clone(x => x.Mesh);
            var pos = new Vector3[frames]; var rot = new Quaternion[frames];
            var pose = new List<SkinBone>[frames];
            for (int f = 0; f < frames; f++)
            {
                a.Apply(copy, a.Start + f);
                var co = copy.Find(o.Id);
                pos[f] = co.Position; rot[f] = co.Rotation;
                var now = (MoloMax.Core.Modifiers.SkinModifier)sk.Clone();
                now.BindObject = MoloMax.Core.Modifiers.SkinModifier.Pack(co.World);
                now.BindBones = sk.BoneIds.Select((id, i) => copy.Find(id) is { } b ? MoloMax.Core.Modifiers.SkinModifier.Pack(b.World) : sk.BindBones[i]).ToList();
                var src = new SkinExport.Source { Skin = now, Points = Array.Empty<Vector3>() };
                pose[f] = SkinExport.Make(copy, co, src, Matrix4x4.CreateScale(co.Scale), model).Bones;
            }
            var c = new YcdClip { Name = model, Hash = MetaXml.Jenkins(model), Frames = frames, Duration = dur, NoClipSuffix = true };
            if (Keyed(o.Id))
            {
                var inv0 = Quaternion.Conjugate(Quaternion.Normalize(rot[0]));
                c.Tracks.Add(new YcdTrack { Bone = 0, Track = 0, Values = pos.Select(p => new Vector4(Vector3.Transform(p - pos[0], inv0), 0)).ToArray() });
                c.Tracks.Add(new YcdTrack { Bone = 0, Track = 1, Values = rot.Select(q => { var l = Quaternion.Normalize(inv0 * q); return new Vector4(l.X, l.Y, l.Z, l.W); }).ToArray() });
            }
            for (int b = 1; b < pose[0].Count; b++)
            {
                ushort tag = (ushort)pose[0][b].Tag;
                var tr = new Vector4[frames]; var rq = new Vector4[frames];
                var prev = Quaternion.Identity;
                for (int f = 0; f < frames; f++)
                {
                    var bone = pose[f][b];
                    tr[f] = new Vector4(bone.Translation, 0);
                    var q = bone.Rotation;
                    if (f > 0 && Quaternion.Dot(q, prev) < 0) q = Quaternion.Negate(q); // same hemisphere: no flip between frames
                    prev = q; rq[f] = new Vector4(q.X, q.Y, q.Z, q.W);
                }
                c.Tracks.Add(new YcdTrack { Bone = tag, Track = 0, Values = tr });
                c.Tracks.Add(new YcdTrack { Bone = tag, Track = 1, Values = rq });
            }
            res.Add((o.Id, c));
        }
        return res;
    }

    /// <summary>The keyed UV animation of an object as the clip of one of its shaders (null when its UVs are not keyed).</summary>
    public static YcdClip UvClipFor(Scene s, SceneObject o, string model, int shaderIndex)
    {
        var t = s.Anim?.Track(o.Id);
        if (t == null || !Ch.Uv.Any(t.Has)) return null;
        var c = ToClips(s, new[] { o.Id }).FirstOrDefault(x => x.IsUv);
        if (c == null) return null;
        model = model.ToLowerInvariant();
        c.Name = $"{model}_uv_{shaderIndex}"; c.Hash = AnimXml.ClipHash(model, shaderIndex);
        return c;
    }

    /// <summary>A procedural UV clip (scroll / spritesheet of the GTA tab) as a sampled clip.</summary>
    public static YcdClip FromUvClip(UvClip u)
    {
        var (frames, dur) = AnimXml.Timing(u.Anim);
        var c = new YcdClip { Name = $"{u.Model}_uv_{u.ShaderIndex}", Hash = AnimXml.ClipHash(u.Model, u.ShaderIndex), Frames = frames, Duration = dur };
        var rows = Enumerable.Range(0, frames).Select(f => AnimXml.Evaluate(u.Anim, f, frames)).ToList();
        c.Tracks.Add(new YcdTrack { Bone = 0, Track = 17, Values = rows.Select(r => new Vector4(r.Uv0, 0)).ToArray() });
        c.Tracks.Add(new YcdTrack { Bone = 0, Track = 18, Values = rows.Select(r => new Vector4(r.Uv1, 0)).ToArray() });
        return c;
    }

    /// <summary>What a clip holds, in words (import report).</summary>
    public static string Describe(YcdClip c)
    {
        var bones = c.Tracks.Where(t => t.Track is 0 or 1 or 2).Select(t => t.Bone).Distinct().Count();
        var parts = new List<string>();
        if (c.IsUv) parts.Add("UV");
        if (c.Tracks.Any(t => t.Track is 5 or 6)) parts.Add("mover");
        if (bones > 0) parts.Add(bones == 1 ? "1 bone" : $"{bones} bones");
        return $"{c.Name} ({c.Frames} frames, {string.Join(", ", parts)})";
    }

    /// <summary>
    /// A clip onto an object as keys from <paramref name="startFrame"/>: the root bone (or the mover) moves it from where it stands,
    /// UV rows become UV keys. Keys are thinned where the motion is straight. Returns the number of keys written.
    /// </summary>
    public static int ApplyClip(SceneAnim a, SceneObject o, YcdClip c, int startFrame, float fps = YcdIO.Fps)
    {
        int keys = 0;
        var t = a.Track(o.Id, true);
        float FrameOf(int i) => startFrame + i * (fps / YcdIO.Fps) * (c.Frames > 1 ? c.Duration * YcdIO.Fps / (c.Frames - 1) : 1);
        var p = c.Track(0, 0) ?? c.Track(0, 5); var r = c.Track(0, 1) ?? c.Track(0, 6); var sc = c.Track(0, 2);
        var basePos = o.Position; var baseRot = Quaternion.Normalize(o.Rotation); var baseScale = o.Scale;
        if (p != null || r != null || sc != null)
        {
            var e = new Vector3(); bool first = true;
            var lx = new List<(float, float)>(); var ly = new List<(float, float)>(); var lz = new List<(float, float)>();
            var rx = new List<(float, float)>(); var ry = new List<(float, float)>(); var rz = new List<(float, float)>();
            var sx = new List<(float, float)>(); var sy = new List<(float, float)>(); var sz = new List<(float, float)>();
            for (int i = 0; i < c.Frames; i++)
            {
                float f = FrameOf(i);
                if (p != null)
                {
                    var l = new Vector3(p.Values[i].X, p.Values[i].Y, p.Values[i].Z);
                    var w = basePos + Vector3.Transform(l, baseRot);
                    lx.Add((f, w.X)); ly.Add((f, w.Y)); lz.Add((f, w.Z));
                }
                if (r != null)
                {
                    var q = Quaternion.Normalize(baseRot * new Quaternion(r.Values[i].X, r.Values[i].Y, r.Values[i].Z, r.Values[i].W));
                    e = SceneAnim.ToEulerDeg(q, first ? null : e); first = false;
                    rx.Add((f, e.X)); ry.Add((f, e.Y)); rz.Add((f, e.Z));
                }
                if (sc != null) { var v = baseScale * new Vector3(sc.Values[i].X, sc.Values[i].Y, sc.Values[i].Z); sx.Add((f, v.X)); sy.Add((f, v.Y)); sz.Add((f, v.Z)); }
            }
            keys += Put(t, Ch.LocX, lx) + Put(t, Ch.LocY, ly) + Put(t, Ch.LocZ, lz);
            keys += Put(t, Ch.RotX, rx) + Put(t, Ch.RotY, ry) + Put(t, Ch.RotZ, rz);
            keys += Put(t, Ch.ScaleX, sx) + Put(t, Ch.ScaleY, sy) + Put(t, Ch.ScaleZ, sz);
        }
        var u0 = c.Track(0, 17); var u1 = c.Track(0, 18);
        if (u0 != null && u1 != null)
        {
            var cu = new List<(float, float)>(); var cv = new List<(float, float)>(); var csu = new List<(float, float)>(); var csv = new List<(float, float)>(); var cr = new List<(float, float)>();
            float prevRot = 0;
            for (int i = 0; i < c.Frames; i++)
            {
                var (u, v, su, sv, rot) = DecomposeUv(new Vector3(u0.Values[i].X, u0.Values[i].Y, u0.Values[i].Z), new Vector3(u1.Values[i].X, u1.Values[i].Y, u1.Values[i].Z));
                while (rot - prevRot > 180) rot -= 360; while (rot - prevRot < -180) rot += 360; prevRot = rot;
                float f = FrameOf(i);
                cu.Add((f, u)); cv.Add((f, v)); csu.Add((f, su)); csv.Add((f, sv)); cr.Add((f, rot));
            }
            keys += Put(t, Ch.UvU, cu) + Put(t, Ch.UvV, cv) + Put(t, Ch.UvSU, csu) + Put(t, Ch.UvSV, csv) + Put(t, Ch.UvRot, cr);
        }
        if (t.Curves.Count == 0) a.Tracks.Remove(t);
        int end = (int)MathF.Ceiling(FrameOf(Math.Max(0, c.Frames - 1)));
        if (end > a.End) a.End = end;
        return keys;
    }

    /// <summary>Offset, scale and rotation (degrees, about the texture centre) of two UV rows (inverse of <see cref="SceneAnim.UvRows"/>).</summary>
    public static (float U, float V, float SU, float SV, float Rot) DecomposeUv(Vector3 r0, Vector3 r1)
    {
        float su = MathF.Sqrt(r0.X * r0.X + r1.X * r1.X);
        float rot = MathF.Atan2(r1.X, r0.X);
        float c = MathF.Cos(rot), s = MathF.Sin(rot);
        float sv = c * r1.Y - s * r0.Y; // r01 = -s sv, r11 = c sv
        float u = r0.Z - 0.5f + (r0.X * 0.5f + r0.Y * 0.5f);
        float v = r1.Z - 0.5f + (r1.X * 0.5f + r1.Y * 0.5f);
        return (u, v, su, sv, rot * 180 / MathF.PI);
    }

    /// <summary>Writes sampled values as keys, dropping those a straight line between their neighbours already gives.</summary>
    static int Put(AnimTrack t, string ch, List<(float F, float V)> s)
    {
        if (s.Count == 0) return 0;
        float range = s.Max(x => x.V) - s.Min(x => x.V);
        if (range < 1e-5f && !t.Has(ch))
        {
            // constant: one key is enough (and none at all for a rest value)
            bool rest = ch is Ch.UvU or Ch.UvV or Ch.UvRot ? MathF.Abs(s[0].V) < 1e-5f : ch is Ch.UvSU or Ch.UvSV && MathF.Abs(s[0].V - 1) < 1e-5f;
            if (rest) return 0;
            t.Curve(ch, true).SetKey(s[0].F, s[0].V, KeyInterp.Linear);
            return 1;
        }
        float eps = MathF.Max(1e-4f, range * 0.002f);
        // greedy: from each kept key, reach as far as a straight line stays within eps of every sample in between
        var keep = new List<(float F, float V)> { s[0] };
        int anchor = 0;
        while (anchor < s.Count - 1)
        {
            int end = anchor + 1;
            while (end + 1 < s.Count)
            {
                var a = s[anchor]; var b = s[end + 1]; bool fits = true;
                for (int i = anchor + 1; i <= end && fits; i++)
                {
                    float tt = (s[i].F - a.F) / MathF.Max(1e-4f, b.F - a.F);
                    fits = MathF.Abs(a.V + (b.V - a.V) * tt - s[i].V) <= eps;
                }
                if (!fits) break;
                end++;
            }
            keep.Add(s[end]); anchor = end;
        }
        var c = t.Curve(ch, true);
        foreach (var (f, v) in keep) c.SetKey(f, v, KeyInterp.Linear);
        return keep.Count;
    }
}
