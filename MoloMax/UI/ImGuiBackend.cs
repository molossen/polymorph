using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ImGuiNET;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.UI;

/// <summary>Dear ImGui renderer for D3D11 (SharpDX) plus WinForms input forwarding.</summary>
public sealed class ImGuiBackend : IDisposable
{
    readonly Device device;
    readonly DeviceContext ctx;
    Buffer vb, ib;
    int vbSize = 20000, ibSize = 40000;
    VertexShader vs; PixelShader ps; InputLayout layout; Buffer cb;
    BlendState blend; RasterizerState raster; DepthStencilState depth; SamplerState sampler;
    ShaderResourceView fontSrv;
    readonly Dictionary<IntPtr, ShaderResourceView> textures = new();
    int nextTex = 1;

    const string Hlsl = @"
cbuffer vertexBuffer : register(b0) { float4x4 ProjectionMatrix; };
struct VS_INPUT { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct PS_INPUT { float4 pos : SV_POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };
PS_INPUT VSMain(VS_INPUT i) { PS_INPUT o; o.pos = mul(ProjectionMatrix, float4(i.pos.xy, 0, 1)); o.col = i.col; o.uv = i.uv; return o; }
sampler sampler0 : register(s0); Texture2D texture0 : register(t0);
float4 PSMain(PS_INPUT i) : SV_Target { return i.col * texture0.Sample(sampler0, i.uv); }";

    public ImGuiBackend(Device device, Control control)
    {
        this.device = device; ctx = device.ImmediateContext;
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigErrorRecovery = true;
        io.ConfigErrorRecoveryEnableAssert = false;
        unsafe { io.NativePtr->IniFilename = null; }
        LoadFonts();
        CreateObjects();
        Hook(control);
        host = control;
    }

    public ImFontPtr FontBold { get; private set; }
    static IntPtr glyphRanges;

    unsafe void LoadFonts()
    {
        var io = ImGui.GetIO();
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string segoe = Path.Combine(fonts, "segoeui.ttf"), segoeBold = Path.Combine(fonts, "segoeuib.ttf");
        // 3ds Max 2016 uses Segoe UI around 9pt; keep Latin-1 for French names.
        // Latin-1 + Latin Extended-A (œ), punctuation (• … — ’), arrows, maths, shapes: no "?" boxes in the UI
        ushort[] wanted = { 0x0020, 0x00FF, 0x0100, 0x017F, 0x2000, 0x206F, 0x2190, 0x21FF, 0x2200, 0x22FF, 0x25A0, 0x25FF, 0 };
        glyphRanges = System.Runtime.InteropServices.Marshal.AllocHGlobal(wanted.Length * 2); // must live as long as the font atlas
        for (int i = 0; i < wanted.Length; i++) System.Runtime.InteropServices.Marshal.WriteInt16(glyphRanges, i * 2, (short)wanted[i]);
        var ranges = glyphRanges;
        if (File.Exists(segoe)) io.Fonts.AddFontFromFileTTF(segoe, 15f, null, ranges);
        else io.Fonts.AddFontDefault();
        FontBold = File.Exists(segoeBold) ? io.Fonts.AddFontFromFileTTF(segoeBold, 15f, null, ranges) : io.Fonts.Fonts[0];
        // brand fonts: IBM Plex Mono for labels (headers, panel titles), Unbounded for the name
        string plex = Path.Combine(AppContext.BaseDirectory, "Assets", "IBMPlexMono-Medium.ttf"), unb = Path.Combine(AppContext.BaseDirectory, "Assets", "Unbounded-Bold.ttf");
        FontLabel = File.Exists(plex) ? io.Fonts.AddFontFromFileTTF(plex, 14f, null, ranges) : io.Fonts.Fonts[0];
        FontBrand = File.Exists(unb) ? io.Fonts.AddFontFromFileTTF(unb, 22f, null, ranges) : FontBold;
        Ui.LabelFont = FontLabel; Ui.HasLabelFont = File.Exists(plex);
        Ui.BrandFont = FontBrand;
        // panel headers in the GIMS Evo style: Segoe UI Light (big page title, section titles), Semilight for tiles
        string light = Path.Combine(fonts, "segoeuil.ttf"), semi = Path.Combine(fonts, "segoeuisl.ttf");
        Ui.HeaderFont = File.Exists(light) ? io.Fonts.AddFontFromFileTTF(light, 34f, null, ranges) : FontBold;
        Ui.SubHeaderFont = File.Exists(light) ? io.Fonts.AddFontFromFileTTF(light, 22f, null, ranges) : FontBold;
        Ui.TileFont = File.Exists(semi) ? io.Fonts.AddFontFromFileTTF(semi, 16f, null, ranges) : io.Fonts.Fonts[0];
        // tutorial films: big, sharp captions (only loaded while recording / testing)
        if (MoloMax.Editor.EditorApp.SelfTestDir != null)
        {
            string semib = Path.Combine(fonts, "seguisb.ttf");
            Ui.CaptionFont = File.Exists(semib) ? io.Fonts.AddFontFromFileTTF(semib, 40f, null, ranges) : File.Exists(segoeBold) ? io.Fonts.AddFontFromFileTTF(segoeBold, 40f, null, ranges) : FontBold;
            Ui.CaptionSubFont = File.Exists(segoe) ? io.Fonts.AddFontFromFileTTF(segoe, 27f, null, ranges) : io.Fonts.Fonts[0];
            Ui.HasCaptionFont = true;
        }
    }
    public ImFontPtr FontLabel { get; private set; }
    public ImFontPtr FontBrand { get; private set; }

