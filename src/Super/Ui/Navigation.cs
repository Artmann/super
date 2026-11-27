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
            object key = depth == 0 ? "root" : (depth, pages[^1]);
            var page = (depth == 0 ? Root : Destination(pages[^1])).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
            var view = new VStack(HorizontalAlignment.Leading, 0f)
            {
                Bar(title, under),
                new Divider(),
                Pages(key, depth, page),
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

    /// <summary>
    /// The page on top, and while it slides in, the page it took over from sliding out: a page pushed comes in from
    /// the trailing side and one gone back to from the leading side. Both keep their places by their keys, so the page
    /// going out is the same one that was showing and the page that comes back is the one that was left.
    /// </summary>
    private UiView Pages(object key, int depth, UiView page)
    {
        var slide = UseState(new PageSlide()).Value;
        if (!Equals(slide.Key, key))
        {
            if (slide.Key is not null)
            {
                (slide.LeavingKey, slide.Leaving, slide.Forward) = (slide.Key, slide.Page, depth >= slide.Depth);
                slide.Changes++;
            }

            (slide.Key, slide.Depth) = (key, depth);
        }

        slide.Page = page;
        bool slides = Environment.Theme.Motion.SlidesPages;
        float moved = UseAnimation(slide.Changes, PageSlide.Seconds, Easing.Out);
        float shown = slides ? 1f - Math.Clamp(slide.Changes - moved, 0f, 1f) : 1f;
        if (shown >= 1f)
        {
            slide.Leaving = null;
        }

        float way = slide.Forward ? PageSlide.Travel : -PageSlide.Travel;
        return new ZStack(Alignment.TopLeading)
        {
            slide.Leaving is { } leaving ? PageSlide.At(leaving, -way * shown, 1f - shown, true).Id(slide.LeavingKey!) : null,
            PageSlide.At(page, way * (1f - shown), shown, false).Id(key),
        };
    }
}

/// <summary>What a navigation stack remembers between frames to slide from one page to the next.</summary>
internal sealed class PageSlide
{
    /// <summary>How long a page takes to slide in, in seconds.</summary>
    public const float Seconds = 0.25f;

    /// <summary>How far a page slides, in units.</summary>
    public const float Travel = 40f;

    public object? Key { get; set; }

    public int Depth { get; set; }

    public UiView? Page { get; set; }

    public object? LeavingKey { get; set; }

    /// <summary>The page going out, until the new one is all the way in.</summary>
    public UiView? Leaving { get; set; }

    /// <summary>Whether the last change went deeper, so the new page comes in from the trailing side.</summary>
    public bool Forward { get; set; } = true;

    /// <summary>How many times the page has changed, which the slide counts towards.</summary>
    public int Changes { get; set; }

    /// <summary>A page this far across and this faded, in the same views however far along so it keeps its state.</summary>
    public static UiView At(UiView page, float x, float alpha, bool leaving) =>
        new LeavingModifier(new OffsetModifier(new BoxModifier(page) { Alpha = alpha }, x, 0f), leaving);
}
