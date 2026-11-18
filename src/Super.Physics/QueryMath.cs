using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>Checks a query's reach and turn, and places its shape the way Jolt does.</summary>
internal static class QueryMath
{
    public static void CheckReach(float reach)
    {
        if (!Numbers.IsAboveZero(reach))
        {
            throw new ArgumentOutOfRangeException(nameof(reach), reach, "A query's reach must be a finite distance above 0.");
        }
    }

    public static Quaternion Turn(Quaternion? rotation)
    {
        if (rotation is not { } turn)
        {
            return Quaternion.Identity;
        }

        if (!(turn.LengthSquared() > 0f) || !float.IsFinite(turn.X + turn.Y + turn.Z + turn.W))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "A query's rotation must be a finite quaternion that isn't zero.");
        }

        return Quaternion.Normalize(turn);
    }

    /// <summary>
    /// Where a shape's centre of mass is, turned, with its origin at <paramref name="position"/>: how Jolt places
    /// shapes. Jolt's matrices act on column vectors, with the move in the last column, so this is the transpose of
    /// System.Numerics' own.
    /// </summary>
    public static Matrix4x4 CentreOfMass(Shape shape, Vector3 position, Quaternion rotation) =>
        Placed(position + Vector3.Transform(shape.CenterOfMass, rotation), rotation);

    /// <summary>A turn and a move, as a Jolt matrix.</summary>
    public static Matrix4x4 Placed(Vector3 position, Quaternion rotation)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        matrix.Translation = position;
        return Matrix4x4.Transpose(matrix);
    }
}
