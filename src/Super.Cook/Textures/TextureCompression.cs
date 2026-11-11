using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Rendition;

namespace Super.Cook;

/// <summary>
/// Turns cooked RGBA8 textures into block-compressed ones, which take a quarter of the memory or
/// less and are read faster: a model's textures, a material's parts and plain textures alike. Each
/// is compressed the way its use suits: normal maps keep only red and green, as BC5, height maps
/// only red, as BC4, and everything else all four channels, as BC7. BC7 is written by
/// <see cref="Bc7Encoder"/>, BC4 and BC5 by BCnEncoder.Net.
/// </summary>
internal static class TextureCompression
{
    private const string Step = "compressed-texture";

    /// <summary>
    /// What a step entry holds, in its key so a change misses entries of the old kind: <c>object 1</c> is the hash of the
    /// compressed texture's object in the cook cache, written as the model writes it.
    /// </summary>
    private const string EntryFormat = "object 1";

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
    /// cook compresses only textures whose pixels changed. The entry only names the texture's object,
    /// which the cook cache keeps once among its objects, so the bytes aren't kept twice. A cut-out's textures keep their coverage
    /// as <see cref="Coverage"/> says, and <paramref name="warn"/> hears when two materials disagree on it.
    /// </summary>
    public static void Compress(
        TextureData[] textures,
        IReadOnlyList<ModelMaterial> materials,
        IEnumerable<int>? masks = null,
        CookInput? input = null,
        Action<string>? warn = null)
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

        var coverage = Coverage(textures, materials, warn);
        int[] wanted = [.. Enumerable.Range(0, textures.Length).Where(i => formats[i] is not null && textures[i].Format == TextureFormat.Rgba8)];
        var keys = new string?[textures.Length];
        if (input is { HasStepCache: true })
        {
            using (input.Time("recall compressed textures"))
            {
                Parallel.ForEach(wanted, i =>
                {
                    var form = Form(formats[i]!.Value, coverage[i]);
                    if (Known(textures[i], form) is { } known)
                    {
                        textures[i] = known;
                        return;
                    }

                    keys[i] = Key(textures[i], form.Format, cutoff: form.Cutoff, partner: form.Partner);
                    if (input.RecallObject(Step, keys[i]!) is var (hash, bytes) && Read(bytes, textures[i], form.Format) is { } read)
                    {
                        // The key leaves out the texture's kind, so a texture with these pixels used another way takes its own.
                        bool sameKind = read.Kind == textures[i].Kind;
                        var compressed = sameKind
                            ? read
                            : new TextureData { Width = read.Width, Height = read.Height, Kind = textures[i].Kind, Format = read.Format, Mips = read.Mips };
                        var remembered = Remember(textures[i], form, compressed);
                        if (sameKind && ReferenceEquals(remembered, compressed))
                        {
                            // The kept object is what the model writes for this texture, so it isn't written and hashed again.
                            SerializedTextures.Know(compressed, hash, bytes);
                        }

                        textures[i] = remembered;
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
                    textures[i] = Shared(textures[i], Form(formats[i]!.Value, coverage[i]));
                    made.Add(i);
                }
            });
        }

        if (input is { HasStepCache: true })
        {
            using (input.Time("keep compressed textures"))
            {
                // The entry names the texture's object, which the cache keeps once among its objects, as the model writes it.
                Parallel.ForEach(made.Where(i => keys[i] is not null), i =>
                {
                    var (hash, bytes) = SerializedTextures.Of(textures[i]);
                    input.KeepObject(Step, keys[i]!, hash, bytes);
                });
            }
        }
    }

    /// <summary>
    /// Compresses one texture, every level of it, into <paramref name="format"/>, and returns it written as a cooked
    /// texture, as a material's parts and plain textures are cooked. With <paramref name="input"/> and a cook cache the
    /// step cache keeps it: the cooked texture is the asset the cooker writes, which the cook cache keeps among its
    /// objects anyway, so the entry only names that object, and the blocks are kept once. A later cook with the same
    /// pixels reads the object back and compresses nothing.
    /// </summary>
    public static byte[] CompressCooked(TextureData texture, TextureFormat format, bool normalMap, CookInput? input)
    {
        if (input is not { HasStepCache: true })
        {
            return CookedTexture.From(Compress(texture, format), normalMap).Write();
        }

        string key = CookedKey(texture, format, normalMap);
        if (input.RecallObject(Step, key) is var (_, kept) && FitsCooked(kept, texture, format, normalMap))
        {
            return kept;
        }

        byte[] cooked = CookedTexture.From(Compress(texture, format), normalMap).Write();
        input.KeepObject(Step, key, Convert.ToHexStringLower(SHA256.HashData(cooked)), cooked);
        return cooked;
    }

