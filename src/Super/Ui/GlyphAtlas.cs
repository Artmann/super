using System.Reflection;
using StbTrueTypeSharp;

namespace Super.Ui;

/// <summary>
/// Measures and draws text, with the pictures of its characters kept in one image.
/// Everything here happens on the CPU, so text measures the same with no GPU.
/// </summary>
internal sealed class GlyphAtlas
{
    /// <summary>The image's width and height. Characters that don't fit are measured but not drawn.</summary>
    public const int Size = UiGlyphSheet.Size;

    private readonly StbTrueType.stbtt_fontinfo _regular = LoadFont("Inter-Regular.ttf");
    private readonly StbTrueType.stbtt_fontinfo _bold = LoadFont("Inter-Bold.ttf");
    private readonly Dictionary<(bool Bold, int Size), UiFontFace> _faces = [];
    private readonly UiGlyphSheet _sheet = new();
    private readonly UiLineBreaker _lines = new();

    /// <summary>Goes up whenever <see cref="Pixels"/> changes.</summary>
    public int Version => _sheet.Version;

    /// <summary>White everywhere, with the shapes of the characters in alpha.</summary>
    public byte[] Pixels => _sheet.Pixels;

    /// <summary>
    /// How many pixels an interface unit covers. Characters are drawn and measured at their
    /// size in pixels, so they stay sharp, and lengths go in and out in interface units.
    /// </summary>
    public float Scale { get; set; } = 1f;

    public UiSize Measure(string text, in TextStyle style, float? maxWidth)
    {
        var face = FaceFor(style);
        _lines.Wrap(text, face, style, maxWidth * Scale);
        float width = 0f;
        foreach (var line in _lines.Lines)
        {
            width = Math.Max(width, line.Width);
        }

        return new UiSize(width / Scale, _lines.Lines.Count * face.LineHeight / Scale);
    }

    /// <summary>
    /// The text as it shows at this width: all of it, or, when a line limit cut it, the
    /// part that shows followed by an ellipsis.
    /// </summary>
    public string Shown(string text, in TextStyle style, float width)
    {
        if (style.LineLimit is null)
        {
            return text;
        }

        _lines.Wrap(text, FaceFor(style), style, width * Scale);
        var last = _lines.Lines[^1];
        return last.Cut ? string.Concat(text.AsSpan(0, last.End), UiLineBreaker.Ellipsis.ToString()) : text;
    }

    public void Draw(UiDrawList list, string text, in TextStyle style, UiRect frame)
    {
        // Worked out in pixels, so characters land on whole pixels, then handed back in interface units.
        var face = FaceFor(style);
        _lines.Wrap(text, face, style, frame.Width * Scale);
        float baseline = MathF.Round(frame.Y * Scale) + face.Ascent;
        foreach (var line in _lines.Lines)
        {
            float pen = MathF.Round(frame.X * Scale);
            char previous = '\0';
            for (int i = line.Start; i < line.End; i++)
            {
                char character = text[i];
                var glyph = face.Glyph(character);
                float advance = face.Advance(style, previous, character);
                float cell = style.MonospacedDigits && char.IsAsciiDigit(character) ? (advance - glyph.Advance) / 2f : face.Kerning(previous, character);
                AddGlyph(list, glyph, MathF.Round(pen + cell), baseline, style.Color);
                pen += advance;
                previous = character;
            }

            if (line.Cut)
            {
                AddGlyph(list, face.Glyph(UiLineBreaker.Ellipsis), MathF.Round(pen), baseline, style.Color);
            }

            baseline += face.LineHeight;
        }
    }

    /// <summary>Draws a character with its pen at x on the baseline, both in pixels.</summary>
    private void AddGlyph(UiDrawList list, UiGlyph glyph, float x, float baseline, Color color)
    {
        if (glyph.Width > 0)
        {
            var box = new UiRect(x + glyph.OffsetX, baseline + glyph.OffsetY, glyph.Width, glyph.Height);
            list.AddQuad(new UiRect(box.X / Scale, box.Y / Scale, box.Width / Scale, box.Height / Scale), glyph.UvMin, glyph.UvMax, color);
        }
    }

    private static StbTrueType.stbtt_fontinfo LoadFont(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} isn't built into Super.");
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return StbTrueType.CreateFont(data, 0) ?? throw new InvalidOperationException($"{name} could not be read.");
    }

    private UiFontFace FaceFor(in TextStyle style)
    {
        int size = Math.Max((int)MathF.Round(style.Size * Scale), 1);
        if (!_faces.TryGetValue((style.Bold, size), out var face))
        {
            face = new UiFontFace(style.Bold ? _bold : _regular, size, _sheet);
            _faces.Add((style.Bold, size), face);
        }

        return face;
    }
}
