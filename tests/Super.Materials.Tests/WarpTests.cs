namespace Super.Materials.Tests;

public class WarpTests
{
    const int Size = 64;

    static float[] VerticalStripes()
    {
        var values = new float[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                values[y * Size + x] = (x / 8) % 2;
            }
        }

        return values;
    }

    static float[] Transposed(float[] values)
    {
        var result = new float[values.Length];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                result[y * Size + x] = values[x * Size + y];
            }
        }

        return result;
    }

    static float[] SineAcross()
    {
        var values = new float[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                values[y * Size + x] = (float)Math.Sin(2 * Math.PI * (x + 0.5) / Size);
            }
        }

        return values;
    }

    static float Contrast(float[] image, int row)
    {
        var low = float.MaxValue;
        var high = float.MinValue;

        for (var x = 0; x < Size; x++)
        {
            low = Math.Min(low, image[row * Size + x]);
            high = Math.Max(high, image[row * Size + x]);
        }

        return high - low;
    }

    [Fact]
    public void Warp_by_a_flat_image_changes_nothing()
    {
        var plain = GraphRunner.Evaluate(() => Noise.Perlin(4), 32);
        var warped = GraphRunner.Evaluate(() => Noise.Perlin(4).Warp(Gray.Of(0.5f), 0.1f), 32);

        Assert.Equal(plain, warped);
    }

    [Fact]
    public void Warp_with_amount_zero_changes_nothing()
    {
        var plain = GraphRunner.Evaluate(() => Noise.Perlin(4), 32);
        var warped = GraphRunner.Evaluate(() => Noise.Perlin(4).Warp(Noise.Perlin(2), 0f), 32);

        Assert.Equal(plain, warped);
    }

    [Fact]
    public void Warp_moves_pixels_down_the_slope()
    {
        var stripes = VerticalStripes();
        var by = SineAcross();
        var warped = GraphRunner.Evaluate(() => Gray.FromArray(stripes).Warp(Gray.FromArray(by), 0.01f), Size);

        // Central difference at pixel 2, per image width.
        var slope = (by[3] - by[1]) / 2.0 * Size;
        var position = 2 - 0.01 * Size * slope;
        var left = (int)Math.Floor(position);
        var t = position - left;
        var a = stripes[(left + Size) % Size];
        var b = stripes[(left + 1 + Size) % Size];
        var expected = a + (b - a) * t;

        Assert.True(position < -1.5 && position > -2.5, $"Expected the read to land about 4 pixels left of pixel 2, got {position}.");
        Assert.Equal(expected, warped[2], 1e-3);
        Assert.Equal(1f, warped[2], 1e-3);
    }

    [Fact]
    public void Slope_blur_only_blurs_along_the_slope()
    {
        var stripes = VerticalStripes();
        var slope = SineAcross();
        var blurred = GraphRunner.Evaluate(() => Gray.FromArray(stripes).SlopeBlur(Gray.FromArray(slope), 0.25f), Size);

        Assert.Equal(1f, Contrast(stripes, 10), 1e-6);
        Assert.True(Contrast(blurred, 10) < 0.5f, $"The contrast along the slope was {Contrast(blurred, 10)}.");

        var horizontal = Transposed(stripes);
        var unchanged = GraphRunner.Evaluate(() => Gray.FromArray(horizontal).SlopeBlur(Gray.FromArray(slope), 0.25f), Size);

        for (var i = 0; i < horizontal.Length; i++)
        {
            Assert.Equal(horizontal[i], unchanged[i], 1e-4);
        }
    }

    [Fact]
    public void Slope_blur_min_never_raises_a_pixel()
    {
        var source = GraphRunner.Evaluate(() => Noise.Perlin(4), Size);
        var result = GraphRunner.Evaluate(() => Noise.Perlin(4).SlopeBlur(Noise.Perlin(3, seed: 5), 0.05f, mode: SlopeBlurMode.Min), Size);

        var lowered = 0;

        for (var i = 0; i < source.Length; i++)
        {
            Assert.True(result[i] <= source[i], $"Pixel {i} rose from {source[i]} to {result[i]}.");

            if (result[i] < source[i])
            {
                lowered++;
            }
        }

        Assert.True(lowered > source.Length / 4, $"Only {lowered} pixels changed.");
    }

    [Fact]
    public void Slope_blur_max_never_lowers_a_pixel()
    {
        var source = GraphRunner.Evaluate(() => Noise.Perlin(4), Size);
        var result = GraphRunner.Evaluate(() => Noise.Perlin(4).SlopeBlur(Noise.Perlin(3, seed: 5), 0.05f, mode: SlopeBlurMode.Max), Size);

        for (var i = 0; i < source.Length; i++)
        {
            Assert.True(result[i] >= source[i], $"Pixel {i} fell from {source[i]} to {result[i]}.");
        }
    }

    [Fact]
    public void Slope_blur_rejects_fewer_than_one_sample()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).SlopeBlur(Noise.Perlin(2), 0.1f, 0), 16));

        Assert.Contains("samples must be at least 1", error.Message);
    }

    [Fact]
    public void Rgb_warp_and_slope_blur_work_on_each_channel()
    {
        var plain = GraphRunner.Evaluate(() => Rgb.Of(0.2f, 0.4f, 0.6f), 16);
        var warped = GraphRunner.Evaluate(() => Rgb.Of(0.2f, 0.4f, 0.6f).Warp(Noise.Perlin(2), 0.1f), 16);
        var blurred = GraphRunner.Evaluate(() => Rgb.Of(0.2f, 0.4f, 0.6f).SlopeBlur(Noise.Perlin(2), 0.1f), 16);

        Assert.Equal(plain, warped);
        Assert.Equal(16 * 16 * 3, blurred.Length);

        for (var i = 0; i < blurred.Length; i++)
        {
            Assert.Equal(plain[i], blurred[i], 1e-5);
        }
    }

    [Fact]
    public void Warp_tiles_and_matches_across_sizes()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).Warp(Noise.Perlin(2), 0.05f));
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).Warp(Noise.Perlin(2), 0.05f));
    }

    [Fact]
    public void Slope_blur_tiles_and_matches_across_sizes()
    {
        NodeChecks.Tiles(() => Tiles.Bricks(4, 8).SlopeBlur(Noise.Cells(6), 0.02f));
        // Cell edges are steps, so the slope direction there depends on the size. Looser than the default.
        NodeChecks.SameAtSizes(() => Tiles.Bricks(4, 8).SlopeBlur(Noise.Cells(6), 0.02f), tolerance: 0.06f);
    }
}
