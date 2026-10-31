namespace Super.Tests;

public sealed class ColorTests
{
    private const int Exact = 3;
    private const int Near = 2;

    private static void AssertColor(Color expected, Color actual, int precision = Exact)
    {
        Assert.Equal(expected.R, actual.R, precision);
        Assert.Equal(expected.G, actual.G, precision);
        Assert.Equal(expected.B, actual.B, precision);
        Assert.Equal(expected.A, actual.A, precision);
    }

    [Fact]
    public void Hex_numbers_read_each_channel()
    {
        AssertColor(Color.Rgb(6, 32, 44, 0.72f), Color.Hex(0x06202C, 0.72f));
    }

    [Theory]
    [InlineData("#06202C")]
    [InlineData("06202C")]
    [InlineData("#06202c")]
    public void Hex_text_reads_with_or_without_the_hash(string text)
    {
        AssertColor(Color.Hex(0x06202C), Color.Hex(text));
    }

    [Fact]
    public void Short_hex_repeats_each_digit()
    {
        AssertColor(Color.Hex(0x3366CC), Color.Hex("#36C"));
        AssertColor(Color.Hex(0x3366CC, 0.6f), Color.Hex("#36C9"));
    }

    [Fact]
    public void Hex_text_can_carry_alpha()
    {
        AssertColor(Color.Hex(0x06202C, 128 / 255f), Color.Hex("#06202C80"));
    }

    [Fact]
    public void A_passed_alpha_wins_over_the_text()
    {
        Assert.Equal(0.25f, Color.Hex("#06202C80", 0.25f).A);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#+12345")]
    public void Bad_hex_text_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => Color.Hex(text));
    }

    [Theory]
    [InlineData(0f, 1f, 0.5f, 0xFF0000)]
    [InlineData(120f, 1f, 0.5f, 0x00FF00)]
    [InlineData(240f, 1f, 0.5f, 0x0000FF)]
    [InlineData(210f, 0.5f, 0.4f, 0x336699)]
    [InlineData(0f, 0f, 0.2f, 0x333333)]
    public void Hsl_matches_css(float hue, float saturation, float lightness, int expected)
    {
        AssertColor(Color.Hex(expected), Color.Hsl(hue, saturation, lightness));
    }

    [Theory]
    [InlineData(-150f)]
    [InlineData(570f)]
    public void Hsl_hue_goes_round_the_wheel(float hue)
    {
        AssertColor(Color.Hsl(210f, 0.5f, 0.4f), Color.Hsl(hue, 0.5f, 0.4f));
    }

    [Theory]
    [InlineData(0.62796f, 0.25768f, 29.234f, 0xFF0000)]
    [InlineData(0.86644f, 0.29483f, 142.495f, 0x00FF00)]
    [InlineData(0.45201f, 0.31321f, 264.052f, 0x0000FF)]
    [InlineData(1f, 0f, 0f, 0xFFFFFF)]
    [InlineData(0f, 0f, 0f, 0x000000)]
    public void Oklch_matches_css(float lightness, float chroma, float hue, int expected)
    {
        AssertColor(Color.Hex(expected), Color.Oklch(lightness, chroma, hue), Near);
    }

    [Fact]
    public void Oklch_clips_colors_a_screen_cant_show()
    {
        var color = Color.Oklch(0.7f, 0.4f, 150f);

        Assert.InRange(color.R, 0f, 1f);
        Assert.InRange(color.G, 0f, 1f);
        Assert.InRange(color.B, 0f, 1f);
    }

    [Fact]
    public void Each_notation_keeps_its_alpha()
    {
        Assert.Equal(0.5f, Color.Rgb(1, 2, 3, 0.5f).A);
        Assert.Equal(0.5f, Color.Hsl(10f, 0.5f, 0.5f, 0.5f).A);
        Assert.Equal(0.5f, Color.Oklch(0.5f, 0.1f, 10f, 0.5f).A);
    }
}
