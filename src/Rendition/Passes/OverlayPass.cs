using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws rectangles over the finished picture, for text and interfaces. It comes after
/// anti-aliasing and color grading, so what's drawn here stays exactly as sharp and as
/// colored as it was asked to be. Rounding, borders and clipping all happen per pixel in
/// the shader, so everything stays one draw call in the order it was written. Images
/// come from the renderer's bindless table, so they don't break that either.
/// </summary>
internal sealed unsafe class OverlayPass : IDisposable
{
    /// <summary>Quads past this are dropped. Each character of text is one quad.</summary>
    public const int MaxQuads = OverlayQuad.MaxPerFrame;

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private readonly GpuBuffer[] _buffers;

    public OverlayPass(VulkanContext context, Format targetFormat, DescriptorSetLayout atlasLayout, ResourceTable resources, int framesInFlight)
    {
        _context = context;
        _resources = resources;
        _buffers = new GpuBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(MaxQuads * sizeof(OverlayQuad)), BufferUsageFlags.VertexBufferBit);
        }

        var setLayouts = stackalloc DescriptorSetLayout[] { atlasLayout, resources.Layout };
        var pushConstants = new PushConstantRange(ShaderStageFlags.VertexBit, 0, sizeof(float) * 2);
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new(0, (uint)sizeof(OverlayQuad), VertexInputRate.Instance),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.Position))),
            new(1, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.Size))),
            new(2, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.UvMin))),
            new(3, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.UvMax))),
            new(4, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.Color))),
            new(5, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.BorderColor))),
            new(6, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.Clip))),
            new(7, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.CornerRadius))),
            new(8, 0, Format.R32Sint, (uint)Marshal.OffsetOf<OverlayQuad>(nameof(OverlayQuad.TextureIndex))),
        ];
        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "overlay.vert",
            FragmentShader = "overlay.frag",
            Layout = _layout,
            ColorFormats = [targetFormat],
            DepthFormat = Format.Undefined,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    /// <summary>Copies this frame's quads to the GPU and returns how many fit.</summary>
    public int Upload(int frameIndex, ReadOnlySpan<OverlayQuad> quads)
    {
        var fitting = quads[..Math.Min(quads.Length, MaxQuads)];
        fitting.CopyTo(new Span<OverlayQuad>(_buffers[frameIndex].Mapped, MaxQuads));
        return fitting.Length;
    }

    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, DescriptorSet atlas, int frameIndex, int quadCount)
    {
        var api = _context.Api;
        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, // The picture is already there.
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.SetFullViewport(cmd, extent);
        var sets = stackalloc DescriptorSet[] { atlas, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);

        var screen = stackalloc float[] { extent.Width, extent.Height };
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit, 0, sizeof(float) * 2, screen);

        var buffer = _buffers[frameIndex].Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        api.CmdDraw(cmd, 6, (uint)quadCount, 0, 0);
        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
