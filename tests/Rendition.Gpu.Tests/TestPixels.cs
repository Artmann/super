using System.Numerics;

namespace Rendition.Gpu.Tests;

// Reads colours out of a captured RGBA8 frame.
internal static class TestPixels
{
    // The mean colour of the square 2 × radius pixels across round a pixel, in 0 to 255, in a frame size pixels wide.
    public static Vector3 Mean(byte[] pixels, int size, int centreX, int centreY, int radius)
    {
        var sum = Vector3.Zero;
        int count = 0;
        for (int y = centreY - radius; y < centreY + radius; y++)
        {
            for (int x = centreX - radius; x < centreX + radius; x++)
            {
                int at = (y * size + x) * 4;
                sum += new Vector3(pixels[at], pixels[at + 1], pixels[at + 2]);
                count++;
            }
        }

        return sum / count;
    }

    // The mean of a square a sixteenth of a square frame size pixels across, round a place given as parts of the frame.
    public static Vector3 Around(byte[] pixels, int size, float across, float down)
    {
        int radius = Math.Max(size / 32, 2);
        int x = Math.Clamp((int)(across * size), radius, size - radius);
        int y = Math.Clamp((int)(down * size), radius, size - radius);
        return Mean(pixels, size, x, y, radius);
    }

    public static float Brightness(Vector3 colour) => (colour.X + colour.Y + colour.Z) / 3f;

    public static string Show(Vector3 colour) => $"({colour.X:F0}, {colour.Y:F0}, {colour.Z:F0})";
}
