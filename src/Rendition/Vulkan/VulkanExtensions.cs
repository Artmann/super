using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

internal static unsafe class VulkanExtensions
{
    public static void Check(this Result result)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan call failed: {result}");
        }
    }

    /// <summary>Records an image layout change along with the access ordering around it.</summary>
    public static void Transition(
        this Vk api,
        CommandBuffer cmd,
        Image image,
        ImageAspectFlags aspect,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        uint mipCount = 1,
        uint baseMip = 0,
        uint baseLayer = 0,
        uint layerCount = 1)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, mipCount, baseLayer, layerCount),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>
    /// Readies an image for a pass to draw over all of it. Targets are shared between
    /// frames in flight, so this waits for the previous frame's sampling to finish.
    /// </summary>
    public static void BeginColorTarget(this Vk api, CommandBuffer cmd, Image image) =>
        api.Transition(cmd, image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

    /// <summary>Makes a finished target ready for the next pass to sample.</summary>
    public static void EndColorTarget(this Vk api, CommandBuffer cmd, Image image) =>
        api.Transition(cmd, image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);

    /// <summary>Sets the viewport and scissor to cover the whole target.</summary>
    public static void SetFullViewport(this Vk api, CommandBuffer cmd, Extent2D extent)
    {
        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
        var scissor = new Rect2D(default, extent);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
    }
}
