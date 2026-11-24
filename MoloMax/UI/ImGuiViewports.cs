using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ImGuiNET;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.UI;

/// <summary>
/// Floating panels can leave the main window: a window dragged outside it becomes a window of its own on the desktop
/// (another monitor too), and comes back inside when dragged over the main window again (Dear ImGui multi-viewports).
/// Coordinates stay those of the main window (its top-left corner is 0,0), so every panel placed by the editor keeps working:
/// the other windows are only translated by where the main window sits on the desktop.
/// </summary>
public sealed partial class ImGuiBackend
{
    /// <summary>Off: everything stays inside the main window (as before 1.8.2).</summary>
    public static bool ViewportsWanted = true;
    public bool ViewportsOn { get; private set; }

    /// <summary>Screen position of the main window's client area (the origin of ImGui coordinates).</summary>
    System.Drawing.Point origin;
    bool originKnown;

    // delegates kept alive for the native side
    delegate void VpFn(IntPtr vp);
    delegate void VpVecFn(IntPtr vp, Vector2 v);
    delegate void VpOutFn(IntPtr vp, IntPtr outVec);
    delegate byte VpBoolFn(IntPtr vp);
    delegate void VpStrFn(IntPtr vp, IntPtr str);
    delegate void VpArgFn(IntPtr vp, IntPtr arg);
    readonly List<Delegate> keep = new();

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] static extern void ImGuiPlatformIO_Set_Platform_GetWindowPos(IntPtr pio, IntPtr fn);
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] static extern void ImGuiPlatformIO_Set_Platform_GetWindowSize(IntPtr pio, IntPtr fn);

    /// <summary>A desktop window that shows one ImGui viewport.</summary>
    sealed class VpWindow : Form
    {
        public ImGuiBackend Owner2; public uint Id; public ImGuiViewportFlags Flags;
        public SwapChain Swap; public RenderTargetView Rtv;
        public bool NoActivate;
        public VpWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            KeyPreview = true; Text = "Polymorph";
            try { Icon = Splash.AppIcon(); } catch { }
        }
        protected override bool ShowWithoutActivation => NoActivate;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (Flags.HasFlag(ImGuiViewportFlags.NoTaskBarIcon)) cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW
                if (Flags.HasFlag(ImGuiViewportFlags.TopMost)) cp.ExStyle |= 0x8;            // WS_EX_TOPMOST
                if (NoActivate) cp.ExStyle |= 0x08000000;                                    // WS_EX_NOACTIVATE (tooltips, menus)
                return cp;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override bool IsInputKey(Keys keyData) => true;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE && NoActivate) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
    }

    readonly Dictionary<uint, VpWindow> vpWindows = new();
    Control mainHost;
    Factory dxgiFactory;

    unsafe void InitViewports(Control host)
    {
        if (!ViewportsWanted) return;
        mainHost = host;
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports;
        io.ConfigViewportsNoTaskBarIcon = true;   // one Polymorph in the taskbar
        io.ConfigViewportsNoAutoMerge = false;    // over the main window, a panel is drawn inside it again
        io.ConfigViewportsNoDecoration = true;    // ImGui draws the title bar itself

        using (var dx = device.QueryInterface<SharpDX.DXGI.Device>()) using (var ad = dx.Adapter) dxgiFactory = ad.GetParent<Factory>();

        var pio = ImGui.GetPlatformIO();
        T Keep<T>(T d) where T : Delegate { keep.Add(d); return d; }
        IntPtr Fp<T>(T d) where T : Delegate => Marshal.GetFunctionPointerForDelegate(Keep(d));

        pio.Platform_CreateWindow = Fp<VpFn>(Guard(PlatformCreate));
        pio.Platform_DestroyWindow = Fp<VpFn>(Guard(PlatformDestroy));
        pio.Platform_ShowWindow = Fp<VpFn>(Guard(PlatformShow));
        pio.Platform_SetWindowPos = Fp<VpVecFn>((vp, p) => { try { if (Win(vp) is { } w) { var s = new System.Drawing.Point(origin.X + (int)MathF.Round(p.X), origin.Y + (int)MathF.Round(p.Y)); if (w.Location != s) w.Location = s; } } catch { } });
        pio.Platform_SetWindowSize = Fp<VpVecFn>((vp, s) => { try { if (Win(vp) is { } w) { var z = new System.Drawing.Size(Math.Max(1, (int)s.X), Math.Max(1, (int)s.Y)); if (w.ClientSize != z) w.ClientSize = z; } } catch { } });
        pio.Platform_SetWindowFocus = Fp<VpFn>(vp => { try { if (Win(vp) is { } w && !w.NoActivate) w.Activate(); } catch { } });
        pio.Platform_GetWindowFocus = Fp<VpBoolFn>(vp => { try { return (byte)(Win(vp) is { } w ? (Form.ActiveForm == w ? 1 : 0) : (Form.ActiveForm == mainHost ? 1 : 0)); } catch { return 0; } });
        pio.Platform_GetWindowMinimized = Fp<VpBoolFn>(vp => { try { return (byte)(Win(vp) == null && mainHost is Form f && f.WindowState == FormWindowState.Minimized ? 1 : 0); } catch { return 0; } });
        pio.Platform_SetWindowTitle = Fp<VpStrFn>((vp, s) => { });
        ImGuiPlatformIO_Set_Platform_GetWindowPos((IntPtr)pio.NativePtr, Fp<VpOutFn>((vp, o) =>
        {
            var r = Vector2.Zero;
            try { if (Win(vp) is { } w) { var p = w.PointToScreen(System.Drawing.Point.Empty); r = new Vector2(p.X - origin.X, p.Y - origin.Y); } } catch { }
            *(Vector2*)o = r;
        }));
        ImGuiPlatformIO_Set_Platform_GetWindowSize((IntPtr)pio.NativePtr, Fp<VpOutFn>((vp, o) =>
        {
            var r = Vector2.Zero;
            try { if (Win(vp) is { } w) r = new Vector2(w.ClientSize.Width, w.ClientSize.Height); else r = ImGui.GetIO().DisplaySize; } catch { }
            *(Vector2*)o = r;
        }));
        pio.Renderer_CreateWindow = Fp<VpFn>(vp => { });           // the swap chain is made with the window
        pio.Renderer_DestroyWindow = Fp<VpFn>(vp => { });
        pio.Renderer_SetWindowSize = Fp<VpVecFn>((vp, s) => { try { if (Win(vp) is { } w) ResizeSwap(w, (int)s.X, (int)s.Y); } catch { } });
        pio.Renderer_RenderWindow = Fp<VpArgFn>((vp, a) => { try { RenderWindow(vp); } catch { } });
        pio.Renderer_SwapBuffers = Fp<VpArgFn>((vp, a) => { try { if (Win(vp) is { Swap: not null } w) w.Swap.Present(0, PresentFlags.None); } catch { } });

        var main = ImGui.GetMainViewport();
        main.PlatformHandle = main.PlatformHandleRaw = host.Handle;
        UpdateMonitors();
        ViewportsOn = true;
    }

    static VpFn Guard(Action<IntPtr> a) => vp => { try { a(vp); } catch (Exception e) { System.Diagnostics.Debug.WriteLine("viewport: " + e.Message); } };

    unsafe VpWindow Win(IntPtr vp)
    {
        if (vp == IntPtr.Zero) return null;
        var id = ((ImGuiViewport*)vp)->ID;
        return vpWindows.TryGetValue(id, out var w) ? w : null;
    }

    unsafe void PlatformCreate(IntPtr vpPtr)
    {
        var vp = new ImGuiViewportPtr((ImGuiViewport*)vpPtr);
        var w = new VpWindow
        {
            Owner2 = this, Id = vp.ID, Flags = vp.Flags,
            NoActivate = vp.Flags.HasFlag(ImGuiViewportFlags.NoFocusOnAppearing) || vp.Flags.HasFlag(ImGuiViewportFlags.NoInputs),
            Location = new System.Drawing.Point(origin.X + (int)vp.Pos.X, origin.Y + (int)vp.Pos.Y),
            ClientSize = new System.Drawing.Size(Math.Max(1, (int)vp.Size.X), Math.Max(1, (int)vp.Size.Y)),
        };
        if (mainHost is Form mf) w.Owner = mf; // above the main window, minimised with it
        HookViewportInput(w);
        w.CreateControl();
        var h = w.Handle;
        vp.PlatformHandle = vp.PlatformHandleRaw = h;
        vp.PlatformUserData = (IntPtr)vp.ID;
        vpWindows[vp.ID] = w;
        w.Swap = new SwapChain(dxgiFactory, device, new SwapChainDescription
        {
            BufferCount = 1,
            ModeDescription = new ModeDescription(w.ClientSize.Width, w.ClientSize.Height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
            IsWindowed = true, OutputHandle = h, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.Discard, Usage = Usage.RenderTargetOutput,
        });
        dxgiFactory.MakeWindowAssociation(h, WindowAssociationFlags.IgnoreAll);
        using (var back = w.Swap.GetBackBuffer<Texture2D>(0)) w.Rtv = new RenderTargetView(device, back);
    }

    unsafe void PlatformDestroy(IntPtr vpPtr)
    {
        var vp = new ImGuiViewportPtr((ImGuiViewport*)vpPtr);
        if (vpWindows.Remove(vp.ID, out var w))
        {
            w.Rtv?.Dispose(); w.Swap?.Dispose();
            bool hadFocus = Form.ActiveForm == w;
            w.Dispose();
            if (hadFocus && mainHost is Form mf) mf.Activate(); // the keyboard goes back to the main window
        }
        vp.PlatformUserData = IntPtr.Zero; vp.PlatformHandle = vp.PlatformHandleRaw = IntPtr.Zero;
    }

    void PlatformShow(IntPtr vpPtr)
    {
        if (Win(vpPtr) is not { } w) return;
        w.Show();
        if (w.Flags.HasFlag(ImGuiViewportFlags.TopMost)) w.TopMost = true;
    }

    void ResizeSwap(VpWindow w, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (w.Swap == null) return;
        var d = w.Swap.Description.ModeDescription;
        if (d.Width == width && d.Height == height) return;
        ctx.OutputMerger.SetRenderTargets((RenderTargetView)null);
        w.Rtv?.Dispose(); w.Rtv = null;
        w.Swap.ResizeBuffers(1, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        using var back = w.Swap.GetBackBuffer<Texture2D>(0);
        w.Rtv = new RenderTargetView(device, back);
    }

    unsafe void RenderWindow(IntPtr vpPtr)
    {
        if (Win(vpPtr) is not { Rtv: not null } w) return;
        var vp = new ImGuiViewportPtr((ImGuiViewport*)vpPtr);
        ResizeSwap(w, (int)vp.Size.X, (int)vp.Size.Y);
        ctx.OutputMerger.SetRenderTargets(w.Rtv);
        if (!vp.Flags.HasFlag(ImGuiViewportFlags.NoRendererClear))
        {
            var c = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            ctx.ClearRenderTargetView(w.Rtv, new SharpDX.Mathematics.Interop.RawColor4(c.X, c.Y, c.Z, 1));
        }
        RenderDrawData(vp.DrawData);
    }

    /// <summary>Before ImGui.NewFrame: where the main window is, the monitors around it, the mouse over the other windows.</summary>
    unsafe void ViewportsNewFrame()
    {
        if (!ViewportsOn || mainHost == null || mainHost.IsDisposed) return;
        var o = mainHost.PointToScreen(System.Drawing.Point.Empty);
        if (originKnown && o != origin)
        {
            // the main window moved: the other windows stay where they are on the desktop (only their coordinates change)
            var pio = ImGui.GetPlatformIO();
            for (int i = 1; i < pio.Viewports.Size; i++) pio.Viewports[i].PlatformRequestMove = true;
        }
        origin = o; originKnown = true;
        UpdateMonitors();
    }

    IntPtr monitorBuf; int monitorCap;
    unsafe void UpdateMonitors()
    {
        var screens = Screen.AllScreens;
        int sz = sizeof(ImGuiPlatformMonitor);
        if (monitorCap < screens.Length)
        {
            if (monitorBuf != IntPtr.Zero) Marshal.FreeHGlobal(monitorBuf); // ImGui never frees the list: it is ours
            monitorCap = screens.Length; monitorBuf = Marshal.AllocHGlobal(sz * monitorCap);
        }
        var m = (ImGuiPlatformMonitor*)monitorBuf;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds; var wa = screens[i].WorkingArea;
            m[i] = default;
            m[i].MainPos = new Vector2(b.X - origin.X, b.Y - origin.Y); m[i].MainSize = new Vector2(b.Width, b.Height);
            m[i].WorkPos = new Vector2(wa.X - origin.X, wa.Y - origin.Y); m[i].WorkSize = new Vector2(wa.Width, wa.Height);
            m[i].DpiScale = 1f;
        }
        var pio = ImGui.GetPlatformIO();
        pio.NativePtr->Monitors = new ImVector(screens.Length, monitorCap, monitorBuf);
    }

    /// <summary>After the main window is drawn: the windows outside it are moved / resized / drawn.</summary>
    void RenderViewports()
    {
        if (!ViewportsOn) return;
        var rtvs = ctx.OutputMerger.GetRenderTargets(1, out var dsv);
        try
        {
            ImGui.UpdatePlatformWindows();
            ImGui.RenderPlatformWindowsDefault();
        }
        finally
        {
            ctx.OutputMerger.SetRenderTargets(dsv, rtvs);
            foreach (var r in rtvs) r?.Dispose();
            dsv?.Dispose();
        }
        // the cursor shape follows ImGui in every window
        foreach (var w in vpWindows.Values) if (!w.IsDisposed && w.Cursor != (host?.Cursor ?? Cursors.Default)) w.Cursor = host?.Cursor ?? Cursors.Default;
    }

    /// <summary>The panels that are out of the main window right now (self test, status).</summary>
    public int OutsideWindows => vpWindows.Count(w => !w.Value.IsDisposed && w.Value.Visible);
    public IEnumerable<(System.Drawing.Rectangle Screen, uint Id)> OutsideWindowRects => vpWindows.Values.Where(w => !w.IsDisposed && w.Visible).Select(w => (w.Bounds, w.Id)).ToList();

    void HookViewportInput(VpWindow w)
    {
        Vector2 V(MouseEventArgs e) { var p = w.PointToScreen(new System.Drawing.Point(e.X, e.Y)); return new Vector2(p.X - origin.X, p.Y - origin.Y); }
        w.MouseDown += (_, e) => { if (!Live) return; int b = Btn(e.Button); if (b >= 0) { var io = ImGui.GetIO(); io.AddMouseViewportEvent(w.Id); io.AddMousePosEvent(V(e).X, V(e).Y); io.AddMouseButtonEvent(b, true); } };
        w.MouseUp += (_, e) => { if (!Live) return; int b = Btn(e.Button); if (b >= 0) { var io = ImGui.GetIO(); io.AddMousePosEvent(V(e).X, V(e).Y); io.AddMouseButtonEvent(b, false); } };
        w.MouseMove += (_, e) => { if (!Live) return; var v = V(e); ImGui.GetIO().AddMousePosEvent(v.X, v.Y); };
        w.MouseWheel += (_, e) => { if (Live) ImGui.GetIO().AddMouseWheelEvent(0, e.Delta / 120f); };
        w.KeyPress += (_, e) => { if (Live) ImGui.GetIO().AddInputCharacter(e.KeyChar); };
        w.KeyDown += (_, e) => { if (Live) { Key(e, true); e.Handled = true; if (e.Alt) e.SuppressKeyPress = true; } };
        w.KeyUp += (_, e) => { if (Live) Key(e, false); };
        w.GotFocus += (_, _) => { if (Live) ImGui.GetIO().AddFocusEvent(true); };
        // files dropped on a panel out of the window go where a drop on the main window goes
        w.AllowDrop = mainHost?.AllowDrop ?? false;
        w.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        w.DragDrop += (_, e) => DroppedOnViewport?.Invoke(e);
    }

    /// <summary>Files dropped on a window that is out of the main one.</summary>
    public event Action<DragEventArgs> DroppedOnViewport;

    void DisposeViewports()
    {
        foreach (var w in vpWindows.Values) { try { w.Rtv?.Dispose(); w.Swap?.Dispose(); w.Dispose(); } catch { } }
        vpWindows.Clear();
        dxgiFactory?.Dispose(); dxgiFactory = null;
    }
}
