using System.Numerics;
using Rendition;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Overlays, offsets, hiding, priorities, aspect ratios, flows, grids of rows, views that fit, and lazy stacks.</summary>
public sealed class LayoutTests
{
    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color Blue = new(0f, 0f, 1f);

    private const float Close = 0.01f;

    private static UiTree Show(UiView view, int width = 800, int height = 600)
    {
        var tree = new UiTree();
        tree.Update(view, width, height);
        return tree;
    }

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView Block(float width, float height) => new Spacer().Frame(width, height);

    private static ShownText Find(UiTree tree, string text) => tree.ShownText.Single(shown => shown.Text == text);


    [Fact]
    public void An_overlay_sits_inside_the_view_and_leaves_its_size_alone()
    {
        var tree = Show(Corner(new VStack(Spacing: 0f)
        {
            Block(200f, 50f).Overlay(new Text("badge"), Alignment.TopTrailing).Id("card"),
            new Text("below"),
        }));
        var card = tree.FrameOf("card");
        var badge = Find(tree, "badge").Frame;

        Assert.Equal(50f, card.Height, Close);
        Assert.Equal(card.Y + card.Height, Find(tree, "below").Frame.Y, Close);
        Assert.Equal(card.X + card.Width, badge.X + badge.Width, Close);
        Assert.Equal(card.Y, badge.Y, Close);
    }

    [Fact]
    public void A_background_view_draws_behind_and_an_overlay_in_front()
    {
        var behind = Show(Block(100f, 50f).Background(Red).Background(new Rectangle().Fill(Blue))).Quads.ToArray();
        var front = Show(Block(100f, 50f).Background(Red).Overlay(new Rectangle().Fill(Blue))).Quads.ToArray();

        var red = new Vector4(1f, 0f, 0f, 1f);
        var blue = new Vector4(0f, 0f, 1f, 1f);
        Assert.Equal([blue, red], behind.Select(quad => quad.Color));
        Assert.Equal([red, blue], front.Select(quad => quad.Color));
        Assert.All(behind, quad => Assert.Equal(new Vector2(100f, 50f), quad.Size));
    }

    [Fact]
    public void An_offset_moves_the_view_but_not_its_neighbours()
    {
        var plain = Show(Corner(new VStack(HorizontalAlignment.Leading) { new Text("moved"), new Text("still") }));
        var moved = Show(Corner(new VStack(HorizontalAlignment.Leading) { new Text("moved").Offset(30f, 5f), new Text("still") }));

        Assert.Equal(Find(plain, "moved").Frame.X + 30f, Find(moved, "moved").Frame.X, Close);
        Assert.Equal(Find(plain, "moved").Frame.Y + 5f, Find(moved, "moved").Frame.Y, Close);
        Assert.Equal(Find(plain, "still").Frame, Find(moved, "still").Frame);
    }

    [Fact]
    public void An_offset_button_is_clicked_where_it_is_drawn()
    {
        var log = new List<string>();
        using var run = PointerGame.Show(_ => Corner(new Button("Go", () => log.Add("go")).Offset(x: 300f)));
        var button = run.Elements.Single(element => element.Role == UiRole.Button).Frame;
        Assert.Equal(300f, button.X, Close);

        // Where it would be without the offset.
        Click(run, new Vector2(10f, button.Y + button.Height / 2f));
        Assert.Empty(log);

        Click(run, new Vector2(button.X + button.Width / 2f, button.Y + button.Height / 2f));
        Assert.Equal(["go"], log);
    }

    [Fact]
    public void A_hidden_view_keeps_its_room_and_shows_nothing()
    {
        var shown = Show(Corner(new VStack { new Text("gone"), new Text("after") }));
        var hidden = Show(Corner(new VStack { new Text("gone").Hidden(), new Text("after") }));

        Assert.DoesNotContain(hidden.ShownText, text => text.Text == "gone");
        Assert.Equal(Find(shown, "after").Frame, Find(hidden, "after").Frame);
        Assert.Contains(Show(new Text("back").Hidden(false)).ShownText, text => text.Text == "back");
    }

    [Fact]
    public void A_hidden_button_takes_no_click_focus_or_element()
    {
        var log = new List<string>();
        using var run = PointerGame.Show(_ => Corner(new VStack
        {
            new Button("Hidden", () => log.Add("hidden")).Id("hidden").Hidden(),
            new Button("Shown", () => log.Add("shown")).Id("shown"),
        }));
        var shown = Assert.Single(run.Elements);
        Assert.Equal("shown", shown.Id);

        Click(run, new Vector2(shown.Frame.X + 10f, shown.Frame.Y - 10f));
        Press(run, Key.Tab);
        Press(run, Key.Enter);

        Assert.Equal(["shown"], log);
    }

