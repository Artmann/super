namespace Rendition.Import;

/// <summary>The hand-written side of an import, read from a materials.json file.</summary>
public sealed class ImportSettings
{
    /// <summary>Real-world size of the model's longest axis in metres; 0 keeps the source scale.</summary>
    public float TargetLength { get; set; }

    public List<MaterialRule> Materials { get; set; } = [];
}

/// <summary>
/// Overrides for every material whose name contains <see cref="Match"/>. Model files
/// often link only colour and normal maps, so surface maps are connected here.
/// </summary>
public sealed class MaterialRule
{
    public string Match { get; set; } = "";
    public string? BaseColor { get; set; }
    public string? Normal { get; set; }

    /// <summary>Red is metalness, green is roughness, alpha is opacity.</summary>
    public string? Surface { get; set; }

    public float[]? Color { get; set; }
    public float Metallic { get; set; } = 0f;
    public float Roughness { get; set; } = 0.6f;
    public float AlphaCutoff { get; set; }
    public bool TwoSided { get; set; }
    public bool Transparent { get; set; }
}
