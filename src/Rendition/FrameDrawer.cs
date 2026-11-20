using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Draws a frame from start to end: keeps the targets, takes the effects and the bakes, plans the probes, writes the
/// uniforms, picks the draws, records, submits and presents. <see cref="Render"/> allocates nothing on the managed
/// heap unless the window has been resized.
/// </summary>
internal sealed class FrameDrawer
{
    private readonly FrameParts _parts;
    private readonly RendererBaking _baking;
    private readonly FrameDrawPicker _picker;
    private readonly FrameRecorder _recorder;
    private readonly FramePresenter _presenter;

    // The view's lights with the sun coloured by the physical sky's air; see SkyModel.Tinted.
    private DirectionalLight[] _tintedLights = [];
    private bool _drawingWithoutBakedLights;

    public FrameDrawer(FrameParts parts, RendererBaking baking, RendererStats stats)
    {
        _parts = parts;
        _baking = baking;
        FrameCameras = new FrameCameras(parts.FrameTargets);
        _picker = new FrameDrawPicker(parts, baking.Frame, stats);
        FrameUniformWriter = new FrameUniformWriter(parts, baking.Frame, FrameCameras);
        _recorder = new FrameRecorder(parts, baking, (_picker, FrameCameras), stats);
        _presenter = new FramePresenter(parts, baking.Background, stats);
        var stores = parts.SceneStores;
        ProbeTests = new ProbeTestHooks(stores.Context, parts.SceneLighting.LightProbes, stores.Kit,
            () => stores.Frames[FrameUniformWriter.LastSlot].Set, parts.FrameTargets.WriteDescriptors);
    }

    /// <summary>The camera from frame to frame, and which frame this is.</summary>
    public FrameCameras FrameCameras { get; }

    /// <summary>Writes each frame's uniforms, and keeps what the last frame's gave, for tests.</summary>
    public FrameUniformWriter FrameUniformWriter { get; }

    /// <summary>What tests do to the live light probes.</summary>
    public ProbeTestHooks ProbeTests { get; }

