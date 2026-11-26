using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class DebugDrawTests
{
    private static void AssertNear(Vector3 expected, Vector3 actual, float within = 0.001f) =>
        Assert.True(Vector3.Distance(expected, actual) < within, $"Expected {expected}, got {actual}");

    private static ViewDebug Canvas() => new View().Debug;

    [Fact]
    public void A_line_keeps_its_ends_colour_and_look_and_draws_two_pixels_thick_and_faint_behind_the_world()
    {
        var debug = Canvas();
        debug.Line(Vector3.Zero, new Vector3(1f, 2f, 3f), Color.Red);

        var line = Assert.Single(debug.Lines.ToArray());
        Assert.Equal(new DebugLine(Vector3.Zero, new Vector3(1f, 2f, 3f), Color.Red, default), line);
        var drawn = Assert.Single(debug.Draws.ToArray());
        Assert.Equal(2f, drawn.Width);
        Assert.Equal(0.25f, drawn.Hidden);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), drawn.Color);
    }

    [Fact]
    public void A_thickness_and_on_top_reach_the_renderer()
    {
        var debug = Canvas();
        debug.Line(Vector3.Zero, Vector3.UnitX, Color.White, new DebugLook(Thickness: 5f, OnTop: true));

        var drawn = Assert.Single(debug.Draws.ToArray());
        Assert.Equal(5f, drawn.Width);
        Assert.Equal(1f, drawn.Hidden);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(65f)]
    [InlineData(float.NaN)]
    public void A_thickness_that_cant_be_drawn_says_what_it_can_be(float thickness)
    {
        var debug = Canvas();
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => debug.Box(Vector3.Zero, Vector3.One, Color.White, new DebugLook(Thickness: thickness)));
        Assert.StartsWith("A debug shape's thickness is in window pixels, from 0 for the default to 64.", error.Message);
    }

    [Fact]
    public void An_arrow_has_a_head_a_quarter_of_its_length_at_its_end()
    {
        var debug = Canvas();
        debug.Arrow(Vector3.Zero, new Vector3(4f, 0f, 0f), Color.White);

        var lines = debug.Lines.ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal(new Vector3(4f, 0f, 0f), lines[0].To);
        // Four long, so the head goes one back and half a unit to each side, level.
        AssertNear(new Vector3(3f, 0f, 0.5f), lines[1].To);
        AssertNear(new Vector3(3f, 0f, -0.5f), lines[2].To);
        Assert.All(lines[1..], line => Assert.Equal(new Vector3(4f, 0f, 0f), line.From));
    }

    [Fact]
    public void A_path_joins_its_points_and_closes_when_asked()
    {
        Vector3[] points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ];
        var open = Canvas();
        open.Path(points, Color.White);
        var closed = Canvas();
        closed.Path(points, Color.White, closed: true);

        Assert.Equal(2, open.Lines.Length);
        Assert.Equal(3, closed.Lines.Length);
        Assert.Equal(new DebugLine(Vector3.UnitZ, Vector3.Zero, Color.White, default), closed.Lines[2]);
    }

    [Fact]
    public void A_box_is_twelve_edges_between_its_eight_corners()
    {
        var debug = Canvas();
        debug.Box(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), Color.White);

        var lines = debug.Lines.ToArray();
        Assert.Equal(12, lines.Length);
        // From 0 to 2 across, 0 to 4 up and 0 to 6 deep: four edges of each length.
        Assert.Equal([2f, 2f, 2f, 2f, 4f, 4f, 4f, 4f, 6f, 6f, 6f, 6f], lines.Select(line => MathF.Round(Vector3.Distance(line.From, line.To), 3)).Order());
        var corners = lines.SelectMany(line => new[] { line.From, line.To }).Distinct().ToArray();
        Assert.Equal(8, corners.Length);
        Assert.All(corners, corner => Assert.True(
            corner.X is 0f or 2f && corner.Y is 0f or 4f && corner.Z is 0f or 6f, $"{corner} isn't a corner"));
    }

    [Fact]
    public void A_circle_goes_all_the_way_round_in_its_plane()
    {
        var debug = Canvas();
        var center = new Vector3(1f, 3f, 0f);
        debug.Circle(center, Vector3.UnitY, 2f, Color.White);

        var lines = debug.Lines.ToArray();
        Assert.Equal(32, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal(2f, Vector3.Distance(center, line.From), 0.001f);
            Assert.Equal(3f, line.From.Y, 0.001f);
        });
        AssertNear(lines[0].From, lines[^1].To);
        for (int i = 1; i < lines.Length; i++)
        {
            Assert.Equal(lines[i - 1].To, lines[i].From);
        }
    }

    [Fact]
    public void A_sphere_is_three_circles_on_its_surface()
    {
        var debug = Canvas();
        debug.Sphere(new Vector3(0f, 1f, 0f), 3f, Color.White);

        Assert.Equal(96, debug.Lines.Length);
        Assert.All(debug.Lines.ToArray(), line => Assert.Equal(3f, Vector3.Distance(new Vector3(0f, 1f, 0f), line.To), 0.001f));
    }

    [Fact]
    public void A_capsule_stays_its_radius_from_the_line_between_its_ends_and_reaches_past_them()
    {
        var debug = Canvas();
        debug.Capsule(Vector3.Zero, new Vector3(0f, 2f, 0f), 1f, Color.White);

        var lines = debug.Lines.ToArray();
        // Two circles, four sides and four half circles of sixteen.
        Assert.Equal(32 + 32 + 4 + 64, lines.Length);
        foreach (var point in lines.SelectMany(line => new[] { line.From, line.To }))
        {
            var nearest = new Vector3(0f, Math.Clamp(point.Y, 0f, 2f), 0f);
            Assert.Equal(1f, Vector3.Distance(nearest, point), 0.001f);
        }

        Assert.Equal(3f, lines.Max(line => line.To.Y), 0.001f);
        Assert.Equal(-1f, lines.Min(line => line.To.Y), 0.001f);
    }

    [Fact]
    public void Axes_point_along_the_transform_in_red_green_and_blue()
    {
        var debug = Canvas();
        debug.Axes(Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(1f, 0f, 0f), 0.5f);

        var lines = debug.Lines.ToArray();
        Assert.Equal(
            [
                new DebugLine(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), Color.Red, default),
                new DebugLine(new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), Color.Green, default),
                new DebugLine(new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 1f), Color.Blue, default),
            ],
            lines);
    }

    [Fact]
    public void A_view_of_its_own_shows_only_shapes_with_no_channel()
    {
        var debug = Canvas();
        debug.Line(Vector3.Zero, Vector3.UnitX, Color.White);
        debug.Line(Vector3.Zero, Vector3.UnitY, Color.White, "ai");
        debug.Text(Vector3.Zero, "seen", Color.White);
        debug.Text(Vector3.Zero, "hidden", Color.White, "ai");

        Assert.Equal(1, debug.Count(null));
        Assert.Equal(0, debug.Count("ai"));
        Assert.Equal("seen", Assert.Single(debug.Labels.ToArray()).Text);
    }

    [Fact]
    public void A_hidden_channel_doesnt_check_the_look()
    {
        var debug = Canvas();
        debug.Line(Vector3.Zero, Vector3.UnitX, Color.White, new DebugLook("ai", Thickness: -1f));
        Assert.Equal(0, debug.Lines.Length);
    }

    [Fact]
    public void Channels_show_hide_and_toggle_case_aside()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        run.Game.Shapes = view => view.Debug.Line(Vector3.Zero, Vector3.UnitX, Color.White, "ai");

        run.Frame(0f);
        Assert.Equal(0, run.Debug.Count("ai"));

        debug.Show("AI");
        run.Frame(0f);
        Assert.True(debug.Shows("ai"));
        Assert.Equal(1, run.Debug.Count("Ai"));

        debug.Toggle("ai");
        run.Frame(0f);
        Assert.Equal(0, run.Debug.Count("ai"));

        debug.Toggle("ai");
        debug.Hide("ai");
        run.Frame(0f);
        Assert.False(debug.Shows("ai"));
        Assert.Equal(0, run.Debug.Count("ai"));
    }

    [Fact]
    public void A_shape_drawn_in_update_stays_until_the_next_tick()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        run.Game.Ticked = tick =>
        {
            if (tick == 0)
            {
                debug.Line(Vector3.Zero, Vector3.UnitX, Color.White);
            }
        };

        run.Ticks(1);
        run.Frame(0f);
        run.Frame(0.5f);
        Assert.Equal(1, run.Debug.Count(null));

        run.Ticks(1);
        run.Frame(0f);
        Assert.Equal(0, run.Debug.Count(null));
    }

    [Fact]
    public void A_kept_shape_stays_for_its_seconds_of_ticks()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        run.Game.Ticked = tick =>
        {
            if (tick == 0)
            {
                // A second at thirty ticks a second.
                debug.For(1f).Sphere(Vector3.Zero, 1f, Color.White);
            }
        };

        run.Ticks(30);
        run.Frame(0f);
        Assert.Equal(96, run.Debug.Count(null));

        run.Ticks(1);
        run.Frame(0f);
        Assert.Equal(0, run.Debug.Count(null));
    }

    [Fact]
    public void A_kept_shape_hides_with_its_channel()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        debug.Show("ai");
        debug.For(10f).Line(Vector3.Zero, Vector3.UnitX, Color.White, "ai");

        run.Frame(0f);
        Assert.Equal(1, run.Debug.Count("ai"));

        debug.Hide("ai");
        run.Frame(0f);
        Assert.Equal(0, run.Debug.Count("ai"));
    }

    [Fact]
    public void The_same_seconds_give_the_same_canvas()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        Assert.Same(debug.For(2f), debug.For(2f));
        Assert.NotSame(debug.For(2f), debug.For(3f));
    }

    [Fact]
    public void Negative_seconds_say_what_they_can_be()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => run.Service<DebugDraw>().For(-1f));
        Assert.StartsWith("A debug shape is kept for 0 seconds or more.", error.Message);
    }

    [Fact]
    public void A_label_shows_its_text_just_above_its_place_on_screen()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        run.Game.Shapes = view => view.Debug.Text(Vector3.Zero, "Goblin", Color.Yellow);

        run.Frame(0f);
        var shown = Assert.Single(run.ShownText, text => text.Text == "Goblin");
        // The camera looks at zero, so it's centred across the screen, above the middle.
        var middle = run.ScreenSize / 2f;
        Assert.Equal(middle.X, shown.Frame.X + shown.Frame.Width / 2f, 1f);
        Assert.True(shown.Frame.Y + shown.Frame.Height < middle.Y, $"{shown.Frame} isn't above the middle");
        Assert.Equal(Color.Yellow, shown.Color);
    }

    [Fact]
    public void A_label_behind_the_camera_isnt_shown()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        run.Game.Shapes = view => view.Debug.Text(new Vector3(0f, 0f, 20f), "Behind", Color.White);

        run.Frame(0f);
        Assert.DoesNotContain(run.ShownText, text => text.Text == "Behind");
    }

    [Fact]
    public void Drawing_and_keeping_shapes_allocates_nothing_once_warm()
    {
        using var run = HeadlessGame.Create<Debugged>(1);
        var debug = run.Service<DebugDraw>();
        var view = new View();
        view.Debug.Channels = debug;
        var kept = debug.For(0f);
        var time = run.Service<GameTime>();

        void Once()
        {
            view.Debug.Clear();
            time.Tick++;
            ((ITickHook)debug).BeforeUpdate();
            kept.Capsule(Vector3.Zero, Vector3.UnitY, 1f, Color.White);
            view.Debug.Sphere(Vector3.Zero, 1f, Color.White);
            view.Debug.Text(Vector3.Zero, "label", Color.White);
            view.Debug.Line(Vector3.Zero, Vector3.UnitX, Color.White, "hidden");
            debug.CopyTo(view.Debug);
        }

        for (int i = 0; i < 5; i++)
        {
            Once();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Once();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class Debugged(GameTime time) : IGame
    {
        public Action<View>? Shapes { get; set; }

        public Action<long>? Ticked { get; set; }

        public void Update(GameTime _) => Ticked?.Invoke(time.Tick);

        public void Draw(View view)
        {
            view.Camera(new Vector3(0f, 0f, 10f), Vector3.Zero);
            Shapes?.Invoke(view);
        }
    }
}
