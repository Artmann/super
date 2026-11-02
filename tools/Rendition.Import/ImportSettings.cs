namespace Rendition.Import;

/// <summary>The hand-written side of an import, read from a materials.json file.</summary>
public sealed class ImportSettings
{
    /// <summary>Real-world size of the model's longest axis in metres; 0 keeps the source scale.</summary>
    public float TargetLength { get; set; }

    /// <summary>Turns the model around the vertical axis, in degrees, so it faces the way a game expects.</summary>
    public float RotateY { get; set; }

    public List<MaterialRule> Materials { get; set; } = [];

    /// <summary>
    /// Clips from other files of the same rig, such as Mixamo's one-animation FBX files,
    /// matched to the model's joints by name.
    /// </summary>
    public List<AnimationSource> Animations { get; set; } = [];
}

/// <summary>A file next to the model to take clips from.</summary>
public sealed class AnimationSource
{
    public string File { get; set; } = "";

    /// <summary>The clip's name in the game. Without one, the file's name without its extension.</summary>
    public string? Name { get; set; }
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

    /// <summary>Ignores the normal map linked in the model file, for maps that do more harm than good.</summary>
    public bool NoNormalMap { get; set; }

    /// <summary>Red is metalness, green is roughness, alpha is opacity.</summary>
    public string? Surface { get; set; }

    /// <summary>Texture of the light the surface gives off.</summary>
    public string? Emissive { get; set; }

    /// <summary>Multiplies the emissive texture. Without a texture it's the glow itself.</summary>
    public float[]? EmissiveColor { get; set; }

    public float[]? Color { get; set; }

    /// <summary>How much tint a transparent material adds.</summary>
    public float Opacity { get; set; } = 1f;

    public float Clearcoat { get; set; }

    /// <summary>Overrides the value in the model file, if it has one. The default is 0.</summary>
    public float? Metallic { get; set; }

    /// <summary>Overrides the value in the model file, if it has one. The default is 0.6.</summary>
    public float? Roughness { get; set; }

    /// <summary>
    /// Cuts out pixels whose opacity is below this. Opacity comes from the colour
    /// texture's alpha and the surface texture's alpha. Without a value, a glTF file's
    /// own MASK cutoff is used. The import fails if the cutoff would cut nothing, or
    /// everything.
    /// </summary>
    public float? AlphaCutoff { get; set; }

    public bool TwoSided { get; set; }

    /// <summary>
    /// For a tree's leaves. Points the normals out of the crown so it's lit as one shape,
    /// lets sunlight through from behind, and draws both sides. Every material this rule
    /// matches counts as one crown, so keep the trunk in a material of its own.
    /// </summary>
    public bool Foliage { get; set; }

    public bool Transparent { get; set; }
}
