namespace Super.Materials;

/// <summary>A separable Gaussian blur that wraps, for any number of interleaved channels.</summary>
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

        var weights = Kernel(radius * size);
        var half = weights.Length / 2;
        var rowLength = size * Channels;
        var across = new float[size * size * Channels];

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                for (var c = 0; c < Channels; c++)
                {
                    float sum = 0;

                    for (var k = -half; k <= half; k++)
                    {
                        sum += weights[k + half] * source[y * rowLength + Wrap(x + k, size) * Channels + c];
                    }

                    across[y * rowLength + x * Channels + c] = sum;
                }
            }
        });

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                for (var c = 0; c < Channels; c++)
                {
                    float sum = 0;

                    for (var k = -half; k <= half; k++)
                    {
                        sum += weights[k + half] * across[Wrap(y + k, size) * rowLength + x * Channels + c];
                    }

                    output[y * rowLength + x * Channels + c] = sum;
                }
            }
        });
    }

    /// <summary>Normalised weights from -3 deviations to +3, so the total is kept.</summary>
    static float[] Kernel(float sigma)
    {
        var half = (int)MathF.Ceiling(3f * sigma);
        var weights = new double[half * 2 + 1];
        double total = 0;

        for (var k = -half; k <= half; k++)
        {
            var weight = DMath.Exp((float)(-(double)k * k / (2.0 * sigma * sigma)));

            weights[k + half] = weight;
            total += weight;
        }

        var result = new float[weights.Length];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)(weights[i] / total);
        }

        return result;
    }

    // A kernel can be wider than the image, so one wrap is not enough.
    static int Wrap(int index, int size) => ((index % size) + size) % size;
}
