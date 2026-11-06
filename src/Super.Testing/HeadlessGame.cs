using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ui;

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
        Input = new ScriptedInput(_host.Input, _host.GetService, _host.Ui);

        // So there is something to look at before the first tick, as there is in the window.
        _host.Draw(1f);
    }

    public TGame Game => _host.Game;

    public GameTime Time => _host.Time;

    public int Seed => _host.Seed;

    /// <summary>The size the interface lays itself out in, in pixels.</summary>
    public Vector2 ScreenSize => new(_host.ScreenWidth, _host.ScreenHeight);

    /// <summary>How many pixels one unit of the interface covers, as <c>--ui-scale</c> sets it. See <see cref="UiTree.Scale"/>.</summary>
    public float UiScale
    {
        get => _host.Ui.Scale;
        set => _host.Ui.Scale = value;
    }

    /// <summary>Key presses, clicks and actions to play. They land on the next tick.</summary>
    public ScriptedInput Input { get; }

    /// <summary>The game's states, to check where it is or to jump there. A jump lands at the end of the next tick.</summary>
    public States<TState> States<TState>()
        where TState : struct, Enum =>
        (States<TState>)_host.GetService(typeof(States<TState>));

    /// <summary>Keeps <see cref="Content.IsLoaded"/> false, so a test can look at a loading screen.</summary>
    public void HoldLoading() => _host.Content.Held = true;

    /// <summary>Lets a held load finish, as if everything arrived at once.</summary>
    public void FinishLoading() => _host.Content.Held = false;

    /// <summary>Acts as if a content file was saved. It lands on the next tick, as it does in the window.</summary>
    public void ContentChanged() => _host.Content.ReloadWanted = true;

    /// <summary>
    /// Acts as if hot reload changed the game's code, so <see cref="IGame.CodeReloaded"/> runs
    /// at the start of the next tick, as it does in the window.
    /// </summary>
    public void CodeChanged() => _host.CodeChanged();

    /// <summary>What the last Draw would have shown.</summary>
    public IReadOnlyList<ModelDraw> Drawn => _host.View.Drawn.ToArray();

    /// <summary>The camera the last frame showed. Call <see cref="Frame"/> first.</summary>
    public Camera Camera => _host.View.Shot;

    /// <summary>How the last frame moved between pictures: its style, how far along, and which held picture. Call <see cref="Frame"/> first.</summary>
    public Rendition.ScreenTransition Transition => _host.View.Transition;

    /// <summary>
    /// What the mixer is playing now: each voice's clip, channel, gain, pan and how far in it is. A sound started
    /// in an Update shows up once that tick's Draw has handed it over, so after the tick.
    /// </summary>
    public IReadOnlyList<PlayingSound> Sounds => _host.Audio.Playing;

    /// <summary>The text the interface showed in the last Draw, with where it landed.</summary>
    public IReadOnlyList<ShownText> ShownText => _host.Ui.ShownText;

    /// <summary>
    /// Where the view given this id with <see cref="UiExtensions.Id"/> was laid out in the
    /// last Draw, without its margin. It throws if no view or more than one has the id.
    /// </summary>
    public UiRect FrameOf(object id) => _host.Ui.FrameOf(id);

    /// <summary>
    /// Plays a script, such as one from the game's <see cref="IGame.Scripts"/>, as
    /// <c>--script</c> does, starting on the next tick. It replaces any script still playing.
    /// </summary>
    public void Play(InputScript script) => _host.Script = script;

    /// <summary>Adds a rule that's checked after every tick, so a problem is caught when it happens.</summary>
    public void Invariant(string name, Func<TGame, bool> holds) => _invariants.Add((name, holds));

    /// <summary>Draws without ticking, part of the way between the last tick and the next, as a fast display would.</summary>
    public void Frame(float blend) => _host.Draw(blend);

    /// <summary>Plays this many ticks, with a Draw after each one at the end of the tick.</summary>
    public void Ticks(long count)
    {
        for (long i = 0; i < count; i++)
        {
            Input.Play(_host.Time.Tick);
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
