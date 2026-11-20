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
    public void Out_of_the_box_every_effect_is_drawn_without_vsync_or_a_frame_limit()
    {
        var graphics = new Graphics();

        Assert.Equal(RenderEffects.None, GraphicsSwitches.Off(graphics));
        Assert.False(graphics.Screen.Vsync);
        Assert.Equal(0, graphics.Screen.MaxFramesPerSecond);
        Assert.Equal(2048, graphics.Shadowing.ShadowResolution);
        Assert.Equal(1f, graphics.Screen.RenderScale);
        Assert.Equal(ProbeQuality.High, graphics.Lighting.ProbeQuality);
        Assert.Equal(ReflectionResolution.Half, graphics.Lighting.ReflectionResolution);
        Assert.Equal(OcclusionResolution.Half, graphics.Lighting.OcclusionResolution);
    }

    [Fact]
    public void An_undefined_occlusion_resolution_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Lighting = { OcclusionResolution = (OcclusionResolution)7 } });

        Assert.StartsWith("Occlusion resolution can be Half or Full.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Only_ultra_works_out_occlusion_at_full_resolution()
    {
        Assert.Equal(OcclusionResolution.Full, Graphics.For(GraphicsQuality.Ultra).Lighting.OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.High).Lighting.OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.Medium).Lighting.OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.For(GraphicsQuality.Low).Lighting.OcclusionResolution);
        Assert.Equal(OcclusionResolution.Half, Graphics.Fastest().Lighting.OcclusionResolution);
    }

    [Fact]
    public void A_changed_occlusion_resolution_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Lighting.OcclusionResolution = OcclusionResolution.Full;

        Assert.Equal(OcclusionResolution.Full, WindowFrames.RendererSettings(host.Graphics, hidden: false).OcclusionResolution);
    }

    [Fact]
    public void An_undefined_reflection_resolution_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Lighting = { ReflectionResolution = (ReflectionResolution)7 } });

        Assert.StartsWith("Reflection resolution can be Half or Full.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Only_ultra_traces_reflections_at_full_resolution()
    {
        Assert.Equal(ReflectionResolution.Full, Graphics.For(GraphicsQuality.Ultra).Lighting.ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.High).Lighting.ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.Medium).Lighting.ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.For(GraphicsQuality.Low).Lighting.ReflectionResolution);
        Assert.Equal(ReflectionResolution.Half, Graphics.Fastest().Lighting.ReflectionResolution);
    }

    [Fact]
    public void A_changed_reflection_resolution_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Lighting.ReflectionResolution = ReflectionResolution.Full;

        Assert.Equal(ReflectionResolution.Full, WindowFrames.RendererSettings(host.Graphics, hidden: false).ReflectionResolution);
    }

    [Fact]
    public void An_undefined_probe_quality_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Lighting = { ProbeQuality = (ProbeQuality)7 } });

        Assert.StartsWith("Probe quality can be Low, Medium or High.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Fastest_sets_probe_quality_to_low_and_light_probes_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(ProbeQuality.Low, graphics.Lighting.ProbeQuality);
        Assert.True(GraphicsSwitches.Off(graphics).HasFlag(RenderEffects.LightProbes));
    }

    [Fact]
    public void A_changed_probe_quality_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Lighting.ProbeQuality = ProbeQuality.Medium;

        Assert.Equal(ProbeQuality.Medium, WindowFrames.RendererSettings(host.Graphics, hidden: false).ProbeQuality);
    }

    [Fact]
    public void A_debug_view_the_game_sets_reaches_the_renderer_next_frame()
    {
        using var host = new GameHost<Settings>(1, 30, null, graphics: new Graphics());
        Assert.Equal(DebugView.None, WindowFrames.RendererSettings(host.Graphics, hidden: false).DebugView);

        host.Game.Graphics.DebugView = DebugView.Probes;

        Assert.Equal(DebugView.Probes, WindowFrames.RendererSettings(host.Graphics, hidden: false).DebugView);
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

        Assert.Equal(expected, WindowFrames.RendererSettings(graphics, hidden: false, commandLineDebugView: commandLine).DebugView);
    }

    [Fact]
    public void A_trace_view_the_game_sets_is_ignored_on_a_gpu_that_cannot_trace()
    {
        var graphics = new Graphics { DebugView = DebugView.Trace };

        Assert.Equal(DebugView.None, WindowFrames.RendererSettings(graphics, hidden: false, canTrace: false).DebugView);
        Assert.Equal(DebugView.Trace, WindowFrames.RendererSettings(graphics, hidden: false, canTrace: true).DebugView);
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
        var graphics = new Graphics { Screen = { Vsync = vsync } };

        Assert.Equal(expected, WindowFrames.RendererSettings(graphics, hidden, takingShot).Vsync);
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
        Assert.Equal((sceneWidth, sceneHeight), RenderSettings.SceneSize(width, height, scale));
    }

    [Fact]
    public void At_render_scale_one_the_scene_is_the_window_size_even_when_odd()
    {
        Assert.Equal((1601, 901), RenderSettings.SceneSize(1601, 901, 1f));
    }

    [Fact]
    public void A_tiny_window_at_half_scale_still_has_a_scene_to_draw()
    {
        Assert.Equal((2, 2), RenderSettings.SceneSize(1, 1, 0.5f));
    }

    [Theory]
    [InlineData(7680, 4320, 8192, 8192, 4608)]
    [InlineData(3000, 1000, 4000, 4000, 1332)]
    [InlineData(1000, 3000, 4000, 1332, 4000)]
    [InlineData(2048, 1000, 4096, 4096, 2000)]
    public void A_scene_larger_than_the_device_allows_shrinks_to_fit_keeping_its_shape(
        int width, int height, int maxSize, int sceneWidth, int sceneHeight)
    {
        Assert.Equal((sceneWidth, sceneHeight), RenderSettings.SceneSize(width, height, 2f, maxSize));
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

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.Screen.RenderScale = scale);

            Assert.Equal($"RenderScale must be from 0.5 to 2, not {shown}. (Parameter 'value')", error.Message);
            Assert.Equal(1f, graphics.Screen.RenderScale);
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
        var graphics = new Graphics { Screen = { RenderScale = scale } };

        Assert.Equal(scale, graphics.Screen.RenderScale);
    }

    [Fact]
    public void A_changed_render_scale_reaches_the_renderer_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Screen.RenderScale = 1.5f;

        // What the window hands the renderer before the next frame, which draws the scene this size.
        float scale = WindowFrames.RendererSettings(host.Graphics, hidden: false).RenderScale;
        Assert.Equal((2400, 1350), RenderSettings.SceneSize(1600, 900, scale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    [InlineData(8192)]
    public void Shadow_resolution_must_be_1024_2048_or_4096(int resolution)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Shadowing = { ShadowResolution = resolution } });

        Assert.StartsWith("Shadow maps can be 1024, 2048 or 4096 texels across.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Shadow_resolution_takes_each_of_the_three_sizes(int resolution)
    {
        var graphics = new Graphics { Shadowing = { ShadowResolution = resolution } };

        Assert.Equal(resolution, graphics.Shadowing.ShadowResolution);
    }

    [Fact]
    public void Changing_shadow_resolution_while_running_is_applied_next_frame()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Shadowing.ShadowResolution = 4096;

        // What the window hands the renderer before the next frame.
        Assert.Equal(4096, WindowFrames.RendererSettings(host.Graphics, hidden: false).ShadowResolution);
    }

    [Theory]
    [InlineData(nameof(Graphics.Shadowing), nameof(GraphicsShadowing.SunShadows), RenderEffects.SunShadows)]
    [InlineData(nameof(Graphics.Shadowing), nameof(GraphicsShadowing.LightShadows), RenderEffects.LightShadows)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.AmbientOcclusion), RenderEffects.AmbientOcclusion)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.Reflections), RenderEffects.Reflections)]
    [InlineData(nameof(Graphics.Screen), nameof(GraphicsScreen.AntiAliasing), RenderEffects.AntiAliasing)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.Bloom), RenderEffects.Bloom)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Materials), RenderEffects.Materials)]
    [InlineData(nameof(Graphics.Shadowing), nameof(GraphicsShadowing.ContactShadows), RenderEffects.ContactShadows)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.LightProbes), RenderEffects.LightProbes)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.MultiscatterSpecular), RenderEffects.MultiscatterSpecular)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.WearAndDirt), RenderEffects.WearAndDirt)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.Fog), RenderEffects.Fog)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.AutoExposure), RenderEffects.AutoExposure)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.DepthOfField), RenderEffects.DepthOfField)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Decals), RenderEffects.Decals)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.VolumetricFog), RenderEffects.VolumetricFog)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Puddles), RenderEffects.Puddles)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Wind), RenderEffects.Wind)]
    [InlineData(nameof(Graphics.Baking), nameof(GraphicsBaking.BakedLighting), RenderEffects.BakedLighting)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.HeightMaps), RenderEffects.HeightMaps)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.Specular), RenderEffects.Specular)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.EnvironmentLight), RenderEffects.EnvironmentLight)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.Emissive), RenderEffects.Emissive)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.Vignette), RenderEffects.Vignette)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.Grain), RenderEffects.Grain)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.Sharpening), RenderEffects.Sharpening)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.ColorGrading), RenderEffects.ColorGrading)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.ColorLut), RenderEffects.ColorLut)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.ChromaticAberration), RenderEffects.ChromaticAberration)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.SpeedBlur), RenderEffects.SpeedBlur)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.Flashes), RenderEffects.Flashes)]
    [InlineData(nameof(Graphics.Picture), nameof(GraphicsPicture.ScreenEffects), RenderEffects.ScreenEffects)]
    [InlineData(nameof(Graphics.Drawing), nameof(GraphicsDrawing.Culling), RenderEffects.Culling)]
    [InlineData(nameof(Graphics.Drawing), nameof(GraphicsDrawing.SimplerModels), RenderEffects.SimplerModels)]
    [InlineData(nameof(Graphics.Drawing), nameof(GraphicsDrawing.DepthFirst), RenderEffects.DepthFirst)]
    [InlineData(nameof(Graphics.Drawing), nameof(GraphicsDrawing.Fades), RenderEffects.Fades)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Subsurface), RenderEffects.Subsurface)]
    [InlineData(nameof(Graphics.Surfaces), nameof(GraphicsSurfaces.Particles), RenderEffects.Particles)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.LocalLights), RenderEffects.LocalLights)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.Haze), RenderEffects.Haze)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.Underwater), RenderEffects.Underwater)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.FogVolumes), RenderEffects.FogVolumes)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.ScreenBounce), RenderEffects.ScreenBounce)]
    [InlineData(nameof(Graphics.Lighting), nameof(GraphicsLighting.OcclusionHistory), RenderEffects.OcclusionHistory)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.AerialPerspective), RenderEffects.AerialPerspective)]
    [InlineData(nameof(Graphics.Atmosphere), nameof(GraphicsAtmosphere.Stars), RenderEffects.Stars)]
    public void Each_switch_turned_off_leaves_out_only_its_own_effect(string group, string setting, RenderEffects effect)
    {
        var graphics = new Graphics();
        object settings = typeof(Graphics).GetProperty(group)!.GetValue(graphics)!;
        settings.GetType().GetProperty(setting)!.SetValue(settings, false);

        Assert.Equal(effect, GraphicsSwitches.Off(graphics));
        Assert.True(RenderEffects.All.HasFlag(effect));
    }

    // A bake is cheaper than lighting live, so the fastest settings keep it.
    [Fact]
    public void Fastest_leaves_out_every_effect_that_costs_time_and_keeps_vsync_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(
            RenderEffects.SunShadows | RenderEffects.LightShadows | RenderEffects.AmbientOcclusion
            | RenderEffects.Reflections | RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.ContactShadows
            | RenderEffects.LightProbes | RenderEffects.MultiscatterSpecular | RenderEffects.WearAndDirt | RenderEffects.Fog
            | RenderEffects.AutoExposure | RenderEffects.DepthOfField | RenderEffects.Decals | RenderEffects.VolumetricFog
            | RenderEffects.Puddles | RenderEffects.Wind | RenderEffects.HeightMaps | RenderEffects.Vignette
            | RenderEffects.Grain | RenderEffects.Sharpening | RenderEffects.Subsurface | RenderEffects.FogVolumes
            | RenderEffects.ScreenBounce | RenderEffects.OcclusionHistory | RenderEffects.ChromaticAberration | RenderEffects.SpeedBlur,
            GraphicsSwitches.Off(graphics));
        Assert.True(graphics.Surfaces.Materials);
        Assert.True(graphics.Baking.BakedLighting);

        // These cost nothing, or turning them off costs time, so the fastest settings keep them.
        Assert.True(graphics.Lighting.Specular);
        Assert.True(graphics.Lighting.EnvironmentLight);
        Assert.True(graphics.Lighting.Emissive);
        Assert.True(graphics.Picture.ColorGrading);
        Assert.True(graphics.Picture.ColorLut);
        Assert.True(graphics.Picture.Flashes);
        Assert.True(graphics.Picture.ScreenEffects);
        Assert.True(graphics.Drawing.Culling);
        Assert.True(graphics.Drawing.SimplerModels);
        Assert.True(graphics.Drawing.DepthFirst);
        Assert.True(graphics.Drawing.Fades);
        Assert.True(graphics.Surfaces.Particles);
        Assert.True(graphics.Lighting.LocalLights);
        Assert.True(graphics.Atmosphere.Haze);
        Assert.True(graphics.Atmosphere.AerialPerspective);
        Assert.True(graphics.Atmosphere.Stars);
        Assert.True(graphics.Atmosphere.Underwater);
        Assert.False(graphics.Screen.Vsync);

        // Dropping the resolution would be a change the game can see, so it stays the game's choice.
        Assert.Equal(1f, graphics.Screen.RenderScale);
    }

    [Fact]
    public void A_changed_upscaler_reaches_the_renderer_next_frame()
    {
        var given = new Graphics { Screen = { Upscaler = Upscaler.Taa } };
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Screen.Upscaler = Upscaler.Fsr;
        Assert.Equal(Upscaler.Fsr, WindowFrames.RendererSettings(host.Graphics, hidden: false).Upscaler);

        host.Game.Graphics.Screen.Upscaler = Upscaler.Taa;
        Assert.Equal(Upscaler.Taa, WindowFrames.RendererSettings(host.Graphics, hidden: false).Upscaler);
    }

    [Fact]
    public void A_negative_frame_limit_is_refused_with_how_to_have_none()
    {
        var graphics = new Graphics();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.Screen.MaxFramesPerSecond = -30);

        Assert.StartsWith("The frame limit can't be negative. Use 0 for no limit.", error.Message);
        Assert.Equal(0, graphics.Screen.MaxFramesPerSecond);
    }

    [Fact]
    public void A_game_that_asks_for_graphics_changes_the_settings_the_window_draws_with()
    {
        var given = new Graphics();
        using var host = new GameHost<Settings>(1, 30, null, graphics: given);

        host.Game.Graphics.Picture.Bloom = false;

        Assert.Same(given, host.Graphics);
        Assert.Equal(RenderEffects.Bloom, GraphicsSwitches.Off(host.Graphics));
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
            var graphics = new Graphics { Baking = { BakeBudget = 3f } };

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => graphics.Baking.BakeBudget = budget);

            Assert.Equal(
                $"BakeBudget must be from 0.5 to 33 milliseconds, not {shown}. Use null for the default. (Parameter 'value')",
                error.Message);
            Assert.Equal(3f, graphics.Baking.BakeBudget);
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
        var graphics = new Graphics { Baking = { BakeBudget = budget } };
        Assert.Equal(budget, graphics.Baking.BakeBudget);

        graphics.Baking.BakeBudget = null;
        Assert.Null(graphics.Baking.BakeBudget);
    }
}
