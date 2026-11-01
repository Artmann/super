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

    public GameHost(int seed, int ticksPerSecond, Action<IServiceCollection>? configure)
    {
        var time = new GameTime(ticksPerSecond);
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(new GameRandom(seed));
        services.AddSingleton<Content>();
        services.AddSingleton<Input>();
        services.AddSingleton(typeof(Actions<>));
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
        ((IGame)Game).Load();
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
        // Input is only readable during Update, so the screen can't depend on it.
        Input.Readable = true;
        Input.Roll();
        foreach (var hook in _hooks)
        {
            hook.BeforeUpdate();
        }

        Game.Update(Time);
        Input.Readable = false;
        Time.Tick++;
    }

    public void Draw(float blend)
    {
        View.Begin(blend);
        Game.Draw(View);

        // The interface runs outside the measurement, so a stats panel reports the game
        // and not itself.
        Stats.EndFrame();
        Ui.Update(((IGame)Game).Ui, ScreenWidth, ScreenHeight);
        View.Ui = Ui;
        Stats.Resume();
    }

    public void Dispose() => _services.Dispose();
}
