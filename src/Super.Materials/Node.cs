using System.Globalization;

namespace Super.Materials;

/// <summary>
/// One step of a graph. Images are interleaved, <c>size * size * channels</c> floats.
/// A node must write every value of its output, because buffers are reused.
/// </summary>
internal abstract class Node
{
    readonly int _graphSeed;

    protected Node(int channels, params Node[] inputs)
    {
        var context = GraphContext.Current;

        Channels = channels;
        Inputs = inputs;
        Index = context.TakeIndex();
        _graphSeed = context.Seed;
        Seed = SeedFor(string.Create(CultureInfo.InvariantCulture, $"node {Index}"));
    }

    public int Index { get; }

    public ulong Seed { get; private set; }

    public int Channels { get; }

    public Node[] Inputs { get; }

    /// <summary>For a node that takes a <c>seed:</c>. Mixes it into this node's seed.</summary>
    protected void UseSeed(int seed)
    {
        Seed = SeedFor(string.Create(CultureInfo.InvariantCulture, $"node {Index} seed {seed}"));
    }

    internal abstract void Evaluate(EvaluationContext context, float[][] inputs, float[] output);

    ulong SeedFor(string stream) => new GameRandom(_graphSeed).Stream(stream).NextBits();
}
