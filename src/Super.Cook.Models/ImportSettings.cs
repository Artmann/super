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

    /// <summary>
    /// Block-compresses the model's textures, to a quarter of the memory or less: normal maps as BC5, height maps as
    /// BC4 and the rest as BC7. On by default. Turn it off to keep every pixel exactly, at four times the size.
    /// </summary>
    public bool CompressTextures { get; set; } = true;

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

    /// <summary>
    /// How far below the top black in the height map lies, in metres. With a <see cref="Graph"/> that makes a height,
    /// the height map is the graph's and this is its <c>Depth</c> unless given here; 0 turns its parallax off.
    /// </summary>
    public float? HeightDepth { get; set; }

    /// <summary>Fine grain over the colour, where mid grey changes nothing. Tiled <see cref="DetailScale"/> times as often.</summary>
    public string? DetailColor { get; set; }

    /// <summary>A second normal map, tiled <see cref="DetailScale"/> times as often and blended over the first.</summary>
    public string? DetailNormal { get; set; }

    /// <summary>
    /// How many times the detail textures repeat for each repeat of the others. With a
    /// <see cref="DetailGraph"/> it follows from the two graphs' widths unless given here.
    /// </summary>
    public float? DetailScale { get; set; }

    /// <summary>
    /// A small material graph laid over a <see cref="Graph"/> as its detail: fine grain that holds up close, where the
    /// main graph's pixels run out. Its normal map becomes the detail normal, and its colour, divided by its average
    /// brightness, the detail colour, so it brightens and darkens without changing the main graph's colour. It tiles
    /// at its own <c>Width</c>. Can't be used with <see cref="DetailColor"/> or <see cref="DetailNormal"/>.
    /// </summary>
    public string? DetailGraph { get; set; }

    /// <summary>The size the detail graph runs at, in pixels across: a power of two from 16 to 4096. The default is 1024.</summary>
    public int? DetailGraphSize { get; set; }

    /// <summary>The surface texture's blue channel says where the detail textures show.</summary>
    public bool DetailMask { get; set; }

    /// <summary>
    /// Darkens and lightens the colour in broad patches laid by where the surface is in the world, not by its UVs, so a
    /// texture that repeats across a big wall or floor doesn't show it.
    /// </summary>
    public bool WorldVariation { get; set; }

    /// <summary>
    /// Opacity fades the surface out softly instead of cutting it, through a pattern of pixels that anti-aliasing
    /// blends. For decals such as stains and grime laid just in front of another surface. Opacity comes from the colour
    /// and surface textures' alpha, as for <see cref="AlphaCutoff"/>, or from a graph's <c>Opacity</c>.
    /// </summary>
    public bool Dithered { get; set; }

    /// <summary>
    /// Water stands on the surface where it lies below the level the game sets with <c>View.Puddles</c>, and it is damp
    /// for a band above. Its height map counts, so hollows fill first. Without a level it stays dry.
    /// </summary>
    public bool Puddles { get; set; }

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
    /// own MASK cutoff is used. With a <see cref="Graph"/>, the graph's <c>Opacity</c> is
    /// written into the colour texture's alpha, and the surface texture's alpha is left
    /// whole. The import fails if the cutoff would cut nothing, or everything.
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

    /// <summary>
    /// A surface shader to draw the material with instead of the standard one, by its file name without the extension,
    /// such as <c>puddles</c> for the game's <c>Shaders/puddles.frag</c>. Transparent materials can't use one yet.
    /// </summary>
    public string? Shader { get; set; }

    /// <summary>
    /// Numbers the <see cref="Shader"/> reads as <c>shaderParameters[i]</c>: up to 8 of up to 4 numbers each. Missing
    /// numbers are 0.
    /// </summary>
    public float[][]? ShaderParameters { get; set; }

    /// <summary>
    /// Textures the <see cref="Shader"/> reads with <c>shaderTexture(i, uv)</c>, by their paths from the model's
    /// folder: up to 4. Each is cooked as a texture of its own, and found by its name when the model loads.
    /// </summary>
    public string[]? ShaderTextures { get; set; }

    /// <summary>
    /// Bakes masks from the material's mesh while it cooks: how curved it is, how much of the sky it sees and how high
    /// above its base it is, which <see cref="Wear"/> and <see cref="Dirt"/> are laid by. They're read through the
    /// mesh's second UV set, TEXCOORD_1, where no two triangles may share texels, so the cook fails without one.
    /// </summary>
    public bool BakeMasks { get; set; }

    /// <summary>Worn edges: the colour and roughness convex edges turn to. Needs <see cref="BakeMasks"/>.</summary>
    public LayerRule? Wear { get; set; }

    /// <summary>
    /// Dirt: the colour and roughness hollows the sky can't reach, and the surface below 0.3 m above its base, turn
    /// to. Not over worn edges. Needs <see cref="BakeMasks"/>.
    /// </summary>
    public LayerRule? Dirt { get; set; }
}

/// <summary>A layer laid over a material by its baked masks, such as <see cref="MaterialRule.Wear"/>.</summary>
public sealed class LayerRule
{
    /// <summary>
    /// The layer's colour as three sRGB numbers from 0 to 1, as a colour picker shows it, which replace the surface's
    /// colour where it shows.
    /// </summary>
    public float[]? Color { get; set; }

    /// <summary>The layer's roughness, from 0 to 1. Without one it keeps the surface's own.</summary>
    public float? Roughness { get; set; }

    /// <summary>How strongly the layer shows where its mask is whole, from 0 to 1.</summary>
    public float Amount { get; set; }
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
