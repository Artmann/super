namespace Super.Materials;

public sealed partial class Gray
{
    /// <summary>
    /// A mask that grows out from the bright parts. Pixels above 0.5 are 1. Any other pixel is
    /// <c>1 − d / reach</c>, held at 0 or above, where d is the straight-line distance to the nearest pixel above 0.5
    /// as a fraction of the image. The distance is measured between pixel centres and wraps at the edges.
    /// <paramref name="reach"/> is more than 0 and at most 0.5. With no pixel above 0.5, the rest is 0.
    /// </summary>
    public Gray Distance(float reach)
    {
        Arguments.MoreThanUpTo(reach, 0f, 0.5f, nameof(reach));

        return new Gray(new DistanceNode(Node, reach));
    }

    /// <summary>
    /// 1 where the image crosses 0.5 within <paramref name="width"/> of the image, else 0. A pixel is 1 when some
    /// pixel within that straight-line distance is on the other side of 0.5 (above it, or at or below it). The
    /// distance wraps at the edges, and is at least one pixel so a thin width still finds the pixel next door.
    /// <paramref name="width"/> must be more than 0.
    /// </summary>
    public Gray EdgeDetect(float width = 0.01f)
    {
        Arguments.MoreThan(width, 0f, nameof(width));

        return new Gray(new EdgeDetectNode(Node, width));
    }
}

/// <summary>An exact Euclidean distance transform that wraps at the edges.</summary>
static class DistanceTransform
{
    // Larger than any squared distance, yet small enough that adding squares to it stays exact enough to order.
    const double Far = 1e18;

    /// <summary>
    /// For every pixel, the squared distance in pixels to the nearest pixel where <paramref name="seeds"/> is set,
    /// or infinity when none is. Two passes of a 1D lower envelope of parabolas, one down the columns and one along
    /// the rows, each over a tripled line so the nearest seed can be on the other side of the edge.
    /// The cost is a few operations per pixel, so it is the same at any reach.
    /// </summary>
    public static float[] Squared(EvaluationContext context, bool[] seeds)
    {
        var size = context.Size;
        var result = new float[size * size];

        if (!seeds.Contains(true))
        {
            Array.Fill(result, float.PositiveInfinity);

            return result;
        }

        // Squared distances along a column are at most (size / 2)^2, so a float holds them exactly up to size 8192.
        var columns = new float[size * size];

        context.Rows(() => new Line(size), (x, line) =>
        {
            for (var k = 0; k < 3 * size; k++)
            {
                line.F[k] = seeds[k % size * size + x] ? 0.0 : Far;
            }

            line.Run();

            for (var y = 0; y < size; y++)
            {
                var d = line.D[size + y];

                // A column with no seed holds about Far, which a float would round, so it is stored as infinity.
                columns[y * size + x] = d >= Far / 2 ? float.PositiveInfinity : (float)d;
            }
        });

        context.Rows(() => new Line(size), (y, line) =>
        {
            for (var k = 0; k < 3 * size; k++)
            {
                line.F[k] = float.IsPositiveInfinity(columns[y * size + k % size]) ? Far : columns[y * size + k % size];
            }

            line.Run();

            for (var x = 0; x < size; x++)
            {
                result[y * size + x] = (float)line.D[size + x];
            }
        });

        return result;
    }

    sealed class Line(int size)
    {
        readonly int _n = 3 * size;
        readonly int[] _v = new int[3 * size];
        readonly double[] _z = new double[3 * size + 1];

        public double[] F { get; } = new double[3 * size];

        public double[] D { get; } = new double[3 * size];

        public void Run()
        {
            var k = 0;

            _v[0] = 0;
            _z[0] = double.NegativeInfinity;
            _z[1] = double.PositiveInfinity;

            for (var q = 1; q < _n; q++)
            {
                double s;

                while (true)
                {
                    var p = _v[k];

                    s = (F[q] + (double)q * q - (F[p] + (double)p * p)) / (2.0 * q - 2.0 * p);

                    if (s <= _z[k])
                    {
                        k--;

                        continue;
                    }

                    break;
                }

                k++;
                _v[k] = q;
                _z[k] = s;
                _z[k + 1] = double.PositiveInfinity;
            }

            k = 0;

            for (var q = 0; q < _n; q++)
            {
                while (_z[k + 1] < q)
                {
                    k++;
                }

                var p = _v[k];

                D[q] = (double)(q - p) * (q - p) + F[p];
            }
        }
    }
}

sealed class DistanceNode(Node input, float reach) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var seeds = new bool[size * size];

        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = source[i] > 0.5f;
        }

        var squared = DistanceTransform.Squared(context, seeds);

        context.Rows(y =>
        {
            for (var i = y * size; i < (y + 1) * size; i++)
            {
                if (seeds[i])
                {
                    output[i] = 1f;
                }
                else if (float.IsPositiveInfinity(squared[i]))
                {
                    output[i] = 0f;
                }
                else
                {
                    var d = MathF.Sqrt(squared[i]) / size;

                    output[i] = MathF.Max(0f, 1f - d / reach);
                }
            }
        });
    }
}

sealed class EdgeDetectNode(Node input, float width) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var above = new bool[size * size];
        var below = new bool[size * size];

        for (var i = 0; i < above.Length; i++)
        {
            above[i] = source[i] > 0.5f;
            below[i] = !above[i];
        }

        // An above pixel looks for the nearest pixel at or below, and the other way round.
        var toBelow = DistanceTransform.Squared(context, below);
        var toAbove = DistanceTransform.Squared(context, above);
        var radius = MathF.Max(width * size, 1f);
        var limit = radius * radius;

        context.Rows(y =>
        {
            for (var i = y * size; i < (y + 1) * size; i++)
            {
                var d = above[i] ? toBelow[i] : toAbove[i];

                output[i] = d <= limit ? 1f : 0f;
            }
        });
    }
}
