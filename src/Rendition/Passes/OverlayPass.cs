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
    private readonly LivePipeline _pipeline;
    private readonly GpuBuffer[] _buffers;
    private readonly DescriptorSet _atlasSet;
    private GpuTexture? _atlas;

    public OverlayPass(VulkanContext context, DescriptorKit kit, Format targetFormat, ResourceTable resources, int framesInFlight)
    {
        _context = context;
        _resources = resources;
        var atlasLayout = kit.OneTextureLayout;
        _atlasSet = kit.NewSet(atlasLayout);
        _buffers = new GpuBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(MaxQuads * sizeof(OverlayQuad)), BufferUsageFlags.VertexBufferBit);
        }

        _layout = context.CreatePipelineLayout(
            [atlasLayout, resources.Layout],
            new PushConstantRange(ShaderStageFlags.VertexBit, 0, sizeof(float) * 2));
        _pipeline = CreatePipeline(context, _layout, targetFormat);
    }

    // Each quad is one instance of the vertex buffer, blended over the picture.
    private static LivePipeline CreatePipeline(VulkanContext context, PipelineLayout layout, Format targetFormat)
    {
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
        return PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "overlay.vert",
            FragmentShader = "overlay.frag",
            Layout = layout,
            ColorFormats = [targetFormat],
            DepthFormat = Format.Undefined,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    /// <summary>
    /// Replaces the image that the quads read from. Only its alpha is used. Waits for the GPU, so it isn't for every
    /// frame.
    /// </summary>
    public void SetAtlas(Sampler linear, TextureData data)
    {
        _context.WaitIdle();
        _atlas?.Dispose();
        _atlas = new GpuTexture(_context, data);
        ImageSets.Write(_context, _atlasSet, [new(linear, _atlas.View, ImageLayout.ShaderReadOnlyOptimal)]);
    }

    /// <summary>Copies this frame's quads to the GPU and returns how many it draws: none before an atlas is set.</summary>
    public int Upload(int frameIndex, ReadOnlySpan<OverlayQuad> quads)
    {
        if (_atlas is null)
        {
            return 0;
        }

        var fitting = quads[..Math.Min(quads.Length, MaxQuads)];
        fitting.CopyTo(new Span<OverlayQuad>(_buffers[frameIndex].Mapped, MaxQuads));
        return fitting.Length;
    }

    /// <summary>Draws the frame's quads over <paramref name="target"/>, a picture already in the colour attachment layout.</summary>
    public void Record(CommandBuffer cmd, (ImageView View, Extent2D Extent) target, int frameIndex, int quadCount)
    {
        var api = _context.Api;
        var extent = target.Extent;
        api.BeginOver(cmd, target);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var sets = stackalloc DescriptorSet[] { _atlasSet, _resources.Set };
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

        _atlas?.Dispose();
        _pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
