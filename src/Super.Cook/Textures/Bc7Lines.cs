namespace Super.Cook;

/// <summary>
/// What <see cref="Bc7Encoder"/>'s two modes share: fitting a line through a block's colours, refining its ends, and
/// writing the bits of a block.
/// </summary>
internal static class Bc7Lines
{
    /// <summary>How many times a fit's ends are refined from the indices it chose.</summary>
    public const int RefineSteps = 2;

    /// <summary>Puts the block's ends along the main axis of its colours, through their mean.</summary>
    public static void FitAxis(ReadOnlySpan<int> pixels, int channels, Span<float> start, Span<float> end)
    {
        Span<float> mean = stackalloc float[4];
        Span<float> axis = stackalloc float[4];
        MeanAndSize(pixels, channels, mean, axis);

        // Power iteration from the box's diagonal finds the main axis.
        Span<float> covariance = stackalloc float[16];
        Covariance(pixels, channels, mean, covariance);
        PowerIterate(covariance, channels, axis);

        var (minimum, maximum) = Extent(pixels, channels, mean, axis);
        for (int c = 0; c < 4; c++)
        {
            start[c] = c < channels ? mean[c] + axis[c] * minimum : 255f;
            end[c] = c < channels ? mean[c] + axis[c] * maximum : 255f;
        }
    }

    /// <summary>The mean of each of the four channels, and the size of the box around the first <paramref name="channels"/>, which is its diagonal.</summary>
    private static void MeanAndSize(ReadOnlySpan<int> pixels, int channels, Span<float> mean, Span<float> size)
    {
        Span<int> low = stackalloc int[4] { 255, 255, 255, 255 };
        Span<int> high = stackalloc int[4];
        for (int i = 0; i < 16; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                int value = pixels[i * 4 + c];
                mean[c] += value;
                low[c] = Math.Min(low[c], value);
                high[c] = Math.Max(high[c], value);
            }
        }

        for (int c = 0; c < 4; c++)
        {
            mean[c] /= 16f;
        }

        for (int c = 0; c < channels; c++)
        {
            size[c] = high[c] - low[c];
        }
    }

    /// <summary>The covariance of the channels, as a 4 by 4 matrix, row by row.</summary>
    private static void Covariance(ReadOnlySpan<int> pixels, int channels, ReadOnlySpan<float> mean, Span<float> covariance)
    {
        // The upper triangle first, then the lower one copied from it.
        for (int i = 0; i < 16; i++)
        {
            for (int a = 0; a < channels; a++)
            {
                float da = pixels[i * 4 + a] - mean[a];
                for (int b = a; b < channels; b++)
                {
                    covariance[a * 4 + b] += da * (pixels[i * 4 + b] - mean[b]);
                }
            }
        }

        for (int a = 0; a < channels; a++)
        {
            for (int b = 0; b < a; b++)
            {
                covariance[a * 4 + b] = covariance[b * 4 + a];
            }
        }
    }

    /// <summary>Turns <paramref name="axis"/> toward the covariance's main axis, by multiplying it by the covariance a few times.</summary>
    private static void PowerIterate(ReadOnlySpan<float> covariance, int channels, Span<float> axis)
    {
        Span<float> next = stackalloc float[4];
        for (int iteration = 0; iteration < 8; iteration++)
        {
            float largest = 0;
            for (int a = 0; a < channels; a++)
            {
                float sum = 0;
                for (int b = 0; b < channels; b++)
                {
                    sum += covariance[a * 4 + b] * axis[b];
                }

                next[a] = sum;
                largest = Math.Max(largest, Math.Abs(sum));
            }

            if (largest <= 0)
            {
                break;
            }

            for (int c = 0; c < channels; c++)
            {
                axis[c] = next[c] / largest;
            }
        }
    }

    /// <summary>How far along the axis from the mean the block's pixels reach each way, in lengths of the axis. Zero both ways when the axis has no length.</summary>
    private static (float Minimum, float Maximum) Extent(ReadOnlySpan<int> pixels, int channels, ReadOnlySpan<float> mean, ReadOnlySpan<float> axis)
    {
        float length = 0;
        for (int c = 0; c < channels; c++)
        {
            length += axis[c] * axis[c];
        }

        float minimum = 0;
        float maximum = 0;
        if (length > 0)
        {
            minimum = float.MaxValue;
            maximum = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                float t = 0;
                for (int c = 0; c < channels; c++)
                {
                    t += (pixels[i * 4 + c] - mean[c]) * axis[c];
                }

                minimum = Math.Min(minimum, t);
                maximum = Math.Max(maximum, t);
            }

            minimum /= length;
            maximum /= length;
        }

        return (minimum, maximum);
    }

    /// <summary>
    /// The ends that best fit the block, by least squares, for each pixel's <paramref name="weights"/> toward the end from
    /// 0 to 1, or false when the weights don't say. <paramref name="pixels"/> starts at the first channel to fit.
    /// </summary>
    public static bool LeastSquares(ReadOnlySpan<int> pixels, int channels, ReadOnlySpan<float> weights, Span<float> start, Span<float> end)
    {
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[4];
        Span<float> bx = stackalloc float[4];
        for (int i = 0; i < 16; i++)
        {
            float w = weights[i];
            float v = 1f - w;
            aa += v * v;
            ab += v * w;
            bb += w * w;
            for (int c = 0; c < channels; c++)
            {
                ax[c] += v * pixels[i * 4 + c];
                bx[c] += w * pixels[i * 4 + c];
            }
        }

        float determinant = aa * bb - ab * ab;
        if (Math.Abs(determinant) < 1e-6f)
        {
            return false;
        }

        for (int c = 0; c < channels; c++)
        {
            start[c] = Math.Clamp((ax[c] * bb - bx[c] * ab) / determinant, 0f, 255f);
            end[c] = Math.Clamp((bx[c] * aa - ax[c] * ab) / determinant, 0f, 255f);
        }

        return true;
    }

    /// <summary>How many pixels' decoded alpha lands on the other side of the threshold from their own.</summary>
    public static int Flips(ReadOnlySpan<int> pixels, ReadOnlySpan<int> decoded, int threshold)
    {
        int flips = 0;
        for (int i = 0; i < 16; i++)
        {
            if (pixels[i * 4 + 3] >= threshold != decoded[i] >= threshold)
            {
                flips++;
            }
        }

        return flips;
    }

    /// <summary>For each weight from 0 to 64, the index whose weight is nearest it.</summary>
    public static byte[] BuildNearestIndex(int[] weights)
    {
        var nearest = new byte[65];
        for (int weight = 0; weight <= 64; weight++)
        {
            int best = 0;
            for (int index = 1; index < weights.Length; index++)
            {
                if (Math.Abs(weights[index] - weight) < Math.Abs(weights[best] - weight))
                {
                    best = index;
                }
            }

            nearest[weight] = (byte)best;
        }

        return nearest;
    }

    public static void Put(ref UInt128 bits, ref int position, int value, int count)
    {
        bits |= (UInt128)(uint)value << position;
        position += count;
    }

    public static void Store(UInt128 bits, Span<byte> destination)
    {
        ulong lowHalf = (ulong)bits;
        ulong highHalf = (ulong)(bits >> 64);
        for (int i = 0; i < 8; i++)
        {
            destination[i] = (byte)(lowHalf >> (i * 8));
            destination[i + 8] = (byte)(highHalf >> (i * 8));
        }
    }
}
