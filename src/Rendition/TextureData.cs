using System.Numerics;

namespace Rendition;

/// <summary>How a texture's bytes are stored.</summary>
public enum TextureFormat
{
    /// <summary>Four bytes a pixel: red, green, blue and alpha.</summary>
    Rgba8,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with all four channels: a quarter of
    /// <see cref="Rgba8"/>'s size, and close to it for photos and painted textures.
    /// </summary>
    Bc7,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with red and green only, each kept as well as
    /// <see cref="Bc4"/> keeps one. For normal maps, whose third axis follows from the other two.
    /// </summary>
    Bc5,

    /// <summary>Blocks of 4x4 pixels in 8 bytes, with red only. For height maps.</summary>
    Bc4,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with red, green and blue as unsigned half floats, so light brighter than 1
    /// keeps its brightness. For baked lightmaps. Only the GPU reads it: <see cref="TextureKind"/> doesn't apply, as
    /// it is always linear.
    /// </summary>
    Bc6h,
}

/// <summary>A texture with a full mip chain, largest level first, in RGBA8 unless <see cref="Format"/> says otherwise.</summary>
public sealed class TextureData
{
    // How far the mip filter reaches each way, in pixels of the smaller level, and how quickly
    // its window falls off. Two pixels and 4 are a common choice for mips: crisp, with little ringing.
    private const float FilterRadius = 2f;
    private const float KaiserAlpha = 4f;

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TextureKind Kind { get; init; }
    public required byte[][] Mips { get; init; }
    public TextureFormat Format { get; init; } = TextureFormat.Rgba8;

    private const uint Magic = 0x58455452; // "RTEX"
    private const int Version = 2; // 2: textures say their format.

    /// <summary>The bytes of the texture and its mips, as <see cref="Read"/> reads them.</summary>
    public byte[] ToBytes()
    {
        var stream = new MemoryStream(Mips.Sum(mip => mip.Length) + 64);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteBody(writer);
        }

