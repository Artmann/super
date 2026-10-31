using System.Numerics;
using System.Reflection;
using StbTrueTypeSharp;

namespace Super.Ui;

/// <summary>
/// Measures text and keeps pictures of characters in one image. Everything here happens
/// on the CPU, so text measures the same with no GPU. A font size's first use draws all
/// of basic Latin at once, so numbers ticking up don't send new images to the GPU.
/// </summary>
internal sealed class GlyphAtlas
{
    /// <summary>The image's width and height. Characters that don't fit are measured but not drawn.</summary>
    public const int Size = 1024;

    private const int Padding = 1;
    private const int SolidBlock = 4;
    private const float WidthTolerance = 0.01f;

    private readonly StbTrueType.stbtt_fontinfo _regular = LoadFont("Inter-Regular.ttf");
    private readonly StbTrueType.stbtt_fontinfo _bold = LoadFont("Inter-Bold.ttf");
    private readonly Dictionary<(bool Bold, int Size), Face> _faces = [];
    private readonly List<Line> _lines = [];
    private int _shelfX = SolidBlock + Padding;
    private int _shelfY;
    private int _shelfHeight = SolidBlock;

    public GlyphAtlas()
    {
        for (int y = 0; y < SolidBlock; y++)
        {
            for (int x = 0; x < SolidBlock; x++)
            {
                SetPixel(x, y, 255);
            }
        }

        Version = 1;
    }

    /// <summary>Goes up whenever <see cref="Pixels"/> changes.</summary>
    public int Version { get; private set; }

    /// <summary>White everywhere, with the shapes of the characters in alpha.</summary>
    public byte[] Pixels { get; } = new byte[Size * Size * 4];

    /// <summary>A spot that's solid, for drawing plain rectangles.</summary>
    public Vector2 SolidUv { get; } = new Vector2(SolidBlock / 2f) / Size;

    public UiSize Measure(string text, in TextStyle style, float? maxWidth)
    {
        var face = FaceFor(style);
        Wrap(text, face, style, maxWidth);
        float width = 0f;
        foreach (var line in _lines)
        {
            width = Math.Max(width, line.Width);
        }

        return new UiSize(width, _lines.Count * face.LineHeight);
    }

