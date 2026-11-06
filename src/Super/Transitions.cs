using Rendition;

namespace Super;

/// <summary>
/// Plays a <see cref="Transition"/> on the screen during play, such as a fade to black
/// while the game moves the player through a door. Ask for it in your game's constructor.
/// It runs on real ticks, so slow motion doesn't stretch it, and it touches only the world:
/// the interface stays as it is on top.
/// </summary>
public sealed class Transitions(GameTime time)
{
    private Transition? _playing;
    private Action? _atCut;
    private bool _cutDone;
    private long _ticks;
    private float _skip;
    private Camera _from;
    private Camera _lastShown;
    private bool _shownAny;
    private int _hold;

    /// <summary>Whether a transition is on screen.</summary>
    public bool IsPlaying => _playing is not null;

    /// <summary>The transition on screen, or none.</summary>
    public Transition? Playing => _playing;

    /// <summary>
    /// Starts a transition on the next frame, ending any that's playing. Put the change it
    /// covers in <paramref name="atCut"/>, such as moving the player or the camera. A fade
    /// runs it halfway, at the start of that tick, while the screen is all colour; the
    /// rest run it now, since they show the frame from before it.
    /// </summary>
    public void Play(Transition transition, Action? atCut = null) => Start(transition, atCut, skipSeconds: 0f);

    /// <summary>Ends the transition now, running a fade's change if it hasn't happened yet.</summary>
    public void Stop()
    {
        var pending = _cutDone ? null : _atCut;
        _playing = null;
        _atCut = null;
        _cutDone = true;
        pending?.Invoke();
    }

    /// <summary>Starts part way in. A cutscene fades in from its colour this way, by skipping the half going out.</summary>
    internal void Start(Transition transition, Action? atCut, float skipSeconds)
    {
        ArgumentNullException.ThrowIfNull(transition);
        Stop();

        if (transition.Kind == TransitionKind.Cut)
        {
            atCut?.Invoke();
            return;
        }

        _playing = transition;
        _ticks = 0;
        _skip = skipSeconds;
        _from = _lastShown;
        if (!_shownAny && transition.Kind == TransitionKind.Blend)
        {
            // Nothing has been shown to blend from, so it ends at once.
            _playing = null;
        }

        if (transition.Holds)
        {
            _hold++;
        }

        if (transition.Kind == TransitionKind.Fade)
        {
            _atCut = atCut;
            _cutDone = atCut is null;
            return;
        }

        atCut?.Invoke();
    }

    /// <summary>Moves on a tick, before the game's Update, and makes a fade's change halfway.</summary>
    internal void BeforeUpdate()
    {
        if (_playing is null)
        {
            return;
        }

        _ticks++;

        // A frame shows the time between the last tick and this one, so once the tick
        // before this reached the end, every frame from here on shows it finished.
        if ((_ticks - 1) * time.TickSeconds + _skip >= _playing.Seconds)
        {
            Stop();
            return;
        }

        if (!_cutDone && _ticks * time.TickSeconds + _skip >= _playing.Seconds / 2f)
        {
            _cutDone = true;
            var change = _atCut;
            _atCut = null;
            change?.Invoke();
        }
    }

    /// <summary>Puts the transition on a frame, after the game and any cutscene have placed the camera.</summary>
    internal void Apply(View view)
    {
        var screen = new ScreenTransition { Hold = _hold };
        if (_playing is { } transition)
        {
            float seconds = MathF.Max(0f, (_ticks - 1 + view.Blend) * time.TickSeconds) + _skip;
            float amount = transition.AmountAt(seconds);
            if (transition.Kind == TransitionKind.Blend)
            {
                view.Camera(Camera.Lerp(_from, view.Shot, amount));
            }
            else
            {
                screen.Style = transition.Style;
                screen.Amount = amount;
                screen.Color = transition.LinearColor;
                screen.Direction = transition.DirectionVector;
                screen.Center = transition.Center;
                screen.Softness = transition.Softness;
            }
        }

        view.Transition = screen;
        _lastShown = view.Shot;
        _shownAny = true;
    }
}
