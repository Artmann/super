using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The stages a frame is drawn through, from the G-buffer to the window: the opaque surfaces, the occlusion, the
/// lighting, what blends over it, anti-aliasing, the lens, bloom and tonemapping, the held picture and the finish,
/// with FSR, the trace debug view and the GPU timer.
/// </summary>
internal sealed class FrameStages
{
    public FrameStages(SceneStores stores, SceneLighting lights, GBuffer targets, Format windowFormat)
    {
        var (context, kit, ring, meshes, resources) = (stores.Context, stores.Kit, stores.Ring, stores.Meshes, stores.Resources);
        var map = lights.EnvironmentSky.Map;
        TraceScene = stores.Tracing is null ? null : new TraceScene(stores.TraceFlagsOf);
        TraceDebugPass = stores.Tracing is null ? null
            : new TraceDebugPass(context, stores.Tracing, FrameRing.Count, kit.FrameSetLayout, resources.Layout);
        var copier = new HistoryCopier(context.Api);
        OcclusionStage = new OcclusionStage((context, stores.Gpu), kit, copier);
        LightingStage = new LightingStage((context, stores.Gpu), kit,
            new LightingStage.Sources(resources, map, lights.ReflectionProbes, lights.ProbeVolume, lights.ShadowPass, lights.Planar));
        // Counting each pass's shaders needs a device feature; without it the frame is only timed.
        GpuWork = context.Abilities.PipelineStatistics ? new GpuWorkCounter(context, FrameRing.Count, GpuTimer.FramePasses) : null;
        GpuTimer = new GpuTimer(context, FrameRing.Count, counter: GpuWork);
        ResolveStage = new ResolveStage(stores.Gpu, kit, copier);
        // Bloom and tonemapping start out reading the anti-aliased image.
        PostStage = new PostStage((context, stores.Gpu), kit, GpuTimer, targets, targets.Picture.Resolved.View);
        HeldPicture = new HeldPicture(context.Api);
        FinishStage = new FinishStage((context, stores.Gpu), kit, ring, GpuTimer, (windowFormat, resources));
        BlendStage = new BlendStage(context, kit, (ring, GpuTimer, meshes), (resources, map, lights.ReflectionProbes), stores.Switches);
        OpaqueStage = new OpaqueStage(context, kit, ring, meshes, (resources, stores.Switches));
        Grass = new GrassStage(context, stores, GpuTimer);
        OpaqueStage.Pass.Grass = Grass;
        FsrPass = new FsrPass((context, stores.Gpu), kit.FrameSetLayout, PostStage.Meter.SetLayout);
    }

    /// <summary>What a frame's draws come to as the instances rays are traced through, or null when the GPU can't trace.</summary>
    public TraceScene? TraceScene { get; }

    /// <summary>Draws what rays hit in place of the lit picture, or null when the GPU can't trace.</summary>
    public TraceDebugPass? TraceDebugPass { get; }

    /// <summary>The screen-space occlusion and bounce.</summary>
    public OcclusionStage OcclusionStage { get; }

    /// <summary>The screen-space reflections and the lighting pass.</summary>
    public LightingStage LightingStage { get; }

    /// <summary>Counts the shaders each pass runs, or null when the GPU can't count them.</summary>
    public GpuWorkCounter? GpuWork { get; }

    /// <summary>Times each pass while the GPU is measured.</summary>
    public GpuTimer GpuTimer { get; }

    /// <summary>Anti-aliasing, by TAA or FSR.</summary>
    public ResolveStage ResolveStage { get; }

    /// <summary>The lens, bloom, the exposure meter and tonemapping.</summary>
    public PostStage PostStage { get; }

    /// <summary>The picture kept while a transition shows it.</summary>
    public HeldPicture HeldPicture { get; }

    /// <summary>The finish into the window, the overlay and screenshots.</summary>
    public FinishStage FinishStage { get; }

    /// <summary>Glass, particles and what else blends over the lit image.</summary>
    public BlendStage BlendStage { get; }

    /// <summary>The G-buffer and the decals.</summary>
    public OpaqueStage OpaqueStage { get; }

    /// <summary>The grass, grown over the ground before the shadows and the G-buffer draw it.</summary>
    public GrassStage Grass { get; }

    /// <summary>FSR, while it draws the picture.</summary>
    public FsrPass FsrPass { get; }

    /// <summary>Frees the stages from the finish back to the G-buffer. The timer and the trace view go first, apart.</summary>
    public void Dispose()
    {
        FinishStage.Dispose();
        FsrPass.Dispose();
        PostStage.Dispose();
        ResolveStage.Dispose();
        BlendStage.Dispose();
        LightingStage.Dispose();
        OcclusionStage.Dispose();
        OpaqueStage.Dispose();
        Grass.Dispose();
    }
}
