namespace Super.Materials.Tests;

public class SurfaceNodeTests
{
    static float[] SineInU(int size)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                values[y * size + x] = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * (x + 0.5f) / size);
            }
        }

        return values;
    }

    static float[] RampTowardsRowZero(int size)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                values[y * size + x] = 0.25f + 0.5f * (size - 1 - y) / (size - 1f);
            }
        }

        return values;
    }

    static float[] ConePit(int size)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size - 0.5f;
                var dy = (y + 0.5f) / size - 0.5f;

                values[y * size + x] = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy) / 0.25f);
            }
        }

        return values;
    }

    [Fact]
    public void A_flat_height_points_straight_out()
    {
        var n = GraphRunner.Evaluate(() => Gray.Of(0.3f).HeightToNormal(1f, 0.1f), 16);

        for (var i = 0; i < 16 * 16; i++)
        {
            Assert.Equal(0f, n[i * 3]);
            Assert.Equal(0f, n[i * 3 + 1]);
            Assert.Equal(1f, n[i * 3 + 2]);
        }
    }

    [Fact]
    public void A_slope_in_metres_tilts_the_normal_by_the_hand_worked_amount()
    {
        // h = 0.5 + 0.5 sin(2 pi u) at size 64, width 1 m, depth 0.1 m. At u near 0 the slope is
        // 0.5 x 2 pi x 0.1 = 0.314, shortened by the finite difference to 0.3138,
        // so x = -0.3138 / sqrt(1 + 0.3138^2) = -0.2994.
        var n = GraphRunner.Evaluate(() => Gray.FromArray(SineInU(64)).HeightToNormal(1f, 0.1f), 64);

        Assert.Equal(-0.2994f, n[(10 * 64 + 0) * 3], 2);
    }

    [Fact]
    public void A_wider_image_makes_the_same_height_less_steep()
    {
        var narrow = GraphRunner.Evaluate(() => Gray.FromArray(SineInU(64)).HeightToNormal(1f, 0.1f), 64);
        var wide = GraphRunner.Evaluate(() => Gray.FromArray(SineInU(64)).HeightToNormal(2f, 0.1f), 64);

        // Slope 0.1569 over twice the width: x = -0.1569 / sqrt(1 + 0.1569^2) = -0.155, shortened a little by the finite difference.
        Assert.Equal(-0.1548f, wide[(10 * 64 + 0) * 3], 3);
        Assert.True(MathF.Abs(wide[(10 * 64) * 3]) < MathF.Abs(narrow[(10 * 64) * 3]));
    }

    [Fact]
    public void Height_rising_towards_the_top_of_the_image_turns_green_down()
    {
        // h rises as the row number falls. The face looks down the image, so y < 0.
        var n = GraphRunner.Evaluate(() => Gray.FromArray(RampTowardsRowZero(64)).HeightToNormal(1f, 0.1f), 64);

        Assert.True(n[(32 * 64 + 32) * 3 + 1] < 0f);
    }

    [Fact]
    public void Height_rising_to_the_right_turns_red_left()
    {
        var n = GraphRunner.Evaluate(() => Gray.FromArray(SineInU(64)).HeightToNormal(1f, 0.1f), 64);

        Assert.True(n[(10 * 64 + 0) * 3] < 0f);
        Assert.True(n[(10 * 64 + 32) * 3] > 0f);
    }

    [Fact]
    public void A_negative_width_for_the_normal_names_the_property()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).HeightToNormal(-1f, 0.1f), 16));

        Assert.StartsWith("Width must be more than 0, not -1.", error.Message);
    }

    [Fact]
    public void Reorienting_a_flat_detail_returns_the_base_and_a_tilted_one_tilts_it()
    {
        var flat = NormalMath.Reorient((0.6f, 0f, 0.8f), (0f, 0f, 1f));

        Assert.Equal(0.6f, flat.X, 4);
        Assert.Equal(0f, flat.Y, 4);
        Assert.Equal(0.8f, flat.Z, 4);

        // A base that points straight out takes the detail as it is.
        var onFlat = NormalMath.Reorient((0f, 0f, 1f), (0.28f, -0.6f, 0.7483f));

        Assert.Equal(0.28f, onFlat.X, 3);
        Assert.Equal(-0.6f, onFlat.Y, 3);
    }

    [Fact]
    public void A_flat_height_is_not_occluded()
    {
        var o = GraphRunner.Evaluate(() => Gray.Of(0.5f).HeightToOcclusion(1f, 0.1f), 32);

        Assert.All(o, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void The_bottom_of_a_pit_is_darker_than_its_rim()
    {
        var o = GraphRunner.Evaluate(() => Gray.FromArray(ConePit(64)).HeightToOcclusion(1f, 1f), 64);

        var centre = o[32 * 64 + 32];
        var rim = o[32 * 64 + 8];

        Assert.True(centre < 0.8f * rim, $"Centre {centre} is not darker than rim {rim}.");
    }
}
