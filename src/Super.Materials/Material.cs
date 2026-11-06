namespace Super.Materials;

/// <summary>What a graph makes. Anything left out gets a plain default when the material is cooked.</summary>
public sealed record Material
{
    /// <summary>How many metres across the image is.</summary>
    public float Width { get; init; } = 1f;

    /// <summary>How many metres high a height of 1 is.</summary>
    public float Depth { get; init; } = 0.02f;

    /// <summary>Defaults to 0.5 everywhere.</summary>
    public Gray? Height { get; init; }

    /// <summary>Defaults to 0.5 grey.</summary>
    public Rgb? Color { get; init; }

    /// <summary>Defaults to 0.6.</summary>
    public Gray? Roughness { get; init; }

    /// <summary>Defaults to 0.</summary>
    public Gray? Metallic { get; init; }

    /// <summary>Defaults to 1.</summary>
    public Gray? Opacity { get; init; }

    /// <summary>Laid over the normal the height makes. Defaults to none.</summary>
    public Normals? Normal { get; init; }

    /// <summary>How much of the occlusion the height makes is multiplied into the colour, from 0 to 1.</summary>
    public float Occlusion { get; init; } = 0.5f;
}
