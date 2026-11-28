using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the transparent pass draws over: Hdr, with depth attached read-only, both adopted into the GPU layer as held,
/// since <see cref="BlendStage"/> moves them in and out, and the copy of the scene glass looks through, made by hand.
/// </summary>
internal sealed unsafe class TransparentTargets(VulkanGpu gpu) : IDisposable
{
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    private const PipelineStageFlags2 CopyStage = PipelineStageFlags2.AllTransferBit;
    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    private readonly AdoptedTarget _hdr = new(gpu);
    private readonly AdoptedTarget _depth = new(gpu);

    /// <summary>Begins a pass over what Hdr holds, with depth loaded read-only, for glass to test against and read.</summary>
    public void Begin(CommandList commands, GBuffer targets)
    {
        var extent = targets.Extent;
        commands.BeginRenderPass(
            [new ColorTarget(_hdr.ForHeld(targets.Hdr.View, targets.Hdr.Image, extent), LoadOp.Load, default)],
            new DepthTarget(_depth.ForHeld(targets.Depth.View, targets.Depth.Image, extent), LoadOp.Load, 0f, ReadOnly: true));
    }

    /// <summary>Copies Hdr into the scene copy, then halves it down the mips, each blurrier than the last.</summary>
    public static void CopyScene(Vk api, CommandBuffer cmd, GBuffer targets)
    {
        var copy = targets.SceneColor;
        api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal), (ColorStage, ColorWrite),
            (CopyStage, AccessFlags2.TransferReadBit));

        // Glass drawn before may still be reading the last copy.
        api.Transition(cmd, new ImageTransition(copy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal) { MipCount = copy.Mips }, (SampledStage, AccessFlags2.None),
            (CopyStage, AccessFlags2.TransferWriteBit));

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        api.CmdCopyImage(cmd, targets.Hdr.Image, ImageLayout.TransferSrcOptimal, copy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        MipBlits.Fill(api, cmd, copy, targets.Extent);

        api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal), (CopyStage, AccessFlags2.TransferReadBit),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
    }

    public void Dispose()
    {
        _hdr.Dispose();
        _depth.Dispose();
    }
}
