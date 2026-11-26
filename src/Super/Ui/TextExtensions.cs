namespace Super.Ui;

/// <summary>
/// The modifiers that style text: its colour, size, weight, font, digits and line limit. Each
/// styles the text in the view it wraps and everything inside it, and one further in wins.
/// </summary>
public static class TextExtensions
{
    /// <summary>Colors the text in this view, and in everything inside it.</summary>
    public static UiView Foreground(this UiView view, Color color) => new StyleModifier(view, Color: color);

    /// <summary>Colors the text in this view, and in everything inside it, with a colour from the theme.</summary>
    public static UiView Foreground(this UiView view, Tone tone) => new StyleModifier(view, Tone: tone);

    /// <summary>Sets the size of the text in this view, and in everything inside it, in pixels.</summary>
    public static UiView FontSize(this UiView view, float size) => new StyleModifier(view, Size: size);

    public static UiView Bold(this UiView view) => new StyleModifier(view, Weight: FontWeight.Bold);

    /// <summary>Sets how heavy the text in this view is, and in everything inside it.</summary>
    public static UiView Weight(this UiView view, FontWeight weight) => new StyleModifier(view, Weight: weight);

    /// <summary>
    /// Slants the text in this view, and in everything inside it. The built-in font has an italic; a
    /// font of your own is drawn upright.
    /// </summary>
    public static UiView Italic(this UiView view) => new StyleModifier(view, Italic: true);

    /// <summary>
    /// Gives the text in this view, and in everything inside it, the theme's size and weight for this
    /// kind of text, such as <see cref="Super.Ui.TextStyle.Title"/> or <see cref="Super.Ui.TextStyle.Caption"/>.
    /// </summary>
    public static UiView TextStyle(this UiView view, TextStyle style) => new StyleModifier(view, Style: style);

    /// <summary>Lines text of more than one line up on the left, the middle or the right of its frame.</summary>
    public static UiView TextAlign(this UiView view, TextAlignment alignment) => new StyleModifier(view, Alignment: alignment);

    /// <summary>
    /// Sets the typeface of the text in this view, and in everything inside it, in place of the
    /// built-in Inter. Name it once with <c>content.Fonts.Font(...)</c> and keep it.
    /// </summary>
    public static UiView Font(this UiView view, GameFont font) => new StyleModifier(view, Font: font);

    /// <summary>Gives all digits the same width, so numbers that change don't jiggle.</summary>
    public static UiView MonospacedDigit(this UiView view) => new StyleModifier(view, MonospacedDigits: true);

    /// <summary>
    /// Keeps the text in this view, and in everything inside it, to at most this many lines.
    /// Text that needs more is cut at the end of the last line, which ends in an ellipsis (…),
    /// so it stays inside its frame and shows that something was left out. One line keeps a
    /// label and its number together, such as "Wood 16" in a row that's too narrow.
    /// </summary>
    public static UiView LineLimit(this UiView view, int lines)
    {
        if (lines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), lines, "A line limit must be at least 1.");
        }

        return new StyleModifier(view, LineLimit: lines);
    }
}
