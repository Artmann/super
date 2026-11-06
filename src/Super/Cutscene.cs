using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>Where a shot is: how far through it, from 0 to 1 and eased, and the seconds since it began.</summary>
public readonly record struct ShotTime(float Amount, float Seconds);

/// <summary>
/// One camera move in a <see cref="Cutscene"/>, from one cut to the next. Moves that
/// follow something take a function for where it is, which is asked every tick, so the
/// camera keeps up with a character as it runs.
/// </summary>
public sealed class Shot
{
    private readonly Func<ShotTime, Camera> _camera;
    private readonly Easing _easing;

    private Shot(float seconds, Easing easing, Func<ShotTime, Camera> camera)
    {
        if (!(seconds > 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A shot must last more than zero seconds.");
        }

        Seconds = seconds;
        _easing = easing;
        _camera = camera;
    }

    public float Seconds { get; }

    /// <summary>A camera that doesn't move.</summary>
    public static Shot Hold(float seconds, Camera camera) => new(seconds, Easing.Linear, _ => camera);

    /// <summary>
    /// Moves from one camera to another: where it is, what it looks at and its field of
    /// view. That's a crane when it rises, a dolly when it rolls in, and a pan when only the
    /// target moves.
    /// </summary>
    public static Shot Move(float seconds, Camera from, Camera to, Easing easing = Easing.InOut) =>
        new(seconds, easing, time => Camera.Lerp(from, to, time.Amount));

    /// <summary>
    /// Rides along with something that moves, sliding from one offset to another while
    /// looking at a point above it. Offsets are in the world's directions, not the subject's.
    /// </summary>
    public static Shot Track(
        float seconds,
        Func<Vector3> subject,
        Vector3 from,
        Vector3 to,
        Vector3 look = default,
        float fieldOfView = MathF.PI / 4f,
        Easing easing = Easing.InOut) =>
        new(seconds, easing, time =>
        {
            var at = subject();
            return new Camera(at + Vector3.Lerp(from, to, time.Amount), at + look, fieldOfView);
        });

    /// <summary>
    /// Circles something, from one angle round it to another, in radians, where 0 looks
    /// from +z and a quarter turn looks from +x. Height is above the point looked at.
    /// </summary>
    public static Shot Orbit(
        float seconds,
        Func<Vector3> subject,
        float radius,
        float height,
        float fromAngle,
        float toAngle,
        Vector3 look = default,
        float fieldOfView = MathF.PI / 4f,
        Easing easing = Easing.InOut) =>
        new(seconds, easing, time =>
        {
            var center = subject() + look;
            float angle = float.Lerp(fromAngle, toAngle, time.Amount);
            var offset = new Vector3(MathF.Sin(angle) * radius, height, MathF.Cos(angle) * radius);
            return new Camera(center + offset, center, fieldOfView);
        });

    /// <summary>
    /// Rides a camera along a rail, from its start to its end, easing on the distance along
    /// it so the speed stays even. It looks at the subject, raised by <paramref name="look"/>,
    /// or without one, straight ahead along the rail.
    /// </summary>
    public static Shot Along(
        float seconds,
        Spline rail,
        Func<Vector3>? subject = null,
        Vector3 look = default,
        float fieldOfView = MathF.PI / 4f,
        Easing easing = Easing.InOut)
    {
        ArgumentNullException.ThrowIfNull(rail);
        return new(seconds, easing, time =>
        {
            var pose = rail.FrameAtDistance(time.Amount * rail.Length);
            var target = subject is null ? pose.Position + pose.Forward : subject() + look;
            return new Camera(pose.Position, target, fieldOfView);
        });
    }

    /// <summary>Any move at all: the function gets how far through the shot it is and gives the camera.</summary>
    public static Shot Custom(float seconds, Func<ShotTime, Camera> camera, Easing easing = Easing.Linear) =>
        new(seconds, easing, camera);

    /// <summary>The camera this far into the shot, in seconds.</summary>
    internal Camera CameraAt(float seconds) =>
        _camera(new ShotTime(Ease.Apply(_easing, seconds / Seconds), seconds));
}

/// <summary>
/// A short film made of the game itself: shots one after another, each a camera move,
/// with things that happen at set moments, such as a character drawing a bow, and
/// stretches of slow motion. Build one, then give it to <see cref="Director.Play"/>.
/// Its clock counts real ticks, so the camera keeps its pace while the world slows.
/// </summary>
public sealed class Cutscene
{
    private readonly List<(float Start, Shot Shot)> _shots = [];
    private readonly List<(float At, Action Action)> _cues = [];
    private readonly List<(float From, float To, float Scale, float Ramp)> _slows = [];
    private readonly List<(float At, Transition Transition, float Skip)> _transitions = [];
    private Transition? _end;

    /// <summary>How long the cutscene lasts: the shots end to end.</summary>
    public float Seconds { get; private set; }

    internal int ShotCount => _shots.Count;

    /// <summary>Adds a shot after the last one. Moving from one shot to the next is a cut.</summary>
    public Cutscene Then(Shot shot)
    {
        if (_end is not null)
        {
            throw new InvalidOperationException("The cutscene's end is already set. Add every shot before End.");
        }

        _shots.Add((Seconds, shot));
        Seconds += shot.Seconds;
        return this;
    }

