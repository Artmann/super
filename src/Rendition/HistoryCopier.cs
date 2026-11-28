using Rendition.Vulkan;
using Silk.NET.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>Keeps a copy of an image the frame drew, for the next frame to read.</summary>
internal sealed unsafe class HistoryCopier(Vk api)
{
    /// <summary>
    /// Copies <paramref name="frame"/>, which rests ready to sample, into <paramref name="history"/>, an image of the
    /// same size and format that is only ever sampled, and leaves both ready to sample.
    /// </summary>
    public void RecordCopy(CommandBuffer cmd, Image frame, Image history, Extent2D extent)
    {
        api.Transition(cmd, new ImageTransition(frame, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal), (SampledStage, SampledAccess),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));

        api.Transition(cmd, new ImageTransition(history, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal), (SampledStage, SampledAccess),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(extent.Width, extent.Height, 1),
        };
        api.CmdCopyImage(cmd, frame, ImageLayout.TransferSrcOptimal,
            history, ImageLayout.TransferDstOptimal, 1, &region);

        api.Transition(cmd, new ImageTransition(history, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit), (SampledStage, SampledAccess));
        api.Transition(cmd, new ImageTransition(frame, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit), (SampledStage, SampledAccess));
    }
}
