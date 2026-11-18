namespace Super.Ui;

/// <summary>A line of text from Start up to End. A cut line ends in an ellipsis, counted in its width.</summary>
internal readonly record struct UiTextLine(int Start, int End, float Width, bool Cut = false);

/// <summary>
/// Breaks text into lines at newlines, and at spaces where it won't fit. With a line limit,
/// it keeps the first lines and ends the last one kept with an ellipsis. Widths are in pixels.
/// </summary>
internal sealed class UiLineBreaker
{
    public const char Ellipsis = '…';

    private const float WidthTolerance = 0.01f;

    private readonly List<UiTextLine> _lines = [];

    /// <summary>The lines the last <see cref="Wrap"/> made.</summary>
    public IReadOnlyList<UiTextLine> Lines => _lines;

    public void Wrap(string text, UiFontFace face, in TextStyle style, float? maxWidth)
    {
        _lines.Clear();
        float limit = (maxWidth ?? float.PositiveInfinity) + WidthTolerance;
        // Each line ends at a newline, a space or the end, and the next one starts after that.
        for (int start = 0; start <= text.Length; start = _lines[^1].End + 1)
        {
            _lines.Add(NextLine(text, start, face, style, limit));
        }

        if (style.LineLimit is { } most)
        {
            Cut(text, face, style, most, limit);
        }
    }

    /// <summary>
    /// The line that begins at start: up to the next newline, or up to the last space before
    /// the text stops fitting. A word too long for a line of its own still gets one.
    /// </summary>
    private static UiTextLine NextLine(string text, int start, UiFontFace face, in TextStyle style, float limit)
    {
        int lastSpace = -1;
        float width = 0f;
        float widthAtSpace = 0f;
        char previous = '\0';
        for (int i = start; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\n')
            {
                return new UiTextLine(start, i, width);
            }

            if (character == ' ')
            {
                lastSpace = i;
                widthAtSpace = width;
            }

            float advance = face.Advance(style, previous, character);
            if (width + advance > limit && character != ' ' && lastSpace >= 0)
            {
                return new UiTextLine(start, lastSpace, widthAtSpace);
            }

            width += advance;
            previous = character;
        }

        return new UiTextLine(start, text.Length, width);
    }

    /// <summary>
    /// Keeps the first lines, and cuts the last one kept where the text and an ellipsis
    /// still fit, when lines were left out or one word is too long for it.
    /// </summary>
    private void Cut(string text, UiFontFace face, in TextStyle style, int most, float limit)
    {
        bool leftOut = _lines.Count > most;
        var last = _lines[Math.Min(most, _lines.Count) - 1];
        if (!leftOut && last.Width <= limit)
        {
            return;
        }

        if (leftOut)
        {
            _lines.RemoveRange(most, _lines.Count - most);
        }

        float ellipsis = face.Glyph(Ellipsis).Advance;
        float room = limit - ellipsis;
        int end = last.Start;
        float width = 0f;
        char previous = '\0';
        while (end < text.Length && text[end] != '\n')
        {
            float advance = face.Advance(style, previous, text[end]);
            if (width + advance > room)
            {
                break;
            }

            width += advance;
            previous = text[end];
            end++;
        }

        while (end > last.Start && text[end - 1] == ' ')
        {
            end--;
        }

        _lines[^1] = new UiTextLine(last.Start, end, Width(text, face, style, last.Start, end) + ellipsis, Cut: true);
    }

    private static float Width(string text, UiFontFace face, in TextStyle style, int start, int end)
    {
        float width = 0f;
        char previous = '\0';
        for (int i = start; i < end; i++)
        {
            width += face.Advance(style, previous, text[i]);
            previous = text[i];
        }

        return width;
    }
}
