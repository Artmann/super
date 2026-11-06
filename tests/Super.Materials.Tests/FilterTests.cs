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

        Assert.Contains("radius", error.Message);
    }
}
