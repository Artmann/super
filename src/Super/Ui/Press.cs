namespace Super.Ui;

/// <summary>How the mouse stands with something that can be clicked.</summary>
public readonly record struct PressState(bool Hovered, bool Pressed);

/// <summary>
/// Something clickable, drawn however a game likes: <paramref name="Content"/> describes
/// it for each state. A click is a left press and release on it. The click runs on the
/// next tick, just before Update, so it may change the game.
/// </summary>
public sealed record Pressable(Func<PressState, UiView> Content, Action? OnClick = null, bool Disabled = false) : UiView
{
    public override UiView Body
    {
        get
        {
            var look = UseState(default(PressState));
            bool off = Disabled || Environment.Disabled;
            var click = new ClickModifier(Content(off ? default : look.Value), OnClick, look, off);
            return new FocusModifier(click, new FocusKeys { Disabled = off });
        }
    }
}

/// <summary>How a <see cref="Button"/> stands out, from the main action down to a link in a line of text.</summary>
public enum ButtonStyle
{
    /// <summary>Filled with the theme's primary colour, for the main action.</summary>
    Default,

    /// <summary>A quiet fill, for actions beside the main one.</summary>
    Secondary,

    /// <summary>Only an edge until the pointer is over it.</summary>
    Outline,

    /// <summary>Nothing until the pointer is over it, for toolbars and lists.</summary>
    Ghost,

    /// <summary>The theme's destructive colour, for what can't be undone.</summary>
    Destructive,

    /// <summary>Text that underlines under the pointer.</summary>
    Link,
}

/// <summary>
/// A labeled button in the theme's colours and the control size around it, in one of the
/// <see cref="ButtonStyle"/>s, with an optional icon before the label. With an icon and an empty
/// label it's a square icon button. A disabled one, or one inside <c>.Disabled()</c>, is faded and
/// doesn't react, but still blocks the mouse.
/// </summary>
public sealed record Button(string Label, Action? OnClick = null, bool Disabled = false) : UiView
{
    public ButtonStyle Style { get; init; }

    public IconName? Icon { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var theme = environment.Theme;
            var metrics = environment.Metrics;
            bool off = Disabled || environment.Disabled;
            var button = new Pressable(
                look => Face(look, theme, metrics)
                    .Frame(minWidth: Label.Length == 0 ? metrics.Height : null, minHeight: Style == ButtonStyle.Link ? null : metrics.Height)
                    .Background(Fill(theme.Colors, look))
                    .Border(Style == ButtonStyle.Outline ? 1f : 0f, theme.Colors.Input)
                    .CornerRadius(theme.Radius)
                    .Opacity(off ? 0.5f : 1f),
                OnClick,
                off);
            string name = Label.Length == 0 && Icon is { } icon ? icon.ToString() : Label;
            return new SemanticsModifier(button, new UiSemantics(UiRole.Button, name) { Disabled = off });
        }
    }

    /// <summary>A fill a little towards the background when hovered, and further when held.</summary>
    internal static Color Shade(Color fill, ThemeColors colors, PressState look) =>
        look.Pressed ? fill.Mix(colors.Background, 0.2f) : look.Hovered ? fill.Mix(colors.Background, 0.1f) : fill;

    /// <summary>The icon and label, in the style's text colour, padded at the sides.</summary>
    private UiView Face(PressState look, Theme theme, ControlMetrics metrics)
    {
        var text = TextColor(theme.Colors, look);
        UiView face = (Label.Length, Icon) switch
        {
            (0, { } icon) => new Icon(icon),
            (_, { } icon) => new Label(Label, new Icon(icon)),
            _ => new Text(Label),
        };
        face = face.FontSize(metrics.FontSize).Weight(FontWeight.Medium).Foreground(text).LineLimit(1);
        if (Style == ButtonStyle.Link)
        {
            // An underline that only shows under the pointer, there all along so nothing moves.
            return new VStack(Spacing: 1f) { face, new Rectangle().Fill(look.Hovered ? text : default).Frame(height: 1f) }.FixedSize();
        }

        return Label.Length == 0 ? face : face.Padding(metrics.Padding, 0f);
    }

    private Color TextColor(ThemeColors colors, PressState look) => Style switch
    {
        ButtonStyle.Default => colors.PrimaryForeground,
        ButtonStyle.Secondary => colors.SecondaryForeground,
        ButtonStyle.Destructive => colors.DestructiveForeground,
        ButtonStyle.Link => look.Pressed ? colors.MutedForeground : colors.Foreground,
        _ => colors.Foreground,
    };

    private Color Fill(ThemeColors colors, PressState look) => Style switch
    {
        ButtonStyle.Default => Shade(colors.Primary, colors, look),
        ButtonStyle.Secondary => Shade(colors.Secondary, colors, look),
        ButtonStyle.Destructive => Shade(colors.Destructive, colors, look),
        ButtonStyle.Outline or ButtonStyle.Ghost => look.Pressed ? colors.Hover.Mix(colors.Foreground, 0.06f) : look.Hovered ? colors.Hover : default,
        _ => default,
    };
}

/// <summary>A modifier the mouse can touch. It covers what its view draws, so a margin stays out of reach.</summary>
internal abstract record PointerModifier(UiView Child) : Modifier(Child)
{
    internal override bool Blocks => true;

    internal override UiRect HitArea(UiNode node) => ShownArea(Inner(node));
}

internal sealed record ClickModifier(UiView Child, Action? Click, State<PressState>? Look = null, bool Disabled = false) : PointerModifier(Child);

/// <summary>Lets the mouse through this view and everything inside it.</summary>
internal sealed record IgnoresPointerModifier(UiView Child) : Modifier(Child)
{
    internal override bool PassesPointer => true;
}

internal sealed record DragModifier(UiView Child, Func<DragStart, object?> Grab, Func<object, UiView?> Preview) : PointerModifier(Child);

/// <summary>How a drag began: the button pressed, and which modifier keys were held at that moment.</summary>
public readonly record struct DragStart(MouseButton Button, bool Shift = false, bool Control = false, bool Alt = false);

/// <summary>Lets the pointer offer a payload without knowing its type.</summary>
internal abstract record DropModifier(UiView Child) : PointerModifier(Child)
{
    public abstract bool Accepts(object payload);

    public abstract void Drop(object payload);
}

internal sealed record DropModifier<T>(UiView Child, Action<T> OnDrop, Func<T, bool>? CanDrop) : DropModifier(Child)
{
    public override bool Accepts(object payload) => payload is T value && (CanDrop?.Invoke(value) ?? true);

    public override void Drop(object payload) => OnDrop((T)payload);
}

internal sealed record TooltipModifier(UiView Child, UiView Content) : PointerModifier(Child);

/// <summary>Follows a held left button across the view, as a fraction of its width.</summary>
internal sealed record SlideModifier(UiView Child, Action<float> Slide, float Inset, bool Disabled = false) : PointerModifier(Child)
{
    /// <summary>How far across this x is, from 0 at the leading inset to 1 at the trailing one.</summary>
    public float Fraction(UiNode node, float x)
    {
        var area = HitArea(node);
        float span = area.Width - 2f * Inset;
        return span <= 0f ? 0.5f : Math.Clamp((x - area.X - Inset) / span, 0f, 1f);
    }
}
