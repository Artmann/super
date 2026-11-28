using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Writes surface properties for every visible pixel. No lighting happens here.</summary>
internal sealed unsafe class GBufferPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly SceneList _list;
    private readonly List<PipelineVariants> _pipelines = [];
    internal static readonly Format[] ColorFormats =
    [
        GBuffer.BaseColorFormat, GBuffer.NormalFormat, GBuffer.EmissiveFormat, GBuffer.MotionFormat,
        GBuffer.BakedFormat, GBuffer.MaskFormat,
    ];

    private readonly List<PipelineVariants> _solids = [];
    private readonly List<PipelineVariants> _shown = [];
    private readonly List<PipelineVariants> _depthOnly = [];
    private readonly DepthFirst _depthFirst;

    public GBufferPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        var context = device.Context;
        _context = context;
        _resources = resources;

        _list = new SceneList(device, resources, [frameSetLayout, resources.Layout], SceneDraws.PushRange);

        // Depth alone: no fragment shader, and nothing written to the colour targets.
        Span<PipelineColorBlendAttachmentState> noColor = stackalloc PipelineColorBlendAttachmentState[ColorFormats.Length];
        var depthOnly = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            Layout = _list.Layout,
            ColorFormats = ColorFormats,
            Blends = noColor,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
        };
        var depthCulled = PipelineFactory.Create(_context, depthOnly);
        depthOnly.TwoSided = true;
        _depthOnly.Add(_list.Adopt(depthCulled, PipelineFactory.Create(_context, depthOnly)));
        _depthFirst = new DepthFirst(_depthOnly, _shown);

        // The first entry is the standard shader, which materials use by default.
        AddShader(null);
    }

    public int ShaderCount => _pipelines.Count;

    /// <summary>The frame's grass, whose blades are drawn after the solid things and before the decals, or none.</summary>
    public GrassStage? Grass { get; set; }

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
            Layout = _list.Layout,
            ColorFormats = ColorFormats,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = SceneDraws.VertexAttributes,
        };
        var culled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _pipelines.Add(_list.Adopt(culled, PipelineFactory.Create(_context, description)));

        // The same shader without its cut-outs, for materials with no holes, so hidden pixels are skipped unshaded.
        description.Solid = true;
        description.TwoSided = false;
        var solidCulled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _solids.Add(_list.Adopt(solidCulled, PipelineFactory.Create(_context, description)));

        // And once more for when their depth is drawn first, to shade only the pixels that show.
        description.DepthDrawn = true;
        description.TwoSided = false;
        var shownCulled = PipelineFactory.Create(_context, description);
        description.TwoSided = true;
        _shown.Add(_list.Adopt(shownCulled, PipelineFactory.Create(_context, description)));
        return new SurfaceShaderHandle(_pipelines.Count - 1);
    }

    /// <summary>
    /// Draws the solid things into <paramref name="targets"/>. Given the camera as <paramref name="depthFirst"/>,
    /// draws whose triangles are large in its view have their depth drawn first; see <see cref="DepthFirst"/>.
    /// <paramref name="pass"/> is <see cref="ScenePass.Opaque"/> for all of them, <see cref="ScenePass.OpaqueTakingDecals"/>
    /// for those the decals then lay on, or <see cref="ScenePass.OpaqueAfterDecals"/> for the rest, drawn over what the
    /// first round and the decals left in the targets, so where they stand in front of a decal they cover it.
    /// </summary>
    public void Record(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, in SceneGeometry scene, ScenePass pass = ScenePass.Opaque,
        DetailView? depthFirst = null)
    {
        var api = _context.Api;
        Begin(cmd, targets, clear: pass != ScenePass.OpaqueAfterDecals, frameSet);

        if (depthFirst is { } view)
        {
            _depthFirst.Aim(view, (float)targets.Extent.Width * targets.Extent.Height);
        }

        SceneDraws.Record(
            _list.Begin(), new ScenePipelines(_pipelines, _resources) { Solids = _solids }, scene,
            new SceneDrawOptions(pass) { DepthFirst = depthFirst is null ? null : _depthFirst });
        _list.Play(cmd);
        if (pass != ScenePass.OpaqueAfterDecals)
        {
            Grass?.RecordBlades(cmd, frameSet);
        }

        api.CmdEndRendering(cmd);
    }

    // Starts drawing into the targets, each cleared or keeping what's there, and binds the frame's and the materials' sets.
    private void Begin(CommandBuffer cmd, GBuffer targets, bool clear, DescriptorSet frameSet)
    {
        var api = _context.Api;
        var load = clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load;
        var colorAttachments = stackalloc RenderingAttachmentInfo[6];
        colorAttachments[0] = ColorAttachment(targets.BaseColor.View, load);
        colorAttachments[1] = ColorAttachment(targets.Normal.View, load);
        colorAttachments[2] = ColorAttachment(targets.Emissive.View, load);
        colorAttachments[3] = ColorAttachment(targets.Motion.View, load); // Cleared to no motion, which the sky keeps.
        colorAttachments[4] = ColorAttachment(targets.Baked.View, load); // Cleared to not lightmapped.
        colorAttachments[5] = ColorAttachment(targets.Mask.View, load, 1f); // Cleared to fully lit.
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = load,
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

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _list.Layout, 0, 2, sets, 0, null);
    }

    public void Dispose() => _list.Dispose();

    private static RenderingAttachmentInfo ColorAttachment(ImageView view, AttachmentLoadOp load, float clear = 0f) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = load,
        StoreOp = AttachmentStoreOp.Store,
        ClearValue = new ClearValue(new ClearColorValue(clear, clear, clear, clear)),
    };
}
