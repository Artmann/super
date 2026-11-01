using System.Numerics;
using Rendition;
using Super.Ui;

namespace Super.Tests.Ui;

public class DrawingTests
{
    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color Blue = new(0f, 0f, 1f);

    private static OverlayQuad[] Show(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return tree.Quads.ToArray();
    }

    private static UiView Block(float width = 100f, float height = 100f) => new Spacer().Frame(width, height);

    [Fact]
    public void A_box_writes_its_radius_and_border_on_one_quad()
    {
        var quad = Assert.Single(Show(Block().Background(Red).CornerRadius(8f).Border(2f, Blue)));
        Assert.Equal(8f, quad.CornerRadius);
        Assert.Equal(2f, quad.BorderWidth);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), quad.BorderColor);
    }

    [Fact]
    public void A_border_draws_with_no_background()
    {
        var quad = Assert.Single(Show(Block().Border(1f, Blue)));
        Assert.Equal(0f, quad.Color.W);
        Assert.Equal(1f, quad.BorderWidth);
    }

    [Fact]
    public void A_corner_radius_clips_what_is_inside()
    {
        var quads = Show(Block().Background(Blue).CornerRadius(8f).Background(Red));
        var inner = Show(Block().Background(Blue).Padding(1f).CornerRadius(8f).Background(Red))[1];
        Assert.Single(quads);
        Assert.Equal(8f, inner.ClipRadius);
        Assert.Equal(new Vector4(349f, 249f, 102f, 102f), inner.Clip);
    }

    [Fact]
    public void A_plain_view_has_no_clip()
    {
        Assert.Equal(default, Assert.Single(Show(Block().Background(Red))).Clip);
    }

    [Fact]
    public void A_clip_ends_with_its_view()
    {
        var quads = Show(new VStack(Spacing: 0f)
        {
            Block().Background(Blue).Padding(1f).CornerRadius(8f),
            Block().Background(Red),
        });
        Assert.NotEqual(default, quads[0].Clip);
        Assert.Equal(default, quads[1].Clip);
    }

    [Fact]
    public void Nested_clips_intersect_and_keep_the_innermost_radius()
    {
        var view = Block(200f, 200f).Background(Blue)
            .Padding(0f).CornerRadius(4f).Frame(100f, 100f, alignment: Alignment.TopLeading)
            .Padding(0f).CornerRadius(10f).Background(Red);
        var inner = Show(view)[1];
        Assert.Equal(4f, inner.ClipRadius);
        Assert.Equal(new Vector4(350f, 250f, 100f, 100f), inner.Clip);
    }

    [Fact]
    public void An_inner_clip_with_no_radius_keeps_the_outer_one()
    {
        var clipped = new ScrollView { Block(100f, 100f).Background(Blue) }.Frame(50f, 50f);
        var inner = Show(clipped.Padding(0f).CornerRadius(10f))[0];
        Assert.Equal(10f, inner.ClipRadius);
    }

    [Fact]
    public void Opacity_multiplies_through_what_is_inside()
    {
        var quads = Show(Block().Background(Blue).Opacity(0.5f).Padding(1f).Background(Red).Opacity(0.5f));
        Assert.Equal(0.5f, quads[0].Color.W);
        Assert.Equal(0.25f, quads[1].Color.W);
    }

    [Fact]
    public void Opacity_reaches_the_border_and_text()
    {
        var quads = Show(new Text("a").Border(1f, Blue).Opacity(0.5f));
        Assert.Equal(0.5f, quads[0].BorderColor.W);
        Assert.Equal(0.5f, quads[1].Color.W);
    }

    [Fact]
    public void Nothing_outside_a_clip_is_added()
    {
        var content = new VStack(Spacing: 0f)
        {
            Block(100f, 40f).Background(Red),
            Block(100f, 40f).Background(Red),
            Block(100f, 40f).Background(Red),
            Block(100f, 40f).Background(Red),
        };
        var tree = new UiTree();
        tree.Update(new ScrollView { content }.Frame(100f, 60f), 800, 600);
        Assert.Equal(2, tree.Quads.Length);
    }

    [Fact]
    public void Text_outside_a_clip_is_not_shown()
    {
        var content = new VStack(Spacing: 0f)
        {
            new Text("seen").Frame(height: 40f),
            new Text("hidden").Frame(height: 40f),
        };
        var tree = new UiTree();
        tree.Update(new ScrollView { content }.Frame(100f, 30f), 800, 600);
        Assert.Contains(tree.ShownText, shown => shown.Text == "seen");
        Assert.DoesNotContain(tree.ShownText, shown => shown.Text == "hidden");
    }

#if DEBUG
    [Fact]
    public void Too_many_quads_throw_in_a_debug_build()
    {
        var stack = new VStack(Spacing: 0f);
        for (int i = 0; i <= OverlayQuad.MaxPerFrame; i++)
        {
            stack.Add(Block(1f, 1f).Background(Red));
        }

        var error = Assert.Throws<InvalidOperationException>(() => Show(stack));
        Assert.Contains("8192", error.Message);
    }
#endif
}
