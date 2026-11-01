namespace Super.Ui;

/// <summary>
/// The modifiers. Each wraps a view, so their order matters: padding then background
/// differs from the reverse. The exception is the box: background, border, corner radius,
/// opacity and margin written next to each other are one box, in any order.
/// </summary>
public static class UiExtensions
{
    /// <summary>Stretch to fill, as in <c>.Frame(maxWidth: UiExtensions.Infinity)</c>.</summary>
    public const float Infinity = float.PositiveInfinity;

    public static UiView Padding(this UiView view, float all) => view.Padding(all, all, all, all);

    public static UiView Padding(this UiView view, float horizontal, float vertical) =>
        view.Padding(vertical, horizontal, vertical, horizontal);

    public static UiView Padding(this UiView view, float top, float trailing, float bottom, float leading) =>
        new PaddingModifier(view, new Edges(top, trailing, bottom, leading));

    /// <summary>Space outside the view, which its background doesn't fill. Margins next to each other add up.</summary>
    public static UiView Margin(this UiView view, float all) => view.Margin(all, all, all, all);

    public static UiView Margin(this UiView view, float horizontal, float vertical) =>
        view.Margin(vertical, horizontal, vertical, horizontal);

    public static UiView Margin(this UiView view, float top, float trailing, float bottom, float leading)
    {
        var box = Box(view);
        return box with { Margin = box.Margin + new Edges(top, trailing, bottom, leading) };
    }

    /// <summary>
    /// Gives the view a fixed size, or limits it. A maximum lets it stretch up to that
    /// length, as flexbox's grow does, and a minimum holds it open. A view smaller than
    /// the frame sits where <paramref name="alignment"/> says.
    /// </summary>
    public static UiView Frame(
        this UiView view,
        float? width = null,
        float? height = null,
        float? maxWidth = null,
        float? maxHeight = null,
        Alignment alignment = Alignment.Center,
        float? minWidth = null,
        float? minHeight = null) =>
        new FrameModifier(view, width, height, maxWidth, maxHeight, alignment, minWidth, minHeight);

    /// <summary>Makes the view the size it would like to be, rather than the size it's offered.</summary>
    public static UiView FixedSize(this UiView view) => new FixedSizeModifier(view);

    /// <summary>Fills the view's whole area behind it.</summary>
    public static UiView Background(this UiView view, Color color) => Box(view) with { Fill = color };

    /// <summary>Rounds this view's background, border and image, and clips what's inside it.</summary>
    public static UiView CornerRadius(this UiView view, float radius) => Box(view) with { Radius = radius };

    /// <summary>A line inside the view's edge, following its corners.</summary>
    public static UiView Border(this UiView view, float width, Color color) =>
        Box(view) with { BorderWidth = width, BorderColor = color };

    /// <summary>Fades this view and everything inside it, from 0 to 1.</summary>
    public static UiView Opacity(this UiView view, float value) => Box(view) with { Alpha = value };

    /// <summary>Colors the text in this view, and in everything inside it.</summary>
    public static UiView Foreground(this UiView view, Color color) => new StyleModifier(view, Color: color);

    /// <summary>Sets the size of the text in this view, and in everything inside it, in pixels.</summary>
    public static UiView FontSize(this UiView view, float size) => new StyleModifier(view, Size: size);

    public static UiView Bold(this UiView view) => new StyleModifier(view, Bold: true);

    /// <summary>Gives all digits the same width, so numbers that change don't jiggle.</summary>
    public static UiView MonospacedDigit(this UiView view) => new StyleModifier(view, MonospacedDigits: true);

    /// <summary>Names this view among its siblings, so it keeps its state when they move.</summary>
    public static UiView Id(this UiView view, object identity) => view with { Identity = identity };

    /// <summary>Continues the box this view already is, so neighbouring box modifiers make one box.</summary>
    private static BoxModifier Box(UiView view) => view as BoxModifier ?? new BoxModifier(view);
}
