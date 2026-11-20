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
    private readonly VulkanContext _context;
    private readonly FrameSwitches _switches;
    private readonly LocalLights _lights = new();
    private readonly PipelineLayout _clusterLayout;
    private readonly LivePipeline _clusterPipeline;

    public LocalLightShadows(
        VulkanContext context, DescriptorSetLayout frameSetLayout, ShadowPass pass, FrameSwitches switches,
        (MeshStore Meshes, ShadowWorld World, MaterialTable Materials) casters)
    {
        _context = context;
        _switches = switches;
        Tiles = new LightShadowTiles(context.Api, pass, switches, casters);
        _clusterLayout = context.CreatePipelineLayout([frameSetLayout]);
        _clusterPipeline = PipelineFactory.CreateCompute(context, "cluster.comp", _clusterLayout);
    }

    /// <summary>The lights' shadow tiles: what each draws, which a frame draws again, and how many it kept.</summary>
    public LightShadowTiles Tiles { get; }

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
        ClustersNeeded = _lights.LightCount > 0 || probes.Count > 0;
        uniforms->CountBlock.LocalLightCount = _lights.LightCount;
        uniforms->Clusters = new Vector4(_lights.NearDepth, _lights.FarDepth, _lights.Parallel ? 1f : 0f, 0f);
        uniforms->Screen = new Vector4(scene.Width, scene.Height, 1f / scene.Width, 1f / scene.Height);
    }

    /// <summary>
    /// Picks what each shadow tile of the atlas can see, one after another, and returns how many draws there are in
    /// all. A tile the atlas still holds as it would be drawn gets none.
    /// </summary>
    public int Select(ReadOnlySpan<DrawItem> draws, DrawBalls measured, float meshDetailScale) =>
        Tiles.Select(_lights.Tiles, draws, measured, meshDetailScale);

    /// <summary>Draws the shadow tiles of the point and spot lights that need it into the atlas.</summary>
    public void RecordTiles(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances) =>
        Tiles.Record(cmd, frameSet, _lights.Tiles, instances);

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
