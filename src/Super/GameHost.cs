using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>
/// Builds a game's services and runs it on them, with or without a window. <see cref="GameLoop"/> runs its Update and
/// Draw, so the window and headless runs can't drift apart.
/// </summary>
internal sealed class GameHost<TGame> : GameLoop, IDisposable
    where TGame : class, IGame
{
    private readonly HostServices _services;

    /// <param name="seed">Seeds <see cref="GameRandom"/>.</param>
    /// <param name="ticksPerSecond">How many ticks the game plays in a second.</param>
    /// <param name="configure">Registers the game's own services, after the engine's, so they can replace them.</param>
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
        : this(seed, HostServices.Build<TGame>(seed, ticksPerSecond, configure, userDataFolder, audioSeed, graphics))
    {
    }

    private GameHost(int seed, HostServices services)
        : base(services.Provider, services.Time, provider => provider.GetRequiredService<TGame>())
    {
        _services = services;
        Seed = seed;
        Game = (TGame)LoopedGame;
        Gpu = services.Provider.GetRequiredService<Gpu>();
    }

    public TGame Game { get; }

    public int Seed { get; }

    /// <summary>The GPU the game is drawn with, filled in by the window once it has made its device.</summary>
    public Gpu Gpu { get; }

    /// <summary>Looks a service up for the test harness. Games get theirs injected instead.</summary>
    internal object GetService(Type type) => _services.Provider.GetRequiredService(type);

    /// <summary>
    /// With hot reload on, an exception in the game's code stops the game instead of closing
    /// it, so the next saved fix carries on from where it was. Only live play turns this on.
    /// </summary>
    public bool KeepsRunningAfterErrors
    {
        get => Halts.KeepsRunning;
        init => Halts.KeepsRunning = value;
    }

    /// <summary>Where a stop is reported.</summary>
    public TextWriter Errors
    {
        get => Halts.Errors;
        init => Halts.Errors = value;
    }

    /// <summary>What stopped the game, until a change to the code or content lets it try again.</summary>
    public Exception? Stopped => Halts.Stopped;

    /// <summary>Acts as if hot reload changed the code. It lands at the start of the next tick.</summary>
    internal void CodeChanged() => Halts.CodeChanged();

    public void Dispose()
    {
        Content.StopLive();

        // Before the services go, so the audio thread is stopped while everything it reads is still there.
        try
        {
            Audio.Device.Close();
        }
        finally
        {
            _services.Dispose();
        }
    }
}
