using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Anti-aliases or upscales the lit image, and keeps the frame for the next one.
/// </summary>
internal sealed class ResolveStage
{
    private readonly VulkanContext _context;
    private readonly DescriptorKit _kit;
    private readonly HistoryCopier _copier;
    private readonly FullscreenPass _taaPass;
    private readonly DescriptorSet _taaSet;

    public ResolveStage(VulkanContext context, DescriptorKit kit, HistoryCopier copier)
    {
        _context = context;
        _kit = kit;
        _copier = copier;
        _taaSet = kit.NewSet(kit.FourTextureLayout);
        _taaPass = new FullscreenPass(context, "taa.frag", GBuffer.HdrFormat, [kit.FrameSetLayout, kit.FourTextureLayout]);
    }

    /// <summary>
    /// Anti-aliases or upscales the lit image, and keeps the frame for the next one. Anti-aliasing blends in the last
    /// frame, and reflections and the screen bounce read it, so it's kept for the next while any wants it. Without
    /// anti-aliasing the lit image is the frame. <paramref name="fsr"/> is the upscaler while FSR draws the picture.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, GBuffer targets,
        (FsrPass Pass, FsrFrame Frame, DescriptorSet MeterSet)? fsr, (bool ReadsAntiAliased, RenderEffects Off) display)
    {
        // FSR keeps its own history, at the window's size, so reflections get the lit image. It's kept every frame,
        // so the last frame's camera and motion stay known to FSR whether reflections are on or not.
        if (fsr is { } upscaler)
        {
            RecordHistoryCopy(cmd, targets, targets.Hdr.Image, drawnJustNow: false);
            upscaler.Pass.Record(cmd, (frameSet, upscaler.MeterSet), (targets.Hdr.Image, targets.Depth.Image, targets.Picture.Resolved.Image), upscaler.Frame);
        }
        else if (display.ReadsAntiAliased)
        {
            ReadOnlySpan<DescriptorSet> taaSets = [frameSet, _taaSet];
            _context.Api.BeginColorTarget(cmd, targets.Picture.Resolved.Image);
            _taaPass.Record(cmd, targets.Picture.Resolved.View, targets.Extent, taaSets);
            RecordHistoryCopy(cmd, targets, targets.Picture.Resolved.Image, drawnJustNow: true);
        }
        else if (!display.Off.HasFlag(RenderEffects.Reflections)
            || (display.Off & (RenderEffects.AmbientOcclusion | RenderEffects.ScreenBounce)) == 0)
        {
            RecordHistoryCopy(cmd, targets, targets.Hdr.Image, drawnJustNow: false);
        }
        else
        {
            targets.Picture.HistoryReady = false;
        }
    }

    /// <summary>Points anti-aliasing at the lit image, the history, depth and motion. Glass has just used depth for testing, and it stays in that layout.</summary>
    public void WriteSet(GBuffer targets)
    {
        ImageSets.Write(_context, _taaSet,
        [
            new(_kit.NearestSampler, targets.Hdr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, targets.Picture.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.NearestSampler, targets.Depth.View, ImageLayout.DepthReadOnlyOptimal),
            new(_kit.NearestSampler, targets.Motion.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
    }

    public void Dispose() => _taaPass.Dispose();

    /// <summary>
    /// Keeps this frame for the next one, and leaves it ready to sample. The frame is still
    /// a target when it was <paramref name="drawnJustNow"/>, and already ready to sample when not.
    /// </summary>
    private void RecordHistoryCopy(CommandBuffer cmd, GBuffer targets, Image frame, bool drawnJustNow)
    {
        _copier.RecordCopy(cmd, frame, targets.Picture.History.Image, targets.Extent, drawnJustNow);
        targets.Picture.HistoryReady = true;
    }
}
