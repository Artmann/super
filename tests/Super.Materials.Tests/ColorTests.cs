using System.Globalization;

namespace Super.Materials.Tests;

public class ColorTests
{
    static readonly Ramp BlackWhiteRed = Ramp.Of("#000000", "#ffffff", "#ff0000");

    static float[] Map(float input, Ramp ramp)
    {
        return GraphRunner.Evaluate(() => Gray.Of(input).GradientMap(ramp), 16)[..3].Select(v => MathF.Round(v, 4)).ToArray();
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(0.5f, 1f, 1f, 1f)]
    [InlineData(1f, 1f, 0f, 0f)]
    [InlineData(0.25f, 0.5f, 0.5f, 0.5f)]
    [InlineData(0.75f, 1f, 0.5f, 0.5f)]
    public void Evenly_spaced_stops_blend_linearly(float input, float r, float g, float b)
    {
        Assert.Equal([r, g, b], Map(input, BlackWhiteRed));
    }

    [Fact]
    public void Input_outside_zero_to_one_is_clamped_to_the_end_stops()
    {
        Assert.Equal([0f, 0f, 0f], Map(-3f, BlackWhiteRed));
        Assert.Equal([1f, 0f, 0f], Map(5f, BlackWhiteRed));
    }

    [Fact]
    public void Stops_can_sit_anywhere_and_hold_their_colour_to_the_ends()
    {
        var ramp = Ramp.Of((0.4f, "#000000"), (0.6f, "#ffffff"));

        Assert.Equal([0f, 0f, 0f], Map(0.1f, ramp));
        Assert.Equal([0.5f, 0.5f, 0.5f], Map(0.5f, ramp));
        Assert.Equal([1f, 1f, 1f], Map(0.9f, ramp));
    }

    [Fact]
    public void A_single_colour_ramp_is_flat()
    {
        Assert.Equal([1f, 0f, 0f], Map(0.3f, Ramp.Of("#ff0000")));
    }

    [Fact]
    public void Ramp_stops_must_go_up()
    {
        var error = Assert.Throws<ArgumentException>(() => Ramp.Of((0.5f, "#000000"), (0.2f, "#ffffff")));

        Assert.Equal("Ramp stops must be in order: 0.2 comes after 0.5.", error.Message);
    }

    [Fact]
    public void Ramp_error_uses_a_dot_under_a_swedish_culture()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

            var error = Assert.Throws<ArgumentException>(() => Ramp.Of((0.5f, "#000000"), (0.2f, "#ffffff")));

            Assert.Equal("Ramp stops must be in order: 0.2 comes after 0.5.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_stop_outside_zero_to_one_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Ramp.Of((0f, "#000000"), (1.5f, "#ffffff")));

        Assert.Contains("1.5", error.Message);
    }

    [Fact]
    public void A_ramp_needs_a_stop()
    {
        var error = Assert.Throws<ArgumentException>(() => Ramp.Of(Array.Empty<string>()));

        Assert.Contains("at least one", error.Message);
    }

    [Fact]
    public void A_bad_colour_reuses_the_colour_message()
    {
        var error = Assert.Throws<ArgumentException>(() => Ramp.Of("#000000", "red"));

        Assert.Equal("Colours are written #rrggbb, not red.", error.Message);
    }

    static float[] Hsl(string hex, float hue = 0f, float saturation = 0f, float lightness = 0f)
    {
        return GraphRunner.Evaluate(() => Rgb.Of(hex).Hsl(hue, saturation, lightness), 16)[..3];
    }

    static void AssertColor(float[] expected, float[] actual)
    {
        Assert.Equal(3, actual.Length);

        for (var c = 0; c < 3; c++)
        {
            Assert.Equal(expected[c], actual[c], 4);
        }
    }

