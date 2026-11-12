using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using Rendition;

namespace Super.Cook;

/// <summary>
/// Rewrites a baked space's pages as block-compressed ones, so they take a fraction of the memory on the GPU: the
/// irradiance pages as BC6H, the direction and mask pages as BC7. The probe atlases stay RGBA16F, since a probe's light is
/// read with a blend between texels that a block would smear. A page whose sides aren't both multiples of 4 stays as the
/// baker wrote it, and so does one that is already compressed, so cooking is safe to repeat. The header, with the hashes
/// the bake is checked against, is written as it was read.
/// </summary>
public sealed class LightBakeCooker : ICooker
{
    // 1: irradiance as BC6H, direction and mask as BC7. Both encoders' versions are folded in, so a change to the bytes either writes cooks every bake again.
    public int Version { get; } = unchecked((1 * 31 + Bc7Encoder.Version) * 31 + Bc6hEncoder.Version);

    public string Id => "lightbake";

    public IReadOnlyList<string> Extensions { get; } = [".lightbake"];

    // A bake holds a few pages of 2048 by 2048 texels, which take a while to encode next to a texture.
    public int StartRank => 1;

    public void Cook(CookInput input, CookOutput output)
    {
        LightBakeFile bake;
        try
        {
            bake = LightBakeFile.Read(input.Bytes);
        }
        catch (InvalidDataException exception)
        {
            output.Error($"{input.Name} is not a light bake this can read: {exception.Message}");
            return;
        }

        byte[] cooked;
        using (input.Time("compress bake"))
        {
            cooked = Compress(bake).ToBytes();
        }

        output.Add(ContentKind.Data, cooked);
    }

    /// <summary>The bake with its pages in the formats described on <see cref="LightBakeCooker"/>.</summary>
    internal static LightBakeFile Compress(LightBakeFile bake)
    {
        return bake with
        {
            Irradiance = [.. bake.Irradiance.Select(page => ToBc6h(page))],
            Direction = [.. bake.Direction.Select(page => ToBc7(page))],
            Mask = [.. bake.Mask.Select(page => ToBc7(page))],
        };
    }

    private static LightBakePage ToBc6h(LightBakePage page)
    {
        if (page.Format != LightBakePageFormat.Rgba16f || !IsWholeBlocks(page))
        {
            return page;
        }

        return page with { Format = LightBakePageFormat.Bc6h, Pixels = Bc6hEncoder.Encode(page.Pixels, page.Width, page.Height) };
    }

    private static LightBakePage ToBc7(LightBakePage page)
    {
        if (page.Format != LightBakePageFormat.Rgba8 || !IsWholeBlocks(page))
        {
            return page;
        }

        return page with { Format = LightBakePageFormat.Bc7, Pixels = Bc7Encoder.Encode(page.Pixels, page.Width, page.Height) };
    }

    private static bool IsWholeBlocks(LightBakePage page) => page.Width > 0 && page.Height > 0 && page.Width % 4 == 0 && page.Height % 4 == 0;
}

/// <summary>
/// Encodes RGBA16F pixels as unsigned BC6H with BCnEncoder.Net, which is slow. A baked page is mostly empty, so the blocks
/// that are all zero are written without encoding, and the rest are packed side by side into one strip, encoded in
/// parallel and put back where they were.
/// </summary>
internal static class Bc6hEncoder
{
    // Bump this when the bytes it writes change: the quality, the light floor or the empty-block shortcut.
    public const int Version = 1;

    // Measured on the Gallery's bake (one 2048 by 2048 page, 1.9 million lit texels): Fast took 1.7 s and was off by 1.76% of the
    // light, Balanced took 1.8 s and 1.25%, and BestQuality took 8.4 s and 0.60%. The empty blocks are what keep Balanced cheap.
    private const CompressionQuality Quality = CompressionQuality.Balanced;

    // Blocks in a row of the strip. Any width would do, as blocks are encoded alone, but rows are what the encoder spreads over threads.
    private const int StripBlocks = 256;

    private const float MaxHalf = 65504f;

