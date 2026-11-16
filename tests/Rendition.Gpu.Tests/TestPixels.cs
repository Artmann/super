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

    public static float Brightness(Vector3 colour) => (colour.X + colour.Y + colour.Z) / 3f;

    public static string Show(Vector3 colour) => $"({colour.X:F0}, {colour.Y:F0}, {colour.Z:F0})";
}
