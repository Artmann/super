using Silk.NET.Vulkan;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// Points the passes that read the render targets, the shadow maps and the light probes at them, after any of them
/// is made again.
/// </summary>
internal static class TargetDescriptors
{
    /// <summary>Points the fullscreen passes at the current render targets.</summary>
    public static void Write(SceneStores stores, (SceneLighting Lights, FrameStages Stages) passes, FrameTargets targets)
    {
        var (lights, stages) = passes;
        var kit = stores.Kit;
        var gBuffer = targets.GBuffer;
        var depth = new DescriptorImageInfo(kit.NearestSampler, gBuffer.Depth.View, ImageLayout.ShaderReadOnlyOptimal);
        var normal = new DescriptorImageInfo(kit.NearestSampler, gBuffer.Normal.View, ImageLayout.ShaderReadOnlyOptimal);
        stages.OpaqueStage.WriteTargets(gBuffer, depth, kit.NearestSampler);

        var shadowDepth = new DescriptorImageInfo(kit.NearestSampler, lights.ShadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal);
        stages.OcclusionStage.WriteSets(gBuffer, depth.ImageView, normal.ImageView, shadowDepth.ImageView);
        stages.LightingStage.WriteSets(gBuffer, depth, normal, shadowDepth);
        WriteBlendSets(stores, passes, gBuffer);
        WriteDisplaySets(stores, passes, targets);
    }

    // The reflection probe capture's and the mirror's sets, and what blends over the lit image: glass and particles.
    private static void WriteBlendSets(SceneStores stores, (SceneLighting Lights, FrameStages Stages) passes, GBuffer gBuffer)
    {
        var (lights, stages) = passes;
        var kit = stores.Kit;
        var map = lights.EnvironmentSky.Map;
        lights.FrameReflectionCapture.Pass.WriteSet(
            new(kit.LinearSampler, lights.LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(kit.LinearSampler, lights.LightProbes.Volume.DepthView, ImageLayout.General),
            new(map.Sampler, map.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            kit.ShadowSampler);
        lights.Planar.WriteSet(
            new(kit.LinearSampler, lights.LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(kit.LinearSampler, lights.LightProbes.Volume.DepthView, ImageLayout.General),
            new(kit.ShadowSampler, lights.ShadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(map.Sampler, map.Irradiance, ImageLayout.ShaderReadOnlyOptimal));

        stages.BlendStage.WriteTargets(gBuffer, lights.ShadowPass.Target.View, map);
    }

    // From anti-aliasing to the window: anti-aliasing, tonemapping, the meter and the finish, the volumetric fog and the clouds.
    private static void WriteDisplaySets(SceneStores stores, (SceneLighting Lights, FrameStages Stages) passes, FrameTargets targets)
    {
        var (lights, stages) = passes;
        var kit = stores.Kit;
        var map = lights.EnvironmentSky.Map;
        stages.ResolveStage.WriteSet(targets.GBuffer);
        stages.PostStage.WriteSets(targets.DisplaySource, targets.GBuffer);
        stages.FinishStage.WriteSet(targets.GBuffer);

        lights.FogStage.SetSources(
            new(kit.ShadowSampler, lights.ShadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(map.Sampler, map.Background, ImageLayout.ShaderReadOnlyOptimal),
            (new(kit.LinearSampler, lights.LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(kit.LinearSampler, lights.LightProbes.Volume.DepthView, ImageLayout.General)));
        stores.Frames.WriteVolumetric(lights.FogStage.Result);

        lights.CloudStage.SetSources(new(kit.LinearSampler, map.SkyView, ImageLayout.ShaderReadOnlyOptimal));
        stores.Frames.WriteClouds(lights.CloudStage.Shadow, lights.CloudStage.Picture);
        stages.PostStage.Flare.SetClouds(lights.CloudStage.Picture);
    }
}
