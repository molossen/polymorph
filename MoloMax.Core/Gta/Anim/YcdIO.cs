using System.Globalization;
using System.Numerics;
using System.Text;
using MoloMax.Core.Gta.Export;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Anim;

/// <summary>One animated value of a clip, sampled at every frame (3 components, or 4 for a rotation).</summary>
public sealed class YcdTrack
{
    public ushort Bone;
    /// <summary>GTA track: 0 bone position, 1 bone rotation, 2 bone scale, 5 mover position, 6 mover rotation, 17 / 18 UV rows.</summary>
    public byte Track;
    public Vector4[] Values = Array.Empty<Vector4>();
    public bool IsRotation => Track is 1 or 6;
    public int Components => IsRotation ? 4 : 3;
    public override string ToString() => $"bone {Bone} track {Track} ({Values.Length} frames)";
}

/// <summary>A clip of a GTA clip dictionary (.ycd), sampled frame by frame.</summary>
public sealed class YcdClip
{
    public string Name;      // clip name without "pack:/" and ".clip"
    public uint Hash;
    public int Frames;
    public float Duration;
    /// <summary>Written as 'pack:/name' without '.clip' (like the game's animated props).</summary>
    public bool NoClipSuffix;
    public List<YcdTrack> Tracks = new();
    public YcdTrack Track(ushort bone, byte track) => Tracks.FirstOrDefault(t => t.Bone == bone && t.Track == track);
    public bool IsUv => Tracks.Any(t => t.Track is 17 or 18);
    public override string ToString() => $"{Name}: {Frames} frames, {Tracks.Count} tracks";
}

/// <summary>Reads and writes GTA clip dictionaries through CodeWalker (binary .ycd; XML as the bridge for writing).</summary>
public static class YcdIO
{
    public const int Fps = 30;

    /// <summary>A binary .ycd as CodeWalker XML (diagnostics).</summary>
    public static string BinaryToXml(byte[] data) { var y = new Cw.YcdFile(); Cw.RpfFile.LoadResourceFile(y, data, 46); return Cw.YcdXml.GetXml(y); }

    /// <summary>CodeWalker XML back to a binary .ycd.</summary>
    public static byte[] XmlToBinary(string xml) { var d = new System.Xml.XmlDocument(); d.LoadXml(xml); return Cw.XmlYcd.GetYcd(d).Save(); }

