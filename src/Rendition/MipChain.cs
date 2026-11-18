using System.Numerics;

namespace Rendition;

/// <summary>
/// Builds a texture's mip chain from its full-size RGBA bytes, each level filtered from the one above with a
/// Kaiser-windowed sinc. See <see cref="TextureData.FromPixels"/>.
/// </summary>
internal static class MipChain
{
    // How far the mip filter reaches each way, in pixels of the smaller level, and how quickly
    // its window falls off. Two pixels and 4 are a common choice for mips: crisp, with little ringing.
    private const float FilterRadius = 2f;
    private const float KaiserAlpha = 4f;

    // Values per piece when converting a level in parallel: whole pixels, and enough that a piece outweighs its scheduling.
    private const int ConvertChunk = 1 << 16;

    // A channel is a byte, so both conversions to floats are tables worked out once, with the
    // same arithmetic as converting each byte, so mips come out the same.
    private static readonly float[] UnitOfByte = Table(b => b / 255f);

    private static readonly float[] LinearOfByte = Table(b => ToLinear(b / 255f));

    /// <summary>
    /// Every level from the full-size one down to one pixel, the full-size one being <paramref name="rgba"/> itself.
    /// Colour is filtered in linear light, the edges wrap round as a tiling texture's do, and a normal map's normals
    /// are brought back to unit length.
    /// </summary>
    public static byte[][] Build(byte[] rgba, int width, int height, TextureKind kind, bool normalMap)
    {
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

        return mips.ToArray();
    }

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
