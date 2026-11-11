using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Rendition;
using StbImageSharp;

namespace Super.Cook.Models;

/// <summary>
/// Decodes a model's images, builds their mip chains, and removes duplicates. Images are
/// read through <see cref="CookInput"/>, so a change to one cooks the model again.
/// </summary>
internal sealed class ModelTextures(CookInput input, CookOutput output, string prefix)
{
    private readonly List<TextureData> _textures = [];
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _embedded = [];
    private readonly HashSet<int> _widened = [];

    public int Count => _textures.Count;

    public TextureData[] ToArray() => _textures.ToArray();

    /// <summary>Registers an encoded image stored inside the model file, under the name its materials use.</summary>
    public void AddEmbedded(string name, byte[] encoded) => _embedded[name] = encoded;

    /// <summary>
    /// Returns the index of the cooked texture, or -1 if there is none. A texture the model
    /// file links to that isn't there is a warning. One named in the settings
    /// (<paramref name="namedBy"/> is the material) is an error, since it's a mistake in them.
    /// With an <paramref name="alphaCutoff"/> above 0, the smaller levels keep the share of
    /// pixels whose alpha passes it, as <see cref="TextureData.KeepAlphaCoverage(TextureData, float)"/> does. With a
    /// <paramref name="partner"/> too, they keep the share whose alpha times the partner's passes, as
    /// <see cref="CutoutPartner"/> explains.
    /// </summary>
    public int Add(
        string? fileName,
        TextureKind kind,
        bool isNormalMap = false,
        bool isGltfSurface = false,
        string? alphaFile = null,
        string? namedBy = null,
        float alphaCutoff = 0f,
        CoveragePartner? partner = null)
    {
        if (fileName is null)
        {
            return -1;
        }

        string key = $"{fileName}|{kind}|{isGltfSurface}|{alphaFile}|{CutoffKey(alphaCutoff)}|{partner?.Key}";
        if (_indices.TryGetValue(key, out int existing))
        {
            return existing;
        }

        byte[]? encoded = Read(fileName);
        if (encoded is null)
        {
            if (namedBy is not null)
            {
                output.Error($"{prefix}material {namedBy}: the texture {fileName} isn't beside the model or in a folder under it.");
            }
            else
            {
                output.Warning($"{prefix}missing texture {fileName}");
            }

            return -1;
        }

        byte[]? alphaEncoded = null;
        if (alphaFile is not null)
        {
            alphaEncoded = Read(alphaFile);
            if (alphaEncoded is null)
            {
                output.Error($"{prefix}The alpha texture {alphaFile} isn't next to the model.");
                return -1;
            }
        }

        _textures.Add(MakeShared(encoded, alphaEncoded, kind, isNormalMap, isGltfSurface, alphaCutoff, partner));
        _indices[key] = _textures.Count - 1;
        return _textures.Count - 1;
    }

    /// <summary>
    /// What a cut-out material's colour texture keeps its coverage against when its surface texture carries opacity
    /// too, or null when it needn't. The shader cuts where the colour's alpha times the surface's falls below the
    /// cutoff, and keeping each one's coverage on its own doesn't keep the product's. So when both have partial alpha
    /// the colour is scaled against the surface, and the surface is cooked whole, without a cutoff. When either is
    /// opaque, the product is the other one's alpha, and each keeping its own coverage is exact. The files are read
    /// and filtered as <see cref="Add"/> would, and shared with it, but nothing is added.
    /// </summary>
    public CoveragePartner? CutoutPartner(string? colorFile, string? alphaFile, string? surfaceFile)
    {
        if (colorFile is null || surfaceFile is null)
        {
            return null;
        }

        byte[]? color = Read(colorFile);
        byte[]? alpha = alphaFile is null ? null : Read(alphaFile);
        byte[]? surface = Read(surfaceFile);
        if (color is null || surface is null || (alphaFile is not null && alpha is null))
        {
            // Add reports what's missing.
            return null;
        }

        var surfaceTexture = MakeShared(surface, null, TextureKind.Linear, false, false, 0f, null);
        if (surfaceTexture.AlphaRange().Lowest >= 1f)
        {
            return null;
        }

        var colorTexture = MakeShared(color, alpha, TextureKind.Color, false, false, 0f, null);
        if (colorTexture.AlphaRange().Lowest >= 1f)
        {
            return null;
        }

        return new CoveragePartner(Convert.ToHexString(SHA256.HashData(surface)), surfaceTexture);
    }

    // Models in a kit share their images, so each image is decoded and filtered once per cook, not once per model.
    private TextureData MakeShared(
        byte[] encoded,
        byte[]? alphaEncoded,
        TextureKind kind,
        bool isNormalMap,
        bool isGltfSurface,
        float alphaCutoff,
        CoveragePartner? partner)
    {
        string shared = string.Join('|', Convert.ToHexString(SHA256.HashData(encoded)),
            alphaEncoded is null ? "" : Convert.ToHexString(SHA256.HashData(alphaEncoded)), kind, isNormalMap, isGltfSurface,
            CutoffKey(alphaCutoff), partner?.Key);
        bool made = false;
        var texture = Shared.GetOrAdd(shared, _ => new Lazy<TextureData>(() =>
        {
            made = true;
            return KeepCoverage(Make(encoded, alphaEncoded, kind, isNormalMap, isGltfSurface), alphaCutoff, partner);
        })).Value;
        if (made)
        {
            Remember(texture);
        }

        return texture;
    }

