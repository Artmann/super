using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BCnEncoder.Decoder;
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

        // The cutoff each cut-out texture's alpha is tested against, as the cooker kept its coverage for, so
        // compression keeps it too. 0 for every other texture.
        var cutoffs = new float[textures.Length];
        foreach (var material in materials)
        {
            float cutoff = ModelCooker.TextureCutoff(material.AlphaCutoff, material.BaseColor.W);
            foreach (int index in (int[])[material.BaseColorTexture, material.SurfaceTexture])
            {
                if (index >= 0 && index < textures.Length && cutoffs[index] == 0f)
                {
                    cutoffs[index] = cutoff;
                }
            }
        }

        int[] wanted = [.. Enumerable.Range(0, textures.Length).Where(i => formats[i] is not null && textures[i].Format == TextureFormat.Rgba8)];
        var keys = new string?[textures.Length];
        if (input is { HasStepCache: true })
        {
            using (input.Time("recall compressed textures"))
            {
                Parallel.ForEach(wanted, i =>
                {
                    var form = Form(formats[i]!.Value, cutoffs[i]);
                    if (Known(textures[i], form) is { } known)
                    {
                        textures[i] = known;
                        return;
                    }

                    keys[i] = Key(textures[i], form.Format, cutoff: form.Cutoff);
                    if (input.Recall(Step, keys[i]!) is { } kept && Read(kept, textures[i], form.Format) is { } compressed)
                    {
                        textures[i] = Remember(textures[i], form, compressed);
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
                    textures[i] = Shared(textures[i], Form(formats[i]!.Value, cutoffs[i]));
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
    private static readonly ConditionalWeakTable<TextureData, ConcurrentDictionary<CompressedForm, Lazy<TextureData>>> Compressed = new();

    /// <summary>How a texture is compressed: its format, and for a BC7 cut-out the cutoff whose coverage it keeps.</summary>
    private readonly record struct CompressedForm(TextureFormat Format, float Cutoff);

    // Only BC7 keeps all four channels, so only it keeps a cut-out's coverage.
    private static CompressedForm Form(TextureFormat format, float cutoff) =>
        new(format, format == TextureFormat.Bc7 ? cutoff : 0f);

    private static TextureData Shared(TextureData texture, CompressedForm form)
    {
        var forms = Compressed.GetValue(texture, _ => new ConcurrentDictionary<CompressedForm, Lazy<TextureData>>());
        var made = forms.GetOrAdd(form, _ => new Lazy<TextureData>(() => Compress(texture, form.Format, form.Cutoff)));
        try
        {
            return made.Value;
        }
        catch
        {
            // A Lazy keeps its exception, so a failure stays out of the memo and the next cook tries again.
            forms.TryRemove(new KeyValuePair<CompressedForm, Lazy<TextureData>>(form, made));
            throw;
        }
    }

    /// <summary>The compressed form this process already made of <paramref name="texture"/>, or null.</summary>
    private static TextureData? Known(TextureData texture, CompressedForm form)
    {
        return Compressed.TryGetValue(texture, out var forms) && forms.TryGetValue(form, out var made) && made.IsValueCreated
            ? made.Value
            : null;
    }

    /// <summary>Puts a compressed form taken from the step cache in the memo, or gives the one another model put there first.</summary>
    private static TextureData Remember(TextureData texture, CompressedForm form, TextureData compressed)
    {
        var forms = Compressed.GetValue(texture, _ => new ConcurrentDictionary<CompressedForm, Lazy<TextureData>>());
        return forms.GetOrAdd(form, _ => new Lazy<TextureData>(compressed)).Value;
    }

    /// <summary>
    /// The step cache's key: the hash of every level's pixels, with the size, format, the cutoff whose coverage a BC7
    /// cut-out keeps, and the encoder's version, this build's unless <paramref name="encoderVersion"/> names another.
    /// </summary>
    internal static string Key(TextureData texture, TextureFormat format, string? encoderVersion = null, float cutoff = 0f)
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
            Convert.ToHexString(hash.GetHashAndReset()))
            + (cutoff > 0f ? "|cutoff " + cutoff.ToString("R", CultureInfo.InvariantCulture) : "");
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

    /// <summary>
    /// Compresses every level of an RGBA8 texture into <paramref name="format"/>. With a <paramref name="cutoff"/> above
    /// 0, a BC7 texture's smaller levels keep the share of pixels whose alpha reaches it that they had before
    /// compression, as <see cref="KeepCompressedCoverage"/> says.
    /// </summary>
    public static TextureData Compress(TextureData texture, TextureFormat format, float cutoff = 0f)
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
                int width = Math.Max(texture.Width >> level, 1);
                int height = Math.Max(texture.Height >> level, 1);
                mips[level] = level > 0 && cutoff > 0f && cutoff <= 1f
                    ? KeepCompressedCoverage(texture.Mips[level], width, height, cutoff)
                    : Bc7Encoder.Encode(texture.Mips[level], width, height);
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

    /// <summary>How far <see cref="KeepCompressedCoverage"/> stretches a level's alpha from the cutoff, in the order it tries them.</summary>
    private static readonly double[] CoverageStretches = [2.0, 4.0, 8.0, 16.0];

    /// <summary>
    /// How many pixels a compressed level's count of passing pixels may be off from the level's own: 1% of its pixels,
    /// or one pixel on a level too small for that. BC7 rounds each block's alpha to its own few steps, so a count that
    /// is exactly right is rarely reachable.
    /// </summary>
    internal static int CoverageTolerance(int pixels) => Math.Max(1, pixels / 100);

    /// <summary>
    /// Encodes one level of a cut-out texture as BC7 so that as many of its pixels pass <paramref name="cutoff"/> once
    /// decoded as did before. The cooker kept each level's coverage as it scaled its alpha, but the filter leaves much
    /// of a small level's alpha just either side of the cutoff, and BC7 moves alpha by a few steps, so a thin cut-out
    /// such as leaves or a fence could lose most of its pixels at a distance. When the plain encoding's decoded count is
    /// off by more than <see cref="CoverageTolerance"/>, the level's alpha is stretched away from the cutoff before it
    /// is encoded again, ever further until the count is near enough. The stretch keeps the cutoff where it is, so it
    /// keeps which pixels pass, and where the cut falls between two pixels as the sampler blends them; it only moves
    /// each pixel's alpha further from the cutoff than BC7's error can carry it back. The encoding that comes nearest is
    /// kept, and the plain one wins a tie.
    /// </summary>
    private static byte[] KeepCompressedCoverage(byte[] pixels, int width, int height, float cutoff)
    {
        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        byte[] best = Bc7Encoder.Encode(pixels, width, height, threshold);
        if (threshold <= 0)
        {
            // Every byte passes a cutoff this small, so compression can't change which do.
            return best;
        }

        int target = Passing(pixels, threshold, 3, 4);
        int tolerance = CoverageTolerance(width * height);
        int bestError = Math.Abs(CompressedPassing(best, width, height, threshold) - target);
        foreach (double stretch in CoverageStretches)
        {
            if (bestError <= tolerance)
            {
                break;
            }

            byte[] encoded = Bc7Encoder.Encode(StretchAlpha(pixels, cutoff * 255.0, threshold, stretch), width, height, threshold);
            int error = Math.Abs(CompressedPassing(encoded, width, height, threshold) - target);
            if (error < bestError)
            {
                best = encoded;
                bestError = error;
            }
        }

        return best;
    }

    // Moves every alpha byte stretch times further from the cutoff, in bytes, keeping each on its side of the threshold.
    private static byte[] StretchAlpha(byte[] pixels, double cutoff, int threshold, double stretch)
    {
        var stretched = (byte[])pixels.Clone();
        for (int i = 3; i < stretched.Length; i += 4)
        {
            int alpha = stretched[i];
            int moved = (int)Math.Clamp(Math.Round(cutoff + (alpha - cutoff) * stretch), 0.0, 255.0);
            stretched[i] = (byte)(alpha >= threshold ? Math.Max(moved, threshold) : Math.Min(moved, threshold - 1));
        }

        return stretched;
    }

    // How many of a BC7 level's pixels have alpha at least the threshold once decoded.
    private static int CompressedPassing(byte[] blocks, int width, int height, int threshold)
    {
        var colors = new BcDecoder().DecodeRaw(blocks, width, height, CompressionFormat.Bc7);
        int count = 0;
        foreach (var color in colors)
        {
            if (color.a >= threshold)
            {
                count++;
            }
        }

        return count;
    }

    // How many pixels have alpha at least the threshold, reading the byte at offset in every stride.
    private static int Passing(byte[] pixels, int threshold, int offset, int stride)
    {
        int count = 0;
        for (int i = offset; i < pixels.Length; i += stride)
        {
            if (pixels[i] >= threshold)
            {
                count++;
            }
        }

        return count;
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
