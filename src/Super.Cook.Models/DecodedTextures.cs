using System.Collections.Concurrent;
using System.Security.Cryptography;
using Rendition;
using StbImageSharp;

namespace Super.Cook.Models;

/// <summary>How one texture is cooked from its file's bytes, and an alpha file's when it has one.</summary>
internal sealed record TextureRecipe(
    byte[] Encoded,
    byte[]? AlphaEncoded,
    TextureKind Kind,
    bool IsNormalMap = false,
    bool IsGltfSurface = false,
    float AlphaCutoff = 0f,
    CoveragePartner? Partner = null)
{
    /// <summary>What the recipe makes the same texture as: the hashes of the bytes and how they're cooked.</summary>
    public string SharedKey => string.Join('|', Convert.ToHexString(SHA256.HashData(Encoded)),
        AlphaEncoded is null ? "" : Convert.ToHexString(SHA256.HashData(AlphaEncoded)), Kind, IsNormalMap, IsGltfSurface,
        CutoffKey(AlphaCutoff), Partner?.Key);

    /// <summary>The cutoff as it goes in a key: empty for none, and the exact number otherwise.</summary>
    public static string CutoffKey(float alphaCutoff) =>
        alphaCutoff > 0f ? alphaCutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";
}

/// <summary>
/// Decoded and filtered images, by the hash of their bytes and how they're cooked, shared by every model of a cook
/// and the cooks after it in the same process. Cooking is a pure function of the bytes, so a hit is always right.
/// Models in a kit share their images, so each image is decoded and filtered once per cook, not once per model.
/// </summary>
internal static class DecodedTextures
{
    /// <summary>The most decoded pixels kept for sharing. Past this everything is dropped and sharing starts again.</summary>
    private const long MaxSharedBytes = 1L << 30;

    private static readonly ConcurrentDictionary<string, Lazy<TextureData>> Shared = new(StringComparer.Ordinal);

    private static long _sharedBytes;

    /// <summary>The texture <paramref name="recipe"/> makes, decoded and filtered once and shared after that.</summary>
    public static TextureData Get(TextureRecipe recipe, CookInput input)
    {
        bool made = false;
        var texture = Shared.GetOrAdd(recipe.SharedKey, _ => new Lazy<TextureData>(() =>
        {
            made = true;
            return KeepCoverage(Make(recipe, input), recipe.AlphaCutoff, recipe.Partner, input);
        })).Value;
        if (made)
        {
            Remember(texture);
        }

        return texture;
    }

    // A texture without a cutoff keeps the mips the filter made, byte for byte.
    public static TextureData KeepCoverage(TextureData texture, float alphaCutoff, CoveragePartner? partner, CookInput input)
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

    private static void Remember(TextureData texture)
    {
        long bytes = texture.Mips.Sum(mip => (long)mip.Length);
        if (Interlocked.Add(ref _sharedBytes, bytes) > MaxSharedBytes)
        {
            Shared.Clear();
            Interlocked.Exchange(ref _sharedBytes, 0);
        }
    }

    private static TextureData Make(TextureRecipe recipe, CookInput input)
    {
        ImageResult image;
        using (input.Time("decode images"))
        {
            image = ImageResult.FromMemory(recipe.Encoded, ColorComponents.RedGreenBlueAlpha);
        }

        if (recipe.IsGltfSurface)
        {
            ConvertGltfSurface(image.Data);
        }

        if (recipe.AlphaEncoded is not null)
        {
            using (input.Time("decode images"))
            {
                var alpha = ImageResult.FromMemory(recipe.AlphaEncoded, ColorComponents.RedGreenBlueAlpha);
                TakeAlpha(image, alpha);
            }
        }

        using (input.Time("build mips"))
        {
            return TextureData.FromPixels(image.Width, image.Height, recipe.Kind, image.Data, recipe.IsNormalMap);
        }
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
