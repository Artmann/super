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

    // A fade's change, until it runs at the cut: while this is set the cut is still to come.
    private Action? _atCut;

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
        var pending = _atCut;
        _playing = null;
        _atCut = null;
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

        if (_atCut is { } change && _ticks * time.TickSeconds + _skip >= _playing.Seconds / 2f)
        {
            _atCut = null;
            change();
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
                TransitionLook.Fill(ref screen, transition, amount);
            }
        }

        view.Transition = screen;
        _lastShown = view.Shot;
        _shownAny = true;
    }
}

/// <summary>Turns a <see cref="Transition"/> into what the renderer draws over the screen.</summary>
internal static class TransitionLook
{
    /// <summary>Sets everything on the screen's transition but the held picture, this far in.</summary>
    public static void Fill(ref ScreenTransition screen, Transition transition, float amount)
    {
        screen.Style = Style(transition.Kind);
        screen.Amount = amount;
        screen.Color = LinearColor(transition.Color);
        screen.Direction = DirectionVector(transition.Direction);
        screen.Center = transition.Center;
        screen.Softness = transition.Softness;
    }

    private static TransitionStyle Style(TransitionKind kind) => kind switch
    {
        TransitionKind.Fade => TransitionStyle.Fade,
        TransitionKind.Dissolve => TransitionStyle.Dissolve,
        TransitionKind.Wipe => TransitionStyle.Wipe,
        TransitionKind.Iris => TransitionStyle.Iris,
        _ => TransitionStyle.None,
    };

    /// <summary>Which way the wipe travels on screen, with y down.</summary>
    private static System.Numerics.Vector2 DirectionVector(WipeDirection direction) => direction switch
    {
        WipeDirection.Left => -System.Numerics.Vector2.UnitX,
        WipeDirection.Down => System.Numerics.Vector2.UnitY,
        WipeDirection.Up => -System.Numerics.Vector2.UnitY,
        _ => System.Numerics.Vector2.UnitX,
    };

    /// <summary>The fade's colour as light, which is what the screen mixes in.</summary>
    private static System.Numerics.Vector3 LinearColor(Color color) =>
        new(ToLinear(color.R), ToLinear(color.G), ToLinear(color.B));

    private static float ToLinear(float srgb) =>
        srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
}
