using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
    private readonly SceneList _list;
    private readonly List<PipelineVariants> _pipelines = [];

    // The mirror's two layers and its depth, adopted into the layer; see Draw.
    private readonly AdoptedTarget[] _held;

    public MirrorPipeline((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, ResourceTable resources)
    {
        _held = [new(device.Gpu), new(device.Gpu), new(device.Gpu)];
        var context = device.Context;
        _context = context;
        _resources = resources;
        _setLayout = context.CreateSetLayout(LitCaptureSet.Bindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4));
        _set = context.AllocateSet(_pool, _setLayout);
        _list = new SceneList(device, resources, [kit.FrameSetLayout, resources.Layout, _setLayout], SceneDraws.PushRange);

        var description = new PipelineDescription
        {
            VertexShader = "mirror.vert",
            FragmentShader = "mirror.frag",
            Layout = _list.Layout,
            ColorFormats = [GBuffer.HdrFormat, GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = MirrorAttributes,
        };
        _pipelines.Add(_list.Adopt(PipelineFactory.Create(context, description),
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

    /// <summary>
    /// Draws the scene's solid surfaces into the mirror's targets, both layers cleared to nothing drawn and the depth to
    /// the far plane. <see cref="MirrorTargets"/> moves them to drawing first and on after, so they are adopted held.
    /// </summary>
    public void Draw(CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry geometry, MirrorTargets targets)
    {
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _list.Layout, 0, 3, sets, 0, null);
        var into = _list.Begin();
        var (color, depth, extent) = (targets.Color, targets.Depth, targets.Extent);
        ReadOnlySpan<ColorTarget> layers =
        [
            new(_held[0].ForHeld(color.LayerViews[0], color.Image, extent), LoadOp.Clear, default),
            new(_held[1].ForHeld(color.LayerViews[1], color.Image, extent), LoadOp.Clear, default),
        ];
        into.Commands.BeginRenderPass(layers, new DepthTarget(_held[2].ForHeld(depth.View, depth.Image, extent), LoadOp.Clear, 0f, ReadOnly: false));
        SceneDraws.Record(into, new ScenePipelines(_pipelines, _resources), geometry, new SceneDrawOptions(ScenePass.Opaque));
        into.Commands.EndPass();
        _list.Play(cmd);
    }

    public void Dispose()
    {
        foreach (var held in _held)
        {
            held.Dispose();
        }

        _list.Dispose();
        _context.DestroySets(_pool, _setLayout);
    }
}
