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

    /// <summary>How many mip levels the image has. <see cref="View"/> covers them all.</summary>
    public readonly uint Mips;

    public readonly ImageAspectFlags Aspect;

    /// <summary>
    /// Makes the image and its views. With <paramref name="flags"/> such as
    /// <see cref="ImageCreateFlags.CreateCubeCompatibleBit"/>, the caller can make views of other kinds of it too.
    /// </summary>
    public RenderTarget(
        VulkanContext context, Format format, Extent2D extent, ImageUsageFlags usage, ImageAspectFlags aspect, uint layers = 1, uint mips = 1,
        SampleCountFlags samples = SampleCountFlags.Count1Bit, ImageCreateFlags flags = ImageCreateFlags.None)
    {
        _context = context;
        Format = format;
        Aspect = aspect;
        Mips = mips;
        var api = context.Api;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = flags,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out Image).Check();
        _allocation = BindMemory(context, Image);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, layers),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();
        LayerViews = CreateLayerViews(context, viewInfo, layers);
    }

    private static GpuAllocation BindMemory(VulkanContext context, Image image)
    {
        context.Api.GetImageMemoryRequirements(context.Device, image, out var requirements);
        var allocation = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        context.Api.BindImageMemory(context.Device, image, allocation.Memory, allocation.Offset).Check();
        return allocation;
    }

    // One 2D view of each layer, like the view of them all but for its single layer and first mip.
    private static ImageView[] CreateLayerViews(VulkanContext context, ImageViewCreateInfo viewInfo, uint layers)
    {
        var views = new ImageView[layers];
        var aspect = viewInfo.SubresourceRange.AspectMask;
        for (uint layer = 0; layer < layers; layer++)
        {
            viewInfo.ViewType = ImageViewType.Type2D;
            viewInfo.SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, layer, 1);
            context.Api.CreateImageView(context.Device, &viewInfo, null, out views[layer]).Check();
        }

        return views;
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