    /// <summary>
    /// What a step entry of <see cref="CompressCooked"/> holds, in its key: <c>cooked 1</c> is the hash of a cooked
    /// texture's object, which is laid out unlike the model's objects that <see cref="EntryFormat"/> entries name.
    /// </summary>
    private const string CookedEntryFormat = "cooked 1";

    /// <summary>
    /// <see cref="Key"/> for a cooked texture: it also names the kind and whether it's a normal map, since the cooked
    /// texture holds both, so the object an entry names is exactly the one the cooker writes.
    /// </summary>
    internal static string CookedKey(TextureData texture, TextureFormat format, bool normalMap) => string.Join('|',
        CookedEntryFormat,
        texture.Kind.ToString(),
        normalMap ? "normal" : "plain",
        Key(texture, format));

    /// <summary>Whether <paramref name="kept"/> is a cooked texture of <paramref name="texture"/>'s size, kind and levels in <paramref name="format"/>.</summary>
    private static bool FitsCooked(byte[] kept, TextureData texture, TextureFormat format, bool normalMap)
    {
        CookedTexture read;
        try
        {
            read = CookedTexture.Read(kept, Step);
        }
        catch (ContentException)
        {
            return false;
        }

        return read.Width == texture.Width && read.Height == texture.Height && read.Kind == texture.Kind
            && read.NormalMap == normalMap && read.Format == format && read.Mips.Length == texture.Mips.Length;
    }

    /// <summary>
    /// The cutoff as a texture's own alpha meets it: the shader cuts by the texture's alpha times the colour's
    /// <paramref name="opacity"/>, so the texture's alpha must reach <paramref name="cutoff"/> over that. 0, for no
    /// cutoff or for one the texture can't reach, leaves the texture's mips as the filter made them.
    /// </summary>
    internal static float TextureCutoff(float cutoff, float opacity)
    {
        if (cutoff <= 0f || opacity <= 0f || cutoff > opacity)
        {
            return 0f;
        }

        return cutoff / opacity;
    }

    /// <summary>
    /// Each texture's compressed forms, by the texture they were made from. The models cooker hands
    /// every model of a kit the same decoded texture, so a shared atlas is compressed once, not once
    /// per model, and the compressed copy is the same object, so it's also written and hashed once.
    /// </summary>
    private static readonly ConditionalWeakTable<TextureData, ConcurrentDictionary<CompressedForm, Lazy<TextureData>>> Compressed = new();

    /// <summary>
    /// How a texture is compressed: its format, and for a BC7 cut-out the cutoff whose coverage it keeps, and the
    /// surface texture, uncompressed, whose alpha it's multiplied by when the shader tests it, if any.
    /// </summary>
    private readonly record struct CompressedForm(TextureFormat Format, float Cutoff, TextureData? Partner);

    /// <summary>The coverage a cut-out's texture keeps: none, its own alpha's, or its alpha times a partner's.</summary>
    internal readonly record struct CoverageClaim(float Cutoff, TextureData? Partner);

    // Only BC7 keeps all four channels, so only it keeps a cut-out's coverage.
    private static CompressedForm Form(TextureFormat format, CoverageClaim? claim) =>
        format == TextureFormat.Bc7 && claim is { } c ? new(format, c.Cutoff, c.Partner) : new(format, 0f, null);

