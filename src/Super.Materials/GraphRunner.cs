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

    /// <summary>
    /// Like <see cref="Rows(Action{int})"/>, with scratch space made once for each worker thread by <paramref name="create"/>
    /// instead of once for each row. A row must not rely on what an earlier row left in it.
    /// </summary>
    public void Rows<T>(Func<T> create, Action<int, T> row)
    {
        try
        {
            Parallel.For(0, Size, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, create, (y, _, scratch) =>
            {
                row(y, scratch);

                return scratch;
            }, _ => { });
        }
        catch (AggregateException error) when (error.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }
}

/// <summary>Works a graph out, one node at a time, each after its inputs.</summary>
public static class GraphRunner
{
    /// <summary>
    /// Builds the graph and works out all its parts in one go, so an image several parts share is made once.
    /// <paramref name="size"/> is the width and height in pixels. <paramref name="maxThreads"/> limits the rows worked at once.
    /// </summary>
    public static MaterialImages Run(MaterialGraph graph, int size, IImageSource? images = null, int? maxThreads = null)
    {
        Arguments.AtLeast(size, 1, nameof(size));

        Node[] roots;
        float width;

        using (GraphContext.Use(graph.Seed, images))
        {
            var material = graph.Build();

            Arguments.Finite(material.Width, nameof(Material.Width));
            Arguments.MoreThan(material.Width, 0f, nameof(Material.Width));
            Arguments.Finite(material.Depth, nameof(Material.Depth));
            Arguments.AtLeast(material.Depth, 0f, nameof(Material.Depth));
            Arguments.Between(material.Occlusion, 0f, 1f, nameof(Material.Occlusion));

            width = material.Width;

            var height = material.Height ?? Gray.Of(0.5f);
            var normal = height.HeightToNormal(material.Width, material.Depth);

            if (material.Normal is not null)
            {
                normal = new Normals(new NormalOverNode(normal.Node, material.Normal.Node));
            }

            var occlusion = height.HeightToOcclusion(material.Width, material.Depth);
            var color = new Rgb(new OccludedColorNode((material.Color ?? Rgb.Of(0.5f, 0.5f, 0.5f)).Node, occlusion.Node, material.Occlusion));

            roots =
            [
                color.Node,
                normal.Node,
                (material.Roughness ?? Gray.Of(0.6f)).Node,
                (material.Metallic ?? Gray.Of(0f)).Node,
                (material.Opacity ?? Gray.Of(1f)).Node,
                height.Node
            ];
        }

        var parts = Execute(roots, size, maxThreads ?? -1);

        return new MaterialImages(size, width, parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
    }

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

        return Execute([root.Node], size, maxThreads ?? -1)[0];
    }

    /// <summary>Works out every root, sharing the nodes between them. The roots' buffers are never reused.</summary>
    static float[][] Execute(Node[] roots, int size, int maxThreads)
    {
        // Post-order walk, so every node comes after its inputs, and each is listed once.
        var order = new List<Node>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(Node Node, int Next)>();

        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                stack.Push((root, 0));
            }

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
        }

        var isRoot = new HashSet<Node>(roots, ReferenceEqualityComparer.Instance);

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
                if (--remaining[input] == 0 && !isRoot.Contains(input))
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

        return roots.Select(root => buffers[root]).ToArray();
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
