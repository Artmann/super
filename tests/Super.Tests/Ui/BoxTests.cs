using System.Numerics;
using Rendition;
using Super.Ui;

namespace Super.Tests.Ui;

public class BoxTests
{
    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color Blue = new(0f, 0f, 1f);

    private static UiTree Show(UiView view, int width = 800, int height = 600)
    {
        var tree = new UiTree();
        tree.Update(view, width, height);
        return tree;
    }

    private static UiView Block(float width = 100f, float height = 40f) => new Spacer().Frame(width, height);

    private static (Vector2 Position, Vector2 Size) Only(UiTree tree)
    {
        var quad = Assert.Single(tree.Quads.ToArray());
        return (quad.Position, quad.Size);
    }

    private static (Vector2 Position, Vector2 Size) First(UiTree tree)
    {
        var quad = tree.Quads[0];
        return (quad.Position, quad.Size);
    }

    [Fact]
    public void Margin_and_background_give_the_same_box_in_either_order()
    {
        var first = Only(Show(Block().Margin(8f).Background(Red)));
        var second = Only(Show(Block().Background(Red).Margin(8f)));
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_margin_is_left_unfilled()
    {
        var (_, size) = Only(Show(Block().Background(Red).Margin(8f)));
        Assert.Equal(new Vector2(100f, 40f), size);
    }

    [Fact]
    public void A_margin_takes_up_room_in_a_stack()
    {
        var tree = Show(new VStack(Spacing: 0f)
        {
            new Text("a").Margin(10f),
            new Text("b"),
        });
        Assert.Equal(tree.ShownText[0].Frame.Y + tree.ShownText[0].Frame.Height + 10f, tree.ShownText[1].Frame.Y);
    }

    [Fact]
    public void Several_margins_in_one_run_add_up()
    {
        var one = new HStack(Spacing: 0f) { Block().Margin(20f).Background(Red) };
        var two = new HStack(Spacing: 0f) { Block().Margin(8f).Background(Red).Margin(12f) };
        Assert.Equal(Only(Show(one)), Only(Show(two)));
    }

    [Fact]
    public void The_last_background_in_a_run_wins()
    {
        var quad = Assert.Single(Show(Block().Background(Red).Background(Blue)).Quads.ToArray());
        Assert.Equal(new Vector4(Blue.R, Blue.G, Blue.B, Blue.A), quad.Color);
    }

    [Fact]
    public void Padding_ends_a_run()
    {
        Assert.Equal(new Vector2(124f, 64f), Only(Show(Block().Padding(12f).Background(Red))).Size);
        Assert.Equal(new Vector2(100f, 40f), Only(Show(Block().Background(Red).Padding(12f))).Size);
    }

    [Fact]
    public void Two_runs_are_two_boxes()
    {
        var tree = Show(Block().Background(Red).Padding(4f).Background(Blue));
        Assert.Equal(2, tree.Quads.Length);
    }

    [Fact]
    public void Padding_can_differ_per_side()
    {
        var view = new Text("a").Padding(top: 1f, trailing: 2f, bottom: 3f, leading: 4f).Background(Red);
        var tree = Show(view);
        var text = tree.ShownText[0].Frame;
        var (position, size) = First(tree);
        Assert.Equal(position.X + 4f, text.X);
        Assert.Equal(position.Y + 1f, text.Y);
        Assert.Equal(text.Width + 6f, size.X);
        Assert.Equal(text.Height + 4f, size.Y);
    }

    [Fact]
    public void A_margin_can_differ_per_side()
    {
        var one = Show(new HStack(VerticalAlignment.Top, 0f)
        {
            Block().Background(Red).Margin(top: 5f, trailing: 0f, bottom: 0f, leading: 7f),
        });
        var (position, _) = Only(one);
        var plain = Show(new HStack(VerticalAlignment.Top, 0f) { Block().Background(Red) });
        Assert.Equal(Only(plain).Position + new Vector2(3.5f, 2.5f), position);
    }

    [Fact]
    public void A_minimum_size_grows_a_small_view()
    {
        var (_, size) = First(Show(new HStack { new Text("a").Frame(minWidth: 120f, minHeight: 50f).Background(Red) }));
        Assert.Equal(new Vector2(120f, 50f), size);
    }

    [Fact]
    public void A_minimum_size_beats_a_small_offer()
    {
        var (_, size) = Only(Show(Block(10f, 10f).Frame(minWidth: 200f).Background(Red).Frame(width: 50f)));
        Assert.Equal(200f, size.X);
    }

    [Fact]
    public void A_minimum_leaves_a_larger_view_alone()
    {
        var (_, size) = Only(Show(new HStack { Block(300f, 10f).Frame(minWidth: 120f).Background(Red) }));
        Assert.Equal(300f, size.X);
    }

    [Fact]
    public void A_maximum_limits_what_a_view_asks_for()
    {
        var view = new HStack { Block(300f, 10f).Frame(maxWidth: 120f).Background(Red) };
        var (_, size) = Only(Show(new VStack { view }.FixedSize()));
        Assert.Equal(120f, size.X);
    }

    [Fact]
    public void A_box_with_nothing_to_draw_adds_no_quad()
    {
        Assert.Equal(0, Show(Block().Margin(4f)).Quads.Length);
    }
}
