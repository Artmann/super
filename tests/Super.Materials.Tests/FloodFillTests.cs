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

        Assert.Contains("threshold must be a finite number", error.Message);
    }

    [Fact]
    public void Random_gray_tiles()
    {
        NodeChecks.Tiles(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f).FloodFill().RandomGray());
    }
}
