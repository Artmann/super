using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Draws the picture into the window's image at the window's size, with the transition and the overlay over it. The
/// surface readies the image and copies it out for a capture.
/// </summary>
internal sealed unsafe class FinishStage
{
    private readonly FrameRing _ring;
    private readonly GpuTimer _timer;
    private readonly FinishPass _finishPass;
    private readonly ResourceTable _resources;
    private readonly OverlayPass _overlayPass;
    private readonly SceneMarks _marks;
    private readonly VulkanGpu _gpu;
    private readonly CommandList _commands = new();

    public FinishStage(
        (VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, FrameRing ring, GpuTimer timer, (Format Format, ResourceTable Resources) window)
    {
        _ring = ring;
        _timer = timer;
        _resources = window.Resources;
        _finishPass = new FinishPass(device.Gpu, kit, (window.Format, window.Resources.Layout));
        _gpu = device.Gpu;
        var format = VulkanFormats.ToGpu(window.Format);
        _overlayPass = new OverlayPass(device, format, window.Resources, FrameRing.Count);
        _marks = new SceneMarks(device, format, FrameRing.Count);
    }

    /// <summary>Uploads the frame's overlay quads, and returns how many it draws: none before an atlas is set.</summary>
    public int UploadOverlay(ReadOnlySpan<OverlayQuad> quads) => _overlayPass.Upload(_ring.Index, quads);

    /// <summary>Uploads the frame's grid overlays and debug lines, with the camera they're seen through.</summary>
    public void UploadMarks(in FrameView view) => _marks.Upload(_ring.Index, view);

    /// <summary>Checks and packs the frame's grid overlays, throwing before anything is recorded if one can't be drawn.</summary>
    public void PrepareGridOverlays(in FrameView view) => _marks.Prepare(view);

    /// <summary>
    /// Replaces the image that the overlay's quads read from. Only its alpha is used. Waits for the GPU, so it isn't
    /// for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data) => _overlayPass.SetAtlas(data);

    /// <summary>Points the finish pass at the targets' finished picture and the held one.</summary>
    public void WriteSet(GBuffer targets)
    {
        _finishPass.SetSources(targets.Picture.Ldr.View, targets.Picture.Held.View);
        _marks.SetDepth(targets.Depth.View);
    }

    /// <summary>
    /// Draws the picture into the surface's acquired image at the window's size, with the transition and the overlay
    /// over it, and leaves the image to present, copied first when a capture was asked for.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, (VulkanSurface Surface, ViewId Image) window,
        (GBuffer Targets, TransitionConstants Transition) picture, int overlayQuads)
    {
        var (surface, image) = window;
        var extent = picture.Targets.Picture.Display;
        surface.Begin(cmd);

        // From here on the picture is the window's size, which the scene may not be.
        var size = surface.Extent;
        var transition = picture.Transition;
        transition.Style.Z = extent.Width != size.Width || extent.Height != size.Height ? 1f : 0f;
        // The resources' textures are there for the screen effects' paper.
        _finishPass.Record(cmd, image, (frameSet, _resources.Set), new ReadOnlySpan<byte>(&transition, (int)TransitionConstants.Size));
        picture.Targets.Picture.LdrReady = true;
        _timer.Mark(cmd);

        // Timed with the overlay: all three are drawn over the finished picture, the grids under the lines.
        _commands.Reset();
        _marks.Record(_commands, image, size);
        if (overlayQuads > 0)
        {
            _overlayPass.Record(_commands, image, size, _ring.Index, overlayQuads);
        }

        _gpu.Play(_commands, cmd);
        _timer.Mark(cmd);
        surface.End(cmd);
    }

    public void Dispose()
    {
        _overlayPass.Dispose();
        _marks.Dispose();
        _finishPass.Dispose();
    }
}
