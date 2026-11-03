using System.Numerics;
using System.Runtime.CompilerServices;

namespace Super;

/// <summary>What a tween does when it reaches the end.</summary>
public enum Repeat
{
    /// <summary>Stops at the end.</summary>
    Once,

    /// <summary>Jumps back to the start and plays again.</summary>
    Loop,

    /// <summary>Plays backwards to the start, then forwards again.</summary>
    Yoyo,
}

/// <summary>
/// Makes tweens. A tween takes a value from one place to another over some seconds, such
/// as a door swinging open or a light fading. Tweens can blend <c>float</c>,
/// <c>Vector2</c>, <c>Vector3</c>, <c>Vector4</c>, <c>Quaternion</c> and <see cref="Color"/>.
/// </summary>
public static class Tween
{
    /// <summary>A number from one value to another.</summary>
    public static Tween<float> Between(float from, float to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>A 2D point or size from one value to another.</summary>
    public static Tween<Vector2> Between(Vector2 from, Vector2 to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>A position, scale or other 3D value from one to another.</summary>
    public static Tween<Vector3> Between(Vector3 from, Vector3 to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>A 4D value from one to another.</summary>
    public static Tween<Vector4> Between(Vector4 from, Vector4 to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>A rotation from one to another, taking the short way round.</summary>
    public static Tween<Quaternion> Between(Quaternion from, Quaternion to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>A color from one to another, blending each channel and the opacity.</summary>
    public static Tween<Color> Between(Color from, Color to, float seconds, Easing easing = Easing.Linear) => new(from, to, seconds, easing);

    /// <summary>
    /// Tweens that play one after another, as one value. Each keeps its own delay, easing
    /// and repeats, but none can repeat forever, or those after it would never play.
    /// </summary>
    public static TweenSequence<T> Sequence<T>(params Tween<T>[] steps)
        where T : struct => new(steps);

    /// <summary>Tweens that play at the same time and finish when the longest does. Read each with the indexer.</summary>
    public static TweenGroup<T> Parallel<T>(params Tween<T>[] tweens)
        where T : struct => new(tweens);

    /// <summary>Blends two values of a type a tween can carry. Past 0 and 1 it carries on, so overshooting easings work.</summary>
    internal static T Mix<T>(T from, T to, float amount)
        where T : struct
    {
        if (typeof(T) == typeof(float))
        {
            return Cast<float, T>(float.Lerp(Cast<T, float>(from), Cast<T, float>(to), amount));
        }

        if (typeof(T) == typeof(Vector2))
        {
            return Cast<Vector2, T>(Vector2.Lerp(Cast<T, Vector2>(from), Cast<T, Vector2>(to), amount));
        }

        if (typeof(T) == typeof(Vector3))
        {
            return Cast<Vector3, T>(Vector3.Lerp(Cast<T, Vector3>(from), Cast<T, Vector3>(to), amount));
        }

        if (typeof(T) == typeof(Vector4))
        {
            return Cast<Vector4, T>(Vector4.Lerp(Cast<T, Vector4>(from), Cast<T, Vector4>(to), amount));
        }

        if (typeof(T) == typeof(Quaternion))
        {
            return Cast<Quaternion, T>(Quaternion.Slerp(Cast<T, Quaternion>(from), Cast<T, Quaternion>(to), amount));
        }

        if (typeof(T) == typeof(Color))
        {
            var a = Cast<T, Color>(from);
            var b = Cast<T, Color>(to);
            var mixed = new Color(
                float.Lerp(a.R, b.R, amount),
                float.Lerp(a.G, b.G, amount),
                float.Lerp(a.B, b.B, amount),
                float.Lerp(a.A, b.A, amount));
            return Cast<Color, T>(mixed);
        }

        throw new NotSupportedException($"A tween can't blend {typeof(T).Name}. Use float, Vector2, Vector3, Vector4, Quaternion or Color.");
    }

    internal static float Forward(float elapsed, float seconds)
    {
        if (!(seconds >= 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A tween only moves forward, by a finite number of seconds.");
        }

        return elapsed + seconds;
    }

    private static TTo Cast<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);
}

/// <summary>
/// A value going from one place to another over some seconds. It's a plain struct, so
/// keep it in your game's state, call <see cref="Update"/> each tick and read
/// <see cref="Value"/>. It follows <see cref="GameTime.DeltaSeconds"/>, so it replays the
/// same from a seed and slows with the time scale. Make one with <see cref="Tween.Between(float, float, float, Easing)"/>.
/// </summary>
public struct Tween<T>
    where T : struct
{
    private float _elapsed;
    private float _lastStep;

    internal Tween(T from, T to, float seconds, Easing easing)
    {
        if (!(seconds > 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A tween must last more than zero seconds.");
        }

        From = from;
        To = to;
        Seconds = seconds;
        Easing = easing;
    }

    public T From { get; init; }

    public T To { get; init; }

    /// <summary>How long one play takes.</summary>
    public float Seconds { get; init; }

    /// <summary>Seconds to wait before starting, holding the start value.</summary>
    public float Delay { get; init; }

    /// <summary>How it gets going and comes to rest. A <see cref="Curve"/>, if set, is used instead.</summary>
    public Easing Easing { get; init; }

    /// <summary>A curve to follow instead of the easing, read from its first key to its last. 0 is the start value and 1 the end.</summary>
    public Curve? Curve { get; init; }

    public Repeat Repeat { get; init; }

    /// <summary>How many times it plays, each way counting once for a yoyo. 0 means forever.</summary>
    public int Times { get; init; }

    /// <summary>How long it lasts, delay included. Infinite if it repeats forever.</summary>
    public readonly float Duration => IsEndless ? float.PositiveInfinity : Delay + Seconds * Plays;

    /// <summary>Seconds since it began, delay included. Set it to jump to a moment.</summary>
    public float Elapsed
    {
        readonly get => _elapsed;
        set
        {
            _elapsed = Tween.Forward(0f, value);
            _lastStep = 0f;
        }
    }

    /// <summary>The value now.</summary>
    public readonly T Value => At(_elapsed);

    /// <summary>Past its delay and not done yet.</summary>
    public readonly bool IsPlaying => _elapsed >= Delay && !Completed;

    /// <summary>At the end and staying there. A tween that repeats forever never completes.</summary>
    public readonly bool Completed => !IsEndless && _elapsed >= Duration;

    private readonly bool IsEndless => Repeat != Repeat.Once && Times == 0;

    private readonly int Plays => Repeat == Repeat.Once ? 1 : Times;

    /// <summary>Waits this many seconds before starting.</summary>
    public readonly Tween<T> After(float seconds)
    {
        if (!(seconds >= 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A tween's delay must be a finite number of at least zero.");
        }

        return this with { Delay = seconds };
    }

    /// <summary>Uses an easing.</summary>
    public readonly Tween<T> Ease(Easing easing) => this with { Easing = easing, Curve = null };

    /// <summary>Follows a curve instead of an easing, such as one an artist tuned in content.</summary>
    public readonly Tween<T> Ease(Curve curve) => this with { Curve = curve ?? throw new ArgumentNullException(nameof(curve)) };

    /// <summary>Plays this many times, jumping back to the start each time. 0 means forever.</summary>
    public readonly Tween<T> Loop(int times = 0) => Repeating(Repeat.Loop, times);

    /// <summary>Plays to the end and back. Each way counts as one of the times, so 2 is there and back. 0 means forever.</summary>
    public readonly Tween<T> Yoyo(int times = 0) => Repeating(Repeat.Yoyo, times);

    /// <summary>Moves on by this tick's <see cref="GameTime.DeltaSeconds"/>.</summary>
    public void Update(GameTime time) => Advance(time.DeltaSeconds);

    /// <summary>Moves on by some seconds.</summary>
    public void Advance(float seconds)
    {
        float before = _elapsed;
        _elapsed = Math.Min(Tween.Forward(_elapsed, seconds), Duration);
        _lastStep = _elapsed - before;

        // A tween that runs forever would lose precision as the seconds pile up, so it
        // drops whole there-and-back trips, keeping one so Smooth can look back.
        float trip = 2f * Seconds;
        if (IsEndless && _elapsed - Delay >= 2f * trip)
        {
            _elapsed -= MathF.Floor((_elapsed - Delay) / trip - 1f) * trip;
        }
    }

    /// <summary>Goes back to the beginning, delay and all.</summary>
    public void Restart() => Elapsed = 0f;

    /// <summary>
    /// The value between the last tick and this one, for drawing: pass
    /// <see cref="View.Blend"/>, so motion stays smooth at any frame rate.
    /// </summary>
    public readonly T Smooth(float blend) => At(_elapsed - (1f - Math.Clamp(blend, 0f, 1f)) * _lastStep);

    /// <summary>The value this tween has at that many seconds from its beginning. It doesn't move the tween.</summary>
    public readonly T At(float elapsed)
    {
        if (!(Seconds > 0f))
        {
            return To;
        }

        float local = elapsed - Delay;
        float amount;
        bool backwards;
        if (local <= 0f)
        {
            amount = 0f;
            backwards = false;
        }
        else if (!IsEndless && local >= Seconds * Plays)
        {
            amount = 1f;
            backwards = Repeat == Repeat.Yoyo && Plays % 2 == 0;
        }
        else
        {
            float plays = MathF.Floor(local / Seconds);
            amount = local / Seconds - plays;
            backwards = Repeat == Repeat.Yoyo && plays % 2f == 1f;
        }

        if (backwards)
        {
            amount = 1f - amount;
        }

        float shaped = Curve is { } curve ? curve.Sample(amount) : Super.Ease.Apply(Easing, amount);
        return Tween.Mix(From, To, shaped);
    }

    private readonly Tween<T> Repeating(Repeat repeat, int times)
    {
        if (times < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(times), times, "A tween can't play a negative number of times. Use 0 for forever.");
        }

        return this with { Repeat = repeat, Times = times };
    }
}

/// <summary>Tweens played one after another, as one value. Make one with <see cref="Tween.Sequence{T}"/>.</summary>
public struct TweenSequence<T>
    where T : struct
{
    private readonly Tween<T>[] _steps;
    private float _elapsed;
    private float _lastStep;

    internal TweenSequence(Tween<T>[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0)
        {
            throw new ArgumentException("A sequence needs at least one tween.", nameof(steps));
        }

        float duration = 0f;
        for (int i = 0; i < steps.Length; i++)
        {
            if (float.IsPositiveInfinity(steps[i].Duration))
            {
                throw new ArgumentException($"Tween {i} in the sequence repeats forever, so the ones after it would never play.", nameof(steps));
            }

            duration += steps[i].Duration;
        }

        _steps = [.. steps];
        Duration = duration;
    }

    /// <summary>The tweens, in the order they play.</summary>
    public readonly IReadOnlyList<Tween<T>> Steps => _steps ?? [];

    /// <summary>Every tween's duration added up.</summary>
    public float Duration { get; }

    /// <summary>Seconds since it began. Set it to jump to a moment.</summary>
    public float Elapsed
    {
        readonly get => _elapsed;
        set
        {
            _elapsed = Tween.Forward(0f, value);
            _lastStep = 0f;
        }
    }

    /// <summary>The value of the tween playing now.</summary>
    public readonly T Value => At(_elapsed);

    /// <summary>Past the first tween's delay and not done yet.</summary>
    public readonly bool IsPlaying => _steps is not null && _elapsed >= _steps[0].Delay && !Completed;

    public readonly bool Completed => _elapsed >= Duration;

    public void Update(GameTime time) => Advance(time.DeltaSeconds);

    public void Advance(float seconds)
    {
        float before = _elapsed;
        _elapsed = Math.Min(Tween.Forward(_elapsed, seconds), Duration);
        _lastStep = _elapsed - before;
    }

    public void Restart() => Elapsed = 0f;

    /// <inheritdoc cref="Tween{T}.Smooth"/>
    public readonly T Smooth(float blend) => At(_elapsed - (1f - Math.Clamp(blend, 0f, 1f)) * _lastStep);

    /// <summary>The value at that many seconds from the beginning. It doesn't move the sequence.</summary>
    public readonly T At(float elapsed)
    {
        if (_steps is null)
        {
            return default;
        }

        float start = 0f;
        for (int i = 0; i < _steps.Length - 1; i++)
        {
            float end = start + _steps[i].Duration;
            if (elapsed < end)
            {
                return _steps[i].At(elapsed - start);
            }

            start = end;
        }

        return _steps[^1].At(elapsed - start);
    }
}

/// <summary>Tweens played at the same time. Make one with <see cref="Tween.Parallel{T}"/>.</summary>
public struct TweenGroup<T>
    where T : struct
{
    private readonly Tween<T>[] _tweens;
    private float _elapsed;
    private float _lastStep;

    internal TweenGroup(Tween<T>[] tweens)
    {
        ArgumentNullException.ThrowIfNull(tweens);
        if (tweens.Length == 0)
        {
            throw new ArgumentException("A group needs at least one tween.", nameof(tweens));
        }

        _tweens = [.. tweens];
        Duration = _tweens.Max(tween => tween.Duration);
        Start = _tweens.Min(tween => tween.Delay);
    }

    /// <summary>How many tweens play together.</summary>
    public readonly int Count => _tweens?.Length ?? 0;

    /// <summary>The longest tween's duration. Infinite if one repeats forever.</summary>
    public float Duration { get; }

    private float Start { get; }

    /// <summary>Seconds since it began. Set it to jump to a moment.</summary>
    public float Elapsed
    {
        readonly get => _elapsed;
        set
        {
            _elapsed = Tween.Forward(0f, value);
            _lastStep = 0f;
        }
    }

    /// <summary>The value of one tween now, by its place in the group.</summary>
    public readonly T this[int index] => _tweens[index].At(_elapsed);

    /// <summary>One of the tweens has started, and not all are done.</summary>
    public readonly bool IsPlaying => _elapsed >= Start && !Completed;

    /// <summary>Every tween is done.</summary>
    public readonly bool Completed => _elapsed >= Duration;

    public void Update(GameTime time) => Advance(time.DeltaSeconds);

    public void Advance(float seconds)
    {
        float before = _elapsed;
        _elapsed = Math.Min(Tween.Forward(_elapsed, seconds), Duration);
        _lastStep = _elapsed - before;
    }

    public void Restart() => Elapsed = 0f;

    /// <summary>One tween's value between the last tick and this one, for drawing. Pass <see cref="View.Blend"/>.</summary>
    public readonly T Smooth(int index, float blend) => _tweens[index].At(_elapsed - (1f - Math.Clamp(blend, 0f, 1f)) * _lastStep);
}
