using Rendition;

namespace Super;

/// <summary>
/// Draws a game's frames in a window: runs the game's Draw with the interface laid out for the window, hands the
/// renderer the game's graphics settings as they are now, and renders the frame, or captures it for a screenshot or a
/// video.
/// </summary>
internal sealed class WindowFrames(OsWindow window, SceneRenderer scene, bool hidden)
{
    /// <summary>Anti-aliasing builds up over several frames, so a screenshot waits for it to settle.</summary>
    private const int ScreenshotSettleFrames = 48;

    /// <summary>
    /// The most frames a screenshot waits, after <see cref="ScreenshotSettleFrames"/>, for the light probes near
    /// the camera to all be drawn. They're drawn a few a frame, so a busy scene may still be filling in at the cap.
    /// </summary>
    private const int ScreenshotProbeFrames = 2048;

    private const int ScreenshotPlayedTicks = 120;

    private const int RecordingSettleFrames = 16;

    private bool _takingShot;

    /// <summary>Hands the frames drawn to the stats panel while it shows.</summary>
    public RenderStatsFeed Stats { get; } = new(scene);

    /// <summary>
    /// What every frame shows when <c>--debug-view</c> asked for a view, or <see cref="Rendition.DebugView.None"/>
    /// when it didn't, and then the game's <see cref="Graphics.DebugView"/> decides.
    /// </summary>
    public Rendition.DebugView CommandLineDebugView { get; set; }

    /// <summary>Plays the given number of ticks and saves the picture. Returns the validation message count.</summary>
    public int Screenshot(GameLoop host, long ticks, string path, bool neutralLut = false)
    {
        // Nobody sees any frame of this run, and the count of frames, not the time, decides what the shot shows.
        _takingShot = true;
        PlayTo(host, ticks);
        Draw(host, 1f);
        float time = (float)host.Time.TotalSeconds;
        Settle(host, time, ScreenshotSettleFrames);
        SettleProbes(host, time);

        // Reflection probes are photographed one a frame and each waits in line once at most, so after a frame for every
        // probe each holds a picture taken in the settled light.
        Settle(host, time, scene.Renderer.Reflections.Count);
        MeasureForStats(host, time);

        host.Capture.Screenshot(path, neutralLut);
        Show(host, time);
        return scene.Renderer.ValidationErrors;
    }

    /// <summary>
    /// Plays the given number of ticks, then saves a video of <paramref name="frames"/> frames at
    /// <paramref name="framesPerSecond"/>. Returns the validation message count.
    /// </summary>
    public int Record(GameLoop host, long ticks, string path, int frames, int framesPerSecond)
    {
        PlayTo(host, ticks);

        // Blending from 0 means the first frame of the video carries straight on from here.
        Draw(host, 0f);
        float start = (float)host.Time.TotalSeconds;
        Settle(host, start, RecordingSettleFrames);

        var clock = new FixedStepClock(host.Time.TicksPerSecond);
        double step = 1.0 / framesPerSecond;
        host.Capture.StartRecording(path, framesPerSecond);
        for (int frame = 0; frame < frames; frame++)
        {
            int due = clock.Advance(step);
            for (int i = 0; i < due; i++)
            {
                host.Tick();
            }

            Draw(host, clock.Blend);
            Show(host, start + (float)(frame * step));
        }

        host.Capture.StopRecording();
        return scene.Renderer.ValidationErrors;
    }

    /// <summary>Plays the given number of ticks, then draws and renders the frame there. Returns the frame's time.</summary>
    public float PlayAndDraw(GameLoop host, long ticks)
    {
        PlayTo(host, ticks);
        Draw(host, 1f);
        float time = (float)host.Time.TotalSeconds;
        Settle(host, time, 1);
        return time;
    }

    public void PlayTo(GameLoop host, long ticks)
    {
        var (width, height) = window.FramebufferSize();

        // The last stretch is rendered as it plays, so changes during play, such as a
        // recolored material, go through the GPU under validation rather than being skipped.
        for (long i = 0; i < ticks; i++)
        {
            host.Tick();

            // A script aims at what's on screen, so the interface has to be laid out as it plays.
            if (host.Script is not null)
            {
                Draw(host, 1f);
            }

            if (i >= ticks - ScreenshotPlayedTicks)
            {
                Draw(host, 1f);
                Render(host, width, height, (float)host.Time.TotalSeconds);
            }
        }
    }

    private void Settle(GameLoop host, float time, int frames)
    {
        var (width, height) = window.FramebufferSize();
        for (int i = 0; i < frames; i++)
        {
            Render(host, width, height, time);
        }
    }

    /// <summary>
    /// Renders on until the light probes near the camera are all drawn, up to <see cref="ScreenshotProbeFrames"/>
    /// frames, so a screenshot shows the bounced light as it will be rather than still filling in. With the probes
    /// off there is nothing to wait for. Says how long it waited, as a slow scene can make a shot take a while.
    /// </summary>
    private void SettleProbes(GameLoop host, float time)
    {
        if (!host.Graphics.Lighting.LightProbes)
        {
            return;
        }

        var (width, height) = window.FramebufferSize();
        int frames = ExtraSettleFrames(() => scene.Renderer.ProbesSettled, () => Render(host, width, height, time), ScreenshotProbeFrames);
        if (frames == ScreenshotProbeFrames && !scene.Renderer.ProbesSettled)
        {
            Console.WriteLine($"The light probes were still filling in after {frames} more frames; the shot shows them as they are.");
        }
        else if (frames > 0)
        {
            Console.WriteLine($"Waited {frames} more frames for the light probes to settle.");
        }
    }

