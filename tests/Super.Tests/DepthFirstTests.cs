using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

// Which draws have their depth drawn ahead of shading: the ones whose triangles are large on screen.
public sealed class DepthFirstTests
{
    private static readonly Matrix4x4 Camera =
        Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY)
        * Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f);

    private static List<PartDetail> Details(float radius) => [default, new PartDetail(Vector3.Zero, radius, [])];

    private static DrawItem At(Vector3 position) => new()
    {
        IndexCount = 300,
        Transform = Matrix4x4.CreateTranslation(position),
        Detail = new DetailHandle(1),
    };

    // 500 pixels a unit at one unit from the eye, which stands at z = 10.
    private static DetailView Perspective() => new(Camera, new Vector3(0f, 0f, 10f), 500f, 0f, 1f);

    [Fact]
    public void A_ball_covers_the_disc_it_shows_as_in_a_perspective_view()
    {
        // Ten units away a unit shows as 50 pixels, so a ball of radius 1 is a disc of radius 50: pi * 2,500 pixels.
        float covered = DrawSelection.PixelsCovered(Perspective(), At(Vector3.Zero), Details(1f));

        Assert.Equal(7853.98f, covered, 0.01f);
    }

    [Fact]
    public void A_ball_twice_as_far_away_covers_a_quarter_of_the_pixels()
    {
        // Twenty units away a unit shows as 25 pixels: pi * 625 pixels.
        float covered = DrawSelection.PixelsCovered(Perspective(), At(new Vector3(0f, 0f, -10f)), Details(1f));

        Assert.Equal(1963.50f, covered, 0.01f);
    }

    [Fact]
    public void A_scaled_ball_covers_the_disc_of_its_scaled_radius()
    {
        var draw = At(Vector3.Zero) with { Transform = Matrix4x4.CreateScale(2f) };

        // Radius 2 at 50 pixels a unit is a disc of radius 100: pi * 10,000 pixels.
        Assert.Equal(31415.93f, DrawSelection.PixelsCovered(Perspective(), draw, Details(1f)), 0.01f);
    }

    [Fact]
    public void A_ball_covers_the_same_disc_at_any_distance_in_a_parallel_view()
    {
        // 36 pixels a unit wherever it stands, so a ball of radius 0.5 is a disc of radius 18: pi * 324 pixels.
        var view = new DetailView(Camera, new Vector3(0f, 0f, 10f), 0f, 36f, 1f);

        Assert.Equal(1017.88f, DrawSelection.PixelsCovered(view, At(Vector3.Zero), Details(0.5f)), 0.01f);
        Assert.Equal(1017.88f, DrawSelection.PixelsCovered(view, At(new Vector3(0f, 0f, -80f)), Details(0.5f)), 0.01f);
    }

    [Fact]
    public void A_ball_the_eye_is_inside_may_cover_any_number_of_pixels()
    {
        // The eye is 2 units from the centre of a ball of radius 3.
        float covered = DrawSelection.PixelsCovered(Perspective(), At(new Vector3(0f, 0f, 8f)), Details(3f));

        Assert.Equal(float.PositiveInfinity, covered);
    }

    [Fact]
    public void A_draw_with_no_bounds_may_cover_any_number_of_pixels()
    {
        var draw = At(Vector3.Zero) with { Detail = default };

        Assert.Equal(float.PositiveInfinity, DrawSelection.PixelsCovered(Perspective(), draw, Details(1f)));
    }

    [Theory]
    // 100 triangles pay from 8 pixels each: 800 pixels.
    [InlineData(800f, 300u, true)]
    [InlineData(799f, 300u, false)]
    [InlineData(100000f, 300u, true)]
    // Two triangles pay from 16 pixels.
    [InlineData(16f, 6u, true)]
    [InlineData(15f, 6u, false)]
    public void Depth_is_drawn_first_from_eight_pixels_a_triangle(float covered, uint indexCount, bool pays)
    {
        Assert.Equal(pays, DepthFirst.Pays(covered, viewPixels: 1_000_000f, indexCount));
    }

    [Fact]
    public void A_draw_covers_no_more_than_the_picture()
    {
        // 30,000 triangles need 240,000 pixels. A draw the eye is inside covers all of a picture, and no more.
        Assert.True(DepthFirst.Pays(float.PositiveInfinity, viewPixels: 240_000f, indexCount: 90_000));
        Assert.False(DepthFirst.Pays(float.PositiveInfinity, viewPixels: 239_999f, indexCount: 90_000));
    }
}
