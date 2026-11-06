namespace Super.Materials;

/// <summary>How <see cref="Gray.SlopeBlur"/> combines the reads along the slope.</summary>
public enum SlopeBlurMode
{
    Average,
    Min,
    Max
}

public sealed partial class Gray
{
    /// <summary>
    /// Each pixel reads from where it is moved to by <paramref name="amount"/> times the slope of <paramref name="by"/>.
    /// A 0 to 1 ramp across the whole image moves pixels by <paramref name="amount"/> of the image. Wraps at the edges.
    /// </summary>
    public Gray Warp(Gray by, float amount)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));

        return new Gray(new WarpNode(Node, 1, by.Node, amount));
    }

    /// <summary>
    /// Blurs along the slope of <paramref name="slope"/>, reaching <paramref name="amount"/> of the image, from
    /// <paramref name="samples"/> reads. Where the slope is flat, the pixel is copied.
    /// </summary>
    public Gray SlopeBlur(Gray slope, float amount, int samples = 16, SlopeBlurMode mode = SlopeBlurMode.Average)
    {
        Arguments.Between(amount, 0f, 1f, nameof(amount));
        Arguments.AtLeast(samples, 1, nameof(samples));

        return new Gray(new SlopeBlurNode(Node, 1, slope.Node, amount, samples, mode));
    }
}

public sealed partial class Rgb
{
    /// <summary>The same as <see cref="Gray.Warp"/>, for each channel.</summary>
    public Rgb Warp(Gray by, float amount)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));

        return new Rgb(new WarpNode(Node, 3, by.Node, amount));
    }

    /// <summary>The same as <see cref="Gray.SlopeBlur"/>, for each channel.</summary>
    public Rgb SlopeBlur(Gray slope, float amount, int samples = 16, SlopeBlurMode mode = SlopeBlurMode.Average)
    {
        Arguments.Between(amount, 0f, 1f, nameof(amount));
        Arguments.AtLeast(samples, 1, nameof(samples));

        return new Rgb(new SlopeBlurNode(Node, 3, slope.Node, amount, samples, mode));
    }
}

static class Slope
{
    /// <summary>The central difference at a pixel, per image width, wrapping at the edges.</summary>
    public static (float X, float Y) At(float[] image, int size, int x, int y)
    {
        var dx = image[y * size + Sampling.Wrap(x + 1, size)] - image[y * size + Sampling.Wrap(x - 1, size)];
        var dy = image[Sampling.Wrap(y + 1, size) * size + x] - image[Sampling.Wrap(y - 1, size) * size + x];

        return (dx * 0.5f * size, dy * 0.5f * size);
    }
}

sealed class WarpNode(Node input, int channels, Node by, float amount) : Node(channels, input, by)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var map = inputs[1];

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                var (gx, gy) = Slope.At(map, size, x, y);
                var u = (x + 0.5f) / size - amount * gx;
                var v = (y + 0.5f) / size - amount * gy;

                for (var c = 0; c < Channels; c++)
                {
                    output[(y * size + x) * Channels + c] = Sampling.Bilinear(source, size, Channels, c, u, v);
                }
            }
        });
    }
}

sealed class SlopeBlurNode(Node input, int channels, Node slope, float amount, int samples, SlopeBlurMode mode)
    : Node(channels, input, slope)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var map = inputs[1];

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                var (gx, gy) = Slope.At(map, size, x, y);
                var length = MathF.Sqrt(gx * gx + gy * gy);
                var at = (y * size + x) * Channels;

                if (length == 0f)
                {
                    for (var c = 0; c < Channels; c++)
                    {
                        output[at + c] = source[at + c];
                    }

                    continue;
                }

                var dx = gx / length;
                var dy = gy / length;
                var u = (x + 0.5f) / size;
                var v = (y + 0.5f) / size;

                for (var c = 0; c < Channels; c++)
                {
                    // The first read is the pixel itself, taken exactly.
                    var first = source[at + c];
                    var sum = first;
                    var low = first;
                    var high = first;

                    for (var i = 1; i < samples; i++)
                    {
                        var t = i * amount / samples;
                        var value = Sampling.Bilinear(source, size, Channels, c, u + dx * t, v + dy * t);

                        sum += value;
                        low = MathF.Min(low, value);
                        high = MathF.Max(high, value);
                    }

                    output[at + c] = mode switch
                    {
                        SlopeBlurMode.Min => low,
                        SlopeBlurMode.Max => high,
                        _ => sum / samples
                    };
                }
            }
        });
    }
}
