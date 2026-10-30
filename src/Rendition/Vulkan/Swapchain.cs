using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition.Vulkan;

/// <summary>The chain of images shown in the window, rebuilt whenever the window resizes.</summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext _context;

    public readonly SwapchainKHR Handle;
    public readonly Format Format;
    public readonly Extent2D Extent;
    public readonly Image[] Images;
    public readonly ImageView[] Views;

    /// <summary>One per image: presentation holds it until that same image comes back.</summary>
    public readonly Semaphore[] RenderFinished;

    public Swapchain(VulkanContext context, uint width, uint height)
    {
        _context = context;
        var api = context.Api;
        var surfaceApi = context.SurfaceApi;

        surfaceApi.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, context.Surface, out var capabilities).Check();
        var surfaceFormat = ChooseFormat();
        Format = surfaceFormat.Format;

        // A current extent of uint.MaxValue means the window lets us choose.
        Extent = capabilities.CurrentExtent.Width != uint.MaxValue
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        }

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = context.Surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };
        context.SwapchainApi.CreateSwapchain(context.Device, &createInfo, null, out Handle).Check();

        uint count = 0;
        context.SwapchainApi.GetSwapchainImages(context.Device, Handle, &count, null).Check();
        Images = new Image[count];
        fixed (Image* ptr = Images)
        {
            context.SwapchainApi.GetSwapchainImages(context.Device, Handle, &count, ptr).Check();
        }

        Views = new ImageView[count];
        RenderFinished = new Semaphore[count];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            api.CreateImageView(context.Device, &viewInfo, null, out Views[i]).Check();
            api.CreateSemaphore(context.Device, &semaphoreInfo, null, out RenderFinished[i]).Check();
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < Views.Length; i++)
        {
            _context.Api.DestroySemaphore(_context.Device, RenderFinished[i], null);
            _context.Api.DestroyImageView(_context.Device, Views[i], null);
        }

        _context.SwapchainApi.DestroySwapchain(_context.Device, Handle, null);
    }

    private SurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        _context.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _context.Surface, &count, null).Check();
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* ptr = formats)
        {
            _context.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _context.Surface, &count, ptr).Check();
        }

        foreach (var format in formats)
        {
            if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }

        throw new InvalidOperationException("The display does not offer an 8-bit sRGB swapchain format.");
    }
}
