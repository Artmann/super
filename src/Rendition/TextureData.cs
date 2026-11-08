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

    private static void Renormalize(float[] level)
    {
        for (int i = 0; i < level.Length; i += 4)
        {
            var normal = new Vector3(level[i], level[i + 1], level[i + 2]) * 2f - Vector3.One;
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            level[i] = normal.X * 0.5f + 0.5f;
            level[i + 1] = normal.Y * 0.5f + 0.5f;
            level[i + 2] = normal.Z * 0.5f + 0.5f;
        }
    }

    // Colour has to be filtered in linear light, or mips come out too dark.
    private static float[] Decode(byte[] data, TextureKind kind)
    {
        float[] colour = kind == TextureKind.Color ? LinearOfByte : UnitOfByte;
        var result = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = i % 4 != 3 ? colour[data[i]] : UnitOfByte[data[i]];
        }

        return result;
    }

    private static byte[] Encode(float[] data, TextureKind kind)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            float value = kind == TextureKind.Color && i % 4 != 3 ? ToSrgb(data[i]) : data[i];
            result[i] = (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
        }

        return result;
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
}
