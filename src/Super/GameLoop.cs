using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ui;

namespace Super;

/// <summary>
/// Runs a game's Update and Draw, with or without a window. The window and headless runs both sit on this, so they
/// can't drift apart. <see cref="GameHost{TGame}"/> builds the services it runs on; <see cref="LoopTick"/> and
/// <see cref="LoopDraw"/> say what a tick and a frame do, and <see cref="LoopHalts"/> what an error in the game's code
/// does.
/// </summary>
internal abstract class GameLoop
{
    private const int DefaultScreenWidth = 1280;
    private const int DefaultScreenHeight = 720;

    private readonly LoopTick _tick;
    private readonly LoopDraw _draw;

    /// <summary>
    /// Takes the services <paramref name="services"/> holds, makes the game with <paramref name="makeGame"/> and loads
    /// it. <paramref name="time"/> is the clock the game's ticks count on.
    /// </summary>
    protected GameLoop(IServiceProvider services, GameTime time, Func<IServiceProvider, IGame> makeGame)
    {
        Time = time;
        Content = services.GetRequiredService<Content>();
        View.Sources = (Content, services.GetRequiredService<World>());
        View.Debug.Channels = services.GetRequiredService<DebugDraw>();
        Input = services.GetRequiredService<Input>();
        Capture = services.GetRequiredService<GameCapture>();
        Audio = services.GetRequiredService<Audio>();
        Content.Models.Animators.Play = (sound, at, volume, pitchVariation) => Audio.Play(sound, at: at, volume: volume, pitchVariation: pitchVariation);
        Graphics = services.GetRequiredService<Graphics>();
        Stats = services.GetRequiredService<GameStats>();
        LoopedGame = makeGame(services);
        Halts = new LoopHalts(Input);
        Input.Mouse.Feed.RoutesMouse = true;
        Input.Keys.Routes = true;
        Input.Touches.Feed.Routes = true;
        var pointer = new UiPointer(Ui, Input, time.TicksPerSecond);
        _tick = new LoopTick(services, LoopedGame, time, pointer, Halts);
        _draw = new LoopDraw(services, LoopedGame, time, pointer, Halts);
        var changes = services.GetRequiredService<StateChanges>();
        changes.Hooks = services.GetServices<IStateHook>().ToArray();
        LoopedGame.Load();
        changes.Apply();
        Content.Data.RequireReferences();
    }

    /// <summary>The game, as the loop calls it.</summary>
    protected IGame LoopedGame { get; }

    /// <summary>Whether an error in the game's code has stopped it.</summary>
    protected LoopHalts Halts { get; }

    public GameTime Time { get; }

    public Content Content { get; }

    public Input Input { get; }

    public GameCapture Capture { get; }

    public Audio Audio { get; }

    /// <summary>How the game is drawn. Changes show from the next frame.</summary>
    public Graphics Graphics { get; }

    /// <summary>How the game is running, and whether the engine's stats panel shows it.</summary>
    public GameStats Stats { get; }

    public View View { get; } = new();

    public UiTree Ui { get; } = new();

    /// <summary>When set, this is the player. Each tick plays what's due on it, counting from the tick it was set.</summary>
    public InputScript? Script
    {
        get => _tick.Script;
        set => _tick.Script = value;
    }

    /// <summary>The size the interface lays itself out in, in pixels. The window keeps this up to date.</summary>
    public int ScreenWidth { get; set; } = DefaultScreenWidth;

    public int ScreenHeight { get; set; } = DefaultScreenHeight;

    /// <summary>
    /// What each frame does, which play tells whether the interface shows what bakes in the background and how the
    /// bake is getting on.
    /// </summary>
    internal LoopDraw DrawSteps => _draw;

    public void Tick() => _tick.Run(Ui, new Vector2(ScreenWidth, ScreenHeight));

    public void Draw(float blend) => _draw.Run(View, Ui, ScreenWidth, ScreenHeight, blend);
}
