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
            | RenderEffects.LightProbes | RenderEffects.MultiscatterSpecular,
            graphics.Off);
        Assert.True(graphics.Materials);
        Assert.False(graphics.Vsync);
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