    [Theory]
    [InlineData("#ff0000", 0.2126f)]
    [InlineData("#00ff00", 0.7152f)]
    [InlineData("#0000ff", 0.0722f)]
    [InlineData("#ffffff", 1f)]
    [InlineData("#000000", 0f)]
    public void To_gray_weighs_the_channels_as_rec_709(string hex, float expected)
    {
        Assert.Equal(expected, GraphRunner.Evaluate(() => Rgb.Of(hex).ToGray(), 16)[0], 5);
    }

    [Fact]
    public void To_gray_uses_the_values_as_stored_with_no_clamping()
    {
        var p = GraphRunner.Evaluate(() => Rgb.Of(2f, 0f, 0f).ToGray(), 16);

        Assert.Equal(16 * 16, p.Length);
        Assert.Equal(0.4252f, p[0], 5);
    }

    [Fact]
    public void Hsl_half_a_turn_of_hue_turns_red_into_cyan()
    {
        AssertColor([0f, 1f, 1f], Hsl("#ff0000", hue: 0.5f));
    }

    [Fact]
    public void Hsl_a_negative_hue_turns_the_other_way()
    {
        // Red at hue 0 turned back a quarter is hue 270 degrees: 0.5, 0, 1.
        AssertColor([0.5f, 0f, 1f], Hsl("#ff0000", hue: -0.25f));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(3f)]
    public void Hsl_a_whole_turn_of_hue_changes_nothing(float hue)
    {
        AssertColor([0x33 / 255f, 0x66 / 255f, 0x99 / 255f], Hsl("#336699", hue: hue));
    }

    [Fact]
    public void Hsl_with_no_offsets_returns_the_colour()
    {
        AssertColor([0xa8 / 255f, 0x60 / 255f, 0x4a / 255f], Hsl("#a8604a"));
    }

    [Fact]
    public void Hsl_removing_all_saturation_gives_the_grey_of_the_same_lightness()
    {
        AssertColor([0.5f, 0.5f, 0.5f], Hsl("#ff0000", saturation: -1f));
    }

    [Fact]
    public void Hsl_saturation_is_added_and_clamped_at_one()
    {
        // 0.75, 0.25, 0.25 is saturation 0.5 at lightness 0.5. Adding 0.5 makes it pure red, and 0.7 changes nothing more.
        AssertColor([1f, 0f, 0f], GraphRunner.Evaluate(() => Rgb.Of(0.75f, 0.25f, 0.25f).Hsl(saturation: 0.5f), 16)[..3]);
        AssertColor([1f, 0f, 0f], GraphRunner.Evaluate(() => Rgb.Of(0.75f, 0.25f, 0.25f).Hsl(saturation: 0.7f), 16)[..3]);
    }

    [Fact]
    public void Hsl_lightness_is_added_and_clamped()
    {
        // Red at lightness 0.75 is 1, 0.5, 0.5.
        AssertColor([1f, 0.5f, 0.5f], Hsl("#ff0000", lightness: 0.25f));
        AssertColor([1f, 1f, 1f], Hsl("#ff0000", lightness: 1f));
        AssertColor([0f, 0f, 0f], Hsl("#ff0000", lightness: -1f));
    }

    [Fact]
    public void Hsl_hue_does_not_change_a_grey()
    {
        AssertColor([0.4f, 0.4f, 0.4f], Hsl("#666666", hue: 0.3f));
    }

    [Fact]
    public void Hsl_offsets_must_be_numbers()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Rgb.Of(1f, 0f, 0f).Hsl(saturation: float.NaN), 16));

        Assert.Equal("saturation must be a finite number, not NaN. (Parameter 'saturation')", error.Message);
    }

    [Fact]
    public void Hsl_matches_across_sizes_and_tiles()
    {
        NodeChecks.Tiles(() => Noise.Perlin(4).GradientMap(Ramp.Of("#ff0000", "#0000ff")).Hsl(0.2f, 0.1f, 0.1f).ToGray());
        NodeChecks.SameAtSizes(() => Noise.Perlin(4).GradientMap(Ramp.Of("#ff0000", "#0000ff")).Hsl(0.2f, 0.1f, 0.1f).ToGray());
    }
}
