using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>D3D11 device, swap chain (4x MSAA when available), back buffer and depth buffer.</summary>
public sealed class GpuDevice : IDisposable
{
    public Device Device { get; private set; }
    public DeviceContext Context { get; private set; }
    public SwapChain SwapChain { get; private set; }
    public RenderTargetView BackBufferView { get; private set; }
    public DepthStencilView DepthView { get; private set; }
    public ShaderResourceView DepthRead { get; private set; }
    public Texture2D DepthTexture => depthTex;
    public bool FeatureLevel11 => Device.FeatureLevel >= FeatureLevel.Level_11_0;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int SampleCount { get; private set; } = 1;

    Texture2D depthTex;

    public GpuDevice(IntPtr hwnd, int width, int height)
    {
        Width = Math.Max(1, width); Height = Math.Max(1, height);
        var flags = DeviceCreationFlags.BgraSupport;
        Device = new Device(DriverType.Hardware, flags, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1);
        Context = Device.ImmediateContext;

        // 4x MSAA keeps wireframes and edges clean, like the Max viewport.
        foreach (var s in new[] { 4, 2 })
            if (Device.CheckMultisampleQualityLevels(Format.R8G8B8A8_UNorm, s) > 0) { SampleCount = s; break; }

        using var dxgiDevice = Device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDevice.Adapter;
        using var factory = adapter.GetParent<Factory>();
        var desc = new SwapChainDescription
        {
            BufferCount = 1,
            ModeDescription = new ModeDescription(Width, Height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
            IsWindowed = true,
            OutputHandle = hwnd,
            SampleDescription = new SampleDescription(SampleCount, 0),
            SwapEffect = SwapEffect.Discard,
            Usage = Usage.RenderTargetOutput,
        };
        SwapChain = new SwapChain(factory, Device, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        CreateViews();
    }

    void CreateViews()
    {
        using (var back = SwapChain.GetBackBuffer<Texture2D>(0))
            BackBufferView = new RenderTargetView(Device, back);
        depthTex = new Texture2D(Device, new Texture2DDescription
        {
            Width = Width, Height = Height, MipLevels = 1, ArraySize = 1,
            Format = Format.R32G8X24_Typeless, // 32-bit float depth (reversed in MAP views, see DepthMode) + stencil
            SampleDescription = new SampleDescription(SampleCount, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        DepthView = new DepthStencilView(Device, depthTex, new DepthStencilViewDescription
        {
            Format = Format.D32_Float_S8X24_UInt, Dimension = SampleCount > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D,
        });
        // readable by the MAP effects (ambient occlusion), when not bound as the depth buffer
        DepthRead = new ShaderResourceView(Device, depthTex, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float_X8X24_Typeless, Dimension = SampleCount > 1 ? ShaderResourceViewDimension.Texture2DMultisampled : ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 },
        });
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width = width; Height = height;
        Context.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
        BackBufferView?.Dispose(); DepthView?.Dispose(); DepthRead?.Dispose(); depthTex?.Dispose();
        SwapChain.ResizeBuffers(1, Width, Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateViews();
    }

    public void BindBackBuffer()
    {
        Context.OutputMerger.SetRenderTargets(DepthView, BackBufferView);
    }

    /// <summary>Returns false when the window is fully hidden (DXGI_STATUS_OCCLUDED).</summary>
    public bool Present(bool vsync)
    {
        var r = SwapChain.Present(vsync ? 1 : 0, PresentFlags.None);
        return r.Code != 0x087A0001;
    }

    /// <summary>Saves what was rendered this frame (before Present) as a PNG: resolves the MSAA back buffer, reads it back.</summary>
    public void Capture(string path)
    {
        using var back = SwapChain.GetBackBuffer<Texture2D>(0);
        var d = back.Description;
        var sd = new Texture2DDescription
        {
            Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
        };
        using var staging = new Texture2D(Device, sd);
        if (d.SampleDescription.Count > 1)
        {
            var rd = sd; rd.Usage = ResourceUsage.Default; rd.CpuAccessFlags = CpuAccessFlags.None;
            using var resolved = new Texture2D(Device, rd);
            Context.ResolveSubresource(back, 0, resolved, 0, d.Format);
            Context.CopyResource(resolved, staging);
        }
        else Context.CopyResource(back, staging);
        var box = Context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            using var bmp = new System.Drawing.Bitmap(d.Width, d.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, d.Width, d.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            var row = new byte[d.Width * 4];
            for (int y = 0; y < d.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(box.DataPointer + y * box.RowPitch, row, 0, row.Length);
                for (int i = 0; i < row.Length; i += 4) { (row[i], row[i + 2]) = (row[i + 2], row[i]); row[i + 3] = 255; } // RGBA -> BGRA, opaque
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
            }
            bmp.UnlockBits(bd);
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                // video frames: JPEG (a tenth of the size of PNG)
                var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                bmp.Save(path, enc, ep);
            }
            else bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { Context.UnmapSubresource(staging, 0); }
    }

    public void Dispose()
    {
        DepthView?.Dispose(); DepthRead?.Dispose(); depthTex?.Dispose(); BackBufferView?.Dispose();
        SwapChain?.Dispose(); Context?.Dispose(); Device?.Dispose();
    }
}