    void CreateObjects()
    {
        using (var b = SharpDX.D3DCompiler.ShaderBytecode.Compile(MoloMax.Rendering.ShaderLoader.AsciiSource(Hlsl), "VSMain", "vs_4_0").Bytecode)
        {
            vs = new VertexShader(device, b);
            layout = new InputLayout(device, b, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new InputElement("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0),
            });
        }
        using (var b = SharpDX.D3DCompiler.ShaderBytecode.Compile(MoloMax.Rendering.ShaderLoader.AsciiSource(Hlsl), "PSMain", "ps_4_0").Bytecode) ps = new PixelShader(device, b);
        cb = new Buffer(device, 64, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true, SourceBlend = BlendOption.SourceAlpha, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One, DestinationAlphaBlend = BlendOption.InverseSourceAlpha, AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        blend = new BlendState(device, bd);
        raster = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsScissorEnabled = true, IsDepthClipEnabled = true });
        depth = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Always });
        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Always,
        });
        unsafe
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* px, out int w, out int h, out _);
            using var tex = new Texture2D(device, new Texture2DDescription
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new SharpDX.DataRectangle((IntPtr)px, w * 4));
            fontSrv = new ShaderResourceView(device, tex);
            io.Fonts.SetTexID(Register(fontSrv));
            io.Fonts.ClearTexData();
        }
    }

    public IntPtr Register(ShaderResourceView srv) { var id = (IntPtr)nextTex++; textures[id] = srv; return id; }
    readonly Dictionary<ShaderResourceView, IntPtr> ids = new();
    /// <summary>ImGui texture id of a view, registered on first use (thumbnails, previews).</summary>
    public IntPtr IdOf(ShaderResourceView srv) { if (!ids.TryGetValue(srv, out var id)) ids[srv] = id = Register(srv); return id; }

    public void NewFrame(int width, int height, float dt)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);
        io.DeltaTime = dt > 0 ? dt : 1f / 60f;
        ImGui.NewFrame();
    }

    Control host; ImGuiMouseCursor lastCursor = ImGuiMouseCursor.Arrow;
    public void Render()
    {
        // OS cursor follows ImGui (resize arrows on splitters and window borders, text beam in fields)
        var mc = ImGui.GetMouseCursor();
        if (host != null && mc != lastCursor)
        {
            lastCursor = mc;
            host.Cursor = mc switch
            {
                ImGuiMouseCursor.ResizeEW => Cursors.SizeWE, ImGuiMouseCursor.ResizeNS => Cursors.SizeNS, ImGuiMouseCursor.ResizeNWSE => Cursors.SizeNWSE,
                ImGuiMouseCursor.ResizeNESW => Cursors.SizeNESW, ImGuiMouseCursor.ResizeAll => Cursors.SizeAll, ImGuiMouseCursor.TextInput => Cursors.IBeam,
                ImGuiMouseCursor.Hand => Cursors.Hand, ImGuiMouseCursor.NotAllowed => Cursors.No, _ => Cursors.Default,
            };
        }
        ImGui.Render();
        var dd = ImGui.GetDrawData();
        if (dd.CmdListsCount == 0 || dd.DisplaySize.X <= 0) return;
        int vsz = Marshal.SizeOf<ImDrawVert>();
        if (vb == null || vbSize < dd.TotalVtxCount) { vb?.Dispose(); vbSize = dd.TotalVtxCount + 5000; vb = null; }
        if (ib == null || ibSize < dd.TotalIdxCount) { ib?.Dispose(); ibSize = dd.TotalIdxCount + 10000; ib = null; }
        vb ??= new Buffer(device, vbSize * vsz, ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        ib ??= new Buffer(device, ibSize * sizeof(ushort), ResourceUsage.Dynamic, BindFlags.IndexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        var vmap = ctx.MapSubresource(vb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        var imap = ctx.MapSubresource(ib, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        unsafe
        {
            byte* vdst = (byte*)vmap.DataPointer, idst = (byte*)imap.DataPointer;
            for (int n = 0; n < dd.CmdListsCount; n++)
            {
                var cl = dd.CmdLists[n];
                int vb2 = cl.VtxBuffer.Size * vsz, ib2 = cl.IdxBuffer.Size * sizeof(ushort);
                System.Buffer.MemoryCopy((void*)cl.VtxBuffer.Data, vdst, vb2, vb2);
                System.Buffer.MemoryCopy((void*)cl.IdxBuffer.Data, idst, ib2, ib2);
                vdst += vb2; idst += ib2;
            }
        }
        ctx.UnmapSubresource(vb, 0); ctx.UnmapSubresource(ib, 0);

        float L = dd.DisplayPos.X, R = dd.DisplayPos.X + dd.DisplaySize.X, T = dd.DisplayPos.Y, B = dd.DisplayPos.Y + dd.DisplaySize.Y;
        var mvp = new Matrix4x4(2f / (R - L), 0, 0, 0, 0, 2f / (T - B), 0, 0, 0, 0, 0.5f, 0, (R + L) / (L - R), (T + B) / (B - T), 0.5f, 1f);
        var cmap = ctx.MapSubresource(cb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        unsafe { *(Matrix4x4*)cmap.DataPointer = mvp; }
        ctx.UnmapSubresource(cb, 0);

        ctx.Rasterizer.SetViewport(0, 0, dd.DisplaySize.X, dd.DisplaySize.Y);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, vsz, 0));
        ctx.InputAssembler.SetIndexBuffer(ib, Format.R16_UInt, 0);
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetSampler(0, sampler);
        ctx.GeometryShader.Set(null);
        ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.OutputMerger.SetDepthStencilState(depth);
        ctx.Rasterizer.State = raster;

        int vo = 0, io2 = 0;
        for (int n = 0; n < dd.CmdListsCount; n++)
        {
            var cl = dd.CmdLists[n];
            for (int i = 0; i < cl.CmdBuffer.Size; i++)
            {
                var cmd = cl.CmdBuffer[i];
                if (cmd.UserCallback != IntPtr.Zero) continue;
                ctx.Rasterizer.SetScissorRectangle((int)(cmd.ClipRect.X - L), (int)(cmd.ClipRect.Y - T), (int)(cmd.ClipRect.Z - L), (int)(cmd.ClipRect.W - T));
                if (textures.TryGetValue(cmd.TextureId, out var srv)) ctx.PixelShader.SetShaderResource(0, srv);
                ctx.DrawIndexed((int)cmd.ElemCount, (int)(cmd.IdxOffset + io2), (int)(cmd.VtxOffset + vo));
            }
            vo += cl.VtxBuffer.Size; io2 += cl.IdxBuffer.Size;
        }
    }

    // ---------------------------------------------------------------- input

    void Hook(Control c)
    {
        // a click can come without a move before it (first click after another window had the mouse, a pen, remote
        // control): the button event carries its own position, so the click lands where it was made, not where the mouse was last seen
        c.MouseDown += (_, e) => { if (!Live) return; int b = Btn(e.Button); if (b >= 0) { ImGui.GetIO().AddMousePosEvent(e.X, e.Y); ImGui.GetIO().AddMouseButtonEvent(b, true); } };
        c.MouseUp += (_, e) => { if (!Live) return; int b = Btn(e.Button); if (b >= 0) { ImGui.GetIO().AddMousePosEvent(e.X, e.Y); ImGui.GetIO().AddMouseButtonEvent(b, false); } };
        c.MouseMove += (_, e) => { if (Live) ImGui.GetIO().AddMousePosEvent(e.X, e.Y); };
        c.MouseWheel += (_, e) => { if (Live) ImGui.GetIO().AddMouseWheelEvent(0, e.Delta / 120f); };
        c.KeyPress += (_, e) => { if (Live) ImGui.GetIO().AddInputCharacter(e.KeyChar); };
        c.KeyDown += (_, e) => { if (Live) Key(e, true); };
        c.KeyUp += (_, e) => { if (Live) Key(e, false); };
        c.LostFocus += (_, _) => { if (Live) ImGui.GetIO().AddFocusEvent(false); };
        c.GotFocus += (_, _) => { if (Live) ImGui.GetIO().AddFocusEvent(true); };
    }

    static int Btn(MouseButtons b) => b switch { MouseButtons.Left => 0, MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => -1 };

    /// <summary>Self test: modifier keys come from posted window messages, not from the physical keyboard state.</summary>
    public static bool SyntheticInput;
    /// <summary>Recent mouse positions a self test posted (other mouse messages are ignored while SyntheticInput is on).</summary>
    static readonly System.Collections.Generic.Queue<System.Drawing.Point> synMouse = new();
    public static void PostedMouse(int x, int y) { lock (synMouse) { synMouse.Enqueue(new(x, y)); while (synMouse.Count > 16) synMouse.Dequeue(); } }
    public static bool ForeignMouse(int x, int y) { if (!SyntheticInput) return false; lock (synMouse) return synMouse.Count > 0 && !synMouse.Contains(new(x, y)); }
    static bool synCtrl, synShift, synAlt;

    static void Key(KeyEventArgs e, bool down)
    {
        var io = ImGui.GetIO();
        if (e.KeyCode == Keys.ControlKey) synCtrl = down;
        if (e.KeyCode == Keys.ShiftKey) synShift = down;
        if (e.KeyCode == Keys.Menu) synAlt = down;
        // self test: only the posted keys count (a person typing on the same PC held Ctrl / Shift and broke the tests)
        io.AddKeyEvent(ImGuiKey.ModCtrl, SyntheticInput ? synCtrl : e.Control); io.AddKeyEvent(ImGuiKey.ModShift, SyntheticInput ? synShift : e.Shift); io.AddKeyEvent(ImGuiKey.ModAlt, SyntheticInput ? synAlt : e.Alt);
        var k = Map(e.KeyCode);
        if (k != ImGuiKey.None) io.AddKeyEvent(k, down);
    }

    static ImGuiKey Map(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return ImGuiKey.A + (key - Keys.A);
        if (key >= Keys.D0 && key <= Keys.D9) return ImGuiKey._0 + (key - Keys.D0);
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return ImGuiKey.Keypad0 + (key - Keys.NumPad0);
        if (key >= Keys.F1 && key <= Keys.F12) return ImGuiKey.F1 + (key - Keys.F1);
        return key switch
        {
            Keys.Tab => ImGuiKey.Tab, Keys.Left => ImGuiKey.LeftArrow, Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow, Keys.Down => ImGuiKey.DownArrow, Keys.PageUp => ImGuiKey.PageUp, Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home, Keys.End => ImGuiKey.End, Keys.Insert => ImGuiKey.Insert, Keys.Delete => ImGuiKey.Delete,
            Keys.Back => ImGuiKey.Backspace, Keys.Space => ImGuiKey.Space, Keys.Enter => ImGuiKey.Enter, Keys.Escape => ImGuiKey.Escape,
            Keys.OemMinus => ImGuiKey.Minus, Keys.Oemplus => ImGuiKey.Equal, Keys.OemPeriod => ImGuiKey.Period, Keys.Oemcomma => ImGuiKey.Comma,
            Keys.Subtract => ImGuiKey.KeypadSubtract, Keys.Add => ImGuiKey.KeypadAdd, Keys.Decimal => ImGuiKey.KeypadDecimal,
            Keys.Oemtilde or Keys.OemQuotes => ImGuiKey.GraveAccent, // ` on QWERTY, ² on AZERTY: view menu
            Keys.ShiftKey => ImGuiKey.LeftShift, Keys.ControlKey => ImGuiKey.LeftCtrl, Keys.Menu => ImGuiKey.LeftAlt,
            _ => ImGuiKey.None,
        };
    }

    bool disposed;
    /// <summary>False once the ImGui context is gone (window events still arrive while the app closes).</summary>
    public bool Live => !disposed && ImGui.GetCurrentContext() != IntPtr.Zero;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        fontSrv?.Dispose(); sampler?.Dispose(); depth?.Dispose(); raster?.Dispose(); blend?.Dispose();
        cb?.Dispose(); layout?.Dispose(); ps?.Dispose(); vs?.Dispose(); ib?.Dispose(); vb?.Dispose();
        ImGui.DestroyContext();
    }
}
