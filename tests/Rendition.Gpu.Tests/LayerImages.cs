using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

/// <summary>
/// Square images for the GPU layer's tests, filled and read the way the layer leaves its targets: resting ready to be
/// read by shaders.
/// </summary>
internal static class LayerImages
{
    public static RenderTarget Color(VulkanContext context, int size) => new(
        context, Format.R8G8B8A8Unorm, new Extent2D((uint)size, (uint)size),
        ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
        ImageAspectFlags.ColorBit);

    public static RenderTarget Depth(VulkanContext context, int size) => new(
        context, Format.D32Sfloat, new Extent2D((uint)size, (uint)size),
        ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.DepthBit);

    // Every pixel different, so filtering between them shows.
    public static byte[] Pattern(int size)
    {
        var bytes = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            bytes[i * 4] = (byte)(i * 4);
            bytes[i * 4 + 1] = (byte)(255 - i * 3);
            bytes[i * 4 + 2] = (byte)(i * 37 % 256);
            bytes[i * 4 + 3] = 255;
        }

        return bytes;
    }

    // Leaves the target ready to be read by shaders, as the layer expects.
    public static void Upload(VulkanContext context, RenderTarget target, int size, byte[] pixels) =>
        Upload(context, target.Image, new Extent2D((uint)size, (uint)size), pixels);

    /// <summary>Fills one mip of one layer, <paramref name="extent"/> across, and leaves it ready to be read by shaders.</summary>
    public static unsafe void Upload(VulkanContext context, Image image, Extent2D extent, byte[] pixels, uint mip = 0, uint layer = 0)
    {
        using var buffer = GpuBuffer.CreateHostVisible(context, (ulong)pixels.Length, BufferUsageFlags.TransferSrcBit);
        pixels.CopyTo(new Span<byte>(buffer.Mapped, pixels.Length));
        var api = context.Api;
        var handle = buffer.Handle;
        context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal)
                { BaseMip = mip, BaseLayer = layer },
                (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, layer, 1),
                ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
            };
            api.CmdCopyBufferToImage(cmd, handle, image, ImageLayout.TransferDstOptimal, 1, &region);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal)
                { BaseMip = mip, BaseLayer = layer },
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit), (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        });
    }

    // The layer leaves a target it drew to ready to be read by shaders, and so does this.
    public static byte[] Read(VulkanContext context, RenderTarget target, int size, ImageLayout resting = ImageLayout.ShaderReadOnlyOptimal) =>
        Read(context, target.Image, new Extent2D((uint)size, (uint)size), resting: resting);

    /// <summary>The four bytes a pixel of one mip of one layer, <paramref name="extent"/> across, which must be resting as <paramref name="resting"/> says.</summary>
    public static unsafe byte[] Read(
        VulkanContext context, Image image, Extent2D extent, uint mip = 0, uint layer = 0, ImageLayout resting = ImageLayout.ShaderReadOnlyOptimal)
    {
        int length = (int)(extent.Width * extent.Height * 4);
        using var buffer = GpuBuffer.CreateHostVisible(context, (ulong)length, BufferUsageFlags.TransferDstBit);
        var api = context.Api;
        var handle = buffer.Handle;
        context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
                resting, ImageLayout.TransferSrcOptimal) { BaseMip = mip, BaseLayer = layer },
                (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit),
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, layer, 1),
                ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
            };
            api.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, handle, 1, &region);
            // Back to resting, so the layer can draw to it or read it again.
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, resting) { BaseMip = mip, BaseLayer = layer },
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit),
                (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        });

        return new ReadOnlySpan<byte>(buffer.Mapped, length).ToArray();
    }

    /// <summary>
    /// The bytes recording and playing a list allocates, once warmed up: <paramref name="record"/> fills the list, which
    /// is played 50 times in a row, and the second round of 50 is the one a frame pays.
    /// </summary>
    public static long AllocatedPlaying(VulkanContext context, Gpu.Vulkan.VulkanGpu device, Action<CommandList> record)
    {
        var commands = new CommandList();
        long allocated = 0;
        context.SubmitAndWait(cmd =>
        {
            for (int round = 0; round < 2; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50; i++)
                {
                    commands.Reset();
                    record(commands);
                    device.Play(commands, cmd);
                }

                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        });

        return allocated;
    }
}
