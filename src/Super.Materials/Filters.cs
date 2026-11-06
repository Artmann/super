namespace Super.Materials;

public sealed partial class Gray
{
    /// <summary>
    /// Stretches <paramref name="inLow"/>..<paramref name="inHigh"/> onto <paramref name="outLow"/>..<paramref name="outHigh"/>,
    /// clamping the input first. A <paramref name="gamma"/> over 1 brightens the middle.
    /// </summary>
    public Gray Levels(float inLow = 0f, float inHigh = 1f, float outLow = 0f, float outHigh = 1f, float gamma = 1f)
    {
        if (!(inHigh != inLow))
        {
            throw new ArgumentException($"inHigh must differ from inLow, both are {inLow}.", nameof(inHigh));
        }

        if (!(gamma > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), FormattableString.Invariant($"gamma must be above 0, not {gamma}."));
        }

        return new Gray(new LevelsNode(Node, inLow, inHigh, outLow, outHigh, gamma));
    }

    /// <summary>Maps 0 to <paramref name="low"/> and 1 to <paramref name="high"/>. Values outside 0..1 carry on past them.</summary>
    public Gray Remap(float low, float high) => new(new MapNode(Node, v => low + (high - low) * v));

    /// <summary>One minus the value.</summary>
    public Gray Invert() => new(new MapNode(Node, v => 1f - v));

    /// <summary>
    /// Turns a gradient into a mask. A <paramref name="position"/> of 0.3 on an even gradient leaves 30% of it white.
    /// Low <paramref name="contrast"/> softens the edge.
    /// </summary>
    public Gray HistogramScan(float position, float contrast = 0.5f)
    {
        var t = 1f - position;
        var w = MathF.Max(1f - contrast, 0.001f);

        return new Gray(new MapNode(Node, v => Math.Clamp((v - t) / w + 0.5f, 0f, 1f)));
    }

    /// <summary>A Gaussian blur whose deviation is <paramref name="radius"/> of the image size. Wraps at the edges.</summary>
    public Gray Blur(float radius)
    {
        Arguments.Between(radius, 0f, 1f, nameof(radius));

        return new Gray(new BlurNode(Node, 1, radius));
    }
}

public sealed partial class Rgb
{
    /// <summary>A Gaussian blur of each channel, whose deviation is <paramref name="radius"/> of the image size. Wraps at the edges.</summary>
    public Rgb Blur(float radius)
    {
        Arguments.Between(radius, 0f, 1f, nameof(radius));

        return new Rgb(new BlurNode(Node, 3, radius));
    }
}

sealed class MapNode(Node input, Func<float, float> map) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            var start = y * context.Size;

            for (var i = start; i < start + context.Size; i++)
            {
                output[i] = map(inputs[0][i]);
            }
        });
    }
}

sealed class LevelsNode(Node input, float inLow, float inHigh, float outLow, float outHigh, float gamma) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var exponent = 1f / gamma;

        context.Rows(y =>
        {
            var start = y * context.Size;

            for (var i = start; i < start + context.Size; i++)
            {
                var t = Math.Clamp((inputs[0][i] - inLow) / (inHigh - inLow), 0f, 1f);

                output[i] = outLow + (outHigh - outLow) * (gamma == 1f ? t : DMath.Pow(t, exponent));
            }
        });
    }
}
