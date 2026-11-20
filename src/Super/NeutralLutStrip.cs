namespace Super;

/// <summary>
/// The strip of colours <c>--neutral-lut</c> stamps on a screenshot: a 32 × 32 × 32 colour table laid out as 32
/// squares side by side, red rising to the right within each square, green rising downwards and blue rising from one
/// square to the next. Graded with the picture and saved on its own as a <c>.lut.png</c>, it is the grade as a table.
/// </summary>
internal static class NeutralLutStrip
{
    /// <summary>How many steps each of red, green and blue has.</summary>
    public const int Size = 32;

    /// <summary>The strip's width in pixels: one square of <see cref="Size"/> for each step of blue.</summary>
    public const int Width = Size * Size;

    /// <summary>The strip's height in pixels.</summary>
    public const int Height = Size;

    /// <summary>
    /// A copy of the RGBA <paramref name="pixels"/> with the strip in its top left corner. The picture must be at least
    /// <see cref="Width"/> by <see cref="Height"/> pixels.
    /// </summary>
    public static byte[] Stamped(byte[] pixels, int width, int height)
    {
        if (width < Width || height < Height)
        {
            throw new ArgumentException(
                $"The neutral colour table is {Width} by {Height} pixels, so it doesn't fit a {width} by {height} picture. Take it at least {Width} pixels wide, such as with --size 1280x720.");
        }

        var stamped = (byte[])pixels.Clone();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = ((y * width) + x) * 4;
                stamped[at] = Step(x % Size);
                stamped[at + 1] = Step(y);
                stamped[at + 2] = Step(x / Size);
                stamped[at + 3] = 255;
            }
        }

        return stamped;
    }

    // Step 0 is black and the last is full, as the table's cells are.
    private static byte Step(int step) => (byte)Math.Round(step * 255.0 / (Size - 1), MidpointRounding.AwayFromZero);
}
