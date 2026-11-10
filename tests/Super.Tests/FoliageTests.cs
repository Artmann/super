using Rendition;

namespace Super.Tests;

/// <summary>Foliage sets how much light shines through it, and the G-buffer carries that to the lighting.</summary>
public class FoliageTests
{
    [Theory]
    [InlineData(1.5f, "foliage translucency 1.5 must be from 0 to 1.")]
    [InlineData(-0.25f, "foliage translucency -0.25 must be from 0 to 1.")]
    [InlineData(float.NaN, "foliage translucency NaN must be from 0 to 1.")]
    public void Translucency_outside_0_to_1_has_a_problem_that_says_so(float translucency, string problem)
    {
        Assert.Equal(problem, Material.TranslucencyProblem(translucency));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.6f)]
    [InlineData(1f)]
    public void Translucency_from_0_to_1_has_no_problem(float translucency)
    {
        Assert.Null(Material.TranslucencyProblem(translucency));
    }

    [Theory]
    [InlineData(-0.5f, "wind sway -0.5 must be 0 or more.")]
    [InlineData(float.PositiveInfinity, "wind sway Infinity must be 0 or more.")]
    public void A_negative_or_endless_wind_sway_has_a_problem_that_says_so(float sway, string problem)
    {
        Assert.Equal(problem, Material.WindProblem(sway));
    }

    [Fact]
    public void The_problems_read_the_same_under_a_swedish_culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");

            Assert.Equal("foliage translucency 1.5 must be from 0 to 1.", Material.TranslucencyProblem(1.5f));
            Assert.Equal("wind sway -0.5 must be 0 or more.", Material.WindProblem(-0.5f));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // There's no GPU in the tests, so these read the shaders and check that foliage writes its material's own
    // translucency, not one constant for all, and that the lighting still reads a negative channel as foliage.
    [Fact]
    public void Foliage_writes_its_materials_translucency_as_a_negative_number()
    {
        string surface = Shader("surface.glsl");

        Assert.DoesNotContain("FOLIAGE_TRANSLUCENCY", surface);
        Assert.Contains("return -max(material.transmission, FOLIAGE_MARK);", surface);
        Assert.Contains("float channel = foliage ? foliageChannel(material) : surface.clearcoat;", surface);
        Assert.Contains("outEmissiveClearcoat = vec4(surface.emissive, foliage ? foliageChannel(material) : 0.0);", surface);
    }

    [Fact]
    public void The_lighting_reads_the_negative_channel_as_foliage_and_its_size_as_the_translucency()
    {
        string lighting = Shader("lighting.frag");

        Assert.Contains("float translucency = max(-emissiveClearcoat.a, 0.0);", lighting);
        Assert.Contains("bool foliage = emissiveClearcoat.a < 0.0;", lighting);
    }

    private static string Shader(string name)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "src", "Rendition", "Shaders", name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException($"src/Rendition/Shaders/{name} isn't in any folder above the tests.");
    }
}
