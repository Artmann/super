using Rendition;

namespace Super;

/// <summary>
/// The real window: GLFW for the operating system (<see cref="OsWindow"/>), and Rendition for the picture. Plays a game
/// in it live (<see cref="WindowPlay"/>), or for a screenshot or a video (<see cref="WindowFrames"/>), a profile
/// (<see cref="WindowProfiler"/>) or a bake (<see cref="WindowBakes"/>).
/// </summary>
internal sealed class GameWindow : IDisposable
{
    private readonly OsWindow _window;
    private readonly SceneRenderer _scene;
    private readonly WindowFrames _frames;
    private readonly WindowCursor _cursor = new();
    private readonly bool _hidden;

    /// <summary>
    /// Opens the window and the renderer, which starts with <paramref name="graphics"/>' vsync, shadow resolution,
    /// render scale and upscaler. Without a window, recordings and profiles wait for the display whatever
    /// <see cref="GraphicsScreen.Vsync"/> says, and screenshots don't.
    /// </summary>
    public GameWindow(WindowSetup setup, Graphics graphics)
    {
        bool hidden = setup.Hidden;
        _window = new OsWindow(setup);
        var (width, height) = _window.FramebufferSize();
        _scene = new SceneRenderer(new RendererOptions
        {
            Surface = RendererSurface.Window(_window.Handle),
            Width = width,
            Height = height,
            EnableValidation = setup.Validate,
            DisableVsync = !graphics.Screen.Vsync && !hidden,
            DisableRayTracing = setup.NoRayTracing,
            ShadowResolution = graphics.Shadowing.ShadowResolution,
            RenderScale = graphics.Screen.RenderScale,
            Upscaler = graphics.Screen.Upscaler,
            // A window someone looks at, run from source, swaps in shaders as they are saved; see WindowPlay.
            LiveShaders = !hidden && ShaderWatch.CanWatch(ShaderWatch.DefaultFolder),
        });
        _frames = new WindowFrames(_window, _scene, hidden);
        _hidden = hidden;
    }

    /// <summary>
    /// The chunk bakes made on this machine, which play reads and writes. Set before <see cref="Run"/>; other runs read
    /// only cooked bakes.
    /// </summary>
    internal BakeCache? BakeCache
    {
        set => _scene.Baking.BakeCache = value;
    }

    /// <summary>
    /// What every frame shows when <c>--debug-view</c> asked for a view, or <see cref="Rendition.DebugView.None"/>
    /// when it didn't, and then the game's <see cref="Graphics.DebugView"/> decides. See <see cref="SceneRenderer.DebugView"/>.
    /// </summary>
    public Rendition.DebugView DebugView
    {
        get => _frames.CommandLineDebugView;
        set
        {
            _frames.CommandLineDebugView = value;
            _scene.DebugView = value;
        }
    }

    /// <summary>Whether the GPU can trace rays.</summary>
    public bool CanTrace => _scene.Renderer.Gpu.CanTrace;

    /// <summary>Why <see cref="CanTrace"/> is false, or null when it is true.</summary>
    public string? NoTraceReason => _scene.Renderer.NoTraceReason;

    /// <summary>The GPU the game is drawn with.</summary>
    public GpuInfo Gpu => _scene.Renderer.Gpu;

    /// <summary>
    /// How many pixels the window has for each unit of its size: 2 on a Mac's Retina screen, where the window is sized
    /// in points, and 1 elsewhere, and in a hidden window, which asks for no Retina pixels.
    /// </summary>
    public float PixelsPerPoint => _window.PixelsPerPoint;

    /// <summary>Plays the game live until the window closes.</summary>
    public void Run(GameLoop host) => new WindowPlay(_window, _scene, _frames, _cursor, _hidden).Run(host);

    /// <summary>Plays the given number of ticks and saves the picture. Returns the validation message count.</summary>
    public int Screenshot(GameLoop host, long ticks, string path, bool neutralLut = false) =>
        _frames.Screenshot(host, ticks, path, neutralLut);

    /// <summary>
    /// Plays the given number of ticks, then saves a video of <paramref name="frames"/> frames at
    /// <paramref name="framesPerSecond"/>. Returns the validation message count.
    /// </summary>
    public int Record(GameLoop host, long ticks, string path, int frames, int framesPerSecond) =>
        _frames.Record(host, ticks, path, frames, framesPerSecond);

    /// <summary>Plays the asked ticks, then measures the asked frames and writes a report.</summary>
    public void Profile(GameLoop host, ProfileRequest request) => new WindowProfiler(_window, _scene, _frames).Profile(host, request);

    /// <summary>See <see cref="WindowBakes.Bake"/>.</summary>
    public int Bake(GameLoop host, long ticks, BakeQuality quality, string folder, out int failed) =>
        new WindowBakes(_window, _scene, _frames).Bake(host, ticks, quality, folder, out failed);

    /// <summary>See <see cref="WindowBakes.BakeCheck"/>.</summary>
    public int BakeCheck(GameLoop host, long ticks) => new WindowBakes(_window, _scene, _frames).BakeCheck(host, ticks);

    public void Dispose()
    {
        unsafe
        {
            _cursor.Release(_window.GlfwApi, _window.NativeWindow);
        }

        _scene.Dispose();
        _window.Dispose();
    }
}
