using System.Diagnostics;
using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

namespace Super;

/// <summary>The real window: GLFW for the operating system, and Rendition for the picture.</summary>
internal sealed unsafe class GameWindow : IDisposable
{
    /// <summary>Anti-aliasing builds up over several frames, so a screenshot waits for it to settle.</summary>
    private const int ScreenshotSettleFrames = 48;

    private const int ScreenshotPlayedTicks = 120;

    private const int RecordingSettleFrames = 16;

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly SceneRenderer _scene;

    public GameWindow(string title, int windowWidth, int windowHeight, bool hidden)
    {
        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
        {
            throw new InvalidOperationException("Could not initialize GLFW.");
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        _glfw.WindowHint(WindowHintBool.Visible, !hidden);

        // Windows shrinks a window whose title bar would push it past the screen's height,
        // and a hidden window should come out at exactly the size that was asked for.
        _glfw.WindowHint(WindowHintBool.Decorated, !hidden);
        _window = _glfw.CreateWindow(windowWidth, windowHeight, title, null, null);
        if (_window is null)
        {
            _glfw.Terminate();
            throw new InvalidOperationException("Could not create a window.");
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        _scene = new SceneRenderer(new RendererOptions
        {
            InstanceExtensions = GetInstanceExtensions(),
            CreateSurface = CreateSurface,
            Width = width,
            Height = height,
            EnableValidation = hidden,
        });
    }

    /// <summary>The game loop: runs whatever ticks are due, then draws one frame.</summary>
    public void Run<TGame>(GameHost<TGame> host, int ticksPerSecond)
        where TGame : class, IGame
    {
        var clock = new FixedStepClock(ticksPerSecond);
        var realTime = Stopwatch.StartNew();
        double last = 0;
        double played = 0;

        while (!_glfw.WindowShouldClose(_window))
        {
            _glfw.PollEvents();

            double now = realTime.Elapsed.TotalSeconds;
            double step = host.Capture.IsRecording ? 1.0 / host.Capture.FramesPerSecond : now - last;
            last = now;
            played += step;

            int ticks = clock.Advance(step);
            for (int i = 0; i < ticks; i++)
            {
                host.Tick();
            }

            host.Draw(clock.Blend);
            Show(host, (float)played);
        }
    }

    /// <summary>Plays the given number of ticks and saves the picture. Returns the validation message count.</summary>
    public int Screenshot<TGame>(GameHost<TGame> host, long ticks, string path)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);
        host.Draw(1f);
        float time = (float)host.Time.TotalSeconds;
        Settle(host, time, ScreenshotSettleFrames);

        host.Capture.Screenshot(path);
        Show(host, time);
        return _scene.ValidationErrors;
    }

    /// <summary>Plays the given number of ticks, then saves a video. Returns the validation message count.</summary>
    public int Record<TGame>(GameHost<TGame> host, long ticks, string path, int frames, int framesPerSecond, int ticksPerSecond)
        where TGame : class, IGame
    {
        PlayTo(host, ticks);

        // Blending from 0 means the first frame of the video carries straight on from here.
        host.Draw(0f);
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

            host.Draw(clock.Blend);
            Show(host, start + (float)(frame * step));
        }

        host.Capture.StopRecording();
        return _scene.ValidationErrors;
    }

    public void Dispose()
    {
        _scene.Dispose();
        _glfw.DestroyWindow(_window);
        _glfw.Terminate();
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
            if (i >= ticks - ScreenshotPlayedTicks)
            {
                host.Draw(1f);
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
            _scene.Render(host.View, host.Content, width, height, time);
        }
    }

    /// <summary>Puts the last Draw on screen, and hands it to the capture if that wants it.</summary>
    private void Show<TGame>(GameHost<TGame> host, float time)
        where TGame : class, IGame
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);

        // A minimized window has no pixels to read.
        if (!host.Capture.WantsFrame || width == 0 || height == 0)
        {
            _scene.Render(host.View, host.Content, width, height, time);
            return;
        }

        byte[] pixels = _scene.Capture(host.View, host.Content, width, height, time, out int capturedWidth, out int capturedHeight);
        host.Capture.Accept(pixels, capturedWidth, capturedHeight);
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
