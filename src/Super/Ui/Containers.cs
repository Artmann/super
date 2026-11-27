using System.Collections;

namespace Super.Ui;

/// <summary>
/// A component that holds a list of views, written with collection syntax as a stack is:
/// <c>new Card { new Text("Hi"), new Button("Go") }</c>. Null keeps a spot empty.
/// </summary>
public abstract record ViewList : UiView, IEnumerable
{
    private readonly List<UiView?> _items = [];

    /// <summary>The views added, nulls included, in the order they were added.</summary>
    protected IReadOnlyList<UiView?> Items => _items;

    public void Add(UiView? view) => _items.Add(view);

    /// <summary>Adds a stack. It's one view, even though it's also a list.</summary>
    public void Add(Container view) => _items.Add(view);

    /// <summary>Adds several views, such as a list of rows. Give them an Id if the list changes.</summary>
    public void Add(IEnumerable<UiView> views) => _items.AddRange(views);

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Puts the views into a stack, keeping the empty spots.</summary>
    protected T Fill<T>(T stack)
        where T : Container
    {
        foreach (var item in _items)
        {
            stack.Add(item);
        }

        return stack;
    }

    /// <summary>Two lists match when their views do, so an unchanged one is skipped.</summary>
    public virtual bool Equals(ViewList? other) => base.Equals(other) && _items.SequenceEqual(other!._items);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), _items.Count);
}

/// <summary>
/// A raised panel on the theme's surface colour, with its edge, large corners and a small shadow. Its views
/// stack down the leading edge, <see cref="Spacing"/> apart.
/// </summary>
public sealed record Card : ViewList
{
    public float Spacing { get; init; } = 12f;

    public float Inset { get; init; } = 16f;

    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var stack = Fill(new VStack(HorizontalAlignment.Leading, Spacing)).Padding(Inset);
            return ThemePanel.On(stack, theme.Panels.Card, theme.Colors.Surface, box => box
                .Background(theme.Colors.Surface)
                .Border(1f, theme.Colors.Border)
                .CornerRadius(theme.RadiusLarge)
                .Shadow(theme.Shadows.Small));
        }
    }
}

/// <summary>
/// A label with a value or a control at the trailing edge of the row, as in a settings list or a stats
/// panel. Text given as the value is muted.
/// </summary>
public sealed record LabeledContent(string Label, UiView Content) : UiView
{
    public LabeledContent(string label, string value)
        : this(label, new Text(value).Foreground(Tone.Muted).LineLimit(1))
    {
    }

    public override UiView Body => new HStack(Spacing: Environment.Metrics.Gap)
    {
        new Text(Label).LineLimit(1),
        new Spacer(),
        Content,
    };
}

/// <summary>
/// A group of rows under an optional header, with a line between each, on a rounded panel, and an optional
/// note under it, as in a settings screen. Put sections in a <see cref="Form"/>.
/// </summary>
public sealed record Section(string? Header = null, string? Footer = null) : ViewList
{
    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var rows = new VStack(HorizontalAlignment.Leading, 0f);
            int shown = 0;
            foreach (var item in Items)
            {
                // Each row keeps its spot, so a row that comes and goes doesn't move the state of those after it.
                rows.Add(item is null ? null : Row(item, shown++ > 0));
            }

            var panel = rows
                .Background(theme.Colors.Surface)
                .Border(1f, theme.Colors.Border)
                .CornerRadius(theme.Radius);
            return new VStack(HorizontalAlignment.Leading, 6f)
            {
                Header is { } header ? new Text(header).TextStyle(TextStyle.Subheadline).Foreground(Tone.Muted).Padding(0f, 0f, 0f, 4f) : null,
                panel,
                Footer is { } footer ? new Text(footer).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted).Padding(0f, 0f, 0f, 4f) : null,
            };
        }
    }

    private static UiView Row(UiView item, bool line) => new VStack(HorizontalAlignment.Leading, 0f)
    {
        line ? new Divider() : null,
        item.Padding(14f, 10f).Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading),
    };
}

/// <summary>
/// A settings screen: <see cref="Section"/>s one under the other, filling the width they're offered. Put it
/// in a <see cref="ScrollView"/> when it may be taller than the screen.
/// </summary>
public sealed record Form : ViewList
{
    public override UiView Body => Fill(new VStack(HorizontalAlignment.Leading, 24f))
        .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.TopLeading);
}

/// <summary>
/// A heading with a chevron that shows or hides the views under it. It shows <paramref name="IsExpanded"/>
/// and calls <paramref name="OnChange"/> with the other value when clicked, which the game keeps and passes
/// back; leave IsExpanded null and it keeps whether it's open itself, starting closed.
/// </summary>
public sealed record DisclosureGroup(string Label, bool? IsExpanded = null, Action<bool>? OnChange = null) : ViewList
{
    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var own = UseState(false);
            bool open = IsExpanded ?? own.Value;
            bool off = Disabled || environment.Disabled;
            var change = OnChange;
            Action toggle = () =>
            {
                own.Value = !open;
                change?.Invoke(!open);
            };
            var header = new Pressable(look => Header(environment, open, look).Opacity(off ? 0.5f : 1f), toggle, off);
            var meaning = new SemanticsModifier(header, new UiSemantics(UiRole.Disclosure, Label, Checked: open) { Disabled = off });
            var metrics = environment.Metrics;
            return new VStack(HorizontalAlignment.Leading, metrics.Gap)
            {
                meaning,
                open ? Fill(new VStack(HorizontalAlignment.Leading, metrics.Gap)).Padding(0f, 0f, 0f, metrics.FontSize + metrics.Gap * 2f).Transition(Entrance.Drop, AnimationExtensions.Quick) : null,
            };
        }
    }

    private UiView Header(UiEnvironment environment, bool open, PressState look)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        return new HStack(Spacing: metrics.Gap)
        {
            new Icon(open ? IconName.ChevronDown : IconName.ChevronRight).FontSize(metrics.FontSize).Foreground(theme.Colors.MutedForeground),
            new Text(Label).FontSize(metrics.FontSize).Weight(FontWeight.Medium),
            new Spacer(),
        }
            .Padding(metrics.Gap, 6f)
            .Frame(maxWidth: UiExtensions.Infinity)
            .Background(look.Hovered ? theme.Colors.Hover : default)
            .CornerRadius(theme.RadiusSmall);
    }
}
