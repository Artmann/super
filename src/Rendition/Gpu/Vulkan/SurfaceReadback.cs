using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// The captures a <see cref="VulkanSurface"/> copied but hasn't read yet, each in a buffer the CPU reads once the
/// frame that copied it is done.
/// </summary>
internal sealed unsafe class SurfaceReadback(VulkanContext context) : IDisposable
{
    private readonly List<(FrameCapture Capture, GpuBuffer Buffer, Extent2D Extent)> _copied = [];

    /// <summary>
    /// Copies the drawn image, in <c>ColorAttachmentOptimal</c>, into a buffer for <paramref name="capture"/>, leaving
    /// the image in <c>TransferSrcOptimal</c>.
    /// </summary>
    public void Copy(CommandBuffer cmd, Image image, Extent2D extent, FrameCapture capture)
    {
        var api = context.Api;
        var buffer = GpuBuffer.CreateHostVisible(context, (ulong)extent.Width * extent.Height * 4, BufferUsageFlags.TransferDstBit);
        _copied.Add((capture, buffer, extent));
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, &region);

        var hostBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &hostBarrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Reads the captures that frame slot copied, whose fence has been waited on, and makes them ready.</summary>
    public void Collect(int frame)
    {
        for (int i = _copied.Count - 1; i >= 0; i--)
        {
            var (capture, buffer, extent) = _copied[i];
            if (capture.Frame != frame)
            {
                continue;
            }

            capture.Complete(ToRgba(new ReadOnlySpan<byte>(buffer.Mapped, (int)buffer.Size)), (int)extent.Width, (int)extent.Height);
            buffer.Dispose();
            _copied.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        foreach (var (_, buffer, _) in _copied)
        {
            buffer.Dispose();
        }

        _copied.Clear();
    }

    // The swapchain stores BGRA. The picture is read back as RGBA, opaque.
    private static byte[] ToRgba(ReadOnlySpan<byte> bgra)
    {
        var pixels = bgra.ToArray();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }

        return pixels;
    }
}
