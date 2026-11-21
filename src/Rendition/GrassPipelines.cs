using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The grass's pipelines and the layouts and sets they share: the scatter, the ground map's draw, and the blades'
/// draws into the G-buffer and the shadow maps. See "Grass" in docs/design.md.
/// </summary>
internal sealed unsafe class GrassPipelines : IDisposable
{
    private const ShaderStageFlags Pushed = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;

    public GrassPipelines(VulkanContext context, SceneStores stores)
    {
        _context = context;
        _resources = stores.Resources;
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
        _layout = context.CreatePipelineLayout(
            [stores.Kit.FrameSetLayout, _resources.Layout, _setLayout],
            new PushConstantRange(Pushed, 0, (uint)sizeof(GrassConstants)));

        Scatter = PipelineFactory.CreateCompute(context, "grass_scatter.comp", _layout);
        Ground = CreateGround(context, _layout);
        Blades = CreateBlades(context, _layout);
        Shadow = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "grass_shadow.vert",
            Layout = _layout,
            DepthFormat = GBuffer.DepthFormat,
            TwoSided = true,
        });
    }

    /// <summary>Each frame slot's set of the grass's own buffers and maps.</summary>
    public DescriptorSet[] Sets { get; } = new DescriptorSet[FrameRing.Count];

    /// <summary>Scatters blades over the planned squares.</summary>
    public LivePipeline Scatter { get; }

    /// <summary>Draws the grounds' tops into the ground map.</summary>
    public LivePipeline Ground { get; }

    /// <summary>Draws the blades into the G-buffer.</summary>
    public LivePipeline Blades { get; }

    /// <summary>Draws the blades into a cascade of the sun's map.</summary>
    public LivePipeline Shadow { get; }

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

    /// <summary>Binds the frame's set, the resource table and the grass's set for <paramref name="slot"/>.</summary>
    public void Bind(CommandBuffer cmd, DescriptorSet frameSet, PipelineBindPoint point, int slot)
    {
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, Sets[slot] };
        _context.Api.CmdBindDescriptorSets(cmd, point, _layout, 0, 3, sets, 0, null);
    }

    /// <summary>Pushes a draw's constants.</summary>
    public void Push(CommandBuffer cmd, in GrassConstants constants)
    {
        fixed (GrassConstants* pushed = &constants)
        {
            _context.Api.CmdPushConstants(cmd, _layout, Pushed, 0, (uint)sizeof(GrassConstants), pushed);
        }
    }

    public void Dispose()
    {
        Scatter.Destroy();
        Ground.Destroy();
        Blades.Destroy();
        Shadow.Destroy();
        _context.DestroyLayouts(_layout, _pool, _setLayout);
    }
}
