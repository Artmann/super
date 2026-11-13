using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public sealed class ShL1Tests
{
    private static Vector3 RandomDirection(Random random)
    {
        while (true)
        {
            var v = new Vector3(random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f);
            if (v.LengthSquared() is > 0.01f and <= 1f)
            {
                return Vector3.Normalize(v);
            }
        }
    }

    [Fact]
    public void Uniform_sky_projects_to_four_pi_y0()
    {
        var sky = ShL1.Uniform(1f);

        // 4 * pi * 0.2820948 = 3.5449077, worked by hand.
        Assert.Equal(3.5449077f, sky.C0, 1e-5f);
        Assert.Equal(0f, sky.C1);
        Assert.Equal(0f, sky.C2);
        Assert.Equal(0f, sky.C3);
    }

    [Fact]
    public void Plane_transfer_dotted_with_a_uniform_sky_gives_pi()
    {
        // A cosine lobe about +y: (pi Y0, pi Y1 2/3 on y, 0, 0), worked by hand.
        var transfer = new ShL1(0.886227f, 1.023327f, 0f, 0f);

        Assert.Equal(MathF.PI, ShL1.Dot(transfer, ShL1.Uniform(1f)), 1e-5f);
    }

    [Theory, Seeds(16)]
    public void Rotating_l1_keeps_its_value_in_the_turned_direction(int seed)
    {
        var random = new Random(seed);
        var axis = RandomDirection(random);
        var rotation = Matrix4x4.CreateFromAxisAngle(axis, random.NextSingle() * MathF.Tau);
        var sh = new ShL1(random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f, random.NextSingle() * 2f - 1f);
        var direction = RandomDirection(random);

        var turned = sh.Rotate(rotation);

        Assert.Equal(sh.Evaluate(direction), turned.Evaluate(Vector3.TransformNormal(direction, rotation)), 1e-5f);
    }

    [Fact]
    public void Quarter_turn_about_y_moves_x_to_minus_z()
    {
        var turned = ShL1.Direction(Vector3.UnitX, 1f).Rotate(Matrix4x4.CreateRotationY(MathF.PI / 2f));

        // Slots are (y, z, x). +x is slot 3 with Y1; turned to -z it sits in slot 2 with -Y1.
        Assert.Equal(0.282094792f, turned.C0, 1e-5f);
        Assert.Equal(0f, turned.C1, 1e-5f);
        Assert.Equal(-0.488602512f, turned.C2, 1e-5f);
        Assert.Equal(0f, turned.C3, 1e-5f);
    }

    [Fact]
    public void Rotate_throws_for_a_scale()
    {
        var scaled = Matrix4x4.CreateScale(2f);

        var error = Assert.Throws<ArgumentException>(() => new ShL1(1f, 0f, 0f, 0f).Rotate(scaled));

        Assert.Contains("must only turn", error.Message);
    }
}
