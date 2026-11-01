using System.Numerics;

namespace Super.Tests;

public class GlfwInputTests
{
    [Fact]
    public void Known_keys_map_by_code()
    {
        Assert.True(GlfwInput.TryMapKey(32, out var space));
        Assert.Equal(Key.Space, space);
        Assert.True(GlfwInput.TryMapKey(87, out var w));
        Assert.Equal(Key.W, w);
    }

    [Fact]
    public void Unknown_keys_are_dropped()
    {
        Assert.False(GlfwInput.TryMapKey(-1, out _));
        Assert.False(GlfwInput.TryMapKey(9999, out _));
        Assert.False(GlfwInput.TryMapKey(33, out _));
    }

    [Fact]
    public void Cursor_positions_scale_to_the_framebuffer()
    {
        Assert.Equal(new Vector2(200f, 100f), GlfwInput.ToFramebuffer(100, 50, 1000, 500, 2000, 1000));
    }

    [Fact]
    public void A_zero_sized_window_passes_positions_through()
    {
        Assert.Equal(new Vector2(5f, 5f), GlfwInput.ToFramebuffer(5, 5, 0, 0, 10, 10));
    }
}
