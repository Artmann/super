using Rendition;

namespace Super.Tests;

public class GraphicsTests
{
    private sealed class Settings(Graphics graphics) : IGame
    {
        public Graphics Graphics { get; } = graphics;

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void Out_of_the_box_every_effect_is_drawn_with_vsync_and_no_frame_limit()
    {
        var graphics = new Graphics();

        Assert.Equal(RenderEffects.None, graphics.Off);
        Assert.True(graphics.Vsync);
        Assert.Equal(0, graphics.MaxFramesPerSecond);
        Assert.Equal(2048, graphics.ShadowResolution);
        Assert.Equal(1f, graphics.RenderScale);
        Assert.Equal(ProbeQuality.High, graphics.ProbeQuality);
        Assert.Equal(ReflectionResolution.Half, graphics.ReflectionResolution);
        Assert.Equal(OcclusionResolution.Half, graphics.OcclusionResolution);
    }

    [Fact]
    public void An_undefined_occlusion_resolution_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { OcclusionResolution = (OcclusionResolution)7 });

        Assert.StartsWith("Occlusion resolution can be Half or Full.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Only_ultra_works_out_occlusion_at_full_resolution()
    {
        Assert.Equal(OcclusionResolution.Full, Graphics.For(GraphicsQuality.Ultra).OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.High).OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.Medium).OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.Low).OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.Fastest().OcclusionResolution);
    }

    [Fact]
    public void A_changed_occlusion_resolution_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.OcclusionResolution = OcclusionResolution.Full;

        Assert.Equal(OcclusionResolution.Full, GameWindow.RendererSettings(host.Graphics, hidden: false).OcclusionResolution);
    }

    [Fact]
    public void An_undefined_reflection_resolution_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { ReflectionResolution = (ReflectionResolution)7 });

        Assert.StartsWith("Reflection resolution can be Half or Full.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Only_ultra_traces_reflections_at_full_resolution()
    {
        Assert.Equal(ReflectionResolution.Full, Graphics.For(GraphicsQuality.Ultra).ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.High).ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.Medium).ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.Low).ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.Fastest().ReflectionResolution);
    }

    [Fact]
    public void A_changed_reflection_resolution_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.ReflectionResolution = ReflectionResolution.Full;

        Assert.Equal(ReflectionResolution.Full, GameWindow.RendererSettings(host.Graphics, hidden: false).ReflectionResolution);
    }

    [Fact]
    public void An_undefined_probe_quality_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { ProbeQuality = (ProbeQuality)7 });

        Assert.StartsWith("Probe quality can be Low, Medium or High.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Fastest_sets_probe_quality_to_low_and_light_probes_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(ProbeQuality.Low, graphics.ProbeQuality);
        Assert.True(graphics.Off.HasFlag(RenderEffects.LightProbes));
    }

    [Fact]
    public void A_changed_probe_quality_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.ProbeQuality = ProbeQuality.Medium;

        Assert.Equal(ProbeQuality.Medium, GameWindow.RendererSettings(host.Graphics, hidden: false).ProbeQuality);
    }

    [Fact]
    public void A_debug_view_the_game_sets_reaches_the_renderer_next_frame()
    {
        using var host = new GameHost<Settings>(1, 30, null, graphics: new Graphics());
        Assert.Equal(DebugView.None, GameWindow.RendererSettings(host.Graphics, hidden: false).DebugView);

        host.Game.Graphics.DebugView = DebugView.Probes;

        Assert.Equal(DebugView.Probes, GameWindow.RendererSettings(host.Graphics, hidden: false).DebugView);
    }

    [Theory]
    [InlineData(DebugView.Probes, DebugView.Trace, DebugView.Trace)]
    [InlineData(DebugView.Trace, DebugView.Probes, DebugView.Probes)]
    [InlineData(DebugView.Probes, DebugView.None, DebugView.Probes)]
    [InlineData(DebugView.None, DebugView.Probes, DebugView.Probes)]
    [InlineData(DebugView.None, DebugView.None, DebugView.None)]
    public void The_command_line_debug_view_wins_over_the_games(DebugView game, DebugView commandLine, DebugView expected)
    {
        var graphics = new Graphics { DebugView = game };

        Assert.Equal(expected, GameWindow.RendererSettings(graphics, hidden: false, commandLineDebugView: commandLine).DebugView);
    }

    [Fact]
    public void A_trace_view_the_game_sets_is_ignored_on_a_gpu_that_cannot_trace()
    {
        var graphics = new Graphics { DebugView = DebugView.Trace };

        Assert.Equal(DebugView.None, GameWindow.RendererSettings(graphics, hidden: false, canTrace: false).DebugView);
        Assert.Equal(DebugView.Trace, GameWindow.RendererSettings(graphics, hidden: false, canTrace: true).DebugView);
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, false)]
    public void A_hidden_window_waits_for_the_display_except_for_a_screenshot(
        bool hidden, bool takingShot, bool vsync, bool expected)
    {
        var graphics = new Graphics { Vsync = vsync };

        Assert.Equal(expected, GameWindow.RendererSettings(graphics, hidden, takingShot).Vsync);
    }

    [Theory]
    [InlineData(1600, 900, 1.5f, 2400, 1350)]
    [InlineData(1600, 900, 0.5f, 800, 450)]
    [InlineData(1600, 900, 2f, 3200, 1800)]
    // 1601 * 0.5 = 800.5 and 901 * 0.5 = 450.5 land on the even 800 and 450.
    [InlineData(1601, 901, 0.5f, 800, 450)]
    // 1601 * 1.5 = 2401.5 and 901 * 1.5 = 1351.5 land on the even 2402 and 1352.
    [InlineData(1601, 901, 1.5f, 2402, 1352)]
    // 1601 * 0.75 = 1200.75, nearest whole 1201 but nearest even 1200; 901 * 0.75 = 675.75 lands on 676.
    [InlineData(1601, 901, 0.75f, 1200, 676)]
    // 1333 * 1.25 = 1666.25, nearest whole 1666 and even too; 749 * 1.25 = 936.25 lands on 936.
    [InlineData(1333, 749, 1.25f, 1666, 936)]
    // 1000 * 1.1 = 1100.0000238 as a float, and 523 * 1.1 = 575.3, nearest whole 575 but nearest even 576.
    [InlineData(1000, 523, 1.1f, 1100, 576)]
    public void Scene_size_rounds_to_even(int width, int height, float scale, int sceneWidth, int sceneHeight)
    {
        Assert.Equal((sceneWidth, sceneHeight), Renderer.SceneSize(width, height, scale));
    }

    [Fact]
    public void At_render_scale_one_the_scene_is_the_window_size_even_when_odd()
    {
        Assert.Equal((1601, 901), Renderer.SceneSize(1601, 901, 1f));
    }

    [Fact]
    public void A_tiny_window_at_half_scale_still_has_a_scene_to_draw()
    {
        Assert.Equal((2, 2), Renderer.SceneSize(1, 1, 0.5f));
    }

    [Theory]
    [InlineData(7680, 4320, 8192, 8192, 4608)]
    [InlineData(3000, 1000, 4000, 4000, 1332)]
    [InlineData(1000, 3000, 4000, 1332, 4000)]
    [InlineData(2048, 1000, 4096, 4096, 2000)]
    public void A_scene_larger_than_the_device_allows_shrinks_to_fit_keeping_its_shape(
        int width, int height, int maxSize, int sceneWidth, int sceneHeight)
    {
        Assert.Equal((sceneWidth, sceneHeight), Renderer.SceneSize(width, height, 2f, maxSize));
    }

    [Theory]
    [InlineData(0.49f, "0.49")]
    [InlineData(2.5f, "2.5")]
    [InlineData(0f, "0")]
    [InlineData(-1f, "-1")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void Render_scale_outside_half_to_two_is_refused(float scale, string shown)
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
        try
        {
            var graphics = new Graphics();

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.RenderScale = scale);

            Assert.Equal($"RenderScale must be from 0.5 to 2, not {shown}. (Parameter 'value')", error.Message);
            Assert.Equal(1f, graphics.RenderScale);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    [InlineData(1.5f)]
    public void Render_scale_takes_any_value_from_half_to_two(float scale)
    {
        var graphics = new Graphics { RenderScale = scale };

        Assert.Equal(scale, graphics.RenderScale);
    }

    [Fact]
    public void A_changed_render_scale_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.RenderScale = 1.5f;

        // What the window hands the renderer before the next frame, which draws the scene this size.
        float scale = GameWindow.RendererSettings(host.Graphics, hidden: false).RenderScale;
        Assert.Equal((2400, 1350), Renderer.SceneSize(1600, 900, scale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    [InlineData(8192)]
    public void Shadow_resolution_must_be_1024_2048_or_4096(int resolution)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { ShadowResolution = resolution });

        Assert.StartsWith("Shadow maps can be 1024, 2048 or 4096 texels across.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Shadow_resolution_takes_each_of_the_three_sizes(int resolution)
    {
        var graphics = new Graphics { ShadowResolution = resolution };

        Assert.Equal(resolution, graphics.ShadowResolution);
    }

    [Fact]
    public void Changing_shadow_resolution_while_running_is_applied_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.ShadowResolution = 4096;

        // What the window hands the renderer before the next frame.
        Assert.Equal(4096, GameWindow.RendererSettings(host.Graphics, hidden: false).ShadowResolution);
    }

    [Theory]
    [InlineData(nameof(Graphics.SunShadows), RenderEffects.SunShadows)]
    [InlineData(nameof(Graphics.LightShadows), RenderEffects.LightShadows)]
    [InlineData(nameof(Graphics.AmbientOcclusion), RenderEffects.AmbientOcclusion)]
    [InlineData(nameof(Graphics.Reflections), RenderEffects.Reflections)]
    [InlineData(nameof(Graphics.AntiAliasing), RenderEffects.AntiAliasing)]
    [InlineData(nameof(Graphics.Bloom), RenderEffects.Bloom)]
    [InlineData(nameof(Graphics.Materials), RenderEffects.Materials)]
    [InlineData(nameof(Graphics.ContactShadows), RenderEffects.ContactShadows)]
    [InlineData(nameof(Graphics.LightProbes), RenderEffects.LightProbes)]
    [InlineData(nameof(Graphics.MultiscatterSpecular), RenderEffects.MultiscatterSpecular)]
    [InlineData(nameof(Graphics.WearAndDirt), RenderEffects.WearAndDirt)]
    [InlineData(nameof(Graphics.Fog), RenderEffects.Fog)]
    [InlineData(nameof(Graphics.AutoExposure), RenderEffects.AutoExposure)]
    [InlineData(nameof(Graphics.DepthOfField), RenderEffects.DepthOfField)]
    [InlineData(nameof(Graphics.Decals), RenderEffects.Decals)]
    [InlineData(nameof(Graphics.VolumetricFog), RenderEffects.VolumetricFog)]
    [InlineData(nameof(Graphics.Puddles), RenderEffects.Puddles)]
    [InlineData(nameof(Graphics.Wind), RenderEffects.Wind)]
    [InlineData(nameof(Graphics.BakedLighting), RenderEffects.BakedLighting)]
    [InlineData(nameof(Graphics.HeightMaps), RenderEffects.HeightMaps)]
    [InlineData(nameof(Graphics.Specular), RenderEffects.Specular)]
    [InlineData(nameof(Graphics.EnvironmentLight), RenderEffects.EnvironmentLight)]
    [InlineData(nameof(Graphics.Emissive), RenderEffects.Emissive)]
    [InlineData(nameof(Graphics.Vignette), RenderEffects.Vignette)]
    [InlineData(nameof(Graphics.Grain), RenderEffects.Grain)]
    [InlineData(nameof(Graphics.Sharpening), RenderEffects.Sharpening)]
    [InlineData(nameof(Graphics.ColorGrading), RenderEffects.ColorGrading)]
    [InlineData(nameof(Graphics.Culling), RenderEffects.Culling)]
    [InlineData(nameof(Graphics.SimplerModels), RenderEffects.SimplerModels)]
    [InlineData(nameof(Graphics.DepthFirst), RenderEffects.DepthFirst)]
    public void Each_switch_turned_off_leaves_out_only_its_own_effect(string setting, RenderEffects effect)
    {
        var graphics = new Graphics();
        typeof(Graphics).GetProperty(setting)!.SetValue(graphics, false);

        Assert.Equal(effect, graphics.Off);
    }

    [Fact]
    public void Baked_lighting_is_on_by_default_and_off_maps_to_the_effect()
    {
        var graphics = new Graphics();
        Assert.True(graphics.BakedLighting);
        Assert.False(graphics.Off.HasFlag(RenderEffects.BakedLighting));

        graphics.BakedLighting = false;

        Assert.Equal(RenderEffects.BakedLighting, graphics.Off);
        Assert.True(RenderEffects.All.HasFlag(RenderEffects.BakedLighting));
    }

    // A bake is cheaper than lighting live, so the fastest settings keep it.
    [Fact]
    public void Fastest_leaves_out_every_effect_that_costs_time_and_turns_vsync_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(
            RenderEffects.SunShadows | RenderEffects.LightShadows | RenderEffects.AmbientOcclusion
            | RenderEffects.Reflections | RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.ContactShadows
            | RenderEffects.LightProbes | RenderEffects.MultiscatterSpecular | RenderEffects.WearAndDirt | RenderEffects.Fog
            | RenderEffects.AutoExposure | RenderEffects.DepthOfField | RenderEffects.Decals | RenderEffects.VolumetricFog
            | RenderEffects.Puddles | RenderEffects.Wind | RenderEffects.HeightMaps | RenderEffects.Vignette
            | RenderEffects.Grain | RenderEffects.Sharpening,
            graphics.Off);
        Assert.True(graphics.Materials);
        Assert.True(graphics.BakedLighting);

        // These cost nothing, or turning them off costs time, so the fastest settings keep them.
        Assert.True(graphics.Specular);
        Assert.True(graphics.EnvironmentLight);
        Assert.True(graphics.Emissive);
        Assert.True(graphics.ColorGrading);
        Assert.True(graphics.Culling);
        Assert.True(graphics.SimplerModels);
        Assert.True(graphics.DepthFirst);
        Assert.False(graphics.Vsync);

        // Dropping the resolution would be a change the game can see, so it stays the game's choice.
        Assert.Equal(1f, graphics.RenderScale);
    }

    [Fact]
    public void A_changed_upscaler_reaches_the_renderer_next_frame()
    {
        var given = new Graphics { Upscaler = Upscaler.Taa };
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Upscaler = Upscaler.Fsr;
        Assert.Equal(Upscaler.Fsr, GameWindow.RendererSettings(host.Graphics, hidden: false).Upscaler);

        host.Game.Graphics.Upscaler = Upscaler.Taa;
        Assert.Equal(Upscaler.Taa, GameWindow.RendererSettings(host.Graphics, hidden: false).Upscaler);
    }

    [Fact]
    public void Materials_can_be_turned_off_and_back_on_while_the_game_runs()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Materials = false;
        Assert.Equal(RenderEffects.Materials, host.Graphics.Off);

        host.Game.Graphics.Materials = true;
        Assert.Equal(RenderEffects.None, host.Graphics.Off);
    }

    [Fact]
    public void A_negative_frame_limit_is_refused_with_how_to_have_none()
    {
        var graphics = new Graphics();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.MaxFramesPerSecond = -30);

        Assert.StartsWith("The frame limit can't be negative. Use 0 for no limit.", error.Message);
        Assert.Equal(0, graphics.MaxFramesPerSecond);
    }

    [Fact]
    public void A_game_that_asks_for_graphics_changes_the_settings_the_window_draws_with()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Bloom = false;

        Assert.Same(given, host.Graphics);
        Assert.Equal(RenderEffects.Bloom, host.Graphics.Off);
    }

    [Theory]
    [InlineData(0.4f, "0.4")]
    [InlineData(34f, "34")]
    [InlineData(float.NaN, "NaN")]
    public void Bake_budget_out_of_range_throws(float budget, string shown)
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
        try
        {
            var graphics = new Graphics { BakeBudget = 3f };

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.BakeBudget = budget);

            Assert.Equal(
                $"BakeBudget must be from 0.5 to 33 milliseconds, not {shown}. Use null for the default. (Parameter 'value')",
                error.Message);
            Assert.Equal(3f, graphics.BakeBudget);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(33f)]
    public void Bake_budget_takes_the_ends_of_its_range_and_null(float budget)
    {
        var graphics = new Graphics { BakeBudget = budget };
        Assert.Equal(budget, graphics.BakeBudget);

        graphics.BakeBudget = null;
        Assert.Null(graphics.BakeBudget);
    }
}
