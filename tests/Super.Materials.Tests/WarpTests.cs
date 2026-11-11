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

    static float[] MarkerColumn(int column)
    {
        var values = new float[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            values[y * Size + column] = 1f;
        }

        return values;
    }

    static float[] MarkerRow(int row)
    {
        var values = new float[Size * Size];

        for (var x = 0; x < Size; x++)
        {
            values[row * Size + x] = 1f;
        }

        return values;
    }

    [Fact]
    public void Directional_warp_moves_pixels_right_by_amount_at_zero_degrees()
    {
        // 0.25 of 64 is 16 pixels, so the marker at column 10 lands on column 26.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(MarkerColumn(10)).DirectionalWarp(Gray.Of(1f), 0.25f, 0f), Size);

        Assert.Equal(1f, p[5 * Size + 26], 4);
        Assert.Equal(0f, p[5 * Size + 10], 4);
        Assert.Equal(0f, p[5 * Size + 25], 4);
    }

    [Fact]
    public void Directional_warp_at_ninety_degrees_moves_pixels_up_the_image_and_wraps()
    {
        // Up is towards row 0: row 10 moves 16 rows to row -6, which is row 58.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(MarkerRow(10)).DirectionalWarp(Gray.Of(1f), 0.25f, 90f), Size);

        Assert.Equal(1f, p[58 * Size + 7], 3);
        Assert.Equal(0f, p[10 * Size + 7], 3);
    }

    [Fact]
    public void Directional_warp_scales_the_move_by_the_warp_image()
    {
        // A warp image of 0.5 moves half as far: 8 pixels, to column 18. A warp image of 0 moves nothing.
        var half = GraphRunner.Evaluate(() => Gray.FromArray(MarkerColumn(10)).DirectionalWarp(Gray.Of(0.5f), 0.25f, 0f), Size);
        var none = GraphRunner.Evaluate(() => Gray.FromArray(MarkerColumn(10)).DirectionalWarp(Gray.Of(0f), 0.25f, 0f), Size);

        Assert.Equal(1f, half[18], 4);
        Assert.Equal(0f, half[26], 4);
        Assert.Equal(1f, none[10], 4);
    }

    [Fact]
    public void Directional_warp_with_a_negative_amount_moves_the_other_way()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(MarkerColumn(10)).DirectionalWarp(Gray.Of(1f), -0.25f, 0f), Size);

        Assert.Equal(1f, p[58], 4);
    }

    [Fact]
    public void Rgb_directional_warp_moves_every_channel()
    {
        var p = GraphRunner.Evaluate(
            () => Gray.FromArray(MarkerColumn(10)).GradientMap(Ramp.Of("#000000", "#ff8000")).DirectionalWarp(Gray.Of(1f), 0.25f, 0f),
            Size);

        Assert.Equal(1f, p[26 * 3], 4);
        Assert.Equal(0.5019608f, p[26 * 3 + 1], 4);
        Assert.Equal(0f, p[26 * 3 + 2], 4);
        Assert.Equal(0f, p[10 * 3], 4);
    }

    [Theory]
    [InlineData(2f, "amount must be from -1 to 1, not 2. (Parameter 'amount')")]
    [InlineData(-1.5f, "amount must be from -1 to 1, not -1.5. (Parameter 'amount')")]
    public void Directional_warp_amount_must_be_from_minus_one_to_one(float amount, string message)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).DirectionalWarp(Gray.Of(0.5f), amount, 0f), 16));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Directional_warp_angle_must_be_a_number()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).DirectionalWarp(Gray.Of(0.5f), 0.1f, float.NaN), 16));

        Assert.Equal("angle must be a finite number, not NaN. (Parameter 'angle')", error.Message);
    }

    [Fact]
    public void Directional_warp_tiles_and_matches_across_sizes()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).DirectionalWarp(Noise.Perlin(3), 0.1f, 30f));
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).DirectionalWarp(Noise.Perlin(3), 0.1f, 30f));
    }
}
