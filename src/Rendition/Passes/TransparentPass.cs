using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Where glass is seen from: to sort it, and to tell where on screen it lands.</summary>
internal readonly record struct TransparentView(Vector3 Eye, Vector3 Forward, Matrix4x4 ViewProjection)
{
    public static TransparentView From(in Matrix4x4 view, in Matrix4x4 projection)
    {
        Matrix4x4.Invert(view, out var world);
        var forward = -new Vector3(view.M13, view.M23, view.M33);
        return new TransparentView(world.Translation, forward, view * projection);
    }
}

/// <summary>
/// Blends glass and particles over the lit image, furthest first, so each covers what's
/// behind it. Glass can't go through the G-buffer, which holds only one surface per
/// pixel, so it's shaded as it's drawn. It sees the scene through a copy of the picture,
/// with blurrier mips for frosted glass, and bends it by its IOR. See
/// <see cref="TransparentPlan"/> for when the copy is made.
/// </summary>
internal sealed unsafe class TransparentPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _environmentLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _environmentSet;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];
    private readonly List<TransparentStep> _steps = [];
    private GlassItem[] _glass = new GlassItem[64];

    public TransparentPass(
        VulkanContext context,
        DescriptorSetLayout frameSetLayout,
        ResourceTable resources,
        EnvironmentMap environment,
        ReflectionProbes probes)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;

        // The studio's reflections and light, the copy of the scene, depth, the reflection probes and the background.
        const int bindingCount = 6;
        Span<DescriptorSetLayoutBinding> bindings = stackalloc DescriptorSetLayoutBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        }

        _environmentLayout = context.CreateSetLayout(bindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, bindingCount));
        _environmentSet = context.AllocateSet(_pool, _environmentLayout);

        var sampler = environment.Sampler;
        const ImageLayout readOnly = ImageLayout.ShaderReadOnlyOptimal;
        WriteImages(0, [new(sampler, environment.Reflections, readOnly), new(sampler, environment.Irradiance, readOnly)]);
        WriteImages(4, [new(sampler, probes.Cubes.View, readOnly), new(sampler, environment.Background, readOnly)]);

        _layout = context.CreatePipelineLayout([frameSetLayout, resources.Layout, _environmentLayout], SceneDraws.PushRange);

        var description = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            FragmentShader = "transparent.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
            Transparent = true,
        };
        var culled = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        _pipelines.Add(new PipelineVariants(culled, PipelineFactory.Create(context, description)));
    }

    /// <summary>
    /// Points glass at the copy of the scene and at depth, which it reads while depth is
    /// also attached for testing. Call again after a resize.
    /// </summary>
    public void WriteTargets(DescriptorImageInfo sceneColor, DescriptorImageInfo depth) => WriteImages(2, [sceneColor, depth]);

    /// <summary>
    /// Points glass at the reflection probes' cubes. Call again when the array grows, while no frame
    /// that reads the old one is in flight.
    /// </summary>
    public void WriteProbes(Sampler sampler, ImageView cubes) => WriteImages(4, [new(sampler, cubes, ImageLayout.ShaderReadOnlyOptimal)]);

    private void WriteImages(uint firstBinding, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* infos = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = Descriptors.ImageWrite(
                    _environmentSet, firstBinding + (uint)i, DescriptorType.CombinedImageSampler, &infos[i]);
            }

            _context.Api.UpdateDescriptorSets(_context.Device, (uint)images.Length, writes, 0, null);
        }
    }

    /// <summary>
    /// Draws glass and particles in one order, furthest first. Hdr must be a colour
    /// attachment and depth read-only, as it stays throughout.
    /// </summary>
    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh?> meshes,
        List<PartDetail> details,
        InstanceBuffer instances,
        in TransparentView view,
        ParticlePass particles,
        int frameIndex,
        int particleCount,
        in ParticlePass.CameraConstants particleCamera,
        WindReach wind = default)
    {
        int glassCount = CollectGlass(draws, details, view, wind);
        var sorted = particles.Sorted[..particleCount];
        TransparentPlan.Build(_glass.AsSpan(0, glassCount), sorted, view, _steps);
        if (_steps.Count == 0)
        {
            return;
        }

        // Glass binds the scene copy even when none of it bends, as with ghosts, so the copy
        // needs a readable layout on frames that don't make one. Its contents go unread.
        if (glassCount > 0 && !_steps.Exists(step => step.Kind == TransparentStepKind.Copy))
        {
            _context.Api.Transition(cmd, targets.SceneColor.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.None,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit, mipCount: targets.SceneColor.Mips);
        }

        TransparentTargets.Begin(_context.Api, cmd, targets);
        RecordSteps(
            cmd,
            targets,
            frameSet,
            new GlassDraws(draws, meshes, details, instances),
            new ParticleDraws(particles, frameIndex, particleCamera));
        _context.Api.CmdEndRendering(cmd);
    }

    private enum Binding
    {
        None,
        Particles,
        Glass,
    }

    /// <summary>What drawing a piece of glass needs.</summary>
    private readonly ref struct GlassDraws(
        ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes, List<PartDetail> details, InstanceBuffer instances)
    {
        public readonly ReadOnlySpan<DrawItem> Draws = draws;
        public readonly List<Mesh?> Meshes = meshes;
        public readonly List<PartDetail> Details = details;
        public readonly InstanceBuffer Instances = instances;
    }

    /// <summary>What drawing a run of particles needs.</summary>
    private readonly record struct ParticleDraws(ParticlePass Pass, int FrameIndex, ParticlePass.CameraConstants Camera);

    // Records the plan's steps in order, inside rendering begun on the targets, binding each kind's state as it changes.
    private void RecordSteps(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, in GlassDraws glass, in ParticleDraws particles)
    {
        var bound = Binding.None;
        var drawn = new SceneDraws.Bound();
        foreach (var step in _steps)
        {
            switch (step.Kind)
            {
                case TransparentStepKind.Particles:
                    if (bound != Binding.Particles)
                    {
                        particles.Pass.Bind(cmd, frameSet, particles.FrameIndex, particles.Camera);
                        bound = Binding.Particles;
                    }

                    particles.Pass.Draw(cmd, step.First, step.Count);
                    break;

                case TransparentStepKind.Copy:
                    _context.Api.CmdEndRendering(cmd);
                    TransparentTargets.CopyScene(_context.Api, cmd, targets);
                    TransparentTargets.Begin(_context.Api, cmd, targets);
                    bound = Binding.None;
                    break;

                case TransparentStepKind.Glass:
                    if (bound != Binding.Glass)
                    {
                        BindGlass(cmd, frameSet);
                        bound = Binding.Glass;
                        drawn = new SceneDraws.Bound();
                    }

                    DrawGlass(cmd, step, glass, ref drawn);
                    break;
            }
        }
    }

    private void BindGlass(CommandBuffer cmd, DescriptorSet frameSet)
    {
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _environmentSet };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);
    }

    private void DrawGlass(CommandBuffer cmd, TransparentStep step, in GlassDraws glass, ref SceneDraws.Bound drawn)
    {
        int index = step.First;
        var constants = SceneDraws.Constants(glass.Draws[index], cascade: 0, _resources.Materials.SwayOf, glass.Details);
        constants.Tint.W = step.Refracts ? 1f : 0f;
        SceneDraws.Draw(
            _context.Api, cmd, _layout, _pipelines, _resources, glass.Draws, MemoryMarshal.CreateReadOnlySpan(ref index, 1),
            glass.Meshes, glass.Instances, constants, solids: null, ref drawn);
    }

    /// <summary>Finds the glass among the draws, with how far each is and where it lands on screen.</summary>
    private int CollectGlass(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, in TransparentView view, WindReach wind)
    {
        int count = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            if (!SceneDraws.Belongs(draw, _resources.Materials[draw.Material].Flags, ScenePass.Transparent))
            {
                continue;
            }

            if (count == _glass.Length)
            {
                Array.Resize(ref _glass, count * 2);
            }

            var (center, radius) = Bounds(draw, details, wind);
            var reach = new Vector3(radius);
            var rect = ScreenRect.OfBox(center - reach, center + reach, view.ViewProjection);
            float depth = TransparentPlan.Depth(center, view.Eye, view.Forward);
            _glass[count++] = new GlassItem(i, depth, rect, CanRefract: draw.Ghost is null);
        }

        return count;
    }

    /// <summary>
    /// The middle and radius of a draw in the world, from its part's bounds. A draw without
    /// any is taken as its origin, and as covering the whole screen. A draw that sways in <paramref name="wind"/> grows
    /// by as far as it can sway.
    /// </summary>
    internal static (Vector3 Center, float Radius) Bounds(in DrawItem draw, List<PartDetail> details, WindReach wind = default)
    {
        ref readonly var transform = ref draw.Transform;
        if (draw.Detail.Index == 0)
        {
            return (transform.Translation, float.PositiveInfinity);
        }

        var detail = details[draw.Detail.Index];
        float scale = MathF.Sqrt(MathF.Max(
            new Vector3(transform.M11, transform.M12, transform.M13).LengthSquared(),
            MathF.Max(
                new Vector3(transform.M21, transform.M22, transform.M23).LengthSquared(),
                new Vector3(transform.M31, transform.M32, transform.M33).LengthSquared())));
        return (Vector3.Transform(detail.Center, transform),
            (detail.Radius + MathF.Abs(draw.Swim.Amplitude)) * scale + wind.Pad(draw.Material));
    }

    public void Dispose()
    {
        var api = _context.Api;
        _pipelines[0].TwoSided.Destroy();
        _pipelines[0].Culled.Destroy();
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _environmentLayout, null);
    }
}
