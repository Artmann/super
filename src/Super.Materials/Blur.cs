using System.Numerics;

namespace Super.Materials;

/// <summary>
/// A separable Gaussian blur that wraps, for any number of interleaved channels. Because the image wraps, taps that land
/// on the same pixel are added into one, so a pass never takes more than <c>size</c> taps, however wide the blur.
/// </summary>
sealed class BlurNode(Node input, int channels, float radius) : Node(channels, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];

        if (radius == 0f)
        {
            Array.Copy(source, output, size * size * Channels);

            return;
        }

        var (offsets, weights) = Taps(radius * size, size);
        var across = new float[size * size * Channels];

        // Each pixel adds its taps in the same order in both passes, whatever the thread count, so the bytes are the same.
        BlurAcross(context, source, across, offsets, weights);
        BlurDown(context, across, output, offsets, weights);
    }

    /// <summary>The first pass: blurs each row of <paramref name="source"/> along x into <paramref name="across"/>, which starts as zeros.</summary>
    void BlurAcross(EvaluationContext context, float[] source, float[] across, int[] offsets, float[] weights)
    {
        var size = context.Size;
        var pixelLength = Channels;
        var rowLength = size * pixelLength;

        context.Rows(y =>
        {
            var row = new Span<float>(across, y * rowLength, rowLength);
            var from = new ReadOnlySpan<float>(source, y * rowLength, rowLength);

            for (var i = 0; i < offsets.Length; i++)
            {
                // Pixel x reads pixel x + offset, which wraps: the row's tail first, then its head.
                var split = Sampling.Wrap(offsets[i], size) * pixelLength;

                AddScaled(row[..(rowLength - split)], from[split..], weights[i]);
                AddScaled(row[(rowLength - split)..], from[..split], weights[i]);
            }
        });
    }

    /// <summary>The second pass: blurs <paramref name="across"/> along y into <paramref name="output"/>.</summary>
    void BlurDown(EvaluationContext context, float[] across, float[] output, int[] offsets, float[] weights)
    {
        var size = context.Size;
        var rowLength = size * Channels;

        context.Rows(y =>
        {
            var row = new Span<float>(output, y * rowLength, rowLength);

            row.Clear();

            for (var i = 0; i < offsets.Length; i++)
            {
                var from = Sampling.Wrap(y + offsets[i], size);

                AddScaled(row, new ReadOnlySpan<float>(across, from * rowLength, rowLength), weights[i]);
            }
        });
    }

    /// <summary>Adds <paramref name="weight"/> times each value of <paramref name="from"/> to <paramref name="to"/>.</summary>
    static void AddScaled(Span<float> to, ReadOnlySpan<float> from, float weight)
    {
        var i = 0;

        // A multiply then an add, never fused, so the vector lanes round exactly as the plain loop does.
        if (Vector.IsHardwareAccelerated)
        {
            var scale = new Vector<float>(weight);

            for (; i <= to.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var sum = new Vector<float>(to[i..]) + scale * new Vector<float>(from[i..]);

                sum.CopyTo(to[i..]);
            }
        }

        for (; i < to.Length; i++)
        {
            to[i] += weight * from[i];
        }
    }

    /// <summary>
    /// Pixel offsets and their normalised weights, from -3 deviations to +3, so the total is kept. When the kernel is
    /// wider than the image, offsets a whole image apart land on the same pixel, so their weights are added into one tap.
    /// </summary>
    static (int[] Offsets, float[] Weights) Taps(float sigma, int size)
    {
        var half = (int)MathF.Ceiling(3f * sigma);
        var (weights, total) = Kernel(sigma, half);

        if (weights.Length <= size)
        {
            return Normalised(weights, total, -half);
        }

        // Folded: one tap for each pixel of the row, offsets 0 up to size.
        var folded = new double[size];

        for (var k = -half; k <= half; k++)
        {
            folded[Sampling.Wrap(k, size)] += weights[k + half];
        }

        return Normalised(folded, total, 0);
    }

    /// <summary>The Gaussian's weights from offset -<paramref name="half"/> to +<paramref name="half"/>, and their sum.</summary>
    static (double[] Weights, double Total) Kernel(float sigma, int half)
    {
        var weights = new double[half * 2 + 1];
        double total = 0;

        for (var k = -half; k <= half; k++)
        {
            var weight = DMath.Exp((float)(-(double)k * k / (2.0 * sigma * sigma)));

            weights[k + half] = weight;
            total += weight;
        }

        return (weights, total);
    }

    /// <summary>One tap per weight, the first at offset <paramref name="first"/>, each weight divided by <paramref name="total"/>.</summary>
    static (int[] Offsets, float[] Weights) Normalised(double[] weights, double total, int first)
    {
        var offsets = new int[weights.Length];
        var result = new float[weights.Length];

        for (var i = 0; i < result.Length; i++)
        {
            offsets[i] = first + i;
            result[i] = (float)(weights[i] / total);
        }

        return (offsets, result);
    }
}
