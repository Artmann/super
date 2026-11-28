using Rendition.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// A swapchain with its images adopted into the layer, one each, as held: the layer leaves their layouts to
/// <see cref="VulkanSurface"/>, which moves them in and out of drawing.
/// </summary>
internal sealed class HeldSwapchain : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private AdoptedTarget[] _images = [];

    public HeldSwapchain(VulkanContext context, VulkanGpu gpu, (int Width, int Height, bool Vsync) window)
    {
        _context = context;
        _gpu = gpu;
        Chain = Make(window);
    }

    public Swapchain Chain { get; private set; }

    /// <summary>Makes the swapchain again at a new size or vsync. The caller waits for the GPU to be idle first.</summary>
    public void Remake((int Width, int Height, bool Vsync) window)
    {
        Dispose();
        Chain = Make(window);
    }

    /// <summary>The image at <paramref name="index"/>, as a view the layer draws into.</summary>
    public ViewId View(uint index) => _images[index].ForHeld(Chain.Views[index], Chain.Images[index], Chain.Extent);

    public void Dispose()
    {
        foreach (var image in _images)
        {
            image.Dispose();
        }

        Chain.Dispose();
    }

    private Swapchain Make((int Width, int Height, bool Vsync) window)
    {
        var swapchain = new Swapchain(_context, (uint)window.Width, (uint)window.Height, !window.Vsync);
        _images = new AdoptedTarget[swapchain.Images.Length];
        for (int i = 0; i < _images.Length; i++)
        {
            _images[i] = new AdoptedTarget(_gpu);
        }

        return swapchain;
    }
}
