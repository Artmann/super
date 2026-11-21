using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>
/// The services a game runs on, as <see cref="GameHost{TGame}"/> registers them, and the throwaway folder that stands
/// in for the player's own when none is given.
/// </summary>
internal sealed class HostServices : IDisposable
{
    private const int HeadlessAudioSeed = 1;

    private readonly string? _throwawayFolder;

    private HostServices(ServiceProvider provider, GameTime time, string? throwawayFolder)
    {
        Provider = provider;
        Time = time;
        _throwawayFolder = throwawayFolder;
    }

    public ServiceProvider Provider { get; }

    /// <summary>The clock the game's ticks count on.</summary>
    public GameTime Time { get; }

    /// <param name="seed">Seeds <see cref="GameRandom"/>.</param>
    /// <param name="ticksPerSecond">How many ticks <see cref="GameTime"/> counts in a second.</param>
    /// <param name="configure">Registers the game's own services, last, so they can replace the ones here.</param>
    /// <param name="userDataFolder">
    /// Where <see cref="UserData"/> keeps its files. Left empty, it's a throwaway folder that's
    /// deleted afterwards, so a test or a screenshot never touches the player's own settings.
    /// </param>
    /// <param name="audioSeed">
    /// Seeds audio's own randomness, such as pitch variation and ambience timing. Null gives the fixed seed 1, so
    /// headless runs, screenshots and recordings sound the same every time.
    /// </param>
    /// <param name="graphics">How the game is drawn. Left empty, everything is on.</param>
    public static HostServices Build<TGame>(
        int seed, int ticksPerSecond, Action<IServiceCollection>? configure, string? userDataFolder = null, int? audioSeed = null,
        Graphics? graphics = null)
        where TGame : class, IGame
    {
        var time = new GameTime(ticksPerSecond);
        var services = new ServiceCollection();
        string? throwawayFolder = userDataFolder is null ? Path.Combine(Path.GetTempPath(), "super-user-data-" + Guid.NewGuid().ToString("N")) : null;
        services.AddSingleton(new UserData(userDataFolder ?? throwawayFolder!));
        services.AddSingleton(time);
        services.AddSingleton(new GameRandom(seed));
        services.AddSingleton<IContentFiles>(_ => CookedFolder.BesideProgram());
        services.AddSingleton<Content>();
        services.AddSingleton(provider => new Audio(provider.GetRequiredService<Content>(), audioSeed ?? HeadlessAudioSeed));
        services.AddSingleton(graphics ?? new Graphics());
        services.AddSingleton<Gpu>();
        services.AddSingleton<Input>();
        services.AddSingleton(typeof(Actions<>));
        services.AddSingleton<StateChanges>();
        services.AddSingleton(typeof(States<>));
        services.AddSingleton(provider => new GameCapture(provider.GetRequiredService<Audio>()));
        services.AddSingleton<GameStats>();
        services.AddSingleton<Transitions>();
        services.AddSingleton<ArtStyle>();
        services.AddSingleton<Director>();
        services.AddSingleton<World>();
        services.AddSingleton<Scenes>();
        services.AddSingleton<Systems>();
        services.AddSingleton<ITickHook>(provider => provider.GetRequiredService<World>());
        services.AddSingleton<IStateHook>(provider => provider.GetRequiredService<World>());
        services.AddSingleton<IDrawHook, SoundSync>();
        services.AddSingleton<IDrawHook>(provider => provider.GetRequiredService<ArtStyle>());
        services.AddSingleton<TGame>();

        // This comes last so a game or a test can replace the services above. Audio is the exception for a game: it
        // is sealed with an internal constructor, so only this factory builds it, reading sounds through whichever
        // Content ends up registered.
        configure?.Invoke(services);

        return new HostServices(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }), time, throwawayFolder);
    }

    /// <summary>Disposes the services, then deletes the throwaway folder, if there is one.</summary>
    public void Dispose()
    {
        Provider.Dispose();
        if (_throwawayFolder is not null && Directory.Exists(_throwawayFolder))
        {
            Directory.Delete(_throwawayFolder, recursive: true);
        }
    }
}