    /// <summary>
    /// The coverage each texture keeps through compression, as the cooker kept it. The shader cuts where the colour's
    /// alpha times the surface's falls below the cutoff. When both carry partial alpha, the colour keeps the product's
    /// coverage against the surface, and the surface is left whole, so compressing it never moves the product. When
    /// only one does, it keeps its own. A texture with no cut-out material keeps nothing. When two cut-out materials
    /// want different coverage from one texture, the first one's is kept and <paramref name="warn"/> says so.
    /// </summary>
    internal static CoverageClaim?[] Coverage(TextureData[] textures, IReadOnlyList<ModelMaterial> materials, Action<string>? warn)
    {
        var claims = new CoverageClaim?[textures.Length];
        var claimedBy = new int[textures.Length];
        for (int m = 0; m < materials.Count; m++)
        {
            var material = materials[m];
            float cutoff = TextureCutoff(material.AlphaCutoff, material.BaseColor.W);
            if (!(cutoff > 0f))
            {
                continue;
            }

            int color = material.BaseColorTexture;
            int surface = material.SurfaceTexture;
            var partner = HasPartialAlpha(textures, color) && HasPartialAlpha(textures, surface) && color != surface
                ? textures[surface]
                : null;
            Claim(color, new CoverageClaim(cutoff, partner), m);
            Claim(surface, new CoverageClaim(partner is null ? cutoff : 0f, null), m);
        }

        return claims;

        void Claim(int index, CoverageClaim claim, int material)
        {
            if (index < 0 || index >= textures.Length)
            {
                return;
            }

            if (claims[index] is not { } existing)
            {
                claims[index] = claim;
                claimedBy[index] = material;
                return;
            }

            if (existing != claim)
            {
                warn?.Invoke($"texture {index} is cut out differently by materials {claimedBy[index]} and {material} "
                    + $"({Describe(existing)}, then {Describe(claim)}); it keeps the coverage of material {claimedBy[index]}.");
            }
        }

        static string Describe(CoverageClaim claim) => claim.Cutoff <= 0f
            ? "whole, under another texture's alpha"
            : claim.Partner is null
                ? $"cutoff {claim.Cutoff.ToString("0.###", CultureInfo.InvariantCulture)}"
                : $"cutoff {claim.Cutoff.ToString("0.###", CultureInfo.InvariantCulture)} times the surface's alpha";
    }

    private static bool HasPartialAlpha(TextureData[] textures, int index) =>
        index >= 0 && index < textures.Length && textures[index].Format == TextureFormat.Rgba8 && textures[index].AlphaRange().Lowest < 1f;

