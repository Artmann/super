using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// Records a frame's command buffer: the uploads, the chunk bakes going up and a slice of the background bake, the
/// sky, and then the frame itself, from the shadow maps to the window.
/// </summary>
internal sealed unsafe class FrameRecorder(FrameParts parts, RendererBaking baking, FrameDrawPicker picker, FrameCameras cameras)
{
    /// <summary>Records the frame's command buffer: the uploads, a bake slice, the sky, and then the frame itself.</summary>
    public CommandBuffer Record(in FrameSlot frame, in FrameView view, uint imageIndex, in FrameDraws draws, bool timed)
    {
        var (stores, stages) = (parts.SceneStores, parts.FrameStages);
        var cmd = Begin(frame);
        // One top-level structure a frame, shared by the trace debug view and the traced probe updates, made only on
        // a frame that traces something.
        TopLevelStructure? traced = null;
        if (stores.Switches.TraceView || parts.SceneLighting.LightProbes.TracesBatch)
        {
            // After the uploads, so a mesh made this frame is traced this frame. From here until the submit below,
            // nothing may destroy or change a material: see RayTracing.TopLevel.
            traced = stores.Tracing!.TopLevel(cmd, stores.TraceScene!.Collect(draws.Scene));
        }

        int overlayQuads = stages.FinishStage.UploadOverlay(view.Extras.Overlay);
        stages.BlendStage.Upload(view);

        stages.OpaqueStage.UploadDecals();
        if (timed)
        {
            stages.GpuTimer.Begin(cmd, stores.Ring.Index);
        }

        // A changed sky, redrawn before the frame's passes read it. It waits on the GPU for earlier reads itself, so the
        // bake slice above traces last frame's sky, a frame behind, which the timer leaves out of the "Sky" pass.
        parts.SceneLighting.EnvironmentSky.RecordFrame(cmd);
        stages.GpuTimer.Mark(cmd);

        // Worked out here, just before the frame is recorded, so a frame that is never drawn doesn't move the clock.
        stages.PostStage.StepExposure(view, stores.Switches.Off);
        RecordFrame(cmd, frame.Set, imageIndex, new FrameDraws(draws.Camera, draws.Scene, traced), overlayQuads);
        stages.GpuTimer.End(cmd);
        stores.Context.Api.EndCommandBuffer(cmd).Check();
        return cmd;
    }

