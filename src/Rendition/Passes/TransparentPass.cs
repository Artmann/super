using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Blends glass over the lit image. It can't go through the G-buffer, which holds only
/// one surface per pixel, so these surfaces are shaded directly as they are drawn.
/// </summary>
internal sealed unsafe class TransparentPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _environmentLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _environmentSet;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _culledPipeline;
    private readonly Pipeline _twoSidedPipeline;

    public TransparentPass(
        VulkanContext context,
        DescriptorSetLayout frameSetLayout,
        ResourceTable resources,
        EnvironmentMap environment)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &setLayoutInfo, null, out _environmentLayout).Check();

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2);
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

        var images = stackalloc DescriptorImageInfo[]
        {
            new(environment.Sampler, environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(environment.Sampler, environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _environmentSet,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);

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
        _culledPipeline = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        _twoSidedPipeline = PipelineFactory.Create(context, description);
    }

    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh> meshes)
    {
        var api = _context.Api;

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

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _environmentSet };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);

        SceneDraws.Record(api, cmd, _layout, _culledPipeline, _twoSidedPipeline, _resources, draws, meshes, transparent: true);

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _twoSidedPipeline, null);
        api.DestroyPipeline(_context.Device, _culledPipeline, null);
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _environmentLayout, null);
    }
}
