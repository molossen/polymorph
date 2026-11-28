using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>
/// The map's lamps lighting the surfaces around them at night, in the HDR image (one patch per lamp, from the depth:
/// thousands cost little). Needs Direct3D 11 (structured buffer) and a multisampled depth copy.
/// </summary>
public sealed class LampLightRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Lamp
    {
        public Vector3 Position; public float Reach; public Vector3 Color; public float Corona;
        public Vector3 Direction; public float CosOuter; public float CosInner; public uint Hours; public float Spare1, Spare2;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Vars { public Matrix4x4 ViewProj, Unproject; public Vector4 Camera, Right, Up, Origin, Sky; }

    /// <summary>Distance up to which the lamps light the surfaces (m).</summary>
    public const float LitDistance = 300f;

    readonly Device device; readonly DeviceContext ctx;
    readonly VertexShader vs; readonly PixelShader ps; readonly Buffer cb;
    readonly BlendState multiplyAdd; readonly DepthStencilState noDepth; readonly RasterizerState noCull;
    Buffer lamps; ShaderResourceView lampsSrv; int count;

    public LampLightRenderer(Device device)
    {
        this.device = device; ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("lamps.hlsl", "VSLamp", "vs_5_0")) vs = new VertexShader(device, b);
        using (var b = ShaderLoader.Compile("lamps.hlsl", "PSLamp", "ps_5_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Vars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true, SourceBlend = BlendOption.One, DestinationBlend = BlendOption.One, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.Zero, DestinationAlphaBlend = BlendOption.One, AlphaBlendOperation = BlendOperation.Add, RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        multiplyAdd = new BlendState(device, bd);
        noDepth = new DepthStencilState(device, new DepthStencilStateDescription { IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Always });
        noCull = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true, IsScissorEnabled = true });
    }

    public int Count => count;

    /// <summary>The lamps from the game's distant lights (colour packed: intensity, red, green, blue) and their detail.</summary>
    public void SetLamps(Vector3[] pos, uint[] col, MoloMax.Core.Gta.Map.DistantLights.Lamp[] detail)
    {
        lampsSrv?.Dispose(); lamps?.Dispose(); lampsSrv = null; lamps = null;
        count = Math.Min(pos.Length, Math.Min(col.Length, detail.Length));
        if (count == 0) return;
        var arr = new Lamp[count];
        static float Lin(uint b) => MathF.Pow((b & 255) / 255f, 2.2f);
        for (int i = 0; i < count; i++)
        {
            uint c = col[i]; var d = detail[i];
            float brightness = 0.6f + (c & 255) / 255f * 3.4f;
            arr[i] = new Lamp
            {
                Position = pos[i], Reach = d.Reach > 0 ? d.Reach : 8f, Color = new Vector3(Lin(c >> 8), Lin(c >> 16), Lin(c >> 24)) * brightness,
                Corona = d.Corona, Direction = d.Direction, CosOuter = d.CosOuter, CosInner = d.CosInner, Hours = d.Hours,
            };
        }
        lamps = Buffer.Create(device, arr, new BufferDescription
        {
            SizeInBytes = count * Marshal.SizeOf<Lamp>(), BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Immutable,
            OptionFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = Marshal.SizeOf<Lamp>(),
        });
        lampsSrv = new ShaderResourceView(device, lamps, new ShaderResourceViewDescription
        {
            Format = Format.Unknown, Dimension = ShaderResourceViewDimension.Buffer, Buffer = new ShaderResourceViewDescription.BufferResource { FirstElement = 0, ElementCount = count },
        });
    }

    /// <summary>Lights the surfaces drawn so far into the bound image (no depth bound: <paramref name="depth"/> is a copy).
    /// viewProj: the scene's; strength: how far into the night, times the Lamp light setting; sky: the night's sky light the
    /// surfaces were drawn under; scene: a copy of the image so far (what each surface looks like).</summary>
    public void Draw(Matrix4x4 viewProj, Vector3 eye, Vector3 right, Vector3 up, int hour, float strength, Vector3 sky, ShaderResourceView scene, ShaderResourceView depth, float x, float y, float w, float h)
    {
        DepthMode.Rebind(ctx);
        if (lampsSrv == null || depth == null || scene == null || strength <= 0) return;
        var rel = Matrix4x4.CreateTranslation(eye) * viewProj;
        Matrix4x4.Invert(rel, out var unproject);
        var v = new Vars
        {
            ViewProj = rel, Unproject = unproject, Camera = new Vector4(eye, LitDistance), Right = new Vector4(right, strength),
            Up = new Vector4(up, Math.Clamp(hour, 0, 23)), Origin = new Vector4(x, y, w, h), Sky = new Vector4(Vector3.Max(sky, new Vector3(0.02f)), 0),
        };
        ctx.UpdateSubresource(ref v, cb);
        ctx.InputAssembler.InputLayout = null; ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        ctx.VertexShader.Set(vs); ctx.GeometryShader.Set(null); ctx.PixelShader.Set(ps);
        ctx.VertexShader.SetConstantBuffer(0, cb); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.VertexShader.SetShaderResource(0, lampsSrv); ctx.PixelShader.SetShaderResource(0, lampsSrv); ctx.PixelShader.SetShaderResource(1, depth); ctx.PixelShader.SetShaderResource(2, scene);
        ctx.OutputMerger.SetBlendState(multiplyAdd, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.OutputMerger.SetDepthStencilState(noDepth, 0);
        var prevRas = ctx.Rasterizer.State;
        ctx.Rasterizer.State = noCull;
        ctx.Rasterizer.SetScissorRectangle((int)x, (int)y, (int)(x + w), (int)(y + h));
        ctx.DrawInstanced(4, count, 0, 0);
        ctx.VertexShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(0, null); ctx.PixelShader.SetShaderResource(1, null); ctx.PixelShader.SetShaderResource(2, null);
        ctx.OutputMerger.SetBlendState(null, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.OutputMerger.SetDepthStencilState(null, 0);
        ctx.Rasterizer.State = prevRas; prevRas?.Dispose();
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); cb.Dispose(); multiplyAdd.Dispose(); noDepth.Dispose(); noCull.Dispose(); lampsSrv?.Dispose(); lamps?.Dispose(); }
}
