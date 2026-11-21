namespace Rendition;

/// <summary>
/// The public face of the renderer. <see cref="Render"/> allocates nothing on the
/// managed heap unless the window has been resized.
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>The most skin matrices one frame can hold across every skinned draw.</summary>
    public const int MaxBones = FrameSlots.MaxBones;

    public Renderer(RendererOptions options)
    {
        var stores = new SceneStores(options);
        var first = FrameTargets.Start(stores, options);
        var lights = new SceneLighting(stores);
        var stages = new FrameStages(stores, lights, first.Scene, first.Window.Format);
        var targets = new FrameTargets(stores, (lights, stages), first);
        FrameParts = new FrameParts(stores, lights, stages, targets);
        Stats = new RendererStats(stores.Context, stages.GpuTimer, stages.GpuWork, () => targets.UpscalerVersion);
        targets.WriteDescriptors();

        (Baking, Assets) = AssetsAndBakes.Make(FrameParts);
        Luts = new RendererLuts(stages.PostStage.Grade, stores.Retired);
        FrameDrawer = new FrameDrawer(FrameParts, Baking, Stats);
        Reflections = new RendererReflections(
            (lights.ReflectionProbes, lights.FrameReflectionCapture), (lights.EnvironmentSky, lights.LightProbes, Baking.Frame), () =>
            {
                // The probes' cube array grew, so the lighting and the glass read the new one.
                targets.WriteDescriptors();
                stages.BlendStage.WriteProbes(lights.EnvironmentSky.Map.Sampler, lights.ReflectionProbes.Cubes.View);
            });
    }

    /// <summary>The stores, what lights the scene, the stages and the targets. Tests reach the renderer's insides here.</summary>
    internal FrameParts FrameParts { get; }

    /// <summary>Draws each frame. Tests reach the cameras, the uniforms and the probe hooks here.</summary>
    internal FrameDrawer FrameDrawer { get; }

    /// <summary>Vulkan validation messages so far. Always zero when validation is off.</summary>
    public int ValidationErrors => FrameParts.SceneStores.Context.Validation?.Errors ?? 0;

    /// <summary>
    /// Whether the GPU can trace rays, with ray queries turned on for the device. Nothing requires it: a GPU
    /// that can't, or <see cref="RendererOptions.DisableRayTracing"/>, just draws without tracing.
    /// </summary>
    public bool CanTrace => FrameParts.SceneStores.Context.CanTrace;

    /// <summary>Why <see cref="CanTrace"/> is false, such as "no VK_KHR_ray_query", or null when it is true.</summary>
    public string? NoTraceReason => FrameParts.SceneStores.Context.Abilities.NoTraceReason;

    /// <summary>The GPU this renderer draws with: its name, kind and memory.</summary>
    public GpuInfo Gpu => FrameParts.SceneStores.Context.Gpu;

    /// <summary>How the renderer draws: the quality and cost settings that take effect on the next frame.</summary>
    public RenderSettings Settings => FrameParts.SceneStores.Settings;

    /// <summary>What the renderer measured of its last frames: GPU times and work, waits, triangles and the upscaler.</summary>
    public RendererStats Stats { get; }

    /// <summary>
    /// What the renderer draws with: meshes, models, textures, materials, surface shaders and the overlay's atlas.
    /// </summary>
    public RendererAssets Assets { get; }

    /// <summary>The artists' colour tables, for <see cref="ColorGrade.Lut"/>.</summary>
    public RendererLuts Luts { get; }

    /// <summary>
    /// Baked lighting: where bakes are looked up, how they fade in, what the last frame's baked areas came to, the
    /// bakes that wait for the GPU and the ones the renderer makes by itself. See "Baked lighting" in docs/rendering.md.
    /// </summary>
    public RendererBaking Baking { get; }

    /// <summary>
    /// The reflection probes, which let surfaces in a box reflect what is really there, and the hashes that say when
    /// to capture them again.
    /// </summary>
    public RendererReflections Reflections { get; }

    /// <summary>
    /// Whether every probe near the camera, in the two finest cascades and the fixed grid, has been
    /// updated since its light last changed. Screenshots wait for it. False while the probes are switched off and
    /// before their first frame, but true on a GPU that can't run them, as there is nothing to wait for there.
    /// </summary>
    public bool ProbesSettled
    {
        get
        {
            var probes = FrameParts.SceneLighting.LightProbes;
            return probes.Batch.Scheduler?.Settled ?? !probes.Volume.Supported;
        }
    }

    /// <summary>
    /// Replaces what surfaces reflect. Nothing waits: the next frame redraws the environment before anything reads it,
    /// so it can change every frame.
    /// </summary>
    public void SetEnvironment(in EnvironmentDescription environment)
    {
        FrameParts.SceneLighting.EnvironmentSky.Set(environment);
    }

    /// <summary>
    /// A hash of what a baked space's light comes from besides its draws and lights: the environment's bands and panels
    /// from the last <see cref="SetEnvironment"/>, and the clear sky's turbidity and the environment intensity of the
    /// last frame drawn. It follows what they hold, so setting the same environment again gives the same hash. See
    /// "Is the bake fresh?" in docs/design.md.
    /// </summary>
    public ulong EnvironmentHash => FrameParts.SceneLighting.EnvironmentSky.Hash;

    /// <summary>
    /// Uploads a photographed sky, cooked from an equirectangular .hdr, for <see cref="AnalyticSky.Image"/>. It waits
    /// for the upload and stays until the renderer is disposed. See "A photographed sky" in docs/rendering.md.
    /// </summary>
    public SkyImageHandle CreateSkyImage(SkyImageData data) => FrameParts.SceneLighting.EnvironmentSky.Create(data);

    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws) => FrameDrawer.Render(view, draws);

    public byte[] Capture(in FrameView view, ReadOnlySpan<DrawItem> draws, out int width, out int height) =>
        FrameDrawer.Capture(view, draws, out width, out height);

    public void Dispose()
    {
        var (stores, stages) = (FrameParts.SceneStores, FrameParts.FrameStages);
        stores.Context.WaitIdle();
        stores.Uploads.Discard();
        Baking.Background.DropAll();
        for (int slot = 0; slot < FrameRing.Count; slot++)
        {
            stores.Retired.Release(slot);
        }

        // Its pages already in the material table go with the table.
        Baking.ChunkBakes.Dispose();
        stages.TraceDebugPass?.Dispose();
        Baking.Jobs.Bakers.Dispose();
        stores.Tracing?.Dispose();
        stages.GpuTimer.Dispose();
        Baking.Background.Dispose();
        stages.Dispose();
        FrameParts.SceneLighting.Dispose();
        stores.DisposeStores();
        FrameParts.FrameTargets.Dispose();
        stores.Context.Dispose();
    }
}
