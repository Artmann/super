using System.Numerics;

namespace Super.Ecs.Tests;

public class TransformTests
{
    private const float Tolerance = 0.0001f;

    private static void AssertEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }

    [Fact]
    public void Matrix_scales_then_rotates_then_moves()
    {
        var transform = new Transform(new Vector3(1f, 2f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), new Vector3(2f, 3f, 4f));
        var expected = Matrix4x4.CreateScale(2f, 3f, 4f)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(1f, 2f, 3f);

        var matrix = transform.Matrix;

        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Assert.Equal(expected[row, column], matrix[row, column], Tolerance);
            }
        }
    }

    [Fact]
    public void Facing_points_z_along_forward_and_keeps_y_up()
    {
        var transform = Transform.Facing(Vector3.Zero, new Vector3(5f, 0f, 0f), scale: 2f);

        var matrix = transform.Matrix;
        AssertEqual(new Vector3(2f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitZ, matrix));
        AssertEqual(new Vector3(0f, 2f, 0f), Vector3.TransformNormal(Vector3.UnitY, matrix));
    }

    [Fact]
    public void Facing_straight_up_still_gives_a_rotation()
    {
        var transform = Transform.Facing(Vector3.Zero, Vector3.UnitY);

        AssertEqual(Vector3.UnitY, Vector3.Transform(Vector3.UnitZ, transform.Rotation));
        Assert.Equal(1f, transform.Rotation.Length(), Tolerance);
    }

    [Fact]
    public void Lerp_blends_position_rotation_and_scale()
    {
        var from = new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var to = new Transform(new Vector3(4f, 0f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), new Vector3(3f));

        var half = Transform.Lerp(in from, in to, 0.5f);

        AssertEqual(new Vector3(2f, 0f, 0f), half.Position);
        AssertEqual(new Vector3(2f), half.Scale);
        var forward = Vector3.Transform(Vector3.UnitZ, half.Rotation);
        Assert.Equal(MathF.PI / 4f, MathF.Acos(Vector3.Dot(forward, Vector3.UnitZ)), 0.001f);
    }
}
