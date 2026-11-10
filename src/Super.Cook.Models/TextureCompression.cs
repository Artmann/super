using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Rendition;

namespace Super.Cook.Models;

/// <summary>
/// Turns a cooked model's RGBA8 textures into block-compressed ones, which take a quarter of the
/// memory or less and are read faster. Each is compressed the way its use suits: normal maps
/// keep only red and green, as BC5, height maps only red, as BC4, and everything else all four
/// channels, as BC7. BC7 is written by <see cref="Bc7Encoder"/>, BC4 and BC5 by BCnEncoder.Net.
/// </summary>
internal static class TextureCompression
{
    private const string Step = "compressed-texture";

    /// <summary>How BCnEncoder.Net compresses BC4 and BC5. Fast loses nothing measurable on them against Balanced.</summary>
    private const CompressionQuality Quality = CompressionQuality.Fast;

    /// <summary>
    /// Names the code that compresses, for the step cache's keys: this library's build, which holds
    /// <see cref="Bc7Encoder"/>, BCnEncoder.Net's version and the quality it runs at.
    /// </summary>
    private static readonly string EncoderVersion = EncoderVersionFor(Bc7Encoder.Version, Quality);

    /// <summary>The encoder's version as <see cref="EncoderVersion"/> names it, for a given <see cref="Bc7Encoder.Version"/> and BCnEncoder.Net quality.</summary>
    internal static string EncoderVersionFor(int bc7Version, CompressionQuality quality) => string.Join(',',
        typeof(TextureCompression).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
        bc7Version.ToString(CultureInfo.InvariantCulture),
        typeof(BcEncoder).Assembly.GetName().Version?.ToString() ?? "",
        quality.ToString());

    /// <summary>
    /// Replaces each of <paramref name="textures"/> with its compressed form, judging its use
    /// from the materials that point to it. A texture no material points to stays as it is.
    /// <paramref name="masks"/> are the materials' baked masks, which keep all four channels.
    /// With <paramref name="input"/> and a cook cache, each compressed texture is kept in the step
    /// cache by the hash of its pixels, its size and format and the encoder's version, so a later
    /// cook compresses only textures whose pixels changed.
    /// </summary>
    public static void Compress(TextureData[] textures, IReadOnlyList<ModelMaterial> materials, IEnumerable<int>? masks = null, CookInput? input = null)
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

        int[] wanted = [.. Enumerable.Range(0, textures.Length).Where(i => formats[i] is not null && textures[i].Format == TextureFormat.Rgba8)];
        var keys = new string?[textures.Length];
        if (input is { HasStepCache: true })
        {
            using (input.Time("recall compressed textures"))
            {
                Parallel.ForEach(wanted, i =>
                {
                    var format = formats[i]!.Value;
                    if (Known(textures[i], format) is { } known)
                    {
                        textures[i] = known;
                        return;
                    }

                    keys[i] = Key(textures[i], format);
                    if (input.Recall(Step, keys[i]!) is { } kept && Read(kept, textures[i], format) is { } compressed)
                    {
                        textures[i] = Remember(textures[i], format, compressed);
                        keys[i] = null;
                    }
                });
            }
        }

        var made = new ConcurrentBag<int>();
        using (input?.Time("compress textures"))
        {
            Parallel.ForEach(wanted, i =>
            {
                if (textures[i].Format == TextureFormat.Rgba8)
                {
                    textures[i] = Shared(textures[i], formats[i]!.Value);
                    made.Add(i);
                }
            });
        }

