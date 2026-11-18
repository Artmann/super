namespace Super.Cook;

/// <summary>
/// A fast BC7 encoder. Every block is tried in mode 6: one pair of RGBA endpoints with 7 bits a channel and a shared
/// low bit each, and a 4-bit index a pixel. It fits the endpoints to the block's main axis, picks the low bits, then
/// refines the endpoints by least squares from the indices it chose. A block whose alpha varies is also tried in mode 5,
/// which gives one channel a line of its own, as a baked mask's four separate channels need, and keeps whichever is
/// closer (<see cref="Bc7Separate"/>). That is a hundred or more times faster than BCnEncoder.Net's search of every
/// mode, and on the smooth, noisy pictures material graphs make it loses about 1 dB. A block whose alpha is all 255
/// keeps it exactly. The output depends only on the pixels, so the same texture always gives the same bytes.
/// </summary>
internal static class Bc7Encoder
{
    /// <summary>Changes whenever the bytes this writes for a texture change, so cached results made before miss.</summary>
    public const int Version = 1;

    private static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    // The index whose weight is nearest each weight from 0 to 64.
    private static readonly byte[] NearestIndex = Bc7Lines.BuildNearestIndex(Weights);

    /// <summary>An RGBA8 image, row 0 first.</summary>
    private readonly record struct Image(byte[] Rgba, int Width, int Height);

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

        var image = new Image(rgba, width, height);
        int across = (width + 3) / 4;
        int down = (height + 3) / 4;
        var output = new byte[across * down * 16];
        Parallel.For(0, down, row =>
        {
            Span<int> block = stackalloc int[64];
            for (int column = 0; column < across; column++)
            {
                Load(image, column * 4, row * 4, block);
                EncodeBlock(block, output.AsSpan((row * across + column) * 16, 16), alphaThreshold);
            }
        });
        return output;
    }

    private static void Load(Image image, int left, int top, Span<int> block)
    {
        for (int y = 0; y < 4; y++)
        {
            int row = Math.Min(top + y, image.Height - 1) * image.Width;
            for (int x = 0; x < 4; x++)
            {
                int source = (row + Math.Min(left + x, image.Width - 1)) * 4;
                int target = (y * 4 + x) * 4;
                block[target] = image.Rgba[source];
                block[target + 1] = image.Rgba[source + 1];
                block[target + 2] = image.Rgba[source + 2];
                block[target + 3] = image.Rgba[source + 3];
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
        bool opaque = IsOpaque(pixels);
        var best = FitLine(pixels, opaque);

        // A block whose alpha varies may have it vary apart from its colour, as a mask's four channels do. Mode 5 gives
        // one channel a line of its own, at the cost of fewer steps along each.
        if (!opaque && best.Error > 0)
        {
            var separate = Bc7Separate.Best(pixels, alphaThreshold);
            if (PrefersSeparate(pixels, best, separate, alphaThreshold))
            {
                Bc7Separate.Write(ref separate, destination);
                return;
            }
        }

        Write(ref best, destination);
    }

    private static bool IsOpaque(ReadOnlySpan<int> pixels)
    {
        for (int i = 3; i < 64; i += 4)
        {
            if (pixels[i] != 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The best mode 6 fit: ends along the block's main axis, then refined from the indices they give.</summary>
    private static Fit FitLine(ReadOnlySpan<int> pixels, bool opaque)
    {
        int channels = opaque ? 3 : 4;
        Span<float> start = stackalloc float[4];
        Span<float> end = stackalloc float[4];
        Bc7Lines.FitAxis(pixels, channels, start, end);

        var best = new Fit();
        Span<float> weights = stackalloc float[16];
        best.Error = long.MaxValue;
        TryEndpoints(pixels, opaque, start, end, ref best);

        for (int step = 0; step < Bc7Lines.RefineSteps; step++)
        {
            best.CopyWeights(weights);
            if (!Bc7Lines.LeastSquares(pixels, channels, weights, start, end))
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

        return best;
    }

    /// <summary>
    /// Whether mode 5 beats mode 6 for the block. A cut-out cares most which side of its cutoff each pixel lands on, so
    /// the mode that moves fewer across wins, and the error only breaks a tie.
    /// </summary>
    private static bool PrefersSeparate(ReadOnlySpan<int> pixels, Fit line, Bc7Separate.SeparateFit separate, int alphaThreshold)
    {
        if (alphaThreshold > 0)
        {
            int separateFlips = Bc7Lines.Flips(pixels, Bc7Separate.Alphas(separate), alphaThreshold);
            int lineFlips = Bc7Lines.Flips(pixels, LineAlphas(line), alphaThreshold);
            if (separateFlips != lineFlips)
            {
                return separateFlips < lineFlips;
            }
        }

        return separate.Error < line.Error;
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

    /// <summary>The endpoints, low bits and indices of one way to encode a block, and its squared error.</summary>
    private struct Fit
    {
        public long Error;
        public int R0, G0, B0, A0, R1, G1, B1, A1;
        public int P0, P1;
        public ulong Indices; // 4 bits a pixel, pixel 0 lowest.

        /// <summary>Each pixel's weight toward the second end, from 0 to 1, for its index.</summary>
        public readonly void CopyWeights(Span<float> weights)
        {
            for (int i = 0; i < 16; i++)
            {
                weights[i] = Weights[(int)((Indices >> (i * 4)) & 0xF)] / 64f;
            }
        }
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
        Bc7Lines.Put(ref bits, ref position, 1 << 6, 7);
        Bc7Lines.Put(ref bits, ref position, fit.R0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.R1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.G0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.G1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.B0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.B1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.A0, 7);
        Bc7Lines.Put(ref bits, ref position, fit.A1, 7);
        Bc7Lines.Put(ref bits, ref position, fit.P0, 1);
        Bc7Lines.Put(ref bits, ref position, fit.P1, 1);
        Bc7Lines.Put(ref bits, ref position, (int)(fit.Indices & 0x7), 3);
        for (int i = 1; i < 16; i++)
        {
            Bc7Lines.Put(ref bits, ref position, (int)((fit.Indices >> (i * 4)) & 0xF), 4);
        }

        Bc7Lines.Store(bits, destination);
    }
}
