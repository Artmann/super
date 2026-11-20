using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>Blurs what is out of focus, adds bloom and the lens flare, meters the exposure and tonemaps the picture into Ldr.</summary>
internal sealed class PostStage
{
    private readonly VulkanContext _context;
    private readonly DescriptorKit _kit;
    private readonly GpuTimer _timer;
    private readonly DepthOfFieldPass _depthOfField;
    private readonly BloomPass _bloom;
    private readonly ExposureClock _clock = new();
    private readonly FullscreenPass _tonemap;
    private readonly DescriptorSet _tonemapSet;
    private DepthOfFieldConstants? _lens;

    public PostStage(VulkanContext context, DescriptorKit kit, GpuTimer timer, GBuffer targets, ImageView source)
    {
        _context = context;
        _kit = kit;
        _timer = timer;
        // The picture, bloom and the lens flare. The fourth binding is free: tonemap.frag never reads it.
        _tonemapSet = kit.NewSet(kit.FourTextureLayout);
        _bloom = new BloomPass(context, kit.LinearSampler, targets.Picture.Resolved.View, targets.Picture.Display);
        Flare = new LensFlarePass(context, kit.NearestSampler, targets.Picture.Display);
        Flare.SetDepth(targets.Depth.View);
        Meter = new ExposureMeter(context, kit.NearestSampler);
        _depthOfField = new DepthOfFieldPass(context, kit.NearestSampler, kit.LinearSampler, kit.FrameSetLayout);
        _depthOfField.SetTargets(source, targets.Depth.View, targets.Extent);
        Grade = new GradeLut(context, kit);
        _tonemap = new FullscreenPass(context, "tonemap.frag", GBuffer.LdrFormat,
            [kit.FrameSetLayout, kit.FourTextureLayout, Meter.SetLayout, kit.OneTextureLayout]);
    }

    /// <summary>What meters the frame's brightness for auto exposure, which FSR and tonemapping read.</summary>
    public ExposureMeter Meter { get; }

    /// <summary>The grade baked into a table, which tonemapping reads, and the artists' colour tables.</summary>
    public GradeLut Grade { get; }

    /// <summary>The lens flare, which takes the frame's lights before the frame is recorded and draws after bloom.</summary>
    public LensFlarePass Flare { get; }

    /// <summary>Takes the lens the view asks for, or none when depth of field is left out or the lens is a pinhole.</summary>
    public void TakeLens(in FrameView view, RenderEffects off, int sceneWidth) =>
        _lens = DepthOfFieldConstants.For(view.Picture.Post, off, sceneWidth);

    /// <summary>
    /// Moves the exposure's clock on to the frame's time. Done just before the frame is recorded, so a frame that is
    /// never drawn doesn't move the clock.
    /// </summary>
    public void StepExposure(in FrameView view, RenderEffects off) =>
        _clock.Next(!off.HasFlag(RenderEffects.AutoExposure), view.Time, Exposure.Speed(view.Picture.Post.AdaptationSpeed));

    /// <summary>Blurs what is out of focus, adds bloom, meters the exposure and tonemaps the picture into Ldr.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, GBuffer targets, (bool ReadsAntiAliased, RenderEffects Off) display)
    {
        // The picture's size from anti-aliasing on: the scene's, or the window's once FSR has rebuilt it.
        var extent = targets.Picture.Display;

        // After the history is kept, so anti-aliasing and reflections next frame see the scene sharp. Bloom
        // and the meter read the blurred picture. Probe captures never come here, so they stay sharp.
        if (_lens is { } lens)
        {
            var source = display.ReadsAntiAliased ? targets.Picture.Resolved : targets.Hdr;
            _depthOfField.Record(cmd, frameSet, (source.Image, source.View, extent), lens);
        }

        _timer.Mark(cmd);

        if (display.Off.HasFlag(RenderEffects.Bloom))
        {
            _bloom.RecordSkipped(cmd);
        }
        else
        {
            _bloom.Record(cmd);
        }

        // Timed with bloom: both are light added onto the picture, which the tonemap reads.
        Flare.Record(cmd);
        _timer.Mark(cmd);

        // Metered from the lit frame before bloom. Its time counts towards the tonemap's.
        if (_clock.Step is { } exposureStep)
        {
            Meter.Record(cmd, exposureStep);
        }

        Grade.Record(cmd, frameSet);
        ReadOnlySpan<DescriptorSet> tonemapSets = [frameSet, _tonemapSet, Meter.Set, Grade.ReadSet];
        _context.Api.BeginColorTarget(cmd, targets.Picture.Ldr.Image);
        _tonemap.Record(cmd, targets.Picture.Ldr.View, extent, tonemapSets);
        _context.Api.EndColorTarget(cmd, targets.Picture.Ldr.Image);
        _timer.Mark(cmd);
    }

    /// <summary>
    /// Points tonemapping and the meter at <paramref name="source"/>, the anti-aliased image or the lit one, and at bloom.
    /// The source is the picture's own size, so reading it smoothly changes nothing at a pixel's centre, and lets the
    /// colour fringes and the speed blur read between pixels.
    /// </summary>
    public void WriteSets(ImageView source, GBuffer targets)
    {
        ImageSets.Write(_context, _tonemapSet,
        [
            new(_kit.LinearSampler, source, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, _bloom.Result, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, Flare.Result, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        Meter.SetSource(source, targets.Picture.Display);
    }

    /// <summary>Points bloom, tonemapping, the meter and depth of field at <paramref name="source"/>.</summary>
    public void SetSource(ImageView source, GBuffer targets)
    {
        _bloom.SetSource(source);
        WriteSets(source, targets);
        _depthOfField.SetSource(source, targets.Depth.View);
        Flare.SetDepth(targets.Depth.View);
    }

    /// <summary>Makes bloom's chain, the flare's target and depth of field's targets for new targets.</summary>
    public void Resize(ImageView source, GBuffer targets)
    {
        _bloom.Resize(source, targets.Picture.Display);
        Flare.Resize(targets.Picture.Display);
        Flare.SetDepth(targets.Depth.View);
        _depthOfField.SetTargets(source, targets.Depth.View, targets.Extent);
    }

    public void Dispose()
    {
        _tonemap.Dispose();
        Grade.Dispose();
        Meter.Dispose();
        _depthOfField.Dispose();
        Flare.Dispose();
        _bloom.Dispose();
    }
}