        if (input is { HasStepCache: true })
        {
            using (input.Time("keep compressed textures"))
            {
                Parallel.ForEach(made.Where(i => keys[i] is not null), i => input.Keep(Step, keys[i]!, Write(textures[i])));
            }
        }
    }

    /// <summary>
    /// Each texture's compressed forms, by the texture they were made from. The models cooker hands
    /// every model of a kit the same decoded texture, so a shared atlas is compressed once, not once
    /// per model, and the compressed copy is the same object, so it's also written and hashed once.
    /// </summary>
    private static readonly ConditionalWeakTable<TextureData, ConcurrentDictionary<TextureFormat, Lazy<TextureData>>> Compressed = new();

    private static TextureData Shared(TextureData texture, TextureFormat format)
    {
        var forms = Compressed.GetValue(texture, _ => new ConcurrentDictionary<TextureFormat, Lazy<TextureData>>());
        var form = forms.GetOrAdd(format, _ => new Lazy<TextureData>(() => Compress(texture, format)));
        try
        {
            return form.Value;
        }
        catch
        {
            // A Lazy keeps its exception, so a failure stays out of the memo and the next cook tries again.
            forms.TryRemove(new KeyValuePair<TextureFormat, Lazy<TextureData>>(format, form));
            throw;
        }
    }

    /// <summary>The compressed form this process already made of <paramref name="texture"/>, or null.</summary>
    private static TextureData? Known(TextureData texture, TextureFormat format)
    {
        return Compressed.TryGetValue(texture, out var forms) && forms.TryGetValue(format, out var form) && form.IsValueCreated
            ? form.Value
            : null;
    }

    /// <summary>Puts a compressed form taken from the step cache in the memo, or gives the one another model put there first.</summary>
    private static TextureData Remember(TextureData texture, TextureFormat format, TextureData compressed)
    {
        var forms = Compressed.GetValue(texture, _ => new ConcurrentDictionary<TextureFormat, Lazy<TextureData>>());
        return forms.GetOrAdd(format, _ => new Lazy<TextureData>(compressed)).Value;
    }

    /// <summary>The step cache's key: the hash of every level's pixels, with the size, format and encoder's version, this build's unless <paramref name="encoderVersion"/> names another.</summary>
    internal static string Key(TextureData texture, TextureFormat format, string? encoderVersion = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] mip in texture.Mips)
        {
            hash.AppendData(BitConverter.GetBytes(mip.Length));
            hash.AppendData(mip);
        }

        return string.Join('|',
            encoderVersion ?? EncoderVersion,
            format.ToString(),
            texture.Width.ToString(CultureInfo.InvariantCulture),
            texture.Height.ToString(CultureInfo.InvariantCulture),
            texture.Mips.Length.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static byte[] Write(TextureData compressed)
    {
        var stream = new MemoryStream(compressed.Mips.Sum(mip => mip.Length) + 4 * compressed.Mips.Length + 4);
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(compressed.Mips.Length);
            foreach (byte[] mip in compressed.Mips)
            {
                writer.Write(mip.Length);
                writer.Write(mip);
            }
        }

        return stream.ToArray();
    }

    /// <returns>The compressed form of <paramref name="texture"/> kept in <paramref name="kept"/>, or null when it doesn't fit it.</returns>
    private static TextureData? Read(byte[] kept, TextureData texture, TextureFormat format)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(kept));
            int count = reader.ReadInt32();
            if (count != texture.Mips.Length)
            {
                return null;
            }

            var mips = new byte[count][];
            for (int level = 0; level < count; level++)
            {
                int length = reader.ReadInt32();
                int blocks = (Math.Max(texture.Width >> level, 1) + 3) / 4 * ((Math.Max(texture.Height >> level, 1) + 3) / 4);
                if (length != blocks * (format == TextureFormat.Bc4 ? 8 : 16))
                {
                    return null;
                }

                mips[level] = reader.ReadBytes(length);
                if (mips[level].Length != length)
                {
                    return null;
                }
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return null;
            }

            return new TextureData { Width = texture.Width, Height = texture.Height, Kind = texture.Kind, Format = format, Mips = mips };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Compresses every level of an RGBA8 texture into <paramref name="format"/>.</summary>
    public static TextureData Compress(TextureData texture, TextureFormat format)
    {
        if (texture.Format != TextureFormat.Rgba8)
        {
            throw new ArgumentException($"Only an RGBA8 texture can be compressed, not a {texture.Format} one.", nameof(texture));
        }

        var mips = new byte[texture.Mips.Length][];
        if (format == TextureFormat.Bc7)
        {
            for (int level = 0; level < mips.Length; level++)
            {
                mips[level] = Bc7Encoder.Encode(texture.Mips[level], Math.Max(texture.Width >> level, 1), Math.Max(texture.Height >> level, 1));
            }

            return new TextureData { Width = texture.Width, Height = texture.Height, Kind = texture.Kind, Format = format, Mips = mips };
        }

        var encoder = new BcEncoder(format switch
        {
            TextureFormat.Bc5 => CompressionFormat.Bc5,
            TextureFormat.Bc4 => CompressionFormat.Bc4,
            _ => throw new ArgumentException($"{format} isn't a compressed format.", nameof(format)),
        });
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = Quality;

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