    /// <summary>
    /// Begins the frame's command buffer with the uploads, this frame's share of the chunk bakes going up, and a slice
    /// of the background bake.
    /// </summary>
    private CommandBuffer Begin(in FrameSlot frame)
    {
        var stores = parts.SceneStores;
        stores.Context.Api.ResetCommandPool(stores.Context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        stores.Context.Api.BeginCommandBuffer(cmd, &beginInfo).Check();
        var retiring = stores.Retired.AfterRecording;
        stores.Uploads.Record(cmd, retiring.FreedOthers);
        // The chunk bakes no copy named this frame are let go once it is done.
        baking.ChunkBakes.Record(cmd, retiring.FreedTextures, retiring.FreedOthers);
        // After the uploads, so a mesh made this frame is in the bake's scene.
        baking.Background.Slicer.RecordSlice(cmd);
        return cmd;
    }

    private void RecordFrame(CommandBuffer cmd, DescriptorSet frameSet, uint imageIndex, in FrameDraws draws, int overlayQuads)
    {
        var (stages, targets) = (parts.FrameStages, parts.FrameTargets);
        var gBuffer = targets.GBuffer;

        // Ldr still holds the last frame, so it's kept before this frame draws over it.
        stages.HeldPicture.Record(cmd, gBuffer);
        RecordScene(cmd, frameSet, draws);
        var display = (targets.ReadsAntiAliased, parts.SceneStores.Switches.Off);
        stages.ResolveStage.Record(cmd, frameSet, gBuffer, targets.Upscaling ? (stages.FsrPass, cameras.FsrFrame, stages.PostStage.Meter.Set) : null, display);
        stages.GpuTimer.Mark(cmd);
        stages.PostStage.Record(cmd, frameSet, gBuffer, display);
        stages.FinishStage.Record(cmd, frameSet, (targets.Swapchain, imageIndex), (gBuffer, stages.HeldPicture.Transition), overlayQuads);
    }

    /// <summary>
    /// Draws and lights the frame's scene into the HDR target. Its shadow draws were picked per cascade by
    /// <see cref="FrameDrawPicker.Select"/>. The whole scene's draws update this frame's batch of light probes and its
    /// reflection probe capture.
    /// </summary>
    private void RecordScene(CommandBuffer cmd, DescriptorSet frameSet, in FrameDraws draws)
    {
        var (stores, lights, stages) = (parts.SceneStores, parts.SceneLighting, parts.FrameStages);
        var gBuffer = parts.FrameTargets.GBuffer;
        var instances = stores.Frames[stores.Ring.Index].Instances;

        // The lighting pass binds history even before there is any.
        if (!gBuffer.Picture.HistoryReady)
        {
            stores.Context.Api.Transition(cmd, gBuffer.Picture.History.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
        }

        lights.SunShadows.Record(cmd, frameSet, instances);
        lights.LocalLightShadows.RecordTiles(cmd, frameSet, instances);
        stages.GpuTimer.Mark(cmd);
        RecordLightSetup(cmd, frameSet, draws, instances);
        stages.OpaqueStage.Record(cmd, gBuffer, frameSet, draws.Camera,
            new OpaqueInputs(instances, baking.Frame, lights.EnvironmentSky.Sh, cameras.Motion, picker.CameraChunks, picker.DepthFirstView));
        stages.GpuTimer.Mark(cmd);

        stages.OcclusionStage.Record(cmd, gBuffer, frameSet, stores.Switches.Off, stages.GpuTimer);

        if (stores.Switches.TraceView && draws.Traced is { } scene)
        {
            RecordTraceView(cmd, gBuffer, frameSet, scene);
            return;
        }

        stages.LightingStage.RecordReflections(cmd, gBuffer, frameSet, stores.Switches.Off, stages.GpuTimer);
        stages.LightingStage.RecordLighting(cmd, gBuffer, frameSet, stores.Switches.Off, stages.GpuTimer);
        stages.BlendStage.Record(cmd, gBuffer, frameSet, draws.Camera, (instances, lights.LightProbes, stages.LightingStage.LightingSet));
    }

    /// <summary>
    /// Updates the light probes and the reflection probe capture from the whole scene, sorts the lights into clusters
    /// and fills the volumetric fog, each as a timed pass, ahead of the G-buffer.
    /// </summary>
    private void RecordLightSetup(CommandBuffer cmd, DescriptorSet frameSet, in FrameDraws draws, InstanceBuffer instances)
    {
        var (lights, timer) = (parts.SceneLighting, parts.FrameStages.GpuTimer);

        // After the lights' tiles, which the probes' captures read for point and spot light shadows.
        lights.LightProbes.Record(cmd, frameSet, draws.Scene, instances, draws.Traced);
        timer.Mark(cmd);

        // After the light probes, whose light the capture's surfaces take.
        lights.FrameReflectionCapture.Record(cmd, frameSet, draws.Scene, instances, parts.SceneStores.Ring.Index);
        timer.Mark(cmd);

        if (lights.LocalLightShadows.ClustersNeeded)
        {
            lights.LocalLightShadows.RecordClusters(cmd, frameSet);
        }

        timer.Mark(cmd);

        // The fog's air needs the shadow maps and the light lists, and the lighting pass needs the fog.
        lights.FogStage.Record(cmd, frameSet);

        timer.Mark(cmd);
    }

    /// <summary>
    /// The trace debug view writes Hdr in place of lighting, and nothing blends over it. Depth is left read-only as the
    /// transparent pass would leave it, for the passes after. Marks the timer as the passes it stands in for would.
    /// </summary>
    private void RecordTraceView(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, TopLevelStructure scene)
    {
        var (stores, timer) = (parts.SceneStores, parts.FrameStages.GpuTimer);
        timer.Mark(cmd);
        parts.FrameStages.TraceDebugPass!.Record(cmd, stores.Ring.Index, scene, (frameSet, stores.Resources.Set), (targets.Hdr, targets.Extent));
        timer.Mark(cmd);
        timer.Mark(cmd);
        stores.Context.Api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
            AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
        timer.Mark(cmd);
    }
}
