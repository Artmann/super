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

    /// <summary>The formats the window's images can have, the first the display offers taken.</summary>
    public static readonly Format[] Formats = [Format.B8G8R8A8Srgb];

    /// <summary>One per image: presentation holds it until that same image comes back.</summary>
    public readonly Semaphore[] RenderFinished;

    public Swapchain(VulkanContext context, uint width, uint height, bool disableVsync)
    {
        _context = context;
        context.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, context.Surface, out var capabilities).Check();
        var surfaceFormat = ChooseFormat();
        Format = surfaceFormat.Format;
        Extent = ChooseExtent(capabilities, width, height);

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = context.Surface,
            MinImageCount = ImageCount(capabilities),
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, // Transfer: Renderer.Capture.
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = disableVsync ? ChoosePresentMode(AvailablePresentModes()) : PresentModeKHR.FifoKhr,
            Clipped = true,
        };
        context.SwapchainApi.CreateSwapchain(context.Device, &createInfo, null, out Handle).Check();

        Images = GetImages(context, Handle);
        Views = new ImageView[Images.Length];
        RenderFinished = new Semaphore[Images.Length];
        CreatePerImage();
    }

    // A current extent of uint.MaxValue means the window lets us choose.
    private static Extent2D ChooseExtent(in SurfaceCapabilitiesKHR capabilities, uint width, uint height) =>
        capabilities.CurrentExtent.Width != uint.MaxValue
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

    // One more image than the surface needs, where it allows that many.
    private static uint ImageCount(in SurfaceCapabilitiesKHR capabilities)
    {
        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        }

        return imageCount;
    }

    private static Image[] GetImages(VulkanContext context, SwapchainKHR handle)
    {
        uint count = 0;
        context.SwapchainApi.GetSwapchainImages(context.Device, handle, &count, null).Check();
        var images = new Image[count];
        fixed (Image* ptr = images)
        {
            context.SwapchainApi.GetSwapchainImages(context.Device, handle, &count, ptr).Check();
        }

        return images;
    }

    // Each image's view and its RenderFinished semaphore.
    private void CreatePerImage()
    {
        var api = _context.Api;
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < Images.Length; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            api.CreateImageView(_context.Device, &viewInfo, null, out Views[i]).Check();
            api.CreateSemaphore(_context.Device, &semaphoreInfo, null, out RenderFinished[i]).Check();
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

    /// <summary>
    /// The mode for running without vsync. Immediate shows each frame at once, Mailbox
    /// swaps in the newest one at each refresh, and Fifo is the only mode every display has.
    /// </summary>
    internal static PresentModeKHR ChoosePresentMode(ReadOnlySpan<PresentModeKHR> available)
    {
        if (available.Contains(PresentModeKHR.ImmediateKhr))
        {
            return PresentModeKHR.ImmediateKhr;
        }

        return available.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
    }

    private PresentModeKHR[] AvailablePresentModes()
    {
        uint count = 0;
        _context.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _context.Surface, &count, null).Check();
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* ptr = modes)
        {
            _context.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _context.Surface, &count, ptr).Check();
        }

        return modes;
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

        foreach (var wanted in Formats)
        {
            foreach (var format in formats)
            {
                if (format.Format == wanted && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }
        }

        throw new InvalidOperationException("The display does not offer an 8-bit sRGB swapchain format.");
    }
}
