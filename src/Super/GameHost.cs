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

    private readonly ServiceProvider _services;
    private readonly ITickHook[] _hooks;
    private readonly StateChanges _changes;

    public GameHost(int seed, int ticksPerSecond, Action<IServiceCollection>? configure)
    {
        var time = new GameTime(ticksPerSecond);
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(new GameRandom(seed));
        services.AddSingleton<IContentFiles, ContentFolder>();
        services.AddSingleton<Content>();
        services.AddSingleton<Input>();
        services.AddSingleton(typeof(Actions<>));
        services.AddSingleton<StateChanges>();
        services.AddSingleton(typeof(States<>));
        services.AddSingleton<GameCapture>();
        services.AddSingleton<GameStats>();
        services.AddSingleton<TGame>();

        // This comes last so a game or a test can replace any of the above.
        configure?.Invoke(services);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Time = time;
        Seed = seed;
        Content = _services.GetRequiredService<Content>();
        Input = _services.GetRequiredService<Input>();
        Capture = _services.GetRequiredService<GameCapture>();
        Stats = _services.GetRequiredService<GameStats>();
        Game = _services.GetRequiredService<TGame>();
        _hooks = _services.GetServices<ITickHook>().ToArray();
        _changes = _services.GetRequiredService<StateChanges>();
        _changes.Hooks = _services.GetServices<IStateHook>().ToArray();
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

    public View View { get; } = new();

    public UiTree Ui { get; } = new();

    /// <summary>The size the interface lays itself out in, in pixels. The window keeps this up to date.</summary>
    public int ScreenWidth { get; set; } = DefaultScreenWidth;

    public int ScreenHeight { get; set; } = DefaultScreenHeight;

    /// <summary>Looks a service up for the test harness. Games get theirs injected instead.</summary>
    internal object GetService(Type type) => _services.GetRequiredService(type);

    public void Tick()
    {
        // Only here, so all of a tick and the Draw after it see one version of the data.
        if (Content.ReloadWanted)
        {
            Content.Reload(Console.Error);
        }

        // Input is only readable during Update, so the screen can't depend on it.
        Input.ScreenSize = new Vector2(ScreenWidth, ScreenHeight);
        Input.Readable = true;
        Input.Roll();
        foreach (var hook in _hooks)
        {
            hook.BeforeUpdate();
        }

        Game.Update(Time);
        Input.Readable = false;

        // Last, so all of Update saw one state and the next Draw shows the new one.
        _changes.Apply();
        Content.RequireReferences();
        Time.Tick++;
    }

    public void Draw(float blend)
    {
        _changes.Open = false;
        View.Begin(blend, Time.Tick, Time.TickSeconds);
        Game.Draw(View);

        // The interface runs outside the measurement, so a stats panel reports the game
        // and not itself.
        Stats.EndFrame();
        Ui.Update(((IGame)Game).Ui, ScreenWidth, ScreenHeight);
        View.Ui = Ui;
        Stats.Resume();
        _changes.Open = true;
    }

    public void Dispose()
    {
        Content.StopLive();
        _services.Dispose();
    }
}
