using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What lights the scene besides its surfaces: the shadow maps of the sun and the point and spot lights, the sky, the
/// reflection probes and the capture a frame makes of one, the planar reflection, the live light probes, and the volumetric fog.
/// </summary>
internal sealed class SceneLighting
{
    private readonly SceneStores _stores;

    public SceneLighting(SceneStores stores)
    {
        _stores = stores;
        var (context, kit, meshes) = (stores.Context, stores.Kit, stores.Meshes);
        ShadowPass = new ShadowPass((context, stores.Gpu), kit.FrameSetLayout, stores.Resources, stores.Settings.ShadowResolution);
        stores.Frames.WriteAtlas(ShadowPass.Atlas.View);
        SunShadows = new SunShadows(context.Api, ShadowPass, meshes, stores.Switches);
        LocalLightShadows = new LocalLightShadows((context, stores.Gpu), kit.FrameSetLayout, ShadowPass, stores.Switches,
            (meshes, stores.ShadowWorld, stores.Resources.Materials));
        // The weather map is made first, since the sky captures read it from their first draw.
        var cloudNoise = new CloudNoise(context, stores.Gpu);
        EnvironmentSky = new EnvironmentSky((context, stores.Gpu), stores.Ring, (cloudNoise.RepeatId, cloudNoise.Weather));
        stores.Frames.WriteSkySh(EnvironmentSky.Sh);
        var map = EnvironmentSky.Map;
        ReflectionProbes = new ReflectionProbes(context, stores.Gpu);
        var captureScene = new CaptureScene(meshes.MeshSlots, meshes.DetailSlots, ShadowPass);
        FrameReflectionCapture = new FrameReflectionCapture(new ReflectionCapturePass((context, stores.Gpu), kit.FrameSetLayout,
            stores.Resources, captureScene, map.Sky), ReflectionProbes, stores.Switches);
        ProbeVolume = new ProbeVolume((context, stores.Gpu), map.Sky);
        LightProbes = new LightProbes((context, stores.Gpu), map, kit,
            new LightProbes.Sources(stores.Resources, stores.Tracing, captureScene, stores.Ring, ProbeVolume));
        stores.Settings.Probes = LightProbes;
        FogStage = new FogStage((context, stores.Gpu), kit, stores.Switches);
        CloudStage = new CloudStage((context, stores.Gpu), kit, stores.Switches, cloudNoise);
        Planar = new PlanarStage((context, stores.Gpu), kit, stores.Resources, stores.Switches, (meshes.MeshSlots, meshes.DetailSlots));
    }

    /// <summary>The shadow maps: the sun's cascades and the lights' tiles.</summary>
    public ShadowPass ShadowPass { get; }

    /// <summary>The sun's cascades, fitted to what casts a shadow each frame.</summary>
    public SunShadows SunShadows { get; }

    /// <summary>The point and spot lights with their shadow tiles and light clusters.</summary>
    public LocalLightShadows LocalLightShadows { get; }

    /// <summary>The sky and the environment surfaces reflect, redrawn when it changes.</summary>
    public EnvironmentSky EnvironmentSky { get; }

    /// <summary>The reflection probes' cubes and the line of probes waiting for a capture.</summary>
    public ReflectionProbes ReflectionProbes { get; }

    /// <summary>The reflection probe a frame captures.</summary>
    public FrameReflectionCapture FrameReflectionCapture { get; }

    /// <summary>The atlases the light probes are kept in.</summary>
    public ProbeVolume ProbeVolume { get; }

    /// <summary>The live light probes. Tests reach their atlases, cascades and switches through this.</summary>
    public LightProbes LightProbes { get; }

    /// <summary>The volumetric fog.</summary>
    public FogStage FogStage { get; }

    /// <summary>The clouds.</summary>
    public CloudStage CloudStage { get; }

    /// <summary>The planar reflection: the scene drawn from the camera mirrored in one level plane.</summary>
    public PlanarStage Planar { get; }

    /// <summary>
    /// Makes the light probes' atlases on the first frame with the probes on, again after a change of
    /// <see cref="ProbeQuality"/>, and frees them when the probes are switched off. Each of those waits
    /// for the GPU, so this runs before the frame's fence, and an ordinary frame changes nothing. Returns whether the
    /// atlases changed, so the sets that read them must be written again.
    /// </summary>
    public bool KeepProbeStorage()
    {
        if (_stores.Switches.Off.HasFlag(RenderEffects.LightProbes) || !LightProbes.Volume.Supported)
        {
            bool released = LightProbes.Release();
            LightProbes.InUse = false;
            return released;
        }

        return LightProbes.Ensure();
    }

    /// <summary>
    /// Plans this frame's light probe work, before its uniforms are written: moves the cascades with
    /// the camera, tells the scheduler what changed, picks the batch of probes to update and where they
    /// are, and plans the probes' sun shadow map. Returns how many instances the work will draw.
    /// </summary>
    public int PlanProbes(in FrameView view, ReadOnlySpan<DrawItem> draws, int frameNumber)
    {
        var switches = _stores.Switches;
        return LightProbes.Plan(view, draws, switches.DrawnLocalLights(view),
            new ProbePlanInputs(RendererReflections.ProbeLightingKey(view, EnvironmentSky.Version, switches.Off), switches.ShadowSun(view), frameNumber));
    }

    /// <summary>Frees the shadow maps, the probes, the fog, the clouds, the mirror, the capture and the sky.</summary>
    public void Dispose()
    {
        ShadowPass.Dispose();
        LocalLightShadows.Dispose();
        ReflectionProbes.Dispose();
        LightProbes.Dispose();
        FogStage.Dispose();
        CloudStage.Dispose();
        Planar.Dispose();
        FrameReflectionCapture.Pass.Dispose();
        EnvironmentSky.Dispose();
    }
}
