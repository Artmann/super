using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// The frame's camera: its view and projection, last frame's, the shift of the scene's pixels and whether last
/// frame is known at all.
/// </summary>
internal readonly record struct FrameCamera(Matrix4x4 ViewProjection, Matrix4x4 Previous, Vector2 Shift, bool LastFrameKnown);

/// <summary>
/// The camera from frame to frame: which frame this is, last frame's view and time, where last frame's keyed draws
/// stood, and the shift of less than a pixel that anti-aliasing and FSR gather detail with.
/// </summary>
internal sealed unsafe class FrameCameras(FrameTargets targets)
{
    private const int JitterSteps = 16;

    private Matrix4x4 _previousViewProjection;

    /// <summary>The number the next frame drawn gets, which picks its frame of the dither's noise. For tests.</summary>
    public int FrameNumber { get; private set; }

    /// <summary>Last frame's time, for motion vectors.</summary>
    public float PreviousTime { get; private set; }

    /// <summary>FSR's frame while it upscales: the shift, the time since last frame and the camera's near plane and angle.</summary>
    public FsrFrame FsrFrame { get; private set; }

    /// <summary>Where last frame's keyed draws stood, for motion vectors.</summary>
    public MotionHistory Motion { get; } = new();

    /// <summary>
    /// Works out the frame's camera, with the shift of less than a pixel, different every frame, that lets
    /// anti-aliasing gather detail from between the pixels, and FSR's frame while it upscales.
    /// </summary>
    public FrameCamera Of(in FrameView view, bool antiAliased)
    {
        var gBuffer = targets.GBuffer;
        // Without anti-aliasing to gather it, the shift would only make the picture shake.
        // The pixels are the scene's, which the render scale may make smaller or larger than the window's.
        var viewProjection = view.View * view.Projection;
        // FSR takes as many shifts as it needs to land on every window pixel, more the smaller the scene.
        var scenePixels = new Vector2(gBuffer.Extent.Width, gBuffer.Extent.Height);
        int jitterPhases = targets.Upscaling ? Upscale.JitterPhaseCount((float)gBuffer.Extent.Width / gBuffer.Picture.Display.Width) : JitterSteps;
        var jitter = Upscale.Jitter(FrameNumber, jitterPhases);
        var shift = antiAliased ? jitter * 2f / scenePixels : Vector2.Zero;
        // The occlusion keeps a history of its own, which follows the camera and the draws while the picture has none.
        bool lastFrameKnown = gBuffer.Picture.HistoryReady || gBuffer.Occlusion.HistoryReady;
        var previous = lastFrameKnown ? _previousViewProjection : viewProjection;
        if (targets.Upscaling)
        {
            float milliseconds = (view.Time - PreviousTime) * 1000f;
            FsrFrame = new FsrFrame(
                jitter,
                gBuffer.Picture.HistoryReady && milliseconds > 0f ? milliseconds : 1000f / 60f,
                Reset: !gBuffer.Picture.HistoryReady,
                CameraNear(view.Projection),
                VerticalFov(view.Projection));
        }

        return new FrameCamera(viewProjection, previous, shift, lastFrameKnown);
    }

    /// <summary>
    /// Keeps where this frame's keyed draws stand, after its bones, and moves on to the next frame: this frame's view
    /// and time become last frame's.
    /// </summary>
    public void Advance(in FrameSlot frame, in FrameView view, ReadOnlySpan<DrawItem> draws, in FrameCamera camera)
    {
        Motion.Begin(draws, view.Bones, camera.LastFrameKnown,
            new Span<Matrix4x4>((Matrix4x4*)frame.Bones.Mapped + view.Bones.Length, 2 * FrameSlots.MaxBones - view.Bones.Length));
        _previousViewProjection = camera.ViewProjection;
        PreviousTime = view.Time;
        FrameNumber++;
    }

    /// <summary>
    /// The near plane's distance, for FSR. A reversed-Z perspective keeps it in M43; an orthographic view has no eye,
    /// so it gets a small stand-in.
    /// </summary>
    private static float CameraNear(in Matrix4x4 projection) =>
        projection.M34 != 0f && projection.M43 > 0f ? projection.M43 : 0.1f;

    /// <summary>The view's height as an angle, in radians, for FSR: from M22, which is -1 / tan(fov / 2).</summary>
    private static float VerticalFov(in Matrix4x4 projection) =>
        projection.M34 != 0f && projection.M22 != 0f ? 2f * MathF.Atan(1f / MathF.Abs(projection.M22)) : MathF.PI / 3f;
}
