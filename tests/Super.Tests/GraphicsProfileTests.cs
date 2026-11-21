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
        Screen = { Vsync = true, MaxFramesPerSecond = 90, RenderScale = 0.75f, Upscaler = Upscaler.Fsr, AntiAliasing = false },
        Shadowing = { SunShadows = false, ShadowResolution = 4096, ShadowCascades = 2, ContactShadows = false, LightShadows = false },
        Lighting =
        {
            LightProbes = false,
            ProbeQuality = ProbeQuality.Medium,
            ReflectionResolution = ReflectionResolution.Full,
            OcclusionResolution = OcclusionResolution.Full,
            AmbientOcclusion = false,
            MultiscatterSpecular = false,
            Reflections = false,
            PlanarReflections = false,
            Specular = false,
            EnvironmentLight = false,
            Emissive = false,
            LocalLights = false,
            ScreenBounce = false,
            OcclusionHistory = false,
        },
        Atmosphere =
        {
            Fog = false,
            VolumetricFog = false,
            VolumetricFogCellSize = 16,
            VolumetricFogSlices = 32,
            Haze = false,
            AerialPerspective = false,
            Stars = false,
            Underwater = false,
            FogVolumes = false,
            Clouds = false,
            VolumetricClouds = false,
            CloudShadows = false,
        },
        Surfaces =
        {
            Decals = false,
            Materials = false,
            WearAndDirt = false,
            Puddles = false,
            Wind = false,
            HeightMaps = false,
            Subsurface = false,
            Particles = false,
            Grass = false,
        },
        Picture =
        {
            Bloom = false,
            AutoExposure = false,
            DepthOfField = false,
            Vignette = false,
            Grain = false,
            Sharpening = false,
            ColorGrading = false,
            ColorLut = false,
            ChromaticAberration = false,
            SpeedBlur = false,
            Flashes = false,
            ScreenEffects = false,
            LensFlare = false,
            Paint = false,
        },
        Baking = { BakedLighting = false, BakeOnArrival = false, BakeBudget = 3f },
        Drawing = { MeshDetail = 1.5f, GrassDetail = 0.5f, Culling = false, SimplerModels = false, DepthFirst = false, Fades = false },
        DebugView = DebugView.Probes,
    };

    // Every setting, as the group it's in and its place there.
    private static (PropertyInfo Group, PropertyInfo Setting)[] Settings() =>
        typeof(Graphics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(group => group.Name != nameof(Graphics.DebugView))
            .SelectMany(group => group.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(setting => setting.CanWrite)
                .Select(setting => (group, setting)))
            .ToArray();

    private static object? Value(Graphics graphics, (PropertyInfo Group, PropertyInfo Setting) setting) =>
        setting.Setting.GetValue(setting.Group.GetValue(graphics));

    [Fact]
    public void Unusual_moves_every_setting_away_from_its_default()
    {
        // Guards the tests below: a setting added to Graphics but not to Unusual would pass them unchecked.
        var defaults = new Graphics();
        var unusual = Unusual();

        Assert.Equal(68, Settings().Length);
        Assert.All(Settings(), setting => Assert.NotEqual(Value(defaults, setting), Value(unusual, setting)));
    }

    [Fact]
    public void Apply_copies_every_setting_but_the_debug_view()
    {
        var graphics = new Graphics { DebugView = DebugView.Trace };

        graphics.Apply(Unusual());

        Assert.All(Settings(), setting => Assert.Equal(Value(Unusual(), setting), Value(graphics, setting)));
        Assert.Equal(DebugView.Trace, graphics.DebugView);
    }

    [Fact]
    public void A_profile_round_trips_through_json()
    {
        var read = Graphics.FromJson(Unusual().ToJson());

        Assert.All(Settings(), setting => Assert.Equal(Value(Unusual(), setting), Value(read, setting)));
        Assert.Equal(DebugView.None, read.DebugView);
    }

    [Fact]
    public void A_profile_writes_choices_by_name_and_leaves_out_the_debug_view()
    {
        string json = Unusual().ToJson();

        Assert.Contains("\"Upscaler\": \"Fsr\"", json);
        Assert.Contains("\"ProbeQuality\": \"Medium\"", json);
        Assert.Contains("\"ReflectionResolution\": \"Full\"", json);
        Assert.Contains("\"OcclusionResolution\": \"Full\"", json);
        Assert.DoesNotContain("DebugView", json);
    }

    [Fact]
    public void A_profile_writes_each_group_as_an_object()
    {
        string json = new Graphics { Picture = { Bloom = false } }.ToJson();

        Assert.Contains("\"Picture\": {", json);
        Assert.Contains("\"Bloom\": false", json);
    }

    [Fact]
    public void A_profile_that_names_a_few_settings_keeps_the_defaults_for_the_rest()
    {
        var read = Graphics.FromJson("""{ "Picture": { "Bloom": false }, "Shadowing": { "ShadowCascades": 2 } }""");

        Assert.False(read.Picture.Bloom);
        Assert.True(read.Picture.Vignette);
        Assert.Equal(2, read.Shadowing.ShadowCascades);
        Assert.True(read.Atmosphere.Fog);
        Assert.Equal(2048, read.Shadowing.ShadowResolution);
        Assert.Equal(1f, read.Drawing.MeshDetail);
    }

    [Fact]
    public void A_profile_that_extends_a_preset_starts_from_it()
    {
        var read = Graphics.FromJson("""{ "Extends": "Low", "Lighting": { "Reflections": true }, "Shadowing": { "ShadowCascades": 3 } }""");

        // Low's own values, by hand: three quarters scale, 1024 shadows, half detail, ambient occlusion off.
        Assert.Equal(0.75f, read.Screen.RenderScale);
        Assert.Equal(1024, read.Shadowing.ShadowResolution);
        Assert.Equal(0.5f, read.Drawing.MeshDetail);
        Assert.False(read.Lighting.AmbientOcclusion);
        Assert.False(read.Atmosphere.VolumetricFog);
        // And the profile's, over them.
        Assert.True(read.Lighting.Reflections);
        Assert.Equal(3, read.Shadowing.ShadowCascades);
    }

    [Theory]
    [InlineData("Low", GraphicsQuality.Low)]
    [InlineData("medium", GraphicsQuality.Medium)]
    [InlineData("High", GraphicsQuality.High)]
    [InlineData("ULTRA", GraphicsQuality.Ultra)]
    public void A_profile_that_only_extends_a_preset_is_that_preset(string name, GraphicsQuality quality)
    {
        var read = Graphics.FromJson($$"""{ "Extends": "{{name}}" }""");

        Assert.All(Settings(), setting => Assert.Equal(Value(Graphics.For(quality), setting), Value(read, setting)));
    }

    [Fact]
    public void A_profile_can_extend_fastest()
    {
        var read = Graphics.FromJson("""{ "Extends": "Fastest", "Picture": { "Bloom": true } }""");

        Assert.False(read.Atmosphere.Fog);
        Assert.False(read.Picture.Vignette);
        Assert.Equal(0.5f, read.Drawing.MeshDetail);
        Assert.True(read.Picture.Bloom);
    }

    [Theory]
    [InlineData("\"Potato\"", "\"Potato\"")]
    [InlineData("2", "2")]
    [InlineData("\"2\"", "\"2\"")]
    [InlineData("null", "null")]
    public void A_profile_that_extends_something_other_than_a_preset_is_refused(string value, string shown)
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson($$"""{ "Extends": {{value}} }""", "low.json"));

        Assert.Equal($"low.json extends {shown}, but a profile can extend Low, Medium, High, Ultra or Fastest.", error.Message);
    }

    [Fact]
    public void A_profile_that_extends_a_preset_still_refuses_names_it_does_not_know()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Graphics.FromJson("""{ "Extends": "Low", "Picture": { "Blooom": false } }""", "low.json"));

        Assert.StartsWith("low.json isn't a graphics profile: ", error.Message);
        Assert.Contains("Blooom", error.Message);
    }

    [Fact]
    public void A_profile_that_extends_a_preset_still_refuses_values_out_of_range()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Graphics.FromJson("""{ "Extends": "Low", "Shadowing": { "ShadowCascades": 6 } }""", "low.json"));

        Assert.StartsWith("low.json has a setting out of range: The sun can draw from 1 to 4 shadow cascades.", error.Message);
    }

    [Fact]
    public void A_profile_with_a_setting_it_does_not_know_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Picture": { "Blooom": false } }""", "low.json"));

        Assert.StartsWith("low.json isn't a graphics profile: ", error.Message);
        Assert.Contains("Blooom", error.Message);
    }

    [Fact]
    public void A_profile_with_a_setting_outside_its_group_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Bloom": false }""", "low.json"));

        Assert.StartsWith("low.json isn't a graphics profile: ", error.Message);
        Assert.Contains("Bloom", error.Message);
    }

    [Fact]
    public void A_profile_with_a_setting_out_of_range_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Graphics.FromJson("""{ "Shadowing": { "ShadowCascades": 6 } }""", "low.json"));

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

            Assert.All(Settings(), setting => Assert.Equal(Value(Unusual(), setting), Value(read, setting)));
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

        Assert.All(Settings(), setting => Assert.Equal(Value(defaults, setting), Value(high, setting)));
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
            var (higherOff, lowerOff) = (GraphicsSwitches.Off(higher), GraphicsSwitches.Off(lower));

            Assert.True((higherOff & ~lowerOff) == RenderEffects.None, $"{pair}: the higher turns off {higherOff & ~lowerOff}.");
            Assert.True(higher.Shadowing.ShadowResolution >= lower.Shadowing.ShadowResolution, pair);
            Assert.True(higher.Shadowing.ShadowCascades >= lower.Shadowing.ShadowCascades, pair);
            Assert.True(higher.Drawing.MeshDetail >= lower.Drawing.MeshDetail, pair);
            Assert.True(higher.Drawing.GrassDetail >= lower.Drawing.GrassDetail, pair);
            Assert.True(higher.Screen.RenderScale >= lower.Screen.RenderScale, pair);
            Assert.True(higher.Lighting.ProbeQuality >= lower.Lighting.ProbeQuality, pair);
            Assert.True(higher.Lighting.ReflectionResolution >= lower.Lighting.ReflectionResolution, pair);
            Assert.True(higher.Lighting.OcclusionResolution >= lower.Lighting.OcclusionResolution, pair);
            Assert.True(higher.Atmosphere.VolumetricFogCellSize <= lower.Atmosphere.VolumetricFogCellSize, pair);
            Assert.True(higher.Atmosphere.VolumetricFogSlices >= lower.Atmosphere.VolumetricFogSlices, pair);
        }
    }

    [Fact]
    public void Low_turns_down_what_costs_the_most()
    {
        var low = Graphics.For(GraphicsQuality.Low);
        var off = GraphicsSwitches.Off(low);

        Assert.Equal(0.75f, low.Screen.RenderScale);
        Assert.Equal(1024, low.Shadowing.ShadowResolution);
        Assert.Equal(2, low.Shadowing.ShadowCascades);
        Assert.Equal(0.5f, low.Drawing.MeshDetail);
        Assert.Equal(0.5f, low.Drawing.GrassDetail);
        Assert.Equal(1f, low.Baking.BakeBudget);
        Assert.True(off.HasFlag(RenderEffects.VolumetricFog));
        Assert.True(off.HasFlag(RenderEffects.Reflections));
        Assert.True(off.HasFlag(RenderEffects.PlanarReflections));
        Assert.True(off.HasFlag(RenderEffects.AmbientOcclusion));
        Assert.True(off.HasFlag(RenderEffects.HeightMaps));
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(2.5f)]
    public void Grass_detail_out_of_range_is_explained_with_invariant_numbers_under_swedish_culture(float detail)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Drawing = { GrassDetail = detail } });

            Assert.StartsWith("Grass detail can be from 0.25 to 2.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(GraphicsQuality.Low, false)]
    [InlineData(GraphicsQuality.Medium, false)]
    [InlineData(GraphicsQuality.High, true)]
    [InlineData(GraphicsQuality.Ultra, true)]
    public void Every_quality_keeps_the_clouds_and_their_shadows_and_only_high_and_ultra_march_them(GraphicsQuality quality, bool marched)
    {
        var graphics = Graphics.For(quality);

        Assert.True(graphics.Atmosphere.Clouds);
        Assert.True(graphics.Atmosphere.CloudShadows);
        Assert.Equal(marched, graphics.Atmosphere.VolumetricClouds);
    }

    [Theory]
    [InlineData(GraphicsQuality.Low)]
    [InlineData(GraphicsQuality.Medium)]
    [InlineData(GraphicsQuality.High)]
    [InlineData(GraphicsQuality.Ultra)]
    public void Every_quality_keeps_the_water_and_the_wind(GraphicsQuality quality)
    {
        // They cost next to nothing, and without them the Courtyard's puddle is gone and its grass stands still.
        var graphics = Graphics.For(quality);

        Assert.True(graphics.Surfaces.Puddles);
        Assert.True(graphics.Surfaces.Wind);
    }

    [Theory]
    [InlineData(GraphicsQuality.Low)]
    [InlineData(GraphicsQuality.Medium)]
    [InlineData(GraphicsQuality.High)]
    [InlineData(GraphicsQuality.Ultra)]
    public void Every_quality_keeps_vsync_off_and_no_frame_limit(GraphicsQuality quality)
    {
        var graphics = Graphics.For(quality);

        Assert.False(graphics.Screen.Vsync);
        Assert.Equal(0, graphics.Screen.MaxFramesPerSecond);
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
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Shadowing = { ShadowCascades = cascades } });

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
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Drawing = { MeshDetail = detail } });

            Assert.StartsWith("Mesh detail can be from 0.5 to 2.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Shadow_cascades_mesh_detail_and_grass_detail_reach_the_renderer()
    {
        var graphics = new Graphics { Shadowing = { ShadowCascades = 2 }, Drawing = { MeshDetail = 0.75f, GrassDetail = 1.5f } };

        var settings = WindowFrames.RendererSettings(graphics, hidden: false);

        Assert.Equal(2, settings.ShadowCascades);
        Assert.Equal(0.75f, settings.MeshDetail);
        Assert.Equal(1.5f, settings.GrassDetail);
    }

    [Fact]
    public void An_unknown_gpu_recommends_the_default()
    {
        var gpu = new Gpu();

        Assert.Equal(GpuKind.Unknown, gpu.Kind);
        Assert.Equal(GraphicsQuality.High, gpu.RecommendedQuality);
    }
}
