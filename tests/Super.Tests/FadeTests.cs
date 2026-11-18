using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>Draws fade out with distance, and a draw about to swap to a finer version dithers into it.</summary>
public sealed class FadeTests
{
    private static readonly Vector3 Eye = new(0f, 0f, 10f);

    private static readonly Matrix4x4 Camera =
        Matrix4x4.CreateLookAt(Eye, Vector3.Zero, Vector3.UnitY)
        * Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f);

    // 500 pixels per unit one unit away makes 50 per unit at the origin, 10 away. With no radius, a level's error
    // in pixels is its error times 50, against the 1 pixel the view allows.
    private static DetailView View() => new(Camera, Eye, 500f, 0f, 1f);

    private static List<PartDetail> OneLevel(float error) => [default, new PartDetail(Vector3.Zero, 0f, [new DetailLevel(300, 100, error)])];

    private static DrawItem AtOrigin(DistanceFade? fade = null) => new()
    {
        IndexCount = 300,
        Transform = Matrix4x4.Identity,
        Detail = new DetailHandle(1),
        Fade = fade,
    };

    private static DrawItem[] Select(DrawItem draw, List<PartDetail> details, FadeRules rules)
    {
        var selected = new DrawItem[2];
        int count = DrawSelection.Select(View(), [draw], details, selected, fades: rules);
        return selected[..count];
    }

    [Theory]
    [InlineData(4f, 1f)]
    [InlineData(5f, 1f)]
    [InlineData(7.5f, 0.75f)]
    [InlineData(14f, 0.1f)]
    [InlineData(15f, 0f)]
    [InlineData(20f, 0f)]
    public void A_fade_keeps_all_before_its_start_none_after_its_end_and_a_straight_share_between(float distance, float kept)
    {
        Assert.Equal(kept, new DistanceFade(5f, 15f).Keep(distance), 1e-5f);
    }

    [Theory]
    [InlineData(-1f, 10f, "fade distances -1 to 10 must be 0 or more.")]
    [InlineData(0f, float.PositiveInfinity, "fade distances 0 to Infinity must be 0 or more.")]
    [InlineData(float.NaN, 10f, "fade distances NaN to 10 must be 0 or more.")]
    [InlineData(20.5f, 10f, "fade distance end 10 must not be before its start 20.5.")]
    public void A_fade_that_is_negative_endless_or_backwards_has_a_problem_that_says_so(float start, float end, string problem)
    {
        Assert.Equal(problem, DistanceFade.Problem(new DistanceFade(start, end)));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(50f, 60f)]
    public void A_fade_forwards_from_0_has_no_problem(float start, float end)
    {
        Assert.Null(DistanceFade.Problem(new DistanceFade(start, end)));
    }

    [Theory]
    [InlineData(-0.5f, "soft edge -0.5 must be 0 or more.")]
    [InlineData(float.NaN, "soft edge NaN must be 0 or more.")]
    public void A_negative_soft_edge_has_a_problem_that_says_so(float softEdge, string problem)
    {
        Assert.Equal(problem, MaterialProblems.SoftEdge(softEdge));
        Assert.Null(MaterialProblems.SoftEdge(0.25f));
    }

    [Fact]
    public void The_problems_read_the_same_under_a_swedish_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

            Assert.Equal("fade distance end 1.5 must not be before its start 2.5.", DistanceFade.Problem(new DistanceFade(2.5f, 1.5f)));
            Assert.Equal("soft edge -0.5 must be 0 or more.", MaterialProblems.SoftEdge(-0.5f));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_draw_halfway_through_its_fade_keeps_half_its_pixels()
    {
        // 10 m from the eye, halfway from 5 to 15.
        var drawn = Assert.Single(Select(AtOrigin(new DistanceFade(5f, 15f)), OneLevel(0.01f), new FadeRules(Eye, smooth: true)));

        Assert.Equal(0.5f, drawn.Dither, 1e-5f);
    }

    [Fact]
    public void A_draw_before_its_fade_starts_is_drawn_whole()
    {
        var drawn = Assert.Single(Select(AtOrigin(new DistanceFade(20f, 30f)), OneLevel(0.01f), new FadeRules(Eye, smooth: true)));

        Assert.Equal(0f, drawn.Dither);
    }

    [Fact]
    public void A_draw_past_its_fade_is_left_out()
    {
        Assert.Empty(Select(AtOrigin(new DistanceFade(2f, 8f)), OneLevel(0.01f), new FadeRules(Eye, smooth: true)));
    }

    [Fact]
    public void A_material_fade_counts_when_the_draw_has_none_and_the_draw_fade_wins_when_it_has_one()
    {
        var rules = new FadeRules(Eye, smooth: true, _ => new DistanceFade(5f, 15f));

        Assert.Equal(0.5f, Assert.Single(Select(AtOrigin(), OneLevel(0.01f), rules)).Dither, 1e-5f);
        // From 0 to 100, 10 m keeps 90 of 100.
        Assert.Equal(0.9f, Assert.Single(Select(AtOrigin(new DistanceFade(0f, 100f)), OneLevel(0.01f), rules)).Dither, 1e-5f);
    }

    [Fact]
    public void A_draw_without_bounds_fades_by_where_it_stands()
    {
        var draw = AtOrigin(new DistanceFade(5f, 15f)) with { Detail = default, Transform = Matrix4x4.CreateTranslation(0f, 0f, 2.5f) };

        // 7.5 m from the eye keeps 75 of the 10 m fade.
        Assert.Equal(0.75f, Assert.Single(Select(draw, OneLevel(0.01f), new FadeRules(Eye, smooth: true))).Dither, 1e-5f);
    }

    [Fact]
    public void With_fades_off_a_fade_cuts_at_its_middle()
    {
        var rules = new FadeRules(Eye, smooth: false);

        Assert.Empty(Select(AtOrigin(new DistanceFade(5f, 15f)), OneLevel(0.01f), rules));
        Assert.Equal(0f, Assert.Single(Select(AtOrigin(new DistanceFade(5f, 16f)), OneLevel(0.01f), rules)).Dither);
    }

    [Fact]
    public void A_draw_near_swapping_to_its_finer_version_draws_both_with_noise_that_fits_together()
    {
        // 0.018 × 50 is 0.9 of the error allowed: 0.1 from the swap, in a cross-fade 0.25 wide, leaves the coarse
        // version 0.4 of the pixels. The full mesh keeps the noise from 0.4 up, the other 0.6.
        var drawn = Select(AtOrigin(), OneLevel(0.018f), new FadeRules(Eye, smooth: true));

        Assert.Equal(2, drawn.Length);
        Assert.Equal((0u, 300u), (drawn[0].FirstIndex, drawn[0].IndexCount));
        Assert.Equal(-0.6f, drawn[0].Dither, 1e-4f);
        Assert.Equal((300u, 100u), (drawn[1].FirstIndex, drawn[1].IndexCount));
        Assert.Equal(0.4f, drawn[1].Dither, 1e-4f);
    }

    [Fact]
    public void A_cross_fade_inside_a_distance_fade_scales_both_versions()
    {
        var drawn = Select(AtOrigin(new DistanceFade(5f, 15f)), OneLevel(0.018f), new FadeRules(Eye, smooth: true));

        Assert.Equal(2, drawn.Length);
        Assert.Equal(-0.3f, drawn[0].Dither, 1e-4f);
        Assert.Equal(0.2f, drawn[1].Dither, 1e-4f);
    }

    [Fact]
    public void A_draw_far_from_swapping_draws_one_version_whole()
    {
        // 0.01 × 50 is half the error allowed, outside the cross-fade.
        var drawn = Assert.Single(Select(AtOrigin(), OneLevel(0.01f), new FadeRules(Eye, smooth: true)));

        Assert.Equal((300u, 100u, 0f), (drawn.FirstIndex, drawn.IndexCount, drawn.Dither));
    }

    [Fact]
    public void With_fades_off_a_draw_near_swapping_draws_only_its_version()
    {
        var drawn = Assert.Single(Select(AtOrigin(), OneLevel(0.018f), new FadeRules(Eye, smooth: false)));

        Assert.Equal((300u, 100u, 0f), (drawn.FirstIndex, drawn.IndexCount, drawn.Dither));
    }

    [Fact]
    public void Both_versions_of_a_cross_fade_name_the_draw_they_came_from()
    {
        DrawItem[] draws = [AtOrigin() with { Transform = Matrix4x4.CreateTranslation(500f, 0f, 0f) }, AtOrigin()];
        var selected = new DrawItem[4];
        var sources = new int[4];

        int count = DrawSelection.Select(View(), draws, OneLevel(0.018f), selected, sources: sources, fades: new FadeRules(Eye, smooth: true));

        Assert.Equal(2, count);
        Assert.Equal([1, 1], sources[..count]);
    }

    [Fact]
    public void A_model_drawn_with_a_fade_carries_it()
    {
        var view = new View();
        view.Begin(0f);

        view.Model(1, ModelRef.Cube, Matrix4x4.Identity, new ModelOptions { Fade = new DistanceFade(50f, 60f) });

        Assert.Equal(new DistanceFade(50f, 60f), Assert.Single(view.Drawn.ToArray()).Fade);
    }

    [Fact]
    public void A_model_drawn_with_a_backwards_fade_throws_and_says_why()
    {
        var view = new View();
        view.Begin(0f);

        var exception = Assert.Throws<ArgumentException>(() => view.Model(7, ModelRef.Cube, Matrix4x4.Identity, new ModelOptions { Fade = new DistanceFade(60f, 50f) }));

        Assert.StartsWith("Model 7's fade distance end 50 must not be before its start 60.", exception.Message);
    }
}
