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

/// <summary>
/// The motion a theme's controls add for feel, each with its own switch. <c>.Animations(false)</c> turns all of it
/// off as well, for a player who asked for less motion.
/// </summary>
public sealed record ThemeMotion
{
    /// <summary>All of it on.</summary>
    public static ThemeMotion Default { get; } = new();

    /// <summary>None of it: presses don't squash, losses don't trail and pages swap at once.</summary>
    public static ThemeMotion None { get; } = new() { PressScale = 1f, TrailsLoss = false, SlidesPages = false };

    /// <summary>How big a button is drawn while it's held down, where 1 doesn't squash it. It springs back when let go.</summary>
    public float PressScale { get; init; } = 0.95f;

    /// <summary>Whether a <see cref="Gauge"/> that drops shows what it lost as a pale piece that drains away after it.</summary>
    public bool TrailsLoss { get; init; } = true;

    /// <summary>Whether a <see cref="NavigationStack{T}"/> slides a pushed page in from the side and the page it covers out.</summary>
    public bool SlidesPages { get; init; } = true;
}

/// <summary>
/// A number that counts to a new value instead of jumping there, as a score, gold or experience does. It shows
/// <paramref name="Value"/> at once when it appears, and counts over <see cref="Seconds"/> each time the value changes,
/// from wherever it's got to. Its digits are all as wide, so the number doesn't shake as it counts. Text modifiers
/// such as <c>.FontSize</c> style it as they do <see cref="Text"/>.
/// </summary>
public sealed record CountingText(float Value) : UiView
{
    public float Seconds { get; init; } = 0.6f;

    /// <summary>How the number reads, such as <c>n => $"{n:0} gold"</c>. Without it, it's a whole number.</summary>
    public Func<float, string>? Format { get; init; }

    public override UiView Body
    {
        get
        {
            float shown = UseAnimation(Value, Seconds, Easing.Out);
            string text = Format?.Invoke(shown) ?? MathF.Round(shown).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            return new Text(text).MonospacedDigit();
        }
    }
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
/// The interface's time, in seconds, which each update moves on. Components that are moving ask for the next
/// frame here, and it runs them again then even though nothing else changed.
/// </summary>
internal sealed class UiClock
{
    /// <summary>How far an update moves the time when nobody says what time it is.</summary>
    public const double Step = 1.0 / 60.0;

    private List<UiBodyHooks> _waiting = [];
    private List<UiBodyHooks> _waking = [];

    public double Now { get; private set; }

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

    /// <summary>Stops waiting on a component that has left the tree.</summary>
    public void Forget(UiBodyHooks hooks) => _waiting.Remove(hooks);

    /// <summary>
    /// Moves to this time and marks each component that asked for the next frame to run again. Those still
    /// moving ask again as they run. A second update at the same time, as when the pointer moved, wakes nothing,
    /// since nothing would look different.
    /// </summary>
    public void Advance(double now)
    {
        if (now == Now)
        {
            return;
        }

        Now = now;
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
            return Partway(Child, Kind, shown);
        }
    }

    /// <summary>
    /// A view <paramref name="shown"/> of the way in, from 0 to 1. The same views however far along, so what's inside
    /// keeps its state as it moves, and a box of its own, so a fade of the view's own isn't replaced.
    /// </summary>
    internal static UiView Partway(UiView child, Entrance kind, float shown)
    {
        float rise = kind switch
        {
            Entrance.Rise => (1f - shown) * Travel,
            Entrance.Drop => (shown - 1f) * Travel,
            _ => 0f,
        };
        return new OffsetModifier(new BoxModifier(child) { Alpha = shown }, 0f, rise);
    }
}

/// <summary>
/// <paramref name="Content"/> while <paramref name="Shown"/> is true, which eases in the way <see cref="Entrance"/>
/// says, as <c>.Transition</c> does, and when it turns false plays that backwards before it goes, so a menu or a panel
/// can leave as well as arrive. While it leaves it's drawn but lets the mouse through. Once it's gone it takes no room
/// and keeps no state, though a stack still puts its spacing beside it.
/// </summary>
public sealed record Presence(bool Shown, UiView Content) : UiView
{
    public Entrance Entrance { get; init; } = Entrance.Fade;

    public float Seconds { get; init; } = 0.2f;

    public override UiView Body
    {
        get
        {
            float shown = UseAnimation(Shown ? 1f : 0f, Seconds, Easing.Out, from: 0f);
            if (!Shown && shown <= 0f)
            {
                return new NothingView();
            }

            return new LeavingModifier(TransitionView.Partway(Content, Entrance, shown), !Shown);
        }
    }
}

/// <summary>A view on its way out, which the mouse goes through.</summary>
internal sealed record LeavingModifier(UiView Child, bool Leaving) : Modifier(Child)
{
    internal override bool PassesPointer => Leaving;
}

/// <summary>A view with nothing in it and no room, for a component that has nothing to show.</summary>
internal sealed record NothingView : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal) => default;

    internal override void Place(UiNode node, UiRect frame)
    {
    }
}
