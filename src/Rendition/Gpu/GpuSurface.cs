namespace Rendition.Gpu;

/// <summary>
/// What a frame draws into and shows: a window's swapchain on Vulkan, a canvas in the browser. Each frame acquires an
/// image, draws into it, and presents it. A capture copies a frame's image and is read once that frame is done, on a
/// later frame, as the browser can only read a buffer back that way. See "Presenting" in docs/design.md.
/// </summary>
internal abstract class GpuSurface : IDisposable
{
    private FrameCapture? _requested;

    public abstract GpuFormat Format { get; }

    public abstract int Width { get; }

    public abstract int Height { get; }

    /// <summary>The capture the next frame copies, or null when none is asked for.</summary>
    public FrameCapture? Requested => _requested;

    /// <summary>Makes the surface again for the window's size and vsync, once no frame in flight still uses it.</summary>
    public abstract void Configure(int width, int height, bool vsync);

    /// <summary>
    /// Takes the image the frame in <paramref name="frame"/>'s slot draws into. False when the surface no longer fits
    /// the window: configure it again, and the frame isn't drawn.
    /// </summary>
    public abstract bool Acquire(int frame, out ViewId image);

    /// <summary>Shows the acquired image once the frame is drawn. False when the surface must be configured again.</summary>
    public abstract bool Present();

    /// <summary>Asks for the next frame drawn to be copied. Asking again before a frame copies it gives the same capture.</summary>
    public FrameCapture RequestCapture() => _requested ??= new FrameCapture();

    /// <summary>Takes back a capture no frame copied, so the next frame doesn't copy it either.</summary>
    public void Cancel(FrameCapture capture)
    {
        if (ReferenceEquals(_requested, capture))
        {
            _requested = null;
        }
    }

    /// <summary>
    /// Reads back the captures the frame in <paramref name="frame"/>'s slot copied. The caller has waited for that
    /// frame to finish.
    /// </summary>
    public abstract void Collect(int frame);

    public abstract void Dispose();

    /// <summary>Marks the requested capture as copied by the frame in <paramref name="frame"/>'s slot.</summary>
    protected FrameCapture TakeRequested(int frame)
    {
        var capture = _requested ?? throw new InvalidOperationException("No capture was requested.");
        capture.Frame = frame;
        _requested = null;
        return capture;
    }
}
