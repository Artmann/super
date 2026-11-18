namespace Super.Materials;

/// <summary>Pixels grouped into islands. Each pixel holds its island number, or -1 for background.</summary>
public sealed partial class Islands : Image
{
    internal Islands(Node node) : base(node)
    {
    }

    /// <summary>
    /// One value from 0 to 1 per island, the same across the whole island. Background is 0.
    /// The value comes from the island's first pixel in row order, so it can differ between sizes.
    /// </summary>
    public Gray RandomGray(int? seed = null) => new(new RandomGrayNode(Node, seed));

    /// <summary>
    /// Fills each island with a gradient that runs from 0 to 1 across its bounding box along <paramref name="angle"/>
    /// (degrees, counter-clockwise from +x, so 90 rises up the image). The box is taken over the pixel centres, so on a
    /// 16 pixel wide island the left pixel is 0 and the right pixel is 1. An island that crosses an edge is measured as
    /// one box. An island that is a single pixel along the angle is 0. Background is 0.
    /// </summary>
    public Gray ToGradient(float angle = 0f)
    {
        Arguments.Finite(angle, nameof(angle));

        return new Gray(new ToGradientNode(Node, angle));
    }
}

public sealed partial class Gray
{
    /// <summary>
    /// Finds islands: pixels above <paramref name="threshold"/> that touch on a side are one island.
    /// Islands wrap at all four edges.
    /// </summary>
    public Islands FloodFill(float threshold = 0f)
    {
        if (!float.IsFinite(threshold))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), FormattableString.Invariant($"threshold must be a finite number, not {threshold}."));
        }

        return new Islands(new FloodFillNode(Node, threshold));
    }
}

sealed class FloodFillNode(Node input, float threshold) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var source = inputs[0];
        var parent = Join(source, context.Size);

        Number(source, parent, output);
    }

    /// <summary>Joins each pixel above the threshold to its neighbours above it, wrapping at the edges.</summary>
    int[] Join(float[] source, int size)
    {
        var count = size * size;
        var parent = new int[count];

        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        // One serial pass, so island numbers never depend on the thread count.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;

                if (!(source[i] > threshold))
                {
                    continue;
                }

                var right = y * size + (x + 1) % size;
                var below = (y + 1) % size * size + x;

                if (source[right] > threshold)
                {
                    Union(parent, i, right);
                }

                if (source[below] > threshold)
                {
                    Union(parent, i, below);
                }
            }
        }

        return parent;
    }

    /// <summary>Numbers islands by their first pixel in row order. Background is -1.</summary>
    void Number(float[] source, int[] parent, float[] output)
    {
        var count = parent.Length;
        var numbers = new int[count];

        Array.Fill(numbers, -1);

        var next = 0;

        for (var i = 0; i < count; i++)
        {
            if (!(source[i] > threshold))
            {
                output[i] = -1f;

                continue;
            }

            var root = Find(parent, i);

            if (numbers[root] < 0)
            {
                numbers[root] = next++;
            }

            output[i] = numbers[root];
        }
    }

    static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    static void Union(int[] parent, int a, int b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);

        if (rootA == rootB)
        {
            return;
        }

        if (rootA < rootB)
        {
            parent[rootB] = rootA;
        }
        else
        {
            parent[rootA] = rootB;
        }
    }
}

sealed class RandomGrayNode : Node
{
    public RandomGrayNode(Node input, int? seed) : base(1, input)
    {
        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var count = size * size;
        var islands = inputs[0];
        var values = new List<float>();

        // Serial, in row order, so each island is met first at its first pixel.
        for (var i = 0; i < count; i++)
        {
            var number = (int)islands[i];

            if (number < 0)
            {
                output[i] = 0f;

                continue;
            }

            if (number == values.Count)
            {
                var x = i % size;
                var y = i / size;
                var qx = (int)MathF.Floor((x + 0.5f) / size * 1024f);
                var qy = (int)MathF.Floor((y + 0.5f) / size * 1024f);

                values.Add(Hash.Unit(Seed, qx, qy));
            }

            output[i] = values[number];
        }
    }
}

