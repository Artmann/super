using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>Makes the plain 2D array images and views the light probes keep, in device memory with one mip.</summary>
internal static unsafe class DeviceImages
{
    /// <summary>A 2D image of <paramref name="size"/>'s width, height and layers in device memory, with one mip.</summary>
    public static (Image Image, GpuAllocation Memory) CreateArrayImage(
        this VulkanContext context, (uint Width, uint Height, uint Layers) size, Format format, ImageUsageFlags usage)
    {
        var api = context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(size.Width, size.Height, 1),
            MipLevels = 1,
            ArrayLayers = size.Layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out var image).Check();
        api.GetImageMemoryRequirements(context.Device, image, out var requirements);
        var memory = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, image, memory.Memory, memory.Offset).Check();
        return (image, memory);
    }

    /// <summary>A 2D array view of <paramref name="layers"/>, the first layer and how many, of an image's one mip.</summary>
    public static ImageView CreateArrayView(
        this VulkanContext context, Image image, (Format Format, ImageAspectFlags Aspect) kind, (uint First, uint Count) layers)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2DArray,
            Format = kind.Format,
            SubresourceRange = new ImageSubresourceRange(kind.Aspect, 0, 1, layers.First, layers.Count),
        };
        context.Api.CreateImageView(context.Device, &viewInfo, null, out var view).Check();
        return view;
    }

    /// <summary>Frees an image made by <see cref="CreateArrayImage"/> and its memory.</summary>
    public static void DestroyArrayImage(this VulkanContext context, (Image Image, GpuAllocation Memory) image)
    {
        context.Api.DestroyImage(context.Device, image.Image, null);
        context.Memory.Free(image.Memory);
    }
}
