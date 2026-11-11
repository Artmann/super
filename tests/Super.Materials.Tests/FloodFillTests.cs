namespace Super.Materials.Tests;

public class FloodFillTests
{
    const int Size = 64;

    static int Distinct(float[] pixels, float background)
    {
        return pixels.Where(v => v != background).Distinct().Count();
    }

    static float[] Squares()
    {
        var pixels = new float[Size * Size];

        for (var y = 4; y < 12; y++)
        {
            for (var x = 4; x < 12; x++)
            {
                pixels[y * Size + x] = 1f;
            }
        }

        for (var y = 30; y < 38; y++)
        {
            for (var x = 40; x < 48; x++)
            {
                pixels[y * Size + x] = 1f;
            }
        }

        return pixels;
    }

    [Fact]
    public void A_brick_across_the_edge_is_one_island()
    {
        // Bricks(2, 2) with offset 0.5 at size 64: row 1's brick at x = 52..63 wraps to x = 0..11.
        var p = GraphRunner.Evaluate(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f).FloodFill().RandomGray(), Size);

        Assert.Equal(p[48 * Size + 2], p[48 * Size + 60]);
        Assert.Equal(4, Distinct(p, background: 0f));
    }

    [Fact]
    public void An_island_across_the_top_and_bottom_edges_is_one_island()
    {
        var pixels = new float[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            if (y < 3 || y >= Size - 3)
            {
                for (var x = 10; x < 14; x++)
                {
                    pixels[y * Size + x] = 1f;
                }
            }
        }

        var p = GraphRunner.Evaluate(() => Gray.FromArray(pixels).FloodFill().RandomGray(), Size);

        Assert.NotEqual(0f, p[1 * Size + 11]);
        Assert.Equal(p[1 * Size + 11], p[(Size - 2) * Size + 11]);
        Assert.Equal(1, Distinct(p, background: 0f));
    }

    [Fact]
    public void Separate_islands_get_different_values()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Squares()).FloodFill().RandomGray(), Size);

        var first = p[5 * Size + 5];
        var second = p[33 * Size + 42];

        Assert.NotEqual(0f, first);
        Assert.NotEqual(0f, second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, p[11 * Size + 11]);
        Assert.Equal(2, Distinct(p, background: 0f));
    }

    [Fact]
    public void Background_stays_zero()
    {
        var pixels = Squares();

        pixels[20 * Size + 20] = 0.5f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(pixels).FloodFill(threshold: 0.5f).RandomGray(), Size);

        Assert.Equal(0f, p[0]);
        Assert.Equal(0f, p[20 * Size + 20]);
        Assert.Equal(2, Distinct(p, background: 0f));
    }

    [Fact]
    public void Island_numbers_follow_row_order_and_background_is_minus_one()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Squares()).FloodFill(), Size);

        Assert.Equal(-1f, p[0]);
        Assert.Equal(0f, p[4 * Size + 4]);
        Assert.Equal(1f, p[30 * Size + 40]);
    }

    [Fact]
    public void Island_numbers_do_not_depend_on_thread_count()
    {
        float[] Run(int threads) => GraphRunner.Evaluate(() => Tiles.Bricks(4, 4, gap: 0.1f, bevel: 0f).FloodFill(), Size, maxThreads: threads);

        Assert.Equal(Run(1), Run(8));
    }

    [Fact]
    public void A_seed_changes_the_values_and_the_same_seed_repeats_them()
    {
        float[] Run(int? seed) => GraphRunner.Evaluate(() => Gray.FromArray(Squares()).FloodFill().RandomGray(seed), Size);

        Assert.Equal(Run(3), Run(3));
        Assert.NotEqual(Run(3)[5 * Size + 5], Run(4)[5 * Size + 5]);
    }

    [Fact]
    public void A_threshold_that_is_not_finite_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(1f).FloodFill(float.NaN), Size));

        Assert.Equal("threshold must be a finite number, not NaN. (Parameter 'threshold')", error.Message);
    }

    static float[] Box(int left, int top, int width, int height)
    {
        var pixels = new float[Size * Size];

        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                pixels[(y % Size) * Size + x % Size] = 1f;
            }
        }

        return pixels;
    }

    [Fact]
    public void To_gradient_runs_from_zero_at_the_left_pixel_to_one_at_the_right_pixel()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(10, 20, 16, 8)).FloodFill().ToGradient(), Size);

        Assert.Equal(0f, p[22 * Size + 10], 5);
        Assert.Equal(1f, p[22 * Size + 25], 5);
        Assert.Equal(7f / 15f, p[22 * Size + 17], 5);
        Assert.Equal(0f, p[22 * Size + 9], 5);
        Assert.Equal(0f, p[22 * Size + 26], 5);
        Assert.Equal(0f, p[5 * Size + 5], 5);
    }

    [Fact]
    public void To_gradient_at_ninety_degrees_rises_up_the_image()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(10, 20, 4, 8)).FloodFill().ToGradient(90f), Size);

        // Rows 20 to 27. The bottom row, 27, is 0 and the top row, 20, is 1. Row 23 is 4 of 7 of the way up.
        Assert.Equal(0f, p[27 * Size + 11], 3);
        Assert.Equal(1f, p[20 * Size + 11], 3);
        Assert.Equal(4f / 7f, p[23 * Size + 11], 3);
    }

    [Fact]
    public void To_gradient_at_forty_five_degrees_is_half_on_the_other_diagonal()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(10, 20, 8, 8)).FloodFill().ToGradient(45f), Size);

        Assert.Equal(0f, p[27 * Size + 10], 4);
        Assert.Equal(1f, p[20 * Size + 17], 4);
        Assert.Equal(0.5f, p[20 * Size + 10], 4);
        Assert.Equal(0.5f, p[27 * Size + 17], 4);
    }

    [Fact]
    public void To_gradient_measures_an_island_across_the_edge_as_one_box()
    {
        // Columns 60 to 63 and 0 to 3 are one island 8 wide. It runs from column 60 to column 3.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(60, 20, 8, 4)).FloodFill().ToGradient(), Size);

        Assert.Equal(0f, p[21 * Size + 60], 5);
        Assert.Equal(3f / 7f, p[21 * Size + 63], 5);
        Assert.Equal(4f / 7f, p[21 * Size + 0], 5);
        Assert.Equal(1f, p[21 * Size + 3], 5);
    }

    [Fact]
    public void To_gradient_measures_an_island_across_the_bottom_edge_as_one_box()
    {
        // Rows 62, 63, 0 and 1, climbing from row 1 (0) to row 62 (1).
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(5, 62, 3, 4)).FloodFill().ToGradient(90f), Size);

        Assert.Equal(0f, p[1 * Size + 6], 3);
        Assert.Equal(1f / 3f, p[0 * Size + 6], 3);
        Assert.Equal(2f / 3f, p[63 * Size + 6], 3);
        Assert.Equal(1f, p[62 * Size + 6], 3);
    }

    [Fact]
    public void To_gradient_gives_each_island_its_own_zero_to_one()
    {
        var pixels = Box(10, 10, 8, 4);
        var other = Box(40, 40, 4, 4);

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Math.Max(pixels[i], other[i]);
        }

        var p = GraphRunner.Evaluate(() => Gray.FromArray(pixels).FloodFill().ToGradient(), Size);

        Assert.Equal(0f, p[11 * Size + 10], 5);
        Assert.Equal(1f, p[11 * Size + 17], 5);
        Assert.Equal(0f, p[41 * Size + 40], 5);
        Assert.Equal(1f, p[41 * Size + 43], 5);
        Assert.Equal(1f / 3f, p[41 * Size + 41], 5);
    }

    [Fact]
    public void To_gradient_of_a_single_pixel_is_zero()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Box(7, 7, 1, 1)).FloodFill().ToGradient(), Size);

        Assert.Equal(0f, p[7 * Size + 7]);
    }

    [Fact]
    public void To_gradient_angle_must_be_a_number()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(1f).FloodFill().ToGradient(float.PositiveInfinity), 16));

        Assert.Equal("angle must be a finite number, not Infinity. (Parameter 'angle')", error.Message);
    }

    [Fact]
    public void Random_gray_tiles()
    {
        NodeChecks.Tiles(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f).FloodFill().RandomGray());
    }
}
