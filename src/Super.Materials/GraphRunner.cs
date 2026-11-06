using System.Runtime.ExceptionServices;

namespace Super.Materials;

/// <summary>What a node needs while it is evaluated.</summary>
internal sealed class EvaluationContext(int size, int maxThreads)
{
    public int Size { get; } = size;

    /// <summary>Runs <paramref name="row"/> for every row, in parallel. Rows must not depend on each other.</summary>
    public void Rows(Action<int> row)
    {
        try
        {
            Parallel.For(0, Size, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, row);
        }
        catch (AggregateException error) when (error.InnerExceptions.Count > 0)
        {
            // Parallel.For wraps what rows throw, and several rows can fail the same way. Callers should see the original, the first one.
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }
}

/// <summary>Works a graph out, one node at a time, each after its inputs.</summary>
internal static class GraphRunner
{
    /// <summary>
    /// Builds the graph and returns the root's pixels, interleaved, <c>size * size * channels</c> floats.
    /// </summary>
    internal static float[] Evaluate(Func<Image> build, int size, int seed = 0, int? maxThreads = null)
    {
        Image root;

        using (GraphContext.Use(seed, null))
        {
            root = build();
        }

        return Run(root.Node, size, maxThreads ?? -1);
    }

    static float[] Run(Node root, int size, int maxThreads)
    {
        // Post-order walk, so every node comes after its inputs, and each is listed once.
        var order = new List<Node>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(Node Node, int Next)>();

        seen.Add(root);
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();

            if (next < node.Inputs.Length)
            {
                stack.Push((node, next + 1));

                var input = node.Inputs[next];

                if (seen.Add(input))
                {
                    stack.Push((input, 0));
                }
            }
            else
            {
                order.Add(node);
            }
        }

        // How many input slots still wait for each node's buffer.
        var remaining = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);

        foreach (var node in order)
        {
            foreach (var input in node.Inputs)
            {
                remaining[input] = remaining.GetValueOrDefault(input) + 1;
            }
        }

        var context = new EvaluationContext(size, maxThreads);
        var buffers = new Dictionary<Node, float[]>(ReferenceEqualityComparer.Instance);
        var pool = new Dictionary<int, Stack<float[]>>();

        foreach (var node in order)
        {
            var output = Rent(pool, size * size * node.Channels);
            var inputs = new float[node.Inputs.Length][];

            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i] = buffers[node.Inputs[i]];
            }

            node.Evaluate(context, inputs, output);
            buffers[node] = output;

            // A node that reads one input twice counts it twice, so the buffer is returned once, at the last read.
            foreach (var input in node.Inputs)
            {
                if (--remaining[input] == 0)
                {
                    var buffer = buffers[input];

                    buffers.Remove(input);

                    if (!pool.TryGetValue(buffer.Length, out var free))
                    {
                        free = pool[buffer.Length] = new Stack<float[]>();
                    }

                    free.Push(buffer);
                }
            }
        }

        return buffers[root];
    }

    static float[] Rent(Dictionary<int, Stack<float[]>> pool, int length)
    {
        if (pool.TryGetValue(length, out var free) && free.Count > 0)
        {
            return free.Pop();
        }

        return new float[length];
    }
}
