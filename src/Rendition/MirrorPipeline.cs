using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// How the planar reflection draws: mirror.vert and mirror.frag, culled and two-sided, and a set of their own with the
/// light probes' atlases, the sun's cascades and the environment's irradiance. See <see cref="PlanarStage"/>.
/// </summary>
internal sealed unsafe class MirrorPipeline : IDisposable
{
    // Where the surface stands, faces and is skinned, and its texture coordinates: what mirror.vert reads.
    private static readonly VertexInputAttributeDescription[] MirrorAttributes =
    [
        SceneDraws.VertexAttributes[0],
        SceneDraws.VertexAttributes[1],
        SceneDraws.VertexAttributes[3],
        SceneDraws.VertexAttributes[4],
        SceneDraws.VertexAttributes[5],
    ];

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];

    public MirrorPipeline(VulkanContext context, DescriptorKit kit, ResourceTable resources)
    {
        _context = context;
        _resources = resources;
        _setLayout = context.CreateSetLayout(LitCaptureSet.Bindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4));
        _set = context.AllocateSet(_pool, _setLayout);
        _layout = context.CreatePipelineLayout([kit.FrameSetLayout, resources.Layout, _setLayout], SceneDraws.PushRange);

        var description = new PipelineDescription
        {
            VertexShader = "mirror.vert",
            FragmentShader = "mirror.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.HdrFormat, GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = MirrorAttributes,
        };
        _pipelines.Add(new PipelineVariants(PipelineFactory.Create(context, description),
            PipelineFactory.Create(context, description with { TwoSided = true })));
    }

    /// <summary>
    /// Points the mirror at the light probes' atlases and the environment's irradiance, for the soft light, and at the
    /// sun's cascades. Call again whenever any is made anew.
    /// </summary>
    public void WriteSet(DescriptorImageInfo irradiance, DescriptorImageInfo depth, DescriptorImageInfo sunShadow, DescriptorImageInfo environment)
    {
        LitCaptureSet.Write(_context, _set, new LitCaptureTextures(irradiance, depth, sunShadow, environment));
    }

    /// <summary>Draws the scene's solid surfaces into the mirror's targets, which drawing has begun on.</summary>
    public void Draw(CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry geometry)
    {
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);
        SceneDraws.Record(new SceneTarget(_context.Api, cmd, _layout), new ScenePipelines(_pipelines, _resources),
            geometry, new SceneDrawOptions(ScenePass.Opaque));
    }

    public void Dispose()
    {
        foreach (var pipelines in _pipelines)
        {
            pipelines.Culled.Destroy();
            pipelines.TwoSided.Destroy();
        }

        _context.DestroyLayouts(_layout, _pool, _setLayout);
    }
}
