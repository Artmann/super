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
