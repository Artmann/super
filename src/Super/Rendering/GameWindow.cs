using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Super.Profiling;

namespace Super;

/// <summary>The real window: GLFW for the operating system, and Rendition for the picture.</summary>
internal sealed unsafe class GameWindow : IDisposable
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

    /// <summary>Frames played before measuring, so results don't include .NET still warming up.</summary>
    private const int ProfileWarmupFrames = 120;

    private const int ProfileFramesPerSecond = 60;

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly SceneRenderer _scene;
    private readonly float[] _gamepadAxes = new float[Input.GamepadAxisCount];
    private readonly bool[] _gamepadButtons = new bool[Input.GamepadButtonCount];

    // GLFW keeps only a pointer to each callback, so the delegates live here to stay alive.
    private GlfwCallbacks.KeyCallback? _onKey;
    private GlfwCallbacks.MouseButtonCallback? _onMouseButton;
    private GlfwCallbacks.CursorPosCallback? _onCursorPos;
    private GlfwCallbacks.CursorEnterCallback? _onCursorEnter;
    private GlfwCallbacks.ScrollCallback? _onScroll;
    private GlfwCallbacks.CharCallback? _onChar;
    private readonly Func<int, bool> _isGamepad;
    private readonly WindowCursor _cursor = new();

    /// <summary>Screenshots, recordings and profiles keep vsync, so they're the same on every computer.</summary>
    private readonly bool _hidden;

    public GameWindow(
        string title, int windowWidth, int windowHeight, bool hidden, bool validate, bool disableVsync, int shadowResolution,
        float renderScale = 1f, Upscaler upscaler = Upscaler.Taa, bool maximized = false, bool borderless = false,
        bool disableRayTracing = false)
    {
        _hidden = hidden;
        _glfw = Glfw.GetApi();
        _isGamepad = slot => _glfw.JoystickIsGamepad(slot);
        if (!_glfw.Init())
        {
            throw new InvalidOperationException("Could not initialize GLFW.");
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        // Every window starts hidden, so a shown one appears already in its place rather than jumping there.
        _glfw.WindowHint(WindowHintBool.Visible, false);

        // Windows shrinks a window whose title bar would push it past the screen's height,
        // and a hidden window should come out at exactly the size that was asked for.
        // A window with no screen to fill (a screenshot, a recording) keeps the size that was asked for, so it ignores both options.
        maximized &= !hidden;
        borderless &= !hidden;
        _glfw.WindowHint(WindowHintBool.Decorated, !hidden && !borderless);
        _glfw.WindowHint(WindowHintBool.Maximized, maximized);
        _window = _glfw.CreateWindow(windowWidth, windowHeight, title, null, null);
        if (_window is null)
        {
            _glfw.Terminate();
            throw new InvalidOperationException("Could not create a window.");
        }

        if (!hidden)
        {
            // A maximized window is already in its place, and centring it would pull it off the corner.
            if (!maximized)
            {
                CentreOnScreen();
            }

            _glfw.ShowWindow(_window);
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        _scene = new SceneRenderer(new RendererOptions
        {
            InstanceExtensions = GetInstanceExtensions(),
            CreateSurface = CreateSurface,
            Width = width,
            Height = height,
            EnableValidation = validate,
            DisableVsync = disableVsync && !hidden,
            DisableRayTracing = disableRayTracing,
            ShadowResolution = shadowResolution,
            RenderScale = renderScale,
            Upscaler = upscaler,
        });
    }

    // Left to itself Windows cascades new windows down the screen. This centres the window, title bar
    // and all, in the primary monitor's work area, and keeps the title bar on screen if it doesn't fit.
    private void CentreOnScreen()
    {
        var monitor = _glfw.GetPrimaryMonitor();
        if (monitor is null)
        {
            return;
        }

        _glfw.GetMonitorWorkarea(monitor, out int areaX, out int areaY, out int areaWidth, out int areaHeight);
        _glfw.GetWindowFrameSize(_window, out int left, out int top, out int right, out int bottom);
        _glfw.GetWindowSize(_window, out int width, out int height);
        int x = areaX + (areaWidth - (left + width + right)) / 2 + left;
        int y = areaY + Math.Max(0, (areaHeight - (top + height + bottom)) / 2) + top;
        _glfw.SetWindowPos(_window, x, y);
    }

    /// <summary>The game loop: runs whatever ticks are due, then draws one frame.</summary>
    public void Run<TGame>(GameHost<TGame> host, int ticksPerSecond)
        where TGame : class, IGame
    {
        var clock = new FixedStepClock(ticksPerSecond);
        var realTime = Stopwatch.StartNew();
        double last = 0;
        double played = 0;
        if (host.Script is null)
        {
            ListenForInput(host.Input);
        }

        // Only here can a player see a loading screen, and only here may real time decide its length.
        host.Content.Streaming = true;
        host.Content.GoLive();

        // Only here is missing lighting baked as it appears, and only here does a bake that arrives fade in.
        _scene.BakeFadeSeconds = PlayBakeFadeSeconds;
        _autoBake.TurnOn();
        host.ShowsBakes = true;
        ApplyAutoBake(host);

        var limiter = new FrameLimiter(TimeProvider.System);
        IDisposable? fineSleep = null;
        try
        {
            while (!_glfw.WindowShouldClose(_window))
            {
                PlayFrame(host, clock, realTime, ref last, ref played);

                int limit = host.Graphics.MaxFramesPerSecond;
                if (limit > 0)
                {
                    fineSleep ??= FrameLimiter.FineSleep();
                }

                FrameLimiter.Wait(limiter.Next(limit));
            }
        }
        finally
        {
            fineSleep?.Dispose();
        }
    }

    private void PlayFrame<TGame>(GameHost<TGame> host, FixedStepClock clock, Stopwatch realTime, ref double last, ref double played)
        where TGame : class, IGame
    {
        _glfw.PollEvents();
        if (host.Script is null)
        {
            PollGamepad(host.Input);
        }

        double now = realTime.Elapsed.TotalSeconds;
        double step = host.Capture.IsRecording ? 1.0 / host.Capture.FramesPerSecond : now - last;
        last = now;
        played += step;

        int ticks = clock.Advance(step);
        for (int i = 0; i < ticks; i++)
        {
            host.Tick();
        }

        _cursor.Apply(_glfw, _window, host.Input, host.Ui.Scale);
        host.BakeProgress = _scene.BakeProgress;
        Draw(host, clock.Blend);
        ApplyAutoBake(host);
        Show(host, (float)played);
    }

    /// <summary>How long a bake that arrives on screen fades in during play.</summary>
    internal const float PlayBakeFadeSeconds = 0.5f;

    /// <summary>
    /// The chunk bakes made on this machine, which play reads and writes. Set before <see cref="Run"/>; other runs read
    /// only cooked bakes.
    /// </summary>
    internal BakeCache? BakeCache
    {
        set => _scene.BakeCache = value;
    }

    private readonly AutoBakeSwitch _autoBake = new();

    private static DevSettings? DevOf<TGame>(GameHost<TGame> host)
        where TGame : class, IGame =>
        host.Content.Files is CookedFolder { Dev: { Roots.Length: > 0 } dev } ? dev : null;

    /// <summary>
    /// Hands the renderer what to bake by itself, from <see cref="Graphics.BakeOnArrival"/> and
    /// <see cref="Graphics.BakeBudget"/>, the first time and whenever either changes. Unchanged, the renderer keeps the
    /// same <see cref="Rendition.AutoBake"/>, since a new one would start its queue again. Called between frames, as
    /// setting it waits for the GPU the first time.
    /// </summary>
    private void ApplyAutoBake<TGame>(GameHost<TGame> host)
        where TGame : class, IGame
    {
        if (!_autoBake.Changed(host.Graphics))
        {
            return;
        }

        var dev = DevOf(host);
        _scene.BakeOnArrival(AutoBakePolicy.For(dev, CanTrace, host.Graphics));

        // From source, a GPU that can't trace says once why the lighting stays live; a player isn't told.
        _scene.SaysWhyNotBaking = host.Graphics.BakeOnArrival && !CanTrace && dev is not null;
    }

    /// <summary>
    /// What every frame shows when <c>--debug-view</c> asked for a view, or <see cref="Rendition.DebugView.None"/>
    /// when it didn't, and then the game's <see cref="Graphics.DebugView"/> decides. See <see cref="SceneRenderer.DebugView"/>.
    /// </summary>
    public Rendition.DebugView DebugView
    {
        get => _commandLineDebugView;
        set
        {
            _commandLineDebugView = value;
            _scene.DebugView = value;
        }
    }

    private Rendition.DebugView _commandLineDebugView;

    /// <summary>Whether the GPU can trace rays.</summary>
    public bool CanTrace => _scene.CanTrace;

    /// <summary>Why <see cref="CanTrace"/> is false, or null when it is true.</summary>
    public string? NoTraceReason => _scene.NoTraceReason;

    /// <summary>The GPU the game is drawn with.</summary>
    public Rendition.GpuInfo Gpu => _scene.Gpu;

    /// <summary>Plays the given number of ticks and saves the picture. Returns the validation message count.</summary>
    public int Screenshot<TGame>(GameHost<TGame> host, long ticks, string path)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);
        Draw(host, 1f);
        float time = (float)host.Time.TotalSeconds;
        Settle(host, time, ScreenshotSettleFrames);
        SettleProbes(host, time);

        host.Capture.Screenshot(path);
        Show(host, time);
        return _scene.ValidationErrors;
    }

    /// <summary>
    /// Plays the given number of ticks, then bakes the frame's baked space, if it names one, and each distinct chunk file
    /// it draws, and saves them in <paramref name="folder"/>, the first content root, as
    /// <c>lighting/&lt;name&gt;.lightbake</c> and <c>lighting/&lt;name&gt;/&lt;hash&gt;.lightbake</c>. Chunks with the
    /// same contents share a file, which is baked once. Prints what each bake found. A chunk that owns no static draws is
    /// reported and skipped, and counted in <paramref name="failed"/>. Returns the validation message count.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame names no baked space and no chunk.</exception>
    public int Bake<TGame>(GameHost<TGame> host, long ticks, BakeQuality quality, string folder, out int failed)
        where TGame : class, IGame
    {
        float time = PlayAndDraw(host, ticks);
        var chunks = host.View.BakedChunks;
        var frame = _scene.LastBakeFrame;
        var targets = BakeCommand.Targets(
            host.View.BakedSpace is { } named ? BakeFiles.Space(named.Name) : null,
            chunks.Count,
            chunk => frame!.ChunkFile(chunk));

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        ApplyGraphics(host.Graphics);
        failed = BakeCommand.Bake(
            targets,
            target => target.Chunk == BakeTarget.Space || frame!.ChunkStaticDraws(target.Chunk) > 0,
            target =>
            {
                // Each is baked at its area's own texel density, which is what a frame checks its bake against.
                byte[] bake = target.Chunk == BakeTarget.Space
                    ? _scene.Bake(host.View, host.Content, width, height, time,
                        new BakeSettings(host.View.BakedSpace!.Value.TexelsPerMetre, quality))
                    : _scene.BakeChunk(host.View, host.Content, width, height, time, target.Chunk,
                        new BakeSettings(chunks[target.Chunk].TexelsPerMetre, quality));

                string path = Path.GetFullPath(Path.Combine(folder, target.File));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bake);
                Console.Write(DescribeBake(path, bake.Length, LightBakeFile.Read(bake).Report));
            },
            Console.Out);

        return _scene.ValidationErrors;
    }

    /// <summary>
    /// Plays the given number of ticks, draws the frame and checks the bake of its baked space and of each distinct
    /// chunk file in the cooked content, the one the frame was drawn with, printing a line for each: missing, stale
    /// with what changed, or fresh. Returns the number of missing or stale bakes.
    /// </summary>
    public int BakeCheck<TGame>(GameHost<TGame> host, long ticks)
        where TGame : class, IGame
    {
        PlayAndDraw(host, ticks);
        var frame = _scene.LastBakeFrame;
        if (frame is null)
        {
            return BakeCommand.Check([], _ => null, Console.Out);
        }

        var targets = BakeCommand.Targets(
            frame.Space is not null && host.View.BakedSpace is { } space ? BakeFiles.Space(space.Name) : null,
            frame.Chunks.Count,
            chunk => frame.ChunkFile(chunk));
        return BakeCommand.Check(
            targets,
            target =>
            {
                var inputs = target.Chunk == BakeTarget.Space ? frame.Space!.Value : frame.Chunks[target.Chunk];
                return _scene.Bakes.Saved(target.File) is { } saved ? inputs.Changes(saved) : null;
            },
            Console.Out);
    }

    /// <summary>What <see cref="Bake"/> prints about a bake it saved.</summary>
    internal static string DescribeBake(string path, long bytes, LightBakeReport report)
    {
        string lights = report.LightsWithoutChannel.Count == 0
            ? "none"
            : string.Join(", ", report.LightsWithoutChannel.Select(key => key.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture,
            $"""
            {path}: {bytes / 1024.0 / 1024.0:0.0} MB
              time: {report.Time.TotalSeconds:0.0} s
              samples: {report.Samples} a texel
              invalid texels: {report.InvalidTexelShare * 100f:0.00}%
              Mixed lights without a shadow mask channel: {lights}

            """);
    }

    /// <summary>Plays the given number of ticks, then draws and renders the frame there. Returns the frame's time.</summary>
    private float PlayAndDraw<TGame>(GameHost<TGame> host, long ticks)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);
        Draw(host, 1f);
        float time = (float)host.Time.TotalSeconds;
        Settle(host, time, 1);
        return time;
    }

    /// <summary>Plays the given number of ticks, then saves a video. Returns the validation message count.</summary>
    public int Record<TGame>(GameHost<TGame> host, long ticks, string path, int frames, int framesPerSecond, int ticksPerSecond)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);

        // Blending from 0 means the first frame of the video carries straight on from here.
        Draw(host, 0f);
        float start = (float)host.Time.TotalSeconds;
        Settle(host, start, RecordingSettleFrames);

        var clock = new FixedStepClock(ticksPerSecond);
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
        return _scene.ValidationErrors;
    }

    /// <summary>Plays the given number of ticks, then measures some frames and writes a report.</summary>
    public void Profile<TGame>(GameHost<TGame> host, string title, int seed, long ticks, int frames, int ticksPerSecond, string path)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);
        var clock = new FixedStepClock(ticksPerSecond);
        double step = 1.0 / ProfileFramesPerSecond;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        _scene.MeasureGpu = true;
        var warmup = new ProfiledFrame[ProfileWarmupFrames];
        var gpu = new Dictionary<string, double>();
        MeasureFrames(host, clock, step, warmup, gpu);
        gpu.Clear();

        long firstTick = host.Time.Tick;
        var measured = new ProfiledFrame[frames];
        var sampler = CpuSampler.Start(Path.ChangeExtension(path, ".nettrace"));
        MeasureFrames(host, clock, step, measured, gpu);
        var stacks = sampler.Stop(nameof(MeasureFrames));
        _scene.MeasureGpu = false;

        foreach (string pass in gpu.Keys)
        {
            gpu[pass] /= frames;
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        var run = new ProfileRun(title, seed, firstTick, width, height, measured, gpu, stacks, _scene.CountModels(host.View, host.Content), _scene.DrawnTriangles);
        File.WriteAllText(path, ProfileReport.Write(run));
    }

    public void Dispose()
    {
        _cursor.Release(_glfw, _window);
        _scene.Dispose();
        _glfw.DestroyWindow(_window);
        _glfw.Terminate();
    }

    /// <summary>The profiler keeps only stacks that pass through here, so it mustn't be inlined.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MeasureFrames<TGame>(GameHost<TGame> host, FixedStepClock clock, double step, ProfiledFrame[] frames, Dictionary<string, double> gpu)
        where TGame : class, IGame
    {
        float time = (float)host.Time.TotalSeconds;
        for (int frame = 0; frame < frames.Length; frame++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int due = clock.Advance(step);
            for (int i = 0; i < due; i++)
            {
                host.Tick();
            }

            long ticked = Stopwatch.GetTimestamp();
            Draw(host, clock.Blend);
            long drawn = Stopwatch.GetTimestamp();
            Show(host, time + (float)(frame * step));
            long shown = Stopwatch.GetTimestamp();

            frames[frame] = new ProfiledFrame(
                due,
                Stopwatch.GetElapsedTime(start, ticked).TotalMilliseconds,
                Stopwatch.GetElapsedTime(ticked, drawn).TotalMilliseconds,
                Stopwatch.GetElapsedTime(drawn, shown).TotalMilliseconds,
                _scene.WaitMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
            foreach (ref readonly var pass in _scene.GpuTimes)
            {
                gpu[pass.Name] = gpu.GetValueOrDefault(pass.Name) + pass.Milliseconds;
            }
        }
    }

    private void PlayTo<TGame>(GameHost<TGame> host, long ticks)
        where TGame : class, IGame
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);

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
                ApplyGraphics(host.Graphics);
                _scene.Render(host.View, host.Content, width, height, (float)host.Time.TotalSeconds);
            }
        }
    }

    private void Settle<TGame>(GameHost<TGame> host, float time, int frames)
        where TGame : class, IGame
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);
        for (int i = 0; i < frames; i++)
        {
            ApplyGraphics(host.Graphics);
            _scene.Render(host.View, host.Content, width, height, time);
        }
    }

    /// <summary>
    /// Renders on until the light probes near the camera are all drawn, up to <see cref="ScreenshotProbeFrames"/>
    /// frames, so a screenshot shows the bounced light as it will be rather than still filling in. With the probes
    /// off there is nothing to wait for. Says how long it waited, as a slow scene can make a shot take a while.
    /// These frames don't wait for the display, as nobody sees them; the count of frames, not the time, decides
    /// what the shot shows, so it's the same either way.
    /// </summary>
    private void SettleProbes<TGame>(GameHost<TGame> host, float time)
        where TGame : class, IGame
    {
        if (!host.Graphics.LightProbes)
        {
            return;
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        int frames = ExtraSettleFrames(
            () => _scene.ProbesSettled,
            () =>
            {
                ApplyGraphics(host.Graphics, settlingProbes: true);
                _scene.Render(host.View, host.Content, width, height, time);
            },
            ScreenshotProbeFrames);

        if (frames == ScreenshotProbeFrames && !_scene.ProbesSettled)
        {
            Console.WriteLine($"The light probes were still filling in after {frames} more frames; the shot shows them as they are.");
        }
        else if (frames > 0)
        {
            Console.WriteLine($"Waited {frames} more frames for the light probes to settle.");
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
    private void Draw<TGame>(GameHost<TGame> host, float blend)
        where TGame : class, IGame
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);
        host.ScreenWidth = width;
        host.ScreenHeight = height;
        host.Draw(blend);
    }

    /// <summary>Puts the last Draw on screen, and hands it to the capture if that wants it.</summary>
    private void Show<TGame>(GameHost<TGame> host, float time)
        where TGame : class, IGame
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);

        // A minimized window has no pixels to read.
        if (!host.Capture.WantsFrame || width == 0 || height == 0)
        {
            ApplyGraphics(host.Graphics);
            _scene.Render(host.View, host.Content, width, height, time);
            return;
        }

        ApplyGraphics(host.Graphics);
        byte[] pixels = _scene.Capture(host.View, host.Content, width, height, time, out int capturedWidth, out int capturedHeight);
        host.Capture.Accept(pixels, capturedWidth, capturedHeight);
    }

    /// <summary>Hands the game's graphics settings, as they are now, to the renderer for the next frame.</summary>
    private void ApplyGraphics(Graphics graphics, bool settlingProbes = false)
    {
        (_scene.Off, _scene.Vsync, _scene.ShadowResolution, _scene.MaxShadowCascades, _scene.MeshDetail, _scene.RenderScale,
            _scene.Upscaler, _scene.ProbeQuality, _scene.ReflectionResolution, _scene.OcclusionResolution, _scene.DebugView) =
            RendererSettings(graphics, _hidden, settlingProbes, _commandLineDebugView, CanTrace);
    }

    /// <summary>
    /// What the renderer is told from the game's graphics settings: the effects left out, whether to wait
    /// for the display, the sun's shadow map size and cascades, the mesh detail, the render scale, the upscaler, the light
    /// probes' quality, the resolution of the reflections and the occlusion, and the debug view.
    /// A hidden window always waits for the display, except while a screenshot waits for the light probes to
    /// settle, where waiting would only slow the shot down. The debug view is the command line's when it named one,
    /// else the game's <see cref="Graphics.DebugView"/>, except that a game's Trace view is dropped to None on a
    /// GPU that can't trace (<paramref name="canTrace"/>), where the renderer would throw.
    /// </summary>
    internal static (RenderEffects Off, bool Vsync, int ShadowResolution, int ShadowCascades, float MeshDetail, float RenderScale,
        Upscaler Upscaler, ProbeQuality ProbeQuality, ReflectionResolution ReflectionResolution,
        OcclusionResolution OcclusionResolution, Rendition.DebugView DebugView) RendererSettings(
            Graphics graphics, bool hidden, bool settlingProbes = false, Rendition.DebugView commandLineDebugView = Rendition.DebugView.None,
            bool canTrace = true)
    {
        var debugView = commandLineDebugView;
        if (debugView == Rendition.DebugView.None && (graphics.DebugView != Rendition.DebugView.Trace || canTrace))
        {
            debugView = graphics.DebugView;
        }

        return (graphics.Off, hidden ? !settlingProbes : graphics.Vsync, graphics.ShadowResolution, graphics.ShadowCascades,
            graphics.MeshDetail, graphics.RenderScale, graphics.Upscaler, graphics.ProbeQuality, graphics.ReflectionResolution,
            graphics.OcclusionResolution, debugView);
    }

    /// <summary>Feeds the window's events into the game's input. Only the interactive loop does this.</summary>
    private void ListenForInput(Input input)
    {
        _onKey = (_, key, _, action, _) =>
        {
            if (action == InputAction.Repeat || !GlfwInput.TryMapKey((int)key, out var mapped))
            {
                return;
            }

            if (action == InputAction.Press)
            {
                input.KeyDown(mapped);
            }
            else
            {
                input.KeyUp(mapped);
            }
        };
        _onMouseButton = (_, button, action, _) =>
        {
            if ((int)button >= Input.MouseButtonCount)
            {
                return;
            }

            if (action == InputAction.Press)
            {
                input.MouseButtonDown((MouseButton)button);
            }
            else if (action == InputAction.Release)
            {
                input.MouseButtonUp((MouseButton)button);
            }
        };
        _onCursorPos = (_, x, y) =>
        {
            _glfw.GetWindowSize(_window, out int windowWidth, out int windowHeight);
            _glfw.GetFramebufferSize(_window, out int framebufferWidth, out int framebufferHeight);
            input.MouseMoved(GlfwInput.ToFramebuffer(x, y, windowWidth, windowHeight, framebufferWidth, framebufferHeight));
        };
        _onCursorEnter = (_, entered) =>
        {
            if (!entered)
            {
                input.MouseLeftWindow();
            }
        };
        _onScroll = (_, _, offsetY) => input.Scrolled((float)offsetY);
        _onChar = (_, codepoint) =>
        {
            foreach (char character in char.ConvertFromUtf32((int)codepoint))
            {
                input.TypedCharacter(character);
            }
        };

        _glfw.SetKeyCallback(_window, _onKey);
        _glfw.SetMouseButtonCallback(_window, _onMouseButton);
        _glfw.SetCursorPosCallback(_window, _onCursorPos);
        _glfw.SetCursorEnterCallback(_window, _onCursorEnter);
        _glfw.SetScrollCallback(_window, _onScroll);
        _glfw.SetCharCallback(_window, _onChar);

        // The cursor may already be over the window, and a first click shouldn't need a move.
        _glfw.GetCursorPos(_window, out double cursorX, out double cursorY);
        _glfw.GetWindowSize(_window, out int width, out int height);
        if (cursorX >= 0 && cursorY >= 0 && cursorX < width && cursorY < height)
        {
            _onCursorPos(_window, cursorX, cursorY);
        }
    }

    /// <summary>Reads the first gamepad, if one is plugged in, so a tick sees it as it is now.</summary>
    private void PollGamepad(Input input)
    {
        int slot = GlfwInput.FirstGamepad(_isGamepad);
        if (slot >= 0 && _glfw.GetGamepadState(slot, out GamepadState state))
        {
            for (int i = 0; i < Input.GamepadAxisCount; i++)
            {
                _gamepadAxes[i] = state.Axes[i];
            }

            for (int i = 0; i < Input.GamepadButtonCount; i++)
            {
                _gamepadButtons[i] = state.Buttons[i] != 0;
            }

            input.Gamepad(true, _gamepadAxes, _gamepadButtons);
        }
        else
        {
            input.Gamepad(false, default, default);
        }
    }

    private ulong CreateSurface(nint instance)
    {
        VkNonDispatchableHandle surface;
        int result = _glfw.CreateWindowSurface(new VkHandle(instance), _window, null, &surface);
        return result == 0
            ? surface.Handle
            : throw new InvalidOperationException($"Could not create a window surface: {result}");
    }

    private string[] GetInstanceExtensions()
    {
        byte** names = _glfw.GetRequiredInstanceExtensions(out uint count);
        var extensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        return extensions;
    }
}
