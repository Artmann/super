namespace Super.Ui;

/// <summary>How a view arrives when it appears, with <see cref="AnimationExtensions.Transition"/>.</summary>
public enum Entrance
{
    /// <summary>Fades in.</summary>
    Fade,

    /// <summary>Fades in while rising a little into place, as a toast or a list row does.</summary>
    Rise,

    /// <summary>Fades in while dropping a little into place, as a menu under its button does.</summary>
    Drop,
}

/// <summary>Modifiers for motion: views that ease in when they appear, and a switch for turning motion off.</summary>
public static class AnimationExtensions
{
    /// <summary>The length of the built-in controls' motion, such as a switch's knob, in seconds.</summary>
    public const float Quick = 0.15f;

    /// <summary>
    /// Eases this view in over <paramref name="seconds"/> when it appears, instead of showing it at once. It
    /// takes its full room from the first frame, so nothing round it moves, and it takes clicks all the way in.
    /// It only plays when the view appears, not when it goes.
    /// </summary>
    public static UiView Transition(this UiView view, Entrance entrance = Entrance.Fade, float seconds = 0.2f) =>
        new TransitionView(view, entrance, seconds);

    /// <summary>
    /// Turns motion on or off for everything inside, such as for a player who asked for less of it: controls
    /// and <see cref="UiView.UseAnimation"/> jump straight to where they're going. It's on by default.
    /// </summary>
    public static UiView Animations(this UiView view, bool on) => new AnimationsModifier(view, on);
}

/// <summary>
/// The interface's time, in seconds, which the frame sets before each update. Components that are moving ask
/// for the next frame here, and it runs them again then even though nothing else changed.
/// </summary>
internal sealed class UiClock
{
    private List<UiBodyHooks> _waiting = [];
    private List<UiBodyHooks> _waking = [];

    public double Now { get; set; }

    /// <summary>Whether something is moving, and the frame after this one will look different.</summary>
    public bool Moving => _waiting.Count > 0;

    /// <summary>Runs this component again on the next update.</summary>
    public void NextFrame(UiBodyHooks hooks)
    {
        if (!_waiting.Contains(hooks))
        {
            _waiting.Add(hooks);
        }
    }

    /// <summary>Marks each component that asked for this frame to run again. Those still moving ask again as they run.</summary>
    public void Wake()
    {
        (_waiting, _waking) = (_waking, _waiting);
        foreach (var hooks in _waking)
        {
            hooks.Changed();
        }

        _waking.Clear();
    }
}

/// <summary>A number on its way from one value to another, held in a component's hooks.</summary>
internal sealed class UiAnimation(float value)
{
    private float _from = value;
    private double _start;
    private float _seconds;
    private Easing _easing;

    public float To { get; private set; } = value;

    /// <summary>Where it is at this time.</summary>
    public float At(double now)
    {
        float t = _seconds > 0f ? (float)Math.Clamp((now - _start) / _seconds, 0.0, 1.0) : 1f;
        return t >= 1f ? To : _from + (To - _from) * Super.Ease.Apply(_easing, t);
    }

    public bool Arrived(double now) => now - _start >= _seconds;

    /// <summary>Heads for a new value from wherever it is now, so a change halfway turns round smoothly.</summary>
    public void Retarget(float to, double now, float seconds, Easing easing)
    {
        _from = At(now);
        To = to;
        _start = now;
        _seconds = seconds;
        _easing = easing;
    }
}

/// <summary>Turns motion on or off for the views inside.</summary>
internal sealed record AnimationsModifier(UiView Child, bool On) : Modifier(Child)
{
    internal override UiEnvironment Inherit(UiEnvironment environment) => environment with { Still = !On };
}

/// <summary>A view that eases in when it appears, from <see cref="AnimationExtensions.Transition"/>.</summary>
internal sealed record TransitionView(UiView Child, Entrance Kind, float Seconds) : UiView
{
    // How far a rising or dropping view travels, in units.
    private const float Travel = 8f;

    public override UiView Body
    {
        get
        {
            float shown = UseAnimation(1f, Seconds, Easing.Out, from: 0f);
            float rise = Kind switch
            {
                Entrance.Rise => (1f - shown) * Travel,
                Entrance.Drop => (shown - 1f) * Travel,
                _ => 0f,
            };

            // The same views however far along, so what's inside keeps its state when it arrives.
            // A box of its own, so a fade of the view's own isn't replaced.
            return new OffsetModifier(new BoxModifier(Child) { Alpha = shown }, 0f, rise);
        }
    }
}
