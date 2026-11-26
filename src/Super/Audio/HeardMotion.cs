using System.Numerics;

namespace Super;

/// <summary>
/// How fast something that makes or hears sound is moving, worked out on the game thread from where it was on each
/// frame and the time on screen. The speed is smoothed over about 0.1 s, so an uneven frame doesn't wobble the pitch,
/// and a jump faster than <see cref="TeleportSpeed"/>, such as a teleport or a camera cut, starts it again from still
/// rather than spiking the pitch.
/// </summary>
internal struct HeardMotion
{
    /// <summary>A move faster than this, in metres a second, is a jump: the velocity starts again from 0.</summary>
    public const float TeleportSpeed = 100f;

    // The smoothing's time constant, in seconds.
    private const double SmoothingSeconds = 0.1;

    // Below this speed, in metres a second, the smoothed velocity is taken as still, so it stops being sent.
    private const float StillSpeed = 0.01f;

    private bool _known;
    private Vector3 _last;
    private double _lastTime;

    /// <summary>The smoothed velocity, in metres a second on screen.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>Takes where it is at <paramref name="now"/>, in seconds on screen, and returns the new velocity.</summary>
    public Vector3 Update(Vector3 at, double now)
    {
        if (!_known)
        {
            _known = true;
            _last = at;
            _lastTime = now;
            Velocity = Vector3.Zero;
            return Velocity;
        }

        double seconds = now - _lastTime;
        Vector3 moved = at - _last;
        _last = at;
        if (!(seconds > 1e-6))
        {
            // No time passed, so any move at all was a jump.
            if (moved != Vector3.Zero)
            {
                Velocity = Vector3.Zero;
            }

            return Velocity;
        }

        _lastTime = now;
        Vector3 raw = moved / (float)seconds;
        if (raw.Length() > TeleportSpeed)
        {
            Velocity = Vector3.Zero;
            return Velocity;
        }

        float keep = (float)Math.Exp(-seconds / SmoothingSeconds);
        Vector3 smoothed = raw + (Velocity - raw) * keep;
        Velocity = smoothed.Length() < StillSpeed ? Vector3.Zero : smoothed;
        return Velocity;
    }
}