    private static TextureData Shared(TextureData texture, CompressedForm form)
    {
        var forms = Compressed.GetValue(texture, _ => new ConcurrentDictionary<CompressedForm, Lazy<TextureData>>());
        var made = forms.GetOrAdd(form, _ => new Lazy<TextureData>(() => Compress(texture, form.Format, form.Cutoff, form.Partner)));
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
    internal static string Key(
        TextureData texture,
        TextureFormat format,
        string? encoderVersion = null,
        float cutoff = 0f,
        TextureData? partner = null)
    {
        return string.Join('|',
            EntryFormat,
            encoderVersion ?? EncoderVersion,
            format.ToString(),
            texture.Width.ToString(CultureInfo.InvariantCulture),
            texture.Height.ToString(CultureInfo.InvariantCulture),
            texture.Mips.Length.ToString(CultureInfo.InvariantCulture),
            PixelHash(texture))
            + (cutoff > 0f ? "|cutoff " + cutoff.ToString("R", CultureInfo.InvariantCulture) : "")
            + (cutoff > 0f && partner is not null
                ? $"|partner {partner.Width}x{partner.Height} {PixelHash(partner)}"
                : "");
    }

    private static string PixelHash(TextureData texture)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] mip in texture.Mips)
        {
            hash.AppendData(BitConverter.GetBytes(mip.Length));
            hash.AppendData(mip);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// The compressed form of <paramref name="texture"/> in a kept object, a cooked texture file, or null when it doesn't
    /// fit it: another size, format or number of levels, or a level of the wrong length. Its kind is the one it was
    /// kept with, which may not be the texture's.
    /// </summary>
    private static TextureData? Read(byte[] kept, TextureData texture, TextureFormat format)
    {
        TextureData read;
        try
        {
            read = TextureData.Read(new MemoryStream(kept), Step);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException or IOException)
        {
            return null;
        }

        if (read.Width != texture.Width || read.Height != texture.Height || read.Format != format || read.Mips.Length != texture.Mips.Length)
        {
            return null;
        }

        for (int level = 0; level < read.Mips.Length; level++)
        {
            if (read.Mips[level].Length != TextureData.LevelSize(format, Math.Max(texture.Width >> level, 1), Math.Max(texture.Height >> level, 1)))
            {
                return null;
            }
        }

        return read;
    }

    /// <summary>
    /// Compresses every level of an RGBA8 texture into <paramref name="format"/>. With a <paramref name="cutoff"/> above
    /// 0, a BC7 texture's smaller levels keep the share of pixels whose alpha reaches it that they had before
    /// compression, as <see cref="KeepCompressedCoverage"/> says. With a <paramref name="partner"/> too, the RGBA8
    /// surface texture whose alpha the shader multiplies this one's by, they keep the share whose product reaches it.
    /// </summary>
    public static TextureData Compress(TextureData texture, TextureFormat format, float cutoff = 0f, TextureData? partner = null)
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
                    ? KeepCompressedCoverage(texture.Mips[level], width, height, cutoff,
                        partner is null ? null : TextureData.PartnerAlpha(partner, texture, level))
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
    private static byte[] KeepCompressedCoverage(byte[] pixels, int width, int height, float cutoff, byte[]? partnerAlpha)
    {
        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);

        // Each pixel's own least passing byte and the cutoff it's stretched from, in bytes. Over a partner, the
        // shader tests the product, so a pixel passes from cutoff / partner on, and over a clear partner never.
        var thresholds = new int[width * height];
        var centres = new double[width * height];
        for (int i = 0; i < thresholds.Length; i++)
        {
            byte partner = partnerAlpha?[i] ?? 255;
            thresholds[i] = partnerAlpha is null ? threshold : TextureData.ProductThreshold(cutoff, partner);
            centres[i] = partner == 0 ? double.NaN : cutoff * 255.0 * 255.0 / partner;
        }

        // The encoder's own cut-out hint knows one threshold only, so over a partner it's left out.
        int hint = partnerAlpha is null ? threshold : 0;
        byte[] best = Bc7Encoder.Encode(pixels, width, height, hint);
        if (thresholds.All(t => t <= 0 || t > 255))
        {
            // Every byte passes, or none can, so compression can't change which do.
            return best;
        }

        int target = Passing(pixels, thresholds);
        int tolerance = CoverageTolerance(width * height);
        int bestError = Math.Abs(CompressedPassing(best, width, height, thresholds) - target);
        foreach (double stretch in CoverageStretches)
        {
            if (bestError <= tolerance)
            {
                break;
            }

            byte[] encoded = Bc7Encoder.Encode(StretchAlpha(pixels, centres, thresholds, stretch), width, height, hint);
            int error = Math.Abs(CompressedPassing(encoded, width, height, thresholds) - target);
            if (error < bestError)
            {
                best = encoded;
                bestError = error;
            }
        }

        return best;
    }

    // Moves every alpha byte stretch times further from its pixel's cutoff, in bytes, keeping each on its side of the
    // pixel's threshold. A pixel that can't pass, or always does, keeps its alpha.
    private static byte[] StretchAlpha(byte[] pixels, double[] centres, int[] thresholds, double stretch)
    {
        var stretched = (byte[])pixels.Clone();
        for (int p = 0; p < thresholds.Length; p++)
        {
            int threshold = thresholds[p];
            if (threshold <= 0 || threshold > 255)
            {
                continue;
            }

            int alpha = stretched[p * 4 + 3];
            int moved = (int)Math.Clamp(Math.Round(centres[p] + (alpha - centres[p]) * stretch), 0.0, 255.0);
            stretched[p * 4 + 3] = (byte)(alpha >= threshold ? Math.Max(moved, threshold) : Math.Min(moved, threshold - 1));
        }

        return stretched;
    }

    // How many of a BC7 level's pixels have alpha at least their threshold once decoded.
    private static int CompressedPassing(byte[] blocks, int width, int height, int[] thresholds)
    {
        var colors = new BcDecoder().DecodeRaw(blocks, width, height, CompressionFormat.Bc7);
        int count = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i].a >= thresholds[i])
            {
                count++;
            }
        }

        return count;
    }

    // How many RGBA8 pixels have alpha at least their threshold.
    private static int Passing(byte[] pixels, int[] thresholds)
    {
        int count = 0;
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (pixels[i * 4 + 3] >= thresholds[i])
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
