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
}