    public void Draw(UiTree tree, string text, in TextStyle style, UiRect frame)
    {
        var face = FaceFor(style);
        Wrap(text, face, style, frame.Width);
        float baseline = MathF.Round(frame.Y) + face.Ascent;
        foreach (var line in _lines)
        {
            float pen = MathF.Round(frame.X);
            char previous = '\0';
            for (int i = line.Start; i < line.End; i++)
            {
                char character = text[i];
                var glyph = GlyphFor(face, character);
                float advance = Advance(face, style, previous, character);
                float cell = style.MonospacedDigits && char.IsAsciiDigit(character) ? (advance - glyph.Advance) / 2f : Kerning(face, previous, character);
                if (glyph.Width > 0)
                {
                    var box = new UiRect(MathF.Round(pen + cell) + glyph.OffsetX, baseline + glyph.OffsetY, glyph.Width, glyph.Height);
                    tree.AddQuad(box, glyph.UvMin, glyph.UvMax, style.Color);
                }

                pen += advance;
                previous = character;
            }

            baseline += face.LineHeight;
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

    private static float Kerning(Face face, char previous, char character) =>
        previous == '\0' ? 0f : StbTrueType.stbtt_GetCodepointKernAdvance(face.Font, previous, character) * face.Scale;

    private float Advance(Face face, in TextStyle style, char previous, char character)
    {
        if (style.MonospacedDigits && char.IsAsciiDigit(character))
        {
            return face.DigitAdvance;
        }

        return GlyphFor(face, character).Advance + Kerning(face, previous, character);
    }

    /// <summary>Breaks text into lines at newlines, and at spaces where it won't fit.</summary>
    private void Wrap(string text, Face face, in TextStyle style, float? maxWidth)
    {
        _lines.Clear();
        float limit = (maxWidth ?? float.PositiveInfinity) + WidthTolerance;
        int start = 0;
        int lastSpace = -1;
        float width = 0f;
        float widthAtSpace = 0f;
        char previous = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\n')
            {
                _lines.Add(new Line(start, i, width));
                start = i + 1;
                lastSpace = -1;
                width = 0f;
                previous = '\0';
                continue;
            }

            if (character == ' ')
            {
                lastSpace = i;
                widthAtSpace = width;
            }

            float advance = Advance(face, style, previous, character);
            if (width + advance > limit && character != ' ' && lastSpace >= start)
            {
                _lines.Add(new Line(start, lastSpace, widthAtSpace));
                start = lastSpace + 1;
                lastSpace = -1;
                width = 0f;
                previous = '\0';
                i = start - 1;
                continue;
            }

            width += advance;
            previous = character;
        }

        _lines.Add(new Line(start, text.Length, width));
    }

    private Face FaceFor(in TextStyle style)
    {
        int size = Math.Max((int)MathF.Round(style.Size), 1);
        if (_faces.TryGetValue((style.Bold, size), out var face))
        {
            return face;
        }

        face = new Face(style.Bold ? _bold : _regular, size);
        _faces.Add((style.Bold, size), face);
        for (char character = ' '; character <= '~'; character++)
        {
            GlyphFor(face, character);
        }

        foreach (char digit in "0123456789")
        {
            face.DigitAdvance = Math.Max(face.DigitAdvance, GlyphFor(face, digit).Advance);
        }

        return face;
    }

    private Glyph GlyphFor(Face face, char character)
    {
        if (!face.Glyphs.TryGetValue(character, out var glyph))
        {
            glyph = Rasterize(face, character);
            face.Glyphs.Add(character, glyph);
        }

        return glyph;
    }

    private unsafe Glyph Rasterize(Face face, char character)
    {
        int advance;
        int bearing;
        StbTrueType.stbtt_GetCodepointHMetrics(face.Font, character, &advance, &bearing);
        int left;
        int top;
        int right;
        int bottom;
        StbTrueType.stbtt_GetCodepointBitmapBox(face.Font, character, face.Scale, face.Scale, &left, &top, &right, &bottom);
        int width = right - left;
        int height = bottom - top;
        float scaledAdvance = advance * face.Scale;
        if (width <= 0 || height <= 0 || !TryReserve(width, height, out int atlasX, out int atlasY))
        {
            return new Glyph(default, default, 0, 0, 0, 0, scaledAdvance);
        }

        byte[] coverage = new byte[width * height];
        fixed (byte* pixels = coverage)
        {
            StbTrueType.stbtt_MakeCodepointBitmap(face.Font, pixels, width, height, width, face.Scale, face.Scale, character);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SetPixel(atlasX + x, atlasY + y, coverage[y * width + x]);
            }
        }

        Version++;
        return new Glyph(
            new Vector2(atlasX, atlasY) / Size,
            new Vector2(atlasX + width, atlasY + height) / Size,
            width,
            height,
            left,
            top,
            scaledAdvance);
    }

    /// <summary>Finds room on the current row, and starts a new row when it's full.</summary>
    private bool TryReserve(int width, int height, out int x, out int y)
    {
        if (_shelfX + width + Padding > Size)
        {
            _shelfY += _shelfHeight + Padding;
            _shelfX = 0;
            _shelfHeight = 0;
        }

        x = _shelfX;
        y = _shelfY;
        if (y + height > Size || x + width > Size)
        {
            return false;
        }

        _shelfX += width + Padding;
        _shelfHeight = Math.Max(_shelfHeight, height);
        return true;
    }

    private void SetPixel(int x, int y, byte alpha)
    {
        int index = (y * Size + x) * 4;
        Pixels[index] = 255;
        Pixels[index + 1] = 255;
        Pixels[index + 2] = 255;
        Pixels[index + 3] = alpha;
    }

    private readonly record struct Line(int Start, int End, float Width);

    private readonly record struct Glyph(Vector2 UvMin, Vector2 UvMax, int Width, int Height, int OffsetX, int OffsetY, float Advance);

    /// <summary>One font at one size.</summary>
    private sealed unsafe class Face
    {
        public Face(StbTrueType.stbtt_fontinfo font, int pixelHeight)
        {
            Font = font;
            Scale = StbTrueType.stbtt_ScaleForPixelHeight(font, pixelHeight);
            int ascent;
            int descent;
            int gap;
            StbTrueType.stbtt_GetFontVMetrics(font, &ascent, &descent, &gap);
            Ascent = MathF.Round(ascent * Scale);
            LineHeight = MathF.Ceiling((ascent - descent + gap) * Scale);
        }

        public StbTrueType.stbtt_fontinfo Font { get; }

        public float Scale { get; }

        public float Ascent { get; }

        public float LineHeight { get; }

        public float DigitAdvance { get; set; }

        public Dictionary<char, Glyph> Glyphs { get; } = [];
    }
}
