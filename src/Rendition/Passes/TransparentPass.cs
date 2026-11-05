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
        EnvironmentMap environment)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;

        // The studio's reflections and light, the copy of the scene and depth.
        const int bindingCount = 4;
        var bindings = stackalloc DescriptorSetLayoutBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        }

        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = bindingCount,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &setLayoutInfo, null, out _environmentLayout).Check();

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, bindingCount);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var environmentLayout = _environmentLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &environmentLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _environmentSet).Check();

        WriteImages(0,
        [
            new(environment.Sampler, environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
        ]);

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout, _environmentLayout };
        var pushRange = SceneDraws.PushRange;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

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

    private void WriteImages(uint firstBinding, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* infos = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _environmentSet,
                    DstBinding = firstBinding + (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &infos[i],
                };
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
        in ParticlePass.CameraConstants particleCamera)
    {
        int glassCount = CollectGlass(draws, details, view);
        var sorted = particles.Sorted[..particleCount];
        TransparentPlan.Build(_glass.AsSpan(0, glassCount), sorted, view.Eye, view.Forward, view.ViewProjection, _steps);
        if (_steps.Count == 0)
        {
            return;
        }

        var api = _context.Api;
        Begin(cmd, targets);
        var glassSets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _environmentSet };
        var bound = Binding.None;
        var drawn = new SceneDraws.Bound();
        foreach (var step in _steps)
        {
            switch (step.Kind)
            {
                case TransparentStepKind.Particles:
                    if (bound != Binding.Particles)
                    {
                        particles.Bind(cmd, frameSet, frameIndex, particleCamera);
                        bound = Binding.Particles;
                    }

                    particles.Draw(cmd, step.First, step.Count);
                    break;

                case TransparentStepKind.Copy:
                    api.CmdEndRendering(cmd);
                    CopyScene(cmd, targets);
                    Begin(cmd, targets);
                    bound = Binding.None;
                    break;

                case TransparentStepKind.Glass:
                    if (bound != Binding.Glass)
                    {
                        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, glassSets, 0, null);
                        bound = Binding.Glass;
                        drawn = new SceneDraws.Bound();
                    }

                    int index = step.First;
                    var constants = SceneDraws.Constants(draws[index], cascade: 0);
                    constants.Tint.W = step.Refracts ? 1f : 0f;
                    SceneDraws.Draw(
                        api, cmd, _layout, _pipelines, _resources, draws, MemoryMarshal.CreateReadOnlySpan(ref index, 1),
                        meshes, instances, constants, solid: null, ref drawn);
                    break;
            }
        }

        api.CmdEndRendering(cmd);
    }

    private enum Binding
    {
        None,
        Particles,
        Glass,
    }

    /// <summary>Finds the glass among the draws, with how far each is and where it lands on screen.</summary>
    private int CollectGlass(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, in TransparentView view)
    {
        int count = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            if (!SceneDraws.Belongs(draw, _resources.GetFlags(draw.Material), ScenePass.Transparent))
            {
                continue;
            }

            if (count == _glass.Length)
            {
                Array.Resize(ref _glass, count * 2);
            }

            var (center, radius) = Bounds(draw, details);
            var reach = new Vector3(radius);
            var rect = ScreenRect.OfBox(center - reach, center + reach, view.ViewProjection);
            float depth = TransparentPlan.Depth(center, view.Eye, view.Forward);
            _glass[count++] = new GlassItem(i, depth, rect, CanRefract: draw.Ghost is null);
        }

        return count;
    }

    /// <summary>
    /// The middle and radius of a draw in the world, from its part's bounds. A draw without
    /// any is taken as its origin, and as covering the whole screen.
    /// </summary>
    internal static (Vector3 Center, float Radius) Bounds(in DrawItem draw, List<PartDetail> details)
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
        return (Vector3.Transform(detail.Center, transform), (detail.Radius + MathF.Abs(draw.Swim.Amplitude)) * scale);
    }

    private void Begin(CommandBuffer cmd, GBuffer targets)
    {
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Hdr.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthReadOnlyOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.None,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        _context.Api.CmdBeginRendering(cmd, &rendering);
        _context.Api.SetFullViewport(cmd, targets.Extent);
    }

    /// <summary>Copies Hdr into the scene copy, then halves it down the mips, each blurrier than the last.</summary>
    private void CopyScene(CommandBuffer cmd, GBuffer targets)
    {
        var api = _context.Api;
        var copy = targets.SceneColor;
        const PipelineStageFlags2 colorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
        const AccessFlags2 colorWrite = AccessFlags2.ColorAttachmentWriteBit;
        const PipelineStageFlags2 copyStage = PipelineStageFlags2.AllTransferBit;
        const PipelineStageFlags2 sampledStage = PipelineStageFlags2.FragmentShaderBit;

        api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, colorStage, colorWrite,
            copyStage, AccessFlags2.TransferReadBit);

        // Glass drawn before may still be reading the last copy.
        api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, sampledStage, AccessFlags2.None,
            copyStage, AccessFlags2.TransferWriteBit, mipCount: copy.Mips);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        api.CmdCopyImage(cmd, targets.Hdr.Image, ImageLayout.TransferSrcOptimal, copy.Image, ImageLayout.TransferDstOptimal, 1, &region);

        int width = (int)targets.Extent.Width;
        int height = (int)targets.Extent.Height;
        for (uint mip = 1; mip < copy.Mips; mip++)
        {
            api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, copyStage, AccessFlags2.TransferWriteBit,
                copyStage, AccessFlags2.TransferReadBit, baseMip: mip - 1);

            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);
            api.CmdBlitImage(cmd, copy.Image, ImageLayout.TransferSrcOptimal, copy.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            width = nextWidth;
            height = nextHeight;
        }

        if (copy.Mips > 1)
        {
            api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, copyStage, AccessFlags2.TransferReadBit,
                sampledStage, AccessFlags2.ShaderSampledReadBit, mipCount: copy.Mips - 1);
        }

        api.Transition(cmd, copy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, copyStage, AccessFlags2.TransferWriteBit,
            sampledStage, AccessFlags2.ShaderSampledReadBit, baseMip: copy.Mips - 1);

        api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal, copyStage, AccessFlags2.TransferReadBit,
            colorStage, colorWrite | AccessFlags2.ColorAttachmentReadBit);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipelines[0].TwoSided, null);
        api.DestroyPipeline(_context.Device, _pipelines[0].Culled, null);
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _environmentLayout, null);
    }
}
