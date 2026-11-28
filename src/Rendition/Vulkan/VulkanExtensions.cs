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

    /// <summary>
    /// Records an image layout change along with the access ordering around it: work at <paramref name="before"/>
    /// finishes, and its writes are visible to <paramref name="after"/>.
    /// </summary>
    public static void Transition(
        this Vk api,
        CommandBuffer cmd,
        in ImageTransition transition,
        (PipelineStageFlags2 Stage, AccessFlags2 Access) before,
        (PipelineStageFlags2 Stage, AccessFlags2 Access) after)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = before.Stage,
            SrcAccessMask = before.Access,
            DstStageMask = after.Stage,
            DstAccessMask = after.Access,
            OldLayout = transition.From,
            NewLayout = transition.To,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = transition.Image,
            SubresourceRange = new ImageSubresourceRange(
                transition.Aspect, transition.BaseMip, transition.MipCount, transition.BaseLayer, transition.LayerCount),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Records a global memory barrier: work at <paramref name="before"/> finishes and its writes are visible to <paramref name="after"/>.</summary>
    public static void MemoryBarrier(
        this Vk api,
        CommandBuffer cmd,
        (PipelineStageFlags2 Stage, AccessFlags2 Access) before,
        (PipelineStageFlags2 Stage, AccessFlags2 Access) after)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = before.Stage,
            SrcAccessMask = before.Access,
            DstStageMask = after.Stage,
            DstAccessMask = after.Access,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>
    /// Starts a compute pass that keeps a history: waits for last frame's compute and fragment work to finish reading
    /// and writing, then binds the frame set and the pass's own set for this frame.
    /// </summary>
    public static void BeginHistoryCompute(this Vk api, CommandBuffer cmd, PipelineLayout layout, DescriptorSet frameSet, DescriptorSet own)
    {
        const AccessFlags2 used = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit;
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, used),
            (PipelineStageFlags2.ComputeShaderBit, used));

        var sets = stackalloc DescriptorSet[] { frameSet, own };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 2, sets, 0, null);
    }

    /// <summary>
    /// Readies an image for a pass to draw over all of it. Targets are shared between
    /// frames in flight, so this waits for the previous frame's sampling to finish.
    /// </summary>
    public static void BeginColorTarget(this Vk api, CommandBuffer cmd, Image image) =>
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit));

    /// <summary>Makes a finished target ready for the next pass to sample.</summary>
    public static void EndColorTarget(this Vk api, CommandBuffer cmd, Image image) =>
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));

    /// <summary>Makes a picture blended onto ready for fragment and compute shaders, such as the exposure meter, to sample.</summary>
    public static void EndBlendOnto(this Vk api, CommandBuffer cmd, Image image) =>
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));

    /// <summary>Sets the viewport and scissor to cover the whole target.</summary>
    public static void SetFullViewport(this Vk api, CommandBuffer cmd, Extent2D extent)
    {
        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
        var scissor = new Rect2D(default, extent);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
    }

    /// <summary>
    /// Begins drawing over a picture already in the colour attachment layout, keeping what's there, with the viewport
    /// covering it all. End it with CmdEndRendering.
    /// </summary>
    public static void BeginOver(this Vk api, CommandBuffer cmd, (ImageView View, Extent2D Extent) target)
    {
        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, target.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, target.Extent);
    }

    /// <summary>An attachment cleared to <paramref name="clear"/> when drawing begins and kept after it.</summary>
    public static RenderingAttachmentInfo ClearedAttachment(ImageView view, ImageLayout layout, ClearValue clear) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = layout,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
        ClearValue = clear,
    };
}
