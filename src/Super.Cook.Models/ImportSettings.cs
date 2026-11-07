namespace Super.Cook.Models;

/// <summary>
/// The model cooker's settings, read from the <c>"model"</c> settings of <c>cook.json</c>
/// rules and the model's sidecar.
/// </summary>
public sealed class ModelSettings : ImportSettings
{
    /// <summary>
    /// Cooks each entry as its own asset, <c>source#key</c>, instead of the whole file. A part
    /// takes every setting written beside <c>parts</c>, unless it sets that one itself.
    /// </summary>
    public Dictionary<string, ImportSettings>? Parts { get; set; }
}

/// <summary>How to turn one model file, or one part of it, into a cooked model.</summary>
public class ImportSettings
{
    /// <summary>Real-world size of the model's longest axis in metres; 0 keeps the source scale.</summary>
    public float TargetLength { get; set; }

    /// <summary>Turns the model around the vertical axis, in degrees, so it faces the way a game expects.</summary>
    public float RotateY { get; set; }

    /// <summary>
    /// Keeps only what's under the nodes whose name contains this, for files that hold
    /// several variants side by side, as Poly Haven's plant sets do. Not for rigged models.
    /// </summary>
    public string? Node { get; set; }

    /// <summary>
    /// Keeps each mesh of a static model as its own part, named by the mesh, instead of merging the
    /// meshes that share a material. Needed to show some parts with <c>Content.Parts</c>. Rigged
    /// models always keep their meshes.
    /// </summary>
    public bool KeepMeshes { get; set; }

    /// <summary>
    /// Keeps the file's own origin: the model is turned and scaled about it, but not centred or
    /// stood on the ground. For a model whose origin means something, such as a character whose
    /// file also holds alternative parts that would throw off the bounds.
    /// </summary>
    public bool KeepOrigin { get; set; }

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

    /// <summary>
    /// A material graph, <c>*.material.cs</c>, or a variant of one, <c>*.material.json</c>, by its path from the
    /// model's folder. It runs while the model cooks and gives the material its colour, normal and surface textures,
    /// so it can't be used with <see cref="BaseColor"/>, <see cref="Normal"/>, <see cref="Surface"/> or
    /// <see cref="Alpha"/>. The meshes' UVs are read in metres: they're divided by the graph's width.
    /// </summary>
    public string? Graph { get; set; }

    /// <summary>The size the graph runs at, in pixels across: a power of two from 16 to 4096. The default is 1024.</summary>
    public int? GraphSize { get; set; }

    public string? BaseColor { get; set; }

    /// <summary>
    /// A grey image of opacity, whose red becomes the colour texture's alpha. Poly Haven's
    /// glTF plants ship their leaf cut-outs this way, beside a colour JPEG with no alpha.
    /// </summary>
    public string? Alpha { get; set; }

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

    /// <summary>A height map in red, white at the top. Needs <see cref="HeightDepth"/>.</summary>
    public string? Height { get; set; }

    /// <summary>How far below the top black in the height map lies, in metres.</summary>
    public float HeightDepth { get; set; }

    /// <summary>Fine grain over the colour, where mid grey changes nothing. Tiled <see cref="DetailScale"/> times as often.</summary>
    public string? DetailColor { get; set; }

    /// <summary>A second normal map, tiled <see cref="DetailScale"/> times as often and blended over the first.</summary>
    public string? DetailNormal { get; set; }

    /// <summary>How many times the detail textures repeat for each repeat of the others.</summary>
    public float DetailScale { get; set; } = 8f;

    /// <summary>The surface texture's blue channel says where the detail textures show.</summary>
    public bool DetailMask { get; set; }

    /// <summary>Light that spreads under the surface, for skin, wax and marble. Off by default.</summary>
    public SubsurfaceRule? Subsurface { get; set; }

    /// <summary>
    /// Overrides the value in the model file, if it has one. The default is 0, or 1 with a
    /// <see cref="Graph"/>, since it multiplies the graph's metalness.
    /// </summary>
    public float? Metallic { get; set; }

    /// <summary>
    /// Overrides the value in the model file, if it has one. The default is 0.6, or 1 with a
    /// <see cref="Graph"/>, since it multiplies the graph's roughness.
    /// </summary>
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

    /// <summary>For transparent glass: 1 is straight through, as a window, and about 1.5 bends the view like a bottle or a marble.</summary>
    public float Ior { get; set; } = 1f;
}

/// <summary>How light spreads under a material's surface. See Rendition.Subsurface.</summary>
public sealed class SubsurfaceRule
{
    /// <summary>How far light travels under the surface, in metres. Skin is about 0.01.</summary>
    public float Radius { get; set; }

    /// <summary>How far red, green and blue travel, as fractions of the radius. The default spreads them all the same.</summary>
    public float[]? Color { get; set; }

    /// <summary>How much light from behind shines through thin parts, from 0 to 1.</summary>
    public float Transmission { get; set; }
}
