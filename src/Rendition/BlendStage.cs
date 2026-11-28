using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// Blends glass and particles over the lit image, furthest first, and the probe debug view's spheres over everything.
/// </summary>
internal sealed unsafe class BlendStage
{
    private readonly Vk _api;
    private readonly DescriptorKit _kit;
    private readonly FrameRing _ring;
    private readonly GpuTimer _timer;
    private readonly MeshStore _meshes;
    private readonly FrameSwitches _switches;
    private readonly TransparentPass _transparentPass;
    private readonly ParticlePass _particlePass;
    private readonly ProbeDebugPass _probeDebugPass;
    private ParticlePass.CameraConstants _particleCamera;
    private TransparentView _transparentView;
    private int _particleCount;

    public BlendStage(
        (VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, (FrameRing Ring, GpuTimer Timer, MeshStore Meshes) frame,
        (ResourceTable Resources, EnvironmentMap Environment, ReflectionProbes Probes) sources, FrameSwitches switches)
    {
        var context = device.Context;
        _api = context.Api;
        _kit = kit;
        (_ring, _timer, _meshes) = frame;
        _switches = switches;
        _particlePass = new ParticlePass(device, kit.FrameSetLayout, FrameRing.Count);
        _transparentPass = new TransparentPass(device, kit.FrameSetLayout, sources.Resources, sources.Environment, sources.Probes);
        _probeDebugPass = new ProbeDebugPass(device, kit.FrameSetLayout, kit.LightingSetLayout);
    }

    /// <summary>
    /// Sorts this frame's particles into their buffer and keeps the camera's axes, which
    /// turn each one to face the screen. The view matrix holds them in its columns.
    /// </summary>
    public void Upload(in FrameView view)
    {
        ref readonly var m = ref view.View;
        var right = new Vector3(m.M11, m.M21, m.M31);
        var up = new Vector3(m.M12, m.M22, m.M32);
        var forward = -new Vector3(m.M13, m.M23, m.M33);
        _particleCamera = new ParticlePass.CameraConstants
        {
            Right = new Vector4(right, 0f),
            Up = new Vector4(up, 0f),
            Forward = new Vector4(forward, 0f),
        };
        ReadOnlySpan<Particle> particles = _switches.Off.HasFlag(RenderEffects.Particles) ? [] : view.Extras.Particles;
        _particleCount = _particlePass.Upload(_ring.Index, particles, view.CameraPosition, forward);
        _transparentView = TransparentView.From(view.View, view.Projection);
    }

    /// <summary>
    /// Blends glass and particles over the lit image, furthest first, and the probe debug view's spheres over
    /// everything. Depth is read-only throughout: glass tests against it, and both read it to see what's behind them.
    /// </summary>
    public void Record(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws,
        (InstanceBuffer Instances, LightProbes Probes, DescriptorSet LightingSet) lit)
    {
        _api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal), (SampledStage, SampledAccess),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
        _api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal), (SampledStage, SampledAccess),
            (DepthStages | SampledStage, AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess));
        _transparentPass.Record(cmd, targets, new SceneGeometry(draws, _meshes.MeshSlots, _meshes.DetailSlots, lit.Instances),
            new TransparentFrame(frameSet, _transparentView, _switches.WindReach), new ParticleDraws(_particlePass, _ring.Index, _particleCount, _particleCamera));

        // With the probes off there are no spheres, and no atlases to read.
        var probes = lit.Probes;
        if (_switches.ProbeView && probes.InUse)
        {
            uint spheres = (uint)probes.Batch.Cascades.Layout(0).Total;
            if (probes.Batch.Grid is { } grid)
            {
                var (x, y, z) = ProbeGrid.Counts(grid);
                spheres += (uint)(x * y * z);
            }

            _probeDebugPass.Record(cmd, targets, frameSet, lit.LightingSet, spheres);
        }

        _api.EndColorTarget(cmd, targets.Hdr.Image);
        _timer.Mark(cmd);
    }

    /// <summary>
    /// Points glass and particles at the targets. They read depth while it's also attached for glass to test against,
    /// and particles are lit by the sun's shadow and the environment.
    /// </summary>
    public void WriteTargets(GBuffer targets, ImageView sunShadow, EnvironmentMap environment)
    {
        var testedDepth = new DescriptorImageInfo(_kit.NearestSampler, targets.Depth.View, ImageLayout.DepthReadOnlyOptimal);
        _transparentPass.WriteTargets(new(_kit.MipSampler, targets.SceneColor.View, ImageLayout.ShaderReadOnlyOptimal), testedDepth);

        _particlePass.WriteTargets(
            testedDepth,
            new(_kit.ShadowSampler, sunShadow, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Background, ImageLayout.ShaderReadOnlyOptimal));
    }

    /// <summary>Points glass at the reflection probes' cubes after their array grew.</summary>
    public void WriteProbes(Sampler sampler, ImageView cubes) => _transparentPass.WriteProbes(sampler, cubes);

    public void Dispose()
    {
        _transparentPass.Dispose();
        _probeDebugPass.Dispose();
        _particlePass.Dispose();
    }
}
