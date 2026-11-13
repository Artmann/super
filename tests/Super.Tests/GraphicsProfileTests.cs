using System.Globalization;
using System.Reflection;
using Rendition;

namespace Super.Tests;

public class GraphicsProfileTests
{
    private const long Gigabyte = 1L << 30;

    // Every setting moved away from its default, so a copy or a round trip that drops one shows.
    private static Graphics Unusual() => new()
    {
        Vsync = false,
        MaxFramesPerSecond = 90,
        SunShadows = false,
        ShadowResolution = 4096,
        ShadowCascades = 2,
        MeshDetail = 1.5f,
        RenderScale = 0.75f,
        Upscaler = Upscaler.Fsr,
        ContactShadows = false,
        LightProbes = false,
        ProbeQuality = ProbeQuality.Medium,
        LightShadows = false,
        AmbientOcclusion = false,
        MultiscatterSpecular = false,
        Reflections = false,
        AntiAliasing = false,
        Bloom = false,
        Fog = false,
        AutoExposure = false,
        DepthOfField = false,
        Decals = false,
        VolumetricFog = false,
        Materials = false,
        WearAndDirt = false,
        Puddles = false,
        Wind = false,
        BakedLighting = false,
        BakeOnArrival = false,
        BakeBudget = 3f,
        DebugView = DebugView.Probes,
    };

    private static PropertyInfo[] Settings() =>
        typeof(Graphics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.Name != nameof(Graphics.DebugView))
            .ToArray();

    [Fact]
    public void Unusual_moves_every_setting_away_from_its_default()
    {
        // Guards the tests below: a setting added to Graphics but not to Unusual would pass them unchecked.
        var defaults = new Graphics();
        var unusual = Unusual();

        Assert.NotEmpty(Settings());
        Assert.All(Settings(), property => Assert.NotEqual(property.GetValue(defaults), property.GetValue(unusual)));
    }

    [Fact]
    public void Apply_copies_every_setting_but_the_debug_view()
    {
        var graphics = new Graphics { DebugView = DebugView.Trace };

        graphics.Apply(Unusual());

        Assert.All(Settings(), property => Assert.Equal(property.GetValue(Unusual()), property.GetValue(graphics)));
        Assert.Equal(DebugView.Trace, graphics.DebugView);
    }

    [Fact]
    public void A_profile_round_trips_through_json()
    {
        var read = Graphics.FromJson(Unusual().ToJson());

        Assert.All(Settings(), property => Assert.Equal(property.GetValue(Unusual()), property.GetValue(read)));
        Assert.Equal(DebugView.None, read.DebugView);
    }

    [Fact]
    public void A_profile_writes_choices_by_name_and_leaves_out_the_debug_view()
    {
        string json = Unusual().ToJson();

        Assert.Contains("\"Upscaler\": \"Fsr\"", json);
        Assert.Contains("\"ProbeQuality\": \"Medium\"", json);
        Assert.DoesNotContain("DebugView", json);
    }

    [Fact]
    public void A_profile_that_names_a_few_settings_keeps_the_defaults_for_the_rest()
    {
        var read = Graphics.FromJson("""{ "Bloom": false, "ShadowCascades": 2 }""");

        Assert.False(read.Bloom);
        Assert.Equal(2, read.ShadowCascades);
        Assert.True(read.Fog);
        Assert.Equal(2048, read.ShadowResolution);
        Assert.Equal(1f, read.MeshDetail);
    }

    [Fact]
    public void A_profile_with_a_base_starts_from_that_preset()
    {
        var read = Graphics.FromJson("""{ "Base": "Low", "Reflections": true, "ShadowCascades": 3 }""");

        // Low's own values, by hand: three quarters scale, 1024 shadows, half detail, ambient occlusion off.
        Assert.Equal(0.75f, read.RenderScale);
        Assert.Equal(1024, read.ShadowResolution);
        Assert.Equal(0.5f, read.MeshDetail);
        Assert.False(read.AmbientOcclusion);
        Assert.False(read.VolumetricFog);
        // And the profile's, over them.
        Assert.True(read.Reflections);
        Assert.Equal(3, read.ShadowCascades);
    }

