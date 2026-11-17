using System.Numerics;
using System.Text;

namespace Super.Testing;

/// <summary>Builds small Radiance .hdr images in memory.</summary>
public static class TestHdr
{
    /// <summary>A Radiance .hdr without run-length encoding, each texel the colour <paramref name="color"/> gives at its column and row.</summary>
    public static byte[] Make(int width, int height, Func<int, int, Vector3> color)
    {
        var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                stream.Write(Rgbe(color(x, y)));
            }
        }

        return stream.ToArray();
    }

    /// <summary>A Radiance .hdr of one colour.</summary>
    public static byte[] Make(int width, int height, Vector3 color) => Make(width, height, (_, _) => color);

    private static byte[] Rgbe(Vector3 color)
    {
        float largest = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (largest <= 0f)
        {
            return [0, 0, 0, 0];
        }

        int exponent = (int)MathF.Floor(MathF.Log2(largest)) + 1;
        float scale = 256f / MathF.Pow(2f, exponent);
        return [(byte)(color.X * scale), (byte)(color.Y * scale), (byte)(color.Z * scale), (byte)(exponent + 128)];
    }
}
