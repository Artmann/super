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
        bool isGltfSurface = false)
    {
        if (fileName is null)
        {
            return -1;
        }

        string key = $"{fileName}|{kind}|{isGltfSurface}";
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

        _textures.Add(TextureData.FromPixels(image.Width, image.Height, kind, image.Data, isNormalMap));
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
}
