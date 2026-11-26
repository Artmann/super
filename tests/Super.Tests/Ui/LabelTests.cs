using System.Numerics;
using Rendition;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The small pieces that sit among text: dividers, badges, labels with icons, and key hints.</summary>
public sealed class LabelTests
{
    private static UiTree Show(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return tree;
    }

    private static UiView Block(float width, float height) => new Spacer().Frame(width, height);

    /// <summary>The colour a quad gets when filled with this colour, whichever way the draw list stores it.</summary>
    private static Vector4 Drawn(Color color) => Assert.Single(Show(Block(10f, 10f).Background(color)).Quads.ToArray()).Color;

    [Fact]
    public void A_divider_in_a_vertical_stack_runs_across_it()
    {
        // It takes all the width it's offered, as SwiftUI's does, so a stack sized by its content keeps it in.
        var tree = Show(new VStack(Spacing: 0f) { Block(200f, 20f), new Divider().Id("d"), Block(100f, 20f) }.FixedSize());

        var frame = tree.FrameOf("d");

        Assert.Equal(200f, frame.Width);
        Assert.Equal(1f, frame.Height);
    }

    [Fact]
    public void A_divider_in_a_horizontal_stack_stands_upright()
    {
        var tree = Show(new HStack(Spacing: 0f) { Block(20f, 30f), new Divider().Id("d"), Block(20f, 10f) }.FixedSize());

        var frame = tree.FrameOf("d");

        Assert.Equal(1f, frame.Width);
        Assert.Equal(30f, frame.Height);
    }

    [Fact]
    public void A_divider_takes_the_themes_border_colour()
    {
        var dark = Assert.Single(Show(new VStack { new Divider() }.Frame(100f, 10f)).Quads.ToArray());
        var light = Assert.Single(Show(new VStack { new Divider() }.Frame(100f, 10f).Theme(Theme.Light)).Quads.ToArray());

        Assert.Equal(Drawn(Theme.Dark.Colors.Border), dark.Color);
        Assert.Equal(Drawn(Theme.Light.Colors.Border), light.Color);
    }

    [Fact]
    public void Each_badge_style_takes_its_fill_and_text_colour_from_the_theme()
    {
        // Every colour its own, as the built-in themes share some greys.
        var theme = Theme.Dark with
        {
            Colors = Theme.Dark.Colors with
            {
                Primary = Color.Hex("#110000"),
                PrimaryForeground = Color.Hex("#220000"),
                Secondary = Color.Hex("#001100"),
                SecondaryForeground = Color.Hex("#002200"),
                Destructive = Color.Hex("#000011"),
                DestructiveForeground = Color.Hex("#000022"),
            },
        };
        (BadgeStyle Style, Color Fill, Color Text)[] styles =
        [
            (BadgeStyle.Default, theme.Colors.Primary, theme.Colors.PrimaryForeground),
            (BadgeStyle.Secondary, theme.Colors.Secondary, theme.Colors.SecondaryForeground),
            (BadgeStyle.Destructive, theme.Colors.Destructive, theme.Colors.DestructiveForeground),
        ];

        foreach (var (style, fill, text) in styles)
        {
            var tree = Show(new Badge("New", style).Theme(theme));

            Assert.Equal(Drawn(fill), tree.Quads[0].Color);
            Assert.Equal(0f, tree.Quads[0].BorderWidth);
            Assert.Equal(text, Assert.Single(tree.ShownText).Color);
        }
    }

    [Fact]
    public void An_outline_badge_is_only_an_edge()
    {
        var tree = Show(new Badge("Beta", BadgeStyle.Outline));
        var box = tree.Quads[0];

        Assert.Equal(0f, box.Color.W);
        Assert.Equal(1f, box.BorderWidth);
        Assert.Equal(Drawn(Theme.Dark.Colors.Border), box.BorderColor);
        Assert.Equal(Theme.Dark.Colors.Foreground, Assert.Single(tree.ShownText).Color);
    }

    [Fact]
    public void A_badge_is_small_text_with_room_around_it()
    {
        var tree = Show(new Badge("New").Id("b"));
        var text = Assert.Single(tree.ShownText);
        var frame = tree.FrameOf("b");

        Assert.Equal(12f, text.Size);
        Assert.Equal(FontWeight.SemiBold, text.Weight);
        Assert.Equal(text.Frame.Width + 16f, frame.Width, 3);
        Assert.Equal(text.Frame.Height + 4f, frame.Height, 3);
    }

    [Fact]
    public void A_label_puts_its_icon_before_its_text_half_the_text_size_apart()
    {
        var tree = Show(new Label("Search", IconName.Search).FontSize(20f).Id("l"));
        var text = Assert.Single(tree.ShownText);
        float iconRight = tree.Quads.ToArray()
            .Where(quad => quad.Kind != OverlayKind.Box)
            .Max(quad => quad.Position.X + quad.Size.X);

        // The icon is 23 wide at 20 pixels, and the text starts 10 after its box.
        Assert.Equal(20f, text.Size);
        Assert.True(iconRight <= text.Frame.X, $"The icon ends at {iconRight}, after the text starts at {text.Frame.X}.");
        Assert.Equal(23f + 10f, text.Frame.X - tree.FrameOf("l").X, 3);
    }

    [Fact]
    public void A_labels_icon_takes_the_labels_colour()
    {
        var red = new Color(1f, 0f, 0f);
        var tree = Show(new Label("Close", IconName.Close).Foreground(red));

        Assert.All(tree.Quads.ToArray(), quad => Assert.Equal(new Vector4(1f, 0f, 0f, 1f), quad.Color));
        Assert.Equal(red, Assert.Single(tree.ShownText).Color);
    }

    [Fact]
    public void A_key_hint_is_at_least_square_and_grows_for_a_long_key()
    {
        var single = Show(new KeyHint("E").Id("k")).FrameOf("k");
        var wide = Show(new KeyHint("Shift").Id("k")).FrameOf("k");

        // At 14 pixel text the key's text is 12, and its cap is 20 square.
        Assert.Equal(new UiRect(single.X, single.Y, 20f, 20f), single);
        Assert.Equal(20f, wide.Height);
        Assert.True(wide.Width > 20f, $"Shift's cap is only {wide.Width} wide.");
    }
}
