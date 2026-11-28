using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the transparent pass draws over: Hdr, with depth attached read-only, both adopted into the GPU layer as held,
/// since <see cref="BlendStage"/> moves them in and out, and the copy of the scene glass looks through, drawn through
/// the layer with its mips.
/// </summary>
internal sealed class TransparentTargets(VulkanGpu gpu, VulkanContext context) : IDisposable
{
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    private readonly AdoptedTarget _hdr = new(gpu);
    private readonly AdoptedTarget _depth = new(gpu);

    // What copies Hdr into the scene copy and halves it, for the targets it was made for. Remade with them.
    private (RenderTarget? For, MipDraws? Draws) _copy;

    /// <summary>Begins a pass over what Hdr holds, with depth loaded read-only, for glass to test against and read.</summary>
    public void Begin(CommandList commands, GBuffer targets)
    {
        var extent = targets.Extent;
        commands.BeginRenderPass(
            [new ColorTarget(_hdr.ForHeld(targets.Hdr.View, targets.Hdr.Image, extent), LoadOp.Load, default)],
            new DepthTarget(_depth.ForHeld(targets.Depth.View, targets.Depth.Image, extent), LoadOp.Load, 0f, ReadOnly: true));
    }

    /// <summary>
    /// Copies Hdr into the scene copy, then halves it down the mips, each blurrier than the last. Hdr arrives as a
    /// colour attachment and is left as one.
    /// </summary>
    public void CopyScene(CommandBuffer cmd, GBuffer targets)
    {
        if (!ReferenceEquals(_copy.For, targets.SceneColor))
        {
            // The old targets are gone, so nothing still reads through these views.
            _copy.Draws?.Dispose();
            var copy = targets.SceneColor;
            _copy = (copy, new MipDraws(gpu, context, new MipImage(copy.Image, GpuFormat.Rgba16Float, targets.Extent, copy.Mips, 1), targets.Hdr.View));
        }

        var api = context.Api;
        api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal), (ColorStage, ColorWrite),
            (SampledStage, AccessFlags2.ShaderSampledReadBit));
        _copy.Draws!.Play(cmd);
        api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal), (SampledStage, AccessFlags2.ShaderSampledReadBit),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
    }

    public void Dispose()
    {
        _copy.Draws?.Dispose();
        _hdr.Dispose();
        _depth.Dispose();
    }
}
