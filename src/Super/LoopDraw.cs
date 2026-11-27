using Microsoft.Extensions.DependencyInjection;
using Super.Ui;

namespace Super;

/// <summary>
/// What one frame does, in order: the world, the game's Draw, the camera, transitions and draw hooks, the audio, then the
/// interface. Takes the services the host already made, so it makes none of its own.
/// </summary>
internal sealed class LoopDraw
{
    private readonly IGame _looped;
    private readonly GameTime _time;
    private readonly UiPointer _pointer;
    private readonly LoopHalts _halts;
    private readonly Director _director;
    private readonly Transitions _transitions;
    private readonly Audio _audio;
    private readonly GameStats _stats;
    private readonly Gpu _gpu;
    private readonly IDrawHook[] _hooks;
    private readonly StateChanges _changes;
    private readonly World _world;
    private readonly DebugDraw _debug;

    public LoopDraw(IServiceProvider services, IGame looped, GameTime time, UiPointer pointer, LoopHalts halts)
    {
        _looped = looped;
        _time = time;
        _pointer = pointer;
        _halts = halts;
        _director = services.GetRequiredService<Director>();
        _transitions = services.GetRequiredService<Transitions>();
        _audio = services.GetRequiredService<Audio>();
        _stats = services.GetRequiredService<GameStats>();
        _gpu = services.GetRequiredService<Gpu>();
        _hooks = services.GetServices<IDrawHook>().ToArray();
        _changes = services.GetRequiredService<StateChanges>();
        _world = services.GetRequiredService<World>();
        _debug = services.GetRequiredService<DebugDraw>();
    }

    /// <summary>Whether the interface shows what bakes in the background, over the game's own. Only play does.</summary>
    public bool ShowsBakes { get; set; }

    /// <summary>How the background bake is getting on, as the renderer last said, or null when nothing bakes.</summary>
    public Rendition.BakeProgress? BakeProgress { get; set; }

    /// <summary>
    /// Draws one frame into <paramref name="view"/>, with the interface laid out in <paramref name="ui"/>. When the game
    /// keeps running after errors, one in Draw or Ui stops it, and from then on frames show only the error.
    /// </summary>
    public void Run(View view, UiTree ui, int width, int height, float blend)
    {
        if (!_halts.KeepsRunning)
        {
            RunOnce(view, ui, width, height, blend);
            return;
        }

        if (_halts.MayDraw)
        {
            try
            {
                RunOnce(view, ui, width, height, blend);
                return;
            }
            catch (Exception error)
            {
                _halts.StopDraw(error);
            }
        }

        RunStopped(view, ui, width, height, blend);
    }

    private void RunOnce(View view, UiTree ui, int width, int height, float blend)
    {
        _changes.Open = false;
        view.Begin(blend, _time.Tick, _time.TickSeconds, _time.Scale);
        WorldFrame.Describe(view, _world);
        _looped.Draw(view);
        if (_director.HasCamera)
        {
            view.Camera(_director.At(blend));
        }

        // After the camera is placed, since a blend starts from the last one shown.
        _transitions.Apply(view);

        foreach (var hook in _hooks)
        {
            hook.AfterDraw(view);
        }

        // After the hooks, which may draw debug shapes of their own.
        _debug.CopyTo(view.Debug);

        // Last of the frame's own work, so the listener is the camera the frame showed and the hooks' changes go too.
        _audio.EndFrame(view.Shot, _time.Scale, view.Clock.Now);

        // The interface runs outside the measurement, so a stats panel reports the game and not itself.
        _stats.EndFrame();
        _pointer.Pump();
        ui.Clock.Now = view.Clock.Now;
        ui.Update(Interface(), width, height);
        if (_pointer.Refresh())
        {
            ui.Update(Interface(), width, height);
        }

        _pointer.ShowOverlays();
        ui.ShowDebugLabels(view.Debug.Labels, view.Shot);
        view.Effects.Ui = ui;
        _stats.Resume();
        _changes.Open = true;
    }

    /// <summary>A frame without any of the game's code, for when Draw or Ui is what broke.</summary>
    private void RunStopped(View view, UiTree ui, int width, int height, float blend)
    {
        view.Begin(blend, _time.Tick, _time.TickSeconds, _time.Scale);
        _audio.EndFrame(view.Shot, _time.Scale, view.Clock.Now);
        ui.Update(StoppedPanel.For(_halts.Stopped!), width, height);
        view.Effects.Ui = ui;
        _changes.Open = true;
    }

    /// <summary>
    /// While stopped by Update, the world is still drawn, with the error in place of the game's interface. In play, the
    /// line saying what bakes in the background goes over either, and so does the engine's stats panel while it shows.
    /// </summary>
    private UiView? Interface()
    {
        var ui = _halts.Stopped is { } error ? StoppedPanel.For(error) : _looped.Ui;
        if (!ShowsBakes && !_stats.Shown)
        {
            return ui;
        }

        var stats = _stats.Shown ? StatsOverlay.Panel(_stats.Latest, _gpu.Name) : null;
        return BakeOverlay.Over(ui, ShowsBakes ? BakeProgress : null, stats);
    }
}
