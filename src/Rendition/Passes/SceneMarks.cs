using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the finish stage draws over the finished picture where the scene is: the grid overlays, then the debug lines
/// over them. Both read the scene's depth through one set. See "Grid overlays" and "Debug lines" in docs/design.md.
/// </summary>
internal sealed class SceneMarks : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DescriptorSet _depth;
    private readonly GridOverlayPass _grids;
    private readonly LinePass _lines;

    public SceneMarks(VulkanContext context, DescriptorKit kit, Format targetFormat, int framesInFlight)
    {
        _context = context;
        var layout = kit.OneTextureLayout;
        _depth = kit.NewSet(layout);
        _grids = new GridOverlayPass(context, (layout, _depth), targetFormat, framesInFlight);
        _lines = new LinePass(context, (layout, _depth), targetFormat, framesInFlight);
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
    public void WriteSet(Sampler nearest, ImageView depth) =>
        ImageSets.Write(_context, _depth, [new(nearest, depth, ImageLayout.DepthReadOnlyOptimal)]);

    /// <summary>Draws the grid overlays, then the lines, over <paramref name="target"/>, already in the colour attachment layout.</summary>
    public void Record(CommandBuffer cmd, (ImageView View, Extent2D Extent) target)
    {
        _grids.Record(cmd, target);
        _lines.Record(cmd, target);
    }

    public void Dispose()
    {
        _grids.Dispose();
        _lines.Dispose();
    }
}
