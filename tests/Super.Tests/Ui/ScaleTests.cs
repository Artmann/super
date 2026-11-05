using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>An interface scale makes everything bigger on screen while layout keeps its own units.</summary>
public sealed class ScaleTests
{
    private const int AtlasSize = 1024;
    private static readonly Color Solid = new(0.2f, 0.2f, 0.2f);

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiTree Show(UiView view, float scale, int width = 800, int height = 600)
    {
        var tree = new UiTree { Scale = scale };
        tree.Update(view, width, height);
        return tree;
    }

    [Fact]
    public void A_box_at_scale_2_covers_twice_the_pixels_but_keeps_its_frame()
    {
        var tree = Show(Corner(new Text("").Frame(100f, 40f).Padding(10f).Background(Solid).Id("box")), 2f);

        var quad = tree.Quads[0];
        Assert.Equal(Vector2.Zero, quad.Position);
        Assert.Equal(new Vector2(240f, 120f), quad.Size);
        Assert.Equal(new UiRect(0f, 0f, 120f, 60f), tree.FrameOf("box"));
    }

    [Fact]
    public void Corners_and_borders_grow_with_the_scale()
    {
        var tree = Show(Corner(new Text("").Frame(100f, 40f).Background(Solid).CornerRadius(6f).Border(2f, Color.White)), 1.5f);

        Assert.Equal(9f, tree.Quads[0].CornerRadius);
        Assert.Equal(3f, tree.Quads[0].BorderWidth);
    }

    [Fact]
    public void The_interface_lays_itself_out_in_the_screen_divided_by_the_scale()
    {
        var filling = new Text("").Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity).Id("all");

        Assert.Equal(new UiRect(0f, 0f, 400f, 300f), Show(filling, 2f).FrameOf("all"));
    }

    [Fact]
    public void Text_is_drawn_at_its_size_in_pixels_so_each_character_is_one_texel_a_pixel()
    {
        var tree = Show(Corner(new Text("Wood 4").FontSize(14f)), 2f);

        Assert.NotEmpty(tree.Quads.ToArray());
        foreach (var quad in tree.Quads)
        {
            Assert.Equal((quad.UvMax - quad.UvMin) * AtlasSize, quad.Size);
        }
    }

    [Fact]
    public void Text_at_scale_2_is_twice_as_tall_in_pixels()
    {
        float Tallest(UiTree tree) => tree.Quads.ToArray().Max(quad => quad.Size.Y);
        var text = Corner(new Text("Hello").FontSize(14f));

        float small = Tallest(Show(text, 1f));
        float large = Tallest(Show(text, 2f));

        Assert.InRange(large, small * 2f - 1f, small * 2f + 1f);
    }

    [Fact]
    public void Text_measures_about_the_same_in_interface_units_at_any_scale()
    {
        var text = Corner(new Text("Stone 6, Wood 4").Id("text"));

        var plain = Show(text, 1f).FrameOf("text");
        var scaled = Show(text, 3f).FrameOf("text");

        Assert.InRange(scaled.Width, plain.Width - 1f, plain.Width + 1f);
        Assert.InRange(scaled.Height, plain.Height - 1f, plain.Height + 1f);
    }

    [Fact]
    public void Text_wraps_at_the_same_words_at_any_scale()
    {
        var text = Corner(new Text("Gather wood and stone to build a cabin before night falls").Frame(width: 120f).Id("text"));

        Assert.Equal(Show(text, 1f).FrameOf("text").Height, Show(text, 2f).FrameOf("text").Height, 0.5f);
    }

    [Fact]
    public void Changing_the_scale_lays_the_interface_out_again()
    {
        var tree = Show(Corner(new Text("").Frame(50f, 50f).Background(Solid)), 1f);

        tree.Scale = 2f;
        tree.Update(Corner(new Text("").Frame(50f, 50f).Background(Solid)), 800, 600);

        Assert.Equal(new Vector2(100f, 100f), tree.Quads[0].Size);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_scale_must_be_a_number_above_zero(float scale)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new UiTree { Scale = scale });

        Assert.StartsWith("The interface scale must be a number above zero.", error.Message);
    }

    [Fact]
    public void The_mouse_in_pixels_finds_the_view_drawn_there()
    {
        using var run = HeadlessGame.Create<PointerGame>(1);
        run.UiScale = 2f;
        run.Game.Screen = game => Corner(new Text("").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("click")));
        run.Ticks(1);

        // 150 pixels is 75 units in, inside the 100 unit box drawn 200 pixels wide.
        run.Input.Click(MouseButton.Left, new Vector2(150f, 150f));
        run.Ticks(3);

        Assert.Equal(["click"], run.Game.Log);
    }

    [Fact]
    public void Clicking_a_view_by_id_aims_at_where_it_is_drawn()
    {
        using var run = HeadlessGame.Create<PointerGame>(1);
        run.UiScale = 2f;
        run.Game.Screen = game => Corner(
            new Text("").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("click")).Id("far").Padding(0f, 0f, 0f, 300f));
        run.Ticks(1);

        run.Input.Click("far");
        run.Ticks(3);

        Assert.Equal(["click"], run.Game.Log);
    }
}
