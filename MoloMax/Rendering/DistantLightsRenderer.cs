using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>The game's distant lights at night (one instanced draw: 6 corners per light, added over the map).</summary>
public sealed class DistantLightsRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vars { public Matrix4x4 ViewProj; public Vector4 Eye, Right, Up; }
    [StructLayout(LayoutKind.Sequential)]
    struct Light { public Vector3 Pos; public uint Col; }

    readonly DeviceContext ctx;
    readonly VertexShader vs;
    readonly PixelShader ps;
    readonly InputLayout layout;
    readonly Buffer cb;
    readonly BlendState add;
    Buffer vb; int count;

    public DistantLightsRenderer(Device device)
    {
        ctx = device.ImmediateContext;
        using (var b = ShaderLoader.Compile("lights.hlsl", "VSMain", "vs_4_0"))
        {
            vs = new VertexShader(device, b);
            layout = new InputLayout(device, b, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
                new InputElement("COLOR", 0, Format.R32_UInt, 12, 0, InputClassification.PerInstanceData, 1),
            });
        }
        using (var b = ShaderLoader.Compile("lights.hlsl", "PSMain", "ps_4_0")) ps = new PixelShader(device, b);
        cb = new Buffer(device, (Marshal.SizeOf<Vars>() + 15) / 16 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var bd = new BlendStateDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true, SourceBlend = BlendOption.One, DestinationBlend = BlendOption.One, BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.Zero, DestinationAlphaBlend = BlendOption.One, AlphaBlendOperation = BlendOperation.Add, RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        add = new BlendState(device, bd);
    }

    public int Count => count;

    public void SetLights(Device device, Vector3[] pos, uint[] col)
    {
        vb?.Dispose(); vb = null; count = Math.Min(pos.Length, col.Length);
        if (count == 0) return;
        var arr = new Light[count];
        for (int i = 0; i < count; i++) arr[i] = new Light { Pos = pos[i], Col = col[i] };
        vb = Buffer.Create(device, BindFlags.VertexBuffer, arr);
    }

    /// <summary>Draws every light (depth read set by the caller); the blend state is set here and left additive.</summary>
    public void Draw(in Vars v)
    {
        if (vb == null) return;
        var x = v; ctx.UpdateSubresource(ref x, cb);
        ctx.OutputMerger.SetBlendState(add, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
        ctx.InputAssembler.InputLayout = layout;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, 16, 0));
        ctx.VertexShader.Set(vs); ctx.VertexShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.Set(ps); ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.GeometryShader.Set(null);
        ctx.DrawInstanced(6, count, 0, 0);
    }

    public void Dispose() { vs.Dispose(); ps.Dispose(); layout.Dispose(); cb.Dispose(); add.Dispose(); vb?.Dispose(); }
}
