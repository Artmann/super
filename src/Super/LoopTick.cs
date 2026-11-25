using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ui;

namespace Super;

/// <summary>
/// What one tick does, in order: the script, reloads and code changes first, then input, the game's Update, and what
/// moves after it. Takes the services the host already made, so it makes none of its own.
/// </summary>
internal sealed class LoopTick
{
    private readonly IGame _looped;
    private readonly GameTime _time;
    private readonly Content _content;
    private readonly Input _input;
    private readonly UiPointer _pointer;
    private readonly ITickHook[] _hooks;
    private readonly Transitions _transitions;
    private readonly Director _director;
    private readonly Audio _audio;
    private readonly StateChanges _changes;
    private readonly Scenes _scenes;
    private readonly LoopHalts _halts;
    private long _scriptStart;

    public LoopTick(IServiceProvider services, IGame looped, GameTime time, UiPointer pointer, LoopHalts halts)
    {
        _looped = looped;
        _time = time;
        _pointer = pointer;
        _halts = halts;
        _content = services.GetRequiredService<Content>();
        _input = services.GetRequiredService<Input>();
        _transitions = services.GetRequiredService<Transitions>();
        _director = services.GetRequiredService<Director>();
        _audio = services.GetRequiredService<Audio>();
        _hooks = services.GetServices<ITickHook>().ToArray();
        _changes = services.GetRequiredService<StateChanges>();
        _scenes = services.GetRequiredService<Scenes>();
    }

    /// <summary>When set, this is the player. Each tick plays what's due on it, counting from the tick it was set.</summary>
    public InputScript? Script
    {
        get;
        set
        {
            field = value;
            _scriptStart = _time.Tick;
        }
    }

    /// <summary>
    /// Plays one tick, with the interface laid out in <paramref name="ui"/> on a screen of <paramref name="screenSize"/>
    /// pixels. When the game keeps running after errors, one stops it instead of closing it, and a stopped game plays
    /// nothing until a change.
    /// </summary>
    public void Run(UiTree ui, Vector2 screenSize)
    {
        if (!_halts.KeepsRunning)
        {
            RunOnce(ui, screenSize);
            return;
        }

        if (!_halts.MayTick(_content.Data.Live.ReloadWanted))
        {
            return;
        }

        try
        {
            RunOnce(ui, screenSize);
        }
        catch (Exception error)
        {
            _halts.StopTick(error);
        }
    }

    private void RunOnce(UiTree ui, Vector2 screenSize)
    {
        BeforeInput(ui);
        RollInput(screenSize);
        Update();

        // Last, so all of Update saw one state and the next Draw shows the new one.
        _changes.Apply();

        // After state changes, so a Go asked for in OnEnter lands this tick too.
        _scenes.Apply();

        // After the scene change, so a camera built with a scene is on its rig for the first frame.
        WorldRigs.Show(_scenes.World);
        _content.Data.RequireReferences();
        _time.Tick++;
    }

    private void BeforeInput(UiTree ui)
    {
        Script?.Play(_time.Tick - _scriptStart, _input, ui);

        // Only here, so all of a tick and the Draw after it see one version of the data.
        if (_content.Data.Live.ReloadWanted)
        {
            _content.Data.Reload(Console.Error);
        }

        // At the start of a tick, on the game's thread, however the runtime delivered the change.
        if (_halts.TakeCodeChange())
        {
            _looped.CodeReloaded();
        }
    }

    private void RollInput(Vector2 screenSize)
    {
        _pointer.BeforeRoll();

        // Input is only readable during Update, so the screen can't depend on it.
        _input.ScreenSize = screenSize;
        _input.Readable = true;
        _input.Roll();
        foreach (var hook in _hooks)
        {
            hook.BeforeUpdate();
        }

        _pointer.Deliver();
    }

    private void Update()
    {
        // Before Update, so a fade's change lands while the screen is all colour, and the
        // game sees it and a cutscene's moments and time scale this tick.
        _transitions.BeforeUpdate();
        _director.BeforeUpdate();

        // Before Update, so this tick's audio is what was asked for up to the last frame. A sound started in this
        // Update is handed over at this tick's Draw and mixed from the next tick.
        if (_audio.Device.Manual)
        {
            _audio.Advance(_time.TickSeconds);
        }

        _looped.Update(_time);
        _input.Readable = false;

        // After Update, so what the game set this tick plays, and Crossed sees each moment once.
        _content.Models.Animators.Advance(_time.DeltaSeconds);

        // After Update, so a burst spawned this tick starts on it and one that has played is gone before Draw.
        WorldBursts.Advance(_scenes.World, _time.Tick, _time.TickSeconds);

        // Once everything has moved, so a camera following something sees where it is now.
        _director.AfterUpdate();
    }
}
