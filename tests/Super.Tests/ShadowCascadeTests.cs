using System.Numerics;
using Rendition.Passes;

namespace Super.Tests;

public class ShadowCascadeTests
{
    [Fact]
    public void Spread_of_a_quarter_turn_view_twice_as_wide_is_root_five()
    {
        // Up and down are 45° each, and twice as wide is 2 across per metre, so a corner is root 5 out.
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 2f, 0.1f, 100f);

        Assert.Equal(MathF.Sqrt(5f), ShadowPass.Spread(projection)!.Value, 4);
    }

    [Fact]
    public void Spread_of_a_parallel_view_is_null()
    {
        var projection = Matrix4x4.CreateOrthographic(20f, 10f, 0.1f, 100f);

        Assert.Null(ShadowPass.Spread(projection));
    }

    [Fact]
    public void First_slice_with_a_spread_of_one_ends_at_the_radius()
    {
        // Slice [0, f] with corners f out at depth f: the ball centred at f touches the eye
        // and the far corners, both f away, so f equals the radius.
        var (center, end) = ShadowPass.FitSlice(0f, 10f, 1f);

        Assert.Equal(10f, center, 3);
        Assert.Equal(10f, end, 3);
    }

    [Fact]
    public void First_slice_with_a_spread_of_a_half_reaches_one_and_six_tenths_radii()
    {
        // Centre 0.625f: the eye is 0.625f away and a far corner sqrt(0.375² + 0.5²) f = 0.625f,
        // so the slice ends at 10 / 0.625 = 16 with its centre at 10.
        var (center, end) = ShadowPass.FitSlice(0f, 10f, 0.5f);

        Assert.Equal(10f, center, 3);
        Assert.Equal(16f, end, 3);
    }

    [Fact]
    public void Slice_too_wide_for_the_ball_ends_where_it_starts()
    {
        // At 30 m the view is already 15 m from its axis, wider than a 10 m ball.
        var (center, end) = ShadowPass.FitSlice(30f, 10f, 0.5f);

        Assert.Equal(30f, center);
        Assert.Equal(30f, end);
    }

    [Theory]
    [InlineData(0f, 5f, 0.4f)]
    [InlineData(5f, 15f, 0.8f)]
    [InlineData(12f, 45f, 1.2f)]
    [InlineData(0.5f, 3f, 2f)]
    public void Ball_holds_every_corner_of_its_slice(float start, float radius, float spread)
    {
        var (center, end) = ShadowPass.FitSlice(start, radius, spread);

        Assert.True(end > start, $"the slice from {start} ended at {end}");
        foreach (float depth in new[] { start, end })
        {
            // A corner at this depth is depth * spread from the axis.
            float distance = MathF.Sqrt((depth - center) * (depth - center) + depth * spread * depth * spread);
            Assert.True(distance <= radius * 1.0001f, $"corner at {depth} is {distance} from the centre, past {radius}");
        }
    }

    [Fact]
    public void Cascade_depth_runs_from_its_reach_towards_the_sun_to_its_radius_away()
    {
        var sun = Vector3.UnitY;
        var center = new Vector3(0f, 0f, 0f);
        var matrix = ShadowPass.CreateMatrix(sun, center, radius: 10f, reach: 30f);

        // Reversed-Z: the point 30 m sunward is the near plane at 1, the one 10 m away the far plane at 0.
        Assert.Equal(1f, Depth(matrix, new Vector3(0f, 30f, 0f)), 4);
        Assert.Equal(0f, Depth(matrix, new Vector3(0f, -10f, 0f)), 4);
        Assert.Equal(0.25f, Depth(matrix, center), 4);
    }

    [Fact]
    public void Cascade_reaching_less_than_its_radius_reaches_its_radius()
    {
        var matrix = ShadowPass.CreateMatrix(Vector3.UnitY, Vector3.Zero, radius: 10f, reach: 2f);

        Assert.Equal(1f, Depth(matrix, new Vector3(0f, 10f, 0f)), 4);
        Assert.Equal(0.5f, Depth(matrix, Vector3.Zero), 4);
    }

    [Theory]
    [InlineData(10f, 10f, 1f)]
    [InlineData(10f, 30f, 0.5f)]
    [InlineData(10f, 4f, 1f)]
    public void Depth_scale_is_map_width_over_depth_span(float radius, float reach, float expected)
    {
        Assert.Equal(expected, ShadowPass.DepthScale(radius, reach), 5);
    }

    [Fact]
    public void Marked_sun_casts_the_shadows()
    {
        var view = new View();
        view.Begin(0f);
        view.Sun(Vector3.UnitY, Vector3.One);
        view.Sun(Vector3.UnitX, Vector3.One, shadows: true);

        Assert.Equal(1, SceneRenderer.ShadowSun(view.Suns));
    }

    [Fact]
    public void First_sun_casts_the_shadows_when_none_is_marked()
    {
        var view = new View();
        view.Begin(0f);
        view.Sun(Vector3.UnitY, Vector3.One);
        view.Sun(Vector3.UnitX, Vector3.One);

        Assert.Equal(0, SceneRenderer.ShadowSun(view.Suns));
    }

    [Fact]
    public void Marking_a_second_sun_explains_that_only_one_casts()
    {
        var view = new View();
        view.Begin(0f);
        view.Sun(Vector3.UnitY, Vector3.One, shadows: true);

        var error = Assert.Throws<InvalidOperationException>(() => view.Sun(Vector3.UnitX, Vector3.One, shadows: true));

        Assert.Equal("Only one sun can cast shadows, and another already does this frame. Pass shadows: true to one sun only.", error.Message);
    }

    private static float Depth(Matrix4x4 matrix, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), matrix);
        return clip.Z / clip.W;
    }
}