    private const float MinLight = 1e-4f;

    private static readonly Lazy<byte[]> BlackBlock = new(() => EncodeBlocks([new ColorRgbFloat(0f, 0f, 0f)], 1, 1));

    /// <summary>Encodes pixels of 8 bytes each (red, green, blue, alpha as halves, row 0 first) into blocks, left to right then top to bottom. Alpha is dropped. The sides must be multiples of 4.</summary>
    public static byte[] Encode(byte[] rgba16f, int width, int height)
    {
        if (rgba16f.Length != width * height * 8)
        {
            throw new ArgumentException($"A {width}x{height} RGBA16F image is {width * height * 8} bytes, not {rgba16f.Length}.", nameof(rgba16f));
        }

        int across = width / 4;
        int down = height / 4;
        var output = new byte[across * down * 16];
        var black = BlackBlock.Value;
        var used = new List<int>();
        for (int block = 0; block < across * down; block++)
        {
            if (IsEmpty(rgba16f, width, block % across * 4, block / across * 4))
            {
                black.CopyTo(output, block * 16);
            }
            else
            {
                used.Add(block);
            }
        }

        if (used.Count == 0)
        {
            return output;
        }

        // The strip: StripBlocks blocks across, as many rows as the used blocks fill, the last one padded with black.
        int stripAcross = Math.Min(StripBlocks, used.Count);
        int stripDown = (used.Count + stripAcross - 1) / stripAcross;
        var strip = new ColorRgbFloat[stripAcross * 4 * stripDown * 4];
        for (int n = 0; n < used.Count; n++)
        {
            int left = n % stripAcross * 4;
            int top = n / stripAcross * 4;
            int sourceLeft = used[n] % across * 4;
            int sourceTop = used[n] / across * 4;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    strip[(top + y) * stripAcross * 4 + left + x] = Read(rgba16f, ((sourceTop + y) * width + sourceLeft + x) * 8);
                }
            }
        }

        var encoded = EncodeBlocks(strip, stripAcross * 4, stripDown * 4);
        for (int n = 0; n < used.Count; n++)
        {
            Array.Copy(encoded, n * 16, output, used[n] * 16, 16);
        }

        return output;
    }

    private static byte[] EncodeBlocks(ColorRgbFloat[] pixels, int width, int height)
    {
        var encoder = new BcEncoder(CompressionFormat.Bc6U);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = Quality;
        return encoder.EncodeToRawBytesHdr(new ReadOnlyMemory2D<ColorRgbFloat>(pixels, height, width))[0];
    }

    private static bool IsEmpty(byte[] pixels, int width, int left, int top)
    {
        for (int y = 0; y < 4; y++)
        {
            int start = ((top + y) * width + left) * 8;
            for (int x = 0; x < 4; x++)
            {
                // Only red, green and blue count, the first 6 bytes of a pixel. A half's top byte holds its sign, which a
                // negative zero sets, so it is masked off: both zeros encode as black.
                if ((pixels[start + x * 8] | (pixels[start + x * 8 + 1] & 0x7F) | pixels[start + x * 8 + 2] | (pixels[start + x * 8 + 3] & 0x7F)
                    | pixels[start + x * 8 + 4] | (pixels[start + x * 8 + 5] & 0x7F)) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ColorRgbFloat Read(byte[] pixels, int offset)
    {
        return new ColorRgbFloat(Channel(pixels, offset), Channel(pixels, offset + 2), Channel(pixels, offset + 4));
    }

    // Unsigned BC6H has no negative numbers, infinity or NaN: they become the nearest value it holds. Nothing goes
    // below MinLight either: at the Fast and Balanced qualities BCnEncoder.Net writes a block that mixes exact zeros with
    // small values as 65504 in the zeros' place. A floor of 0.0001 keeps that away and decodes as black to the eye.
    private static float Channel(byte[] pixels, int offset)
    {
        float value = (float)BitConverter.ToHalf(pixels, offset);
        if (float.IsNaN(value))
        {
            return MinLight;
        }

        return Math.Clamp(value, MinLight, MaxHalf);
    }
}
