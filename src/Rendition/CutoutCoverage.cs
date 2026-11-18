namespace Rendition;

/// <summary>
/// Keeps a cut-out texture's share of passing pixels the same at every mip level, so thin cut-out shapes don't vanish
/// in the distance.
/// </summary>
public static class CutoutCoverage
{
    // How many times to halve the range of scales. 32 halvings of 0 to 255 is far finer than a byte of alpha can tell.
    private const int CoverageSearchSteps = 32;

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
    public static TextureData Keep(TextureData texture, float cutoff)
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
    /// As <see cref="Keep(TextureData, float)"/>, for a material that cuts where this texture's alpha times
    /// <paramref name="partner"/>'s falls below <paramref name="cutoff"/>, as one whose colour and surface textures both
    /// carry opacity does. Keeping each one's own coverage wouldn't keep the product's, so only this texture's alpha is
    /// scaled: each level keeps the share of pixels whose product passes that the full-size level has, against the
    /// partner's own filtered level, which it is left at. The partner is read at the level that covers the same part
    /// of the surface, at the nearest pixel, so the two can be different sizes. Returns the texture as it is when the
    /// cutoff is outside 0 to 1, when no pixel's product passes or every one does, or when either isn't RGBA8.
    /// </summary>
    public static TextureData Keep(TextureData texture, float cutoff, TextureData partner)
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

    /// <summary>The share of <paramref name="texture"/>'s pixels on <paramref name="level"/> whose alpha is at least <paramref name="cutoff"/>.</summary>
    public static double Of(TextureData texture, int level, float cutoff)
    {
        if (texture.Format != TextureFormat.Rgba8)
        {
            throw new InvalidOperationException($"Can't read the alpha of a {texture.Format} texture, only of an RGBA8 one.");
        }

        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        byte[] pixels = texture.Mips[level];
        return (double)Passing(AlphaHistogram(pixels), 1.0, threshold) / (pixels.Length / 4);
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
}
