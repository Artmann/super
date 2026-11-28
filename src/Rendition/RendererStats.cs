using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What the renderer measured of its last frames: the GPU's times and work, the waits, the triangles, shadow tiles and
/// draw calls drawn, the GPU memory held and the upscaler in use. Read through <see cref="Renderer.Stats"/>.
/// </summary>
public sealed class RendererStats
{
    private readonly VulkanContext _context;
    private readonly GpuTimer _gpuTimer;
    private readonly GpuWorkCounter? _gpuWork;
    private readonly Func<string?> _upscalerVersion;

    internal RendererStats(VulkanContext context, GpuTimer gpuTimer, GpuWorkCounter? gpuWork, Func<string?> upscalerVersion)
    {
        _context = context;
        _gpuTimer = gpuTimer;
        _gpuWork = gpuWork;
        _upscalerVersion = upscalerVersion;
    }

    /// <summary>The FSR version in use, such as "3.1.4", or null while FSR isn't drawing the picture.</summary>
    public string? UpscalerVersion => _upscalerVersion();

    /// <summary>Times from the newest finished frame, while <see cref="RenderSettings.MeasureGpu"/> is on. Empty if the GPU can't time passes.</summary>
    public ReadOnlySpan<GpuPassTime> GpuTimes => _gpuTimer.Times;

    /// <summary>
    /// The shaders each pass ran in the newest finished frame, while <see cref="RenderSettings.MeasureGpu"/> is on. Empty if the GPU
    /// can't count them. A pass that is switched off runs none.
    /// </summary>
    public ReadOnlySpan<GpuPassWork> GpuWork => _gpuWork is { } counter ? counter.Work : default;

    /// <summary>How long the last frame waited for the GPU to finish earlier work and for the display, while <see cref="RenderSettings.MeasureGpu"/> is on.</summary>
    public double WaitMilliseconds { get; internal set; }

    /// <summary>
    /// Triangles in the last frame's camera and shadow passes, while <see cref="RenderSettings.MeasureGpu"/> is on. The
    /// shadows count the sun's cascades and the point and spot light tiles drawn that frame, not the ones kept from
    /// earlier frames.
    /// </summary>
    public (long Camera, long Shadows) DrawnTriangles { get; internal set; }

    /// <summary>
    /// How many point and spot light shadow tiles the last frame drew, and how many it kept as earlier frames drew them,
    /// while <see cref="RenderSettings.MeasureGpu"/> is on.
    /// </summary>
    public (int Drawn, int Kept) ShadowTiles { get; internal set; }

    /// <summary>
    /// The model draw calls the last frame recorded, while <see cref="RenderSettings.MeasureGpu"/> is on: one for each
    /// batch of copies of a model part, in the camera, shadow, reflection and probe passes together.
    /// </summary>
    public int DrawCalls { get; internal set; }

    /// <summary>
    /// GPU memory the renderer holds: the bytes in use, and the bytes taken from the device, which also counts the
    /// free room in its memory blocks.
    /// </summary>
    public (long Used, long Reserved) GpuMemoryBytes => ((long)_context.Memory.UsedBytes, (long)_context.Memory.ReservedBytes);

    /// <summary>
    /// How many times the renderer has stopped until the GPU had nothing left to do, as it must to swap out
    /// something a frame in flight still reads. A frame that does is a hitch, so profiles count them.
    /// </summary>
    public int GpuIdleWaits => _context.Waits.WaitIdleCount;
}
