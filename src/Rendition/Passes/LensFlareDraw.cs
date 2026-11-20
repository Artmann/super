using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws the lens flare's elements as instanced quads into a half-size HDR target, which tonemapping adds after bloom.
/// lens_flare.vert dims each quad by how much of its light <see cref="LensFlareVisibility"/> saw.
/// </summary>
internal sealed unsafe class LensFlareDraw : IDisposable
{
    private readonly VulkanContext _context;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly GpuBuffer[] _buffers;
    private RenderTarget _target;
    private Extent2D _extent;

    // Whether the target may hold something other than nothing ready to sample: a flare, or nothing at all yet.
    private bool _targetDirty = true;

    public LensFlareDraw(VulkanContext context, DescriptorSetLayout visibility, Extent2D display)
    {
        _context = context;
        _layout = context.CreatePipelineLayout([visibility]);
        _pipeline = CreatePipeline(context, _layout);
        _buffers = new GpuBuffer[FrameRing.Count];
        for (int i = 0; i < _buffers.Length; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(context, (ulong)(LensFlareFrame.MaxQuads * sizeof(FlareQuad)), BufferUsageFlags.VertexBufferBit);
        }

        (_target, _extent) = CreateTarget(context, display);
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
        var api = _context.Api;
        quads.CopyTo(new Span<FlareQuad>(_buffers[slot].Mapped, LensFlareFrame.MaxQuads));
        api.BeginColorTarget(cmd, _target.Image);
        BeginRendering(cmd);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.SetFullViewport(cmd, _extent);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var buffer = _buffers[slot].Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        api.CmdDraw(cmd, 6, (uint)quads.Length, 0, 0);
        api.CmdEndRendering(cmd);
        api.EndColorTarget(cmd, _target.Image);
        _targetDirty = true;
    }

    /// <summary>Clears the target, once, for a frame with no flare, so the tonemap adds nothing.</summary>
    public void RecordSkipped(CommandBuffer cmd)
    {
        if (!_targetDirty)
        {
            return;
        }

        var api = _context.Api;
        api.BeginColorTarget(cmd, _target.Image);
        BeginRendering(cmd);
        api.CmdEndRendering(cmd);
        api.EndColorTarget(cmd, _target.Image);
        _targetDirty = false;
    }

    public void Dispose()
    {
        _target.Dispose();
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        _pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    // Clears the target to nothing, so the flare's elements add onto black.
    private void BeginRendering(CommandBuffer cmd)
    {
        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _target.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)),
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, _extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };
        _context.Api.CmdBeginRendering(cmd, &rendering);
    }

    // Each quad is one instance, added onto the target: the shader writes no alpha, so the premultiplied blend adds.
    private static LivePipeline CreatePipeline(VulkanContext context, PipelineLayout layout)
    {
        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new(0, (uint)sizeof(FlareQuad), VertexInputRate.Instance),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Center))),
            new(1, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.HalfSize))),
            new(2, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Color))),
            new(3, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<FlareQuad>(nameof(FlareQuad.Shape))),
        ];
        return PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "lens_flare.vert",
            FragmentShader = "lens_flare.frag",
            Layout = layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = Format.Undefined,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    // Half the picture's size, at least a pixel: the flare is soft, so a quarter of the pixels is plenty.
    private static (RenderTarget Target, Extent2D Extent) CreateTarget(VulkanContext context, Extent2D display)
    {
        var extent = new Extent2D(Math.Max(display.Width / 2, 1u), Math.Max(display.Height / 2, 1u));
        return (new RenderTarget(context, GBuffer.HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit), extent);
    }
}
