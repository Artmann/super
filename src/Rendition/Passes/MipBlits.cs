using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Fills a target's mips on the GPU by halving each from the one before, each blurrier than the last, and leaves them
/// all for fragment shaders to sample. Glass looks through such a copy of the scene, and the planar reflection blurs
/// its mirror picture with one.
/// </summary>
internal static unsafe class MipBlits
{
    private const PipelineStageFlags2 CopyStage = PipelineStageFlags2.AllTransferBit;
    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    /// <summary>
    /// Halves the first mip of every layer of <paramref name="target"/>, <paramref name="extent"/> across, down to the
    /// last, and leaves every mip ready to sample. Every mip must be ready to be written by a transfer.
    /// </summary>
    public static void Fill(Vk api, CommandBuffer cmd, RenderTarget target, Extent2D extent, uint layers = 1)
    {
        int width = (int)extent.Width;
        int height = (int)extent.Height;
        for (uint mip = 1; mip < target.Mips; mip++)
        {
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal) { BaseMip = mip - 1, LayerCount = layers },
                (CopyStage, AccessFlags2.TransferWriteBit), (CopyStage, AccessFlags2.TransferReadBit));

            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, layers),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, layers),
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);
            api.CmdBlitImage(cmd, target.Image, ImageLayout.TransferSrcOptimal, target.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            width = nextWidth;
            height = nextHeight;
        }

        if (target.Mips > 1)
        {
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal) { MipCount = target.Mips - 1, LayerCount = layers },
                (CopyStage, AccessFlags2.TransferReadBit), (SampledStage, AccessFlags2.ShaderSampledReadBit));
        }

        api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal) { BaseMip = target.Mips - 1, LayerCount = layers },
            (CopyStage, AccessFlags2.TransferWriteBit), (SampledStage, AccessFlags2.ShaderSampledReadBit));
    }
}
