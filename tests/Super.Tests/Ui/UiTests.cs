using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

public sealed class UiTests
{
    private const float Close = 0.01f;

    private static ShownText Find(UiTree tree, string text) => tree.ShownText.Single(shown => shown.Text == text);

    private static UiTree Show(UiView view, int width = 800, int height = 600)
    {
        var tree = new UiTree();
        tree.Update(view, width, height);
        return tree;
    }

    [Fact]
    public void Text_is_measured_with_the_real_font()
    {
        var tree = Show(new VStack { new Text("i"), new Text("WWWW") });

        Assert.True(Find(tree, "WWWW").Frame.Width > Find(tree, "i").Frame.Width * 10f);
        Assert.InRange(Find(tree, "i").Frame.Height, 14f, 22f);
    }

    [Fact]
    public void Bigger_text_takes_more_room()
    {
        var tree = Show(new VStack { new Text("small"), new Text("big").FontSize(40f) });

        Assert.True(Find(tree, "big").Frame.Height > Find(tree, "small").Frame.Height * 2f);
    }

    [Fact]
    public void Text_wraps_when_offered_too_little_width()
    {
        var wide = Find(Show(new Text("several words of text")), "several words of text").Frame;
        var narrow = Find(Show(new Text("several words of text").Frame(width: wide.Width / 2f)), "several words of text").Frame;

        Assert.True(narrow.Height >= wide.Height * 2f);
        Assert.True(narrow.Width < wide.Width);
    }

    [Fact]
    public void A_vstack_stacks_downwards_with_spacing()
    {
        var tree = Show(new VStack(Spacing: 10f) { new Text("a"), new Text("b") });
        var a = Find(tree, "a").Frame;
        var b = Find(tree, "b").Frame;

        Assert.Equal(a.Y + a.Height + 10f, b.Y, Close);
    }

    [Fact]
    public void A_leading_vstack_lines_up_on_the_left()
    {
        var tree = Show(new VStack(HorizontalAlignment.Leading) { new Text("a"), new Text("a much longer line") });

        Assert.Equal(Find(tree, "a").Frame.X, Find(tree, "a much longer line").Frame.X, Close);
    }

    [Fact]
    public void A_spacer_pushes_its_neighbors_to_the_edges()
    {
        var tree = Show(new HStack { new Text("left"), new Spacer(), new Text("right") }.Frame(width: 400f));
        var left = Find(tree, "left").Frame;
        var right = Find(tree, "right").Frame;

        Assert.Equal(400f, right.X + right.Width - left.X, Close);
    }

    [Fact]
    public void Spacers_share_the_space_equally()
    {
        var tree = Show(new HStack(Spacing: 0f) { new Spacer(), new Text("middle"), new Spacer() }.Frame(width: 400f));
        var middle = Find(tree, "middle").Frame;

        Assert.Equal(400f, middle.X + middle.Width / 2f, 1f); // The screen's center, as the frame is centered too.
    }

    [Fact]
    public void A_spacer_doesnt_squeeze_text_into_wrapping()
    {
        var plain = Find(Show(new Text("several words of text")), "several words of text").Frame;
        var tree = Show(new VStack
        {
            new HStack { new Text("label"), new Spacer(), new Text("several words of text") },
        }.FixedSize());

        Assert.Equal(plain.Height, Find(tree, "several words of text").Frame.Height, Close);
    }

    [Fact]
    public void Showing_nothing_draws_nothing()
    {
        var tree = new UiTree();
        tree.Update(new Text("hello").Background(Color.Black), 800, 600);
        Assert.False(tree.Quads.IsEmpty);
        Assert.NotEmpty(tree.ShownText);

        tree.Update(null, 800, 600);

        Assert.True(tree.Quads.IsEmpty);
        Assert.Empty(tree.ShownText);
    }

    [Fact]
    public void A_spacer_doesnt_widen_a_vstack()
    {
        var tree = Show(new VStack { new Text("a"), new Spacer() }.Background(Color.Black).FixedSize());

        Assert.Equal(Find(tree, "a").Frame.Width, tree.Quads[0].Size.X, Close);
    }

    [Fact]
    public void A_stretching_child_takes_what_the_fixed_child_leaves()
    {
        var tree = Show(new HStack(Spacing: 0f)
        {
            new Text("fixed"),
            new Text("grow").Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Trailing),
        }.Frame(width: 300f));

