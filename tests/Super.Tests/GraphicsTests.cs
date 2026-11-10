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
    public void Each_switch_turned_off_leaves_out_only_its_own_effect(string setting, RenderEffects effect)
    {
        var graphics = new Graphics();
        typeof(Graphics).GetProperty(setting)!.SetValue(graphics, false);

        Assert.Equal(effect, graphics.Off);
    }

    [Fact]
    public void Fastest_leaves_out_every_effect_but_keeps_the_materials_and_turns_vsync_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(
            RenderEffects.SunShadows | RenderEffects.LightShadows | RenderEffects.AmbientOcclusion
            | RenderEffects.Reflections | RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.ContactShadows
            | RenderEffects.LightProbes | RenderEffects.MultiscatterSpecular | RenderEffects.WearAndDirt | RenderEffects.Fog
            | RenderEffects.AutoExposure | RenderEffects.DepthOfField | RenderEffects.Decals | RenderEffects.VolumetricFog
            | RenderEffects.Puddles,
            graphics.Off);
        Assert.True(graphics.Materials);
        Assert.False(graphics.Vsync);

        // Dropping the resolution would be a change the game can see, so it stays the game's choice.
        Assert.Equal(1f, graphics.RenderScale);
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
}
