using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

public sealed class ScrollAndDragTests
{
    private const float Close = 0.01f;
    private const int RowHeight = 40;

    private static readonly Color Solid = new(0.2f, 0.2f, 0.2f);
    private static readonly Vector2 Away = new(600f, 600f);

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen)
    {
        var run = HeadlessGame.Create<PointerGame>(1);
        run.Game.Screen = screen;
        run.Ticks(1);
        return run;
    }

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView Rows(int count = 10)
    {
        var scroll = new ScrollView();
        for (int i = 0; i < count; i++)
        {
            scroll.Add(new Text($"row {i}").Frame(90f, RowHeight, alignment: Alignment.TopLeading));
        }

        return scroll.Frame(100f, 100f);
    }

    private static float Y(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text).Frame.Y;

    private static void MoveTo(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.MoveMouse(at);
        run.Ticks(1);
    }

    private static void Hold(HeadlessRun<PointerGame> run)
    {
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
    }

    private static void Release(HeadlessRun<PointerGame> run)
    {
        run.Input.Release(MouseButton.Left);
        run.Ticks(2);
    }

    private static void DragTo(HeadlessRun<PointerGame> run, Vector2 from, Vector2 to)
    {
        MoveTo(run, from);
        Hold(run);
        MoveTo(run, to);
    }

    [Fact]
    public void The_wheel_scrolls_what_it_is_over()
    {
        using var run = Run(_ => Corner(Rows()));
        MoveTo(run, new Vector2(40f, 40f));

        run.Input.Scroll(-1f);
        run.Ticks(1);

        Assert.Equal(32f, Y(run, "row 2"), Close);
    }

    [Fact]
    public void The_wheel_stops_at_the_ends()
    {
        using var run = Run(_ => Corner(Rows()));
        MoveTo(run, new Vector2(40f, 40f));

        run.Input.Scroll(1f);
        run.Ticks(1);
        Assert.Equal(0f, Y(run, "row 0"), Close);

        run.Input.Scroll(-100f);
        run.Ticks(1);
        Assert.Equal(60f, Y(run, "row 9"), Close);
    }

    [Fact]
    public void The_wheel_moves_outward_once_the_inner_area_can_go_no_further()
    {
        using var run = Run(_ =>
        {
            var outer = new ScrollView { Rows(3) };
            for (int i = 0; i < 5; i++)
            {
                outer.Add(new Text($"outer {i}").Frame(80f, RowHeight));
            }

            return Corner(outer.Frame(120f, 200f));
        });

        MoveTo(run, new Vector2(40f, 40f));
        float before = Y(run, "outer 0");

        // The inner list has 20 pixels to give, so this reaches the outer one on the next notch.
        run.Input.Scroll(-1f);
        run.Ticks(1);
        Assert.Equal(before, Y(run, "outer 0"), Close);

        run.Input.Scroll(-1f);
        run.Ticks(1);
        Assert.Equal(before - 48f, Y(run, "outer 0"), Close);
    }

    [Fact]
    public void The_thumb_follows_the_mouse_anywhere_until_release()
    {
        using var run = Run(_ => Corner(Rows()));

        DragTo(run, new Vector2(95f, 10f), new Vector2(300f, 46f));
        Assert.Equal(10f, Y(run, "row 4"), Close);

        Release(run);
        MoveTo(run, new Vector2(95f, 10f));
        Assert.Equal(10f, Y(run, "row 4"), Close);
    }

    [Fact]
    public void A_press_on_the_track_jumps_there()
    {
        using var run = Run(_ => Corner(Rows()));

        MoveTo(run, new Vector2(95f, 80f));
        Hold(run);

        Assert.Equal(85f, Y(run, "row 9"), Close);
    }

    [Fact]
    public void Scrolling_keeps_hover_on_what_is_under_the_cursor_now()
    {
        using var run = Run(_ =>
        {
            var scroll = new ScrollView();
            for (int i = 0; i < 10; i++)
            {
                int row = i;
                scroll.Add(new Pressable(look => new Text(look.Hovered ? $"over {row}" : $"row {row}").Frame(80f, RowHeight).Background(Solid)).Id(row));
            }

            return Corner(scroll.Frame(100f, 100f));
        });
        MoveTo(run, new Vector2(40f, 10f));
        Assert.Contains(run.ShownText, shown => shown.Text == "over 0");

        run.Input.Scroll(-1f);
        run.Ticks(1);

        Assert.Contains(run.ShownText, shown => shown.Text == "over 1");
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "over 0");
    }

    [Fact]
    public void Content_that_shrinks_pulls_the_scroll_back()
    {
        using var run = Run(game => Corner(Rows(game.Reads > 0 ? 3 : 10)));
        MoveTo(run, new Vector2(40f, 40f));
        run.Input.Scroll(-100f);
        run.Ticks(1);

        run.Game.Reads = 1;
        run.Ticks(1);

        Assert.Equal(60f, Y(run, "row 2"), Close);
    }

    [Fact]
    public void A_list_selects_what_is_clicked()
    {
        string? selected = null;
        string[] items = ["iron", "wood", "stone"];
        using var run = Run(_ => Corner(new ListView<string>(items, item => new Text(item), selected, item => selected = item).Frame(200f, 200f)));

        var row = run.ShownText.Single(shown => shown.Text == "wood").Frame;
        run.Input.Click(MouseButton.Left, new Vector2(row.X + 2f, row.Y + 2f));
        run.Ticks(2);

        Assert.Equal("wood", selected);
    }

    [Fact]
    public void A_grid_fills_equal_cells_and_skips_empty_ones()
    {
        var tree = new UiTree();
        tree.Update(
            Corner(new Grid(3, ColumnGap: 10f, RowGap: 4f)
            {
                new Text("a"),
                (UiView?)null,
                new Text("c"),
                new Text("d").Frame(height: 50f),
            }.Frame(width: 320f)),
            800,
            600);

        var a = tree.ShownText.Single(shown => shown.Text == "a").Frame;
        var c = tree.ShownText.Single(shown => shown.Text == "c").Frame;
        var d = tree.ShownText.Single(shown => shown.Text == "d").Frame;

        Assert.Equal(220f, c.X - a.X, Close);
        Assert.True(d.Y > a.Bottom + 3f);
    }

    private sealed record Item(string Name);

    private static UiView Slots(PointerGame game, Func<Item, bool>? canDrop = null) => Corner(new HStack(Spacing: 0f)
    {
        new Text("source").Frame(100f, 100f).Background(Solid).Draggable(new Item("gem"), new Text("preview")),
        new Pressable(look => new Text(look.Hovered ? "target over" : "target").Frame(100f, 100f).Background(Solid))
            .DropTarget<Item>(item => game.Log.Add(item.Name), canDrop),
    });

    [Fact]
    public void A_drag_carries_its_payload_to_a_target()
    {
        using var run = Run(game => Slots(game));

        DragTo(run, new Vector2(50f, 50f), new Vector2(150f, 50f));
        Assert.Contains(run.ShownText, shown => shown.Text == "preview");
        Assert.Contains(run.ShownText, shown => shown.Text == "target over");

        Release(run);

        Assert.Equal(["gem"], run.Game.Log);
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "preview");
    }

    [Fact]
    public void A_short_wiggle_is_not_a_drag()
    {
        using var run = Run(game => Corner(new Text("x").Frame(100f, 100f).Background(Solid)
            .OnClick(() => game.Log.Add("click"))
            .Draggable(new Item("gem"), new Text("preview"))));

        DragTo(run, new Vector2(50f, 50f), new Vector2(52f, 51f));
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "preview");

        Release(run);

        Assert.Equal(["click"], run.Game.Log);
    }

    [Fact]
    public void A_drag_that_starts_ends_the_click()
    {
        using var run = Run(game => Corner(new Text("x").Frame(100f, 100f).Background(Solid)
            .OnClick(() => game.Log.Add("click"))
            .Draggable(new Item("gem"))));

        DragTo(run, new Vector2(50f, 50f), new Vector2(70f, 50f));
        Release(run);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_refused_target_looks_like_nothing_and_takes_nothing()
    {
        using var run = Run(game => Slots(game, _ => false));

        DragTo(run, new Vector2(50f, 50f), new Vector2(150f, 50f));
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "target over");

        Release(run);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Releasing_elsewhere_cancels()
    {
        using var run = Run(game => Slots(game));

        DragTo(run, new Vector2(50f, 50f), Away);
        Release(run);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_drag_ends_when_its_source_goes_away()
    {
        using var run = Run(game => game.Reads > 0 ? new Text("gone") : Slots(game));

        DragTo(run, new Vector2(50f, 50f), new Vector2(150f, 50f));
        run.Game.Reads = 1;
        run.Ticks(1);

        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "preview");
    }

    private static UiView Tipped(float x, float y) => new Text("thing").Frame(100f, 100f).Background(Solid).Tooltip(new Text("tip").Frame(120f, 40f))
        .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: x > 0f ? Alignment.BottomTrailing : Alignment.TopLeading);

    [Fact]
    public void A_tooltip_waits_for_a_short_hover()
    {
        using var run = Run(_ => Tipped(0f, 0f));

        MoveTo(run, new Vector2(40f, 40f));
        run.Ticks(5);
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "tip");

        run.Ticks(15);
        Assert.Contains(run.ShownText, shown => shown.Text == "tip");
    }

    [Fact]
    public void A_tooltip_goes_away_on_a_press_and_stays_away()
    {
        using var run = Run(_ => Tipped(0f, 0f));
        MoveTo(run, new Vector2(40f, 40f));
        run.Ticks(20);

        Hold(run);
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "tip");

        Release(run);
        run.Ticks(20);
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "tip");
    }

    [Fact]
    public void A_tooltip_flips_away_from_the_window_edges()
    {
        using var run = Run(_ => Tipped(1f, 1f));
        var cursor = new Vector2(1270f, 710f);

        MoveTo(run, cursor);
        run.Ticks(20);

        var tip = run.ShownText.Single(shown => shown.Text == "tip").Frame;
        Assert.True(tip.Right <= cursor.X);
        Assert.True(tip.Bottom <= cursor.Y);
    }
}
