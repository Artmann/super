using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class BakeFilesTests
{
    [Fact]
    public void A_turn_and_a_move_is_rigid()
    {
        var transform = Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f) * Matrix4x4.CreateTranslation(5f, -2f, 9f);

        Assert.True(BakeFiles.IsRigid(transform));
    }

    [Fact]
    public void Rows_within_the_tolerance_of_unit_length_are_rigid()
    {
        var transform = Matrix4x4.Identity;
        transform.M11 = 1.00005f;

        Assert.True(BakeFiles.IsRigid(transform));
    }

    [Theory]
    [InlineData("scaled")]
    [InlineData("mirrored")]
    [InlineData("sheared")]
    [InlineData("projected")]
    [InlineData("not a number")]
    [InlineData("infinite move")]
    public void A_transform_that_does_more_than_turn_and_move_is_not_rigid(string kind)
    {
        var transform = Matrix4x4.Identity;
        switch (kind)
        {
            case "scaled":
                transform.M22 = 1.001f;
                break;
            case "mirrored":
                transform.M33 = -1f;
                break;
            case "sheared":
                transform.M21 = 0.001f;
                break;
            case "projected":
                transform.M24 = 0.5f;
                break;
            case "not a number":
                transform.M42 = float.NaN;
                break;
            default:
                transform.M41 = float.PositiveInfinity;
                break;
        }

        Assert.False(BakeFiles.IsRigid(transform));
    }
}