    /// <summary>Draws the frame, with the sun coloured by the physical sky's air.</summary>
    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws) =>
        RenderTinted(SkyModel.Tinted(view, ref _tintedLights), draws);

    /// <summary>Draws the frame at the view's size and reads its picture back as RGBA bytes, waiting for the GPU.</summary>
    public unsafe byte[] Capture(in FrameView view, ReadOnlySpan<DrawItem> draws, out int width, out int height)
    {
        var (context, targets, finish) = (_parts.SceneStores.Context, _parts.FrameTargets, _parts.FrameStages.FinishStage);
        if ((uint)view.Width != targets.Swapchain.Extent.Width || (uint)view.Height != targets.Swapchain.Extent.Height)
        {
            targets.Resize((uint)view.Width, (uint)view.Height);
        }

        width = (int)targets.Swapchain.Extent.Width;
        height = (int)targets.Swapchain.Extent.Height;
        int size = width * height * 4;
        using var capture = GpuBuffer.CreateHostVisible(context, (ulong)size, BufferUsageFlags.TransferDstBit);

        finish.Capture = capture;
        finish.CaptureRecorded = false;
        try
        {
            Render(view, draws);
        }
        finally
        {
            // A frame that throws, such as for a draw it can't make, must not leave the next frame copying into the
            // buffer the using frees.
            finish.Capture = null;
        }

        context.WaitIdle();

        if (!finish.CaptureRecorded)
        {
            throw new InvalidOperationException("The window changed while capturing, so no frame was rendered.");
        }

        return ToRgba(new ReadOnlySpan<byte>(capture.Mapped, size));
    }

    // The swapchain stores BGRA. The picture is read back as RGBA, opaque.
    private static byte[] ToRgba(ReadOnlySpan<byte> bgra)
    {
        var pixels = bgra.ToArray();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private void RenderTinted(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (view.Width <= 0 || view.Height <= 0)
        {
            return; // Minimized: there is nothing to present to.
        }

        var (stores, targets) = (_parts.SceneStores, _parts.FrameTargets);
        var viewOff = stores.Switches.TakeDebugView(view, stores.Tracing is not null, stores.Context.Abilities.NoTraceReason);
        targets.Keep(view, viewOff);
        TakeEffects(view, viewOff);
        bool antiAliased = !stores.Switches.Off.HasFlag(RenderEffects.AntiAliasing);
        targets.KeepDisplaySource(antiAliased);

        CheckDraws(view, draws);
        if (!_drawingWithoutBakedLights && DrewWithoutBakedLights(view, draws))
        {
            return;
        }

        if (_parts.SceneLighting.KeepProbeStorage())
        {
            targets.WriteDescriptors();
        }

        ref FrameSlot frame = ref stores.Frames[stores.Ring.Index];
        if (!_presenter.Acquire(frame, view, out uint imageIndex))
        {
            return;
        }

        bool timed = _presenter.Reset(frame);
        var camera = FrameCameras.Of(view, antiAliased);
        int cameraCount = WriteFrameData(frame, view, draws, camera);
        var cmd = _recorder.Record(frame, view, imageIndex, new FrameDraws(_picker.CameraDraws(cameraCount), draws), timed);
        var renderFinished = targets.Swapchain.RenderFinished[imageIndex];
        _presenter.Submit(frame, cmd, renderFinished);
        _presenter.Present(view, imageIndex, renderFinished);
        stores.Ring.MoveToNextSlot();
    }

    /// <summary>
    /// Takes the effects the frame leaves out, and what follows from them: the wind's reach, the fade rules, the
    /// volumetric fog's targets, depth of field and the grade's table.
    /// </summary>
    private void TakeEffects(in FrameView view, RenderEffects viewOff)
    {
        var (stores, targets) = (_parts.SceneStores, _parts.FrameTargets);
        var settings = stores.Settings;
        stores.Switches.Take(view, viewOff, stores.Resources.Materials);
        if (_parts.SceneLighting.FogStage.KeepTargets(view, targets.GBuffer.Extent, (settings.VolumetricFogCellSize, settings.VolumetricFogSlices)))
        {
            targets.WriteDescriptors();
        }

        _parts.FrameStages.PostStage.TakeLens(view, stores.Switches.Off, (int)targets.GBuffer.Extent.Width);
        _parts.FrameStages.PostStage.Grade.Take(PostGrade.Without(view.Picture.Post, stores.Switches.Off), stores.Ring.Index);
        var fog = Fog.Pack(view.Weather.Fog, stores.Switches.Off, underwater: stores.Switches.Underwater(view));
        _parts.FrameStages.PostStage.Flare.Take(
            view, stores.Switches.Off, fog, (stores.Ring.Index, targets.GBuffer.Picture.HistoryReady, targets.GBuffer.Extent));
    }

    /// <summary>
    /// Checks the decals, the static draws and the baked chunks before anything is recorded, so a frame that can't be
    /// drawn throws with the frame untouched.
    /// </summary>
    private void CheckDraws(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        _parts.FrameStages.OpaqueStage.PrepareDecals(view);
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.Baked.Chunks);
    }

    /// <summary>
    /// Takes the finished bakes, works out the frame's baked areas and moves the background bake on. True when a fresh
    /// bake has taken some lights over, and the frame has been drawn again without them, its sun tinted already.
    /// </summary>
    private bool DrewWithoutBakedLights(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        // Before the bakes are resolved, so a file Done saves can be fresh this very frame.
        _baking.Background.TakeFinished();

        // Worked out once, from the original view: a frame drawn again without its baked sun keeps the sky that sun
        // lights, and its disc.
        _parts.SceneLighting.EnvironmentSky.Follow(view);
        bool relit = _baking.Frame.TryResolve(view, draws, _baking.Bakes, out var withoutBakedLights);

        // From the frame with every light, since a bake takes the lights a fresh bake would drop.
        _baking.Background.Update(view, draws, _baking.LastFrame);
        if (!relit)
        {
            return false;
        }

        _drawingWithoutBakedLights = true;
        try
        {
            RenderTinted(withoutBakedLights, draws);
        }
        finally
        {
            _drawingWithoutBakedLights = false;
        }

        return true;
    }

    /// <summary>
    /// Plans the probes, writes the frame's uniforms, bones and chunks, and picks what each view draws. Returns how
    /// many draws the camera has, at the head of <see cref="FrameDrawPicker.CameraDraws"/>.
    /// </summary>
    private int WriteFrameData(in FrameSlot frame, in FrameView view, ReadOnlySpan<DrawItem> draws, in FrameCamera camera)
    {
        var (stores, lights) = (_parts.SceneStores, _parts.SceneLighting);
        // Before the uniforms, which carry the probes' sun shadow maps and whether surfaces read the probes.
        int frameNumber = FrameCameras.FrameNumber;
        int probeInstances = lights.PlanProbes(view, draws, frameNumber) + lights.FrameReflectionCapture.Plan(view, draws, frameNumber);

        bool castsShadow = FrameUniformWriter.Write(frame, view, camera, _picker.Casters(draws));
        FrameSlots.WriteBones(frame, view.Bones);
        FrameCameras.Advance(frame, view, draws, camera);
        _parts.FrameStages.HeldPicture.Take(view.Picture.Transition);
        var (cameraCount, shadowCount) = _picker.Select(view, camera.ViewProjection, draws, castsShadow);
        int tileDrawCount = _picker.SelectTiles(draws);
        stores.Frames.ResetInstances(frame, cameraCount + shadowCount + tileDrawCount + probeInstances);
        _baking.Frame.Relight.WriteTo(frame.Chunks, _baking.Frame.MaskSlots.SpaceSlots);
        return cameraCount;
    }
}
