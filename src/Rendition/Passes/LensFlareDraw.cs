using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws the lens flare's elements as instanced quads into a half-size HDR target, which tonemapping adds after bloom,
/// through the GPU layer. lens_flare.vert dims each quad by how much of its light <see cref="LensFlareVisibility"/>
/// saw. The visibility's set is made outside the layer, so its layout is lent.
/// </summary>
internal sealed unsafe class LensFlareDraw : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly PipelineId _pipeline;
    private readonly FrameBuffers _buffers;
    private readonly AdoptedTarget _adopted;
    private readonly CommandList _commands = new();
    private RenderTarget _target;
    private Extent2D _extent;

    // Whether the target may hold something other than nothing ready to sample: a flare, or nothing at all yet.
    private bool _targetDirty = true;

    public LensFlareDraw((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout visibility, Extent2D display)
    {
        (_context, _gpu) = device;
        _buffers = new FrameBuffers(device, FrameRing.Count, LensFlareFrame.MaxQuads * sizeof(FlareQuad), BufferUsageFlags.VertexBufferBit);
        _adopted = new AdoptedTarget(_gpu);

        // Each quad is one instance, added onto the target: the shader writes no alpha, so the premultiplied blend adds.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("lens_flare.vert"),
            FragmentShader = _gpu.CreateShader("lens_flare.frag"),
            Targets = [new ColorTargetState(VulkanFormats.ToGpu(GBuffer.HdrFormat), BlendState.Premultiplied)],
            Vertices =
            [
                new VertexLayout(sizeof(FlareQuad), PerInstance: true,
                [
                    new(0, VertexFormat.Float32x2, (int)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Center))),
                    new(1, VertexFormat.Float32x2, (int)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.HalfSize))),
                    new(2, VertexFormat.Float32x4, (int)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Color))),
                    new(3, VertexFormat.Float32x4, (int)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Shape))),
                ]),
            ],
        }, [visibility]);

        (_target, _extent) = CreateTarget(_context, display);
    }

    /// <summary>The flare, half the picture's size, ready to sample once a frame has been recorded.</summary>
    public ImageView Result => _target.View;

    /// <summary>Makes the target for a picture of a new size. Only call it while no frame in flight uses it.</summary>
    public void Resize(Extent2D display)
    {
        _target.Dispose();
        (_target, _extent) = CreateTarget(_context, display);
        _targetDirty = true;
    }

    /// <summary>
    /// Copies the quads into the buffer of <paramref name="slot"/>, whose last frame is done with it, and draws them,
    /// reading the visibility from <paramref name="set"/>.
    /// </summary>
    public void Record(CommandBuffer cmd, ReadOnlySpan<FlareQuad> quads, int slot, DescriptorSet set)
    {
        quads.CopyTo(_buffers.Items<FlareQuad>(slot, LensFlareFrame.MaxQuads));
        _commands.Reset();
        Begin();
        _commands.SetPipeline(_pipeline);
        _commands.SetBindGroup(0, _gpu.Adopt(set));
        _commands.SetVertexBuffer(0, _buffers[slot], 0);
        _commands.Draw(6, quads.Length, 0, 0);
        _commands.EndPass();
        _gpu.Play(_commands, cmd);
        _targetDirty = true;
    }

    /// <summary>Clears the target, once, for a frame with no flare, so the tonemap adds nothing.</summary>
    public void RecordSkipped(CommandBuffer cmd)
    {
        if (!_targetDirty)
        {
            return;
        }

        _commands.Reset();
        Begin();
        _commands.EndPass();
        _gpu.Play(_commands, cmd);
        _targetDirty = false;
    }

    public void Dispose()
    {
        _adopted.Dispose();
        _target.Dispose();
        _buffers.Dispose();
        _gpu.Destroy(_pipeline);
    }

    // Clears the target to nothing, so the flare's elements add onto black.
    private void Begin() =>
        _commands.BeginRenderPass([new ColorTarget(_adopted.For(_target, _extent), LoadOp.Clear, default)], null);

    // Half the picture's size, at least a pixel: the flare is soft, so a quarter of the pixels is plenty.
    private static (RenderTarget Target, Extent2D Extent) CreateTarget(VulkanContext context, Extent2D display)
    {
        var extent = new Extent2D(Math.Max(display.Width / 2, 1u), Math.Max(display.Height / 2, 1u));
        return (new RenderTarget(context, GBuffer.HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit), extent);
    }
}
