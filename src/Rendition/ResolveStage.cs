using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Anti-aliases or upscales the lit image, and keeps the frame for the next one. TAA draws through the GPU layer.
/// </summary>
internal sealed class ResolveStage
{
    private readonly VulkanGpu _gpu;
    private readonly HistoryCopier _copier;
    private readonly FullscreenPass _taaPass;
    private readonly CommandList _commands = new();
    private readonly ImageGroup _sources;
    private readonly SamplerId _linear;
    private readonly AdoptedTarget _resolved;

    public ResolveStage(VulkanGpu gpu, DescriptorKit kit, HistoryCopier copier)
    {
        _gpu = gpu;
        _copier = copier;
        _resolved = new AdoptedTarget(gpu);
        _sources = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));
        _linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        // Group 1 is the pass's own, laid out from taa.frag; the frame's set is lent.
        _taaPass = new FullscreenPass(gpu, "taa.frag", [VulkanFormats.ToGpu(GBuffer.HdrFormat)], borrowed: [kit.FrameSetLayout, default]);
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
            RecordHistoryCopy(cmd, targets, targets.Hdr.Image);
            upscaler.Pass.Record(cmd, (frameSet, upscaler.MeterSet), (targets.Hdr.Image, targets.Depth.Image, targets.Picture.Resolved.Image), upscaler.Frame);
        }
        else if (display.ReadsAntiAliased)
        {
            _commands.Reset();
            _taaPass.Record(_commands, [new ColorTarget(_resolved.For(targets.Picture.Resolved, targets.Extent), LoadOp.DontCare, default)],
                [_gpu.Adopt(frameSet), _sources.Id], []);
            _gpu.Play(_commands, cmd);
            // The layer leaves the anti-aliased image ready to sample, as the lit one is.
            RecordHistoryCopy(cmd, targets, targets.Picture.Resolved.Image);
        }
        else if (!display.Off.HasFlag(RenderEffects.Reflections)
            || (display.Off & (RenderEffects.AmbientOcclusion | RenderEffects.ScreenBounce)) == 0)
        {
            RecordHistoryCopy(cmd, targets, targets.Hdr.Image);
        }
        else
        {
            targets.Picture.HistoryReady = false;
        }
    }

    /// <summary>
    /// Points anti-aliasing at the lit image, the history, depth and motion. Glass has just used depth for testing, and
    /// it stays in that layout. Makes the bind group again, so it must only be called while the GPU is idle.
    /// </summary>
    public void WriteSet(GBuffer targets) =>
        _sources.Make(_taaPass.Pipeline, 1,
        [
            ("currentTexture", targets.Hdr.View),
            new GroupImage("historyTexture", targets.Picture.History.View, _linear),
            new GroupImage("depthTexture", targets.Depth.View, Use: GroupImageUse.Depth),
            ("motionTexture", targets.Motion.View),
        ]);

    public void Dispose()
    {
        _sources.Dispose();
        _resolved.Dispose();
        _taaPass.Dispose();
    }

    /// <summary>Keeps this frame, which rests ready to sample, for the next one, and leaves it ready to sample.</summary>
    private void RecordHistoryCopy(CommandBuffer cmd, GBuffer targets, Image frame)
    {
        _copier.RecordCopy(cmd, frame, targets.Picture.History.Image, targets.Extent);
        targets.Picture.HistoryReady = true;
    }
}