        var grow = Find(tree, "grow").Frame;
        var fixedText = Find(tree, "fixed").Frame;
        Assert.Equal(300f, grow.X + grow.Width - fixedText.X, Close);
    }

    [Fact]
    public void A_full_frame_pins_a_view_to_a_corner()
    {
        var tree = Show(new Text("corner")
            .Padding(10f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.BottomTrailing));
        var corner = Find(tree, "corner").Frame;

        Assert.Equal(790f, corner.X + corner.Width, Close);
        Assert.Equal(590f, corner.Y + corner.Height, Close);
    }

    [Fact]
    public void Padding_goes_around_a_background_or_inside_it_depending_on_order()
    {
        var inside = Show(new Text("a").Padding(10f).Background(Color.Black));
        var outside = Show(new Text("a").Background(Color.Black).Padding(10f));

        Assert.Equal(outside.Quads[0].Size.X + 20f, inside.Quads[0].Size.X, Close);
    }

    [Fact]
    public void A_zstack_puts_children_in_the_same_place()
    {
        var tree = Show(new ZStack(Alignment.TopLeading) { new Text("back"), new Text("front") });

        Assert.Equal(Find(tree, "back").Frame.X, Find(tree, "front").Frame.X, Close);
        Assert.Equal(Find(tree, "back").Frame.Y, Find(tree, "front").Frame.Y, Close);
    }

    [Fact]
    public void Styles_reach_everything_inside()
    {
        var plain = new UiTree();
        plain.Update(new VStack { new Text("a") }, 800, 600);
        var styled = new UiTree();
        styled.Update(new VStack { new Text("a") }.FontSize(30f).Foreground(Color.Hex("#FF0000")), 800, 600);

        Assert.True(Find(styled, "a").Frame.Height > Find(plain, "a").Frame.Height);
        Assert.Equal(1f, styled.Quads[0].Color.X);
        Assert.Equal(0f, styled.Quads[0].Color.Y);
    }

    [Fact]
    public void Monospaced_digits_keep_numbers_the_same_width()
    {
        var one = Find(Show(new Text("111").MonospacedDigit()), "111").Frame;
        var eight = Find(Show(new Text("888").MonospacedDigit()), "888").Frame;

        Assert.Equal(one.Width, eight.Width, Close);
    }

    [Fact]
    public void An_unchanged_component_doesnt_run_again()
    {
        var log = new RunLog();
        var tree = new UiTree();
        tree.Update(new Counted(log, 1), 800, 600);
        tree.Update(new Counted(log, 1), 800, 600);
        Assert.Equal(1, log.Runs);

        tree.Update(new Counted(log, 2), 800, 600);
        Assert.Equal(2, log.Runs);
        Assert.Equal("2", Assert.Single(tree.ShownText).Text);
    }

    [Fact]
    public void Equal_stacks_count_as_unchanged()
    {
        UiView stack = new VStack { new Text("a") };

        Assert.True(stack.Equals(new VStack { new Text("a") }));
        Assert.False(stack.Equals(new VStack { new Text("b") }));
        Assert.False(stack.Equals(new HStack { new Text("a") }));
    }

    [Fact]
    public void A_child_stays_put_when_a_parent_runs_again()
    {
        var log = new RunLog();
        var tree = new UiTree();
        tree.Update(new Parent("first", log), 800, 600);
        tree.Update(new Parent("second", log), 800, 600);

        Assert.Equal(1, log.Runs);
        Assert.Contains(tree.ShownText, shown => shown.Text == "second");
    }

    [Fact]
    public void State_survives_a_new_description()
    {
        var holder = new Holder();
        var tree = new UiTree();
        tree.Update(new Stateful(holder, 1), 800, 600);
        holder.State!.Value = 7;
        tree.Update(new Stateful(holder, 2), 800, 600);

        Assert.Equal("2:7", Assert.Single(tree.ShownText).Text);
    }

    [Fact]
    public void Changing_state_only_runs_the_components_that_read_it()
    {
        var holder = new Holder();
        var log = new RunLog();
        var tree = new UiTree();
        var view = new VStack { new Stateful(holder, 1), new Counted(log, 1) };
        tree.Update(view, 800, 600);

        holder.State!.Value = 5;
        tree.Update(new VStack { new Stateful(holder, 1), new Counted(log, 1) }, 800, 600);

        Assert.Contains(tree.ShownText, shown => shown.Text == "1:5");
        Assert.Equal(1, log.Runs);
    }

    [Fact]
    public void A_state_passed_down_wakes_the_child_that_reads_it()
    {
        var holder = new Holder();
        var log = new RunLog();
        var tree = new UiTree();
        tree.Update(new Owner(holder, log), 800, 600);
        holder.State!.Value = 3;
        tree.Update(new Owner(holder, log), 800, 600);

        Assert.Equal("3", Assert.Single(tree.ShownText).Text);
        Assert.Equal(1, log.Runs);
    }

    [Fact]
    public void A_different_type_in_the_same_place_starts_over()
    {
        var holder = new Holder();
        var tree = new UiTree();
        tree.Update(new VStack { new Stateful(holder, 1) }, 800, 600);
        var first = holder.State!;
        first.Value = 9;

        tree.Update(new VStack { new Text("swap") }, 800, 600);
        tree.Update(new VStack { new Stateful(holder, 1) }, 800, 600);

        Assert.NotSame(first, holder.State);
        Assert.Equal(0, holder.State!.Value);
    }

    [Fact]
    public void An_empty_spot_keeps_the_state_of_what_follows()
    {
        var holder = new Holder();
        var tree = new UiTree();
        tree.Update(new VStack { (UiView?)new Text("banner"), new Stateful(holder, 1) }, 800, 600);
        holder.State!.Value = 4;
        tree.Update(new VStack { (UiView?)null, new Stateful(holder, 1) }, 800, 600);

        Assert.Equal("1:4", Assert.Single(tree.ShownText).Text);
    }

    [Fact]
    public void An_id_keeps_state_when_rows_trade_places()
    {
        var a = new Holder();
        var b = new Holder();
        var tree = new UiTree();
        tree.Update(new VStack { new Stateful(a, 1).Id("a"), new Stateful(b, 2).Id("b") }, 800, 600);
        a.State!.Value = 100;
        b.State!.Value = 200;

        tree.Update(new VStack { new Stateful(b, 2).Id("b"), new Stateful(a, 1).Id("a") }, 800, 600);

        Assert.Equal(["2:200", "1:100"], tree.ShownText.Select(shown => shown.Text));
    }

    [Fact]
    public void A_removed_view_stops_listening()
    {
        var holder = new Holder();
        var tree = new UiTree();
        tree.Update(new Stateful(holder, 1), 800, 600);
        var state = holder.State!;
        Assert.Equal(1, state.ReaderCount);

        tree.Update(null, 800, 600);

        Assert.Equal(0, state.ReaderCount);
    }

    [Fact]
    public void Calling_usestate_out_of_order_says_what_went_wrong()
    {
        var tree = new UiTree();
        var flip = new Flip();
        tree.Update(new OutOfOrder(flip), 800, 600);
        flip.Value = true;

        var problem = Assert.Throws<InvalidOperationException>(() => tree.Update(new OutOfOrder(flip) { Identity = null, Version = 2 }, 800, 600));

        Assert.Contains("UseState", problem.Message);
    }

    [Fact]
    public void Resizing_lays_out_again()
    {
        var tree = new UiTree();
        var view = new Text("a").Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Trailing);
        tree.Update(view, 800, 600);
        tree.Update(view, 400, 600);

        var frame = Find(tree, "a").Frame;
        Assert.Equal(400f, frame.X + frame.Width, Close);
    }

    [Fact]
    public void A_game_shows_its_interface_in_a_headless_run()
    {
        using var run = HeadlessGame.Create<GameWithUi>(seed: 1);
        run.Ticks(3);

        Assert.Equal("tick 3", Assert.Single(run.ShownText).Text);
    }

    [Fact]
    public void A_game_without_an_interface_shows_nothing()
    {
        using var run = HeadlessGame.Create<GameWithoutUi>(seed: 1);
        run.Ticks(3);

        Assert.Empty(run.ShownText);
    }

    private sealed class RunLog
    {
        public int Runs { get; set; }
    }

    private sealed class Holder
    {
        public State<int>? State { get; set; }
    }

    private sealed class Flip
    {
        public bool Value { get; set; }
    }

    private sealed record Counted(RunLog Log, int Value) : UiView
    {
        public override UiView Body
        {
            get
            {
                Log.Runs++;
                return new Text($"{Value}");
            }
        }
    }

    private sealed record Parent(string Title, RunLog Log) : UiView
    {
        public override UiView Body => new VStack { new Text(Title), new Counted(Log, 1) };
    }

    private sealed record Stateful(Holder Holder, int Label) : UiView
    {
        public override UiView Body
        {
            get
            {
                var state = UseState(0);
                Holder.State = state;
                return new Text($"{Label}:{state.Value}");
            }
        }
    }

    private sealed record Owner(Holder Holder, RunLog Log) : UiView
    {
        public override UiView Body
        {
            get
            {
                Log.Runs++;
                var state = UseState(0);
                Holder.State = state;
                return new Reader(state);
            }
        }
    }

    private sealed record Reader(State<int> Value) : UiView
    {
        public override UiView Body => new Text($"{Value.Value}");
    }

    private sealed record OutOfOrder(Flip Flip) : UiView
    {
        public int Version { get; init; }

        public override UiView Body
        {
            get
            {
                if (Flip.Value)
                {
                    UseState("text");
                }
                else
                {
                    UseState(0);
                }

                return new Text("x");
            }
        }
    }

    private sealed class GameWithUi(GameTime time) : IGame
    {
        public UiView? Ui => new Text($"tick {time.Tick}");

        public void Update(GameTime gameTime)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private sealed class GameWithoutUi : IGame
    {
        public void Update(GameTime gameTime)
        {
        }

        public void Draw(View view)
        {
        }
    }
}
