using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// Lights the G-buffer: the screen-space reflections the lighting reads, the lighting pass itself, and the spread of
/// skin and wax across the screen and down it, drawn through the GPU layer.
/// </summary>
internal sealed unsafe class LightingStage : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly DescriptorKit _kit;
    private readonly LightingStage.Sources _sources;
    private readonly FullscreenPass _reflectionPass;
    private readonly FullscreenPass _lightingPass;
    private readonly FullscreenPass _scatterAcrossPass;
    private readonly FullscreenPass _scatterDownPass;
    private readonly CommandList _commands = new();
    private readonly AdoptedTargets _targets;

    // The reflections' images, and the spread's: across from the lit scatter, then down from the blurred one.
    private readonly (ImageGroup Reflection, ImageGroup Across, ImageGroup Down, SamplerId Linear) _inputs;

    /// <summary>
    /// The lighting pass is lent the lighting set's layout, which the probe debug view's spheres read too, and the
    /// material table's. The reflections and the spread lay out their own images from their shaders.
    /// </summary>
    public LightingStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, Sources sources)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _kit = kit;
        _sources = sources;
        _targets = new AdoptedTargets(gpu);
        LightingSet = kit.NewLightingSet();
        var nearest = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
        _inputs = (new ImageGroup(gpu, nearest), new ImageGroup(gpu, nearest), new ImageGroup(gpu, nearest),
            gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));

        var hdr = VulkanFormats.ToGpu(GBuffer.HdrFormat);
        var materials = sources.Resources.Layout;
        _reflectionPass = new FullscreenPass(gpu, "reflections.frag", [hdr], borrowed: [kit.FrameSetLayout, default]);
        _lightingPass = new FullscreenPass(gpu, "lighting.frag", [hdr, hdr], borrowed: [kit.FrameSetLayout, kit.LightingSetLayout, materials]);
        _scatterAcrossPass = new FullscreenPass(gpu, "sss_blur.frag", [hdr], borrowed: [kit.FrameSetLayout, default, materials]);
        _scatterDownPass = new FullscreenPass(gpu, "sss_blur.frag", [hdr], blend: true, borrowed: [kit.FrameSetLayout, default, materials]);
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
        if (!targets.Picture.HistoryReady || (off & (RenderEffects.Reflections | RenderEffects.Specular)) != 0)
        {
            _context.Api.Transition(cmd, new ImageTransition(targets.Traced.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
            timer.Mark(cmd);
            return;
        }

        int scale = targets.Reflections == ReflectionResolution.Full ? 1 : 2;
        _commands.Reset();
        _reflectionPass.Record(_commands, [_targets.For(targets.Traced, targets.TracedExtent)],
            [_gpu.Adopt(frameSet), _inputs.Reflection.Id], new ReadOnlySpan<byte>(&scale, sizeof(int)));
        _gpu.Play(_commands, cmd);
        timer.Mark(cmd);
    }

    /// <summary>
    /// Lights the G-buffer into Hdr, and spreads the diffuse light of skin and wax across the screen, then down it and
    /// onto the lit image. Nothing to spread when no material scatters, or while subsurface is off. Leaves Hdr ready to
    /// sample, as the layer leaves every target.
    /// </summary>
    public void RecordLighting(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, RenderEffects off, GpuTimer timer)
    {
        var extent = targets.Extent;
        var resources = _sources.Resources;
        var frame = _gpu.Adopt(frameSet);
        var materials = _gpu.Adopt(resources.Set);
        _commands.Reset();
        _lightingPass.Record(_commands, [_targets.For(targets.Hdr, extent), _targets.For(targets.Scatter, extent)],
            [frame, _gpu.Adopt(LightingSet), materials], []);
        _gpu.Play(_commands, cmd);
        timer.Mark(cmd);

        if (resources.Materials.AnySubsurface && !off.HasFlag(RenderEffects.Subsurface))
        {
            var across = BlurConstants.Across;
            var down = BlurConstants.Down;
            _commands.Reset();
            _scatterAcrossPass.Record(_commands, [_targets.For(targets.ScatterBlurred, extent)], [frame, _inputs.Across.Id, materials],
                new ReadOnlySpan<byte>(&across, (int)BlurConstants.Size));
            _scatterDownPass.Record(_commands, [_targets.For(targets.Hdr, extent, LoadOp.Load)], [frame, _inputs.Down.Id, materials],
                new ReadOnlySpan<byte>(&down, (int)BlurConstants.Size));
            _gpu.Play(_commands, cmd);
        }

        timer.Mark(cmd);
    }

    /// <summary>
    /// Points the lighting pass, the screen reflections and the spread of skin and wax at the targets. Makes the bind
    /// groups again, so it must only be called while the GPU is idle.
    /// </summary>
    public void WriteSets(GBuffer targets, DescriptorImageInfo depth, DescriptorImageInfo normal, DescriptorImageInfo shadowDepth)
    {
        WriteLightingSet(targets, depth, normal, shadowDepth);
        _targets.Clear();
        _inputs.Reflection.Make(_reflectionPass.Pipeline, 1,
        [
            ("depthTexture", depth.ImageView),
            ("normalRoughnessAoTexture", normal.ImageView),
            ("emissiveClearcoatTexture", targets.Emissive.View),
            new GroupImage("historyTexture", targets.Picture.History.View, _inputs.Linear),
        ]);
        _inputs.Across.Make(_scatterAcrossPass.Pipeline, 1, [("scatterTexture", targets.Scatter.View), ("depthTexture", depth.ImageView)]);
        _inputs.Down.Make(_scatterDownPass.Pipeline, 1, [("scatterTexture", targets.ScatterBlurred.View), ("depthTexture", depth.ImageView)]);
    }

    private void WriteLightingSet(GBuffer targets, DescriptorImageInfo depth, DescriptorImageInfo normal, DescriptorImageInfo shadowDepth)
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
            new(nearest, targets.Occlusion.AoBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, targets.Picture.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, _sources.ReflectionProbes.Cubes.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, volume.IrradianceView, ImageLayout.General),
            new(_kit.LinearSampler, volume.DepthView, ImageLayout.General),
            new(nearest, targets.Baked.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Mask.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Traced.View, ImageLayout.ShaderReadOnlyOptimal),
            new(nearest, targets.Occlusion.BounceBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Background, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.MipSampler, _sources.Planar.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
    }

    public void Dispose()
    {
        _inputs.Reflection.Dispose();
        _inputs.Across.Dispose();
        _inputs.Down.Dispose();
        _targets.Dispose();
        _lightingPass.Dispose();
        _reflectionPass.Dispose();
        _scatterAcrossPass.Dispose();
        _scatterDownPass.Dispose();
    }

    /// <summary>
    /// What the lighting reads besides the targets: the material table, the environment, the reflection probes, the
    /// light probes' atlases, the sun's shadow maps and the planar reflection's picture.
    /// </summary>
    public sealed record Sources(
        ResourceTable Resources, EnvironmentMap Environment, ReflectionProbes ReflectionProbes, ProbeVolume ProbeVolume,
        ShadowPass Shadows, PlanarStage Planar);
}
