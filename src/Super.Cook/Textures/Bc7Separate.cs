namespace Super.Cook;

/// <summary>
/// BC7's mode 5, as <see cref="Bc7Encoder"/> uses it: a colour line through three channels with 7-bit ends, and an alpha
/// line of its own with 8-bit ends, each with a 2-bit index a pixel. A rotation can swap alpha with one of the colour
/// channels first, so any one channel can have the line of its own.
/// </summary>
internal static class Bc7Separate
{
    private static readonly int[] Weights = [0, 21, 43, 64];

    private static readonly byte[] NearestIndex = Bc7Lines.BuildNearestIndex(Weights);

    /// <summary>A mode 5 encoding: one channel, swapped into alpha by the rotation, on a line of its own.</summary>
    internal struct SeparateFit
    {
        public long Error;
        public int Rotation;
        public int R0, G0, B0, R1, G1, B1, A0, A1;
        public uint ColorIndices, AlphaIndices; // 2 bits a pixel, pixel 0 lowest.
    }

    /// <summary>
    /// The best mode 5 encoding of the block over every rotation. With an <paramref name="alphaThreshold"/> above 0 the
    /// block is a cut-out's, and the rotation is picked as its mode is: fewest pixels across the cutoff first, then error.
    /// </summary>
    public static SeparateFit Best(ReadOnlySpan<int> pixels, int alphaThreshold)
    {
        var best = new SeparateFit { Error = long.MaxValue };
        int bestFlips = int.MaxValue;
        Span<int> rotated = stackalloc int[64];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Rotate(pixels, rotation, rotated);
            var fit = new SeparateFit { Rotation = rotation };
            long colorError = FitColor(rotated, ref fit);
            long alphaError = FitAlpha(rotated, ref fit);
            fit.Error = colorError + alphaError;

            int flips = alphaThreshold > 0 ? Bc7Lines.Flips(pixels, Alphas(fit), alphaThreshold) : 0;
            if (flips < bestFlips || (flips == bestFlips && fit.Error < best.Error))
            {
                best = fit;
                bestFlips = flips;
            }
        }

