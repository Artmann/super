namespace Super.Materials;

/// <summary>A material written in code. Inherit and make the images in <see cref="Build"/>.</summary>
public abstract class MaterialGraph
{
    /// <summary>Mixed into every node's seed, so the same graph can make different results.</summary>
    public int Seed { get; init; }

    public abstract Material Build();

    /// <summary>
    /// A picture beside the graph file, such as <c>Image("rock.png")</c>, stretched to the size the graph runs at.
    /// Colours are as stored in the file, 0..1, with no conversion, and alpha is ignored.
    /// </summary>
    protected static Rgb Image(string path)
    {
        var images = GraphContext.Current.Images
            ?? throw new InvalidOperationException("This graph was run without a way to read images.");

        // Read now, once, so the cook sees the file even if the graph never uses the image.
        return new Rgb(new ImageInputNode(images.Read(path)));
    }
}
