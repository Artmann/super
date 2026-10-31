using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>
/// Runs a game's Update and Draw, with or without a window. The window and headless
/// runs both sit on this, so they can't drift apart.
/// </summary>
internal sealed class GameHost<TGame> : IDisposable
    where TGame : class, IGame
{
    private readonly ServiceProvider _services;

    public GameHost(int seed, int ticksPerSecond, Action<IServiceCollection>? configure)
    {
        var time = new GameTime(ticksPerSecond);
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(new GameRandom(seed));
        services.AddSingleton<Content>();
        services.AddSingleton<TGame>();

        // This comes last so a game or a test can replace any of the above.
        configure?.Invoke(services);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Time = time;
        Seed = seed;
        Content = _services.GetRequiredService<Content>();
        Game = _services.GetRequiredService<TGame>();
        ((IGame)Game).Load();
    }

    public TGame Game { get; }

    public GameTime Time { get; }

    public Content Content { get; }

    public int Seed { get; }

    public View View { get; } = new();

    public void Tick()
    {
        Game.Update(Time);
        Time.Tick++;
    }

    public void Draw(float blend)
    {
        View.Begin(blend);
        Game.Draw(View);
    }

    public void Dispose() => _services.Dispose();
}
