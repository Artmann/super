using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Line limits: text that keeps to a number of lines and shows where it was cut.</summary>
public sealed class LineLimitTests
{
    private const string Sentence = "several words of text";

    private static UiTree Show(UiView view, int width = 800, int height = 600)
    {
        var tree = new UiTree();
        tree.Update(view, width, height);
        return tree;
    }

    private static ShownText Only(UiTree tree) => Assert.Single(tree.ShownText);

    private static float LineHeight => Only(Show(new Text("x"))).Frame.Height;

    private static float WidthOf(string text) => Only(Show(new Text(text))).Frame.Width;

    [Fact]
    public void Without_a_limit_text_squeezed_in_a_row_wraps_between_a_label_and_its_number()
    {
        var tree = Show(new HStack(Spacing: 8f) { new Text("Wood 16"), new Text("Stone 6"), new Text("Thatch 8") }
            .Frame(width: WidthOf("Wood 16") * 2.5f));

        Assert.Contains(tree.ShownText, shown => shown.Frame.Height >= LineHeight * 2f);
    }

    [Fact]
    public void A_row_with_a_line_limit_of_one_keeps_every_label_on_one_line()
    {
        var tree = Show(new HStack(Spacing: 8f) { new Text("Wood 16"), new Text("Stone 6"), new Text("Thatch 8") }
            .LineLimit(1)
            .Frame(width: WidthOf("Wood 16") * 2.5f));

        Assert.Equal(3, tree.ShownText.Count);
        Assert.All(tree.ShownText, shown => Assert.Equal(LineHeight, shown.Frame.Height, 0.01f));
        Assert.Contains(tree.ShownText, shown => shown.Text.EndsWith('…'));
    }

    [Fact]
    public void Text_cut_by_a_line_limit_ends_in_an_ellipsis_and_fits_its_width()
    {
        float width = WidthOf(Sentence) / 2f;

        var shown = Only(Show(new Text(Sentence).LineLimit(1).Frame(width: width)));

        Assert.EndsWith("…", shown.Text);
        Assert.StartsWith("several", shown.Text);
        Assert.True(shown.Text.Length < Sentence.Length);
        Assert.True(shown.Frame.Width <= width, $"{shown.Frame.Width} is wider than {width}.");
        Assert.Equal(LineHeight, shown.Frame.Height, 0.01f);
    }

    [Fact]
    public void A_cut_leaves_no_space_before_the_ellipsis()
    {
        // Just wide enough for "several " and an ellipsis, but not for "several w".
        float width = WidthOf("several …") + 0.5f;

        var shown = Only(Show(new Text(Sentence).LineLimit(1).Frame(width: width)));

        Assert.Equal("several…", shown.Text);
    }

    [Fact]
    public void A_limit_of_two_wraps_once_and_cuts_the_second_line()
    {
        float width = WidthOf("several words");

        var shown = Only(Show(new Text("several words of text that goes on and on").LineLimit(2).Frame(width: width)));

        Assert.Equal(LineHeight * 2f, shown.Frame.Height, 0.01f);
        Assert.StartsWith("several words of", shown.Text);
        Assert.EndsWith("…", shown.Text);
    }

    [Fact]
    public void Text_that_fits_its_limit_is_left_whole()
    {
        var shown = Only(Show(new Text(Sentence).LineLimit(1)));

        Assert.Equal(Sentence, shown.Text);
        Assert.Equal(WidthOf(Sentence), shown.Frame.Width, 0.01f);
    }

    [Fact]
    public void Lines_past_the_limit_from_newlines_are_cut()
    {
        var shown = Only(Show(new Text("first\nsecond").LineLimit(1)));

        Assert.Equal("first…", shown.Text);
        Assert.Equal(LineHeight, shown.Frame.Height, 0.01f);
    }

    [Fact]
    public void One_word_too_long_for_its_line_is_cut()
    {
        float width = WidthOf("Thatchwork") / 2f;

        var shown = Only(Show(new Text("Thatchwork").LineLimit(1).Frame(width: width)));

        Assert.EndsWith("…", shown.Text);
        Assert.True(shown.Frame.Width <= width, $"{shown.Frame.Width} is wider than {width}.");
    }

    [Fact]
    public void The_cut_text_is_what_gets_drawn()
    {
        var tree = Show(new Text(Sentence).LineLimit(1).Frame(width: WidthOf(Sentence) / 2f));

        int visible = Only(tree).Text.Count(character => !char.IsWhiteSpace(character));
        Assert.Equal(visible, tree.Quads.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_line_limit_below_one_is_explained(int lines)
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => new Text("x").LineLimit(lines));

        Assert.StartsWith("A line limit must be at least 1.", problem.Message);
    }
}