        return stream.ToArray();
    }

    /// <summary>Reads a texture written by <see cref="ToBytes"/>. <paramref name="name"/> is only for messages.</summary>
    public static TextureData Read(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current texture file. Cook it again.");
        }

        return ReadBody(reader);
    }

    /// <summary>Writes the size, kind, format and mips, without a header, for a texture inside another file.</summary>
    internal void WriteBody(BinaryWriter writer)
    {
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((int)Kind);
        writer.Write((int)Format);
        writer.Write(Mips.Length);
        foreach (byte[] mip in Mips)
        {
            writer.Write(mip.Length);
            writer.Write(mip);
        }
    }

    internal static TextureData ReadBody(BinaryReader reader)
    {
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        var kind = (TextureKind)reader.ReadInt32();
        var format = (TextureFormat)reader.ReadInt32();
        var mips = new byte[reader.ReadInt32()][];
        for (int i = 0; i < mips.Length; i++)
        {
            mips[i] = new byte[reader.ReadInt32()];
            reader.BaseStream.ReadExactly(mips[i]);
        }

        return new TextureData { Width = width, Height = height, Kind = kind, Format = format, Mips = mips };
    }

    /// <summary>How many bytes a level of <paramref name="width"/> by <paramref name="height"/> pixels takes in <paramref name="format"/>.</summary>
    public static int LevelSize(TextureFormat format, int width, int height)
    {
        int blocks = (width + 3) / 4 * ((height + 3) / 4);
        return format switch
        {
            TextureFormat.Rgba8 => width * height * 4,
            TextureFormat.Bc4 => blocks * 8,
            _ => blocks * 16,
        };
    }

    /// <summary>The lowest and highest alpha of the full-size image, from 0 to 1. Only for <see cref="TextureFormat.Rgba8"/>.</summary>
    public (float Lowest, float Highest) AlphaRange()
    {
        if (Format != TextureFormat.Rgba8)
        {
            throw new InvalidOperationException($"Can't read the alpha of a {Format} texture, only of an RGBA8 one.");
        }

        byte lowest = 255;
        byte highest = 0;
        byte[] pixels = Mips[0];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            lowest = Math.Min(lowest, pixels[i]);
            highest = Math.Max(highest, pixels[i]);
        }

        return (lowest / 255f, highest / 255f);
    }

    /// <summary>
    /// Builds the mip chain for an image's RGBA bytes, row by row. Each level is filtered from
    /// the one above with a Kaiser-windowed sinc, which keeps detail crisp where a plain average
    /// of four pixels would soften it a little more at every level. Colour is filtered in linear
    /// light, the edges wrap round as a tiling texture's do, and a normal map's normals are
    /// brought back to unit length.
    /// </summary>
    public static TextureData FromPixels(int width, int height, TextureKind kind, byte[] rgba, bool normalMap = false)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"A texture can't be {width}x{height}.", nameof(width));
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"A {width}x{height} texture needs {width * height * 4} bytes, not {rgba.Length}.", nameof(rgba));
        }

        var mips = new List<byte[]> { rgba };
        float[] level = Decode(rgba, kind);
        int levelWidth = width;
        int levelHeight = height;
        while (levelWidth > 1 || levelHeight > 1)
        {
            int nextWidth = Math.Max(levelWidth / 2, 1);
            int nextHeight = Math.Max(levelHeight / 2, 1);
            level = Downsample(level, levelWidth, levelHeight, nextWidth, nextHeight);
            if (normalMap)
            {
                Renormalize(level);
            }

            mips.Add(Encode(level, kind));
            levelWidth = nextWidth;
            levelHeight = nextHeight;
        }

        return new TextureData { Width = width, Height = height, Kind = kind, Mips = mips.ToArray() };
    }

    /// <summary>
    /// A copy of a cut-out texture whose smaller levels keep the share of pixels that pass
    /// <paramref name="cutoff"/>. Filtering averages a thin opaque thread with the clear pixels
    /// beside it, so its alpha falls below the cutoff a few levels down and it vanishes in the
    /// distance. Each level's alpha is scaled instead, by the factor that makes as large a share
    /// of its pixels reach the cutoff as on the full-size level, found by binary search (after
    /// Castaño's coverage-preserving mipmaps). Only alpha changes, and the full-size level is
    /// left as it is. A level is scaled from the filtered level, never from the scaled one above,
    /// so the error doesn't build up. Returns the texture as it is when the cutoff is outside
    /// 0 to 1, when nothing or everything passes it, or when it isn't RGBA8.
    /// </summary>
    public static TextureData KeepAlphaCoverage(TextureData texture, float cutoff)
    {
        if (texture.Format != TextureFormat.Rgba8 || !(cutoff > 0f && cutoff <= 1f))
        {
            return texture;
        }

        // Alpha is a byte, so a pixel passes when its byte is at least this, as the shader's
        // a >= cutoff reads it.
        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        long[] top = AlphaHistogram(texture.Mips[0]);
        long topPixels = texture.Mips[0].Length / 4;
        double target = (double)Passing(top, 1.0, threshold) / topPixels;
        if (target <= 0.0 || target >= 1.0)
        {
            return texture;
        }

        return ScaleAlpha(texture, level =>
        {
            byte[] pixels = texture.Mips[level];
            long[] histogram = AlphaHistogram(pixels);
            return CoverageScale(scale => Passing(histogram, scale, threshold), pixels.Length / 4, target);
        });
    }

    /// <summary>
    /// As <see cref="KeepAlphaCoverage(TextureData, float)"/>, for a material that cuts where this texture's alpha times
    /// <paramref name="partner"/>'s falls below <paramref name="cutoff"/>, as one whose colour and surface textures both
    /// carry opacity does. Keeping each one's own coverage wouldn't keep the product's, so only this texture's alpha is
    /// scaled: each level keeps the share of pixels whose product passes that the full-size level has, against the
    /// partner's own filtered level, which it is left at. The partner is read at the level that covers the same part
    /// of the surface, at the nearest pixel, so the two can be different sizes. Returns the texture as it is when the
    /// cutoff is outside 0 to 1, when no pixel's product passes or every one does, or when either isn't RGBA8.
    /// </summary>
    public static TextureData KeepAlphaCoverage(TextureData texture, float cutoff, TextureData partner)
    {
        if (texture.Format != TextureFormat.Rgba8 || partner.Format != TextureFormat.Rgba8 || !(cutoff > 0f && cutoff <= 1f))
        {
            return texture;
        }

        var thresholds = new int[256];
        for (int p = 0; p < 256; p++)
        {
            thresholds[p] = ProductThreshold(cutoff, (byte)p);
        }

        long[] top = ProductHistogram(texture.Mips[0], PartnerAlpha(partner, texture, 0), thresholds);
        long topPixels = texture.Mips[0].Length / 4;
        double target = (double)ProductPassing(top, 1.0) / topPixels;
        if (target <= 0.0 || target >= 1.0)
        {
            return texture;
        }

        return ScaleAlpha(texture, level =>
        {
            byte[] pixels = texture.Mips[level];
            long[] histogram = ProductHistogram(pixels, PartnerAlpha(partner, texture, level), thresholds);
            return CoverageScale(scale => ProductPassing(histogram, scale), pixels.Length / 4, target);
        });
    }

    // Scales the alpha of every level but the full-size one by the scale each level is given.
    private static TextureData ScaleAlpha(TextureData texture, Func<int, double> scaleOf)
    {
        var mips = new byte[texture.Mips.Length][];
        mips[0] = texture.Mips[0];
        for (int level = 1; level < mips.Length; level++)
        {
            byte[] pixels = texture.Mips[level];
            double scale = scaleOf(level);
            if (scale == 1.0)
            {
                mips[level] = pixels;
                continue;
            }

            var scaled = (byte[])pixels.Clone();
            for (int i = 3; i < scaled.Length; i += 4)
            {
                scaled[i] = ScaledAlpha(scaled[i], scale);
            }

            mips[level] = scaled;
        }

        return new TextureData { Width = texture.Width, Height = texture.Height, Kind = texture.Kind, Mips = mips };
    }

    /// <summary>
    /// The shader's <c>(a / 255) * (p / 255) >= cutoff</c>, as the least byte of a texture's alpha that passes over a
    /// partner byte <paramref name="partner"/>. 256 means nothing passes, as over a clear partner.
    /// </summary>
    public static int ProductThreshold(float cutoff, byte partner) =>
        partner == 0 ? 256 : Math.Clamp((int)Math.Ceiling(cutoff * 255.0 * 255.0 / partner - 1e-3), 0, 256);

    /// <summary>
    /// The partner's alpha under each pixel of <paramref name="texture"/>'s level <paramref name="level"/>: from the
    /// partner's level that covers the same part of the surface, as the GPU picks it for the same footprint, at the
    /// nearest pixel, so the two can be different sizes.
    /// </summary>
    public static byte[] PartnerAlpha(TextureData partner, TextureData texture, int level)
    {
        int width = Math.Max(texture.Width >> level, 1);
        int height = Math.Max(texture.Height >> level, 1);
        int shift = (int)Math.Round(Math.Log2((double)partner.Width / texture.Width));
        int partnerLevel = Math.Clamp(level + shift, 0, partner.Mips.Length - 1);
        int partnerWidth = Math.Max(partner.Width >> partnerLevel, 1);
        int partnerHeight = Math.Max(partner.Height >> partnerLevel, 1);
        byte[] pixels = partner.Mips[partnerLevel];
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int py = y * partnerHeight / height;
            for (int x = 0; x < width; x++)
            {
                int px = x * partnerWidth / width;
                alpha[y * width + x] = pixels[(py * partnerWidth + px) * 4 + 3];
            }
        }

        return alpha;
    }

    // For each alpha byte, how many pixels with it need at most each threshold, 0 to 256, to pass: a running count, so
    // ProductPassing reads how many pass at a scale with one look per alpha.
    private static long[] ProductHistogram(byte[] pixels, byte[] partnerAlpha, int[] thresholds)
    {
        var counts = new long[256 * 257];
        for (int i = 0; i < partnerAlpha.Length; i++)
        {
            counts[pixels[i * 4 + 3] * 257 + thresholds[partnerAlpha[i]]]++;
        }

        for (int alpha = 0; alpha < 256; alpha++)
        {
            for (int threshold = 1; threshold < 257; threshold++)
            {
                counts[alpha * 257 + threshold] += counts[alpha * 257 + threshold - 1];
            }
        }

        return counts;
    }

    // How many pixels' products pass once this texture's alpha is scaled: those whose scaled alpha reaches their threshold.
    private static long ProductPassing(long[] runningCounts, double scale)
    {
        long count = 0;
        for (int alpha = 0; alpha < 256; alpha++)
        {
            count += runningCounts[alpha * 257 + ScaledAlpha((byte)alpha, scale)];
        }

        return count;
    }

    /// <summary>The share of <paramref name="texture"/>'s pixels on <paramref name="level"/> whose alpha is at least <paramref name="cutoff"/>.</summary>
    public static double AlphaCoverage(TextureData texture, int level, float cutoff)
    {
        if (texture.Format != TextureFormat.Rgba8)
        {
            throw new InvalidOperationException($"Can't read the alpha of a {texture.Format} texture, only of an RGBA8 one.");
        }

        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        byte[] pixels = texture.Mips[level];
        return (double)Passing(AlphaHistogram(pixels), 1.0, threshold) / (pixels.Length / 4);
    }

    // How many times to halve the range of scales. 32 halvings of 0 to 255 is far finer than a byte of alpha can tell.
    private const int CoverageSearchSteps = 32;

    // The scale whose share of passing pixels is nearest the target. Scaling up only ever lets
    // more pixels pass, so the search halves the range each step. The two ends it closes in on
    // are checked at the end, as the share moves in whole pixels. 1 wins a tie, so a level that
    // already has the share is left as it is.
    private static double CoverageScale(Func<double, long> passing, long pixels, double target)
    {
        double Error(double scale) => Math.Abs((double)passing(scale) / pixels - target);

        double low = 0.0;
        double high = 255.0;
        for (int step = 0; step < CoverageSearchSteps; step++)
        {
            double middle = (low + high) * 0.5;
            if ((double)passing(middle) / pixels < target)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        double best = Error(low) < Error(high) ? low : high;
        return Error(1.0) <= Error(best) ? 1.0 : best;
    }

    private static long[] AlphaHistogram(byte[] pixels)
    {
        var histogram = new long[256];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            histogram[pixels[i]]++;
        }

        return histogram;
    }

    // How many pixels pass once their alpha is scaled.
    private static long Passing(long[] histogram, double scale, int threshold)
    {
        long count = 0;
        for (int alpha = 0; alpha < 256; alpha++)
        {
            if (histogram[alpha] != 0 && ScaledAlpha((byte)alpha, scale) >= threshold)
            {
                count += histogram[alpha];
            }
        }

        return count;
    }

    private static byte ScaledAlpha(byte alpha, double scale) => (byte)Math.Round(Math.Min(alpha * scale, 255.0));

    /// <summary>
    /// A copy of a surface texture (roughness in green) whose smaller levels are rougher where
    /// the normal map's bumps get too small to see. Averaging bumpy normals leaves a flat one,
    /// which would turn a rough surface smooth and shiny in the distance. How much the normals
    /// disagree is kept instead, as extra roughness, so the highlight stays as broad as the
    /// bumps made it. The full-size level is left as it is. Returns the surface unchanged when
    /// the two aren't the same size.
    /// </summary>
    public static TextureData WidenRoughness(TextureData surface, TextureData normal)
    {
        if (surface.Width != normal.Width || surface.Height != normal.Height || surface.Mips.Length != normal.Mips.Length
            || surface.Format != TextureFormat.Rgba8 || normal.Format != TextureFormat.Rgba8)
        {
            return surface;
        }

        // The average of the full-size normals under each pixel of each level, left at the
        // length it comes out at: the shorter, the more the normals under it disagree.
        byte[] top = normal.Mips[0];
        var average = new Vector3[surface.Width * surface.Height];
        for (int i = 0; i < average.Length; i++)
        {
            var n = new Vector3(top[i * 4], top[i * 4 + 1], top[i * 4 + 2]) / 255f * 2f - Vector3.One;
            average[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }

        var mips = new byte[surface.Mips.Length][];
        mips[0] = surface.Mips[0];
        int width = surface.Width;
        int height = surface.Height;
        for (int level = 1; level < mips.Length; level++)
        {
            int nextWidth = Math.Max(width / 2, 1);
            int nextHeight = Math.Max(height / 2, 1);
            average = AverageNormals(average, width, height, nextWidth, nextHeight);
            width = nextWidth;
            height = nextHeight;

            byte[] pixels = (byte[])surface.Mips[level].Clone();
            for (int i = 0; i < width * height; i++)
            {
                float roughness = WidenedRoughness(pixels[i * 4 + 1] / 255f, average[i].Length());
                pixels[i * 4 + 1] = (byte)MathF.Round(Math.Clamp(roughness, 0f, 1f) * 255f);
            }

            mips[level] = pixels;
        }

        return new TextureData { Width = surface.Width, Height = surface.Height, Kind = surface.Kind, Mips = mips };
    }

    /// <summary>
    /// The roughness that gives as broad a highlight as <paramref name="roughness"/> does on
    /// bumps whose unit normals average to <paramref name="averageLength"/>. The normals'
    /// spread is taken as a von Mises-Fisher lobe, whose sharpness follows from the length,
    /// and its slopes add to those of the GGX alpha, which is roughness squared.
    /// </summary>
    public static float WidenedRoughness(float roughness, float averageLength)
    {
        float r = Math.Clamp(averageLength, 0f, 1f);
        if (r >= 0.9999f)
        {
            return roughness;
        }

        float kappa = (3f * r - r * r * r) / (1f - r * r);
        float alpha = roughness * roughness;
        float widened = kappa > 0f ? MathF.Sqrt(alpha * alpha + 2f / kappa) : 1f;
        return MathF.Sqrt(MathF.Min(widened, 1f));
    }

    // Each pixel of the smaller level is the plain average of the ones it covers, which keeps
    // the shortening that tells how much they disagree.
    private static Vector3[] AverageNormals(Vector3[] source, int width, int height, int nextWidth, int nextHeight)
    {
        var result = new Vector3[nextWidth * nextHeight];
        int stepX = width / nextWidth;
        int stepY = height / nextHeight;
        for (int y = 0; y < nextHeight; y++)
        {
            for (int x = 0; x < nextWidth; x++)
            {
                var sum = Vector3.Zero;
                for (int offsetY = 0; offsetY < stepY; offsetY++)
                {
                    for (int offsetX = 0; offsetX < stepX; offsetX++)
                    {
                        sum += source[x * stepX + offsetX + (y * stepY + offsetY) * width];
                    }
                }

                result[x + y * nextWidth] = sum / (stepX * stepY);
            }
        }

        return result;
    }

    // A channel is a byte, so both conversions to floats are tables worked out once, with the
    // same arithmetic as converting each byte, so mips come out the same.
    private static readonly float[] UnitOfByte = Table(b => b / 255f);

    private static readonly float[] LinearOfByte = Table(b => ToLinear(b / 255f));

    private static float[] Table(Func<float, float> of)
    {
        var table = new float[256];
        for (int b = 0; b < 256; b++)
        {
            table[b] = of(b);
        }

        return table;
    }

    // One pixel of the smaller level along one axis: the source pixels it takes in and their weights.
    private readonly record struct Taps(int[] Sources, float[] Weights);

    private static Taps[] FilterTaps(int size, int nextSize)
    {
        float scale = (float)size / nextSize;
        var taps = new Taps[nextSize];
        for (int x = 0; x < nextSize; x++)
        {
            float center = (x + 0.5f) * scale;
            int first = (int)MathF.Floor(center - FilterRadius * scale);
            int last = (int)MathF.Ceiling(center + FilterRadius * scale);
            var sources = new List<int>();
            var weights = new List<float>();
            float total = 0f;
            for (int i = first; i <= last; i++)
            {
                float t = (i + 0.5f - center) / scale;
                float weight = Sinc(t) * Kaiser(t / FilterRadius);
                if (weight == 0f)
                {
                    continue;
                }

                sources.Add(((i % size) + size) % size);
                weights.Add(weight);
                total += weight;
            }

            for (int i = 0; i < weights.Count; i++)
            {
                weights[i] /= total;
            }

            taps[x] = new Taps(sources.ToArray(), weights.ToArray());
        }

        return taps;
    }

    private static float Sinc(float t) => MathF.Abs(t) < 1e-5f ? 1f : MathF.Sin(MathF.PI * t) / (MathF.PI * t);

    private static float Kaiser(float t) =>
        MathF.Abs(t) >= 1f ? 0f : BesselI0(KaiserAlpha * MathF.Sqrt(1f - t * t)) / BesselI0(KaiserAlpha);

    // The modified Bessel function of the first kind, order 0, from its series.
    private static float BesselI0(float x)
    {
        double sum = 1.0;
        double term = 1.0;
        double quarter = x * x / 4.0;
        for (int k = 1; k < 32; k++)
        {
            term *= quarter / (k * k);
            sum += term;
        }

        return (float)sum;
    }

    // Filters across, then down, a row at a time in parallel.
    private static float[] Downsample(float[] source, int width, int height, int nextWidth, int nextHeight)
    {
        var across = FilterTaps(width, nextWidth);
        var down = FilterTaps(height, nextHeight);
        var narrow = new float[nextWidth * height * 4];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < nextWidth; x++)
            {
                var taps = across[x];
                var sum = Vector4.Zero;
                for (int i = 0; i < taps.Sources.Length; i++)
                {
                    int s = (taps.Sources[i] + y * width) * 4;
                    sum += new Vector4(source[s], source[s + 1], source[s + 2], source[s + 3]) * taps.Weights[i];
                }

                Store(narrow, (x + y * nextWidth) * 4, sum);
            }
        });

        var result = new float[nextWidth * nextHeight * 4];
        Parallel.For(0, nextHeight, y =>
        {
            var taps = down[y];
            for (int x = 0; x < nextWidth; x++)
            {
                var sum = Vector4.Zero;
                for (int i = 0; i < taps.Sources.Length; i++)
                {
                    int s = (x + taps.Sources[i] * nextWidth) * 4;
                    sum += new Vector4(narrow[s], narrow[s + 1], narrow[s + 2], narrow[s + 3]) * taps.Weights[i];
                }

                // The filter's negative lobes can overshoot a little at sharp steps.
                Store(result, (x + y * nextWidth) * 4, Vector4.Clamp(sum, Vector4.Zero, Vector4.One));
            }
        });

        return result;
    }

    private static void Store(float[] data, int offset, Vector4 value)
    {
        data[offset] = value.X;
        data[offset + 1] = value.Y;
        data[offset + 2] = value.Z;
        data[offset + 3] = value.W;
    }

    // Each pixel on its own, in pieces of whole pixels on several threads, like decoding.
    private static void Renormalize(float[] level)
    {
        Parallel.For(0, (level.Length + ConvertChunk - 1) / ConvertChunk, chunk =>
        {
            int end = Math.Min(level.Length, (chunk + 1) * ConvertChunk);
            for (int i = chunk * ConvertChunk; i < end; i += 4)
            {
                var normal = new Vector3(level[i], level[i + 1], level[i + 2]) * 2f - Vector3.One;
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                level[i] = normal.X * 0.5f + 0.5f;
                level[i + 1] = normal.Y * 0.5f + 0.5f;
                level[i + 2] = normal.Z * 0.5f + 0.5f;
            }
        });
    }

    // Values per piece when converting a level in parallel: whole pixels, and enough that a piece outweighs its scheduling.
    private const int ConvertChunk = 1 << 16;

    // Colour has to be filtered in linear light, or mips come out too dark. Each value is converted on its own, so
    // converting in pieces on several threads gives the same floats.
    private static float[] Decode(byte[] data, TextureKind kind)
    {
        float[] colour = kind == TextureKind.Color ? LinearOfByte : UnitOfByte;
        var result = new float[data.Length];
        Parallel.For(0, (data.Length + ConvertChunk - 1) / ConvertChunk, chunk =>
        {
            int end = Math.Min(data.Length, (chunk + 1) * ConvertChunk);
            for (int i = chunk * ConvertChunk; i < end; i++)
            {
                result[i] = i % 4 != 3 ? colour[data[i]] : UnitOfByte[data[i]];
            }
        });

        return result;
    }

    // Like decoding, in pieces on several threads. The power in ToSrgb makes it the slow half of building mips.
    private static byte[] Encode(float[] data, TextureKind kind)
    {
        var result = new byte[data.Length];
        Parallel.For(0, (data.Length + ConvertChunk - 1) / ConvertChunk, chunk =>
        {
            int end = Math.Min(data.Length, (chunk + 1) * ConvertChunk);
            for (int i = chunk * ConvertChunk; i < end; i++)
            {
                float value = kind == TextureKind.Color && i % 4 != 3 ? ToSrgb(data[i]) : data[i];
                result[i] = (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
            }
        });

        return result;
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
}
