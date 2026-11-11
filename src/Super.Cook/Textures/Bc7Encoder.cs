namespace Super.Cook;

/// <summary>
/// A fast BC7 encoder. Every block is tried in mode 6: one pair of RGBA endpoints with 7 bits a channel and a shared
/// low bit each, and a 4-bit index a pixel. It fits the endpoints to the block's main axis, picks the low bits, then
/// refines the endpoints by least squares from the indices it chose. A block whose alpha varies is also tried in mode 5,
/// which gives one channel a line of its own, as a baked mask's four separate channels need, and keeps whichever is
/// closer. That is a hundred or more times faster than BCnEncoder.Net's search of every mode, and on the smooth, noisy
/// pictures material graphs make it loses about 1 dB. A block whose alpha is all 255 keeps it exactly. The output
/// depends only on the pixels, so the same texture always gives the same bytes.
/// </summary>
internal static class Bc7Encoder
{
    /// <summary>Changes whenever the bytes this writes for a texture change, so cached results made before miss.</summary>
    public const int Version = 1;

    private static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    // The index whose weight is nearest each weight from 0 to 64.
    private static readonly byte[] NearestIndex = BuildNearestIndex(Weights);

    private static readonly int[] Weights2 = [0, 21, 43, 64];

    private static readonly byte[] NearestIndex2 = BuildNearestIndex(Weights2);

    private const int RefineSteps = 2;

