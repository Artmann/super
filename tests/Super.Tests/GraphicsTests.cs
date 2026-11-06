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
    }

    [Theory]
    [InlineData(nameof(Graphics.SunShadows), RenderEffects.SunShadows)]
    [InlineData(nameof(Graphics.LightShadows), RenderEffects.LightShadows)]
    [InlineData(nameof(Graphics.AmbientOcclusion), RenderEffects.AmbientOcclusion)]
    [InlineData(nameof(Graphics.Reflections), RenderEffects.Reflections)]
    [InlineData(nameof(Graphics.AntiAliasing), RenderEffects.AntiAliasing)]
    [InlineData(nameof(Graphics.Bloom), RenderEffects.Bloom)]
    public void Each_switch_turned_off_leaves_out_only_its_own_effect(string setting, RenderEffects effect)
    {
        var graphics = new Graphics();
        typeof(Graphics).GetProperty(setting)!.SetValue(graphics, false);

        Assert.Equal(effect, graphics.Off);
    }

    [Fact]
    public void Fastest_leaves_out_every_effect_and_turns_vsync_off()
    {
        var graphics = Graphics.Fastest();

        Assert.Equal(RenderEffects.All, graphics.Off);
        Assert.False(graphics.Vsync);
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
