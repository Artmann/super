namespace Super.Materials;

/// <summary>How <see cref="GrayWarps.SlopeBlur"/> combines the reads along the slope.</summary>
public enum SlopeBlurMode
{
    Average,
    Min,
    Max
}

/// <summary>Moves a grey image's pixels by another image, written as <c>image.Warp(by, amount)</c>.</summary>
public static class GrayWarps
{
    /// <summary>
    /// Each pixel reads from where it is moved to by <paramref name="amount"/> times the slope of <paramref name="by"/>.
    /// A 0 to 1 ramp across the whole image moves pixels by <paramref name="amount"/> of the image. Wraps at the edges.
    /// </summary>
    public static Gray Warp(this Gray image, Gray by, float amount)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));

        return new Gray(new WarpNode(image.Node, 1, by.Node, amount));
    }

    /// <summary>
    /// Moves each pixel along <paramref name="angle"/> (degrees, counter-clockwise from +x, so 90 is up the image) by
    /// <paramref name="amount"/> of the image times the value of <paramref name="by"/> there. A <c>by</c> of 1 and an
    /// <paramref name="amount"/> of 0.25 shifts the image a quarter of its width. A negative amount moves the other way.
    /// Wraps at the edges, and the angle does not snap, since the image is moved rather than patterned.
    /// </summary>
    public static Gray DirectionalWarp(this Gray image, Gray by, float amount, float angle)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));
        Arguments.Finite(angle, nameof(angle));

        return new Gray(new DirectionalWarpNode(image.Node, 1, by.Node, amount, angle));
    }

    /// <summary>
    /// Blurs along the slope of <paramref name="slope"/>, reaching <paramref name="amount"/> of the image, from
    /// <paramref name="samples"/> reads. Where the slope is flat, the pixel is copied.
    /// </summary>
    public static Gray SlopeBlur(this Gray image, Gray slope, float amount, int samples = 16, SlopeBlurMode mode = SlopeBlurMode.Average)
    {
        Arguments.Between(amount, 0f, 1f, nameof(amount));
        Arguments.AtLeast(samples, 1, nameof(samples));

        return new Gray(new SlopeBlurNode(image.Node, 1, slope.Node, amount, samples, mode));
    }
}

public sealed partial class Rgb
{
    /// <summary>The same as <see cref="GrayWarps.Warp"/>, for each channel.</summary>
    public Rgb Warp(Gray by, float amount)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));

        return new Rgb(new WarpNode(Node, 3, by.Node, amount));
    }

    /// <summary>The same as <see cref="GrayWarps.DirectionalWarp"/>, for each channel.</summary>
    public Rgb DirectionalWarp(Gray by, float amount, float angle)
    {
        Arguments.Between(amount, -1f, 1f, nameof(amount));
        Arguments.Finite(angle, nameof(angle));

        return new Rgb(new DirectionalWarpNode(Node, 3, by.Node, amount, angle));
    }

    /// <summary>The same as <see cref="GrayWarps.SlopeBlur"/>, for each channel.</summary>
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
        var source = new InterleavedImage(inputs[0], size, Channels);
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
                    output[(y * size + x) * Channels + c] = source.Bilinear(c, u, v);
                }
            }
        });
    }
}

sealed class DirectionalWarpNode(Node input, int channels, Node by, float amount, float angle) : Node(channels, input, by)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = new InterleavedImage(inputs[0], size, Channels);
        var map = inputs[1];
        var radians = angle * (MathF.PI / 180f);

        // Up the image is towards row 0, so the row part of the direction is the negative of the sine.
        var dx = DMath.Cos(radians);
        var dy = -DMath.Sin(radians);

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                var move = amount * map[y * size + x];
                var u = (x + 0.5f) / size - move * dx;
                var v = (y + 0.5f) / size - move * dy;

                for (var c = 0; c < Channels; c++)
                {
                    output[(y * size + x) * Channels + c] = source.Bilinear(c, u, v);
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
        var pixels = new InterleavedImage(source, size, Channels);
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

                var line = new Line((x + 0.5f) / size, (y + 0.5f) / size, gx / length, gy / length);

                for (var c = 0; c < Channels; c++)
                {
                    // The first read is the pixel itself, taken exactly.
                    output[at + c] = Along(pixels, c, source[at + c], line);
                }
            }
        });
    }

    /// <summary>The channel's samples down the slope from a pixel, combined by the mode.</summary>
    float Along(InterleavedImage pixels, int channel, float first, Line line)
    {
        var sum = first;
        var low = first;
        var high = first;

        for (var i = 1; i < samples; i++)
        {
            var t = i * amount / samples;
            var value = pixels.Bilinear(channel, line.U + line.Dx * t, line.V + line.Dy * t);

            sum += value;
            low = MathF.Min(low, value);
            high = MathF.Max(high, value);
        }

        return mode switch
        {
            SlopeBlurMode.Min => low,
            SlopeBlurMode.Max => high,
            _ => sum / samples
        };
    }

    /// <summary>Where a pixel's samples start, and the way down the slope they step.</summary>
    readonly record struct Line(float U, float V, float Dx, float Dy);
}
