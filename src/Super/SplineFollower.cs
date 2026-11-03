using System.Numerics;

namespace Super;

/// <summary>What a <see cref="SplineFollower"/> does when it reaches the end.</summary>
public enum SplineLoop
{
    /// <summary>Stops at the end.</summary>
    Once,

    /// <summary>Goes round again: on a closed spline it carries on, and on an open one it jumps back to the start.</summary>
    Loop,

    /// <summary>Turns round at each end and goes back.</summary>
    PingPong,
}

/// <summary>
/// Moves something along a <see cref="Spline"/> at a steady speed in metres per second,
/// such as a cart on a rail or a guard on a patrol. It's a struct you keep in your state
/// and move each tick with <see cref="Update"/>, so it replays the same with a seed.
/// It always faces the way it's going, turning round when it goes back.
/// </summary>
public struct SplineFollower
{
    private float _travelled;
    private float _lastStep;

    /// <summary>A follower <paramref name="distance"/> metres along the spline, moving at <paramref name="speed"/>.</summary>
    public SplineFollower(Spline spline, float speed, SplineLoop loop = SplineLoop.Once, float distance = 0f)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (!float.IsFinite(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The speed must be a finite number.");
        }

        if (!float.IsFinite(distance))
        {
            throw new ArgumentOutOfRangeException(nameof(distance), distance, "The distance must be a finite number.");
        }

        Spline = spline;
        Speed = speed;
        Loop = loop;
        _travelled = Settle(distance);
    }

    public Spline Spline { get; }

    /// <summary>Metres per second. Below zero it goes back towards the start.</summary>
    public float Speed { get; set; }

    public SplineLoop Loop { get; }

    /// <summary>How far along the spline it is, in metres.</summary>
    public readonly float Distance => Place(_travelled).Distance;

    /// <summary>Whether it's heading back towards the start.</summary>
    public readonly bool Backwards => Place(_travelled).Backwards;

    /// <summary>True once a follower that plays once has reached its end. One that loops never is.</summary>
    public readonly bool Completed => Loop == SplineLoop.Once && (Speed >= 0f ? _travelled >= Spline.Length : _travelled <= 0f);

    /// <summary>Where it is and which way it faces.</summary>
    public readonly SplinePose Pose => PoseAt(_travelled);

    /// <summary>Where it is.</summary>
    public readonly Vector3 Position => Pose.Position;

    /// <summary>Moves on by the tick's game time, so slow motion slows it too.</summary>
    public void Update(GameTime time) => Advance(time.DeltaSeconds);

    /// <summary>Moves on by some seconds.</summary>
    public void Advance(float seconds)
    {
        float step = Speed * seconds;
        float before = _travelled;
        _travelled = Settle(_travelled + step);
        _lastStep = Loop == SplineLoop.Once ? _travelled - before : step;
    }

    /// <summary>Where it is between the last tick and this one, for drawing. See smooth motion in the views guide.</summary>
    public readonly SplinePose Smooth(float blend) => PoseAt(_travelled - (1f - Math.Clamp(blend, 0f, 1f)) * _lastStep);

    /// <summary>Keeps the distance travelled inside one trip, so it never grows large enough to lose precision.</summary>
    private readonly float Settle(float travelled)
    {
        float length = Spline.Length;
        return Loop switch
        {
            SplineLoop.Once => Math.Clamp(travelled, 0f, length),
            SplineLoop.Loop => Wrap(travelled, length),
            _ => Wrap(travelled, 2f * length),
        };
    }

    private readonly (float Distance, bool Backwards) Place(float travelled)
    {
        float length = Spline.Length;
        bool reversing = Speed < 0f;
        switch (Loop)
        {
            case SplineLoop.Once:
                return (Math.Clamp(travelled, 0f, length), reversing);
            case SplineLoop.Loop:
                return (Wrap(travelled, length), reversing);
            default:
                float trip = Wrap(travelled, 2f * length);
                return trip <= length ? (trip, reversing) : (2f * length - trip, !reversing);
        }
    }

    private readonly SplinePose PoseAt(float travelled)
    {
        var (distance, backwards) = Place(travelled);
        var pose = Spline.FrameAtDistance(distance);
        return backwards ? pose with { Rotation = pose.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) } : pose;
    }

    private static float Wrap(float value, float period) => value - MathF.Floor(value / period) * period;
}
