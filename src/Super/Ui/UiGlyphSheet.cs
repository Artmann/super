using System.Numerics;

namespace Super.Ui;

/// <summary>
/// The one image every character is drawn into: white everywhere, with the shapes in alpha.
/// Characters are packed in rows, left to right. A small solid block in the corner is for
/// drawing plain rectangles.
/// </summary>
internal sealed class UiGlyphSheet
{
    /// <summary>The image's width and height. Characters that don't fit are measured but not drawn.</summary>
    public const int Size = 1024;

    private const int Padding = 1;
    private const int SolidBlock = 4;

    private int _shelfX = SolidBlock + Padding;
    private int _shelfY;
    private int _shelfHeight = SolidBlock;

    public UiGlyphSheet()
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

    /// <summary>A spot that's solid, for drawing plain rectangles.</summary>
    public static Vector2 SolidUv { get; } = new Vector2(SolidBlock / 2f) / Size;

    /// <summary>Goes up whenever <see cref="Pixels"/> changes.</summary>
    public int Version { get; private set; }

    public byte[] Pixels { get; } = new byte[Size * Size * 4];

    /// <summary>Finds room on the current row, and starts a new row when it's full.</summary>
    public bool TryReserve(int width, int height, out int x, out int y)
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

    /// <summary>Copies a character's coverage, one byte a pixel, into the room reserved at x and y.</summary>
    public void Write(int x, int y, int width, byte[] coverage)
    {
        int height = coverage.Length / width;
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                SetPixel(x + column, y + row, coverage[row * width + column]);
            }
        }

        Version++;
    }

    private void SetPixel(int x, int y, byte alpha)
    {
        int index = (y * Size + x) * 4;
        Pixels[index] = 255;
        Pixels[index + 1] = 255;
        Pixels[index + 2] = 255;
        Pixels[index + 3] = alpha;
    }
}
