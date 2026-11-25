using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Typefaces a game cooks and puts on its text in place of the built-in one.</summary>
public sealed class FontTests
{
    // Inter's bold file stands in for a game's font: it's a real font, and wider than the regular weight.
    private static readonly byte[] Heavy = Embedded("Inter-Bold.ttf");

    private static byte[] Embedded(string name)
    {
        using var stream = typeof(UiTree).Assembly.GetManifestResourceStream(name)!;
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Content ContentWith(params (string Name, byte[] Bytes)[] files)
    {
        var memory = new MemoryContent();
        foreach (var (name, bytes) in files)
        {
            memory.Add(name, bytes);
        }

        return new Content(memory);
    }

    private static ShownText Find(UiView view, string text)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return Assert.Single(tree.ShownText, shown => shown.Text == text);
    }

    [Fact]
    public void Naming_a_font_again_gives_the_same_one()
    {
        var content = ContentWith(("fonts/heavy.ttf", Heavy));

        var font = content.Fonts.Font("fonts/heavy.ttf");

        Assert.Same(font, content.Fonts.Font("fonts/heavy.ttf"));
        Assert.Equal("fonts/heavy.ttf", font.Path);
        Assert.Null(font.BoldPath);
    }

    [Fact]
    public void Text_in_a_font_is_drawn_in_it()
    {
        var font = ContentWith(("fonts/heavy.ttf", Heavy)).Fonts.Font("fonts/heavy.ttf");

        var plain = Find(new Text("Drowned Bell"), "Drowned Bell");
        var shown = Find(new Text("Drowned Bell").Font(font), "Drowned Bell");

        Assert.Null(plain.Font);
        Assert.Same(font, shown.Font);
        Assert.True(shown.Frame.Width > plain.Frame.Width, $"{shown.Frame.Width} should be wider than {plain.Frame.Width}");
    }

    [Fact]
    public void A_font_reaches_the_text_inside_and_an_inner_one_wins()
    {
        var content = ContentWith(("fonts/heavy.ttf", Heavy), ("fonts/other.ttf", Heavy));
        var heavy = content.Fonts.Font("fonts/heavy.ttf");
        var other = content.Fonts.Font("fonts/other.ttf");

        var view = new VStack { new Text("outer"), new Text("inner").Font(other) }.Font(heavy);

        Assert.Same(heavy, Find(view, "outer").Font);
        Assert.Same(other, Find(view, "inner").Font);
    }

    [Fact]
    public void Bold_text_in_a_font_without_a_bold_weight_is_its_regular_weight()
    {
        var font = ContentWith(("fonts/heavy.ttf", Heavy)).Fonts.Font("fonts/heavy.ttf");

        var regular = Find(new Text("Sabatons").Font(font), "Sabatons");
        var bold = Find(new Text("Sabatons").Font(font).Bold(), "Sabatons");

        Assert.Equal(regular.Frame.Width, bold.Frame.Width);
        Assert.True(bold.Bold);
    }

    [Fact]
    public void Bold_text_in_a_font_with_a_bold_weight_is_drawn_in_that()
    {
        var content = ContentWith(("fonts/light.ttf", Embedded("Inter-Regular.ttf")), ("fonts/heavy.ttf", Heavy));
        var font = content.Fonts.Font("fonts/light.ttf", bold: "fonts/heavy.ttf");

        var regular = Find(new Text("Sabatons").Font(font), "Sabatons");
        var bold = Find(new Text("Sabatons").Font(font).Bold(), "Sabatons");

        Assert.Equal("fonts/heavy.ttf", font.BoldPath);
        Assert.True(bold.Frame.Width > regular.Frame.Width);
    }

    [Fact]
    public void A_name_that_isnt_cooked_stops_naming_it()
    {
        var content = ContentWith(("fonts/heavy.ttf", Heavy));

        var problem = Assert.Throws<ContentException>(() => content.Fonts.Font("fonts/Heavy.ttf"));

        Assert.Contains("fonts/Heavy.ttf", problem.Message);
    }

    [Fact]
    public void Data_that_isnt_a_font_stops_naming_it()
    {
        var memory = new MemoryContent();
        memory["fonts/notes.json"] = "{}";

        var problem = Assert.Throws<ContentException>(() => new Content(memory).Fonts.Font("fonts/notes.json"));

        Assert.Contains("fonts/notes.json isn't a font", problem.Message);
    }
}
