using System.Numerics;
using Rendition;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Views over points in the world, and the compass, minimap frame and floating text that go with them.</summary>
public sealed class WorldTests
{
    // Ten units back from the middle of the world, looking at it, with the default quarter-turn-wide view.
    private static readonly Camera Looking = new(new Vector3(0f, 0f, 10f), Vector3.Zero);

    private static UiTree Shown(UiView view, Camera camera, float scale = 1f)
    {
        var tree = new UiTree { Scale = scale };
        tree.World.Look(camera);
        tree.Update(new ZStack { view }.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity), 800, 600);
        return tree;
    }

    private static UiView Bar => new Rectangle().Frame(width: 20f, height: 10f).Id("bar");

    private static int Arrows(UiTree tree) => tree.Quads.ToArray().Count(quad => quad.Kind == OverlayKind.Arc);

    [Fact]
    public void A_view_sits_on_its_point_with_its_bottom_middle()
    {
        var tree = Shown(Bar.AtWorld(Vector3.Zero), Looking);

        Assert.Equal(new UiRect(390f, 290f, 20f, 10f), tree.FrameOf("bar"));
    }

    [Fact]
    public void A_point_to_the_side_lands_where_the_camera_sees_it()
    {
        // One unit right at ten away: tan(22.5°) is 0.41421, so it's 1 / (10 × 0.41421 × 4/3) = 0.18107 of the way
        // from the middle to the right edge, 72.4 pixels at 400 across.
        var tree = Shown(Bar.AtWorld(Vector3.UnitX, Alignment.Center), Looking);

        var frame = tree.FrameOf("bar");
        Assert.Equal(472.4f, frame.X + frame.Width / 2f, 0.1f);
        Assert.Equal(300f, frame.Y + frame.Height / 2f, 0.1f);
    }

    [Fact]
    public void Its_place_is_in_interface_units_when_the_interface_is_scaled()
    {
        var tree = Shown(Bar.AtWorld(Vector3.Zero, Alignment.Center), Looking, scale: 2f);

        Assert.Equal(new UiRect(190f, 145f, 20f, 10f), tree.FrameOf("bar"));
    }

    [Fact]
    public void It_takes_no_room_where_it_is_laid_out()
    {
        var tree = Shown(new HStack(VerticalAlignment.Top, 0f) { Bar.AtWorld(Vector3.Zero), new Rectangle().Frame(width: 30f, height: 30f).Id("next") }, Looking);

        var alone = Shown(new Rectangle().Frame(width: 30f, height: 30f).Id("next"), Looking);
        Assert.Equal(alone.FrameOf("next"), tree.FrameOf("next"));
    }

    [Fact]
    public void A_point_behind_the_camera_hides_it()
    {
        var tree = Shown(new Text("Goblin").AtWorld(new Vector3(0f, 0f, 20f)), Looking);

        Assert.DoesNotContain(tree.ShownText, text => text.Text == "Goblin");
        Assert.Equal(0, Arrows(tree));
    }

    [Fact]
    public void A_point_in_front_shows_its_view()
    {
        var tree = Shown(new Text("Goblin").AtWorld(Vector3.Zero), Looking);

        Assert.Contains(tree.ShownText, text => text.Text == "Goblin");
    }

    [Fact]
    public void A_point_off_the_side_clings_to_that_edge_with_an_arrow()
    {
        var tree = Shown(Bar.AtWorld(new Vector3(100f, 0f, 0f), offScreen: OffScreen.Edge), Looking);

        // In from the right edge by half its width, the arrow's gap and width, and the room kept to the edge.
        var frame = tree.FrameOf("bar");
        Assert.Equal(800f - 10f - 6f - 3f - 8f, frame.X + frame.Width / 2f, 0.01f);
        Assert.Equal(300f, frame.Y + frame.Height / 2f, 0.01f);
        Assert.Equal(1, Arrows(tree));
    }

    [Fact]
    public void Hidden_off_the_side_it_goes_past_the_edge_with_no_arrow()
    {
        var tree = Shown(Bar.AtWorld(new Vector3(100f, 0f, 0f), Alignment.Center), Looking);

        Assert.True(tree.FrameOf("bar").X > 800f);
        Assert.Equal(0, Arrows(tree));
    }

    [Fact]
    public void A_point_behind_and_to_the_left_clings_to_the_left_edge()
    {
        var tree = Shown(Bar.AtWorld(new Vector3(-5f, 0f, 20f), offScreen: OffScreen.Edge), Looking);

        var frame = tree.FrameOf("bar");
        Assert.Equal(10f + 6f + 3f + 8f, frame.X + frame.Width / 2f, 0.01f);
        Assert.Equal(300f, frame.Y + frame.Height / 2f, 0.01f);
    }

    [Fact]
    public void A_point_straight_behind_clings_to_the_bottom()
    {
        var tree = Shown(Bar.AtWorld(new Vector3(0f, 0f, 20f), offScreen: OffScreen.Edge), Looking);

        var frame = tree.FrameOf("bar");
        Assert.Equal(400f, frame.X + frame.Width / 2f, 0.01f);
        Assert.Equal(600f - 27f, frame.Y + frame.Height / 2f, 0.01f);
    }

    [Fact]
    public void The_view_follows_when_the_camera_moves()
    {
        var view = new ZStack { Bar.AtWorld(Vector3.Zero, Alignment.Center) }.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity);
        var tree = new UiTree();
        tree.World.Look(Looking);
        tree.Update(view, 800, 600);

        // Moved one unit left with the point dead ahead of where it was, the point is now right of the middle.
        tree.World.Look(new Camera(new Vector3(-1f, 0f, 10f), new Vector3(-1f, 0f, 0f)));
        tree.Update(view, 800, 600);

        Assert.Equal(472.4f, tree.FrameOf("bar").X + 10f, 0.1f);
    }

    [Fact]
    public void With_no_camera_yet_nothing_shows()
    {
        var tree = new UiTree();
        tree.Update(new Text("Goblin").AtWorld(Vector3.Zero), 800, 600);

        Assert.Empty(tree.ShownText);
    }

    [Fact]
    public void The_compass_puts_north_in_the_middle_facing_north_and_east_there_facing_east()
    {
        var north = Shown(new Compass(0f), Looking);
        var east = Shown(new Compass(MathF.PI / 2f), Looking);

        Assert.Equal(400f, Middle(north, "N"), 0.5f);
        Assert.Equal(400f, Middle(east, "E"), 0.5f);

        // North-east is an eighth of a turn right, a quarter of the strip's 180°, so 90 of its 360 units. South is
        // half a turn round, past the end.
        Assert.Equal(400f + 90f, Middle(north, "NE"), 0.5f);
        Assert.DoesNotContain(north.ShownText, text => text.Text == "S");
    }

    [Fact]
    public void Bearings_count_clockwise_from_north_at_minus_z()
    {
        Assert.Equal(0f, Compass.BearingTo(Vector3.Zero, -Vector3.UnitZ), 0.0001f);
        Assert.Equal(MathF.PI / 2f, Compass.BearingTo(Vector3.Zero, Vector3.UnitX), 0.0001f);
        Assert.Equal(-MathF.PI / 2f, Compass.BearingTo(Vector3.Zero, -Vector3.UnitX), 0.0001f);
        Assert.Equal(MathF.PI / 2f, Compass.HeadingOf(new Camera(Vector3.One, Vector3.One + Vector3.UnitX)), 0.0001f);
    }

    [Fact]
    public void Floating_text_rises_and_is_gone_after_its_time()
    {
        var texts = new FloatingTexts { Seconds = 1f };
        texts.Add("12", Vector3.Zero);
        var tree = new UiTree();
        tree.World.Look(Looking);
        UiView Screen() => texts.View.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity);

        tree.Update(Screen(), 800, 600, now: 0);
        float start = tree.ShownText.Single(text => text.Text == "12").Frame.Y;
        tree.Update(Screen(), 800, 600, now: 0.5);
        float later = tree.ShownText.Single(text => text.Text == "12").Frame.Y;
        Assert.True(later < start - 20f, $"It rose from {start} only to {later}.");

        texts.Update(0.6f);
        Assert.Equal(1, texts.Count);
        texts.Update(0.5f);
        Assert.Equal(0, texts.Count);
    }

    [Fact]
    public void Floating_text_stays_put_and_shown_with_animations_off()
    {
        var tree = new UiTree();
        tree.Update(new FloatingText("12").Animations(false).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity), 800, 600, now: 0);
        tree.Update(new FloatingText("12").Animations(false).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity), 800, 600, now: 5);

        var shown = tree.ShownText.Single(text => text.Text == "12");
        Assert.Equal(1f, shown.Color.A, 0.001f);
        Assert.Equal(300f, shown.Frame.Y + shown.Frame.Height / 2f, 0.5f);
    }

    [Fact]
    public void A_minimap_mark_past_the_rim_waits_on_it()
    {
        var tree = Shown(new MinimapFrame(new Rectangle()) { Size = 160f, Marks = [new MinimapMark(new Vector2(3f, 0f), Color.Red)] }, Looking);

        // The rim is 80 from the middle; the mark sits its own size in, at 72.
        var mark = tree.Quads.ToArray().Single(quad => quad.Kind == OverlayKind.Ellipse && quad.Color == new Vector4(1f, 0f, 0f, 1f));
        Assert.Equal(400f + 72f, mark.Position.X + 4f, 0.01f);
    }

    private static float Middle(UiTree tree, string text)
    {
        var frame = tree.ShownText.Single(shown => shown.Text == text).Frame;
        return frame.X + frame.Width / 2f;
    }
}
