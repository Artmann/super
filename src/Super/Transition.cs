using System.Numerics;

namespace Super;

/// <summary>The kinds of <see cref="Transition"/>.</summary>
public enum TransitionKind
{
    /// <summary>Jumps straight to the new picture.</summary>
    Cut,

    /// <summary>Moves the camera smoothly from where it was to the new one.</summary>
    Blend,

    /// <summary>Fades out to a colour, then in to the new picture.</summary>
    Fade,

    /// <summary>The new picture shows through the old one, a little more each frame.</summary>
    Dissolve,

    /// <summary>An edge sweeps across the screen, leaving the new picture behind it.</summary>
    Wipe,

    /// <summary>A circle grows from a point on the screen, with the new picture inside it.</summary>
    Iris,
}

/// <summary>Which way a <see cref="Transition.Wipe"/>'s edge travels across the screen.</summary>
public enum WipeDirection
{
    Right,
    Left,
    Down,
    Up,
}

/// <summary>
/// How the picture moves from one camera to the next: a cut, a camera blend, a fade
/// through a colour, a dissolve, a wipe or an iris. Give one to <see cref="Transitions.Play"/>
/// during play, or to <see cref="Cutscene.Then(Shot, Transition)"/> between shots.
/// Dissolves, wipes and irises show the last frame before they began, held still, so
/// the old picture doesn't move while it goes.
/// </summary>
public sealed class Transition
{
    private Transition(TransitionKind kind, float seconds, Easing easing)
    {
        if (kind != TransitionKind.Cut && (!(seconds > 0f) || !float.IsFinite(seconds)))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A transition must last more than zero seconds.");
        }

        Kind = kind;
        Seconds = seconds;
        Easing = easing;
    }

    public TransitionKind Kind { get; }

    /// <summary>How long it lasts. A fade spends half going out and half coming in.</summary>
    public float Seconds { get; }

    /// <summary>How the move gets going and comes to rest.</summary>
    public Easing Easing { get; }

    /// <summary>The colour a fade passes through.</summary>
    public Color Color { get; private init; } = Color.Black;

    /// <summary>Which way a wipe's edge travels.</summary>
    public WipeDirection Direction { get; private init; }

    /// <summary>Where an iris opens from, as a fraction of the screen from the top left.</summary>
    public Vector2 Center { get; private init; } = new(0.5f, 0.5f);

    /// <summary>How soft a wipe's or an iris's edge is, as a fraction of the distance it travels.</summary>
    public float Softness { get; private init; }

    /// <summary>Jumps straight to the new picture.</summary>
    public static Transition Cut { get; } = new(TransitionKind.Cut, 0f, Easing.Linear);

    /// <summary>
    /// Moves the camera from where it was to the new one. Only the camera moves, so it
    /// suits two views of the same place.
    /// </summary>
    public static Transition Blend(float seconds, Easing easing = Easing.InOut) =>
        new(TransitionKind.Blend, seconds, easing);

    /// <summary>
    /// Fades out to a colour, black unless you pick one, and back in. The switch happens
    /// halfway, while the screen is all colour, so both halves show the world moving.
    /// </summary>
    public static Transition Fade(float seconds, Color? color = null) =>
        new(TransitionKind.Fade, seconds, Easing.Linear) { Color = color ?? Color.Black };

    /// <summary>The new picture shows through the old one, a little more each frame.</summary>
    public static Transition Dissolve(float seconds, Easing easing = Easing.Linear) =>
        new(TransitionKind.Dissolve, seconds, easing);

    /// <summary>An edge sweeps across the screen, leaving the new picture behind it.</summary>
    public static Transition Wipe(
        float seconds,
        WipeDirection direction = WipeDirection.Right,
        float softness = 0.05f,
        Easing easing = Easing.InOut)
    {
        CheckSoftness(softness);
        return new(TransitionKind.Wipe, seconds, easing) { Direction = direction, Softness = softness };
    }

    /// <summary>
    /// A circle grows from a point, the middle unless you give one, until the new picture
    /// fills the screen. The point is a fraction of the screen from the top left.
    /// </summary>
    public static Transition Iris(float seconds, Vector2? center = null, float softness = 0.02f, Easing easing = Easing.InOut)
    {
        CheckSoftness(softness);
        var at = center ?? new Vector2(0.5f, 0.5f);
        if (!(at.X >= 0f && at.X <= 1f && at.Y >= 0f && at.Y <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(center), center, "An iris opens from a point on the screen, with x and y from 0 to 1.");
        }

        return new(TransitionKind.Iris, seconds, easing) { Center = at, Softness = softness };
    }

    /// <summary>Whether it shows a held picture of the frame before it began.</summary>
    internal bool Holds => Kind is TransitionKind.Dissolve or TransitionKind.Wipe or TransitionKind.Iris;

    /// <summary>What the screen shows this far in, in seconds, from no change to all of it.</summary>
    internal float AmountAt(float seconds)
    {
        float through = Math.Clamp(seconds / Seconds, 0f, 1f);
        if (Kind == TransitionKind.Fade)
        {
            // Out to the colour and back in: all colour at the halfway point.
            return 1f - MathF.Abs(2f * through - 1f);
        }

        return Ease.Apply(Easing, through);
    }

    internal Rendition.TransitionStyle Style => Kind switch
    {
        TransitionKind.Fade => Rendition.TransitionStyle.Fade,
        TransitionKind.Dissolve => Rendition.TransitionStyle.Dissolve,
        TransitionKind.Wipe => Rendition.TransitionStyle.Wipe,
        TransitionKind.Iris => Rendition.TransitionStyle.Iris,
        _ => Rendition.TransitionStyle.None,
    };

    /// <summary>Which way the wipe travels on screen, with y down.</summary>
    internal Vector2 DirectionVector => Direction switch
    {
        WipeDirection.Left => -Vector2.UnitX,
        WipeDirection.Down => Vector2.UnitY,
        WipeDirection.Up => -Vector2.UnitY,
        _ => Vector2.UnitX,
    };

    /// <summary>The fade's colour as light, which is what the screen mixes in.</summary>
    internal Vector3 LinearColor => new(ToLinear(Color.R), ToLinear(Color.G), ToLinear(Color.B));

    private static float ToLinear(float srgb) =>
        srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);

    private static void CheckSoftness(float softness)
    {
        if (!(softness >= 0f) || !float.IsFinite(softness))
        {
            throw new ArgumentOutOfRangeException(nameof(softness), softness, "An edge's softness must be a number of at least zero.");
        }
    }
}