    [Fact]
    public void A_higher_layout_priority_gets_its_room_first()
    {
        const string Wanted = "the label that must stay whole";
        const string Other = "a label that can wrap instead";
        float line = Find(Show(new Text(Wanted)), Wanted).Frame.Height;
        float whole = Find(Show(new Text(Wanted)), Wanted).Frame.Width;
        float room = whole + 80f;

        var plain = Show(new HStack(Spacing: 0f) { new Text(Wanted), new Text(Other) }.Frame(width: room));
        var first = Show(new HStack(Spacing: 0f) { new Text(Wanted).LayoutPriority(1f), new Text(Other) }.Frame(width: room));

        Assert.True(Find(plain, Wanted).Frame.Height > line * 1.5f);
        Assert.Equal(line, Find(first, Wanted).Frame.Height, Close);
        Assert.Equal(whole, Find(first, Wanted).Frame.Width, 0.5f);
        Assert.True(Find(first, Other).Frame.Height > line * 1.5f);
    }

    [Fact]
    public void An_aspect_ratio_fits_inside_the_room_or_fills_it()
    {
        var fit = Assert.Single(Show(new Rectangle().Fill(Red).AspectRatio(2f).Frame(400f, 100f)).Quads.ToArray());
        var fill = Assert.Single(Show(new Rectangle().Fill(Red).AspectRatio(2f, ContentMode.Fill).Frame(100f, 100f)).Quads.ToArray());

        Assert.Equal(new Vector2(200f, 100f), fit.Size);
        Assert.Equal(new Vector2(200f, 100f), fill.Size);
    }

    [Fact]
    public void An_aspect_ratio_with_one_length_works_out_the_other()
    {
        var quad = Assert.Single(Show(new VStack { new Rectangle().Fill(Red).AspectRatio(4f).Frame(width: 200f) }).Quads.ToArray());

        Assert.Equal(new Vector2(200f, 50f), quad.Size);
    }