    /// <summary>
    /// Adds a texture from its RGBA8 pixels, row by row, such as one a material graph made, and returns its index.
    /// Pixels added again under the same <paramref name="key"/> and <paramref name="alphaCutoff"/> give the first one's
    /// index. The cutoff works as in <see cref="Add"/>.
    /// </summary>
    public int AddPixels(string key, int width, int height, TextureKind kind, byte[] rgba, bool isNormalMap = false, float alphaCutoff = 0f)
    {
        // A file's key always holds a '|', and this one starts with one, so the two never meet.
        key = $"|pixels|{key}|{CutoffKey(alphaCutoff)}";
        if (_indices.TryGetValue(key, out int existing))
        {
            return existing;
        }

        TextureData texture;
        using (input.Time("build mips"))
        {
            texture = TextureData.FromPixels(width, height, kind, rgba, isNormalMap);
        }

        _textures.Add(KeepCoverage(texture, alphaCutoff, partner: null));
        _indices[key] = _textures.Count - 1;
        return _textures.Count - 1;
    }

    // A texture without a cutoff keeps the mips the filter made, byte for byte.
    private TextureData KeepCoverage(TextureData texture, float alphaCutoff, CoveragePartner? partner)
    {
        if (alphaCutoff <= 0f)
        {
            return texture;
        }

        using (input.Time("keep alpha coverage"))
        {
            return partner is null
                ? TextureData.KeepAlphaCoverage(texture, alphaCutoff)
                : TextureData.KeepAlphaCoverage(texture, alphaCutoff, partner.Texture);
        }
    }

    private static string CutoffKey(float alphaCutoff) =>
        alphaCutoff > 0f ? alphaCutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Decoded and filtered images, by the hash of their bytes and how they're cooked, shared by every model of a cook
    /// and the cooks after it in the same process. Cooking is a pure function of the bytes, so a hit is always right.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<TextureData>> Shared = new(StringComparer.Ordinal);

    private static long _sharedBytes;

    /// <summary>
    /// Adds a texture to the output as an object of its own and returns its hash, for the model to point to.
    /// A texture several models share is written and hashed once, by <see cref="SerializedTextures"/>.
    /// </summary>
    public static string Share(TextureData texture, CookOutput output)
    {
        var (hash, bytes) = SerializedTextures.Of(texture);
        return output.AddObject(bytes, hash);
    }

    /// <summary>The most decoded pixels kept for sharing. Past this everything is dropped and sharing starts again.</summary>
    private const long MaxSharedBytes = 1L << 30;

    private static void Remember(TextureData texture)
    {
        long bytes = texture.Mips.Sum(mip => (long)mip.Length);
        if (Interlocked.Add(ref _sharedBytes, bytes) > MaxSharedBytes)
        {
            Shared.Clear();
            Interlocked.Exchange(ref _sharedBytes, 0);
        }
    }

    private TextureData Make(byte[] encoded, byte[]? alphaEncoded, TextureKind kind, bool isNormalMap, bool isGltfSurface)
    {
        ImageResult image;
        using (input.Time("decode images"))
        {
            image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        }

        if (isGltfSurface)
        {
            ConvertGltfSurface(image.Data);
        }

        if (alphaEncoded is not null)
        {
            using (input.Time("decode images"))
            {
                var alpha = ImageResult.FromMemory(alphaEncoded, ColorComponents.RedGreenBlueAlpha);
                TakeAlpha(image, alpha);
            }
        }

        using (input.Time("build mips"))
        {
            return TextureData.FromPixels(image.Width, image.Height, kind, image.Data, isNormalMap);
        }
    }

    /// <summary>
    /// Makes the surface texture's smaller levels rougher where the normal map's bumps are too
    /// small to see at that level, so the material doesn't turn shiny in the distance. See
    /// <see cref="TextureData.WidenRoughness"/>. Each surface texture is widened once, by the
    /// first normal map it's used with.
    /// </summary>
    public void WidenRoughness(int surface, int normal)
    {
        if (surface < 0 || normal < 0 || !_widened.Add(surface))
        {
            return;
        }

        _textures[surface] = TextureData.WidenRoughness(_textures[surface], _textures[normal]);
    }

    private byte[]? Read(string fileName)
    {
        if (_embedded.TryGetValue(fileName, out byte[]? encoded))
        {
            return encoded;
        }

        // A name with a folder in it is a path from the model's folder. A bare name is looked
        // for beside the model, then under it, since glTF files often keep their textures in
        // a folder of their own, such as textures/.
        string? relative = fileName.Contains('/') || fileName.Contains('\\') ? fileName : input.Find(fileName);
        if (relative is not null)
        {
            try
            {
                return input.Read(relative);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }

        // Track it anyway, so adding the file later cooks the model again.
        input.Track(fileName);
        return null;
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

/// <summary>A surface texture a colour texture keeps its cut-out coverage against, and the hash of its file's bytes.</summary>
internal sealed record CoveragePartner(string Key, TextureData Texture);
