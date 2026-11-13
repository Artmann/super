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

    [Fact]
    public void A_surface_is_not_foliage_lets_a_leafs_share_of_light_through_and_stands_still_unless_told()
    {
        var surface = new Surface();

        Assert.False(surface.Foliage);
        Assert.Equal(0.6f, surface.Translucency);
        Assert.Equal(0f, surface.Wind);
    }

    [Fact]
    public void A_new_material_lets_a_leafs_share_of_light_through()
    {
        Assert.Equal(0.6f, new Material().Translucency);
        Assert.Equal(0.6f, new Material { Flags = MaterialFlags.Foliage }.Translucency);
    }

    [Fact]
    public void Foliage_sends_its_translucency_to_the_shader_clamped_to_0_to_1()
    {
        Assert.Equal(0.35f, Rendition.Vulkan.ResourceTable.Transmission(new Material { Flags = MaterialFlags.Foliage, Translucency = 0.35f }));
        Assert.Equal(1f, Rendition.Vulkan.ResourceTable.Transmission(new Material { Flags = MaterialFlags.Foliage, Translucency = 1.5f }));
        Assert.Equal(0f, Rendition.Vulkan.ResourceTable.Transmission(new Material { Flags = MaterialFlags.Foliage, Translucency = -1f }));
    }

    [Fact]
    public void A_scattering_surface_sends_its_own_transmission_and_a_plain_one_none()
    {
        var subsurface = new Subsurface { Radius = 0.01f, Transmission = 0.25f };

        Assert.Equal(0.25f, Rendition.Vulkan.ResourceTable.Transmission(new Material { Subsurface = subsurface, Translucency = 0.9f }));
        Assert.Equal(0f, Rendition.Vulkan.ResourceTable.Transmission(new Material { Translucency = 0.9f }));
    }

    [Theory]
    [InlineData(0.7f, 0.7f)]
    [InlineData(-1f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void A_materials_sway_is_its_wind_but_never_below_0_or_not_a_number(float wind, float sway)
    {
        Assert.Equal(sway, Rendition.Vulkan.ResourceTable.Sway(new Material { Wind = wind }));
    }

    [Fact]
    public void A_cooked_models_foliage_translucency_and_wind_reach_its_material()
    {
        TextureHandle[] textures = [new(40), new(41)];
        var source = new ModelMaterial
        {
            BaseColorTexture = 1,
            NormalTexture = -1,
            SurfaceTexture = -1,
            EmissiveTexture = -1,
            HeightTexture = -1,
            DetailColorTexture = -1,
            DetailNormalTexture = 0,
            Flags = MaterialFlags.Foliage,
            Translucency = 0.45f,
            Wind = 0.8f,
        };

        var material = Renderer.MaterialOf(source, textures);

        Assert.Equal(MaterialFlags.Foliage, material.Flags);
        Assert.Equal(0.45f, material.Translucency);
        Assert.Equal(0.8f, material.Wind);
        Assert.Equal(new TextureHandle(41), material.BaseColorTexture);
        Assert.Equal(new TextureHandle(40), material.DetailNormalTexture);
        Assert.Null(material.NormalTexture);
    }

    [Fact]
    public void A_surfaces_foliage_translucency_and_wind_reach_its_material()
    {
        var surface = new Surface { Foliage = true, Translucency = 0.3f, Wind = 0.5f, Puddles = true };

        var material = SceneRenderer.MaterialOf(surface);

        Assert.Equal(0.3f, material.Translucency);
        Assert.Equal(0.5f, material.Wind);
        Assert.Equal(MaterialFlags.Foliage | MaterialFlags.TwoSided | MaterialFlags.Puddles, material.Flags);
        Assert.Equal(MaterialFlags.None, SceneRenderer.FlagsOf(new Surface()));
    }

    [Fact]
    public void A_glass_surface_keeps_its_other_switches_and_stays_see_through()
    {
        var material = SceneRenderer.MaterialOf(new Surface { Opacity = 0.4f, WorldVariation = true });

        Assert.Equal(MaterialFlags.Transparent | MaterialFlags.WorldVariation, material.Flags);
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