    /// <summary>
    /// Encodes an RGBA8 image, row 0 first, into BC7 blocks, left to right then top to bottom. Edges are padded by
    /// repeating the last pixel. An <paramref name="alphaThreshold"/> above 0 marks a cut-out, as <see cref="EncodeBlock"/> says.
    /// </summary>
    public static byte[] Encode(byte[] rgba, int width, int height, int alphaThreshold = 0)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"A {width}x{height} RGBA8 image is {width * height * 4} bytes, not {rgba.Length}.", nameof(rgba));
        }

        int across = (width + 3) / 4;
        int down = (height + 3) / 4;
        var output = new byte[across * down * 16];
        Parallel.For(0, down, row =>
        {
            Span<int> block = stackalloc int[64];
            for (int column = 0; column < across; column++)
            {
                Load(rgba, width, height, column * 4, row * 4, block);
                EncodeBlock(block, output.AsSpan((row * across + column) * 16, 16), alphaThreshold);
            }
        });
        return output;
    }

    private static void Load(byte[] rgba, int width, int height, int left, int top, Span<int> block)
    {
        for (int y = 0; y < 4; y++)
        {
            int row = Math.Min(top + y, height - 1) * width;
            for (int x = 0; x < 4; x++)
            {
                int source = (row + Math.Min(left + x, width - 1)) * 4;
                int target = (y * 4 + x) * 4;
                block[target] = rgba[source];
                block[target + 1] = rgba[source + 1];
                block[target + 2] = rgba[source + 2];
                block[target + 3] = rgba[source + 3];
            }
        }
    }

    /// <summary>
    /// Encodes one block of 16 RGBA pixels, as ints from 0 to 255, into 16 bytes of mode 6 or 5. With an
    /// <paramref name="alphaThreshold"/> above 0, the block is a cut-out's, and of the two modes the one that keeps more
    /// pixels on their side of that alpha byte wins.
    /// </summary>
    internal static void EncodeBlock(ReadOnlySpan<int> pixels, Span<byte> destination, int alphaThreshold = 0)
    {
        bool opaque = true;
        for (int i = 3; i < 64; i += 4)
        {
            if (pixels[i] != 255)
            {
                opaque = false;
                break;
            }
        }

        int channels = opaque ? 3 : 4;
        Span<float> start = stackalloc float[4];
        Span<float> end = stackalloc float[4];
        FitAxis(pixels, channels, start, end);

        var best = new Fit();
        Span<byte> indices = stackalloc byte[16];
        best.Error = long.MaxValue;
        TryEndpoints(pixels, opaque, start, end, ref best);

        for (int step = 0; step < RefineSteps; step++)
        {
            best.CopyIndices(indices);
            if (!LeastSquares(pixels, 0, channels, indices, Weights, start, end))
            {
                break;
            }

            long before = best.Error;
            TryEndpoints(pixels, opaque, start, end, ref best);
            if (best.Error >= before)
            {
                break;
            }
        }

        // A block whose alpha varies may have it vary apart from its colour, as a mask's four channels do. Mode 5 gives
        // one channel a line of its own, at the cost of fewer steps along each.
        if (!opaque && best.Error > 0)
        {
            var separate = new SeparateFit { Error = long.MaxValue };
            TrySeparate(pixels, ref separate, alphaThreshold);
            bool useSeparate = separate.Error < best.Error;
            if (alphaThreshold > 0)
            {
                // A cut-out cares most which side of its cutoff each pixel lands on, so the mode that moves fewer across
                // wins, and the error only breaks a tie.
                int separateFlips = Flips(pixels, SeparateAlphas(separate), alphaThreshold);
                int lineFlips = Flips(pixels, LineAlphas(best), alphaThreshold);
                if (separateFlips != lineFlips)
                {
                    useSeparate = separateFlips < lineFlips;
                }
            }

            if (useSeparate)
            {
                WriteSeparate(ref separate, destination);
                return;
            }
        }

        Write(ref best, destination);
    }

    // How many pixels' decoded alpha lands on the other side of the threshold from their own.
    private static int Flips(ReadOnlySpan<int> pixels, ReadOnlySpan<int> decoded, int threshold)
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

    // Each pixel's alpha as a mode 6 block of the fit decodes.
    private static int[] LineAlphas(Fit fit)
    {
        int a0 = fit.A0 * 2 + fit.P0;
        int a1 = fit.A1 * 2 + fit.P1;
        var alphas = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int w = Weights[(int)((fit.Indices >> (i * 4)) & 0xF)];
            alphas[i] = ((64 - w) * a0 + w * a1 + 32) >> 6;
        }

        return alphas;
    }

    // Each pixel's alpha as a mode 5 block of the fit decodes: from the alpha line, or with a rotation from the colour
    // channel it swapped alpha with.
    private static int[] SeparateAlphas(SeparateFit fit)
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
            int w = Weights2[(int)((indices >> (i * 2)) & 3)];
            alphas[i] = ((64 - w) * low + w * high + 32) >> 6;
        }

        return alphas;
    }

    /// <summary>A mode 5 encoding: one channel, swapped into alpha by the rotation, on a line of its own.</summary>
    private struct SeparateFit
    {
        public long Error;
        public int Rotation;
        public int R0, G0, B0, R1, G1, B1, A0, A1;
        public uint ColorIndices, AlphaIndices; // 2 bits a pixel, pixel 0 lowest.
    }

    private static void TrySeparate(ReadOnlySpan<int> pixels, ref SeparateFit best, int alphaThreshold)
    {
        int bestFlips = int.MaxValue;
        Span<int> rotated = stackalloc int[64];
        Span<float> start = stackalloc float[4];
        Span<float> end = stackalloc float[4];
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        Span<byte> indices = stackalloc byte[16];
        Span<int> colorEnds = stackalloc int[6];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            pixels.CopyTo(rotated);
            if (rotation > 0)
            {
                for (int i = 0; i < 16; i++)
                {
                    (rotated[i * 4 + rotation - 1], rotated[i * 4 + 3]) = (rotated[i * 4 + 3], rotated[i * 4 + rotation - 1]);
                }
            }

            // The colour: a line through three channels, with 7-bit ends.
            FitAxis(rotated, 3, start, end);
            long colorError = long.MaxValue;
            uint colorIndices = 0;
            for (int step = 0; step <= RefineSteps; step++)
            {
                if (step > 0)
                {
                    Unpack(colorIndices, indices);
                    if (!LeastSquares(rotated, 0, 3, indices, Weights2, start, end))
                    {
                        break;
                    }
                }

                for (int c = 0; c < 3; c++)
                {
                    low[c] = Expand7(Quantise7(start[c]));
                    high[c] = Expand7(Quantise7(end[c]));
                }

                long error = AssignLine(rotated, 0, 3, low, high, out uint assigned);
                if (error >= colorError)
                {
                    break;
                }

                colorError = error;
                colorIndices = assigned;
                for (int c = 0; c < 3; c++)
                {
                    colorEnds[c] = Quantise7(start[c]);
                    colorEnds[c + 3] = Quantise7(end[c]);
                }
            }

            // The alpha: its own ends, with 8 bits each.
            int minimum = 255, maximum = 0;
            for (int i = 0; i < 16; i++)
            {
                minimum = Math.Min(minimum, rotated[i * 4 + 3]);
                maximum = Math.Max(maximum, rotated[i * 4 + 3]);
            }

            start[0] = minimum;
            end[0] = maximum;
            long alphaError = long.MaxValue;
            uint alphaIndices = 0;
            int a0 = 0, a1 = 0;
            for (int step = 0; step <= RefineSteps; step++)
            {
                if (step > 0)
                {
                    Unpack(alphaIndices, indices);
                    if (!LeastSquares(rotated, 3, 1, indices, Weights2, start, end))
                    {
                        break;
                    }
                }

                low[0] = (int)MathF.Round(start[0], MidpointRounding.AwayFromZero);
                high[0] = (int)MathF.Round(end[0], MidpointRounding.AwayFromZero);
                long error = AssignLine(rotated, 3, 1, low, high, out uint assigned);
                if (error >= alphaError)
                {
                    break;
                }

                alphaError = error;
                alphaIndices = assigned;
                (a0, a1) = (low[0], high[0]);
            }

            var fit = new SeparateFit
            {
                Error = colorError + alphaError,
                Rotation = rotation,
                R0 = colorEnds[0], G0 = colorEnds[1], B0 = colorEnds[2],
                R1 = colorEnds[3], G1 = colorEnds[4], B1 = colorEnds[5],
                A0 = a0, A1 = a1,
                ColorIndices = colorIndices,
                AlphaIndices = alphaIndices,
            };

            // A cut-out's rotation is picked as its mode is: fewest pixels across the cutoff first, then error.
            int flips = alphaThreshold > 0 ? Flips(pixels, SeparateAlphas(fit), alphaThreshold) : 0;
            if (flips < bestFlips || (flips == bestFlips && fit.Error < best.Error))
            {
                best = fit;
                bestFlips = flips;
            }
        }
    }

    /// <summary>Gives each pixel the 2-bit index nearest it on the line from <paramref name="low"/> to <paramref name="high"/>, over <paramref name="count"/> channels from <paramref name="first"/>, and sums the squared error.</summary>
    private static long AssignLine(ReadOnlySpan<int> pixels, int first, int count, ReadOnlySpan<int> low, ReadOnlySpan<int> high, out uint indices)
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
                    along += (pixels[i * 4 + first + c] - low[c]) * (high[c] - low[c]);
                }

                index = NearestIndex2[along <= 0 ? 0 : Math.Min(64, (along * 64 + length / 2) / length)];
            }

            int w = Weights2[index];
            for (int c = 0; c < count; c++)
            {
                int difference = pixels[i * 4 + first + c] - (((64 - w) * low[c] + w * high[c] + 32) >> 6);
                error += difference * difference;
            }

            indices |= (uint)index << (i * 2);
        }

        return error;
    }

    private static void Unpack(uint indices, Span<byte> unpacked)
    {
        for (int i = 0; i < 16; i++)
        {
            unpacked[i] = (byte)((indices >> (i * 2)) & 3);
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

    /// <summary>Writes a mode 5 block. Each first index must have its top bit clear, so the colour's or alpha's ends swap when it doesn't.</summary>
    private static void WriteSeparate(ref SeparateFit fit, Span<byte> destination)
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
        Put(ref bits, ref position, 1 << 5, 6);
        Put(ref bits, ref position, fit.Rotation, 2);
        Put(ref bits, ref position, fit.R0, 7);
        Put(ref bits, ref position, fit.R1, 7);
        Put(ref bits, ref position, fit.G0, 7);
        Put(ref bits, ref position, fit.G1, 7);
        Put(ref bits, ref position, fit.B0, 7);
        Put(ref bits, ref position, fit.B1, 7);
        Put(ref bits, ref position, fit.A0, 8);
        Put(ref bits, ref position, fit.A1, 8);
        Put(ref bits, ref position, (int)(fit.ColorIndices & 1), 1);
        for (int i = 1; i < 16; i++)
        {
            Put(ref bits, ref position, (int)((fit.ColorIndices >> (i * 2)) & 3), 2);
        }

        Put(ref bits, ref position, (int)(fit.AlphaIndices & 1), 1);
        for (int i = 1; i < 16; i++)
        {
            Put(ref bits, ref position, (int)((fit.AlphaIndices >> (i * 2)) & 3), 2);
        }

        Store(bits, destination);
    }

    private static void Store(UInt128 bits, Span<byte> destination)
    {
        ulong lowHalf = (ulong)bits;
        ulong highHalf = (ulong)(bits >> 64);
        for (int i = 0; i < 8; i++)
        {
            destination[i] = (byte)(lowHalf >> (i * 8));
            destination[i + 8] = (byte)(highHalf >> (i * 8));
        }
    }

    /// <summary>The endpoints, low bits and indices of one way to encode a block, and its squared error.</summary>
    private struct Fit
    {
        public long Error;
        public int R0, G0, B0, A0, R1, G1, B1, A1;
        public int P0, P1;
        public ulong Indices; // 4 bits a pixel, pixel 0 lowest.

        public readonly void CopyIndices(Span<byte> indices)
        {
            for (int i = 0; i < 16; i++)
            {
                indices[i] = (byte)((Indices >> (i * 4)) & 0xF);
            }
        }
    }

    /// <summary>Puts the block's ends along the main axis of its colours, through their mean.</summary>
    private static void FitAxis(ReadOnlySpan<int> pixels, int channels, Span<float> start, Span<float> end)
    {
        Span<float> mean = stackalloc float[4];
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

        // The covariance, upper triangle only.
        Span<float> covariance = stackalloc float[16];
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

        // Power iteration from the box's diagonal finds the main axis.
        Span<float> axis = stackalloc float[4];
        Span<float> next = stackalloc float[4];
        for (int c = 0; c < channels; c++)
        {
            axis[c] = high[c] - low[c];
        }

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

        for (int c = 0; c < 4; c++)
        {
            start[c] = c < channels ? mean[c] + axis[c] * minimum : 255f;
            end[c] = c < channels ? mean[c] + axis[c] * maximum : 255f;
        }
    }

    /// <summary>The ends that best fit the block for the indices it has, by least squares, or false when the indices don't say.</summary>
    private static bool LeastSquares(
        ReadOnlySpan<int> pixels, int first, int channels, ReadOnlySpan<byte> indices, int[] weights, Span<float> start, Span<float> end)
    {
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[4];
        Span<float> bx = stackalloc float[4];
        for (int i = 0; i < 16; i++)
        {
            float w = weights[indices[i]] / 64f;
            float v = 1f - w;
            aa += v * v;
            ab += v * w;
            bb += w * w;
            for (int c = 0; c < channels; c++)
            {
                ax[c] += v * pixels[i * 4 + first + c];
                bx[c] += w * pixels[i * 4 + first + c];
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

    /// <summary>Quantises the ends with each choice of low bits, and keeps the best in <paramref name="best"/> when it beats it.</summary>
    private static void TryEndpoints(ReadOnlySpan<int> pixels, bool opaque, ReadOnlySpan<float> start, ReadOnlySpan<float> end, ref Fit best)
    {
        // An opaque block keeps alpha at 255, which needs both low bits set.
        int first = opaque ? 1 : 0;
        for (int p0 = first; p0 < 2; p0++)
        {
            for (int p1 = first; p1 < 2; p1++)
            {
                var fit = new Fit
                {
                    P0 = p0,
                    P1 = p1,
                    R0 = Quantise(start[0], p0),
                    G0 = Quantise(start[1], p0),
                    B0 = Quantise(start[2], p0),
                    A0 = opaque ? 127 : Quantise(start[3], p0),
                    R1 = Quantise(end[0], p1),
                    G1 = Quantise(end[1], p1),
                    B1 = Quantise(end[2], p1),
                    A1 = opaque ? 127 : Quantise(end[3], p1),
                };
                Assign(pixels, ref fit);
                if (fit.Error < best.Error)
                {
                    best = fit;
                }
            }
        }
    }

    private static int Quantise(float value, int lowBit)
    {
        return Math.Clamp((int)MathF.Round((value - lowBit) / 2f, MidpointRounding.AwayFromZero), 0, 127);
    }

    /// <summary>Gives each pixel the index nearest it on the line between the fit's ends, and sums the squared error.</summary>
    private static void Assign(ReadOnlySpan<int> pixels, ref Fit fit)
    {
        int r0 = fit.R0 * 2 + fit.P0, g0 = fit.G0 * 2 + fit.P0, b0 = fit.B0 * 2 + fit.P0, a0 = fit.A0 * 2 + fit.P0;
        int r1 = fit.R1 * 2 + fit.P1, g1 = fit.G1 * 2 + fit.P1, b1 = fit.B1 * 2 + fit.P1, a1 = fit.A1 * 2 + fit.P1;
        int dr = r1 - r0, dg = g1 - g0, db = b1 - b0, da = a1 - a0;
        int length = dr * dr + dg * dg + db * db + da * da;
        long error = 0;
        ulong indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = pixels[i * 4], g = pixels[i * 4 + 1], b = pixels[i * 4 + 2], a = pixels[i * 4 + 3];
            int index = 0;
            if (length > 0)
            {
                int along = (r - r0) * dr + (g - g0) * dg + (b - b0) * db + (a - a0) * da;
                int weight = Math.Clamp((along * 64 + length / 2) / length, 0, 64);
                if (along < 0)
                {
                    weight = 0;
                }

                index = NearestIndex[weight];
            }

            int w = Weights[index];
            int er = r - (((64 - w) * r0 + w * r1 + 32) >> 6);
            int eg = g - (((64 - w) * g0 + w * g1 + 32) >> 6);
            int eb = b - (((64 - w) * b0 + w * b1 + 32) >> 6);
            int ea = a - (((64 - w) * a0 + w * a1 + 32) >> 6);
            error += er * er + eg * eg + eb * eb + ea * ea;
            indices |= (ulong)index << (i * 4);
        }

        fit.Error = error;
        fit.Indices = indices;
    }

    /// <summary>Writes the fit as a mode 6 block. The first pixel's index must have its top bit clear, so the ends swap when it doesn't.</summary>
    private static void Write(ref Fit fit, Span<byte> destination)
    {
        if (((fit.Indices & 0xF) & 8) != 0)
        {
            (fit.R0, fit.R1) = (fit.R1, fit.R0);
            (fit.G0, fit.G1) = (fit.G1, fit.G0);
            (fit.B0, fit.B1) = (fit.B1, fit.B0);
            (fit.A0, fit.A1) = (fit.A1, fit.A0);
            (fit.P0, fit.P1) = (fit.P1, fit.P0);
            fit.Indices = ~fit.Indices;
        }

        UInt128 bits = 0;
        int position = 0;
        Put(ref bits, ref position, 1 << 6, 7);
        Put(ref bits, ref position, fit.R0, 7);
        Put(ref bits, ref position, fit.R1, 7);
        Put(ref bits, ref position, fit.G0, 7);
        Put(ref bits, ref position, fit.G1, 7);
        Put(ref bits, ref position, fit.B0, 7);
        Put(ref bits, ref position, fit.B1, 7);
        Put(ref bits, ref position, fit.A0, 7);
        Put(ref bits, ref position, fit.A1, 7);
        Put(ref bits, ref position, fit.P0, 1);
        Put(ref bits, ref position, fit.P1, 1);
        Put(ref bits, ref position, (int)(fit.Indices & 0x7), 3);
        for (int i = 1; i < 16; i++)
        {
            Put(ref bits, ref position, (int)((fit.Indices >> (i * 4)) & 0xF), 4);
        }

        Store(bits, destination);
    }

    private static void Put(ref UInt128 bits, ref int position, int value, int count)
    {
        bits |= (UInt128)(uint)value << position;
        position += count;
    }

    private static byte[] BuildNearestIndex(int[] weights)
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
}