    /// <summary>
    /// Adds a shot after the last one, moving into it with <paramref name="transition"/>.
    /// A fade is centred on the change, so its first half plays over the end of the shot
    /// before; on the first shot, it fades in from its colour. A blend into the first shot
    /// starts from the game's camera. The others start as the shot does.
    /// </summary>
    public Cutscene Then(Shot shot, Transition transition)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(transition);
        float start = Seconds;
        float half = transition.Seconds / 2f;
        bool fade = transition.Kind == TransitionKind.Fade;
        if (fade && _shots.Count > 0 && half > _shots[^1].Shot.Seconds)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The fade's first half, {half:0.###} seconds, must fit in the shot before it, which lasts {_shots[^1].Shot.Seconds:0.###}."),
                nameof(transition));
        }

        float into = fade ? half : transition.Seconds;
        if (into > shot.Seconds)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The transition's {into:0.###} seconds into the shot must fit in it, and it lasts {shot.Seconds:0.###}."),
                nameof(transition));
        }

        Then(shot);
        if (transition.Kind == TransitionKind.Cut)
        {
            return this;
        }

        if (fade)
        {
            _transitions.Add(start == 0f ? (0f, transition, half) : (start - half, transition, 0f));
        }
        else
        {
            _transitions.Add((start, transition, 0f));
        }

        return this;
    }

    /// <summary>
    /// Moves back to the game with <paramref name="transition"/> when the cutscene runs to
    /// its end. A fade goes out over the last shot and comes in on the game. Stopping it
    /// early skips this. Add it after the last shot.
    /// </summary>
    public Cutscene End(Transition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (_shots.Count == 0)
        {
            throw new InvalidOperationException("The cutscene has no shots to end. Add them with Then first.");
        }

        float half = transition.Seconds / 2f;
        if (transition.Kind == TransitionKind.Fade && half > _shots[^1].Shot.Seconds)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The fade's first half, {half:0.###} seconds, must fit in the last shot, which lasts {_shots[^1].Shot.Seconds:0.###}."),
                nameof(transition));
        }

        _end = transition;
        if (transition.Kind == TransitionKind.Fade)
        {
            _transitions.Add((Seconds - half, transition, 0f));
        }

        return this;
    }

    /// <summary>What plays when the cutscene ends on its own and isn't a fade, which starts before the end.</summary>
    internal Transition? EndTransition => _end is { Kind: not TransitionKind.Fade } end ? end : null;

    /// <summary>Starts the transitions due after <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
    internal void StartTransitions(float from, float to, Transitions transitions)
    {
        foreach (var (at, transition, skip) in _transitions)
        {
            if (from < at && at <= to)
            {
                transitions.Start(transition, null, skip);
            }
        }
    }

    /// <summary>
    /// Does something at a moment, in seconds from the start, during that tick's Update
    /// so the game sees it at once. Moments that share a tick go in the order they were added.
    /// </summary>
    public Cutscene At(float seconds, Action action)
    {
        if (!(seconds >= 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A moment must be at least zero seconds in.");
        }

        _cues.Add((seconds, action));
        return this;
    }

    /// <summary>
    /// Slows the world to <paramref name="scale"/> from one moment to another, easing in
    /// and out over <paramref name="ramp"/> seconds at each end so it doesn't jolt.
    /// </summary>
    public Cutscene Slow(float from, float to, float scale, float ramp = 0.25f)
    {
        if (!(to > from) || !(from >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, $"Slow motion must end after it starts, at {from} seconds or later.");
        }

        if (!(scale >= 0f) || !float.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The time scale must be a number of at least zero.");
        }

        if (!(ramp >= 0f) || ramp * 2f > to - from)
        {
            throw new ArgumentOutOfRangeException(nameof(ramp), ramp, "The ramps at each end must fit inside the slow motion.");
        }

        _slows.Add((from, to, scale, ramp));
        return this;
    }

    /// <summary>The shot playing at a moment and how far into it, or the last shot at its end.</summary>
    internal (int Index, float Into) ShotAt(float seconds)
    {
        if (_shots.Count == 0)
        {
            throw new InvalidOperationException("The cutscene has no shots. Add one with Then.");
        }

        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            if (seconds >= _shots[i].Start)
            {
                return (i, MathF.Min(seconds - _shots[i].Start, _shots[i].Shot.Seconds));
            }
        }

        return (0, 0f);
    }

    internal Camera CameraAt(float seconds)
    {
        var (index, into) = ShotAt(seconds);
        return _shots[index].Shot.CameraAt(into);
    }

    /// <summary>Runs the moments after <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
    internal void Cue(float from, float to)
    {
        foreach (var (at, action) in _cues)
        {
            if (from < at && at <= to)
            {
                action();
            }
        }
    }

    /// <summary>How fast the world moves at a moment: 1 outside slow motion.</summary>
    internal float ScaleAt(float seconds)
    {
        float scale = 1f;
        foreach (var (from, to, slow, ramp) in _slows)
        {
            if (seconds < from || seconds >= to)
            {
                continue;
            }

            float into = ramp > 0f ? Math.Clamp(MathF.Min(seconds - from, to - seconds) / ramp, 0f, 1f) : 1f;
            scale = float.Lerp(1f, slow, Ease.Apply(Easing.InOut, into));
        }

        return scale;
    }
}
