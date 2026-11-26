using System.Globalization;

namespace Super.Ui;

/// <summary>
/// The pages pushed onto a <see cref="NavigationStack{T}"/>, as values the game picks, such as an enum of
/// screens. Make one, keep it, and call <see cref="Push"/> and <see cref="Back"/> from anywhere: the stack
/// shows the page on top.
/// </summary>
public sealed class NavigationPath<T>
{
    private readonly State<IReadOnlyList<T>> _pages = new([]);

    /// <summary>The pages pushed, the root's first child first. Empty when the root shows.</summary>
    public IReadOnlyList<T> Pages => _pages.Value;

    /// <summary>How many pages are pushed above the root.</summary>
    public int Count => _pages.Value.Count;

    /// <summary>Shows this page above the others.</summary>
    public void Push(T page) => _pages.Value = [.. _pages.Value, page];

    /// <summary>Goes back to the page under the top. Returns false, and does nothing, at the root.</summary>
    public bool Back()
    {
        var pages = _pages.Value;
        if (pages.Count == 0)
        {
            return false;
        }

        _pages.Value = pages.Take(pages.Count - 1).ToArray();
        return true;
    }

    /// <summary>Goes back to the root.</summary>
    public void BackToRoot()
    {
        if (_pages.Value.Count > 0)
        {
            _pages.Value = [];
        }
    }
}

/// <summary>
/// Pages that go deeper, as in a settings menu: it shows <paramref name="Root"/>, or the view
/// <paramref name="Destination"/> makes for the page on top of <paramref name="Path"/>, under a bar with its
/// title and a back button. Escape, the gamepad's East button and the back button go back a page.
/// </summary>
public sealed record NavigationStack<T>(NavigationPath<T> Path, string Title, UiView Root, Func<T, UiView> Destination) : UiView
{
    /// <summary>The title of a pushed page in the bar. Without it, the page's ToString is.</summary>
    public Func<T, string>? TitleOf { get; init; }

    public override UiView Body
    {
        get
        {
            var pages = Path.Pages;
            int depth = pages.Count;
            string title = depth == 0 ? Title : TitleFor(pages[^1]);
            string? under = depth switch
            {
                0 => null,
                1 => Title,
                _ => TitleFor(pages[^2]),
            };

            // Each depth is its own place, so a page pushed where another was starts afresh.
            var page = (depth == 0 ? Root : Destination(pages[^1])).Id(depth == 0 ? "root" : (depth, pages[^1]));
            var view = new VStack(HorizontalAlignment.Leading, 0f)
            {
                Bar(title, under),
                new Divider(),
                page.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading),
            };
            var path = Path;
            var meaning = new SemanticsModifier(view, new UiSemantics(UiRole.Navigation, title, depth.ToString(CultureInfo.InvariantCulture)));
            return new SignUpModifier(meaning, depth > 0 ? () => path.Back() : null, null);
        }
    }

    private string TitleFor(T page) => TitleOf?.Invoke(page) ?? page?.ToString() ?? string.Empty;

    /// <summary>The title in the middle, and a button back to the page under, named after it.</summary>
    private UiView Bar(string title, string? under)
    {
        var metrics = Environment.Metrics;
        var path = Path;
        var back = under is null ? null : new Button(under, () => path.Back()) { Style = ButtonStyle.Ghost, Icon = IconName.ChevronLeft };
        return new ZStack
        {
            new Text(title).TextStyle(TextStyle.Headline).LineLimit(1),
            new HStack { back, new Spacer() },
        }
            .Padding(8f, 6f)
            .Frame(maxWidth: UiExtensions.Infinity, minHeight: metrics.Height + 12f);
    }
}
