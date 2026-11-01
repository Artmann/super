using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>An image a pass draws into and a later pass samples from.</summary>
internal sealed unsafe class RenderTarget : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocation _allocation;

    public readonly Image Image;
    public readonly ImageView View;
    public readonly Format Format;
    public readonly ImageAspectFlags Aspect;

    public RenderTarget(VulkanContext context, Format format, Extent2D extent, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        _context = context;
        Format = format;
        Aspect = aspect;
        var api = context.Api;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out Image).Check();

        api.GetImageMemoryRequirements(context.Device, Image, out var requirements);
        _allocation = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, Image, _allocation.Memory, _allocation.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();
    }

    public void Dispose()
    {
        _context.Api.DestroyImageView(_context.Device, View, null);
        _context.Api.DestroyImage(_context.Device, Image, null);
        _context.Memory.Free(_allocation);
    }
}
