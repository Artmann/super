using System.Globalization;
using Super.Cook.Materials;
using Super.Materials;

namespace Super.Cook.Tests;

public sealed class MaterialChecksTests
{
    private const int Size = 16;
    private const int Pixels = Size * Size;

    private static MaterialImages Images(float[]? color = null, float[]? metallic = null, float[]? roughness = null)
    {
        return new MaterialImages(
            Size,
            1f,
            color ?? Fill(Pixels * 3, 0.5f),
            Fill(Pixels * 3, 0f),
            roughness ?? Alternating(Pixels, 0.5f, 0.7f),
            metallic ?? Fill(Pixels, 0f),
            Fill(Pixels, 1f),
            Fill(Pixels, 0.5f));
    }

    private static float[] Fill(int count, float value)
    {
        var values = new float[count];
        Array.Fill(values, value);
        return values;
    }

    private static float[] Alternating(int count, float even, float odd)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = i % 2 == 0 ? even : odd;
        }

        return values;
    }

    /// <summary>A grey colour image where the first pixels are <paramref name="a"/> and the rest <paramref name="b"/>.</summary>
    private static float[] Split(int first, float a, float b)
    {
        var values = new float[Pixels * 3];
        for (int i = 0; i < Pixels * 3; i++)
        {
            values[i] = i / 3 < first ? a : b;
        }

        return values;
    }

    private static float[] Rgb(float r, float g, float b)
    {
        var values = new float[Pixels * 3];
        for (int i = 0; i < Pixels; i++)
        {
            values[i * 3] = r;
            values[i * 3 + 1] = g;
            values[i * 3 + 2] = b;
        }

        return values;
    }

    private static string[] Problems(MaterialImages images) => [.. MaterialChecks.Problems(images, "m.material.cs")];

    [Fact]
    public void Plain_values_give_no_problems()
    {
        Assert.Empty(Problems(Images()));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void Dark_means_a_brightest_channel_below_30_in_the_written_byte(int level, bool warns)
    {
        var problems = Problems(Images(color: Fill(Pixels * 3, level / 255f)));

        Assert.Equal(warns, problems.Length == 1);
    }

    [Theory]
    [InlineData(240, false)]
    [InlineData(241, true)]
    public void Bright_means_a_darkest_channel_above_240_in_the_written_byte(int level, bool warns)
    {
        var problems = Problems(Images(color: Fill(Pixels * 3, level / 255f)));

        Assert.Equal(warns, problems.Length == 1);
    }

    [Fact]
    public void The_written_byte_decides_not_the_float()
    {
        // 29.6 / 255 is below 30/255 as a float but is written as 30.
        Assert.Empty(Problems(Images(color: Fill(Pixels * 3, 29.6f / 255f))));
    }

    [Fact]
    public void A_pixel_with_one_bright_channel_is_not_dark()
    {
        Assert.Empty(Problems(Images(color: Rgb(200f / 255f, 10f / 255f, 10f / 255f))));
    }

    [Fact]
    public void A_pixel_with_one_dark_channel_is_not_bright()
    {
        Assert.Empty(Problems(Images(color: Rgb(250f / 255f, 250f / 255f, 100f / 255f))));
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public void More_than_ten_percent_dark_warns_and_ten_percent_or_less_does_not(int dark, bool warns)
    {
        // 25 of 256 is 9.8% and 26 is 10.2%.
        var problems = Problems(Images(color: Split(dark, 10f / 255f, 0.5f)));

        Assert.Equal(warns, problems.Length == 1);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public void More_than_ten_percent_bright_warns_and_ten_percent_or_less_does_not(int bright, bool warns)
    {
        var problems = Problems(Images(color: Split(bright, 250f / 255f, 0.5f)));

        Assert.Equal(warns, problems.Length == 1);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public void Exactly_ten_percent_of_the_numbers_is_not_more_than_ten_percent(int dark, bool warns)
    {
        // Six pixels are not numbers, so 250 are counted and 25 of them is exactly a tenth.
        var color = Split(dark, 10f / 255f, 0.5f);
        for (int i = Pixels - 6; i < Pixels; i++)
        {
            color[i * 3] = float.NaN;
        }

        Assert.Equal(warns, Problems(Images(color: color)).Length == 1);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public void Exactly_ten_percent_of_the_numbers_is_not_more_than_ten_percent_bright(int bright, bool warns)
    {
        var color = Split(bright, 250f / 255f, 0.5f);
        for (int i = Pixels - 6; i < Pixels; i++)
        {
            color[i * 3] = float.NaN;
        }

        Assert.Equal(warns, Problems(Images(color: color)).Length == 1);
    }
    [Fact]
    public void The_dark_percentage_is_rounded_to_a_whole_number()
    {
        // 59 of 256 is 23.05%.
        var problem = Assert.Single(Problems(Images(color: Split(59, 10f / 255f, 0.5f))));

        Assert.Equal("m.material.cs: 23% of the base colour is darker than sRGB 30. Nothing real is that dark. Charcoal is about 50.", problem);
    }

    [Fact]
    public void The_bright_percentage_is_rounded_to_a_whole_number()
    {
        // 38 of 256 is 14.84%.
        var problem = Assert.Single(Problems(Images(color: Split(38, 250f / 255f, 0.5f))));

        Assert.Equal("m.material.cs: 15% of the base colour is brighter than sRGB 240. Fresh snow is about 240.", problem);
    }

    [Fact]
    public void A_percentage_of_exactly_a_half_rounds_up()
    {
        // 32 of 256 is exactly 12.5%.
        var problem = Assert.Single(Problems(Images(color: Split(32, 10f / 255f, 0.5f))));

        Assert.StartsWith("m.material.cs: 13% of", problem);
    }

    [Fact]
    public void Percentages_use_the_invariant_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var problem = Assert.Single(Problems(Images(color: Split(59, 10f / 255f, 0.5f))));

            Assert.StartsWith("m.material.cs: 23% of", problem);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Both_colour_rules_can_fire_together_dark_first()
    {
        var color = new float[Pixels * 3];
        for (int i = 0; i < Pixels * 3; i++)
        {
            color[i] = i / 3 < Pixels / 2 ? 0f : 1f;
        }

        var problems = Problems(Images(color: color));

        Assert.Equal(2, problems.Length);
        Assert.Contains("50% of the base colour is darker than sRGB 30", problems[0]);
        Assert.Contains("50% of the base colour is brighter than sRGB 240", problems[1]);
    }

    [Fact]
    public void Exactly_half_the_metalness_in_between_is_not_most()
    {
        var metallic = new float[Pixels];
        for (int i = 0; i < Pixels; i++)
        {
            metallic[i] = i < Pixels / 2 ? 0.5f : 1f;
        }

        Assert.Empty(Problems(Images(metallic: metallic)));
    }

    [Fact]
    public void More_than_half_the_metalness_in_between_warns()
    {
        var metallic = new float[Pixels];
        for (int i = 0; i < Pixels; i++)
        {
            metallic[i] = i <= Pixels / 2 ? 0.5f : 1f;
        }

        Assert.Equal(
            "m.material.cs: most of the metalness is between 0.1 and 0.9. Surfaces are metal or not, and in-between values look like plastic.",
            Assert.Single(Problems(Images(metallic: metallic))));
    }

    [Theory]
    [InlineData(0.05f, false)]
    [InlineData(0.1f, true)]
    [InlineData(0.9f, true)]
    [InlineData(0.95f, false)]
    public void The_metalness_range_includes_its_ends(float value, bool warns)
    {
        Assert.Equal(warns, Problems(Images(metallic: Fill(Pixels, value))).Length == 1);
    }

    [Theory]
    [InlineData(0.5f, 0.52f, true)]
    [InlineData(0.5f, 0.6f, false)]
    public void Roughness_with_a_standard_deviation_under_0_02_warns(float low, float high, bool warns)
    {
        // Half the pixels at each value gives a population standard deviation of (high - low) / 2: 0.01 and 0.05.
        var problems = Problems(Images(roughness: Alternating(Pixels, low, high)));

        Assert.Equal(warns, problems.Length == 1);
    }

    [Fact]
    public void Flat_roughness_gets_the_exact_message()
    {
        Assert.Equal(
            "m.material.cs: the roughness hardly varies. Real surfaces have smudges and wear, so vary it by a few percent at least.",
            Assert.Single(Problems(Images(roughness: Fill(Pixels, 0.6f)))));
    }

    [Fact]
    public void Roughness_is_judged_by_the_population_deviation_not_the_sample_one()
    {
        // Half at 0.48 and half at 0.51993: the population deviation is 0.019965 and the sample one (n - 1) 0.020004.
        Assert.Single(Problems(Images(roughness: Alternating(Pixels, 0.48f, 0.51993f))));
    }

    [Fact]
    public void Pixels_that_are_not_numbers_are_left_out()
    {
        var color = Fill(Pixels * 3, 0.5f);
        var roughness = Alternating(Pixels, 0.5f, 0.7f);
        var metallic = Fill(Pixels, 0f);
        for (int i = 0; i < Pixels / 2; i++)
        {
            color[i * 3] = 0f;
            color[i * 3 + 1] = 0f;
            color[i * 3 + 2] = 0f;
            color[i * 3 + i % 3] = float.NaN;
            metallic[i] = float.NaN;
        }

        // Left in as zero, the first half would be dark. Left out, the rest is mid grey.
        Assert.Empty(Problems(Images(color: color, roughness: roughness, metallic: metallic)));
    }

    [Fact]
    public void Roughness_that_is_not_a_number_does_not_count_towards_the_spread()
    {
        var roughness = Fill(Pixels, 0.6f);
        roughness[0] = float.NaN;
        roughness[1] = float.NaN;

        Assert.Single(Problems(Images(roughness: roughness)));
    }

    [Fact]
    public void An_image_of_only_pixels_that_are_not_numbers_gives_no_problems()
    {
        Assert.Empty(Problems(Images(
            color: Fill(Pixels * 3, float.NaN),
            metallic: Fill(Pixels, float.NaN),
            roughness: Fill(Pixels, float.NaN))));
    }

    [Fact]
    public void Values_outside_0_to_1_count_as_they_will_be_written()
    {
        // 2 is written as 255, and -1 as 0.
        Assert.Contains("brighter than sRGB 240", Assert.Single(Problems(Images(color: Fill(Pixels * 3, 2f)))));
        Assert.Contains("darker than sRGB 30", Assert.Single(Problems(Images(color: Fill(Pixels * 3, -1f)))));
    }
}
