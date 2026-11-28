using Silk.NET.Vulkan;
using Rendition.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// A window's swapchain as a <see cref="GpuSurface"/>. Beyond what every surface does, a Vulkan frame waits for its
/// image with <see cref="ImageAvailable"/>, signals <see cref="RenderFinished"/> for presenting, and moves the image
/// in and out of drawing with <see cref="Begin"/> and <see cref="End"/>, which also copies it for a capture.
/// </summary>
internal sealed unsafe class VulkanSurface : GpuSurface
{
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;

    private readonly VulkanContext _context;
    // One per frame slot: the image a frame acquires is there once its slot's semaphore is signalled.
    private readonly Semaphore[] _imageAvailable;
    private readonly SurfaceReadback _readback;
    private readonly HeldSwapchain _swapchain;
    private uint _image;
    private int _frame;

    public VulkanSurface(VulkanContext context, VulkanGpu gpu, int framesInFlight, (int Width, int Height, bool Vsync) window)
    {
        _context = context;
        _readback = new SurfaceReadback(context);
        _imageAvailable = new Semaphore[framesInFlight];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < framesInFlight; i++)
        {
            context.Api.CreateSemaphore(context.Device, &semaphoreInfo, null, out _imageAvailable[i]).Check();
        }

        _swapchain = new HeldSwapchain(context, gpu, window);
    }

    public override GpuFormat Format => VulkanFormats.ToGpu(_swapchain.Chain.Format);

    public override int Width => (int)Extent.Width;

    public override int Height => (int)Extent.Height;

    /// <summary>The swapchain's size, as Vulkan gives it.</summary>
    public Extent2D Extent => _swapchain.Chain.Extent;

    /// <summary>Signalled once the acquired image is there to draw into: the frame's submit waits for it.</summary>
    public Semaphore ImageAvailable => _imageAvailable[_frame];

    /// <summary>What the frame's submit signals, and presenting waits for. One per image, held until it comes back.</summary>
    public Semaphore RenderFinished => _swapchain.Chain.RenderFinished[_image];

    public override void Configure(int width, int height, bool vsync)
    {
        _context.WaitIdle();
        _swapchain.Remake((width, height, vsync));
    }

    public override bool Acquire(int frame, out ViewId image)
    {
        uint index = 0;
        var acquired = _context.SwapchainApi.AcquireNextImage(
            _context.Device, _swapchain.Chain.Handle, ulong.MaxValue, _imageAvailable[frame], default, &index);
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            image = default;
            return false;
        }

        if (acquired != Result.SuboptimalKhr)
        {
            acquired.Check();
        }

        (_frame, _image) = (frame, index);
        image = _swapchain.View(index);
        return true;
    }

    /// <summary>Readies the acquired image to be drawn into, leaving what it held.</summary>
    public void Begin(CommandBuffer cmd) =>
        _context.Api.Transition(cmd, new ImageTransition(_swapchain.Chain.Images[_image], ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
            (ColorStage, AccessFlags2.None), (ColorStage, AccessFlags2.ColorAttachmentWriteBit));

    /// <summary>Copies the drawn image for the capture asked for, if any, and readies it to present.</summary>
    public void End(CommandBuffer cmd)
    {
        var image = _swapchain.Chain.Images[_image];
        var layout = ImageLayout.ColorAttachmentOptimal;
        var drawn = (ColorStage, AccessFlags2.ColorAttachmentWriteBit);
        if (Requested is not null)
        {
            _readback.Copy(cmd, image, Extent, TakeRequested(_frame));
            (layout, drawn) = (ImageLayout.TransferSrcOptimal, (PipelineStageFlags2.CopyBit, AccessFlags2.None));
        }

        _context.Api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, layout, ImageLayout.PresentSrcKhr),
            drawn, (PipelineStageFlags2.None, AccessFlags2.None));
    }

    public override bool Present()
    {
        var handle = _swapchain.Chain.Handle;
        var renderFinished = RenderFinished;
        uint image = _image;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &handle,
            PImageIndices = &image,
        };
        var presented = _context.SwapchainApi.QueuePresent(_context.Queue, &present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            return false;
        }

        presented.Check();
        return true;
    }

    public override void Collect(int frame) => _readback.Collect(frame);

    public override void Dispose()
    {
        _readback.Dispose();
        _swapchain.Dispose();
        foreach (var semaphore in _imageAvailable)
        {
            _context.Api.DestroySemaphore(_context.Device, semaphore, null);
        }
    }
}
