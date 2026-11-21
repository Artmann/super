namespace Rendition.Gpu.Tests;

// Reads a band of a captured RGBA picture: the rows from a quarter to three quarters down, and the columns from across to
// until as shares of its width.
internal static class PictureBand
{
    public static void Each(int wide, float across, float until, Action<int, int> each)
    {
        for (int y = wide / 4; y < wide * 3 / 4; y++)
        {
            for (int x = (int)(across * wide); x < (int)(until * wide); x++)
            {
                each(x, y);
            }
        }
    }

    // How many pixels in the band differ between two pictures by more than the dither's two steps in any channel.
    public static int Changed(byte[] a, byte[] b, int wide, float across, float until)
    {
        int changed = 0;
        Each(wide, across, until, (x, y) =>
        {
            int at = (y * wide + x) * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                if (Math.Abs(a[at + channel] - b[at + channel]) > 2)
                {
                    changed++;
                    return;
                }
            }
        });
        return changed;
    }

    public static float Shade(byte[] pixels, int wide, int x, int y)
    {
        int at = (y * wide + x) * 4;
        return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3f;
    }
}
