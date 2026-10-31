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

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly SceneRenderer _scene;

    public GameWindow(GameOptions options, bool hidden)
    {
        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
        {
            throw new InvalidOperationException("Could not initialize GLFW.");
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        _glfw.WindowHint(WindowHintBool.Visible, !hidden);
        _window = _glfw.CreateWindow(options.Width, options.Height, options.Title, null, null);
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

        while (!_glfw.WindowShouldClose(_window))
        {
            _glfw.PollEvents();

            double now = realTime.Elapsed.TotalSeconds;
            int ticks = clock.Advance(now - last);
            last = now;
            for (int i = 0; i < ticks; i++)
            {
                host.Tick();
            }

            host.Draw(clock.Blend);
            _glfw.GetFramebufferSize(_window, out int width, out int height);
            _scene.Render(host.View, width, height, (float)now);
        }
    }

    /// <summary>Plays the given number of ticks and saves the picture. Returns the validation message count.</summary>
    public int Screenshot<TGame>(GameHost<TGame> host, long ticks, string path)
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
                _scene.Render(host.View, width, height, (float)host.Time.TotalSeconds);
            }
        }

        float time = (float)host.Time.TotalSeconds;
        host.Draw(1f);
        for (int i = 0; i < ScreenshotSettleFrames; i++)
        {
            _scene.Render(host.View, width, height, time);
        }

        byte[] pixels = _scene.Capture(host.View, width, height, time, out int capturedWidth, out int capturedHeight);
        PngFile.Write(path, pixels, capturedWidth, capturedHeight);
        return _scene.ValidationErrors;
    }

    public void Dispose()
    {
        _scene.Dispose();
        _glfw.DestroyWindow(_window);
        _glfw.Terminate();
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
