using Super.Materials;

namespace Super.Cook.Materials;

public sealed record MaterialSettings
{
    /// <summary>The width and height in pixels: a power of two from 16 to 4096.</summary>
    public int Size { get; init; } = MaterialGraphs.DefaultSize;
}

/// <summary>Compiles a <c>*.material.cs</c> graph, runs it, and writes its colour, normal, surface and height textures.</summary>
public sealed class MaterialCooker : ICooker
{
    public string Id => "material";

    /// <summary>Changes whenever <c>Super.Materials</c>, <c>Super.Random</c> or this library is rebuilt with a change, so every graph cooks again.</summary>
    public int Version => MaterialGraphs.Version;

    public IReadOnlyList<string> Extensions { get; } = [".material.cs", ".material.json"];

    public void Cook(CookInput input, CookOutput output)
    {
        var settings = input.SettingsAs<MaterialSettings>();
        if (MaterialGraphs.SizeProblem("size", settings.Size) is { } problem)
        {
            output.Error($"{input.Name}: {problem}");
            return;
        }

        MaterialImages? images;
        using (input.Time("run graphs"))
        {
            images = MaterialGraphs.RunSource(input, settings.Size, output);
        }

        if (images is null)
        {
            return;
        }

        using (input.Time("write material"))
        {
            MaterialWriter.Write(images, input.Name, output);
        }

        using (input.Time("check material"))
        {
            foreach (string check in MaterialChecks.Problems(images, input.Name))
            {
                output.Warning(check);
            }
        }
    }
}
