using System.Numerics;

namespace Super;

/// <summary>How loud a sound in the world is and which side it comes from.</summary>
internal static class Spatial
{
    /// <summary>
    /// 1 within <paramref name="near"/>, 0 from <paramref name="far"/> on, and in between the inverse-distance
    /// curve near/d, shifted and stretched so it reaches 0 exactly at far.
    /// </summary>
    public static float Falloff(float distance, float near, float far)
    {
        if (distance <= near)
        {
            return 1;
        }

        if (distance >= far)
        {
            return 0;
        }

        float atFar = near / far;
        return (near / distance - atFar) / (1 - atFar);
    }

    /// <summary>-1 for a source straight to the listener's left, 1 straight to the right, 0 ahead, behind or at the listener.</summary>
    public static float Pan(Vector3 listener, Vector3 forward, Vector3 source)
    {
        Vector3 toSource = source - listener;
        float distance = toSource.Length();
        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
        float rightLength = right.Length();
        if (distance < 1e-6f || rightLength < 1e-6f)
        {
            return 0;
        }

        float pan = Vector3.Dot(toSource / distance, right / rightLength);
        return Math.Clamp(pan, -1, 1);
    }

    /// <summary>The speed of sound in air, in metres a second.</summary>
    public const float SpeedOfSound = 343f;

    /// <summary>The lowest and highest pitch the Doppler effect can give, an octave each way.</summary>
    public const float LowestDoppler = 0.5f;
    public const float HighestDoppler = 2f;

    /// <summary>
    /// How much the Doppler effect raises the pitch of a sound at <paramref name="source"/>, heard from
    /// <paramref name="listener"/>: (c + the listener's speed towards the source) / (c + the source's speed away from
    /// the listener), kept from 0.5 to 2. 1 when neither moves along the line between them, or when they're in the
    /// same place.
    /// </summary>
    public static float Doppler(Vector3 listener, Vector3 listenerVelocity, Vector3 source, Vector3 sourceVelocity)
    {
        Vector3 toSource = source - listener;
        float distance = toSource.Length();
        if (distance < 1e-4f)
        {
            return 1;
        }

        Vector3 direction = toSource / distance;
        float listenerTowards = Math.Clamp(Vector3.Dot(listenerVelocity, direction), -SpeedOfSound, SpeedOfSound);

        // A source can't outrun its own sound: at half the speed of sound towards the listener the pitch is
        // already past the highest it can go.
        float sourceAway = Math.Clamp(Vector3.Dot(sourceVelocity, direction), -SpeedOfSound / 2, SpeedOfSound);
        float factor = (SpeedOfSound + listenerTowards) / (SpeedOfSound + sourceAway);
        return Math.Clamp(factor, LowestDoppler, HighestDoppler);
    }

    /// <summary>Gains for each side that keep the total power the same wherever the sound is panned.</summary>
    public static (float Left, float Right) EqualPower(float pan)
    {
        float angle = (pan + 1) * MathF.PI / 4;
        return (MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>
    /// Gains for a flat stereo voice panned by a balance control: both 1 at the centre, and panning to one side
    /// turns the other side down along a quarter circle to 0 at the edge.
    /// </summary>
    public static (float Left, float Right) Balance(float pan)
    {
        float turn = MathF.Abs(Math.Clamp(pan, -1, 1)) * MathF.PI / 2;
        float far = MathF.Cos(turn);
        return pan > 0 ? (far, 1) : (1, far);
    }
}
