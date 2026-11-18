using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using StbImageSharp;

namespace Super.Cook;

/// <summary>
/// Turns an equirectangular Radiance .hdr into a <see cref="SkyImageData"/>: a half-float cube with its mips, with the
/// sun taken out and kept as a disc. See "A photographed sky" in docs/rendering.md.
/// </summary>
public sealed class SkyCooker : ICooker
{
    /// <summary>How wide each cooked face is, to match the cube the environment draws the sky into.</summary>
    internal const int FaceSize = 256;

    // A sun is a texel this many times brighter than the sky on average. Its patch is the texels within
    // SunReach of it and brighter than PatchFactor times the average, and the ring out to RingReach gives
    // the colour that fills the hole.
    private const float SunFactor = 20f;
    private const float PatchFactor = 10f;
    private static readonly float SunReach = 5f * MathF.PI / 180f;
    private static readonly float RingReach = 7f * MathF.PI / 180f;
    private const int Supersamples = 4;
    private const float HalfMax = 65504f;

    public string Id => "sky";

    public int Version => 1;

    public IReadOnlyList<string> Extensions { get; } = [".hdr"];

    public void Cook(CookInput input, CookOutput output)
    {
        ImageResultFloat image;
        try
        {
            using (input.Time("decode"))
            {
                image = ImageResultFloat.FromMemory(input.Bytes, ColorComponents.RedGreenBlue);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            output.Error($"{input.Name} is not a Radiance .hdr image it can read: {exception.Message}");
            return;
        }

        if (image.Width != image.Height * 2)
        {
            output.Error(
                $"{input.Name} is {image.Width}x{image.Height}. A sky must be equirectangular, twice as wide as it is tall.");
            return;
        }

        SkyImageData sky;
        using (input.Time("cube"))
        {
            sky = FromEquirect(image.Data, image.Width, image.Height);
        }

        output.Add(ContentKind.Data, sky.ToBytes());
    }

    /// <summary>
    /// Takes the sun out of an equirectangular image of <paramref name="width"/> by <paramref name="height"/> RGB floats
    /// and draws what is left onto a cube. The middle of the image faces -Z, with +X to its right and +Y up.
    /// </summary>
    internal static SkyImageData FromEquirect(float[] rgb, int width, int height, int faceSize = FaceSize)
    {
        var pixels = new Vector3[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Vector3(Finite(rgb[i * 3]), Finite(rgb[i * 3 + 1]), Finite(rgb[i * 3 + 2]));
        }

        var sun = TakeOutSun(pixels, width, height);
        var faces = new TextureData[6];
        for (int face = 0; face < 6; face++)
        {
            faces[face] = Face(pixels, width, height, face, faceSize);
        }

        return new SkyImageData
        {
            Faces = faces,
            SunDirection = sun.Direction,
            SunRadiance = sun.Radiance,
            SunCosine = sun.Cosine,
        };
    }

    private static float Finite(float value) => float.IsFinite(value) && value > 0f ? value : 0f;

    private static (Vector3 Direction, Vector3 Radiance, float Cosine) TakeOutSun(Vector3[] pixels, int width, int height)
    {
        var (mean, peak, peakLuminance) = Brightest(pixels, width, height);
        if (mean <= 0f || peakLuminance < SunFactor * mean)
        {
            return (Vector3.Zero, Vector3.Zero, 1f);
        }

        var (fill, patch) = AroundSun(pixels, width, height, mean, peak);
        var lost = Vector3.Zero;
        var aim = Vector3.Zero;
        float patchArea = 0f;
        foreach (int i in patch)
        {
            int x = i % width, y = i / width;
            float solidAngle = SolidAngle(y, width, height);
            var excess = Vector3.Max(pixels[i] - fill, Vector3.Zero) * solidAngle;
            lost += excess;
            aim += Direction(x, y, width, height) * Luminance(excess);
            patchArea += solidAngle;
            pixels[i] = Vector3.Min(pixels[i], fill);
        }

        float cosineRadius = 1f - patchArea / (2f * MathF.PI);
        var radiance = Vector3.Min(lost / patchArea, new Vector3(HalfMax));
        return (Vector3.Normalize(aim), radiance, cosineRadius);
    }

    // The picture's mean luminance over the sphere, and its brightest texel.
    private static (float Mean, int Peak, float PeakLuminance) Brightest(Vector3[] pixels, int width, int height)
    {
        double total = 0, area = 0;
        int peak = 0;
        float peakLuminance = 0f;
        for (int y = 0; y < height; y++)
        {
            float solidAngle = SolidAngle(y, width, height);
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                float luminance = Luminance(pixels[i]);
                total += luminance * solidAngle;
                area += solidAngle;
                if (luminance > peakLuminance)
                {
                    peakLuminance = luminance;
                    peak = i;
                }
            }
        }

        return ((float)(total / area), peak, peakLuminance);
    }

