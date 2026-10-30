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
        uint mipCount = 1)
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
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mipCount, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Sets the viewport and scissor to cover the whole target.</summary>
    public static void SetFullViewport(this Vk api, CommandBuffer cmd, Extent2D extent)
    {
        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
        var scissor = new Rect2D(default, extent);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
    }
}
