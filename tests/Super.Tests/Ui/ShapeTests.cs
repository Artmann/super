using System.Numerics;
using Rendition;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Gradients, shadows, corners of their own, shapes and icons, read from the quads they draw.</summary>
public sealed class ShapeTests
{
    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color Blue = new(0f, 0f, 1f);

    private static OverlayQuad[] Show(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return tree.Quads.ToArray();
    }

    private static UiView Block(float width, float height) => new Spacer().Frame(width, height);

    [Fact]
    public void A_colour_background_is_a_box_with_no_gradient()
    {
        var quad = Assert.Single(Show(Block(100f, 100f).Background(Red)));

        Assert.Equal(OverlayKind.Box, quad.Kind);
        Assert.Equal(OverlayGradient.None, quad.Gradient);
        Assert.Equal(quad.Color, quad.Color2);
    }

    [Fact]
    public void A_gradient_at_90_degrees_runs_from_the_left_edge_to_the_right_one()
    {
        var quad = Assert.Single(Show(Block(200f, 100f).Background(Paint.Linear(Red, Blue, 90f))));

        Assert.Equal(OverlayGradient.Linear, quad.Gradient);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), quad.Color);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), quad.Color2);
        AssertNear(new Vector4(0f, 50f, 200f, 50f), quad.GradientPoints);
    }

    [Fact]
    public void A_gradient_at_the_default_angle_runs_from_the_top_down()
    {
        var quad = Assert.Single(Show(Block(200f, 100f).Background(Paint.Linear(Red, Blue))));

        AssertNear(new Vector4(100f, 0f, 100f, 100f), quad.GradientPoints);
    }

    [Fact]
    public void A_gradient_at_135_degrees_reaches_from_corner_to_corner_of_a_square()
    {
        var quad = Assert.Single(Show(Block(100f, 100f).Background(Paint.Linear(Red, Blue, 135f))));

        AssertNear(new Vector4(0f, 0f, 100f, 100f), quad.GradientPoints);
    }

    [Fact]
    public void A_gradient_between_two_points_lands_on_them()
    {
        var paint = Paint.Linear(Red, Blue, Alignment.Top, Alignment.BottomTrailing);
        var quad = Assert.Single(Show(Block(200f, 100f).Background(paint)));

        AssertNear(new Vector4(100f, 0f, 200f, 100f), quad.GradientPoints);
    }

    [Fact]
    public void A_radial_gradient_reaches_the_farthest_corner_unless_told_how_far()
    {
        var reaching = Assert.Single(Show(Block(80f, 60f).Background(Paint.Radial(Red, Blue))));
        var cornered = Assert.Single(Show(Block(80f, 60f).Background(Paint.Radial(Red, Blue, Alignment.TopLeading))));
        var short_ = Assert.Single(Show(Block(80f, 60f).Background(Paint.Radial(Red, Blue, radius: 12f))));

        Assert.Equal(OverlayGradient.Radial, reaching.Gradient);
        AssertNear(new Vector4(40f, 30f, 50f, 0f), reaching.GradientPoints);
        AssertNear(new Vector4(0f, 0f, 100f, 0f), cornered.GradientPoints);
        AssertNear(new Vector4(40f, 30f, 12f, 0f), short_.GradientPoints);
    }

    [Fact]
    public void Each_corner_keeps_its_own_radius()
    {
        var quad = Assert.Single(Show(Block(100f, 100f).Background(Red).CornerRadius(new CornerRadii(8f, 4f, 0f, 2f))));

        Assert.Equal(new Vector4(8f, 4f, 0f, 2f), quad.Radii);
    }

    [Fact]
    public void A_shadow_is_drawn_first_and_reaches_past_its_blur()
    {
        var shadow = new Shadow(Blue, Blur: 8f, Y: 4f, Spread: 2f);
        var quads = Show(Block(100f, 100f).Background(Red).CornerRadius(6f).Shadow(shadow));

        Assert.Equal(2, quads.Length);
        var cast = quads[0];
        Assert.Equal(OverlayKind.Shadow, cast.Kind);
        Assert.Equal(OverlayKind.Box, quads[1].Kind);

        // The box is at (350, 250). The shadow falls 4 down and grows 2, then blurs 1.5 × 8 + 1 further.
        Assert.Equal(new Vector2(335f, 239f), cast.Position);
        Assert.Equal(new Vector2(130f, 130f), cast.Size);
        Assert.Equal(new Vector4(348f, 252f, 104f, 104f), cast.Params);
        Assert.Equal(new Vector4(350f, 250f, 100f, 100f), cast.GradientPoints);
        Assert.Equal(new Vector4(8f), cast.Radii);
        Assert.Equal(8f, cast.Thickness);
    }

    [Fact]
    public void A_theme_shadow_follows_the_theme()
    {
        var view = new VStack { Block(50f, 50f).Background(Red).Shadow(ShadowSize.Large) }.Theme(Theme.Light);

        var cast = Show(view).Single(quad => quad.Kind == OverlayKind.Shadow);

        Assert.Equal(Theme.Light.Shadows.Large.Blur, cast.Thickness);
        Assert.Equal(Theme.Light.Shadows.Large.Color.A, cast.Color.W, 4);
    }

    [Fact]
    public void A_clear_shadow_draws_nothing()
    {
        Assert.Single(Show(Block(50f, 50f).Background(Red).Shadow(new Shadow(Blue with { A = 0f }, 8f))));
    }

    [Fact]
    public void A_shape_fills_what_it_is_offered()
    {
        var quad = Assert.Single(Show(new Rectangle().Fill(Red).Frame(120f, 40f)));

        Assert.Equal(new Vector2(120f, 40f), quad.Size);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), quad.Color);
    }

    [Fact]
    public void A_shape_stretches_to_share_a_stack()
    {
        var quads = Show(new HStack(Spacing: 0f)
        {
            new Rectangle().Fill(Red),
            Block(200f, 50f),
        }.Frame(600f, 50f));

        Assert.Equal(new Vector2(400f, 50f), Assert.Single(quads).Size);
    }

    [Fact]
    public void A_shape_asks_for_ten_by_ten_when_nothing_is_offered()
    {
        var quad = Assert.Single(Show(new Rectangle().Fill(Red).FixedSize()));

        Assert.Equal(new Vector2(10f, 10f), quad.Size);
    }

    [Fact]
    public void A_shape_takes_the_text_colour_unless_filled()
    {
        var quad = Assert.Single(Show(new Rectangle().Frame(10f, 10f).Foreground(Blue)));

        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), quad.Color);
    }

    [Fact]
    public void A_stroked_shape_is_only_its_outline()
    {
        var quad = Assert.Single(Show(new RoundedRectangle(5f).Stroke(Blue, 2f).Frame(40f, 40f)));

        Assert.Equal(0f, quad.Color.W);
        Assert.Equal(2f, quad.BorderWidth);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), quad.BorderColor);
        Assert.Equal(new Vector4(5f), quad.Radii);
    }

    [Fact]
    public void A_capsule_rounds_its_short_ends_fully()
    {
        var quad = Assert.Single(Show(new Capsule().Fill(Red).Frame(80f, 24f)));

        Assert.Equal(new Vector4(12f), quad.Radii);
    }

    [Fact]
    public void A_circle_is_the_square_in_the_middle_of_its_frame()
    {
        var quad = Assert.Single(Show(new Circle().Fill(Red).Frame(100f, 40f)));

        Assert.Equal(OverlayKind.Ellipse, quad.Kind);
        Assert.Equal(new Vector2(40f, 40f), quad.Size);
        Assert.Equal(new Vector2(380f, 280f), quad.Position);
    }

    [Fact]
    public void An_ellipse_fills_its_frame()
    {
        var quad = Assert.Single(Show(new Ellipse().Fill(Red).Frame(100f, 40f)));

        Assert.Equal(OverlayKind.Ellipse, quad.Kind);
        Assert.Equal(new Vector2(100f, 40f), quad.Size);
    }

    [Fact]
    public void An_arc_turns_fractions_of_a_turn_into_angles_and_stops_at_a_full_turn()
    {
        var quarter = Assert.Single(Show(new Arc(0.25f, 0.5f, Thickness: 6f).Fill(Red).Frame(50f, 50f)));
        var over = Assert.Single(Show(new Arc(0f, 3f).Fill(Red).Frame(50f, 50f)));

        Assert.Equal(OverlayKind.Arc, quarter.Kind);
        Assert.Equal(MathF.PI / 2f, quarter.Params.X, 4);
        Assert.Equal(MathF.PI, quarter.Params.Y, 4);
        Assert.Equal(6f, quarter.Thickness);
        Assert.Equal(MathF.Tau, over.Params.Y, 4);
    }

    [Fact]
    public void An_arc_with_only_a_stroke_takes_the_strokes_colour()
    {
        var quad = Assert.Single(Show(new Arc(0f, 0.5f).Stroke(Blue).Frame(50f, 50f)));

        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), quad.Color);
    }

    [Fact]
    public void An_icon_is_sized_and_coloured_like_the_text_around_it()
    {
        var quads = Show(new Icon(IconName.Close).FontSize(20f).Foreground(Red));

        // Two crossed lines, in a box 1.15 × the text size.
        Assert.Equal(2, quads.Length);
        Assert.All(quads, quad => Assert.Equal(OverlayKind.Segment, quad.Kind));
        Assert.All(quads, quad => Assert.Equal(new Vector4(1f, 0f, 0f, 1f), quad.Color));

        var left = quads.Min(quad => quad.Position.X);
        var right = quads.Max(quad => quad.Position.X + quad.Size.X);

        // The lines run from 6 to 18 on the 24 grid of a 23 wide box, so they span 11.5 plus their ends.
        float width = 2f * 23f / 24f;
        Assert.Equal(23f / 2f + width + 2f, right - left, 3);
    }

    [Fact]
    public void Every_icon_draws_a_picture_of_its_own()
    {
        var drawn = Enum.GetValues<IconName>()
            .Select(name => string.Join(";", Show(new Icon(name)).Select(quad => $"{quad.Kind} {quad.Position} {quad.Size} {quad.Params}")))
            .ToArray();

        Assert.All(drawn, Assert.NotEmpty);
        Assert.Equal(drawn.Length, drawn.Distinct().Count());
    }

    [Fact]
    public void An_icon_name_that_isnt_one_draws_a_dot()
    {
        var quad = Assert.Single(Show(new Icon((IconName)99)));

        Assert.Equal(OverlayKind.Ellipse, quad.Kind);
    }

    private static void AssertNear(Vector4 expected, Vector4 actual)
    {
        Assert.True(Vector4.Distance(expected, actual) < 0.01f, $"Expected {expected}, got {actual}.");
    }
}
