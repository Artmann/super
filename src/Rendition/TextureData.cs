using System.Numerics;

namespace Rendition;

/// <summary>An RGBA8 texture with a full mip chain, largest level first.</summary>
public sealed class TextureData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TextureKind Kind { get; init; }
    public required byte[][] Mips { get; init; }

    /// <summary>
    /// Builds the mip chain for an image's RGBA bytes, row by row. Colour is averaged in
    /// linear light, and a normal map's averages are brought back to unit length.
    /// </summary>
    public static TextureData FromPixels(int width, int height, TextureKind kind, byte[] rgba, bool normalMap = false)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"A texture can't be {width}x{height}.", nameof(width));
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"A {width}x{height} texture needs {width * height * 4} bytes, not {rgba.Length}.", nameof(rgba));
        }

        var mips = new List<byte[]> { rgba };
        int levelWidth = width;
        int levelHeight = height;
        while (levelWidth > 1 || levelHeight > 1)
        {
            int nextWidth = Math.Max(levelWidth / 2, 1);
            int nextHeight = Math.Max(levelHeight / 2, 1);
            mips.Add(Downsample(mips[^1], levelWidth, levelHeight, nextWidth, nextHeight, kind, normalMap));
            levelWidth = nextWidth;
            levelHeight = nextHeight;
        }

        return new TextureData { Width = width, Height = height, Kind = kind, Mips = mips.ToArray() };
    }

    private static byte[] Downsample(
        byte[] source, int width, int height, int nextWidth, int nextHeight, TextureKind kind, bool normalMap)
    {
        var result = new byte[nextWidth * nextHeight * 4];
        for (int y = 0; y < nextHeight; y++)
        {
            for (int x = 0; x < nextWidth; x++)
            {
                var sum = Vector4.Zero;
                for (int offsetY = 0; offsetY < 2; offsetY++)
                {
                    for (int offsetX = 0; offsetX < 2; offsetX++)
                    {
                        int sampleX = Math.Min(x * 2 + offsetX, width - 1);
                        int sampleY = Math.Min(y * 2 + offsetY, height - 1);
                        sum += Decode(source, (sampleX + sampleY * width) * 4, kind);
                    }
                }

                var average = sum / 4f;
                if (normalMap)
                {
                    var normal = Vector3.Normalize(new Vector3(average.X, average.Y, average.Z) * 2f - Vector3.One);
                    average = new Vector4(normal * 0.5f + new Vector3(0.5f), average.W);
                }

                Encode(result, (x + y * nextWidth) * 4, average, kind);
            }
        }

        return result;
    }

    // Colour has to be averaged in linear light, or mips come out too dark.
    private static Vector4 Decode(byte[] data, int offset, TextureKind kind)
    {
        var value = new Vector4(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]) / 255f;
        return kind == TextureKind.Color
            ? new Vector4(ToLinear(value.X), ToLinear(value.Y), ToLinear(value.Z), value.W)
            : value;
    }

    private static void Encode(byte[] data, int offset, Vector4 value, TextureKind kind)
    {
        if (kind == TextureKind.Color)
        {
            value = new Vector4(ToSrgb(value.X), ToSrgb(value.Y), ToSrgb(value.Z), value.W);
        }

        data[offset] = (byte)MathF.Round(Math.Clamp(value.X, 0f, 1f) * 255f);
        data[offset + 1] = (byte)MathF.Round(Math.Clamp(value.Y, 0f, 1f) * 255f);
        data[offset + 2] = (byte)MathF.Round(Math.Clamp(value.Z, 0f, 1f) * 255f);
        data[offset + 3] = (byte)MathF.Round(Math.Clamp(value.W, 0f, 1f) * 255f);
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
}