    public static List<YcdClip> Read(byte[] data, string fileName)
    {
        var y = new Cw.YcdFile();
        y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = Path.GetFileName(fileName) };
        Cw.RpfFile.LoadResourceFile(y, data, 46);
        if (y.ClipDictionary == null) throw new InvalidDataException("Not a clip dictionary (.ycd): " + (y.LoadException ?? "empty"));
        y.InitDictionaries();
        var res = new List<YcdClip>();
        foreach (var cme in y.ClipMap.Values)
        {
            var clip = cme?.Clip; if (clip == null) continue;
            var anims = new List<(Cw.Animation A, float Start, float End)>();
            if (clip is Cw.ClipAnimation ca && ca.Animation != null) anims.Add((ca.Animation, ca.StartTime, ca.EndTime));
            else if (clip is Cw.ClipAnimationList cl && cl.Animations != null)
                foreach (var e in cl.Animations) if (e?.Animation != null) anims.Add((e.Animation, e.StartTime, e.EndTime));
            foreach (var (a, _, _) in anims)
            {
                var c = new YcdClip
                {
                    Name = CleanName(clip.ShortName ?? clip.Name ?? cme.Hash.ToString()),
                    Hash = cme.Hash.Hash, Frames = a.Frames, Duration = a.Duration,
                };
                var ids = a.BoneIds?.data_items ?? Array.Empty<Cw.AnimationBoneId>();
                for (int b = 0; b < ids.Length; b++)
                {
                    var t = new YcdTrack { Bone = ids[b].BoneId, Track = ids[b].Track, Values = new Vector4[a.Frames] };
                    for (int f = 0; f < a.Frames; f++)
                    {
                        var fp = new Cw.Animation.FramePosition { Frame0 = f, Frame1 = f + 1, Alpha0 = 1, Alpha1 = 0 };
                        try
                        {
                            if (t.IsRotation) { var q = a.EvaluateQuaternion(fp, b, false); t.Values[f] = new Vector4(q.X, q.Y, q.Z, q.W); }
                            else { var v = a.EvaluateVector4(fp, b, false); t.Values[f] = new Vector4(v.X, v.Y, v.Z, v.W); }
                        }
                        catch { t.Values[f] = f > 0 ? t.Values[f - 1] : (t.IsRotation ? new Vector4(0, 0, 0, 1) : Vector4.Zero); }
                    }
                    c.Tracks.Add(t);
                }
                res.Add(c);
            }
        }
        return res;
    }

    static string CleanName(string n)
    {
        n = n.Replace("pack:/", "");
        if (n.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)) n = n[..^5];
        return n;
    }

    static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);

    /// <summary>The clip dictionary as CodeWalker XML (one animation per clip, 30 fps).</summary>
    public static string ToXml(IEnumerable<YcdClip> clips)
    {
        var list = clips.ToList();
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ClipDictionary>\n <Clips>\n");
        foreach (var c in list)
        {
            uint hash = c.Hash != 0 ? c.Hash : MetaXml.Jenkins(c.Name);
            sb.Append("  <Item>\n");
            sb.Append($"   <Hash>hash_{hash:X8}</Hash>\n");
            sb.Append($"   <Name>pack:/{c.Name}{(c.NoClipSuffix ? "" : ".clip")}</Name>\n");
            sb.Append("   <Type value=\"Animation\" />\n   <Unknown30 value=\"0\" />\n   <Tags />\n   <Properties />\n");
            sb.Append($"   <AnimationHash>{c.Name}</AnimationHash>\n");
            sb.Append($"   <StartTime value=\"0\" />\n   <EndTime value=\"{F(c.Duration)}\" />\n   <Rate value=\"1\" />\n");
            sb.Append("  </Item>\n");
        }
        sb.Append(" </Clips>\n <Animations>\n");
        foreach (var c in list)
        {
            sb.Append("  <Item>\n");
            sb.Append($"   <Hash>{c.Name}</Hash>\n");
            sb.Append("   <Unknown10 value=\"1\" />\n");
            sb.Append($"   <FrameCount value=\"{c.Frames}\" />\n");
            sb.Append($"   <SequenceFrameLimit value=\"{c.Frames + 2}\" />\n");
            sb.Append($"   <Duration value=\"{F(c.Duration)}\" />\n");
            sb.Append("   <Unknown1C>hash_6B002400</Unknown1C>\n");
            sb.Append("   <BoneIds>\n");
            foreach (var t in c.Tracks) sb.Append($"    <Item><BoneId value=\"{t.Bone}\" /><Track value=\"{t.Track}\" /><Unk0 value=\"{(t.IsRotation ? 1 : 0)}\" /></Item>\n");
            sb.Append("   </BoneIds>\n");
            sb.Append($"   <Sequences>\n    <Item>\n     <Hash>hash_00000000</Hash>\n     <FrameCount value=\"{c.Frames}\" />\n     <SequenceData>\n");
            foreach (var t in c.Tracks)
            {
                sb.Append("      <Item>\n       <Channels>\n");
                for (int comp = 0; comp < t.Components; comp++)
                {
                    var vals = t.Values.Select(v => comp switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W }).ToList();
                    if (vals.Count == 0) vals.Add(t.IsRotation && comp == 3 ? 1 : 0);
                    bool constant = vals.All(v => MathF.Abs(v - vals[0]) < 1e-7f);
                    if (constant) sb.Append($"        <Item><Type value=\"StaticFloat\" /><Value value=\"{F(vals[0])}\" /></Item>\n");
                    else sb.Append($"        <Item><Type value=\"RawFloat\" /><Values>{string.Join(" ", vals.Select(F))}</Values></Item>\n");
                }
                sb.Append("       </Channels>\n      </Item>\n");
            }
            sb.Append("     </SequenceData>\n    </Item>\n   </Sequences>\n  </Item>\n");
        }
        sb.Append(" </Animations>\n</ClipDictionary>\n");
        return sb.ToString();
    }

    public static byte[] Write(IEnumerable<YcdClip> clips)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(ToXml(clips));
        return Cw.XmlYcd.GetYcd(doc).Save();
    }
}
