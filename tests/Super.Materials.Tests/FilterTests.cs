namespace Super.Materials.Tests;

public class FilterTests
{
    static float Pixel(Func<Gray> build)
    {
        return GraphRunner.Evaluate(build, 16)[0];
    }

    static float[] Ramp100x100()
    {
        var values = new float[100 * 100];

        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                values[y * 100 + x] = (x + 0.5f) / 100f;
            }
        }

        return values;
    }

    [Fact]
    public void Levels_maps_the_input_range_onto_zero_to_one()
    {
        Assert.Equal(0.5f, Pixel(() => Gray.Of(0.4f).Levels(0.2f, 0.6f)), 5);
        Assert.Equal(0f, Pixel(() => Gray.Of(0.1f).Levels(0.2f, 0.6f)), 5);
        Assert.Equal(1f, Pixel(() => Gray.Of(0.9f).Levels(0.2f, 0.6f)), 5);
    }

    [Fact]
    public void Levels_gamma_two_brightens_a_quarter_to_a_half()
    {
        Assert.Equal(0.5f, Pixel(() => Gray.Of(0.25f).Levels(gamma: 2f)), 5);
    }

    [Fact]
    public void Levels_maps_to_the_output_range()
    {
        Assert.Equal(0.6f, Pixel(() => Gray.Of(0.5f).Levels(outLow: 0.2f, outHigh: 1f)), 5);
    }

    [Fact]
    public void Remap_scales_into_the_range_without_clamping()
    {
        Assert.Equal(0.85f, Pixel(() => Gray.Of(0f).Remap(0.85f, 1f)), 5);
        Assert.Equal(1f, Pixel(() => Gray.Of(1f).Remap(0.85f, 1f)), 5);
        Assert.Equal(1.15f, Pixel(() => Gray.Of(2f).Remap(0.85f, 1f)), 5);
    }

    [Fact]
    public void Invert_flips_around_a_half()
    {
        Assert.Equal(0.7f, Pixel(() => Gray.Of(0.3f).Invert()), 5);
    }

    [Fact]
    public void Histogram_scan_at_full_contrast_keeps_the_share_it_is_given()
    {
        // A gradient where pixel i of 100 has value i / 100: position 0.3 leaves 30 white pixels.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(Ramp100x100()).HistogramScan(0.3f, contrast: 1f), 100);

        Assert.Equal(30 * 100, p.Count(v => v > 0.5f));
    }

    [Fact]
    public void Histogram_scan_softens_the_edge_at_low_contrast()
    {
        // t = 0.5, w = 0.5: a value of 0.5 gives 0.5, and 0.25 gives 0.
        Assert.Equal(0.5f, Pixel(() => Gray.Of(0.5f).HistogramScan(0.5f, 0.5f)), 5);
        Assert.Equal(0f, Pixel(() => Gray.Of(0.25f).HistogramScan(0.5f, 0.5f)), 5);
    }

    [Fact]
    public void Blur_keeps_the_total_and_spreads_across_the_edge()
    {
        var impulse = new float[64 * 64];

        impulse[0] = 1f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(impulse).Blur(0.02f), 64);

        Assert.Equal(1f, p.Sum(), 3);
        Assert.True(p[1] > 0.001f, "The blur did not spread.");
        Assert.Equal(p[1], p[63], 6);
        Assert.Equal(p[64], p[63 * 64], 6);
    }

    [Fact]
    public void Blurring_a_flat_image_changes_nothing()
    {
        var p = GraphRunner.Evaluate(() => Gray.Of(0.4f).Blur(0.1f), 32);

        Assert.All(p, v => Assert.Equal(0.4f, v, 6));
    }

    [Fact]
    public void Blur_with_a_kernel_wider_than_the_image_still_keeps_the_total()
    {
        var impulse = new float[16 * 16];

        impulse[0] = 1f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(impulse).Blur(0.5f), 16);

        Assert.Equal(1f, p.Sum(), 3);
    }

    [Fact]
    public void Blur_of_zero_copies()
    {
        var values = new float[16 * 16];

        values[5] = 0.7f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(values).Blur(0f), 16);

        Assert.Equal(values, p);
    }

    [Fact]
    public void Rgb_blur_blurs_each_channel()
    {
        var p = GraphRunner.Evaluate(() => Rgb.Of(0.1f, 0.5f, 0.9f).Blur(0.1f), 16);

        for (var i = 0; i < 16 * 16; i++)
        {
            Assert.Equal(0.1f, p[i * 3], 5);
            Assert.Equal(0.5f, p[i * 3 + 1], 5);
            Assert.Equal(0.9f, p[i * 3 + 2], 5);
        }
    }

    [Fact]
    public void Blurred_noise_tiles()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).Blur(0.02f));
    }

    [Fact]
    public void Blurred_noise_looks_the_same_at_other_sizes()
    {
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).Blur(0.02f));
    }

    [Fact]
    public void Levels_rejects_an_empty_input_range()
    {
        var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Levels(0.5f, 0.5f), 16));

        Assert.Contains("inHigh", error.Message);
    }

    [Fact]
    public void Blur_rejects_a_negative_radius()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Blur(-0.1f), 16));

        Assert.Equal("radius must be from 0 to 1, not -0.1. (Parameter 'radius')", error.Message);
    }

    [Fact]
    public void Levels_rejects_a_gamma_of_zero_in_one_line()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Levels(gamma: 0f), 16));

        Assert.Equal("gamma must be above 0, not 0. (Parameter 'gamma')", error.Message);
    }

    [Theory]
    [InlineData(0.25f, 0.4f)]
    [InlineData(0.5f, 0.8f)]
    [InlineData(0.75f, 0.9f)]
    public void Curve_is_straight_between_its_points(float input, float expected)
    {
        Assert.Equal(expected, Pixel(() => Gray.Of(input).Curve((0f, 0f), (0.5f, 0.8f), (1f, 1f))), 5);
    }

    [Fact]
    public void Curve_holds_the_end_outputs_outside_its_points()
    {
        Assert.Equal(0.3f, Pixel(() => Gray.Of(0.1f).Curve((0.2f, 0.3f), (0.6f, 0.7f))), 5);
        Assert.Equal(0.7f, Pixel(() => Gray.Of(0.9f).Curve((0.2f, 0.3f), (0.6f, 0.7f))), 5);
        Assert.Equal(0.7f, Pixel(() => Gray.Of(0.6f).Curve((0.2f, 0.3f), (0.6f, 0.7f))), 5);
    }

    [Fact]
    public void Curve_can_fall_as_well_as_rise()
    {
        Assert.Equal(0.75f, Pixel(() => Gray.Of(0.25f).Curve((0f, 1f), (1f, 0f))), 5);
    }

    [Fact]
    public void Curve_needs_two_points()
    {
        var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Curve((0f, 0f)), 16));

        Assert.Equal("A curve needs at least two points, not 1. (Parameter 'points')", error.Message);
    }

    [Fact]
    public void Curve_points_must_go_up_in_their_input()
    {
        var same = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Curve((0f, 0f), (0.5f, 1f), (0.5f, 0f)), 16));
        var back = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Curve((0f, 0f), (0.8f, 1f), (0.5f, 0f)), 16));

        Assert.Equal("Curve points must go up in In: 0.5 comes after 0.5. (Parameter 'points')", same.Message);
        Assert.Equal("Curve points must go up in In: 0.5 comes after 0.8. (Parameter 'points')", back.Message);
    }

    [Fact]
    public void Curve_refuses_an_output_that_is_not_a_number()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Curve((0f, 0f), (1f, float.NaN)), 16));

        Assert.Equal("Out must be a finite number, not NaN. (Parameter 'Out')", error.Message);
    }

    static float[] HalfBlackHalfWhite(int size)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = size / 2; x < size; x++)
            {
                values[y * size + x] = 1f;
            }
        }

        return values;
    }

    static float[] SingleColumn(int size, int column)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            values[y * size + column] = 1f;
        }

        return values;
    }

    [Fact]
    public void Edge_detect_is_one_at_the_seam_and_zero_a_quarter_away()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(HalfBlackHalfWhite(64)).EdgeDetect(0.05f), 64);

        Assert.Equal(1f, p[10 * 64 + 31]);
        Assert.Equal(1f, p[10 * 64 + 32]);
        Assert.Equal(0f, p[10 * 64 + 16]);
        Assert.Equal(0f, p[10 * 64 + 48]);
    }

    [Fact]
    public void Edge_detect_reaches_exactly_width_away_in_both_directions()
    {
        // 0.05 of 64 is 3.2 pixels. White starts at column 32, so black column 29 is 3 away and column 28 is 4.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(HalfBlackHalfWhite(64)).EdgeDetect(0.05f), 64);

        Assert.Equal(0f, p[28]);
        Assert.Equal(1f, p[29]);
        Assert.Equal(1f, p[34]);
        Assert.Equal(0f, p[35]);
    }

    [Fact]
    public void Edge_detect_marks_both_sides_of_the_seam_where_the_image_wraps()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(HalfBlackHalfWhite(64)).EdgeDetect(0.05f), 64);

        Assert.Equal(1f, p[63]);
        Assert.Equal(1f, p[0]);
    }

    [Fact]
    public void Edge_detect_with_a_very_thin_width_still_finds_the_next_pixel()
    {
        var p = GraphRunner.Evaluate(() => Gray.FromArray(HalfBlackHalfWhite(64)).EdgeDetect(0.001f), 64);

        Assert.Equal(0f, p[30]);
        Assert.Equal(1f, p[31]);
        Assert.Equal(1f, p[32]);
        Assert.Equal(0f, p[33]);
    }

    [Fact]
    public void Edge_detect_of_a_flat_image_is_zero()
    {
        Assert.All(GraphRunner.Evaluate(() => Gray.Of(0.9f).EdgeDetect(0.1f), 16), v => Assert.Equal(0f, v));
        Assert.All(GraphRunner.Evaluate(() => Gray.Of(0.1f).EdgeDetect(0.1f), 16), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Edge_detect_counts_a_half_as_below()
    {
        var pixels = new float[16 * 16];

        Array.Fill(pixels, 0.5f);

        // One pixel just over a half in a field of exactly a half: they are on different sides.
        pixels[8 * 16 + 8] = 0.5001f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(pixels).EdgeDetect(0.01f), 16);

        Assert.Equal(1f, p[8 * 16 + 8]);
        Assert.Equal(1f, p[8 * 16 + 9]);
        Assert.Equal(0f, p[8 * 16 + 10]);
    }

    [Fact]
    public void Edge_detect_needs_a_width_above_zero()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).EdgeDetect(0f), 16));

        Assert.Equal("width must be more than 0, not 0. (Parameter 'width')", error.Message);
    }

    [Fact]
    public void Edge_detect_tiles_and_matches_across_sizes()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).EdgeDetect(0.03f));
        // A thin width is a few pixels at 64 and twice that at 128, and the edge is rounded to whole pixels, so the
        // sizes only agree on average from about a tenth of the image up.
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).EdgeDetect(0.1f));
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(4, 0.75f)]
    [InlineData(8, 0.5f)]
    [InlineData(16, 0f)]
    [InlineData(32, 0f)]
    [InlineData(48, 0f)]
    [InlineData(56, 0.5f)]
    [InlineData(60, 0.75f)]
    [InlineData(63, 0.9375f)]
    public void Distance_falls_off_from_a_white_column_and_wraps(int x, float expected)
    {
        // Reach 0.25 of 64 is 16 pixels, so 8 pixels away is 1 - (8 / 64) / 0.25 = 0.5.
        var p = GraphRunner.Evaluate(() => Gray.FromArray(SingleColumn(64, 0)).Distance(0.25f), 64);

        Assert.Equal(expected, p[20 * 64 + x], 5);
    }

    [Fact]
    public void Distance_is_straight_line_not_a_square()
    {
        var pixels = new float[64 * 64];

        pixels[0] = 1f;

        var p = GraphRunner.Evaluate(() => Gray.FromArray(pixels).Distance(0.5f), 64);

        // 3 across and 4 down is 5 pixels, so 1 - (5 / 64) / 0.5 = 0.84375. The same through both wraps.
        Assert.Equal(0.84375f, p[4 * 64 + 3], 5);
        Assert.Equal(0.84375f, p[60 * 64 + 61], 5);
        Assert.Equal(0.84375f, p[4 * 64 + 61], 5);
        Assert.Equal(0.84375f, p[60 * 64 + 3], 5);
    }

    [Fact]
    public void Distance_is_zero_where_nothing_is_above_a_half()
    {
        Assert.All(GraphRunner.Evaluate(() => Gray.Of(0.5f).Distance(0.2f), 16), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Distance_is_one_everywhere_when_everything_is_above_a_half()
    {
        Assert.All(GraphRunner.Evaluate(() => Gray.Of(0.6f).Distance(0.2f), 16), v => Assert.Equal(1f, v));
    }

    [Fact]
    public void Distance_matches_a_brute_force_search_on_noise()
    {
        const int size = 32;
        const float reach = 0.3f;

        var source = GraphRunner.Evaluate(() => Noise.Perlin(3), size);
        var p = GraphRunner.Evaluate(() => Noise.Perlin(3).Distance(reach), size);
        var bright = 0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (source[y * size + x] > 0.5f)
                {
                    bright++;
                }
            }
        }

        // Some of the image is above a half and some is not, so both branches are tried.
        Assert.InRange(bright, 50, size * size - 50);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nearest = double.MaxValue;

                for (var sy = 0; sy < size; sy++)
                {
                    for (var sx = 0; sx < size; sx++)
                    {
                        if (!(source[sy * size + sx] > 0.5f))
                        {
                            continue;
                        }

                        var dx = Math.Min(Math.Abs(x - sx), size - Math.Abs(x - sx));
                        var dy = Math.Min(Math.Abs(y - sy), size - Math.Abs(y - sy));

                        nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy) / size);
                    }
                }

                var expected = source[y * size + x] > 0.5f ? 1.0 : Math.Max(0.0, 1.0 - nearest / reach);

                Assert.Equal(expected, p[y * size + x], 4);
            }
        }
    }

    [Theory]
    [InlineData(0f, "reach must be more than 0 and at most 0.5, not 0. (Parameter 'reach')")]
    [InlineData(0.6f, "reach must be more than 0 and at most 0.5, not 0.6. (Parameter 'reach')")]
    [InlineData(-0.1f, "reach must be more than 0 and at most 0.5, not -0.1. (Parameter 'reach')")]
    public void Distance_reach_must_be_above_zero_and_at_most_half(float reach, string message)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Gray.Of(0.5f).Distance(reach), 16));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Distance_tiles_and_matches_across_sizes()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).Distance(0.1f));
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).Distance(0.1f));
    }

    [Fact]
    public void Distance_is_the_same_on_any_number_of_threads()
    {
        var one = GraphRunner.Evaluate(() => Noise.Perlin(4).Distance(0.1f), 64, maxThreads: 1);
        var many = GraphRunner.Evaluate(() => Noise.Perlin(4).Distance(0.1f), 64);

        Assert.Equal(one, many);
    }
}
