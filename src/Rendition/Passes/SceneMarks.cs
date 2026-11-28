using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the finish stage draws over the finished picture where the scene is: the grid overlays, then the debug lines
/// over them. Both read the scene's depth. See "Grid overlays" and "Debug lines" in docs/design.md.
/// </summary>
internal sealed class SceneMarks : IDisposable
{
    private readonly GridOverlayPass _grids;
    private readonly LinePass _lines;

    public SceneMarks((VulkanContext Context, VulkanGpu Gpu) device, GpuFormat targetFormat, int framesInFlight)
    {
        _grids = new GridOverlayPass(device, targetFormat, framesInFlight);
        _lines = new LinePass(device, targetFormat, framesInFlight);
    }

    /// <summary>Checks and packs the frame's grid overlays, throwing before anything is recorded if one can't be drawn.</summary>
    public void Prepare(in FrameView view) => _grids.Prepare(view);

    /// <summary>Copies the frame's grid overlays and debug lines to the frame in flight's buffers.</summary>
    public void Upload(int frameIndex, in FrameView view)
    {
        _grids.Upload(frameIndex, view);
        _lines.Upload(frameIndex, view);
    }

    /// <summary>Points both at the scene's depth, which they read to find what they lie on and what hides them.</summary>
    public void SetDepth(ImageView depth)
    {
        _grids.SetDepth(depth);
        _lines.SetDepth(depth);
    }

    /// <summary>Draws the grid overlays, then the lines, over <paramref name="target"/>, keeping what it holds.</summary>
    public void Record(CommandList commands, ViewId target, Extent2D extent)
    {
        _grids.Record(commands, target, extent);
        _lines.Record(commands, target, extent);
    }

    public void Dispose()
    {
        _grids.Dispose();
        _lines.Dispose();
    }
}
