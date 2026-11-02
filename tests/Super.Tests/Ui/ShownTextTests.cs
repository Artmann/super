using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>What a test can read about the text on screen besides its words and place.</summary>
public sealed class ShownTextTests
{
    private static readonly Color Warm = Color.Hex("#E0896B");

    private static ShownText Find(UiView view, string text)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return Assert.Single(tree.ShownText, shown => shown.Text == text);
    }

    [Fact]
    public void Text_is_white_14_pixel_and_regular_unless_styled()
    {
        var shown = Find(new Text("plain"), "plain");

        Assert.Equal(Color.White, shown.Color);
        Assert.Equal(14f, shown.Size);
        Assert.False(shown.Bold);
    }

    [Fact]
    public void Shown_text_has_its_own_colour_size_and_weight()
    {
        var shown = Find(new Text("short").Foreground(Warm).FontSize(12f).Bold(), "short");

        Assert.Equal(Warm, shown.Color);
        Assert.Equal(12f, shown.Size);
        Assert.True(shown.Bold);
    }

    [Fact]
    public void Shown_text_has_the_colour_it_inherits()
    {
        var view = new HStack { new Text("Wood 4"), new Text("Stone 6").Foreground(Color.White) }.Foreground(Warm);

        Assert.Equal(Warm, Find(view, "Wood 4").Color);
        Assert.Equal(Color.White, Find(view, "Stone 6").Color);
    }

    [Fact]
    public void A_faded_view_fades_the_colour_of_its_text()
    {
        var shown = Find(new VStack { new Text("faint").Foreground(Warm with { A = 0.8f }) }.Opacity(0.5f), "faint");

        Assert.Equal(Warm with { A = 0.4f }, shown.Color);
    }
}
