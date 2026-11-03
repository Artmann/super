using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class SplineTests
{
    private const float Tolerance = 1e-4f;

    // The handle length that makes a Bézier quarter circle: 4/3 × (√2 − 1).
    private static readonly float Kappa = 4f / 3f * (MathF.Sqrt(2f) - 1f);

    public record Route(string Id, Spline Path, Vector3 Start);

    private static Spline Line() => Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f)]);

    /// <summary>A quarter of a circle of radius 1 on the ground, from +x round to +z.</summary>
    private static Spline QuarterCircle() => Spline.Bezier(
    [
        new SplineKnot(Vector3.UnitX, Out: new Vector3(0f, 0f, Kappa)),
        new SplineKnot(Vector3.UnitZ, In: new Vector3(Kappa, 0f, 0f)),
    ]);

    [Fact]
    public void A_spline_through_two_points_is_a_straight_line()
    {
        var line = Line();

        Assert.Equal(10f, line.Length, Tolerance);
        AssertNear(new Vector3(5f, 0f, 0f), line.Position(0.5f));
        AssertNear(new Vector3(2.5f, 0f, 0f), line.Position(0.25f));
        AssertNear(Vector3.UnitX, line.Tangent(0.3f));
        Assert.Equal(2.5f, line.DistanceAt(0.25f), Tolerance);
    }

    [Fact]
    public void Each_piece_takes_an_equal_share_of_t_but_distance_is_even()
    {
        // Two pieces, of 1 m and 9 m, along a line.
        var line = Spline.Through([Vector3.Zero, Vector3.UnitX, new Vector3(10f, 0f, 0f)]);

        AssertNear(Vector3.UnitX, line.Position(0.5f));
        Assert.Equal(1f, line.DistanceAt(0.5f), Tolerance);
        AssertNear(new Vector3(5f, 0f, 0f), line.PointAtDistance(5f), 1e-3f);
        AssertNear(new Vector3(9.5f, 0f, 0f), line.PointAtDistance(9.5f), 1e-3f);
    }

    [Fact]
    public void A_quarter_circle_passes_through_its_middle_and_is_a_quarter_of_two_pi_long()
    {
        var arc = QuarterCircle();

        // The Bézier's midpoint is (P0 + 3 P1 + 3 P2 + P3) / 8 = ((4 + 3κ) / 8, 0, (4 + 3κ) / 8) = (√½, 0, √½).
        float half = MathF.Sqrt(0.5f);
        AssertNear(new Vector3(half, 0f, half), arc.Position(0.5f));
        AssertNear(new Vector3(-half, 0f, half), arc.Tangent(0.5f));
        Assert.Equal(MathF.PI / 2f, arc.Length, 1e-3f);
    }

    [Fact]
    public void Points_at_even_distances_are_evenly_spaced_round_a_quarter_circle()
    {
        var arc = QuarterCircle();

        // On a circle of radius 1 the angle is the distance travelled.
        for (int i = 0; i <= 20; i++)
        {
            float distance = arc.Length * i / 20f;
            var point = arc.PointAtDistance(distance);
            float angle = MathF.Atan2(point.Z, point.X);
            Assert.Equal(distance, angle, 2e-3f);
            Assert.Equal(distance, arc.DistanceAt(arc.TAt(distance)), 1e-4f);
        }
    }

    [Fact]
    public void Nearest_finds_the_closest_point_on_a_line()
    {
        var hit = Line().Nearest(new Vector3(3f, 4f, 0f));

        AssertNear(new Vector3(3f, 0f, 0f), hit.Position);
        Assert.Equal(0.3f, hit.T, Tolerance);
        Assert.Equal(3f, hit.Distance, Tolerance);
        Assert.Equal(4f, hit.Gap, Tolerance);
    }

    [Fact]
    public void Nearest_stops_at_the_end_of_an_open_spline()
    {
        var hit = Line().Nearest(new Vector3(15f, 1f, 0f));

        AssertNear(new Vector3(10f, 0f, 0f), hit.Position);
        Assert.Equal(1f, hit.T, Tolerance);
        Assert.Equal(MathF.Sqrt(26f), hit.Gap, Tolerance);
    }

    [Fact]
    public void Nearest_finds_the_middle_of_a_quarter_circle_from_outside_it()
    {
        var hit = QuarterCircle().Nearest(new Vector3(2f, 0f, 2f));

        float half = MathF.Sqrt(0.5f);
        AssertNear(new Vector3(half, 0f, half), hit.Position, 1e-3f);
        Assert.Equal(2f * MathF.Sqrt(2f) - 1f, hit.Gap, 1e-3f);
    }

    [Fact]
    public void Frames_face_along_the_spline_with_up_to_the_sky_and_right_to_the_right()
    {
        var pose = Line().Frame(0.5f);

        AssertNear(Vector3.UnitX, pose.Forward);
        AssertNear(Vector3.UnitY, pose.Up);

        // Looking along +x with +y up, +z is to the right.
        AssertNear(Vector3.UnitZ, pose.Right);
        AssertNear(new Vector3(5f, 0f, 0f), Vector3.Transform(Vector3.Zero, pose.Matrix));
    }

    [Fact]
    public void A_world_frame_on_a_slope_leans_back_but_doesnt_tip_sideways()
    {
        var slope = Spline.Through([Vector3.Zero, new Vector3(10f, 5f, 0f)]);

        var pose = slope.Frame(0.5f);

        // Square to the slope (2, 1) and towards the sky: (-1, 2) / √5.
        AssertNear(new Vector3(-1f, 2f, 0f) / MathF.Sqrt(5f), pose.Up);
        AssertNear(Vector3.UnitZ, pose.Right);
    }

    [Fact]
    public void Roll_tips_the_top_to_the_right_easing_between_points()
    {
        var line = Spline.Through([Vector3.Zero, new Vector3(0f, 0f, 10f)], roll: [0f, MathF.PI / 2f]);

        // Looking along +z, right is -x. Halfway the roll has eased to a quarter of a half turn.
        AssertNear(Vector3.UnitY, line.Frame(0f).Up);
        float half = MathF.Sqrt(0.5f);
        AssertNear(new Vector3(-half, half, 0f), line.Frame(0.5f).Up);
        AssertNear(-Vector3.UnitX, line.Frame(1f).Up);
    }

    [Fact]
    public void A_minimal_frame_goes_upside_down_through_a_loop_without_flipping()
    {
        var loop = LoopTheLoop(SplineUp.Minimal);

        var previous = loop.Frame(0f).Up;
        for (int i = 1; i <= 400; i++)
        {
            var up = loop.Frame(i / 400f).Up;
            Assert.True(Vector3.Dot(previous, up) > 0.95f, $"The up direction jumped at t = {i / 400f}.");
            previous = up;
        }

        Assert.True(loop.Frame(Top(loop)).Up.Y < -0.95f, "At the top of the loop the rider should be upside down.");
        Assert.True(loop.Frame(1f).Up.Y > 0.95f, $"After the loop the rider should be the right way up, not {loop.Frame(1f).Up}, top {loop.Frame(Top(loop)).Up}.");
    }

    [Fact]
    public void A_world_frame_stays_upright_at_the_top_of_a_loop()
    {
        // The other choice, shown to differ: up is to the sky, so the rider isn't upside down.
        var loop = LoopTheLoop(SplineUp.World);

        Assert.True(loop.Frame(Top(loop)).Up.Y > 0.95f);
    }

    [Fact]
    public void A_minimal_frame_turns_round_a_helix_by_its_torsion()
    {
        // A helix of radius 1 rising 1 m per radian, with a point every 15°, for one and a half turns.
        const int perTurn = 24;
        var points = new Vector3[perTurn * 3 / 2 + 1];
        for (int i = 0; i < points.Length; i++)
        {
            float angle = 2f * MathF.PI * i / perTurn;
            points[i] = new Vector3(MathF.Cos(angle), angle, MathF.Sin(angle));
        }

        var helix = Spline.Through(points, up: SplineUp.Minimal);

        // Against the normal pointing in to the axis, a frame that doesn't twist falls behind by the
        // torsion: 1 / (1² + 1²) per metre, over 2π√2 metres a turn, so 2π / √2 radians.
        float start = AngleFromAxis(helix, 4f / helix.Segments);
        float end = AngleFromAxis(helix, (4f + perTurn) / helix.Segments);
        float expected = 2f * MathF.PI / MathF.Sqrt(2f);
        Assert.Equal(MathF.Cos(expected), MathF.Cos(end - start), 0.01f);
        Assert.Equal(MathF.Abs(MathF.Sin(expected)), MathF.Abs(MathF.Sin(end - start)), 0.01f);
    }

    [Fact]
    public void A_minimal_frame_turns_with_a_sharp_corner_that_goes_straight_up()
    {
        // Knots with no handles make sharp corners: along +x, straight up, then along +x again.
        var steps = Spline.Bezier([new SplineKnot(Vector3.Zero), new SplineKnot(new Vector3(10f, 0f, 0f)), new SplineKnot(new Vector3(10f, 10f, 0f)), new SplineKnot(new Vector3(20f, 10f, 0f))], up: SplineUp.Minimal);

        // Up tips back with the heading going up, so it faces -x on the climb, and tips forward again after.
        AssertNear(-Vector3.UnitX, steps.Frame(0.5f).Up, 1e-3f);
        AssertNear(Vector3.UnitY, steps.Frame(5f / 6f).Up, 1e-3f);
    }

    [Fact]
    public void A_closed_minimal_spline_ends_with_the_frame_it_started_with()
    {
        // An uneven ring that climbs and dips, which leaves a minimal frame turned when it comes round unless the twist is spread.
        Vector3[] points = [Vector3.Zero, new(10f, 0f, 0f), new(12f, 5f, 8f), new(4f, 1f, 12f), new(-3f, 6f, 5f)];

        var ring = Spline.Through(points, closed: true, up: SplineUp.Minimal);

        var start = ring.Frame(0f).Up;
        var end = ring.Frame(0.99999f).Up;
        Assert.True(Vector3.Dot(start, end) > 0.999f, $"The frame came round as {end}, not {start}.");
    }

    [Fact]
    public void A_closed_spline_joins_its_end_to_its_start_and_distances_go_round()
    {
        var square = Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 10f), new Vector3(0f, 0f, 10f)], closed: true);

        Assert.Equal(4, square.Segments);
        AssertNear(square.Position(0f), square.Position(1f));
        AssertNear(square.PointAtDistance(3f), square.PointAtDistance(square.Length + 3f), 1e-3f);
        AssertNear(new Vector3(10f, 0f, 0f), square.Position(0.25f));
    }

    [Fact]
    public void Knots_with_no_handles_make_straight_lines()
    {
        var corner = Spline.Bezier([new SplineKnot(Vector3.Zero), new SplineKnot(new Vector3(4f, 0f, 0f)), new SplineKnot(new Vector3(4f, 0f, 3f))]);

        Assert.Equal(7f, corner.Length, 1e-3f);
        AssertNear(new Vector3(2f, 0f, 0f), corner.Position(0.25f));
        AssertNear(Vector3.UnitX, corner.Tangent(0f));
    }

    [Fact]
    public void Scatter_places_poses_every_spacing_along_an_open_spline()
    {
        var poses = Line().Scatter(2.5f);

        Assert.Equal([0f, 2.5f, 5f, 7.5f, 10f], poses.Select(pose => MathF.Round(pose.Position.X, 3)));
    }

    [Fact]
    public void Scatter_spaces_a_closed_spline_evenly_all_the_way_round()
    {
        var square = Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 10f), new Vector3(0f, 0f, 10f)], closed: true);

        // 4.4 fit round, so four, nudged to a quarter of the way each.
        var poses = square.Scatter(square.Length / 4.4f);

        Assert.Equal(4, poses.Length);
        for (int i = 0; i < poses.Length; i++)
        {
            Assert.Equal(square.Length * i / 4f, square.Nearest(poses[i].Position).Distance, 1e-2f);
        }
    }

    [Theory]
    [InlineData(new float[] { 0f, 0f, 0f }, false, "A spline needs at least two points.")]
    [InlineData(new float[] { 0f, 0f, 0f, 1f, 0f, 0f }, true, "A closed spline needs at least three points.")]
    [InlineData(new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f }, false, "Spline points 1 and 2 are in the same place.")]
    [InlineData(new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f }, true, "The last spline point is where the first is. A closed spline joins its ends itself, so leave the last one out.")]
    [InlineData(new float[] { 0f, 0f, 0f, float.NaN, 0f, 0f }, false, "Spline point 1 must be finite.")]
    public void Splines_that_cant_be_made_are_explained(float[] numbers, bool closed, string expected)
    {
        var points = Enumerable.Range(0, numbers.Length / 3).Select(i => new Vector3(numbers[i * 3], numbers[i * 3 + 1], numbers[i * 3 + 2])).ToArray();

        var problem = Assert.Throws<ArgumentException>(() => Spline.Through(points, closed));

        Assert.Equal(expected, problem.Message);
    }

    [Fact]
    public void A_roll_list_must_have_one_roll_for_each_point()
    {
        var problem = Assert.Throws<ArgumentException>(() => Spline.Through([Vector3.Zero, Vector3.UnitX], roll: [0f]));

        Assert.Equal("A spline through 2 points needs one roll for each, but has 1.", problem.Message);
    }

    [Fact]
    public void Content_loads_vectors_and_a_spline_through_points()
    {
        var content = Content(("routes/a.json", """
            {
              "id": "a",
              "start": [1, 2, 3.5],
              "path": { "points": [[0, 0, 0], [10, 0, 0]] },
            }
            """));

        var route = content.LoadAll<Route>("routes")["a"];

        Assert.Equal(new Vector3(1f, 2f, 3.5f), route.Start);
        Assert.Equal(10f, route.Path.Length, Tolerance);
        Assert.False(route.Path.Closed);
    }

    [Fact]
    public void Content_loads_a_closed_spline_of_knots_with_minimal_frames()
    {
        var content = Content(("routes/a.json", """
            {
              "id": "a",
              "start": [0, 0, 0],
              "path": {
                "knots": [
                  { "position": [0, 0, 0] },
                  { "position": [4, 0, 0] },
                  { "position": [4, 0, 3], "in": [0, 0, -1], "out": [0, 0, 1], "roll": 0.5 },
                ],
                "closed": true,
                "up": "minimal",
              },
            }
            """));

        var path = content.LoadAll<Route>("routes")["a"].Path;

        Assert.True(path.Closed);
        Assert.Equal(SplineUp.Minimal, path.Up);
        Assert.Equal(3, path.Segments);
        Assert.Equal(0.5f, path.Knots![2].Roll);
    }

    [Theory]
    [InlineData("""{ "id": "a", "start": [1, 2], "path": { "points": [[0, 0, 0], [1, 0, 0]] } }""", "routes/a.json:1: start must be a list of three numbers, such as [1, 0, 2]")]
    [InlineData("""{ "id": "a", "start": null, "path": { "points": [[0, 0, 0], [1, 0, 0]] } }""", "routes/a.json:1: start must be a list of three numbers, such as [1, 0, 2]")]
    [InlineData("""{ "id": "a", "start": "here", "path": { "points": [[0, 0, 0], [1, 0, 0]] } }""", "routes/a.json:1: start must be a list of three numbers, such as [1, 0, 2]")]
    [InlineData("""{ "id": "a", "start": [1, 2, "3"], "path": { "points": [[0, 0, 0], [1, 0, 0]] } }""", "routes/a.json:1: start must be a list of three numbers, such as [1, 0, 2]")]
    [InlineData("""{ "id": "a", "start": [0, 0, 0], "path": { "points": [[0, 0, 0], [1, 0]] } }""", "routes/a.json:1: path.points[1] must be a list of three numbers, such as [1, 0, 2]")]
    [InlineData("""{ "id": "a", "start": [0, 0, 0], "path": { "points": [[0, 0, 0], [1, 0, 0]], "closed": true } }""", "routes/a.json: A closed spline needs at least three points.")]
    [InlineData("""{ "id": "a", "start": [0, 0, 0], "path": { } }""", "routes/a.json: A spline needs points or knots.")]
    public void Content_explains_vectors_and_splines_that_are_wrong(string json, string expected)
    {
        var content = Content(("routes/a.json", json));

        var problem = Assert.Throws<ContentException>(() => content.LoadAll<Route>("routes"));

        Assert.Equal([expected], problem.Problems);
    }

    [Fact]
    public void A_spline_schema_offers_points_as_three_numbers_but_not_its_length()
    {
        string schema = Content().SchemaText(typeof(Route));

        Assert.Contains("\"points\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"knots\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"minItems\": 3", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"length\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"segments\"", schema, StringComparison.Ordinal);
    }

    /// <summary>A track that runs along +x, goes up and over through a loop, and carries on beside where it started.</summary>
    private static Spline LoopTheLoop(SplineUp up) => Spline.Through(
        [
            new Vector3(-10f, 0f, 0f),
            Vector3.Zero,
            new Vector3(5f, 5f, 0.1f),
            new Vector3(0f, 10f, 0.2f),
            new Vector3(-5f, 5f, 0.3f),
            new Vector3(0f, 0f, 0.4f),
            new Vector3(10f, 0f, 0.4f),
        ],
        up: up);

    /// <summary>How far a frame's up is turned from the normal pointing in to the y axis, round the way it heads.</summary>
    private static float AngleFromAxis(Spline spline, float t)
    {
        var pose = spline.Frame(t);
        var inward = -(pose.Position with { Y = 0f });
        inward -= pose.Forward * Vector3.Dot(inward, pose.Forward);
        inward = Vector3.Normalize(inward);
        return MathF.Atan2(Vector3.Dot(Vector3.Cross(inward, pose.Up), pose.Forward), Vector3.Dot(inward, pose.Up));
    }

    /// <summary>The t of the highest point, where the third point is.</summary>
    private static float Top(Spline spline) => 3f / spline.Segments;

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = Tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}.");

    private static Content Content(params (string Path, string Text)[] files)
    {
        var memory = new MemoryContent();
        foreach (var (path, text) in files)
        {
            memory[path] = text;
        }

        return new Content(memory);
    }
}
