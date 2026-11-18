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
        api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, ColorStage, ColorWrite,
            CopyStage, AccessFlags2.TransferReadBit);

        // Glass drawn before may still be reading the last copy.
        api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, AccessFlags2.None,
            CopyStage, AccessFlags2.TransferWriteBit, mipCount: copy.Mips);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        api.CmdCopyImage(cmd, targets.Hdr.Image, ImageLayout.TransferSrcOptimal, copy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        HalveDownMips(api, cmd, copy, targets.Extent);

        if (copy.Mips > 1)
        {
            api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, CopyStage, AccessFlags2.TransferReadBit,
                SampledStage, AccessFlags2.ShaderSampledReadBit, mipCount: copy.Mips - 1);
        }

        api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, CopyStage, AccessFlags2.TransferWriteBit,
            SampledStage, AccessFlags2.ShaderSampledReadBit, baseMip: copy.Mips - 1);

        api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal, CopyStage, AccessFlags2.TransferReadBit,
            ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit);
    }

    // Blits each mip of the copy from the one before it, at half the size. Every mip but the last is left ready to read
    // from by a transfer, and the last ready to write.
    private static void HalveDownMips(Vk api, CommandBuffer cmd, RenderTarget copy, Extent2D extent)
    {
        int width = (int)extent.Width;
        int height = (int)extent.Height;
        for (uint mip = 1; mip < copy.Mips; mip++)
        {
            api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, CopyStage, AccessFlags2.TransferWriteBit,
                CopyStage, AccessFlags2.TransferReadBit, baseMip: mip - 1);

            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);
            api.CmdBlitImage(cmd, copy.Image, ImageLayout.TransferSrcOptimal, copy.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            width = nextWidth;
            height = nextHeight;
        }
    }
}
