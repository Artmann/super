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
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, $"threshold must be a finite number, not {threshold}.");
        }

        return new Islands(new FloodFillNode(Node, threshold));
    }
}

sealed class FloodFillNode(Node input, float threshold) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var count = size * size;
        var source = inputs[0];
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

        // Number islands by their first pixel in row order.
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
