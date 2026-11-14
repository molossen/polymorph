using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MoloMax.Editor;
using MoloMax.Rendering;
using MoloMax.UI;

namespace MoloMax;

/// <summary>The window: owns the D3D device, ImGui and the editor, and runs the frame loop.</summary>
public sealed class MainForm : Form
{
    GpuDevice gpu;
    ImGuiBackend imgui;
    EditorApp app;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double last;
    readonly string startFile;
    bool inFrame, disposed;
    string lastError;

    public MainForm(string startFile)
    {
        this.startFile = startFile;
        Text = "Polymorph";
        ClientSize = new System.Drawing.Size(1600, 940);
        StartPosition = FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        KeyPreview = true;
        WindowState = FormWindowState.Maximized;
        AllowDrop = true;
        try { Icon = Splash.AppIcon(); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Splash.Step("Starting the 3D view", 0.2f);
        gpu = new GpuDevice(Handle, ClientSize.Width, ClientSize.Height);
        Splash.Step("Loading fonts and interface", 0.4f);
        imgui = new ImGuiBackend(gpu.Device, this);
        Splash.Step("Compiling shaders", 0.6f);
        app = new EditorApp(gpu, imgui, this);
        if (!string.IsNullOrEmpty(startFile)) { Splash.Step("Opening " + Path.GetFileName(startFile), 0.85f); app.OpenFile(startFile); }
        Splash.Step("Ready", 1f);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Splash.End();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (gpu != null && ClientSize.Width > 0 && ClientSize.Height > 0) gpu.Resize(ClientSize.Width, ClientSize.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    // during a self test only the test's own mouse messages count (Windows also sends moves at the real cursor)
    static bool Foreign(MouseEventArgs e) => ImGuiBackend.ForeignMouse(e.X, e.Y);
    protected override void OnMouseDown(MouseEventArgs e) { if (Foreign(e)) return; base.OnMouseDown(e); app?.Input.MouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (Foreign(e)) return; base.OnMouseUp(e); app?.Input.MouseUp(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Foreign(e)) return; base.OnMouseMove(e); app?.Input.MouseMove(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); app?.Input.MouseWheel(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); app?.Input.KeyDown(e); e.Handled = true;
        if (e.Alt) e.SuppressKeyPress = true; // no beep / menu activation on Alt+W, Alt+Q
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); app?.Input.ReleaseAllKeys(); }

    const int WM_SYSCOMMAND = 0x0112, SC_KEYMENU = 0xF100;
    protected override void WndProc(ref Message m)
    {
        // Alt alone (or Alt+MMB orbit) must not put the window in menu mode, it would eat the next shortcut.
        if (m.Msg == WM_SYSCOMMAND && ((long)m.WParam & 0xFFF0) == SC_KEYMENU) return;
        if ((m.Msg == 0x100 || m.Msg == 0x101) && (((long)m.LParam >> 24) & 1) == 0)
        {
            // With NumLock off the numpad sends Home/End/arrows/Delete without the "extended" bit:
            // map them back so numpad views (7, 1, 3, .) work instead of deleting or framing.
            int vk = (int)(long)m.WParam;
            int np = vk switch { 0x2D => 0x60, 0x23 => 0x61, 0x28 => 0x62, 0x22 => 0x63, 0x25 => 0x64, 0x0C => 0x65, 0x27 => 0x66, 0x24 => 0x67, 0x26 => 0x68, 0x21 => 0x69, 0x2E => 0x6E, _ => 0 };
            if (np != 0) m.WParam = (IntPtr)np;
        }
        base.WndProc(ref m);
    }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); app?.Input.KeyUp(e); }
    protected override void OnKeyPress(KeyPressEventArgs e) { base.OnKeyPress(e); app?.Input.KeyChar(e.KeyChar); }

    protected override bool IsInputKey(Keys keyData) => true; // arrows, tab… reach the editor

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) app?.DropFile(f);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (app != null && !app.ClosingForUpdate && !app.ConfirmDiscard()) { e.Cancel = true; return; }
        app?.EndSession();
        base.OnFormClosing(e);
    }

    /// <summary>Message pump that renders whenever the window is idle.</summary>
    public void Run()
    {
        Show();
        Application.Idle += (_, _) =>
        {
            if (!Visible && !IsDisposed && EditorApp.SelfTestDir != null) { EditorApp.FrameStage = "test window was hidden, shown again"; ShowWindow(Handle, 4); }
            while (IsIdle() && !IsDisposed && Visible) Frame();
        };
        Application.Run(this);
    }

    void Frame()
    {
        // Modal dialogs opened from the UI run their own message loop, which raises Idle again:
        // never start a second frame inside the first one.
        if (inFrame || disposed) { if (inFrame && !EditorApp.FrameStage.Contains("nested")) EditorApp.FrameStage += " | Idle raised inside the frame (a nested message loop: dialog, menu, drag)"; return; }
        if (gpu == null || app == null) { System.Threading.Thread.Sleep(15); return; }
        if (WindowState == FormWindowState.Minimized && EditorApp.SelfTestDir != null)
        {
            // a self test needs its frames: someone minimised the test window, bring it back without taking the focus
            EditorApp.FrameStage = $"test window was minimised ({DateTime.Now:HH:mm:ss}), shown again";
            ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
        }
        if (WindowState == FormWindowState.Minimized)
        {
            if (!EditorApp.FrameStage.StartsWith("minimised")) EditorApp.FrameStage = $"minimised (since {DateTime.Now:HH:mm:ss})";
            // minimised: nothing to draw, but scripting / MCP calls must still be answered
            inFrame = true;
            try { app.BackgroundTick(); } catch { } finally { inFrame = false; }
            System.Threading.Thread.Sleep(15);
            return;
        }
        inFrame = true;
        try
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)(now - last); last = now;
            EditorApp.FrameStage = $"inside a frame (started {DateTime.Now:HH:mm:ss.fff})";
            if (!app.Frame(dt)) System.Threading.Thread.Sleep(30); // window hidden: do not spin
            EditorApp.FrameStage = $"between frames (last ended {DateTime.Now:HH:mm:ss.fff})";
        }
        catch (Exception e)
        {
            EditorApp.FrameErrorCount++; EditorApp.LastFrameError = e.ToString();
            // Show each distinct error once instead of a dialog every frame.
            if (e.Message != lastError)
            {
                lastError = e.Message;
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "molomax_errors.txt"), $"{DateTime.Now}: {e}\n\n"); } catch { }
                if (EditorApp.SelfTestDir != null) File.AppendAllText(Path.Combine(EditorApp.SelfTestDir, "frame_errors.txt"), $"{DateTime.Now}: {e}\n\n");
                else MessageBox.Show(this, $"{e.Message}\n\nThe scene is still open. Details: molomax_errors.txt", "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally { inFrame = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage { public IntPtr Handle; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public System.Drawing.Point Location; }

    [DllImport("user32.dll")]
    static extern int PeekMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    static bool IsIdle() => PeekMessage(out _, IntPtr.Zero, 0, 0, 0) == 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            app?.Dispose(); app = null;
            imgui?.Dispose(); imgui = null;
            gpu?.Dispose(); gpu = null;
        }
        base.Dispose(disposing);
    }
}
