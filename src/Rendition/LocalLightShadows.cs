using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The point and spot lights a frame draws: those the view needs, their shadow tiles in the atlas and what each tile
/// draws, and the clusters the lights are sorted into on the GPU.
/// </summary>
internal sealed unsafe class LocalLightShadows
{
    // How far a simpler model may stray in a shadow. Shadows are soft, so they can afford more than the camera.
    private const float ShadowDetailError = 2f;

    private readonly VulkanContext _context;
    private readonly ShadowPass _pass;
    private readonly MeshStore _meshes;
    private readonly FrameSwitches _switches;
    private readonly LocalLights _lights = new();
    private readonly (int First, int Count)[] _ranges = new (int, int)[LocalLights.MaxTiles];
    private readonly PipelineLayout _clusterLayout;
    private readonly LivePipeline _clusterPipeline;
    private DrawItem[] _draws = [];
    private int _tileCount;

    public LocalLightShadows(VulkanContext context, DescriptorSetLayout frameSetLayout, ShadowPass pass, MeshStore meshes, FrameSwitches switches)
    {
        _context = context;
        _pass = pass;
        _meshes = meshes;
        _switches = switches;
        _clusterLayout = context.CreatePipelineLayout([frameSetLayout]);
        _clusterPipeline = PipelineFactory.CreateCompute(context, "cluster.comp", _clusterLayout);
    }

    /// <summary>Whether this frame sorts anything into the clusters: a point or spot light, or a reflection probe.</summary>
    public bool ClustersNeeded { get; private set; }

    /// <summary>How many of the frame's lights light the probes' captures.</summary>
    public int ProbeLightCount => _lights.ProbeLightCount;

    /// <summary>
    /// Picks the point and spot lights the view needs and their shadow tiles, and writes them where the shaders read
    /// them. <paramref name="sorting"/> gives each light's shadow mask channel and the depths the reflection probes cover.
    /// </summary>
    public void Write(
        in FrameSlot frame, FrameUniforms* uniforms, in FrameView view, Extent2D scene,
        (IReadOnlyDictionary<int, int> MaskChannels, ReflectionProbeOrder Probes) sorting)
    {
        if (view.LocalLights.Length > LocalLight.MaxPerFrame)
        {
            throw new ArgumentException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights, not {view.LocalLights.Length}.", nameof(view));
        }

        var probes = sorting.Probes;
        _lights.Prepare(
            _switches.DrawnLocalLights(view),
            (view.View, view.Projection, (int)scene.Height),
            new Span<GpuLocalLight>(frame.Lights.Mapped, LocalLight.MaxPerFrame),
            new Span<GpuShadowTile>(frame.ShadowTiles.Mapped, LocalLights.MaxTiles),
            shadows: !_switches.Off.HasFlag(RenderEffects.LightShadows),
            maskChannels: sorting.MaskChannels,
            cover: probes.Count > 0 ? (probes.NearDepth, probes.FarDepth) : null);
        _tileCount = _lights.TileCount;
        ClustersNeeded = _lights.LightCount > 0 || probes.Count > 0;
        uniforms->CountBlock.LocalLightCount = _lights.LightCount;
        uniforms->Clusters = new Vector4(_lights.NearDepth, _lights.FarDepth, _lights.Parallel ? 1f : 0f, 0f);
        uniforms->Screen = new Vector4(scene.Width, scene.Height, 1f / scene.Width, 1f / scene.Height);
    }

    /// <summary>
    /// Picks what each shadow tile of the atlas can see, one after another, and returns how many there are in all.
    /// </summary>
    public int Select(ReadOnlySpan<DrawItem> draws, float meshDetailScale)
    {
        var tiles = _lights.Tiles;
        int total = 0;
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            if (_draws.Length < total + draws.Length * 2)
            {
                Array.Resize(ref _draws, Math.Max(_draws.Length * 2, total + draws.Length * 2));
            }

            ref readonly var view = ref tiles[tile];
            var shadow = new DetailView(
                view.Matrix, view.Eye, view.PixelsAtOneUnit, 0f, _switches.DetailError(ShadowDetailError, meshDetailScale), far: true, cull: _switches.Culls);
            int count = DrawSelection.Select(shadow, draws, _meshes.DetailSlots, _draws.AsSpan(total), _switches.WindReach, fades: _switches.FadeRules);
            _ranges[tile] = (total, count);
            total += count;
        }

        return total;
    }

    /// <summary>Draws the shadow tiles of the point and spot lights into the atlas.</summary>
    public void RecordTiles(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances)
    {
        var api = _context.Api;

        // The lighting pass binds the atlas even when no light has a tile in it.
        var atlas = _pass.Atlas.Image;
        if (_tileCount > 0)
        {
            api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, ShadowReadStages, SampledAccess, DepthStages,
                DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);
            _pass.RecordTiles(cmd, frameSet, _lights.Tiles, _ranges.AsSpan(0, _tileCount), new SceneGeometry(_draws, _meshes.MeshSlots, _meshes.DetailSlots, instances));
            api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, ShadowReadStages, SampledAccess);
        }
        else
        {
            api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ShadowReadStages, SampledAccess, ShadowReadStages, SampledAccess);
        }
    }

    /// <summary>
    /// Sorts the point and spot lights into the view's clusters on the GPU, before anything
    /// that lights reads the lists. The last frame to use this slot's lists has finished,
    /// as its fence was waited on, so only this frame's readers need to wait.
    /// </summary>
    public void RecordClusters(CommandBuffer cmd, DescriptorSet frameSet)
    {
        var api = _context.Api;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _clusterPipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _clusterLayout, 0, 1, &frameSet, 0, null);
        api.CmdDispatch(cmd, (LocalLights.ClusterCount + 63) / 64, 1, 1);

        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    public void Dispose()
    {
        _clusterPipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _clusterLayout, null);
    }
}
