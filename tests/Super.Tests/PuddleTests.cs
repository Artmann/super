using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>
/// Puddles: which surfaces are under the water, which are damp above it, and how the view's
/// water level reaches the frame block that surface.glsl reads.
/// </summary>
public class PuddleTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void Below_the_level_is_water()
    {
        var (water, damp) = Puddles.Wetness(surfaceHeight: -0.02f, level: 0f, band: 0.08f);

        Assert.Equal(1f, water, Tolerance);
        Assert.Equal(1f, damp, Tolerance);
    }

    [Fact]
    public void Half_a_band_up_is_half_damp()
    {
        // 4 cm above the water with an 8 cm band: half way from the waterline to dry.
        var (water, damp) = Puddles.Wetness(surfaceHeight: 0.04f, level: 0f, band: 0.08f);

        Assert.Equal(0f, water, Tolerance);
        Assert.Equal(0.5f, damp, Tolerance);
    }

    [Fact]
    public void Above_the_band_is_dry()
    {
        var (water, damp) = Puddles.Wetness(surfaceHeight: 0.1f, level: 0f, band: 0.08f);

        Assert.Equal(0f, water, Tolerance);
        Assert.Equal(0f, damp, Tolerance);
    }

    [Fact]
    public void A_groove_in_a_high_wall_never_floods()
    {
        // The bottom of a 3 cm groove in a wall 2 m up still lies 1.97 m above the water.
        float surface = Puddles.SurfaceHeight(worldY: 2f, height: 0f, heightDepth: 0.03f);
        var (water, damp) = Puddles.Wetness(surface, level: 0f, band: 0.08f);

        Assert.Equal(1.97f, surface, Tolerance);
        Assert.Equal(0f, water, Tolerance);
        Assert.Equal(0f, damp, Tolerance);
    }

    [Fact]
    public void A_groove_sits_below_its_face()
    {
        // Mud whose face is 1 cm above the water: a pixel at the top of the height map stays dry
        // of water, but one at the bottom of its 3 cm hollows lies 2 cm under it.
        Assert.Equal(0.01f, Puddles.SurfaceHeight(worldY: 0.01f, height: 1f, heightDepth: 0.03f), Tolerance);
        Assert.Equal(-0.02f, Puddles.SurfaceHeight(worldY: 0.01f, height: 0f, heightDepth: 0.03f), Tolerance);
        Assert.Equal(-0.005f, Puddles.SurfaceHeight(worldY: 0.01f, height: 0.5f, heightDepth: 0.03f), Tolerance);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(float.NaN)]
    public void A_negative_band_is_refused(float band)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Puddles(-0.045f, band));

        Assert.StartsWith("The damp band above the water must be 0 or more metres, and 0 leaves no damp band.", error.Message);
        Assert.Equal("band", error.ParamName);
    }

    [Fact]
    public void A_level_that_is_not_a_number_is_refused()
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Puddles(float.PositiveInfinity));

        Assert.StartsWith("The water level must be a height in metres.", error.Message);
        Assert.Equal("level", error.ParamName);
    }

    [Fact]
    public void No_band_leaves_only_the_water()
    {
        Assert.Equal((1f, 1f), Puddles.Wetness(surfaceHeight: -0.01f, level: 0f, band: 0f));
        Assert.Equal((0f, 0f), Puddles.Wetness(surfaceHeight: 0.001f, level: 0f, band: 0f));
    }

    [Fact]
    public void The_level_and_band_reach_the_view_and_a_new_frame_starts_dry()
    {
        var view = new View();
        view.Begin(0f);
        Assert.Null(view.PuddleSettings);

        view.Puddles(-0.045f);
        Assert.Equal(new PuddleSettings(-0.045f, 0.08f), view.PuddleSettings);

        view.Puddles(0.2f, 0.03f);
        Assert.Equal(new PuddleSettings(0.2f, 0.03f), view.PuddleSettings);

        view.Begin(0f);
        Assert.Null(view.PuddleSettings);
    }

    [Fact]
    public void Pack_marks_the_puddles_on_and_none_or_switched_off_is_all_zero()
    {
        var settings = new PuddleSettings(-0.045f, 0.08f);

        Assert.Equal(new Vector4(-0.045f, 0.08f, 1f, 0f), Puddles.Pack(-0.045f, 0.08f));
        Assert.Equal(new Vector4(-0.045f, 0.08f, 1f, 0f), Puddles.Pack(settings, RenderEffects.None));
        Assert.Equal(Vector4.Zero, Puddles.Pack(null, RenderEffects.None));
        Assert.Equal(Vector4.Zero, Puddles.Pack(settings, RenderEffects.Puddles));

        // Other effects off leave the puddles alone.
        Assert.Equal(new Vector4(-0.045f, 0.08f, 1f, 0f), Puddles.Pack(settings, RenderEffects.All & ~RenderEffects.Puddles));
    }
}
