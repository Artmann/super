using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>An image a pass draws into and a later pass samples from.</summary>
internal sealed unsafe class RenderTarget : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocation _allocation;

    public readonly Image Image;

    /// <summary>Covers every layer, as a 2D array when there is more than one.</summary>
    public readonly ImageView View;

    /// <summary>One 2D view per layer, for rendering into that layer alone.</summary>
    public readonly ImageView[] LayerViews;

    public readonly Format Format;
    public readonly ImageAspectFlags Aspect;

    public RenderTarget(
        VulkanContext context, Format format, Extent2D extent, ImageUsageFlags usage, ImageAspectFlags aspect, uint layers = 1)
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
            ArrayLayers = layers,
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
            ViewType = layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, layers),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();

        LayerViews = new ImageView[layers];
        for (uint layer = 0; layer < layers; layer++)
        {
            viewInfo.ViewType = ImageViewType.Type2D;
            viewInfo.SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, layer, 1);
            api.CreateImageView(context.Device, &viewInfo, null, out LayerViews[layer]).Check();
        }
    }

    public void Dispose()
    {
        foreach (var view in LayerViews)
        {
            _context.Api.DestroyImageView(_context.Device, view, null);
        }

        _context.Api.DestroyImageView(_context.Device, View, null);
        _context.Api.DestroyImage(_context.Device, Image, null);
        _context.Memory.Free(_allocation);
    }
}
