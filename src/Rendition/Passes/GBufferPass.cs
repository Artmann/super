using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Writes surface properties for every visible pixel. No lighting happens here.</summary>
internal sealed unsafe class GBufferPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];

    public GBufferPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout };
        var pushRange = SceneDraws.PushRange;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

        // The first entry is the standard shader, which materials use by default.
        AddShader(null);
    }

    public int ShaderCount => _pipelines.Count;

    /// <summary>Adds a surface shader. No code means the standard one.</summary>
    public SurfaceShaderHandle AddShader(byte[]? code)
    {
        var description = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            FragmentShader = "gbuffer.frag",
            FragmentCode = code,
            Layout = _layout,
            ColorFormats = [GBuffer.BaseColorFormat, GBuffer.NormalFormat, GBuffer.EmissiveFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
        };
        var culled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _pipelines.Add(new PipelineVariants(culled, PipelineFactory.Create(_context, description)));
        return new SurfaceShaderHandle(_pipelines.Count - 1);
    }

    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh> meshes)
    {
        var api = _context.Api;

        var colorAttachments = stackalloc RenderingAttachmentInfo[3];
        colorAttachments[0] = ColorAttachment(targets.BaseColor.View);
        colorAttachments[1] = ColorAttachment(targets.Normal.View);
        colorAttachments[2] = ColorAttachment(targets.Emissive.View);
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 3,
            PColorAttachments = colorAttachments,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);

        SceneDraws.Record(api, cmd, _layout, _pipelines, _resources, draws, meshes);

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        foreach (var variants in _pipelines)
        {
            _context.Api.DestroyPipeline(_context.Device, variants.TwoSided, null);
            _context.Api.DestroyPipeline(_context.Device, variants.Culled, null);
        }

        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    private static RenderingAttachmentInfo ColorAttachment(ImageView view) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
    };
}
