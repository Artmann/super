namespace Super.Ui;

/// <summary>
/// A thin line in the theme's border colour that runs across the stack it's in: from side to side in a
/// <see cref="VStack"/>, and from top to bottom in an <see cref="HStack"/>.
/// </summary>
public sealed record Divider : PrimitiveView
{
    private const float Thickness = 1f;

    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.View is Stack stack)
            {
                return stack.ToSize(Thickness, stack.CrossOf(proposal) ?? 0f);
            }
        }

        return new UiSize(proposal.Width ?? 0f, Thickness);
    }

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw) =>
        draw.AddBox(node.Frame, node.Environment.Theme.Colors.Border, default(CornerRadii));
}

/// <summary>How a <see cref="Badge"/> stands out.</summary>
public enum BadgeStyle
{
    /// <summary>The theme's primary colour, for what matters most.</summary>
    Default,

    /// <summary>A quiet fill, for counts and tags.</summary>
    Secondary,

    /// <summary>The theme's destructive colour, for errors and warnings.</summary>
    Destructive,

    /// <summary>Only an edge, for the quietest tag.</summary>
    Outline,
}

/// <summary>A short tag in a small rounded box: "New", a count, a rarity, a status.</summary>
public sealed record Badge(string Label, BadgeStyle Style = BadgeStyle.Default) : UiView
{
    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var (fill, text) = Style switch
            {
                BadgeStyle.Secondary => (theme.Colors.Secondary, theme.Colors.SecondaryForeground),
                BadgeStyle.Destructive => (theme.Colors.Destructive, theme.Colors.DestructiveForeground),
                BadgeStyle.Outline => (default(Color), theme.Colors.Foreground),
                _ => (theme.Colors.Primary, theme.Colors.PrimaryForeground),
            };
            var badge = new Text(Label)
                .FontSize(12f)
                .Weight(FontWeight.SemiBold)
                .Foreground(text)
                .LineLimit(1)
                .Padding(8f, 2f)
                .Background(fill)
                .CornerRadius(theme.RadiusSmall);
            return Style == BadgeStyle.Outline ? badge.Border(1f, theme.Colors.Border) : badge;
        }
    }
}

/// <summary>
/// Text with an icon before it, as in a menu row, a list or a button. The icon takes the text's size and
/// colour, so <c>.FontSize(...)</c> and <c>.Foreground(...)</c> on the label change both.
/// </summary>
public sealed record Label(string Title, UiView Icon) : UiView
{
    public Label(string title, IconName icon)
        : this(title, new Icon(icon))
    {
    }

    public override UiView Body => new HStack(Spacing: MathF.Round(Environment.FontSize * 0.5f))
    {
        Icon,
        new Text(Title),
    };
}

/// <summary>A key drawn as a small keycap, for hints such as "Press E to open" or a shortcut beside a menu row.</summary>
public sealed record KeyHint(string Key) : UiView
{
    public override UiView Body => Cap(new Text(Key), Environment);

    /// <summary>The size of the text on a keycap here, and how tall the cap is.</summary>
    internal static (float Size, float Side) Sizes(UiEnvironment environment)
    {
        float size = MathF.Round(environment.FontSize * 0.85f);
        return (size, MathF.Round(size * 1.7f));
    }

    /// <summary>A keycap round a face, which may be text or an icon.</summary>
    internal static UiView Cap(UiView face, UiEnvironment environment)
    {
        var theme = environment.Theme;
        var (size, side) = Sizes(environment);
        return face
            .FontSize(size)
            .Weight(FontWeight.Medium)
            .Foreground(theme.Colors.MutedForeground)
            .LineLimit(1)
            .Padding(MathF.Round(size * 0.4f), 0f)
            .Frame(minWidth: side, minHeight: side)
            .Background(theme.Colors.Muted)
            .Border(1f, theme.Colors.Border)
            .CornerRadius(theme.RadiusSmall);
    }
}
