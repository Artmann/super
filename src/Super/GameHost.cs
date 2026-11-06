using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ui;

namespace Super;

/// <summary>
/// Runs a game's Update and Draw, with or without a window. The window and headless
/// runs both sit on this, so they can't drift apart.
/// </summary>
internal sealed class GameHost<TGame> : IDisposable
    where TGame : class, IGame
{
    private const int DefaultScreenWidth = 1280;
    private const int DefaultScreenHeight = 720;
    private const int HeadlessAudioSeed = 1;

    private readonly ServiceProvider _services;
    private readonly ITickHook[] _hooks;
    private readonly IDrawHook[] _drawHooks;
    private readonly StateChanges _changes;
    private readonly UiPointer _pointer;

    private readonly string? _throwawayFolder;

    private long _scriptStart;
    private int _codeChangesSeen = HotReload.Changes;
    private bool _codeChanged;
    private bool _drawStopped;

    /// <summary>When set, this is the player. Each tick plays what's due on it, counting from the tick it was set.</summary>
    public InputScript? Script
    {
        get;
        set
        {
            field = value;
            _scriptStart = Time.Tick;
        }
    }

    /// <param name="userDataFolder">
    /// Where <see cref="UserData"/> keeps its files. Left empty, it's a throwaway folder that's
    /// deleted afterwards, so a test or a screenshot never touches the player's own settings.
    /// </param>
    /// <param name="audioSeed">
    /// Seeds audio's own randomness, such as pitch variation and ambience timing. Null gives the fixed seed 1, so
    /// headless runs, screenshots and recordings sound the same every time.
    /// </param>
    /// <param name="graphics">How the game is drawn. Left empty, everything is on.</param>
    public GameHost(int seed, int ticksPerSecond, Action<IServiceCollection>? configure, string? userDataFolder = null, int? audioSeed = null, Graphics? graphics = null)
    {
        var time = new GameTime(ticksPerSecond);
        var services = new ServiceCollection();
        _throwawayFolder = userDataFolder is null ? Path.Combine(Path.GetTempPath(), "super-user-data-" + Guid.NewGuid().ToString("N")) : null;
        services.AddSingleton(new UserData(userDataFolder ?? _throwawayFolder!));
        services.AddSingleton(time);
        services.AddSingleton(new GameRandom(seed));
        services.AddSingleton<IContentFiles>(_ => CookedFolder.BesideProgram());
        services.AddSingleton<Content>();
        services.AddSingleton(provider => new Audio(provider.GetRequiredService<Content>(), audioSeed ?? HeadlessAudioSeed));
        services.AddSingleton(graphics ?? new Graphics());
        services.AddSingleton<Input>();
        services.AddSingleton(typeof(Actions<>));
        services.AddSingleton<StateChanges>();
        services.AddSingleton(typeof(States<>));
        services.AddSingleton(provider => new GameCapture(provider.GetRequiredService<Audio>()));
        services.AddSingleton<GameStats>();
        services.AddSingleton<Transitions>();
        services.AddSingleton<Director>();
        services.AddSingleton<TGame>();

        // This comes last so a game or a test can replace the services above. Audio is the exception for a game: it
        // is sealed with an internal constructor, so only this factory builds it, reading sounds through whichever
        // Content ends up registered.
        configure?.Invoke(services);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Time = time;
        Seed = seed;
        Content = _services.GetRequiredService<Content>();
        View.Content = Content;
        Input = _services.GetRequiredService<Input>();
        Capture = _services.GetRequiredService<GameCapture>();
        Stats = _services.GetRequiredService<GameStats>();
        Director = _services.GetRequiredService<Director>();
        Transitions = _services.GetRequiredService<Transitions>();
        Audio = _services.GetRequiredService<Audio>();
        Graphics = _services.GetRequiredService<Graphics>();
        Game = _services.GetRequiredService<TGame>();
        _hooks = _services.GetServices<ITickHook>().ToArray();
        _drawHooks = _services.GetServices<IDrawHook>().ToArray();
        _changes = _services.GetRequiredService<StateChanges>();
        _changes.Hooks = _services.GetServices<IStateHook>().ToArray();
        Input.RoutesMouse = true;
        _pointer = new UiPointer(Ui, Input, ticksPerSecond);
        ((IGame)Game).Load();
        _changes.Apply();
        Content.RequireReferences();
    }

    public TGame Game { get; }

    public GameTime Time { get; }

    public Content Content { get; }

    public Input Input { get; }

    public GameCapture Capture { get; }

    public int Seed { get; }

    public GameStats Stats { get; }

    public Director Director { get; }

    public Transitions Transitions { get; }

    public Audio Audio { get; }

    /// <summary>How the game is drawn. Changes show from the next frame.</summary>
    public Graphics Graphics { get; }

    public View View { get; } = new();

    public UiTree Ui { get; } = new();

    /// <summary>The size the interface lays itself out in, in pixels. The window keeps this up to date.</summary>
    public int ScreenWidth { get; set; } = DefaultScreenWidth;

    public int ScreenHeight { get; set; } = DefaultScreenHeight;

    /// <summary>Looks a service up for the test harness. Games get theirs injected instead.</summary>
    internal object GetService(Type type) => _services.GetRequiredService(type);

    /// <summary>
    /// With hot reload on, an exception in the game's code stops the game instead of closing
    /// it, so the next saved fix carries on from where it was. Only live play turns this on.
    /// </summary>
    public bool KeepsRunningAfterErrors { get; init; }

    /// <summary>Where a stop is reported.</summary>
    public TextWriter Errors { get; init; } = Console.Error;

    /// <summary>What stopped the game, until a change to the code or content lets it try again.</summary>
    public Exception? Stopped { get; private set; }

    /// <summary>Acts as if hot reload changed the code. It lands at the start of the next tick.</summary>
    internal void CodeChanged() => _codeChanged = true;

    private bool CodeChangeDue => _codeChanged || HotReload.Changes != _codeChangesSeen;

    public void Tick()
    {
        if (!KeepsRunningAfterErrors)
        {
            TickOnce();
            return;
        }

        if (Stopped is not null)
        {
            if (!CodeChangeDue && !Content.ReloadWanted)
            {
                return;
            }

            Stopped = null;
            _drawStopped = false;
        }

        try
        {
            TickOnce();
        }
        catch (Exception error)
        {
            Stop(error);
        }
    }

    private void TickOnce()
    {
        Script?.Play(Time.Tick - _scriptStart, Input, Ui);

        // Only here, so all of a tick and the Draw after it see one version of the data.
        if (Content.ReloadWanted)
        {
            Content.Reload(Console.Error);
        }

        // At the start of a tick, on the game's thread, however the runtime delivered the change.
        if (CodeChangeDue)
        {
            _codeChanged = false;
            _codeChangesSeen = HotReload.Changes;
            ((IGame)Game).CodeReloaded();
        }

        _pointer.BeforeRoll();

        // Input is only readable during Update, so the screen can't depend on it.
        Input.ScreenSize = new Vector2(ScreenWidth, ScreenHeight);
        Input.Readable = true;
        Input.Roll();
        foreach (var hook in _hooks)
        {
            hook.BeforeUpdate();
        }

        _pointer.Deliver();

        // Before Update, so a fade's change lands while the screen is all colour, and the
        // game sees it and a cutscene's moments and time scale this tick.
        Transitions.BeforeUpdate();
        Director.BeforeUpdate();

        // Before Update, so this tick's audio is what was asked for up to the last frame. A sound started in this
        // Update is handed over at this tick's Draw and mixed from the next tick.
        if (Audio.Manual)
        {
            Audio.Advance(Time.TickSeconds);
        }

        Game.Update(Time);
        Input.Readable = false;

        // After Update, so what the game set this tick plays, and Crossed sees each moment once.
        Content.Animators.Advance(Time.DeltaSeconds);

        // Once everything has moved, so a camera following something sees where it is now.
        Director.AfterUpdate();

        // Last, so all of Update saw one state and the next Draw shows the new one.
        _changes.Apply();
        Content.RequireReferences();
        Time.Tick++;
    }

    public void Draw(float blend)
    {
        if (!KeepsRunningAfterErrors)
        {
            DrawOnce(blend);
            return;
        }

        // Once Draw or Ui has thrown, it isn't called again until a change, so the error is reported once.
        if (!_drawStopped)
        {
            try
            {
                DrawOnce(blend);
                return;
            }
            catch (Exception error)
            {
                _drawStopped = true;
                Stop(error);
            }
        }

        DrawStopped(blend);
    }

    private void DrawOnce(float blend)
    {
        _changes.Open = false;
        View.Begin(blend, Time.Tick, Time.TickSeconds, Time.Scale);
        Game.Draw(View);
        if (Director.HasCamera)
        {
            View.Camera(Director.At(blend));
        }

        // After the camera is placed, since a blend starts from the last one shown.
        Transitions.Apply(View);

        foreach (var hook in _drawHooks)
        {
            hook.AfterDraw(View);
        }

        // Last of the frame's own work, so the listener is the camera the frame showed and the hooks' changes go too.
        Audio.EndFrame(View.Shot, Time.Scale);

        // The interface runs outside the measurement, so a stats panel reports the game
        // and not itself.
        Stats.EndFrame();
        _pointer.Pump();
        Ui.Update(Interface(), ScreenWidth, ScreenHeight);
        if (_pointer.Refresh())
        {
            Ui.Update(Interface(), ScreenWidth, ScreenHeight);
        }

        _pointer.ShowOverlays();
        View.Ui = Ui;
        Stats.Resume();
        _changes.Open = true;
    }

    /// <summary>While stopped by Update, the world is still drawn, with the error in place of the game's interface.</summary>
    private UiView? Interface() => Stopped is { } error ? StoppedPanel.For(error) : ((IGame)Game).Ui;

    /// <summary>A frame without any of the game's code, for when Draw or Ui is what broke.</summary>
    private void DrawStopped(float blend)
    {
        View.Begin(blend, Time.Tick, Time.TickSeconds, Time.Scale);
        Audio.EndFrame(View.Shot, Time.Scale);
        Ui.Update(StoppedPanel.For(Stopped!), ScreenWidth, ScreenHeight);
        View.Ui = Ui;
        _changes.Open = true;
    }

    private void Stop(Exception error)
    {
        // Update may have thrown part way, with input still readable. A Draw that threw leaves state changes
        // closed, which DrawStopped opens again.
        Input.Readable = false;
        if (Stopped is not null)
        {
            return;
        }

        Stopped = error;
        Errors.WriteLine($"{StoppedPanel.Title}: {error}");
        Errors.WriteLine(StoppedPanel.CarryOn);
    }

    public void Dispose()
    {
        Content.StopLive();

        // Before the services go, so the audio thread is stopped while everything it reads is still there.
        try
        {
            Audio.Close();
        }
        finally
        {
            _services.Dispose();
            if (_throwawayFolder is not null && Directory.Exists(_throwawayFolder))
            {
                Directory.Delete(_throwawayFolder, recursive: true);
            }
        }
    }
}
