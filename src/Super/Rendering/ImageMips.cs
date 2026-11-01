namespace Super;

/// <summary>Builds the mip chain for an image of any shape.</summary>
internal static class ImageMips
{
    /// <summary>
    /// Halves RGBA pixels down to one pixel. Each color counts as much as it's solid, so
    /// the invisible color around an icon doesn't bleed into its edges when it's small.
    /// </summary>
    public static byte[][] Build(int width, int height, byte[] pixels)
    {
        var mips = new List<byte[]> { pixels };
        while (width > 1 || height > 1)
        {
            int nextWidth = Math.Max(width / 2, 1);
            int nextHeight = Math.Max(height / 2, 1);
            mips.Add(Halve(mips[^1], width, height, nextWidth, nextHeight));
            width = nextWidth;
            height = nextHeight;
        }

        return mips.ToArray();
    }

    private static byte[] Halve(byte[] source, int width, int height, int nextWidth, int nextHeight)
    {
        byte[] mip = new byte[nextWidth * nextHeight * 4];
        for (int y = 0; y < nextHeight; y++)
        {
            for (int x = 0; x < nextWidth; x++)
            {
                int red = 0;
                int green = 0;
                int blue = 0;
                int alpha = 0;
                for (int offsetY = 0; offsetY < 2; offsetY++)
                {
                    for (int offsetX = 0; offsetX < 2; offsetX++)
                    {
                        int sourceX = Math.Min(x * 2 + offsetX, width - 1);
                        int sourceY = Math.Min(y * 2 + offsetY, height - 1);
                        int at = (sourceY * width + sourceX) * 4;
                        int weight = source[at + 3];
                        red += source[at] * weight;
                        green += source[at + 1] * weight;
                        blue += source[at + 2] * weight;
                        alpha += weight;
                    }
                }

                int to = (y * nextWidth + x) * 4;
                if (alpha > 0)
                {
                    mip[to] = (byte)(red / alpha);
                    mip[to + 1] = (byte)(green / alpha);
                    mip[to + 2] = (byte)(blue / alpha);
                    mip[to + 3] = (byte)(alpha / 4);
                }
            }
        }

        return mip;
    }
}