    // The sun's patch round the peak, and the colour of the ring round it, to fill the hole it leaves.
    private static (Vector3 Fill, List<int> Patch) AroundSun(Vector3[] pixels, int width, int height, float mean, int peak)
    {
        var centre = Direction(peak % width, peak / width, width, height);
        float sunCosine = MathF.Cos(SunReach), ringCosine = MathF.Cos(RingReach);
        int peakRow = peak / width;
        int rowReach = (int)MathF.Ceiling(RingReach / MathF.PI * height) + 1;
        int top = Math.Max(peakRow - rowReach, 0), bottom = Math.Min(peakRow + rowReach, height - 1);

        var ring = Vector3.Zero;
        float ringArea = 0f;
        var patch = new List<int>();
        for (int y = top; y <= bottom; y++)
        {
            float solidAngle = SolidAngle(y, width, height);
            for (int x = 0; x < width; x++)
            {
                float cosine = Vector3.Dot(Direction(x, y, width, height), centre);
                int i = y * width + x;
                if (cosine >= sunCosine)
                {
                    if (Luminance(pixels[i]) > PatchFactor * mean)
                    {
                        patch.Add(i);
                    }
                }
                else if (cosine >= ringCosine)
                {
                    ring += pixels[i] * solidAngle;
                    ringArea += solidAngle;
                }
            }
        }

        return (ringArea > 0f ? ring / ringArea : Vector3.Zero, patch);
    }

    private static TextureData Face(Vector3[] pixels, int width, int height, int face, int size)
    {
        var level = new Vector3[size * size];
        Parallel.For(0, size, row =>
        {
            for (int column = 0; column < size; column++)
            {
                var sum = Vector3.Zero;
                for (int sy = 0; sy < Supersamples; sy++)
                {
                    for (int sx = 0; sx < Supersamples; sx++)
                    {
                        var uv = new Vector2(
                            (column + (sx + 0.5f) / Supersamples) / size * 2f - 1f,
                            (row + (sy + 0.5f) / Supersamples) / size * 2f - 1f);
                        sum += Sample(pixels, width, height, Vector3.Normalize(SkyImageData.FaceDirection(face, uv)));
                    }
                }

                level[row * size + column] = sum / (Supersamples * Supersamples);
            }
        });

        var mips = new List<byte[]> { Halves(level) };
        for (int mipSize = size / 2; mipSize >= 1; mipSize /= 2)
        {
            level = Halve(level, mipSize * 2);
            mips.Add(Halves(level));
        }

        return new TextureData
        {
            Width = size,
            Height = size,
            Kind = TextureKind.Linear,
            Format = TextureFormat.Rgba16f,
            Mips = [.. mips],
        };
    }

    private static Vector3 Sample(Vector3[] pixels, int width, int height, Vector3 direction)
    {
        float u = 0.5f + MathF.Atan2(direction.X, -direction.Z) / (2f * MathF.PI);
        float v = MathF.Acos(Math.Clamp(direction.Y, -1f, 1f)) / MathF.PI;
        float x = u * width - 0.5f, y = Math.Clamp(v * height - 0.5f, 0f, height - 1f);
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int left = ((x0 % width) + width) % width, right = (left + 1) % width;
        int below = Math.Min(y0 + 1, height - 1);
        var upper = Vector3.Lerp(pixels[y0 * width + left], pixels[y0 * width + right], fx);
        var lower = Vector3.Lerp(pixels[below * width + left], pixels[below * width + right], fx);
        return Vector3.Lerp(upper, lower, fy);
    }

    private static Vector3[] Halve(Vector3[] level, int size)
    {
        int half = size / 2;
        var next = new Vector3[half * half];
        for (int y = 0; y < half; y++)
        {
            for (int x = 0; x < half; x++)
            {
                next[y * half + x] = (level[2 * y * size + 2 * x] + level[2 * y * size + 2 * x + 1]
                    + level[(2 * y + 1) * size + 2 * x] + level[(2 * y + 1) * size + 2 * x + 1]) / 4f;
            }
        }

        return next;
    }

    private static byte[] Halves(Vector3[] level)
    {
        var halves = new Half[level.Length * 4];
        for (int i = 0; i < level.Length; i++)
        {
            halves[i * 4] = (Half)MathF.Min(level[i].X, HalfMax);
            halves[i * 4 + 1] = (Half)MathF.Min(level[i].Y, HalfMax);
            halves[i * 4 + 2] = (Half)MathF.Min(level[i].Z, HalfMax);
            halves[i * 4 + 3] = (Half)1f;
        }

        return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
    }

    private static Vector3 Direction(int x, int y, int width, int height)
    {
        float theta = (y + 0.5f) / height * MathF.PI;
        float phi = ((x + 0.5f) / width - 0.5f) * 2f * MathF.PI;
        return new Vector3(MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), -MathF.Sin(theta) * MathF.Cos(phi));
    }

    private static float SolidAngle(int y, int width, int height) =>
        2f * MathF.PI / width * (MathF.PI / height) * MathF.Sin((y + 0.5f) / height * MathF.PI);

    private static float Luminance(Vector3 color) => 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
}
