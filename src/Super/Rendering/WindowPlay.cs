using System.Diagnostics;
using Rendition;

namespace Super;

/// <summary>
/// Live play in a window: the game loop, with the player's input, missing lighting baked in the background, and shaders
/// swapped in as they are saved. Screenshots, recordings, profiles and bakes never get here.
/// </summary>
internal sealed class WindowPlay(OsWindow window, SceneRenderer scene, WindowFrames frames, WindowCursor cursor, bool hidden)
{
    /// <summary>How long a bake that arrives on screen fades in during play.</summary>
    internal const float PlayBakeFadeSeconds = 0.5f;

    private readonly AutoBakeSwitch _autoBake = new();
    private readonly GamepadPoll _gamepad = new(window.GlfwApi);

    // Holds the window's callbacks, which GLFW only points at, for as long as the game plays.
    private WindowInputFeed? _feed;
    private double _last;
    private double _played;

    /// <summary>The game loop: runs whatever ticks are due, then draws one frame, until the window closes.</summary>
    public void Run(GameLoop host)
    {
        var clock = new FixedStepClock(host.Time.TicksPerSecond);
        var realTime = Stopwatch.StartNew();
        Start(host);

        var limiter = new FrameLimiter(TimeProvider.System);
        IDisposable? fineSleep = null;

        // From source, a saved shader is compiled and swapped in while the game runs. A shipped game has no sources.
        using var shaders = hidden ? null : ShaderWatch.Start(ShaderWatch.DefaultFolder, Console.WriteLine);
        try
        {
            while (!window.ShouldClose)
            {
                ReloadSavedShaders(shaders);
                PlayFrame(host, clock, realTime);

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

    /// <summary>Listens to the player, unless a script plays, and turns on what only play does.</summary>
    private void Start(GameLoop host)
    {
        if (host.Script is null)
        {
            _feed = new WindowInputFeed(window, host.Input);
            _feed.Listen();
        }

        // Only here can a player see a loading screen, and only here may real time decide its length.
        host.Content.Store.Loading.Streaming = true;
        host.Content.Data.Live.GoLive();

        // Only here is missing lighting baked as it appears, and only here does a bake that arrives fade in.
        scene.Renderer.BakeFadeSeconds = PlayBakeFadeSeconds;
        _autoBake.TurnOn();
        host.ShowsBakes = true;
        ApplyAutoBake(host);
    }

    private void PlayFrame(GameLoop host, FixedStepClock clock, Stopwatch realTime)
    {
        window.PollEvents();
        if (host.Script is null)
        {
            _gamepad.Poll(host.Input);
        }

        double now = realTime.Elapsed.TotalSeconds;
        double step = host.Capture.IsRecording ? 1.0 / host.Capture.FramesPerSecond : now - _last;
        _last = now;
        _played += step;

        int ticks = clock.Advance(step);
        for (int i = 0; i < ticks; i++)
        {
            host.Tick();
        }

        unsafe
        {
            cursor.Apply(window.GlfwApi, window.NativeWindow, host.Input, host.Ui.Scale);
        }

        host.BakeProgress = scene.Renderer.BakeProgress;
        frames.Draw(host, clock.Blend);
        ApplyAutoBake(host);
        frames.Show(host, (float)_played);
    }

    /// <summary>
    /// Swaps in the shaders that were saved and compiled since the last frame. A shader that needs something the
    /// running game didn't set up, such as a new texture, can fail here; the game then keeps the old one.
    /// </summary>
    private void ReloadSavedShaders(ShaderWatch? shaders)
    {
        if (shaders?.TakeChanged() is not { Count: > 0 } changed)
        {
            return;
        }

        try
        {
            scene.Renderer.ReloadShaders(changed);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not swap in {string.Join(", ", changed.Order())}, so the game keeps the old one: {e.Message}");
        }
    }

    private static DevSettings? DevOf(GameLoop host) =>
        host.Content.Store.Files is CookedFolder { Dev: { Roots.Length: > 0 } dev } ? dev : null;

    /// <summary>
    /// Hands the renderer what to bake by itself, from <see cref="Graphics.BakeOnArrival"/> and
    /// <see cref="Graphics.BakeBudget"/>, the first time and whenever either changes. Unchanged, the renderer keeps the
    /// same <see cref="Rendition.AutoBake"/>, since a new one would start its queue again. Called between frames, as
    /// setting it waits for the GPU the first time.
    /// </summary>
    private void ApplyAutoBake(GameLoop host)
    {
        if (!_autoBake.Changed(host.Graphics))
        {
            return;
        }

        var dev = DevOf(host);
        scene.Baking.BakeOnArrival(AutoBakePolicy.For(dev, scene.Renderer.CanTrace, host.Graphics));

        // From source, a GPU that can't trace says once why the lighting stays live; a player isn't told.
        scene.Baking.SaysWhyNotBaking = host.Graphics.BakeOnArrival && !scene.Renderer.CanTrace && dev is not null;
    }
}
