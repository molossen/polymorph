using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>
/// Loads textures from disk once and keeps them. DDS (DXT1/DXT3/DXT5, ATI1/ATI2 = BC4/BC5,
/// BC7 via DX10 header, A8R8G8B8 / X8R8G8B8) is uploaded as-is with its mip chain — the formats
/// GTA uses. PNG / JPG / BMP are decoded and get generated mips.
/// </summary>
public sealed class TextureCache : IDisposable
{
    readonly Device device;
    readonly Dictionary<string, ShaderResourceView> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Errors = new();

    public ShaderResourceView White { get; }
    /// <summary>Stands in for a texture still on its way (MAP streaming): a neutral grey-brown, not a blinding white.</summary>
    public ShaderResourceView Waiting { get; }
    public ShaderResourceView FlatNormal { get; }
    public ShaderResourceView DefaultSpec { get; }

    /// <summary>Extra folders searched for bare texture names (the scene folder, a texture library…).</summary>
    public readonly List<string> SearchFolders = new();

    public TextureCache(Device device)
    {
        this.device = device;
        White = Solid(255, 255, 255, 255);
        Waiting = Solid(118, 112, 104, 255);
        FlatNormal = Solid(128, 128, 255, 255);
        DefaultSpec = Solid(26, 26, 26, 26);
    }

