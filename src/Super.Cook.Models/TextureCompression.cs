using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Rendition;

namespace Super.Cook.Models;

/// <summary>
/// Turns a cooked model's RGBA8 textures into block-compressed ones, which take a quarter of the
/// memory or less and are read faster. Each is compressed the way its use suits: normal maps
/// keep only red and green, as BC5, height maps only red, as BC4, and everything else all four
/// channels, as BC7.
/// </summary>
internal static class TextureCompression
{
    /// <summary>
    /// Replaces each of <paramref name="textures"/> with its compressed form, judging its use
    /// from the materials that point to it. A texture no material points to stays as it is.
    /// <paramref name="masks"/> are the materials' baked masks, which keep all four channels.
    /// </summary>
    public static void Compress(TextureData[] textures, IReadOnlyList<ModelMaterial> materials, IEnumerable<int>? masks = null)
    {
        var formats = new TextureFormat?[textures.Length];
        foreach (int mask in masks ?? [])
        {
            Use(formats, mask, TextureFormat.Bc7);
        }

        foreach (var material in materials)
        {
            Use(formats, material.NormalTexture, TextureFormat.Bc5);
            Use(formats, material.DetailNormalTexture, TextureFormat.Bc5);
            Use(formats, material.HeightTexture, TextureFormat.Bc4);
            Use(formats, material.BaseColorTexture, TextureFormat.Bc7);
            Use(formats, material.SurfaceTexture, TextureFormat.Bc7);
            Use(formats, material.EmissiveTexture, TextureFormat.Bc7);
            Use(formats, material.DetailColorTexture, TextureFormat.Bc7);
        }

        Parallel.For(0, textures.Length, i =>
        {
            if (formats[i] is { } format && textures[i].Format == TextureFormat.Rgba8)
            {
                textures[i] = Shared(textures[i], format);
            }
        });
    }

    /// <summary>
    /// Each texture's compressed forms, by the texture they were made from. The models cooker hands
    /// every model of a kit the same decoded texture, so a shared atlas is compressed once, not once
    /// per model, and the compressed copy is the same object, so it's also written and hashed once.
    /// </summary>
    private static readonly ConditionalWeakTable<TextureData, ConcurrentDictionary<TextureFormat, Lazy<TextureData>>> Compressed = new();

    private static TextureData Shared(TextureData texture, TextureFormat format) =>
        Compressed.GetValue(texture, _ => new ConcurrentDictionary<TextureFormat, Lazy<TextureData>>())
            .GetOrAdd(format, _ => new Lazy<TextureData>(() => Compress(texture, format)))
            .Value;

    /// <summary>Compresses every level of an RGBA8 texture into <paramref name="format"/>.</summary>
    public static TextureData Compress(TextureData texture, TextureFormat format)
    {
        if (texture.Format != TextureFormat.Rgba8)
        {
            throw new ArgumentException($"Only an RGBA8 texture can be compressed, not a {texture.Format} one.", nameof(texture));
        }

        var encoder = new BcEncoder(format switch
        {
            TextureFormat.Bc7 => CompressionFormat.Bc7,
            TextureFormat.Bc5 => CompressionFormat.Bc5,
            TextureFormat.Bc4 => CompressionFormat.Bc4,
            _ => throw new ArgumentException($"{format} isn't a compressed format.", nameof(format)),
        });
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;

        var mips = new byte[texture.Mips.Length][];
        for (int level = 0; level < mips.Length; level++)
        {
            int width = Math.Max(texture.Width >> level, 1);
            int height = Math.Max(texture.Height >> level, 1);
            mips[level] = encoder.EncodeToRawBytes(texture.Mips[level], width, height, PixelFormat.Rgba32)[0];
        }

        return new TextureData { Width = texture.Width, Height = texture.Height, Kind = texture.Kind, Format = format, Mips = mips };
    }

    // A texture with more than one use gets the format that keeps everything, BC7, unless
    // every use agrees.
    private static void Use(TextureFormat?[] formats, int texture, TextureFormat format)
    {
        if (texture < 0)
        {
            return;
        }

        formats[texture] = formats[texture] is null || formats[texture] == format ? format : TextureFormat.Bc7;
    }
}
