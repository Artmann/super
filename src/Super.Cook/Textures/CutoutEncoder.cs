using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Rendition;

namespace Super.Cook;

/// <summary>
/// Encodes one level of a cut-out texture as BC7 so that as many of its pixels pass the cutoff once decoded as did before.
/// The cooker kept each level's coverage as it scaled its alpha, but the filter leaves much of a small level's alpha just
/// either side of the cutoff, and BC7 moves alpha by a few steps, so a thin cut-out such as leaves or a fence could lose
/// most of its pixels at a distance.
/// </summary>
internal static class CutoutEncoder
{
    /// <summary>How far <see cref="Encode"/> stretches a level's alpha from the cutoff, in the order it tries them.</summary>
    private static readonly double[] Stretches = [2.0, 4.0, 8.0, 16.0];

    /// <summary>
    /// How many pixels a compressed level's count of passing pixels may be off from the level's own: 1% of its pixels,
    /// or one pixel on a level too small for that. BC7 rounds each block's alpha to its own few steps, so a count that
    /// is exactly right is rarely reachable.
    /// </summary>
    private static int Tolerance(int pixels) => Math.Max(1, pixels / 100);

    /// <summary>
    /// Encodes one level of RGBA8 <paramref name="pixels"/> as BC7. When the plain encoding's decoded count of pixels that
    /// pass <paramref name="cutoff"/> is off by more than <see cref="Tolerance"/>, the level's alpha is stretched away from
    /// the cutoff before it is encoded again, ever further until the count is near enough. The stretch keeps the cutoff
    /// where it is, so it keeps which pixels pass, and where the cut falls between two pixels as the sampler blends them;
    /// it only moves each pixel's alpha further from the cutoff than BC7's error can carry it back. The encoding that comes
    /// nearest is kept, and the plain one wins a tie. With <paramref name="partnerAlpha"/>, the alpha of the surface texture
    /// the shader multiplies this one's by, a pixel passes when the product does.
    /// </summary>
    public static byte[] Encode(byte[] pixels, int width, int height, float cutoff, byte[]? partnerAlpha)
    {
        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        var (thresholds, centres) = PixelThresholds(width * height, cutoff, threshold, partnerAlpha);

        // The encoder's own cut-out hint knows one threshold only, so over a partner it's left out.
        int hint = partnerAlpha is null ? threshold : 0;
        byte[] best = Bc7Encoder.Encode(pixels, width, height, hint);
        if (thresholds.All(t => t <= 0 || t > 255))
        {
            // Every byte passes, or none can, so compression can't change which do.
            return best;
        }

        int target = Passing(pixels, thresholds);
        int tolerance = Tolerance(width * height);
        int bestError = Math.Abs(CompressedPassing(best, width, height, thresholds) - target);
        foreach (double stretch in Stretches)
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

    // Each pixel's own least passing byte and the cutoff it's stretched from, in bytes. Over a partner, the
    // shader tests the product, so a pixel passes from cutoff / partner on, and over a clear partner never.
    private static (int[] Thresholds, double[] Centres) PixelThresholds(int count, float cutoff, int threshold, byte[]? partnerAlpha)
    {
        var thresholds = new int[count];
        var centres = new double[count];
        for (int i = 0; i < thresholds.Length; i++)
        {
            byte partner = partnerAlpha?[i] ?? 255;
            thresholds[i] = partnerAlpha is null ? threshold : CutoutCoverage.ProductThreshold(cutoff, partner);
            centres[i] = partner == 0 ? double.NaN : cutoff * 255.0 * 255.0 / partner;
        }

        return (thresholds, centres);
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
}
