using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// Lights the G-buffer: the screen-space reflections the lighting reads, the lighting pass itself, and the spread of
/// skin and wax across the screen and down it, with their passes and sets.
/// </summary>
internal sealed unsafe class LightingStage : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Vk _api;
    private readonly DescriptorKit _kit;
    private readonly LightingStage.Sources _sources;
    private readonly FullscreenPass _reflectionPass;
    private readonly FullscreenPass _lightingPass;
    private readonly FullscreenPass _scatterAcrossPass;
    private readonly FullscreenPass _scatterDownPass;
    private readonly DescriptorSet _reflectionSet;
    private readonly DescriptorSet _scatterAcrossSet;
    private readonly DescriptorSet _scatterDownSet;

    public LightingStage(VulkanContext context, DescriptorKit kit, Sources sources)
    {
        _context = context;
        _api = context.Api;
        _kit = kit;
        _sources = sources;
        _scatterAcrossSet = kit.NewSet(kit.TwoTextureLayout);
        _scatterDownSet = kit.NewSet(kit.TwoTextureLayout);
        LightingSet = kit.NewLightingSet();
        _reflectionSet = kit.NewSet(kit.FourTextureLayout);
        var resources = sources.Resources;
        _reflectionPass = new FullscreenPass(context, "reflections.frag", GBuffer.HdrFormat, [kit.FrameSetLayout, kit.FourTextureLayout], sizeof(int));
        _lightingPass = new FullscreenPass(context, "lighting.frag", [GBuffer.HdrFormat, GBuffer.HdrFormat],
            [kit.FrameSetLayout, kit.LightingSetLayout, resources.Layout]);
        _scatterAcrossPass = new FullscreenPass(context, "sss_blur.frag", [GBuffer.HdrFormat],
            [kit.FrameSetLayout, kit.TwoTextureLayout, resources.Layout], BlurConstants.Size);
        _scatterDownPass = new FullscreenPass(context, "sss_blur.frag", [GBuffer.HdrFormat],
            [kit.FrameSetLayout, kit.TwoTextureLayout, resources.Layout], BlurConstants.Size, blend: true);
    }

    /// <summary>The lighting pass's set, which the probe debug view's spheres read too.</summary>
    public DescriptorSet LightingSet { get; }

    /// <summary>
    /// Traces the screen-space reflections from the G-buffer just drawn and last frame's picture, for the lighting to
    /// read: one ray a pixel, or one for each block of four at <see cref="ReflectionResolution.Half"/>. Left out when
    /// nothing would be traced: with reflections or specular off, or with no last frame. The lighting reads no
    /// reflection then, but it still binds the target.
    /// </summary>
    public void RecordReflections(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, RenderEffects off, GpuTimer timer)
    {
        if (!targets.HistoryReady || (off & (RenderEffects.Reflections | RenderEffects.Specular)) != 0)
        {
            _api.Transition(cmd, targets.Traced.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
            timer.Mark(cmd);
            return;
        }

        int scale = targets.Reflections == ReflectionResolution.Full ? 1 : 2;
        ReadOnlySpan<DescriptorSet> sets = [frameSet, _reflectionSet];
        _api.BeginColorTarget(cmd, targets.Traced.Image);
        _reflectionPass.Record(cmd, targets.Traced.View, targets.TracedExtent, sets, new ReadOnlySpan<byte>(&scale, sizeof(int)));
        _api.EndColorTarget(cmd, targets.Traced.Image);
        timer.Mark(cmd);
    }

    /// <summary>
    /// Lights the G-buffer into Hdr, and spreads the diffuse light of skin and wax across the screen, then down it and
    /// onto the lit image. Nothing to spread when no material scatters, or while subsurface is off. Leaves Hdr a target.
    /// </summary>
    public void RecordLighting(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, RenderEffects off, GpuTimer timer)
    {
        var extent = targets.Extent;
        var resources = _sources.Resources;
        _api.BeginColorTarget(cmd, targets.Hdr.Image);
        _api.BeginColorTarget(cmd, targets.Scatter.Image);

        ReadOnlySpan<DescriptorSet> lightingSets = [frameSet, LightingSet, resources.Set];
        _lightingPass.Record(cmd, [targets.Hdr.View, targets.Scatter.View], extent, lightingSets, []);
        _api.EndColorTarget(cmd, targets.Scatter.Image);
        timer.Mark(cmd);

        if (resources.Materials.AnySubsurface && !off.HasFlag(RenderEffects.Subsurface))
        {
            var across = BlurConstants.Across;
            ReadOnlySpan<DescriptorSet> acrossSets = [frameSet, _scatterAcrossSet, resources.Set];
            _api.BeginColorTarget(cmd, targets.ScatterBlurred.Image);
            _scatterAcrossPass.Record(cmd, targets.ScatterBlurred.View, extent, acrossSets,
                new ReadOnlySpan<byte>(&across, (int)BlurConstants.Size));
            _api.EndColorTarget(cmd, targets.ScatterBlurred.Image);

            var down = BlurConstants.Down;
            ReadOnlySpan<DescriptorSet> downSets = [frameSet, _scatterDownSet, resources.Set];
            _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
                ColorWrite | AccessFlags2.ColorAttachmentReadBit);
            _scatterDownPass.Record(cmd, targets.Hdr.View, extent, downSets,
                new ReadOnlySpan<byte>(&down, (int)BlurConstants.Size));
        }

        timer.Mark(cmd);
    }

    /// <summary>Points the lighting pass, the screen reflections and the spread of skin and wax at the targets.</summary>
    public void WriteSets(GBuffer targets, DescriptorImageInfo depth, DescriptorImageInfo normal, DescriptorImageInfo shadowDepth)
    {
        var nearest = _kit.NearestSampler;
        var environment = _sources.Environment;
        var volume = _sources.ProbeVolume;
        ImageSets.Write(_context, LightingSet,
        [
            new(nearest, targets.BaseColor.View, ImageLayout.ShaderReadOnlyOptimal),
            normal,
            depth,
            shadowDepth,
            new(_kit.ShadowSampler, _sources.Shadows.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Emissive.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.AoBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, targets.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, _sources.ReflectionProbes.Cubes.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, volume.IrradianceView, ImageLayout.General),
            new(_kit.LinearSampler, volume.DepthView, ImageLayout.General),
            new(nearest, targets.Baked.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Mask.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Traced.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.BounceBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Background, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        ImageSets.Write(_context, _reflectionSet,
        [
            depth,
            normal,
            new(nearest, targets.Emissive.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, targets.History.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        ImageSets.Write(_context, _scatterAcrossSet, [new(nearest, targets.Scatter.View, ImageLayout.ShaderReadOnlyOptimal), depth]);
        ImageSets.Write(_context, _scatterDownSet, [new(nearest, targets.ScatterBlurred.View, ImageLayout.ShaderReadOnlyOptimal), depth]);
    }

    public void Dispose()
    {
        _lightingPass.Dispose();
        _reflectionPass.Dispose();
        _scatterAcrossPass.Dispose();
        _scatterDownPass.Dispose();
    }

    /// <summary>
    /// What the lighting reads besides the targets: the material table, the environment, the reflection probes, the
    /// light probes' atlases and the sun's shadow maps.
    /// </summary>
    public sealed record Sources(
        ResourceTable Resources, EnvironmentMap Environment, ReflectionProbes ReflectionProbes, ProbeVolume ProbeVolume,
        ShadowPass Shadows);
}
