namespace Super.Ui;

/// <summary>
/// The modifiers that style text: its colour, size, weight, font, digits and line limit. Each
/// styles the text in the view it wraps and everything inside it, and one further in wins.
/// </summary>
public static class TextExtensions
{
    /// <summary>Colors the text in this view, and in everything inside it.</summary>
    public static UiView Foreground(this UiView view, Color color) => new StyleModifier(view, Color: color);

    /// <summary>Sets the size of the text in this view, and in everything inside it, in pixels.</summary>
    public static UiView FontSize(this UiView view, float size) => new StyleModifier(view, Size: size);

    public static UiView Bold(this UiView view) => new StyleModifier(view, Bold: true);

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
