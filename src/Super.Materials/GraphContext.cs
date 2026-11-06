namespace Super.Materials;

/// <summary>Where cooked images that a graph reads (such as a texture loaded by name) come from.</summary>
public interface IImageSource
{
    DecodedImage Read(string path);
}

/// <summary>An image read from disk: <paramref name="Rgba"/> is four floats per pixel, row 0 first.</summary>
public sealed record DecodedImage(int Width, int Height, float[] Rgba);

/// <summary>
/// The graph being built on this thread. Nodes take their index and seed from it.
/// </summary>
internal sealed class GraphContext
{
    int _nextIndex;

    [ThreadStatic]
    static GraphContext? _current;

    GraphContext(int seed, IImageSource? images)
    {
        Seed = seed;
        Images = images;
    }

    public static GraphContext Current => _current
        ?? throw new InvalidOperationException("Nodes are made inside MaterialGraph.Build.");

    public int Seed { get; }

    public IImageSource? Images { get; }

    public static IDisposable Use(int seed, IImageSource? images)
    {
        var previous = _current;
        _current = new GraphContext(seed, images);
        return new Scope(previous);
    }

    internal int TakeIndex() => _nextIndex++;

    sealed class Scope(GraphContext? previous) : IDisposable
    {
        public void Dispose()
        {
            _current = previous;
        }
    }
}
