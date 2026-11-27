using Super.Ui;

namespace StorybookExample;

/// <summary>
/// One variation in a story: a heading, a line on what it shows, and the views themselves on a card.
/// It reads the theme, so it follows the switch.
/// </summary>
public sealed record Specimen(string Title, string Note, UiView Content) : UiView
{
    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            return new VStack(HorizontalAlignment.Leading, 6f)
            {
                new Text(Title).TextStyle(TextStyle.Headline),
                new Text(Note).TextStyle(TextStyle.Callout).Foreground(Tone.Muted),
                Content
                    .Padding(24f)
                    .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading)
                    .Background(theme.Colors.Surface)
                    .Border(1f, theme.Colors.Border)
                    .CornerRadius(theme.RadiusLarge)
                    .Margin(6f, 0f, 0f, 0f),
            };
        }
    }
}

/// <summary>Ways to lay out the specimens of a story.</summary>
internal static class Layout
{
    /// <summary>The specimens of one story, one under the other. A null keeps its spot empty.</summary>
    public static UiView Page(params UiView?[] specimens)
    {
        var page = new VStack(HorizontalAlignment.Leading, 32f);
        foreach (var specimen in specimens)
        {
            page.Add(specimen);
        }

        return page;
    }

    /// <summary>Views side by side, lined up along their middles.</summary>
    public static UiView Row(float spacing, params UiView[] views)
    {
        var row = new HStack(VerticalAlignment.Center, spacing);
        row.Add(views);
        return row;
    }

    /// <summary>A view with a small caption under it.</summary>
    public static UiView Captioned(UiView view, string caption) => new VStack(HorizontalAlignment.Center, 8f)
    {
        view,
        new Text(caption).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted),
    };
}
