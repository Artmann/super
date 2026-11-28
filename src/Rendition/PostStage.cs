using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Blurs what is out of focus, adds bloom and the lens flare, paints the picture, meters the exposure and tonemaps it
/// into Ldr.
/// </summary>
internal sealed class PostStage
{
    private readonly GpuTimer _timer;
    private readonly DepthOfFieldPass _depthOfField;
    private readonly BloomPass _bloom;
    private readonly ExposureClock _clock = new();
    private readonly TonemapPass _tonemap;
    private (DepthOfFieldConstants? Lens, bool Softens) _taken;

    public PostStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, GpuTimer timer, GBuffer targets, ImageView source)
    {
        var context = device.Context;
        _timer = timer;
        _bloom = new BloomPass(device, targets.Picture.Resolved.View, targets.Picture.Display);
        Flare = new LensFlarePass(context, kit.NearestSampler, targets.Picture.Display);
        Flare.SetDepth(targets.Depth.View);
        Meter = new ExposureMeter(context, kit.NearestSampler);
        _depthOfField = new DepthOfFieldPass(device, kit.FrameSetLayout);
        _depthOfField.SetTargets(source, targets.Depth.View, targets.Extent);
        Grade = new GradeLut(context, kit);
        _tonemap = new TonemapPass(device.Gpu, kit, Meter.SetLayout);
        Paint = new PaintPass(device, (kit.NearestSampler, kit.LinearSampler), kit.FrameSetLayout, Meter.SetLayout);
        Paint.SetTargets(source, targets.Depth.View, targets.Picture.Display);
    }

    /// <summary>What meters the frame's brightness for auto exposure, which FSR and tonemapping read.</summary>
    public ExposureMeter Meter { get; }

    /// <summary>The grade baked into a table, which tonemapping reads, and the artists' colour tables.</summary>
    public GradeLut Grade { get; }

    /// <summary>The lens flare, which takes the frame's lights before the frame is recorded and draws after bloom.</summary>
    public LensFlarePass Flare { get; }

    /// <summary>The oil-paint look, which takes the frame's paint before the frame is recorded and draws after bloom.</summary>
    public PaintPass Paint { get; }

    /// <summary>
    /// Takes the lens the view asks for, or none when depth of field is left out or the lens is a pinhole, and whether
    /// the picture is softened, which needs bloom's blur whether or not the glow shows.
    /// </summary>
    public void TakeLens(in FrameView view, RenderEffects off, int sceneWidth)
    {
        _taken = (DepthOfFieldConstants.For(view.Picture.Post, off, sceneWidth), PostGrade.Without(view.Picture.Post, off).Screen.Blur > 0f);
    }

    /// <summary>
    /// Moves the exposure's clock on to the frame's time. Done just before the frame is recorded, so a frame that is
    /// never drawn doesn't move the clock.
    /// </summary>
    public void StepExposure(in FrameView view, RenderEffects off) =>
        _clock.Next(!off.HasFlag(RenderEffects.AutoExposure), view.Time, Exposure.Aim(view.Picture.Post));

    /// <summary>Blurs what is out of focus, adds bloom, paints, meters the exposure and tonemaps the picture into Ldr.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, GBuffer targets, (bool ReadsAntiAliased, RenderEffects Off) display)
    {
        // The picture's size from anti-aliasing on: the scene's, or the window's once FSR has rebuilt it.
        var extent = targets.Picture.Display;
        var source = display.ReadsAntiAliased ? targets.Picture.Resolved : targets.Hdr;

        // After the history is kept, so anti-aliasing and reflections next frame see the scene sharp. Bloom
        // and the meter read the blurred picture. Probe captures never come here, so they stay sharp.
        if (_taken.Lens is { } lens)
        {
            _depthOfField.Record(cmd, frameSet, (source, extent), lens);
        }

        _timer.Mark(cmd);
        // Timed together: bloom and the flare are both light added onto the picture, which the tonemap reads.
        // The soft blur reads bloom's blur, so bloom runs for it even with the glow off.
        _bloom.Record(cmd, _taken.Softens ? display.Off & ~RenderEffects.Bloom : display.Off);
        Flare.Record(cmd);
        _timer.Mark(cmd);

        // Painted after bloom, which keeps the unpainted picture's glow, and before the meter and the tonemap.
        if (Paint.Painting)
        {
            Paint.Record(cmd, (frameSet, Meter.Set), (source, extent));
        }

        _timer.Mark(cmd);

        // Metered from the lit frame before bloom. Its time counts towards the tonemap's.
        if (_clock.Step is { } exposureStep)
        {
            Meter.Record(cmd, exposureStep);
        }

        Grade.Record(cmd, frameSet);
        _tonemap.Record(cmd, (targets.Picture.Ldr, extent), (frameSet, Meter.Set, Grade.ReadSet));
        _timer.Mark(cmd);
    }

    /// <summary>
    /// Points tonemapping and the meter at <paramref name="source"/>, the anti-aliased image or the lit one, and at bloom.
    /// The source is the picture's own size, so reading it smoothly changes nothing at a pixel's centre, and lets the
    /// colour fringes and the speed blur read between pixels.
    /// </summary>
    public void WriteSets(ImageView source, GBuffer targets)
    {
        _tonemap.SetSources(source, _bloom.Result, Flare.Result);
        Meter.SetSource(source, targets.Picture.Display);
    }

    /// <summary>Points bloom, tonemapping, the meter and depth of field at <paramref name="source"/>.</summary>
    public void SetSource(ImageView source, GBuffer targets)
    {
        _bloom.SetSource(source);
        WriteSets(source, targets);
        _depthOfField.SetSource(source, targets.Depth.View);
        Paint.SetSource(source, targets.Depth.View);
        Flare.SetDepth(targets.Depth.View);
    }

    /// <summary>Makes bloom's chain, the flare's target and depth of field's targets for new targets.</summary>
    public void Resize(ImageView source, GBuffer targets)
    {
        _bloom.Resize(source, targets.Picture.Display);
        Flare.Resize(targets.Picture.Display);
        Flare.SetDepth(targets.Depth.View);
        _depthOfField.SetTargets(source, targets.Depth.View, targets.Extent);
        Paint.SetTargets(source, targets.Depth.View, targets.Picture.Display);
    }

    public void Dispose()
    {
        _tonemap.Dispose();
        Paint.Dispose();
        Grade.Dispose();
        Meter.Dispose();
        _depthOfField.Dispose();
        Flare.Dispose();
        _bloom.Dispose();
    }
}
