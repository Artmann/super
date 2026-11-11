using Super.Materials;

namespace Super.Cook.Materials;

public sealed record MaterialSettings
{
    /// <summary>The width and height in pixels: a power of two from 16 to 4096.</summary>
    public int Size { get; init; } = MaterialGraphs.DefaultSize;

    /// <summary>
    /// Block-compresses the four textures with their whole mip chains: the colour and surface as BC7,
    /// the normal map as BC5 and the height as BC4, so they take a quarter of the memory or less. Off
    /// keeps them as RGBA8 pixels, and the game builds the mips as it loads them.
    /// </summary>
    public bool CompressTextures { get; init; } = true;
}

/// <summary>Compiles a <c>*.material.cs</c> graph, runs it, and writes its colour, normal, surface and height textures.</summary>
public sealed class MaterialCooker : ICooker
{
    public string Id => "material";

    /// <summary>
    /// Changes whenever <c>Super.Materials</c>, <c>Super.Random</c> or this library is rebuilt with a change, or the
    /// BC7 encoder writes different bytes, so every graph cooks again.
    /// </summary>
    public int Version { get; } = unchecked(MaterialGraphs.Version * 31 + Bc7Encoder.Version);

    public IReadOnlyList<string> Extensions { get; } = [".material.cs", ".material.json"];

    public void Cook(CookInput input, CookOutput output)
    {
        var settings = input.SettingsAs<MaterialSettings>();
        if (MaterialGraphs.SizeProblem("size", settings.Size) is { } problem)
        {
            output.Error($"{input.Name}: {problem}");
            return;
        }

        // A model that runs this graph at the same size may have run it already, or be running it now.
        if (MaterialGraphs.RunSourceShared(input, settings.Size, output) is not { } run)
        {
            return;
        }

        using (input.Time("write material"))
        {
            MaterialWriter.Write(run.Packed, input.Name, output, settings.CompressTextures ? input : null);
        }

        foreach (string check in run.Findings.Problems(input.Name))
        {
            output.Warning(check);
        }
    }
}
