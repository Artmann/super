using System.Numerics;

namespace Super.Ecs;

/// <summary>
/// Where an entity is, which way it faces and how big it is. Keep it on entities that
/// gameplay moves, and <see cref="ViewExtensions.Models"/> draws them from it.
/// </summary>
public record struct Transform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static Transform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>A transform at a position, unrotated.</summary>
    public static Transform At(Vector3 position, float scale = 1f) => new(position, Quaternion.Identity, new Vector3(scale));

    /// <summary>
    /// A transform whose +z points along <paramref name="forward"/>, kept upright. Forward
    /// needn't be one unit long.
    /// </summary>
    public static Transform Facing(Vector3 position, Vector3 forward, float scale = 1f) =>
        Facing(position, forward, Vector3.UnitY, new Vector3(scale));

    /// <summary>A transform whose +z points along <paramref name="forward"/> and whose +y leans towards <paramref name="up"/>.</summary>
    public static Transform Facing(Vector3 position, Vector3 forward, Vector3 up, Vector3 scale)
    {
        forward = Vector3.Normalize(forward);
        var side = Vector3.Cross(up, forward);
        side = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.UnitX;
        up = Vector3.Cross(forward, side);
        var rotation = new Matrix4x4(
            side.X, side.Y, side.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return new Transform(position, Quaternion.CreateFromRotationMatrix(rotation), scale);
    }

    /// <summary>The transform as a matrix: scale, then rotation, then position.</summary>
    public readonly Matrix4x4 Matrix
    {
        get
        {
            var matrix = Matrix4x4.CreateFromQuaternion(Rotation);
            matrix.M11 *= Scale.X;
            matrix.M12 *= Scale.X;
            matrix.M13 *= Scale.X;
            matrix.M21 *= Scale.Y;
            matrix.M22 *= Scale.Y;
            matrix.M23 *= Scale.Y;
            matrix.M31 *= Scale.Z;
            matrix.M32 *= Scale.Z;
            matrix.M33 *= Scale.Z;
            matrix.M41 = Position.X;
            matrix.M42 = Position.Y;
            matrix.M43 = Position.Z;
            return matrix;
        }
    }

    /// <summary>Blends between two transforms. It suits small steps, not whole turns in one tick.</summary>
    public static Transform Lerp(in Transform from, in Transform to, float amount) => new(
        Vector3.Lerp(from.Position, to.Position, amount),
        Quaternion.Lerp(from.Rotation, to.Rotation, amount),
        Vector3.Lerp(from.Scale, to.Scale, amount));
}
