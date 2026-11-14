using System.Numerics;

namespace Super.Physics;

internal static class Turns
{
    /// <summary>A rotation as an axis whose length is its angle in radians, the short way round.</summary>
    public static Vector3 AxisAngle(Quaternion turn)
    {
        if (turn.W < 0f)
        {
            turn = -turn;
        }

        var axis = new Vector3(turn.X, turn.Y, turn.Z);
        float sine = axis.Length();
        if (sine < 1e-7f)
        {
            return axis * 2f;
        }

        return axis * (2f * MathF.Atan2(sine, turn.W) / sine);
    }
}