        return best;
    }

    /// <summary>Copies the pixels, with alpha swapped with red, green or blue for rotation 1, 2 or 3.</summary>
    private static void Rotate(ReadOnlySpan<int> pixels, int rotation, Span<int> rotated)
    {
        pixels.CopyTo(rotated);
        if (rotation == 0)
        {
            return;
        }

        for (int i = 0; i < 16; i++)
        {
            (rotated[i * 4 + rotation - 1], rotated[i * 4 + 3]) = (rotated[i * 4 + 3], rotated[i * 4 + rotation - 1]);
        }
    }

    /// <summary>Fits the colour: a line through three channels, with 7-bit ends. Gives its squared error.</summary>
    private static long FitColor(ReadOnlySpan<int> pixels, ref SeparateFit fit)
    {
        Span<float> start = stackalloc float[4];
        Span<float> end = stackalloc float[4];
        Span<int> ends = stackalloc int[6];
        Span<int> expanded = stackalloc int[6];
        Span<float> weights = stackalloc float[16];
        Bc7Lines.FitAxis(pixels, 3, start, end);
        long colorError = long.MaxValue;
        for (int step = 0; step <= Bc7Lines.RefineSteps; step++)
        {
            if (step > 0)
            {
                Unpack(fit.ColorIndices, weights);
                if (!Bc7Lines.LeastSquares(pixels, 3, weights, start, end))
                {
                    break;
                }
            }

            for (int c = 0; c < 3; c++)
            {
                ends[c] = Quantise7(start[c]);
                ends[c + 3] = Quantise7(end[c]);
                expanded[c] = Expand7(ends[c]);
                expanded[c + 3] = Expand7(ends[c + 3]);
            }

            long error = AssignLine(pixels, 3, expanded[..3], expanded[3..], out uint assigned);
            if (error >= colorError)
            {
                break;
            }

            colorError = error;
            fit.ColorIndices = assigned;
            (fit.R0, fit.G0, fit.B0, fit.R1, fit.G1, fit.B1) = (ends[0], ends[1], ends[2], ends[3], ends[4], ends[5]);
        }

        return colorError;
    }

    /// <summary>Fits the alpha: its own line, with 8-bit ends. Gives its squared error.</summary>
    private static long FitAlpha(ReadOnlySpan<int> pixels, ref SeparateFit fit)
    {
        int minimum = 255, maximum = 0;
        for (int i = 0; i < 16; i++)
        {
            minimum = Math.Min(minimum, pixels[i * 4 + 3]);
            maximum = Math.Max(maximum, pixels[i * 4 + 3]);
        }

        Span<float> start = stackalloc float[] { minimum };
        Span<float> end = stackalloc float[] { maximum };
        Span<int> ends = stackalloc int[2];
        Span<float> weights = stackalloc float[16];
        var alpha = pixels[3..];
        long alphaError = long.MaxValue;
        for (int step = 0; step <= Bc7Lines.RefineSteps; step++)
        {
            if (step > 0)
            {
                Unpack(fit.AlphaIndices, weights);
                if (!Bc7Lines.LeastSquares(alpha, 1, weights, start, end))
                {
                    break;
                }
            }

            ends[0] = (int)MathF.Round(start[0], MidpointRounding.AwayFromZero);
            ends[1] = (int)MathF.Round(end[0], MidpointRounding.AwayFromZero);
            long error = AssignLine(alpha, 1, ends[..1], ends[1..], out uint assigned);
            if (error >= alphaError)
            {
                break;
            }

            alphaError = error;
            fit.AlphaIndices = assigned;
            (fit.A0, fit.A1) = (ends[0], ends[1]);
        }

        return alphaError;
    }

    /// <summary>
    /// Gives each pixel the 2-bit index nearest it on the line from <paramref name="low"/> to <paramref name="high"/>, over
    /// <paramref name="count"/> channels from the first in <paramref name="pixels"/>, and sums the squared error.
    /// </summary>
    private static long AssignLine(ReadOnlySpan<int> pixels, int count, ReadOnlySpan<int> low, ReadOnlySpan<int> high, out uint indices)
    {
        int length = 0;
        for (int c = 0; c < count; c++)
        {
            length += (high[c] - low[c]) * (high[c] - low[c]);
        }

        long error = 0;
        indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int index = 0;
            if (length > 0)
            {
                int along = 0;
                for (int c = 0; c < count; c++)
                {
                    along += (pixels[i * 4 + c] - low[c]) * (high[c] - low[c]);
                }

                index = NearestIndex[along <= 0 ? 0 : Math.Min(64, (along * 64 + length / 2) / length)];
            }

            int w = Weights[index];
            for (int c = 0; c < count; c++)
            {
                int difference = pixels[i * 4 + c] - (((64 - w) * low[c] + w * high[c] + 32) >> 6);
                error += difference * difference;
            }

            indices |= (uint)index << (i * 2);
        }

        return error;
    }

    /// <summary>Each pixel's weight toward the end, from 0 to 1, for its 2-bit index.</summary>
    private static void Unpack(uint indices, Span<float> weights)
    {
        for (int i = 0; i < 16; i++)
        {
            weights[i] = Weights[(int)((indices >> (i * 2)) & 3)] / 64f;
        }
    }

    /// <summary>The 7-bit value whose 8-bit expansion is nearest <paramref name="value"/>.</summary>
    private static int Quantise7(float value)
    {
        int guess = Math.Clamp((int)MathF.Round(value * 127f / 255f, MidpointRounding.AwayFromZero), 0, 127);
        int best = guess;
        for (int candidate = Math.Max(guess - 1, 0); candidate <= Math.Min(guess + 1, 127); candidate++)
        {
            if (Math.Abs(Expand7(candidate) - value) < Math.Abs(Expand7(best) - value))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int Expand7(int value) => (value << 1) | (value >> 6);

    /// <summary>
    /// Each pixel's alpha as a mode 5 block of the fit decodes: from the alpha line, or with a rotation from the colour
    /// channel it swapped alpha with.
    /// </summary>
    public static int[] Alphas(SeparateFit fit)
    {
        int low = fit.A0;
        int high = fit.A1;
        uint indices = fit.AlphaIndices;
        if (fit.Rotation > 0)
        {
            (low, high) = fit.Rotation switch
            {
                1 => (Expand7(fit.R0), Expand7(fit.R1)),
                2 => (Expand7(fit.G0), Expand7(fit.G1)),
                _ => (Expand7(fit.B0), Expand7(fit.B1)),
            };
            indices = fit.ColorIndices;
        }

        var alphas = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int w = Weights[(int)((indices >> (i * 2)) & 3)];
            alphas[i] = ((64 - w) * low + w * high + 32) >> 6;
        }

        return alphas;
    }

    /// <summary>Writes a mode 5 block. Each first index must have its top bit clear, so the colour's or alpha's ends swap when it doesn't.</summary>
    public static void Write(ref SeparateFit fit, Span<byte> destination)
    {
        if ((fit.ColorIndices & 2) != 0)
        {
            (fit.R0, fit.R1) = (fit.R1, fit.R0);
            (fit.G0, fit.G1) = (fit.G1, fit.G0);
            (fit.B0, fit.B1) = (fit.B1, fit.B0);
            fit.ColorIndices = ~fit.ColorIndices;
        }

        if ((fit.AlphaIndices & 2) != 0)
        {
            (fit.A0, fit.A1) = (fit.A1, fit.A0);
            fit.AlphaIndices = ~fit.AlphaIndices;
        }

        UInt128 bits = 0;
        int position = 0;
        Bc7Lines.Put(ref bits, ref position, 1 << 5, 6);
        Bc7Lines.Put(ref bits, ref position, fit.Rotation, 2);
        Bc7Lines.Put(ref bits, ref position, fit.R0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.R1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.G0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.G1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.B0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.B1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.A0, 8);
        Bc7Lines.Put(ref bits, ref position, fit.A1, 8);
        PutIndices(ref bits, ref position, fit.ColorIndices);
        PutIndices(ref bits, ref position, fit.AlphaIndices);
        Bc7Lines.Store(bits, destination);
    }

    /// <summary>Puts 16 2-bit indices, the first with its top bit left out, as it's always clear.</summary>
    private static void PutIndices(ref UInt128 bits, ref int position, uint indices)
    {
        Bc7Lines.Put(ref bits, ref position, (int)(indices & 1), 1);
        for (int i = 1; i < 16; i++)
        {
            Bc7Lines.Put(ref bits, ref position, (int)((indices >> (i * 2)) & 3), 2);
        }
    }
}