    ShaderResourceView Solid(byte r, byte g, byte b, byte a)
    {
        var px = new byte[] { r, g, b, a };
        var h = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            using var t = new Texture2D(device, new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(h.AddrOfPinnedObject(), 4));
            return new ShaderResourceView(device, t);
        }
        finally { h.Free(); }
    }

    readonly Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call after changing SearchFolders or when files appear on disk.</summary>
    public void ClearResolveCache() { resolved.Clear(); dirIndex.Clear(); merged.Clear(); mergedCount = 0; Epoch++; }

    static readonly string[] Exts = { "", ".dds", ".png", ".jpg", ".bmp" };
    // file name -> its path in the first searched folder that holds it (and that folder's place in the list)
    readonly Dictionary<string, (string Path, int Folder)> merged = new(StringComparer.OrdinalIgnoreCase);
    int mergedCount; string mergedFirst;
    Dictionary<string, (string Path, int Folder)> Merged()
    {
        // folders are only ever added at the end; a cleared or changed list is read again
        if (mergedCount > SearchFolders.Count || mergedCount > 0 && !string.Equals(mergedFirst, SearchFolders[0], StringComparison.OrdinalIgnoreCase)) { merged.Clear(); mergedCount = 0; }
        for (; mergedCount < SearchFolders.Count; mergedCount++)
            foreach (var kv in DirFiles(SearchFolders[mergedCount])) merged.TryAdd(kv.Key, (kv.Value, mergedCount));
        mergedFirst = SearchFolders.Count > 0 ? SearchFolders[0] : null;
        return merged;
    }
    /// <summary>Changes whenever a lookup could give another answer (renderer caches per material check it).</summary>
    public int Epoch { get; private set; }
    /// <summary>The last Get returned null only because the texture is still on its way.</summary>
    public bool LastGetPending { get; private set; }
    /// <summary>File names of each search folder (read once): a lookup does not probe the disk folder by folder.</summary>
    readonly Dictionary<string, Dictionary<string, string>> dirIndex = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> DirFiles(string dir)
    {
        if (dirIndex.TryGetValue(dir, out var m)) return m;
        m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) m[Path.GetFileName(f)] = f; } catch { }
        dirIndex[dir] = m;
        return m;
    }
    /// <summary>New texture files arrived: only the names not found so far are looked up again (found ones stay cached).</summary>
    /// <summary>These texture files were just written: their names get looked up again, nothing else.</summary>
    public void ForgetNames(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            var dir = Path.GetDirectoryName(f);
            if (dir != null && dirIndex.TryGetValue(dir, out var idx)) idx[Path.GetFileName(f)] = f;
            if (dir != null && mergedCount > 0)
            {
                int fi = SearchFolders.FindIndex(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase));
                if (fi >= 0 && fi < mergedCount && (!merged.TryGetValue(Path.GetFileName(f), out var me) || me.Folder > fi)) merged[Path.GetFileName(f)] = (f, fi);
            }
            var n = Path.GetFileNameWithoutExtension(f);
            if (resolved.TryGetValue(n, out var v) && v == null) { resolved.Remove(n); Epoch++; }
            if (resolved.TryGetValue(Path.GetFileName(f), out v) && v == null) { resolved.Remove(Path.GetFileName(f)); Epoch++; }
        }
    }

    public void ForgetMissing()
    {
        foreach (var k in resolved.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) resolved.Remove(k);
        Epoch++;
    }

    public string Resolve(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        if (resolved.TryGetValue(nameOrPath, out var hit)) return hit;
        loadSw.Restart();
        var r = ResolveUncached(nameOrPath);
        LoadMs += loadSw.Elapsed.TotalMilliseconds;
        resolved[nameOrPath] = r;
        return r;
    }

    string ResolveUncached(string nameOrPath)
    {
        if (File.Exists(nameOrPath) || MoloMax.Core.IO.PendingFiles.IsPending(nameOrPath)) return Path.GetFullPath(nameOrPath);
        bool bare = nameOrPath.IndexOfAny(new[] { '/', '\\', ':' }) < 0;
        if (bare)
        {
            // one index of every searched folder: MAP mode searches hundreds of folders (one per model read)
            var m = Merged(); string best = null; int bestIdx = int.MaxValue;
            foreach (var ext in Exts)
                if (m.TryGetValue(nameOrPath + ext, out var e) && e.Folder < bestIdx) { best = e.Path; bestIdx = e.Folder; }
            return best;
        }
        foreach (var dir in SearchFolders)
        {
            foreach (var ext in new[] { "", ".dds", ".png", ".jpg", ".bmp" })
            {
                var p = Path.Combine(dir, nameOrPath + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- live textures (Molo Paint)

    sealed class Dyn { public Texture2D Tex; public ShaderResourceView Srv; public int W, H; }
    readonly Dictionary<string, Dyn> dynamic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers (or updates) an in-memory texture under a path. While registered it replaces the file of that
    /// path in the viewport, so painted textures show live and still point to their real .dds for the export.
    /// </summary>
    public void SetDynamic(string path, MoloMax.Core.Gta.Export.RgbaImage img, (int X0, int Y0, int X1, int Y1)? rect = null)
    {
        if (string.IsNullOrEmpty(path) || img == null) return;
        if (!dynamic.TryGetValue(path, out var d) || d.W != img.Width || d.H != img.Height)
        {
            if (d != null) { d.Srv.Dispose(); d.Tex.Dispose(); }
            int mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(img.Width, img.Height)));
            d = new Dyn { W = img.Width, H = img.Height };
            d.Tex = new Texture2D(device, new Texture2DDescription
            {
                Width = img.Width, Height = img.Height, MipLevels = mips, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
            });
            d.Srv = new ShaderResourceView(device, d.Tex);
            dynamic[path] = d; Epoch++;
            rect = null;
        }
        var h = GCHandle.Alloc(img.Pixels, GCHandleType.Pinned);
        try
        {
            int pitch = img.Width * 4;
            if (rect is { } r)
            {
                int x0 = Math.Clamp(r.X0, 0, img.Width - 1), y0 = Math.Clamp(r.Y0, 0, img.Height - 1);
                int x1 = Math.Clamp(r.X1, x0, img.Width - 1), y1 = Math.Clamp(r.Y1, y0, img.Height - 1);
                device.ImmediateContext.UpdateSubresource(new DataBox(h.AddrOfPinnedObject() + (y0 * img.Width + x0) * 4, pitch, 0), d.Tex, 0,
                    new ResourceRegion(x0, y0, 0, x1 + 1, y1 + 1, 1));
            }
            else device.ImmediateContext.UpdateSubresource(new DataBox(h.AddrOfPinnedObject(), pitch, 0), d.Tex, 0);
        }
        finally { h.Free(); }
        device.ImmediateContext.GenerateMips(d.Srv);
    }

    public void RemoveDynamic(string path)
    {
        if (path != null && dynamic.Remove(path, out var d)) { d.Srv.Dispose(); d.Tex.Dispose(); Epoch++; }
    }

    // ---------------------------------------------------------------- thumbnails (library browser)

    readonly Dictionary<string, ShaderResourceView> thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> thumbFailed = new(StringComparer.OrdinalIgnoreCase);
    int thumbLoadsThisFrame;
    public void BeginFrameThumbs() => thumbLoadsThisFrame = 0;

    /// <summary>Preview image for the UI (loaded a few per frame so the browser stays smooth). Null while not loaded.</summary>
    public ShaderResourceView Thumb(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (thumbs.TryGetValue(path, out var s)) return s;
        if (thumbFailed.Contains(path) || thumbLoadsThisFrame >= 6) return null;
        thumbLoadsThisFrame++;
        try { s = LoadThumb(path); thumbs[path] = s; return s; }
        catch { thumbFailed.Add(path); return null; }
    }

    /// <summary>Size and mip count of a loaded texture (diagnostics).</summary>
    public string Describe(string nameOrPath)
    {
        var p = Resolve(nameOrPath);
        if (p == null) return "(not found)";
        if (!cache.TryGetValue(p, out var s) || s == null) return "(not loaded)";
        using var r = s.Resource; using var t = r.QueryInterface<Texture2D>();
        var d = t.Description; return $"{d.Width}x{d.Height} {d.MipLevels} mips {d.Format}";
    }

    /// <summary>Is this texture on the GPU already?</summary>
    public bool IsLoaded(string nameOrPath) { var p = Resolve(nameOrPath); return p != null && cache.ContainsKey(p); }

    /// <summary>Frees textures that were loaded for one picture only (Asset Browser thumbnails): the GPU memory does not grow.</summary>
    public void Unload(IEnumerable<string> namesOrPaths)
    {
        bool any = false;
        foreach (var n in namesOrPaths) { var p = Resolve(n); if (p != null && cache.Remove(p, out var s)) { s?.Dispose(); Untrack(p); any = true; } }
        if (any) Epoch++;
    }

    /// <summary>Drops loaded thumbnails (Refresh of the Asset Browser: the pictures are made again).</summary>
    public void ForgetThumbs(IEnumerable<string> paths)
    {
        foreach (var p in paths) { if (thumbs.Remove(p, out var s)) s?.Dispose(); thumbFailed.Remove(p); }
    }

    /// <summary>96 px thumbnail (small on the GPU even with thousands of materials).</summary>
    ShaderResourceView LoadThumb(string path)
    {
        const int T = 96;
        Bitmap src;
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var img = MoloMax.Core.Gta.Library.TextureReader.Load(path, 256) ?? throw new InvalidDataException("dds");
            src = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = src.LockBits(new System.Drawing.Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bgra = new byte[img.Pixels.Length];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = img.Pixels[i + 2]; bgra[i + 1] = img.Pixels[i + 1]; bgra[i + 2] = img.Pixels[i]; bgra[i + 3] = img.Pixels[i + 3]; }
            Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
            src.UnlockBits(bd);
        }
        else src = new Bitmap(path);
        using (src)
        using (var bmp = new Bitmap(T, T, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                // checker behind transparent decals
                for (int y = 0; y < T; y += 12) for (int x = 0; x < T; x += 12)
                        g.FillRectangle(((x + y) / 12 % 2 == 0) ? Brushes.DimGray : Brushes.Gray, x, y, 12, 12);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, T, T);
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, T, T), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var tex = new Texture2D(device, new Texture2DDescription
                {
                    Width = T, Height = T, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                }, new DataRectangle(data.Scan0, data.Stride));
                return new ShaderResourceView(device, tex);
            }
            finally { bmp.UnlockBits(data); }
        }
    }

    /// <summary>
    /// When above 0 (map mode), .dds files are read in the background and at most this many milliseconds per frame go to
    /// creating textures; until then Get returns null (the surface shows untextured for a few frames, no hitch).
    /// </summary>
    public double FrameBudgetMs;
    double frameLoadMs;
    /// <summary>Milliseconds spent loading textures since start (profiling).</summary>
    public double LoadMs;
    /// <summary>Textures asked for last frame that were still on their way (background read or over the frame budget).</summary>
    public int Pending { get; private set; }
    int pendingNow;
    readonly Dictionary<string, System.Threading.Tasks.Task<ShaderResourceView>> reads = new();
    /// <summary>Textures that arrived from the background reads (MAP mode).</summary>
    public int Arrived;
    // Background texture loads (disk + graphics card): a few threads of their own taking the newest request first (what
    // the camera looks at now). They used to be thread-pool tasks each waiting for its turn: with hundreds of textures
    // asked for at once the pool filled with waiting threads and the model readers behind them starved.
    readonly System.Collections.Concurrent.ConcurrentStack<(string Path, System.Threading.Tasks.TaskCompletionSource<ShaderResourceView> Done)> loadStack = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Path, System.Threading.Tasks.TaskCompletionSource<ShaderResourceView> Done)> loadLater = new();
    readonly System.Threading.SemaphoreSlim loadSignal = new(0);
    System.Threading.Thread[] loaders; volatile bool loadersStop;
    System.Threading.Tasks.Task<ShaderResourceView> QueueLoad(string path)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<ShaderResourceView>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        loadStack.Push((path, done)); loadSignal.Release();
        if (loaders == null)
        {
            loaders = new System.Threading.Thread[Math.Clamp(Environment.ProcessorCount / 3, 2, 4)];
            for (int i = 0; i < loaders.Length; i++) { loaders[i] = new System.Threading.Thread(LoaderLoop) { IsBackground = true, Name = "textures", Priority = System.Threading.ThreadPriority.BelowNormal }; loaders[i].Start(); }
        }
        return done.Task;
    }
    void LoaderLoop()
    {
        while (!loadersStop)
        {
            loadSignal.Wait(200);
            if (loadersStop) break;
            if (!loadStack.TryPop(out var job) && !loadLater.TryDequeue(out job)) continue;
            // a texture of a model just read: its file may still be on its way to the disk; the others go first meanwhile
            if (MoloMax.Core.IO.PendingFiles.IsPending(job.Path))
            {
                loadLater.Enqueue(job); loadSignal.Release();
                if (loadStack.IsEmpty) System.Threading.Thread.Sleep(3);
                continue;
            }
            try { job.Done.TrySetResult(LoadDds(File.ReadAllBytes(job.Path))); }
            catch (Exception e) { job.Done.TrySetException(e); }
        }
    }
    readonly System.Diagnostics.Stopwatch loadSw = new();
    public void BeginFrame() { frameLoadMs = 0; Pending = pendingNow; pendingNow = 0; }

    /// <summary>Returns the texture or null when it is not set / cannot be found.</summary>
    public ShaderResourceView Get(string nameOrPath)
    {
        LastGetPending = false;
        if (nameOrPath != null && dynamic.TryGetValue(nameOrPath, out var dyn)) return dyn.Srv;
        // map mode: finding a texture on disk counts in the frame budget too (thousands of new names when an area streams in)
        if (FrameBudgetMs > 0 && frameLoadMs >= FrameBudgetMs && nameOrPath != null && !resolved.ContainsKey(nameOrPath)) { pendingNow++; LastGetPending = true; return null; }
        double r0 = LoadMs;
        var path = Resolve(nameOrPath);
        frameLoadMs += LoadMs - r0;
        if (path == null) return null;
        if (cache.TryGetValue(path, out var srv)) { if (trimMarking) usedAt[path] = clock.Elapsed.TotalSeconds; return srv; }
        if (failed.Contains(path)) return null;
        bool dds = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = null;
        if (FrameBudgetMs > 0 && dds)
        {
            // MAP mode: the file is read AND put on the graphics card by a background thread (creating a texture is
            // allowed from any thread in Direct3D 11); this thread only takes the finished one, so nothing waits on it
            if (!reads.TryGetValue(path, out var t)) { var pth = path; reads[path] = QueueLoad(pth); pendingNow++; LastGetPending = true; return null; }
            if (!t.IsCompleted) { pendingNow++; LastGetPending = true; return null; }
            reads.Remove(path);
            if (t.IsFaulted) { failed.Add(path); Epoch++; Errors.Add($"{Path.GetFileName(path)}: {t.Exception?.GetBaseException().Message}"); return null; }
            // no Epoch change here: only the materials still waiting for a texture look again (they do each frame); a change
            // of Epoch made every material of the map look up all its textures again each time one texture arrived
            cache[path] = t.Result; Arrived++; Track(path, t.Result);
            return t.Result;
        }
        loadSw.Restart();
        try
        {
            MoloMax.Core.IO.PendingFiles.Wait(path);
            srv = dds ? LoadDds(bytes ?? File.ReadAllBytes(path)) : LoadBitmap(path);
            cache[path] = srv; Epoch++; Track(path, srv);
            return srv;
        }
        catch (Exception e)
        {
            failed.Add(path); Epoch++;
            Errors.Add($"{Path.GetFileName(path)}: {e.Message}");
            return null;
        }
        finally { double ms = loadSw.Elapsed.TotalMilliseconds; frameLoadMs += ms; LoadMs += ms; }
    }

    // ---- memory: textures nothing has drawn for a while leave the graphics card (MAP mode: a tour of the map read every
    // texture of every district and kept them all, 20 GB after six districts) ----
    readonly Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, double> usedAt = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    bool trimMarking; double trimMarkAt, trimLast = -100;
    /// <summary>Bytes of the textures on the graphics card (those read from files).</summary>
    public long GpuBytes { get; private set; }
    /// <summary>Textures let go so far by <see cref="Trim"/>.</summary>
    public int Trimmed { get; private set; }

    void Track(string path, ShaderResourceView srv)
    {
        long n = 0;
        try
        {
            using var res = srv.Resource; using var tex = res.QueryInterfaceOrNull<Texture2D>();
            if (tex != null)
            {
                var d = tex.Description; long bits = SharpDX.DXGI.FormatHelper.SizeOfInBits(d.Format);
                n = (long)d.Width * d.Height * bits / 8 * d.ArraySize; if (d.MipLevels != 1) n = n * 4 / 3;
            }
        }
        catch { }
        Untrack(path); sizes[path] = n; GpuBytes += n; usedAt[path] = clock.Elapsed.TotalSeconds;
    }

    void Untrack(string path) { if (sizes.Remove(path, out var n)) GpuBytes -= n; usedAt.Remove(path); }

    /// <summary>Call once a frame. Over <paramref name="budget"/> bytes: every material is asked to look its textures up
    /// again, and a few seconds later the .dds textures no material asked for are let go (read again from their file if
    /// they come back in view). Under the budget nothing happens.</summary>
    public void Trim(long budget)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (!trimMarking)
        {
            if (GpuBytes <= budget || now - trimLast < 20) return;
            trimMarking = true; trimMarkAt = now; Epoch++;
            return;
        }
        if (now - trimMarkAt < 5) return;
        trimMarking = false; trimLast = now;
        List<string> gone = null;
        foreach (var kv in usedAt)
            if (kv.Value < trimMarkAt && kv.Key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) (gone ??= new()).Add(kv.Key);
        if (gone == null) return;
        foreach (var path in gone) { if (cache.Remove(path, out var srv)) { srv?.Dispose(); Trimmed++; } Untrack(path); }
        Epoch++; // a material not drawn these seconds may still hold one of them: it looks again when it is drawn
    }

    public void Reload(string nameOrPath)
    {
        resolved.Clear(); Epoch++;
        var path = Resolve(nameOrPath);
        if (path == null) return;
        if (cache.Remove(path, out var old)) { old.Dispose(); Untrack(path); }
        failed.Remove(path);
        if (reads.Remove(path, out var pend)) pend.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); });
    }

    ShaderResourceView LoadBitmap(string path)
    {
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(w, h)));
            var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = w, Height = h, MipLevels = mips, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
            });
            device.ImmediateContext.UpdateSubresource(new DataBox(data.Scan0, data.Stride, 0), tex, 0);
            var srv = new ShaderResourceView(device, tex);
            device.ImmediateContext.GenerateMips(srv);
            tex.Dispose();
            return srv;
        }
        finally { bmp.UnlockBits(data); }
    }

    // ---------------------------------------------------------------- DDS

    static uint FourCC(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

    /// <summary>A DDS in a small plain format, first level only, spread to 32-bit (grey -> grey, alpha only -> white with
    /// that alpha, grey + alpha, 5-6-5 colour, 24-bit colour).</summary>
    ShaderResourceView LoadDdsSmall(byte[] bytes, int width, int height, int offset, int bits, bool alphaOnly, bool grey)
    {
        int bpp = bits / 8, n = width * height;
        if (width <= 0 || height <= 0 || offset + (long)n * bpp > bytes.Length) throw new InvalidDataException("DDS has no pixel data");
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int p = offset + i * bpp; byte r, g, b, al = 255;
            if (bits == 8) { if (alphaOnly) { r = g = b = 255; al = bytes[p]; } else r = g = b = bytes[p]; }
            else if (bits == 16)
            {
                if (grey) { r = g = b = bytes[p]; al = bytes[p + 1]; }
                else { int v = bytes[p] | bytes[p + 1] << 8; r = (byte)((v >> 11 & 31) * 255 / 31); g = (byte)((v >> 5 & 63) * 255 / 63); b = (byte)((v & 31) * 255 / 31); }
            }
            else { b = bytes[p]; g = bytes[p + 1]; r = bytes[p + 2]; }
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = al;
        }
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            using var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new[] { new DataBox(handle.AddrOfPinnedObject(), width * 4, px.Length) });
            return new ShaderResourceView(device, tex);
        }
        finally { handle.Free(); }
    }

    ShaderResourceView LoadDds(byte[] bytes)
    {
        if (bytes.Length < 128 || BitConverter.ToUInt32(bytes, 0) != 0x20534444) throw new InvalidDataException("not a DDS file");
        int height = BitConverter.ToInt32(bytes, 12), width = BitConverter.ToInt32(bytes, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(bytes, 28));
        uint pfFlags = BitConverter.ToUInt32(bytes, 80), fourcc = BitConverter.ToUInt32(bytes, 84);
        uint rgbBits = BitConverter.ToUInt32(bytes, 88), rMask = BitConverter.ToUInt32(bytes, 92);
        int offset = 128;
        Format fmt; int blockBytes = 0; int bpp = 0;

        if ((pfFlags & 0x4) != 0)
        {
            if (fourcc == FourCC("DXT1")) { fmt = Format.BC1_UNorm; blockBytes = 8; }
            else if (fourcc == FourCC("DXT3")) { fmt = Format.BC2_UNorm; blockBytes = 16; }
            else if (fourcc == FourCC("DXT5")) { fmt = Format.BC3_UNorm; blockBytes = 16; }
            else if (fourcc == FourCC("ATI1") || fourcc == FourCC("BC4U")) { fmt = Format.BC4_UNorm; blockBytes = 8; }
            else if (fourcc == FourCC("ATI2") || fourcc == FourCC("BC5U")) { fmt = Format.BC5_UNorm; blockBytes = 16; }
            else if (fourcc == FourCC("DX10"))
            {
                var dxgi = (Format)BitConverter.ToInt32(bytes, 128);
                offset = 148;
                fmt = dxgi;
                switch (dxgi)
                {
                    case Format.BC1_UNorm: case Format.BC1_UNorm_SRgb: case Format.BC4_UNorm: blockBytes = 8; break;
                    case Format.BC2_UNorm: case Format.BC3_UNorm: case Format.BC3_UNorm_SRgb: case Format.BC5_UNorm:
                    case Format.BC7_UNorm: case Format.BC7_UNorm_SRgb: blockBytes = 16; break;
                    case Format.R8G8B8A8_UNorm: case Format.B8G8R8A8_UNorm: bpp = 4; break;
                    case Format.R8_UNorm: return LoadDdsSmall(bytes, width, height, offset, 8, false, true);
                    case Format.A8_UNorm: return LoadDdsSmall(bytes, width, height, offset, 8, true, false);
                    case Format.R8G8_UNorm: return LoadDdsSmall(bytes, width, height, offset, 16, false, true);
                    default: throw new NotSupportedException($"DDS DXGI format {dxgi}");
                }
            }
            else throw new NotSupportedException("DDS fourCC " + System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(fourcc)));
        }
        else if (rgbBits == 32)
        {
            fmt = rMask == 0x00FF0000 ? Format.B8G8R8A8_UNorm : Format.R8G8B8A8_UNorm; bpp = 4;
            if ((pfFlags & 0x1) == 0 && fmt == Format.B8G8R8A8_UNorm) fmt = Format.B8G8R8X8_UNorm;
        }
        else if (rgbBits is 8 or 16 or 24)
            // small plain formats (grey, alpha only, grey + alpha, 5-6-5, 24-bit): the game uses them for masks and flat colours
            return LoadDdsSmall(bytes, width, height, offset, (int)rgbBits, (pfFlags & 0x2) != 0 && (pfFlags & 0x20040) == 0, (pfFlags & 0x20000) != 0);
        else throw new NotSupportedException($"DDS with {rgbBits}-bit uncompressed pixels");

        // a two-channel block format named in the header over one-channel data (seen in map resources: half the bytes):
        // read as what the data is
        if (blockBytes == 16 && (fmt == Format.BC5_UNorm || fmt == Format.BC3_UNorm))
        {
            long first16 = (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
            if (offset + first16 > bytes.Length && offset + first16 / 2 <= bytes.Length) { blockBytes = 8; fmt = fmt == Format.BC5_UNorm ? Format.BC4_UNorm : Format.BC1_UNorm; }
        }
        // build the mip chain, stopping if the file is shorter than its header promises
        var boxes = new List<DataBox>();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            int w = width, h = height, pos = offset;
            for (int i = 0; i < mips; i++)
            {
                int pitch, size;
                if (blockBytes > 0) { pitch = Math.Max(1, (w + 3) / 4) * blockBytes; size = pitch * Math.Max(1, (h + 3) / 4); }
                else { pitch = w * bpp; size = pitch * h; }
                if (pos + size > bytes.Length) break;
                boxes.Add(new DataBox(handle.AddrOfPinnedObject() + pos, pitch, size));
                pos += size; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            if (boxes.Count == 0) throw new InvalidDataException("DDS has no pixel data");
            using var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = boxes.Count, ArraySize = 1, Format = fmt,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, boxes.ToArray());
            return new ShaderResourceView(device, tex);
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        loadersStop = true;
        foreach (var s in cache.Values) s.Dispose();
        cache.Clear();
        White.Dispose(); Waiting.Dispose(); FlatNormal.Dispose(); DefaultSpec.Dispose();
    }
}
