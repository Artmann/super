using Microsoft.Extensions.DependencyInjection;

namespace Super.Testing;

public static class HeadlessGame
{
    /// <summary>Creates a game with no window and no GPU, at full speed.</summary>
    public static HeadlessRun<TGame> Create<TGame>(int seed, Action<IServiceCollection>? services = null)
        where TGame : class, IGame =>
        new(seed, services);
}

/// <summary>A game running under a test's control, through the same Update and Draw as the window.</summary>
public sealed class HeadlessRun<TGame> : IDisposable
    where TGame : class, IGame
{
    private const int DefaultTicksPerSecond = 30;

    private readonly GameHost<TGame> _host;
    private readonly List<(string Name, Func<TGame, bool> Holds)> _invariants = [];

    internal HeadlessRun(int seed, Action<IServiceCollection>? services)
    {
        _host = new GameHost<TGame>(seed, DefaultTicksPerSecond, services);
    }

    public TGame Game => _host.Game;

    public GameTime Time => _host.Time;

    public int Seed => _host.Seed;

    /// <summary>What the last Draw would have shown.</summary>
    public IReadOnlyList<ModelDraw> Drawn => _host.View.Drawn.ToArray();

    /// <summary>Adds a rule that's checked after every tick, so a problem is caught when it happens.</summary>
    public void Invariant(string name, Func<TGame, bool> holds) => _invariants.Add((name, holds));

    /// <summary>Plays this many ticks, with a Draw after each one.</summary>
    public void Ticks(long count)
    {
        for (long i = 0; i < count; i++)
        {
            _host.Tick();
            foreach (var (name, holds) in _invariants)
            {
                if (!holds(Game))
                {
                    throw new InvariantFailedException(name, Seed, _host.Time.Tick);
                }
            }

            _host.Draw(1f);
        }
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>An invariant stopped holding. The message says how to get back to that moment.</summary>
public sealed class InvariantFailedException(string name, int seed, long tick)
    : Exception($"Invariant failed: {name}\n  seed {seed}, tick {tick:N0}\n  replay: --seed {seed} --ticks {tick} --screenshot failure.png")
{
    public string Invariant { get; } = name;

    public int Seed { get; } = seed;

    public long Tick { get; } = tick;
}
