using System.Numerics;
using StbTrueTypeSharp;

namespace Super.Ui;

/// <summary>Where a character's picture is in the sheet, how big it is, and how far it moves the pen.</summary>
internal readonly record struct UiGlyph(Vector2 UvMin, Vector2 UvMax, int Width, int Height, int OffsetX, int OffsetY, float Advance);

/// <summary>
/// One font at one size in pixels. Its first use draws all of basic Latin into the sheet
/// at once, so numbers ticking up don't send new images to the GPU.
/// </summary>
internal sealed unsafe class UiFontFace
{
    private readonly UiGlyphSheet _sheet;
    private readonly Dictionary<char, UiGlyph> _glyphs = [];

    public UiFontFace(StbTrueType.stbtt_fontinfo font, int pixelHeight, UiGlyphSheet sheet)
    {
        _sheet = sheet;
        Font = font;
        Scale = StbTrueType.stbtt_ScaleForPixelHeight(font, pixelHeight);
        int ascent;
        int descent;
        int gap;
        StbTrueType.stbtt_GetFontVMetrics(font, &ascent, &descent, &gap);
        Ascent = MathF.Round(ascent * Scale);
        LineHeight = MathF.Ceiling((ascent - descent + gap) * Scale);
        for (char character = ' '; character <= '~'; character++)
        {
            Glyph(character);
        }

        foreach (char digit in "0123456789")
        {
            DigitAdvance = Math.Max(DigitAdvance, Glyph(digit).Advance);
        }
    }

    public StbTrueType.stbtt_fontinfo Font { get; }

    public float Scale { get; }

    public float Ascent { get; }

    public float LineHeight { get; }

    /// <summary>The widest digit, which every digit takes when digits are monospaced.</summary>
    public float DigitAdvance { get; }

    public UiGlyph Glyph(char character)
    {
        if (!_glyphs.TryGetValue(character, out var glyph))
        {
            glyph = Rasterize(character);
            _glyphs.Add(character, glyph);
        }

        return glyph;
    }

    public float Kerning(char previous, char character) =>
        previous == '\0' ? 0f : StbTrueType.stbtt_GetCodepointKernAdvance(Font, previous, character) * Scale;

    /// <summary>How far this character moves the pen after the one before it.</summary>
    public float Advance(in TextLook style, char previous, char character)
    {
        if (style.MonospacedDigits && char.IsAsciiDigit(character))
        {
            return DigitAdvance;
        }

        return Glyph(character).Advance + Kerning(previous, character);
    }

    private UiGlyph Rasterize(char character)
    {
        int advance;
        int bearing;
        StbTrueType.stbtt_GetCodepointHMetrics(Font, character, &advance, &bearing);
        int left;
        int top;
        int right;
        int bottom;
        StbTrueType.stbtt_GetCodepointBitmapBox(Font, character, Scale, Scale, &left, &top, &right, &bottom);
        int width = right - left;
        int height = bottom - top;
        float scaledAdvance = advance * Scale;
        if (width <= 0 || height <= 0 || !_sheet.TryReserve(width, height, out int sheetX, out int sheetY))
        {
            return new UiGlyph(default, default, 0, 0, 0, 0, scaledAdvance);
        }

        byte[] coverage = new byte[width * height];
        fixed (byte* pixels = coverage)
        {
            StbTrueType.stbtt_MakeCodepointBitmap(Font, pixels, width, height, width, Scale, Scale, character);
        }

        _sheet.Write(sheetX, sheetY, width, coverage);
        var uvMin = new Vector2(sheetX, sheetY) / UiGlyphSheet.Size;
        var uvMax = new Vector2(sheetX + width, sheetY + height) / UiGlyphSheet.Size;
        return new UiGlyph(uvMin, uvMax, width, height, left, top, scaledAdvance);
    }
}
