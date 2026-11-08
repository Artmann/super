using System.Numerics;
using Rendition;
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
        var matrix = ShadowPass.CreateMatrix(sun, center, radius: 10f, reach: 30f, resolution: 2048);

        // Reversed-Z: the point 30 m sunward is the near plane at 1, the one 10 m away the far plane at 0.
        Assert.Equal(1f, Depth(matrix, new Vector3(0f, 30f, 0f)), 4);
        Assert.Equal(0f, Depth(matrix, new Vector3(0f, -10f, 0f)), 4);
        Assert.Equal(0.25f, Depth(matrix, center), 4);
    }

    [Fact]
    public void Cascade_reaching_less_than_its_radius_reaches_its_radius()
    {
        var matrix = ShadowPass.CreateMatrix(Vector3.UnitY, Vector3.Zero, radius: 10f, reach: 2f, resolution: 2048);

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

    // A cascade round the origin with a radius of 2 and the sun straight up. Its map sees a
    // square column up to the sun whose corners are 2√2 ≈ 2.83 out.
    private static float Reach(Vector4 caster, float reach = 2f) =>
        ShadowPass.CasterReach(Vector3.Zero, 2f, Vector3.UnitY, reach, [caster]);

    [Fact]
    public void Caster_up_sun_stretches_the_reach_to_its_far_side()
    {
        // A ball of radius 1, 7 up: its far side is 8 up.
        Assert.Equal(8f, Reach(new Vector4(0f, 7f, 0f, 1f)), 4);
    }

    [Fact]
    public void Caster_overlapping_a_corner_of_the_column_counts()
    {
        // 3.5 aside is past the radius of 2 but within 2.83 + 1 of the column's corner.
        Assert.Equal(8f, Reach(new Vector4(3.5f, 7f, 0f, 1f)), 4);
    }

    [Fact]
    public void Caster_beside_the_column_is_left_out()
    {
        // 5 aside is further than 2.83 + 1, so it can't shade anything in the cascade.
        Assert.Equal(2f, Reach(new Vector4(5f, 7f, 0f, 1f)), 4);
    }

    [Fact]
    public void Caster_far_up_sun_stretches_the_reach_only_to_six_radii()
    {
        Assert.Equal(12f, Reach(new Vector4(0f, 30f, 0f, 1f)), 4);
    }

    [Fact]
    public void Caster_nearer_than_the_reach_leaves_it_as_it_was()
    {
        // Down-sun, and up-sun but inside a reach of 5 already.
        Assert.Equal(2f, Reach(new Vector4(0f, -5f, 0f, 1f)), 4);
        Assert.Equal(5f, Reach(new Vector4(0f, 1f, 0f, 1f), reach: 5f), 4);
    }

    [Fact]
    public void Sun_size_lasts_until_the_next_frame_begins()
    {
        var view = new View();
        view.Begin(0f);
        view.Shadows(7f, 3, sunSize: 0.02f);

        Assert.Equal(0.02f, view.SunSize);

        view.Begin(0f);

        Assert.Equal(0.12f, view.SunSize);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(0.51f)]
    [InlineData(float.NaN)]
    public void Sun_size_out_of_range_explains_the_range(float sunSize)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Shadows(7f, 3, sunSize));

        Assert.StartsWith("The sun's size must be from 0 to 0.5 radians.", error.Message);
        Assert.Equal("sunSize", error.ParamName);
    }

    [Fact]
    public void Contact_length_reaches_the_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.Shadows(7f, 3, contact: 0.4f);

        Assert.Equal(0.4f, SceneRenderer.FrameShadows(view, 16f / 9f).ContactLength);
    }

    [Fact]
    public void Contact_length_is_a_quarter_metre_until_a_frame_sets_it()
    {
        var view = new View();
        view.Begin(0f);
        view.Shadows(7f, 3, contact: 0f);

        Assert.Equal(0f, SceneRenderer.FrameShadows(view, 1f).ContactLength);

        view.Begin(0f);

        Assert.Equal(0.25f, SceneRenderer.FrameShadows(view, 1f).ContactLength);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(float.NaN)]
    public void A_contact_length_below_zero_is_refused(float contact)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Shadows(7f, 3, contact: contact));

        Assert.StartsWith("The contact shadows' length must be 0 or more metres, and 0 turns them off.", error.Message);
        Assert.Equal("contact", error.ParamName);
    }

    [Fact]
    public void Renderer_draws_contact_shadows_the_settings_length()
    {
        var shadows = new ShadowSettings { ContactLength = 0.4f };

        Assert.Equal(0.4f, ShadowPass.ContactLength(shadows, RenderEffects.Bloom, capturingProbe: false));
    }

    [Fact]
    public void Contact_shadows_left_out_reach_the_renderer_as_no_length()
    {
        var graphics = new Graphics { ContactShadows = false };
        var shadows = new ShadowSettings { ContactLength = 0.4f };

        Assert.Equal(0f, ShadowPass.ContactLength(shadows, graphics.Off, capturingProbe: false));
    }

    [Fact]
    public void Reflection_probes_are_captured_without_contact_shadows()
    {
        var shadows = new ShadowSettings { ContactLength = 0.4f };

        Assert.Equal(0f, ShadowPass.ContactLength(shadows, RenderEffects.None, capturingProbe: true));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Cascade_maps_can_be_1024_2048_or_4096_texels_across(int resolution)
    {
        Assert.Equal(resolution, ShadowPass.CheckResolution(resolution));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    [InlineData(8192)]
    public void Renderer_refuses_other_cascade_map_sizes(int resolution)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ShadowPass.CheckResolution(resolution));

        Assert.StartsWith("Shadow maps can be 1024, 2048 or 4096 texels across.", error.Message);
        Assert.Equal("resolution", error.ParamName);
    }

    [Fact]
    public void Finer_maps_snap_cascades_to_finer_steps()
    {
        // A 20 m wide map: a 1024 map's texel is 20 / 1024 = 1.95 cm, a 4096 map's 0.488 cm.
        // A centre 0.6 cm aside snaps back to 0 on the coarse map, so the origin is in the
        // middle, and one texel aside on the fine map, so the origin sits 0.488 cm / 10 m off it.
        var center = new Vector3(0.006f, 0f, 0f);
        var coarse = ShadowPass.CreateMatrix(Vector3.UnitY, center, radius: 10f, reach: 10f, resolution: 1024);
        var fine = ShadowPass.CreateMatrix(Vector3.UnitY, center, radius: 10f, reach: 10f, resolution: 4096);
        var origin = new Vector4(0f, 0f, 0f, 1f);

        Assert.Equal(0f, Vector4.Transform(origin, coarse).X, 6);
        Assert.Equal(0.00048828f, MathF.Abs(Vector4.Transform(origin, fine).X), 6);
    }

    private static float Depth(Matrix4x4 matrix, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), matrix);
        return clip.Z / clip.W;
    }
}
