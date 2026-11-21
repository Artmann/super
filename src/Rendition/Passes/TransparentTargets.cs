using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the transparent pass records on the targets themselves: rendering into Hdr over read-only depth, and
/// the copy of the scene glass looks through.
/// </summary>
internal static unsafe class TransparentTargets
{
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    private const PipelineStageFlags2 CopyStage = PipelineStageFlags2.AllTransferBit;
    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    /// <summary>Begins rendering into Hdr, with depth attached read-only.</summary>
    public static void Begin(Vk api, CommandBuffer cmd, GBuffer targets)
    {
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Hdr.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthReadOnlyOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.None,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);
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
}
