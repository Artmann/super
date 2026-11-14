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
    private static readonly Format[] ColorFormats =
    [
        GBuffer.BaseColorFormat, GBuffer.NormalFormat, GBuffer.EmissiveFormat, GBuffer.MotionFormat,
        GBuffer.BakedFormat, GBuffer.MaskFormat,
    ];

    private readonly List<PipelineVariants> _solids = [];
    private readonly List<PipelineVariants> _shown = [];
    private readonly List<PipelineVariants> _depthOnly = [];
    private readonly DepthFirst _depthFirst;

    public GBufferPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;

        _layout = context.CreatePipelineLayout([frameSetLayout, resources.Layout], SceneDraws.PushRange);

        // Depth alone: no fragment shader, and nothing written to the colour targets.
        Span<PipelineColorBlendAttachmentState> noColor = stackalloc PipelineColorBlendAttachmentState[ColorFormats.Length];
        var depthOnly = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            Layout = _layout,
            ColorFormats = ColorFormats,
            Blends = noColor,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
        };
        var depthCulled = PipelineFactory.Create(_context, depthOnly);
        depthOnly.TwoSided = true;
        _depthOnly.Add(new PipelineVariants(depthCulled, PipelineFactory.Create(_context, depthOnly)));
        _depthFirst = new DepthFirst(_depthOnly, _shown);

        // The first entry is the standard shader, which materials use by default.
        AddShader(null);
    }

    public int ShaderCount => _pipelines.Count;

    /// <summary>
    /// Adds a surface shader. No code means the standard one. The name is the compiled file the code came from, such
    /// as <c>terrain.frag</c>, for live shader reload to read again.
    /// </summary>
    public SurfaceShaderHandle AddShader(byte[]? code, string? name = null)
    {
        var description = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            FragmentShader = "gbuffer.frag",
            FragmentCode = code is null ? null : (code, name),
            Layout = _layout,
            ColorFormats = ColorFormats,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
        };
        var culled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _pipelines.Add(new PipelineVariants(culled, PipelineFactory.Create(_context, description)));

        // The same shader without its cut-outs, for materials with no holes, so hidden pixels are skipped unshaded.
        description.Solid = true;
        description.TwoSided = false;
        var solidCulled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _solids.Add(new PipelineVariants(solidCulled, PipelineFactory.Create(_context, description)));

        // And once more for when their depth is drawn first, to shade only the pixels that show.
        description.DepthDrawn = true;
        description.TwoSided = false;
        var shownCulled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _shown.Add(new PipelineVariants(shownCulled, PipelineFactory.Create(_context, description)));
        return new SurfaceShaderHandle(_pipelines.Count - 1);
    }

    /// <summary>
    /// Draws the solid things into <paramref name="targets"/>. Given the camera as <paramref name="depthFirst"/>,
    /// draws whose triangles are large in its view have their depth drawn first; see <see cref="DepthFirst"/>.
    /// </summary>
    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh?> meshes,
        List<PartDetail> details,
        InstanceBuffer instances,
        DetailView? depthFirst = null)
    {
        var api = _context.Api;
        Begin(cmd, targets);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);

        if (depthFirst is { } view)
        {
            _depthFirst.Aim(view, (float)targets.Extent.Width * targets.Extent.Height);
        }

        SceneDraws.Record(
            api, cmd, _layout, _pipelines, _resources, draws, meshes, details, instances, ScenePass.Opaque,
            solids: _solids, depthFirst: depthFirst is null ? null : _depthFirst);

        api.CmdEndRendering(cmd);
    }

    // Starts drawing into the targets, each cleared.
    private void Begin(CommandBuffer cmd, GBuffer targets)
    {
        var api = _context.Api;
        var colorAttachments = stackalloc RenderingAttachmentInfo[6];
        colorAttachments[0] = ColorAttachment(targets.BaseColor.View);
        colorAttachments[1] = ColorAttachment(targets.Normal.View);
        colorAttachments[2] = ColorAttachment(targets.Emissive.View);
        colorAttachments[3] = ColorAttachment(targets.Motion.View); // Cleared to no motion, which the sky keeps.
        colorAttachments[4] = ColorAttachment(targets.Baked.View); // Cleared to not lightmapped.
        colorAttachments[5] = ColorAttachment(targets.Mask.View, 1f); // Cleared to fully lit.
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
            ColorAttachmentCount = 6,
            PColorAttachments = colorAttachments,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);
    }

    public void Dispose()
    {
        foreach (var variants in _pipelines.Concat(_solids).Concat(_shown).Concat(_depthOnly))
        {
            variants.TwoSided.Destroy();
            variants.Culled.Destroy();
        }

        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    private static RenderingAttachmentInfo ColorAttachment(ImageView view, float clear = 0f) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
        ClearValue = new ClearValue(new ClearColorValue(clear, clear, clear, clear)),
    };
}
