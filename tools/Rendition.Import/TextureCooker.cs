using System.Numerics;
using Rendition;
using StbImageSharp;
using File = System.IO.File;

namespace Rendition.Import;

/// <summary>Decodes source images, builds their mip chains, and removes duplicates.</summary>
public sealed class TextureCooker(string sourceDirectory)
{
    private readonly List<TextureData> _textures = [];
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _embedded = [];

    public int Count => _textures.Count;

    public TextureData[] ToArray() => _textures.ToArray();

    /// <summary>Registers an encoded image stored inside the model file, under the name its materials use.</summary>
    public void AddEmbedded(string name, byte[] encoded) => _embedded[name] = encoded;

    /// <summary>Returns the index of the cooked texture, or -1 if there is none.</summary>
    public int Add(
        string? fileName,
        TextureKind kind,
        bool isNormalMap = false,
        bool isGltfSurface = false,
        bool flipGreen = false)
    {
        if (fileName is null)
        {
            return -1;
        }

        string key = $"{fileName}|{kind}|{isGltfSurface}|{flipGreen}";
        if (_indices.TryGetValue(key, out int existing))
        {
            return existing;
        }

        if (!_embedded.TryGetValue(fileName, out byte[]? encoded))
        {
            string path = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  warning: missing texture {fileName}");
                return -1;
            }

            encoded = File.ReadAllBytes(path);
        }

        var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        if (isGltfSurface)
        {
            ConvertGltfSurface(image.Data);
        }

        if (flipGreen)
        {
            FlipGreen(image.Data);
        }

        var mips = BuildMips(image.Data, image.Width, image.Height, kind, isNormalMap);

        _textures.Add(new TextureData { Width = image.Width, Height = image.Height, Kind = kind, Mips = mips });
        _indices[key] = _textures.Count - 1;
        return _textures.Count - 1;
    }

    // glTF keeps metalness in blue and has no opacity here. Ours is metalness in red and
    // opacity in alpha, while roughness is in green for both.
    private static void ConvertGltfSurface(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 2];
            pixels[i + 3] = 255;
        }
    }

    private static void FlipGreen(byte[] pixels)
    {
        for (int i = 1; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(255 - pixels[i]);
        }
    }

    private static byte[][] BuildMips(byte[] top, int width, int height, TextureKind kind, bool isNormalMap)
    {
        var mips = new List<byte[]> { top };
        while (width > 1 || height > 1)
        {
            int nextWidth = Math.Max(width / 2, 1);
            int nextHeight = Math.Max(height / 2, 1);
            mips.Add(Downsample(mips[^1], width, height, nextWidth, nextHeight, kind, isNormalMap));
            width = nextWidth;
            height = nextHeight;
        }

        return mips.ToArray();
    }

    private static byte[] Downsample(
        byte[] source, int width, int height, int nextWidth, int nextHeight, TextureKind kind, bool isNormalMap)
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
                if (isNormalMap)
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
