namespace Super.Materials;

/// <summary>A material written in code. Inherit and make the images in <see cref="Build"/>.</summary>
public abstract class MaterialGraph
{
    /// <summary>Mixed into every node's seed, so the same graph can make different results.</summary>
    public int Seed { get; init; }

    public abstract Material Build();

    /// <summary>A cooked image, read by its path.</summary>
    protected static Rgb Image(string path)
    {
        throw new NotSupportedException("Reading images in a graph is not built yet.");
    }
}
