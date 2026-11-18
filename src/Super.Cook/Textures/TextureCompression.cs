using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
    /// Names the code that compresses, for the step cache's keys: this library's code (<see cref="CodeId"/>), which holds
    /// <see cref="Bc7Encoder"/>, BCnEncoder.Net's version and the quality it runs at.
    /// </summary>
    private static readonly string EncoderVersion = EncoderVersionFor(Bc7Encoder.Version, Quality);

    /// <summary>The encoder's version as <see cref="EncoderVersion"/> names it, for a given <see cref="Bc7Encoder.Version"/> and BCnEncoder.Net quality.</summary>
    internal static string EncoderVersionFor(int bc7Version, CompressionQuality quality) => string.Join(',',
        CodeId.Of(typeof(TextureCompression).Assembly).ToString("N"),
        bc7Version.ToString(CultureInfo.InvariantCulture),
        typeof(BcEncoder).Assembly.GetName().Version?.ToString() ?? "",
        quality.ToString());

    /// <summary>
    /// Replaces each of <paramref name="textures"/> with its compressed form, judging its use
    /// from the materials that point to it. A texture no material points to stays as it is.
    /// <paramref name="extra"/> are the textures a material names outside <see cref="ModelMaterial"/>, with the format
    /// each wants: BC7 for the baked masks, which keep all four channels, and BC4 for occlusion, which is red alone.
    /// With <paramref name="input"/> and a cook cache, each compressed texture is kept in the step
    /// cache by the hash of its pixels, its size and format and the encoder's version, so a later
    /// cook compresses only textures whose pixels changed. The entry only names the texture's object,
    /// which the cook cache keeps once among its objects, so the bytes aren't kept twice. A cut-out's textures keep their coverage
    /// as <see cref="CoverageClaims"/> says, and <paramref name="warn"/> hears when two materials disagree on it.
    /// </summary>
    public static void Compress(
        TextureData[] textures,
        IReadOnlyList<ModelMaterial> materials,
        IEnumerable<(int Texture, TextureFormat Format)>? extra = null,
        CookInput? input = null,
        Action<string>? warn = null)
    {
        var formats = Formats(textures.Length, materials, extra);
        var coverage = CoverageClaims.Of(textures, materials, warn);
        var forms = formats.Select((format, i) => format is { } known ? Form(known, coverage[i]) : default).ToArray();
        int[] wanted = [.. Enumerable.Range(0, textures.Length).Where(i => formats[i] is not null && textures[i].Format == TextureFormat.Rgba8)];
        var keys = input is { StepResults.HasCache: true } ? Recall(textures, wanted, forms, input) : new string?[textures.Length];

        var made = new ConcurrentBag<int>();
        using (input?.Time("compress textures"))
        {
            Parallel.ForEach(wanted, i =>
            {
                if (textures[i].Format == TextureFormat.Rgba8)
                {
                    textures[i] = Shared(textures[i], forms[i]);
                    made.Add(i);
                }
            });
        }

        if (input is { StepResults.HasCache: true })
        {
            using (input.Time("keep compressed textures"))
            {
                // The entry names the texture's object, which the cache keeps once among its objects, as the model writes it.
                Parallel.ForEach(made.Where(i => keys[i] is not null), i =>
                {
                    var (hash, bytes) = SerializedTextures.Of(textures[i]);
                    input.StepResults.KeepObject(Step, keys[i]!, hash, bytes);
                });
            }
        }
    }

    /// <summary>
    /// The format each texture is compressed into, judged from the materials that point to it and <paramref name="extra"/>,
    /// or null for a texture nothing points to.
    /// </summary>
    private static TextureFormat?[] Formats(int count, IReadOnlyList<ModelMaterial> materials, IEnumerable<(int Texture, TextureFormat Format)>? extra)
    {
        var formats = new TextureFormat?[count];
        foreach (var (texture, format) in extra ?? [])
        {
            Use(formats, texture, format);
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

        return formats;
    }

    /// <summary>
    /// Replaces each of the <paramref name="wanted"/> textures with its compressed form where this process or the step
    /// cache already has it. Gives the step cache's key for each of the others, to keep it under once it's made.
    /// </summary>
    private static string?[] Recall(TextureData[] textures, int[] wanted, CompressedForm[] forms, CookInput input)
    {
        var keys = new string?[textures.Length];
        using (input.Time("recall compressed textures"))
        {
            Parallel.ForEach(wanted, i =>
            {
                if (Recall(textures[i], forms[i], input, out keys[i]) is { } recalled)
                {
                    textures[i] = recalled;
                }
            });
        }

        return keys;
    }

    /// <summary>
    /// The compressed <paramref name="form"/> of <paramref name="texture"/> as this process or the step cache already has
    /// it, or null when neither has, with the step cache's <paramref name="key"/> to keep it under once it's made.
    /// </summary>
    private static TextureData? Recall(TextureData texture, CompressedForm form, CookInput input, out string? key)
    {
        key = null;
        if (Known(texture, form) is { } known)
        {
            return known;
        }

        key = Key(texture, form.Format, cutoff: form.Cutoff, partner: form.Partner);
        if (input.StepResults.RecallObject(Step, key) is not var (hash, bytes) || Read(bytes, texture, form.Format) is not { } read)
        {
            return null;
        }

        // The key leaves out the texture's kind, so a texture with these pixels used another way takes its own.
        bool sameKind = read.Kind == texture.Kind;
        var compressed = sameKind
            ? read
            : new TextureData { Width = read.Width, Height = read.Height, Kind = texture.Kind, Format = read.Format, Mips = read.Mips };
        var remembered = Remember(texture, form, compressed);
        if (sameKind && ReferenceEquals(remembered, compressed))
        {
            // The kept object is what the model writes for this texture, so it isn't written and hashed again.
            SerializedTextures.Know(compressed, hash, bytes);
        }

        key = null;
        return remembered;
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
        if (input is not { StepResults.HasCache: true })
        {
            return CookedTexture.From(Compress(texture, format), normalMap).Write();
        }

        string key = CookedKey(texture, format, normalMap);
        if (input.StepResults.RecallObject(Step, key) is var (_, kept) && FitsCooked(kept, texture, format, normalMap))
        {
            return kept;
        }

        byte[] cooked = CookedTexture.From(Compress(texture, format), normalMap).Write();
        input.StepResults.KeepObject(Step, key, Convert.ToHexStringLower(SHA256.HashData(cooked)), cooked);
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

    // Only BC7 keeps all four channels, so only it keeps a cut-out's coverage.
    private static CompressedForm Form(TextureFormat format, CoverageClaim? claim) =>
        format == TextureFormat.Bc7 && claim is { } c ? new(format, c.Cutoff, c.Partner) : new(format, 0f, null);

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
    /// compression, as <see cref="CutoutEncoder"/> says. With a <paramref name="partner"/> too, the RGBA8
    /// surface texture whose alpha the shader multiplies this one's by, they keep the share whose product reaches it.
    /// </summary>
    public static TextureData Compress(TextureData texture, TextureFormat format, float cutoff = 0f, TextureData? partner = null)
    {
        if (texture.Format != TextureFormat.Rgba8)
        {
            throw new ArgumentException($"Only an RGBA8 texture can be compressed, not a {texture.Format} one.", nameof(texture));
        }

        byte[][] mips;
        if (format == TextureFormat.Bc7)
        {
            mips = EachLevel(texture, (pixels, width, height, level) => level > 0 && cutoff > 0f && cutoff <= 1f
                ? CutoutEncoder.Encode(pixels, width, height, cutoff, partner is null ? null : TextureData.PartnerAlpha(partner, texture, level))
                : Bc7Encoder.Encode(pixels, width, height));
        }
        else
        {
            var encoder = Bc4Or5Encoder(format);
            mips = EachLevel(texture, (pixels, width, height, _) => encoder.EncodeToRawBytes(pixels, width, height, PixelFormat.Rgba32)[0]);
        }

        return new TextureData { Width = texture.Width, Height = texture.Height, Kind = texture.Kind, Format = format, Mips = mips };
    }

    /// <summary>Encodes each level of <paramref name="texture"/>, given its pixels, its width and height, and its index.</summary>
    private static byte[][] EachLevel(TextureData texture, Func<byte[], int, int, int, byte[]> encode)
    {
        var mips = new byte[texture.Mips.Length][];
        for (int level = 0; level < mips.Length; level++)
        {
            int width = Math.Max(texture.Width >> level, 1);
            int height = Math.Max(texture.Height >> level, 1);
            mips[level] = encode(texture.Mips[level], width, height, level);
        }

        return mips;
    }

    /// <summary>BCnEncoder.Net's encoder for BC4 or BC5, one level at a time.</summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is neither.</exception>
    private static BcEncoder Bc4Or5Encoder(TextureFormat format)
    {
        var encoder = new BcEncoder(format switch
        {
            TextureFormat.Bc5 => CompressionFormat.Bc5,
            TextureFormat.Bc4 => CompressionFormat.Bc4,
            _ => throw new ArgumentException($"{format} isn't a compressed format.", nameof(format)),
        });
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = Quality;
        return encoder;
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
