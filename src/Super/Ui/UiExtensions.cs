namespace Super.Ui;

/// <summary>The modifiers. Each wraps a view, so their order matters: padding then background differs from the reverse.</summary>
public static class UiExtensions
{
    /// <summary>Stretch to fill, as in <c>.Frame(maxWidth: UiExtensions.Infinity)</c>.</summary>
    public const float Infinity = float.PositiveInfinity;

    public static UiView Padding(this UiView view, float all) => new PaddingModifier(view, all, all);

    public static UiView Padding(this UiView view, float horizontal, float vertical) => new PaddingModifier(view, horizontal, vertical);

    /// <summary>
    /// Gives the view a fixed size, or lets it stretch up to a maximum, as flexbox's grow
    /// does. A view smaller than the frame sits where <paramref name="alignment"/> says.
    /// </summary>
    public static UiView Frame(
        this UiView view,
        float? width = null,
        float? height = null,
        float? maxWidth = null,
        float? maxHeight = null,
        Alignment alignment = Alignment.Center) =>
        new FrameModifier(view, width, height, maxWidth, maxHeight, alignment);

    /// <summary>Makes the view the size it would like to be, rather than the size it's offered.</summary>
    public static UiView FixedSize(this UiView view) => new FixedSizeModifier(view);

    /// <summary>Fills the view's whole area behind it.</summary>
    public static UiView Background(this UiView view, Color color) => new BackgroundModifier(view, color);

    /// <summary>Colors the text in this view, and in everything inside it.</summary>
    public static UiView Foreground(this UiView view, Color color) => new StyleModifier(view, Color: color);

    /// <summary>Sets the size of the text in this view, and in everything inside it, in pixels.</summary>
    public static UiView FontSize(this UiView view, float size) => new StyleModifier(view, Size: size);

    public static UiView Bold(this UiView view) => new StyleModifier(view, Bold: true);

    /// <summary>Gives all digits the same width, so numbers that change don't jiggle.</summary>
    public static UiView MonospacedDigit(this UiView view) => new StyleModifier(view, MonospacedDigits: true);

    /// <summary>Names this view among its siblings, so it keeps its state when they move.</summary>
    public static UiView Id(this UiView view, object identity) => view with { Identity = identity };
}