    /// <summary>
    /// With the stats panel shown, draws the settled frame over and over until the panel has measured half a second of
    /// them, so a screenshot shows the figures of frames like its own. Nothing ticks, so the shot is the same.
    /// </summary>
    private void MeasureForStats(GameLoop host, float time)
    {
        if (!host.Stats.Shown)
        {
            return;
        }

        host.Stats.Restart();
        for (int i = 0; i < RenderStatsFeed.MostScreenshotFrames && !host.Stats.Latest.Render.Measured; i++)
        {
            Show(host, time);
            Draw(host, 1f);
        }
    }

    /// <summary>
    /// Renders frames until <paramref name="settled"/> says there's nothing left to wait for, or <paramref name="cap"/>
    /// frames have been rendered, and returns how many were. None are rendered if it's settled already.
    /// </summary>
    internal static int ExtraSettleFrames(Func<bool> settled, Action renderFrame, int cap)
    {
        int frames = 0;
        while (frames < cap && !settled())
        {
            renderFrame();
            frames++;
        }

        return frames;
    }

    /// <summary>Runs the game's Draw with the interface laid out for the window as it is now.</summary>
    public void Draw(GameLoop host, float blend)
    {
        (host.ScreenWidth, host.ScreenHeight) = window.FramebufferSize();
        host.Draw(blend);
    }

    /// <summary>
    /// Puts the last Draw on screen, and hands it to the capture if that wants it, and to the stats panel while it
    /// shows.
    /// </summary>
    public void Show(GameLoop host, float time)
    {
        var (width, height) = window.FramebufferSize();
        bool measured = Stats.Begin(host.Stats);

        // A minimized window has no pixels to read.
        if (!host.Capture.WantsFrame || width == 0 || height == 0)
        {
            Render(host, width, height, time);
        }
        else
        {
            ApplyGraphics(host.Graphics);
            byte[] pixels = scene.Capture(host.View, host.Content, width, height, time, out int capturedWidth, out int capturedHeight);
            host.Capture.Accept(pixels, capturedWidth, capturedHeight);
        }

        if (measured)
        {
            Stats.Add(host.Stats, width, height);
        }
    }

    /// <summary>Renders the last Draw with the game's graphics settings as they are now.</summary>
    private void Render(GameLoop host, int width, int height, float time)
    {
        ApplyGraphics(host.Graphics);
        scene.Render(host.View, host.Content, width, height, time);
    }

    /// <summary>Hands the game's graphics settings, as they are now, to the renderer for the next frame.</summary>
    public void ApplyGraphics(Graphics graphics)
    {
        var settings = scene.Renderer.Settings;
        (scene.Off, settings.Vsync, settings.ShadowResolution, settings.MaxShadowCascades, settings.MeshDetailScale,
            settings.RenderScale, settings.Upscaler, settings.ProbeQuality, settings.ReflectionResolution,
            settings.OcclusionResolution, settings.VolumetricFogCellSize, settings.VolumetricFogSlices, scene.DebugView) =
            RendererSettings(graphics, hidden, _takingShot, CommandLineDebugView, scene.Renderer.CanTrace);
    }

    /// <summary>
    /// What the renderer is told from the game's graphics settings: the effects left out, whether to wait
    /// for the display, the sun's shadow map size and cascades, the mesh detail, the render scale, the upscaler, the light
    /// probes' quality, the resolution of the reflections and the occlusion, the volumetric fog's grid, and the debug view.
    /// A hidden window always waits for the display, except for a screenshot, where waiting would only slow the
    /// shot down. The debug view is the command line's when it named one,
    /// else the game's <see cref="Graphics.DebugView"/>, except that a game's Trace view is dropped to None on a
    /// GPU that can't trace (<paramref name="canTrace"/>), where the renderer would throw.
    /// </summary>
    internal static (RenderEffects Off, bool Vsync, int ShadowResolution, int ShadowCascades, float MeshDetail, float RenderScale,
        Upscaler Upscaler, ProbeQuality ProbeQuality, ReflectionResolution ReflectionResolution,
        OcclusionResolution OcclusionResolution, int VolumetricFogCellSize, int VolumetricFogSlices, Rendition.DebugView DebugView)
        RendererSettings(
            Graphics graphics, bool hidden, bool takingShot = false, Rendition.DebugView commandLineDebugView = Rendition.DebugView.None,
            bool canTrace = true)
    {
        var debugView = commandLineDebugView;
        if (debugView == Rendition.DebugView.None && (graphics.DebugView != Rendition.DebugView.Trace || canTrace))
        {
            debugView = graphics.DebugView;
        }

        var (screen, shadowing, lighting, atmosphere) = (graphics.Screen, graphics.Shadowing, graphics.Lighting, graphics.Atmosphere);
        return (GraphicsSwitches.Off(graphics), hidden ? !takingShot : screen.Vsync, shadowing.ShadowResolution, shadowing.ShadowCascades,
            graphics.Drawing.MeshDetail, screen.RenderScale, screen.Upscaler, lighting.ProbeQuality, lighting.ReflectionResolution,
            lighting.OcclusionResolution, atmosphere.VolumetricFogCellSize, atmosphere.VolumetricFogSlices, debugView);
    }
}