    [Theory]
    [InlineData("Low", GraphicsQuality.Low)]
    [InlineData("medium", GraphicsQuality.Medium)]
    [InlineData("High", GraphicsQuality.High)]
    [InlineData("ULTRA", GraphicsQuality.Ultra)]
    public void A_base_alone_gives_the_preset_it_names(string name, GraphicsQuality quality)
    {
        var read = Graphics.FromJson($$"""{ "Base": "{{name}}" }""");

        Assert.All(Settings(), property => Assert.Equal(property.GetValue(Graphics.For(quality)), property.GetValue(read)));
    }

    [Fact]
    public void A_profile_can_start_from_fastest()
    {
        var read = Graphics.FromJson("""{ "Base": "Fastest", "Bloom": true }""");

        Assert.False(read.Vsync);
        Assert.False(read.Fog);
        Assert.Equal(0.5f, read.MeshDetail);
        Assert.True(read.Bloom);
    }

    [Theory]
    [InlineData("\"Potato\"", "\"Potato\"")]
    [InlineData("2", "2")]
    [InlineData("\"2\"", "\"2\"")]
    [InlineData("null", "null")]
    public void A_base_that_is_not_a_preset_is_refused(string value, string shown)
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson($$"""{ "Base": {{value}} }""", "low.json"));

        Assert.Equal($"low.json has a Base of {shown}, but it can be Low, Medium, High, Ultra or Fastest.", error.Message);
    }

    [Fact]
    public void A_profile_with_a_base_still_refuses_names_it_does_not_know()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Base": "Low", "Blooom": false }""", "low.json"));

        Assert.StartsWith("low.json isn't a graphics profile: ", error.Message);
        Assert.Contains("Blooom", error.Message);
    }

    [Fact]
    public void A_profile_with_a_base_still_refuses_values_out_of_range()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Base": "Low", "ShadowCascades": 6 }""", "low.json"));

        Assert.StartsWith("low.json has a setting out of range: The sun can draw from 1 to 4 shadow cascades.", error.Message);
    }

    [Fact]
    public void A_profile_with_a_setting_it_does_not_know_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Blooom": false }""", "low.json"));

        Assert.StartsWith("low.json isn't a graphics profile: ", error.Message);
        Assert.Contains("Blooom", error.Message);
    }

    [Fact]
    public void A_profile_with_a_setting_out_of_range_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "ShadowCascades": 6 }""", "low.json"));

        Assert.StartsWith("low.json has a setting out of range: The sun can draw from 1 to 4 shadow cascades.", error.Message);
    }

    [Fact]
    public void An_empty_profile_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("null", "low.json"));

        Assert.StartsWith("low.json is empty.", error.Message);
    }

    [Fact]
    public void A_saved_profile_loads_back()
    {
        string folder = Directory.CreateTempSubdirectory("super-graphics-").FullName;
        try
        {
            string path = Path.Combine(folder, "mine.json");
            Unusual().Save(path);

            var read = Graphics.Load(path);

            Assert.All(Settings(), property => Assert.Equal(property.GetValue(Unusual()), property.GetValue(read)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void High_is_the_default_graphics()
    {
        var high = Graphics.For(GraphicsQuality.High);
        var defaults = new Graphics();

        Assert.All(Settings(), property => Assert.Equal(property.GetValue(defaults), property.GetValue(high)));
    }

    [Fact]
    public void Each_quality_up_draws_at_least_as_much_as_the_one_below()
    {
        GraphicsQuality[] qualities = [GraphicsQuality.Low, GraphicsQuality.Medium, GraphicsQuality.High, GraphicsQuality.Ultra];
        for (int i = 1; i < qualities.Length; i++)
        {
            var lower = Graphics.For(qualities[i - 1]);
            var higher = Graphics.For(qualities[i]);
            string pair = $"{qualities[i - 1]} against {qualities[i]}";

            Assert.True((higher.Off & ~lower.Off) == RenderEffects.None, $"{pair}: the higher turns off {higher.Off & ~lower.Off}.");
            Assert.True(higher.ShadowResolution >= lower.ShadowResolution, pair);
            Assert.True(higher.ShadowCascades >= lower.ShadowCascades, pair);
            Assert.True(higher.MeshDetail >= lower.MeshDetail, pair);
            Assert.True(higher.RenderScale >= lower.RenderScale, pair);
            Assert.True(higher.ProbeQuality >= lower.ProbeQuality, pair);
        }
    }

    [Fact]
    public void Low_turns_down_what_costs_the_most()
    {
        var low = Graphics.For(GraphicsQuality.Low);

        Assert.Equal(0.75f, low.RenderScale);
        Assert.Equal(1024, low.ShadowResolution);
        Assert.Equal(2, low.ShadowCascades);
        Assert.Equal(0.5f, low.MeshDetail);
        Assert.Equal(1f, low.BakeBudget);
        Assert.True(low.Off.HasFlag(RenderEffects.VolumetricFog));
        Assert.True(low.Off.HasFlag(RenderEffects.Reflections));
        Assert.True(low.Off.HasFlag(RenderEffects.AmbientOcclusion));
    }

    [Theory]
    [InlineData(GraphicsQuality.Low)]
    [InlineData(GraphicsQuality.Medium)]
    [InlineData(GraphicsQuality.High)]
    [InlineData(GraphicsQuality.Ultra)]
    public void Every_quality_keeps_vsync_on_and_no_frame_limit(GraphicsQuality quality)
    {
        var graphics = Graphics.For(quality);

        Assert.True(graphics.Vsync);
        Assert.Equal(0, graphics.MaxFramesPerSecond);
    }

    [Fact]
    public void An_undefined_quality_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Graphics.For((GraphicsQuality)9));

        Assert.StartsWith("Graphics quality can be Low, Medium, High or Ultra.", error.Message);
    }

    [Theory]
    [InlineData(GpuKind.Integrated, 16, GraphicsQuality.Low)]
    [InlineData(GpuKind.Virtual, 16, GraphicsQuality.Low)]
    [InlineData(GpuKind.Discrete, 2, GraphicsQuality.Low)]
    [InlineData(GpuKind.Discrete, 4, GraphicsQuality.Medium)]
    [InlineData(GpuKind.Discrete, 6, GraphicsQuality.High)]
    [InlineData(GpuKind.Discrete, 8, GraphicsQuality.High)]
    [InlineData(GpuKind.Discrete, 12, GraphicsQuality.Ultra)]
    [InlineData(GpuKind.Discrete, 24, GraphicsQuality.Ultra)]
    [InlineData(GpuKind.Unknown, 0, GraphicsQuality.High)]
    public void Recommends_a_quality_from_the_kind_of_gpu_and_its_memory(GpuKind kind, int gigabytes, GraphicsQuality expected)
    {
        var gpu = new GpuInfo("Test GPU", kind, gigabytes * Gigabyte, CanTrace: false);

        Assert.Equal(expected, Graphics.Recommend(gpu));
    }

    [Fact]
    public void A_card_just_under_six_gigabytes_gets_medium()
    {
        var gpu = new GpuInfo("Test GPU", GpuKind.Discrete, 6 * Gigabyte - 1, CanTrace: true);

        Assert.Equal(GraphicsQuality.Medium, Graphics.Recommend(gpu));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Shadow_cascades_outside_one_to_four_are_refused(int cascades)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { ShadowCascades = cascades });

        Assert.StartsWith("The sun can draw from 1 to 4 shadow cascades.", error.Message);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(2.5f)]
    public void Mesh_detail_out_of_range_is_explained_with_invariant_numbers_under_swedish_culture(float detail)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { MeshDetail = detail });

            Assert.StartsWith("Mesh detail can be from 0.5 to 2.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Shadow_cascades_and_mesh_detail_reach_the_renderer()
    {
        var graphics = new Graphics { ShadowCascades = 2, MeshDetail = 0.75f };

        var settings = GameWindow.RendererSettings(graphics, hidden: false);

        Assert.Equal(2, settings.ShadowCascades);
        Assert.Equal(0.75f, settings.MeshDetail);
    }

    [Fact]
    public void An_unknown_gpu_recommends_the_default()
    {
        var gpu = new Gpu();

        Assert.Equal(GpuKind.Unknown, gpu.Kind);
        Assert.Equal(GraphicsQuality.High, gpu.RecommendedQuality);
    }
}
