using System.Numerics;

namespace Super.Materials.Tests;

public class PixelTests
{
    static float[] XRamp(int size)
    {
        var values = new float[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                values[y * size + x] = x / (float)size;
            }
        }

        return values;
    }

    [Fact]
    public void Uv_is_the_pixel_centre_along_a_row()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Uv.X), 4);

        Assert.Equal([0.125f, 0.375f, 0.625f, 0.875f], p[..4]);
        Assert.Equal([0.125f, 0.375f, 0.625f, 0.875f], p[12..16]);
    }

    [Fact]
    public void Uv_y_grows_down_the_image()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Uv.Y), 4);

        Assert.Equal([0.125f, 0.375f, 0.625f, 0.875f], new[] { p[0], p[4], p[8], p[12] });
    }

    [Fact]
    public void Gray_reads_the_input_at_the_pixel()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Gray(0) * 2f, Gray.Of(0.25f)), 16);

        Assert.All(p, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Gray_reads_the_pixel_it_is_called_for_and_not_its_mirror()
    {
        // The ramp rises along the row, so pixel (3, 1) must read 3 / 16 and not the 1 / 16 of its mirror (1, 3).
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Gray(0), Gray.FromArray(XRamp(16))), 16);

        Assert.Equal(XRamp(16), p);
    }

    [Fact]
    public void Rgb_reads_the_pixel_it_is_called_for_and_not_its_mirror()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Rgb(a => a.Rgb(0), Gray.FromArray(XRamp(16)).GradientMap(Ramp.Of("#000000", "#ff0000"))), 16);

        Assert.Equal(3f / 16f, p[(1 * 16 + 3) * 3], 5);
    }

    [Fact]
    public void Sample_gray_reads_down_the_image_with_the_v_of_the_uv()
    {
        // A column ramp, value y / 16. A quarter of the image down, row y gets the value of row y + 4.
        var rows = new float[16 * 16];

        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = i / 16 / 16f;
        }

        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.SampleGray(0, a.Uv + new Vector2(0f, 0.25f)), Gray.FromArray(rows)), 16);

        Assert.Equal(6f / 16f, p[2 * 16 + 5], 5);
        Assert.Equal(2f / 16f, p[14 * 16 + 5], 5);
    }

    [Fact]
    public void Inputs_are_numbered_in_the_order_they_are_given()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Gray(0) + a.Gray(1) * 10f, Gray.Of(0.5f), Gray.Of(0.25f)), 16);

        Assert.All(p, v => Assert.Equal(3f, v));
    }

    [Fact]
    public void Rgb_body_and_input_work_in_three_channels()
    {
        var p = GraphRunner.Evaluate(() => Pixel.Rgb(a => new Vector3(a.Uv.X, a.Uv.Y, a.Rgb(0).X * 2f), Rgb.Of(0.25f, 0f, 0f)), 4);
        var at = (2 * 4 + 1) * 3;

        Assert.Equal(4 * 4 * 3, p.Length);
        Assert.Equal([0.375f, 0.625f, 0.5f], p[at..(at + 3)]);
    }

    [Fact]
    public void Sample_gray_reads_at_a_uv_and_wraps()
    {
        // Reading a quarter of the image to the right: pixel x gets the value of pixel x + 4.
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.SampleGray(0, a.Uv + new Vector2(0.25f, 0f)), Gray.FromArray(XRamp(16))), 16);

        Assert.Equal(6f / 16f, p[2], 5);
        Assert.Equal(2f / 16f, p[14], 5);
    }

    [Fact]
    public void Sample_gray_blends_between_pixels()
    {
        // Half a pixel to the right of a centre is the mean of two pixels: (2 + 3) / 2 / 16.
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.SampleGray(0, a.Uv + new Vector2(0.5f / 16f, 0f)), Gray.FromArray(XRamp(16))), 16);

        Assert.Equal(2.5f / 16f, p[2], 5);
    }

    [Fact]
    public void Sample_rgb_reads_all_three_channels_at_a_uv()
    {
        var p = GraphRunner.Evaluate(
            () => Pixel.Rgb(a => a.SampleRgb(0, a.Uv + new Vector2(0.25f, 0f)), Gray.FromArray(XRamp(16)).GradientMap(Ramp.Of("#000000", "#ff8000"))),
            16);

        // Pixel 2 reads pixel 6, whose value is 6 / 16.
        Assert.Equal(6f / 16f, p[2 * 3], 5);
        Assert.Equal(6f / 16f * (128f / 255f), p[2 * 3 + 1], 5);
        Assert.Equal(0f, p[2 * 3 + 2], 5);
    }

    [Fact]
    public void Normals_can_be_read_as_three_channels()
    {
        Func<Image> build = () => Pixel.Gray(a => a.Rgb(0).Z, Gray.Of(0.5f).HeightToNormal(1f, 1f));
        var p = GraphRunner.Evaluate(build, 16);

        Assert.All(p, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void Reading_a_colour_input_as_gray_says_how_to_fix_it()
    {
        Func<Image> build = () => Pixel.Gray(a => a.Gray(0), Rgb.Of(1f, 0f, 0f));
        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Evaluate(build, 16));

        Assert.Equal("Gray(0) needs a one-channel input, but input 0 has three. Use Rgb(0) for it.", error.Message);
    }

    [Fact]
    public void Reading_a_gray_input_as_colour_says_how_to_fix_it()
    {
        Func<Image> build = () => Pixel.Rgb(a => a.Rgb(1), Rgb.Of(1f, 0f, 0f), Gray.Of(0.5f));
        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Evaluate(build, 16));

        Assert.Equal("Rgb(1) needs a three-channel input, but input 1 has one. Use Gray(1) for it.", error.Message);
    }

    [Fact]
    public void Sampling_the_wrong_kind_of_input_is_refused_too()
    {
        Func<Image> build = () => Pixel.Gray(a => a.SampleGray(0, a.Uv), Rgb.Of(1f, 0f, 0f));
        var error = Assert.Throws<InvalidOperationException>(() => GraphRunner.Evaluate(build, 16));

        Assert.Equal("SampleGray(0) needs a one-channel input, but input 0 has three. Use SampleRgb(0) for it.", error.Message);
    }

    [Theory]
    [InlineData(1, "Gray(1) has no input 1: the node has 1 input. (Parameter 'input')")]
    [InlineData(-1, "Gray(-1) has no input -1: the node has 1 input. (Parameter 'input')")]
    public void Reading_an_input_that_is_not_there_names_the_input_and_the_count(int input, string message)
    {
        Func<Image> build = () => Pixel.Gray(a => a.Gray(input), Gray.Of(0.5f));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(build, 16));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_node_with_no_inputs_says_so()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Pixel.Gray(a => a.Gray(0)), 16));

        Assert.Equal("Gray(0) has no input 0: the node has 0 inputs. (Parameter 'input')", error.Message);
    }

    [Fact]
    public void A_missing_body_is_refused()
    {
        var error = Assert.Throws<ArgumentNullException>(() => Pixel.Gray(null!));

        Assert.Equal("body", error.ParamName);
    }

    [Fact]
    public void Dmath_in_the_body_gives_the_same_pixels_on_any_number_of_threads()
    {
        Gray Build() => Pixel.Gray(a => 0.5f + 0.5f * DMath.Sin(a.Uv.X * 37f + DMath.Cos(a.Uv.Y * 11f)) * a.Gray(0), Noise.Perlin(4));

        var one = GraphRunner.Evaluate(Build, 64, maxThreads: 1);
        var many = GraphRunner.Evaluate(Build, 64);

        Assert.Equal(one, many);
        Assert.NotEqual(one[0], one[17]);
    }

    [Fact]
    public void A_pixel_node_wraps_with_the_nodes_around_it()
    {
        // A Pixel node's output is a normal image to the next node: Invert gives one minus it.
        var p = GraphRunner.Evaluate(() => Pixel.Gray(a => a.Uv.X).Invert(), 4);

        Assert.Equal([0.875f, 0.625f, 0.375f, 0.125f], p[..4]);
    }
}