    [Fact]
    public void An_aspect_ratio_must_be_above_zero()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Spacer().AspectRatio(0f));

        Assert.Equal("ratio", error.ParamName);
        Assert.StartsWith("An aspect ratio is a width over a height, above zero.", error.Message);
    }

    private static FlowStack Chips(HorizontalAlignment alignment = HorizontalAlignment.Leading)
    {
        var flow = new FlowStack(Spacing: 10f, LineSpacing: 6f, Alignment: alignment);
        for (int i = 0; i < 5; i++)
        {
            flow.Add(Block(100f, 20f).Id($"chip {i}"));
        }

        return flow;
    }

    [Fact]
    public void A_flow_stack_starts_a_new_line_when_the_next_child_does_not_fit()
    {
        var tree = Show(Corner(Chips().Frame(width: 330f, alignment: Alignment.TopLeading).Id("flow")));
        var origin = tree.FrameOf("chip 0");

        Assert.Equal(origin.X + 220f, tree.FrameOf("chip 2").X, Close);
        Assert.Equal(origin.Y, tree.FrameOf("chip 2").Y, Close);
        Assert.Equal(origin.X, tree.FrameOf("chip 3").X, Close);
        Assert.Equal(origin.Y + 26f, tree.FrameOf("chip 3").Y, Close);
        Assert.Equal(46f, tree.FrameOf("flow").Height, Close);
    }

    [Fact]
    public void A_centred_flow_stack_centres_each_line()
    {
        var tree = Show(Corner(Chips(HorizontalAlignment.Center).Frame(width: 330f)));

        // The first line is 320 wide in 330, so 5 in; the second is 210, so 60 in.
        Assert.Equal(tree.FrameOf("chip 0").X + 55f, tree.FrameOf("chip 3").X, Close);
    }

    [Fact]
    public void Grid_rows_line_their_columns_up_on_the_widest_cell()
    {
        var tree = Show(Corner(new Grid(ColumnGap: 12f)
        {
            new GridRow { new Text("Name"), new Text("one").Id("first") },
            new GridRow { new Text("A much longer label"), new Text("two").Id("second") },
        }));

        var label = Find(tree, "A much longer label").Frame;
        Assert.Equal(tree.FrameOf("first").X, tree.FrameOf("second").X, Close);
        Assert.Equal(label.X + label.Width + 12f, tree.FrameOf("second").X, 0.5f);
    }

    [Fact]
    public void A_stretching_grid_cell_takes_the_width_left_over()
    {
        var tree = Show(Corner(new Grid(ColumnGap: 10f)
        {
            new GridRow { new Text("Volume"), new Spacer().Frame(maxWidth: UiExtensions.Infinity, height: 10f).Id("bar") },
            new GridRow { new Text("Music"), new Text("50%") },
        }.Frame(width: 400f, alignment: Alignment.TopLeading).Id("grid")));
        var grid = tree.FrameOf("grid");
        var bar = tree.FrameOf("bar");

        Assert.Equal(grid.X + 400f, bar.X + bar.Width, 0.5f);
        Assert.Equal(Find(tree, "50%").Frame.X, bar.X, Close);
    }

    [Fact]
    public void A_child_that_is_not_a_row_spans_every_column()
    {
        var tree = Show(Corner(new Grid(ColumnGap: 10f)
        {
            new GridRow { Block(100f, 20f), Block(50f, 20f) },
            new Spacer().Frame(maxWidth: UiExtensions.Infinity, height: 2f).Id("rule"),
            new GridRow { Block(80f, 20f), Block(70f, 20f).Id("last") },
        }));
        var rule = tree.FrameOf("rule");

        // The columns are 100 and 70 wide, with a gap of 10.
        Assert.Equal(180f, rule.Width, Close);
        Assert.Equal(rule.Y + rule.Height + 10f, tree.FrameOf("last").Y, Close);
    }

    [Fact]
    public void A_grid_without_rows_or_columns_says_what_it_needs()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Show(new Grid { new Text("lost") }));

        Assert.Equal("A Grid needs Columns above zero, or GridRows that set the columns.", error.Message);
    }

    private static UiView Labels() => new ViewThatFits
    {
        new Text("Start a new game from the beginning"),
        new Text("New game"),
        new Text("New"),
    };

    [Fact]
    public void A_view_that_fits_shows_the_first_child_that_fits()
    {
        var wide = Show(Labels().Frame(width: 600f));
        var middle = Show(Labels().Frame(width: Find(Show(new Text("New game")), "New game").Frame.Width + 2f));
        var narrow = Show(Labels().Frame(width: 5f));

        Assert.Equal(["Start a new game from the beginning"], wide.ShownText.Select(text => text.Text));
        Assert.Equal(["New game"], middle.ShownText.Select(text => text.Text));
        Assert.Equal(["New"], narrow.ShownText.Select(text => text.Text));
    }

    [Fact]
    public void Only_the_view_that_fits_is_an_element_and_takes_focus()
    {
        var log = new List<string>();
        using var run = PointerGame.Show(_ => Corner(new ViewThatFits
        {
            new Button("Start a new game from the beginning", () => log.Add("long")),
            new Button("New", () => log.Add("short")),
        }.Frame(width: 120f)));

        Assert.Equal(["New"], run.Elements.Select(element => element.Label));
        Press(run, Key.Tab);
        Press(run, Key.Enter);
        Assert.Equal(["short"], log);
    }

    [Fact]
    public void A_view_that_fits_in_one_direction_ignores_the_other()
    {
        var both = Show(Labels().Frame(width: 600f, height: 5f));
        var across = Show(new ViewThatFits(Axis.Horizontal)
        {
            new Text("Start a new game from the beginning"),
            new Text("New"),
        }.Frame(width: 600f, height: 5f));

        Assert.Equal(["New"], both.ShownText.Select(text => text.Text));
        Assert.Equal(["Start a new game from the beginning"], across.ShownText.Select(text => text.Text));
    }

    private static readonly string[] Thousand = Enumerable.Range(0, 1000).Select(i => $"item {i}").ToArray();

    // Every row the stack has asked for, so a test sees what it made and not only what shows.
    private static UiView Leaderboard(HashSet<string> made) => Corner(new ScrollView
    {
        new LazyVStack<string>(Thousand, item => { made.Add(item); return new Text(item); }, RowHeight: 20f),
    }.Frame(200f, 100f));

    [Fact]
    public void A_lazy_stack_makes_only_the_rows_near_the_screen()
    {
        var made = new HashSet<string>();
        using var run = PointerGame.Show(_ => Leaderboard(made));
        run.Ticks(2);

        Assert.Equal(["item 0", "item 1", "item 2", "item 3", "item 4"], run.ShownText.Select(text => text.Text));
        Assert.Contains("item 9", made);
        Assert.InRange(made.Count, 5, 40);
    }

    [Fact]
    public void A_lazy_stack_is_as_tall_as_every_row()
    {
        var tree = Show(Corner(new ScrollView
        {
            new LazyVStack<string>(Thousand, item => new Text(item), RowHeight: 20f, Spacing: 4f).Id("rows"),
        }.Frame(200f, 100f)));

        Assert.Equal(1000f * 24f - 4f, tree.FrameOf("rows").Height, Close);
    }

    [Fact]
    public void A_lazy_stack_makes_the_rows_it_scrolls_to()
    {
        var made = new HashSet<string>();
        using var run = PointerGame.Show(_ => Leaderboard(made));
        run.Input.MoveMouse(new Vector2(50f, 50f));
        run.Ticks(1);

        run.Input.Scroll(-10000f);
        run.Ticks(3);

        Assert.InRange(Find(run, "item 999").Frame.Y, 0f, 100f);
        Assert.DoesNotContain(run.ShownText, text => text.Text == "item 0");
        Assert.DoesNotContain("item 500", made);
        Assert.InRange(made.Count, 10, 80);
    }

    [Fact]
    public void A_lazy_stack_outside_a_scroll_view_makes_every_row()
    {
        var tree = Show(new LazyVStack<string>(Thousand[..50], item => new Text(item), RowHeight: 10f), height: 2000);
        tree.Update(new LazyVStack<string>(Thousand[..50], item => new Text(item), RowHeight: 10f), 800, 2000);

        Assert.Equal(50, tree.ShownText.Count);
    }

    [Fact]
    public void A_lazy_stack_needs_a_row_height()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Show(new LazyVStack<string>(Thousand, item => new Text(item), RowHeight: 0f)));

        Assert.Equal("RowHeight", error.ParamName);
        Assert.StartsWith("A lazy stack's rows need a height above zero.", error.Message);
    }

    private static ShownText Find(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text);
}
