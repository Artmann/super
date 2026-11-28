using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The grass's pipelines and the layouts and sets they share: the scatter, the ground map's draw, and the blades'
/// draws into the G-buffer and the shadow maps, all handed to the GPU layer. See "Grass" in docs/design.md.
/// </summary>
internal sealed unsafe class GrassPipelines : IDisposable
{
    private const ShaderStageFlags Pushed = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;
    private readonly BindGroupId _resources;
    private readonly BindGroupId[] _slots = new BindGroupId[FrameRing.Count];

    public GrassPipelines(VulkanContext context, SceneStores stores)
    {
        _context = context;
        _gpu = stores.Gpu;
        const ShaderStageFlags compute = ShaderStageFlags.ComputeBit;
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.UniformBuffer, 1, Pushed | compute),
            new(1, DescriptorType.StorageBuffer, 1, compute),
            new(2, DescriptorType.StorageBuffer, 1, compute | ShaderStageFlags.VertexBit),
            new(3, DescriptorType.StorageBuffer, 1, compute),
            new(4, DescriptorType.CombinedImageSampler, 1, compute),
            new(5, DescriptorType.CombinedImageSampler, 1, compute),
        ]);
        _pool = context.CreateDescriptorPool(
            FrameRing.Count,
            new DescriptorPoolSize(DescriptorType.UniformBuffer, FrameRing.Count),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 3 * FrameRing.Count),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2 * FrameRing.Count));
        context.AllocateSets(_pool, _setLayout, Sets);
        DescriptorSetLayout[] setLayouts = [stores.Kit.FrameSetLayout, stores.Resources.Layout, _setLayout];
        _layout = context.CreatePipelineLayout(setLayouts, new PushConstantRange(Pushed, 0, (uint)sizeof(GrassConstants)));
        _resources = _gpu.Adopt(stores.Resources.Set);
        for (int i = 0; i < FrameRing.Count; i++)
        {
            _slots[i] = _gpu.Adopt(Sets[i]);
        }

        Scatter = new ComputePass(_gpu, "grass_scatter.comp", setLayouts);
        Ground = Adopt(CreateGround(context, _layout), setLayouts);
        Blades = Adopt(CreateBlades(context, _layout), setLayouts);
        Shadow = Adopt(PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "grass_shadow.vert",
            Layout = _layout,
            DepthFormat = GBuffer.DepthFormat,
            TwoSided = true,
        }), setLayouts);
    }

    /// <summary>Each frame slot's set of the grass's own buffers and maps.</summary>
    public DescriptorSet[] Sets { get; } = new DescriptorSet[FrameRing.Count];

    /// <summary>Scatters blades over the planned squares.</summary>
    public ComputePass Scatter { get; }

    /// <summary>Draws the grounds' tops into the ground map.</summary>
    public PipelineId Ground { get; }

    /// <summary>Draws the blades into the G-buffer.</summary>
    public PipelineId Blades { get; }

    /// <summary>Draws the blades into a cascade of the sun's map.</summary>
    public PipelineId Shadow { get; }

    /// <summary>
    /// Fills <paramref name="groups"/> with what every grass pipeline reads: the frame's set, the resource table and
    /// the grass's set for <paramref name="slot"/>, in that order.
    /// </summary>
    public void Groups(Span<BindGroupId> groups, DescriptorSet frameSet, int slot)
    {
        groups[0] = _gpu.Adopt(frameSet);
        groups[1] = _resources;
        groups[2] = _slots[slot];
    }

    /// <summary>Sets a draw's constants.</summary>
    public static void SetConstants(CommandList commands, in GrassConstants constants) =>
        commands.SetConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<GrassConstants>(in constants)));

    private PipelineId Adopt(LivePipeline pipeline, DescriptorSetLayout[] setLayouts) =>
        _gpu.Adopt(pipeline, _layout, setLayouts, Pushed);

    // Draws the ground's top from above: both faces, so a mesh wound either way counts, and the highest wins.
    private static LivePipeline CreateGround(VulkanContext context, PipelineLayout layout)
    {
        ReadOnlySpan<VertexInputBindingDescription> bindings = [new(0, (uint)sizeof(Vertex), VertexInputRate.Vertex)];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))),
        ];
        ReadOnlySpan<Format> info = [GrassTargets.InfoFormat];
        return PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "grass_ground.vert",
            FragmentShader = "grass_ground.frag",
            Layout = layout,
            ColorFormats = info,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            TwoSided = true,
        });
    }

    private static LivePipeline CreateBlades(VulkanContext context, PipelineLayout layout) =>
        PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "grass.vert",
            FragmentShader = "grass.frag",
            Layout = layout,
            ColorFormats = GBufferPass.ColorFormats,
            DepthFormat = GBuffer.DepthFormat,
            TwoSided = true,
            Solid = true,
        });

    public void Dispose()
    {
        Scatter.Dispose();
        _gpu.Destroy(Ground);
        _gpu.Destroy(Blades);
        _gpu.Destroy(Shadow);
        _context.DestroyLayouts(_layout, _pool, _setLayout);
    }
}
