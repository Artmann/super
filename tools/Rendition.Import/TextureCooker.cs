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
        string? alphaFile = null)
    {
        if (fileName is null)
        {
            return -1;
        }

        string key = $"{fileName}|{kind}|{isGltfSurface}|{alphaFile}";
        if (_indices.TryGetValue(key, out int existing))
        {
            return existing;
        }

        byte[]? encoded = Read(fileName);
        if (encoded is null)
        {
            return -1;
        }

        var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        if (isGltfSurface)
        {
            ConvertGltfSurface(image.Data);
        }

        if (alphaFile is not null)
        {
            byte[] alphaEncoded = Read(alphaFile) ?? throw new FileNotFoundException($"The alpha texture {alphaFile} isn't next to the model.");
            var alpha = ImageResult.FromMemory(alphaEncoded, ColorComponents.RedGreenBlueAlpha);
            TakeAlpha(image, alpha);
        }

        _textures.Add(TextureData.FromPixels(image.Width, image.Height, kind, image.Data, isNormalMap));
        _indices[key] = _textures.Count - 1;
        return _textures.Count - 1;
    }

    private byte[]? Read(string fileName)
    {
        if (_embedded.TryGetValue(fileName, out byte[]? encoded))
        {
            return encoded;
        }

        string path = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(path))
        {
            // glTF files often keep their textures in a folder of their own, such as textures/.
            path = Directory.EnumerateFiles(sourceDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault() ?? path;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"  warning: missing texture {fileName}");
            return null;
        }

        return File.ReadAllBytes(path);
    }

    // Puts the alpha image's red into the colour's alpha, sampling the nearest pixel when
    // the two are different sizes.
    private static void TakeAlpha(ImageResult color, ImageResult alpha)
    {
        for (int y = 0; y < color.Height; y++)
        {
            int ay = y * alpha.Height / color.Height;
            for (int x = 0; x < color.Width; x++)
            {
                int ax = x * alpha.Width / color.Width;
                color.Data[(y * color.Width + x) * 4 + 3] = alpha.Data[(ay * alpha.Width + ax) * 4];
            }
        }
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
}