sealed class ToGradientNode(Node input, float angle) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var islands = inputs[0];
        var radians = angle * (MathF.PI / 180f);

        // Up the image is towards row 0, so the row part of the direction is the negative of the sine.
        var dx = DMath.Cos(radians);
        var dy = -DMath.Sin(radians);

        var places = IslandPlaces.Walk(islands, size);

        context.Rows(y =>
        {
            for (var i = y * size; i < (y + 1) * size; i++)
            {
                output[i] = places.Along(i, (int)islands[i], dx, dy);
            }
        });
    }

    /// <summary>
    /// Each pixel's place in its island, and each island's box around those places. Each island is walked from its
    /// first pixel in row order, counting steps across the edges as steps, so a pixel's place is measured from that
    /// start and an island over an edge is one unbroken box. Serial, and in a fixed order, so the result never depends
    /// on the thread count.
    /// </summary>
    sealed class IslandPlaces
    {
        // The four steps to a pixel's neighbours: right, left, down and up.
        static readonly int[] StepX = [1, -1, 0, 0];
        static readonly int[] StepY = [0, 0, 1, -1];

        readonly int[] placeX;
        readonly int[] placeY;
        readonly List<(int X, int Y)> low = [];
        readonly List<(int X, int Y)> high = [];

        IslandPlaces(int count)
        {
            placeX = new int[count];
            placeY = new int[count];
        }

        public static IslandPlaces Walk(float[] islands, int size)
        {
            var count = size * size;
            var places = new IslandPlaces(count);
            var seen = new bool[count];
            var queue = new int[count];

            for (var start = 0; start < count; start++)
            {
                if ((int)islands[start] < 0 || seen[start])
                {
                    continue;
                }

                // Island numbers count up in order of first pixel, which is the order walked here.
                var box = places.WalkIsland(start, islands, size, seen, queue);
                places.low.Add((box.MinX, box.MinY));
                places.high.Add((box.MaxX, box.MaxY));
            }

            return places;
        }

        (int MinX, int MinY, int MaxX, int MaxY) WalkIsland(int start, float[] islands, int size, bool[] seen, int[] queue)
        {
            var number = (int)islands[start];
            var box = (MinX: 0, MinY: 0, MaxX: 0, MaxY: 0);
            var head = 0;
            var tail = 0;

            seen[start] = true;
            queue[tail++] = start;

            while (head < tail)
            {
                var at = queue[head++];
                var x = at % size;
                var y = at / size;

                box = (Math.Min(box.MinX, placeX[at]), Math.Min(box.MinY, placeY[at]), Math.Max(box.MaxX, placeX[at]), Math.Max(box.MaxY, placeY[at]));

                for (var step = 0; step < 4; step++)
                {
                    var next = Sampling.Wrap(y + StepY[step], size) * size + Sampling.Wrap(x + StepX[step], size);

                    if (seen[next] || (int)islands[next] != number)
                    {
                        continue;
                    }

                    seen[next] = true;
                    placeX[next] = placeX[at] + StepX[step];
                    placeY[next] = placeY[at] + StepY[step];
                    queue[tail++] = next;
                }
            }

            return box;
        }

        /// <summary>How far pixel <paramref name="i"/> lies along the direction across its island's box, from 0 to 1. Background is 0.</summary>
        public float Along(int i, int number, float dx, float dy)
        {
            if (number < 0)
            {
                return 0f;
            }

            var width = high[number].X - low[number].X;
            var height = high[number].Y - low[number].Y;
            var smallest = MathF.Min(0f, dx * width) + MathF.Min(0f, dy * height);
            var range = MathF.Abs(dx) * width + MathF.Abs(dy) * height;

            if (!(range > 0f))
            {
                return 0f;
            }

            var along = dx * (placeX[i] - low[number].X) + dy * (placeY[i] - low[number].Y);

            return Math.Clamp((along - smallest) / range, 0f, 1f);
        }
    }
}
