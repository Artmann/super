using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A picture made by code, as RGBA bytes row by row. It must be square.</summary>
public sealed record GeneratedTexture(int Size, byte[] Pixels);

/// <summary>
/// Cel shading for a <see cref="Surface.Toon"/>: the light from the sun and lamps falls into <paramref name="Steps"/> flat
/// bands, or the colours of <paramref name="Ramp"/> read from dark on its left to lit on its right, shine is a
/// hard-edged spot, and <paramref name="Rim"/> lights the outline of the shape. See docs/rendering.md.
/// </summary>
/// <param name="Steps">How many bands the light falls into, from 1 to <see cref="ToonShading.MostSteps"/>.</param>
/// <param name="Ramp">A linear texture read across its middle row instead of plain bands, or null.</param>
/// <param name="Rim">Light added along the outline of the shape, zero for none.</param>
public sealed record Toon(int Steps = 3, TextureRef? Ramp = null, Vector3 Rim = default);

/// <summary>
/// A height map for <see cref="Surface.Height"/>: white in red at the top of the surface. The other textures shift
/// with it as the view moves, so grooves such as mortar look sunk in.
/// </summary>
/// <param name="Map">The height map, in red.</param>
/// <param name="Depth">How far below the top black in the height map lies, in metres, such as 0.02 for brick.</param>
public sealed record HeightMap(TextureRef Map, float Depth);

/// <summary>
/// Fine grain for <see cref="Surface.Detail"/>, tiled <see cref="Scale"/> times as often as the surface's other
/// textures.
/// </summary>
public sealed record DetailMap
{
    /// <summary>
    /// Grain over the colour. Make it linear data: mid grey (128) changes nothing, lighter brightens and darker darkens.
    /// </summary>
    public TextureRef? ColorTexture { get; init; }

    /// <summary>A second normal map, blended over the surface's own.</summary>
    public TextureRef? NormalTexture { get; init; }

    /// <summary>How many times the detail textures repeat for each repeat of the others.</summary>
    public float Scale { get; init; } = 8f;

    /// <summary>Lets the surface texture's blue channel say where the detail shows, from none at black to full at white.</summary>
    public bool Mask { get; init; }
}

/// <summary>
/// How much soft light reaches each point of a surface, for <see cref="Surface.Occlusion"/>: white for all, black for
/// none, as in the cracks between planks. It darkens the light from the sky, the light probes and the bake, and dims
/// reflections, but not the sun's or a lamp's direct light.
/// </summary>
/// <param name="Map">The occlusion, in red. Cook it as linear data, with <c>"texture": { "linear": true }</c> in a <c>cook.json</c> rule.</param>
/// <param name="OnUv2">
/// Reads the map through the second UV set, <see cref="Vertex.Uv2"/>, instead of the first, for occlusion baked onto
/// a layout of its own. <see cref="Content.Mesh"/> with <c>isStatic</c> refuses a mesh whose <see cref="Vertex.Uv2"/>
/// it would have to replace.
/// </param>
public sealed record OcclusionMap(TextureRef Map, bool OnUv2 = false);

/// <summary>
/// Leaves or blades, for <see cref="Surface.Foliage"/>: sunlight shines through from behind, both sides are lit as the
/// front, and there are no contact shadows, which thin cut-out leaves would only spoil.
/// </summary>
/// <param name="Translucency">How much sunlight shines through from behind, from 0 to 1.</param>
public sealed record Foliage(float Translucency = Rendition.Material.DefaultTranslucency);

/// <summary>Switches a <see cref="Surface"/> can turn on, combined with <c>|</c>.</summary>
[Flags]
public enum SurfaceFeatures
{
    None = 0,

    /// <summary>Draws an ink line round the surface's outline and along its creases, as <see cref="View.Outlines"/> sets it.</summary>
    Outline = 1,

    /// <summary>
    /// Darkens and lightens the colour in broad patches laid by where the surface is in the world, so a texture that
    /// repeats across a big wall or floor doesn't show it.
    /// </summary>
    WorldVariation = 2,

    /// <summary>
    /// Opacity fades the surface out softly instead of cutting it, through a pattern of pixels that anti-aliasing
    /// blends. For decals such as stains and grime laid just in front of another surface.
    /// </summary>
    Dithered = 4,

    /// <summary>
    /// Water stands on the surface where it lies below the level set with <see cref="View.Puddles"/>, and it is damp
    /// for a band above. Its height map counts, so hollows fill first. Without a level it stays dry.
    /// </summary>
    Puddles = 8,

    /// <summary>
    /// <see cref="View.Decal"/>s don't lay on the surface, so a prop standing in a stain or a crate in a scorch mark
    /// keeps its own look. Animated characters never take decals.
    /// </summary>
    NoDecals = 16,
}

/// <summary>A fragment shader of the game's own for <see cref="Surface.Shader"/>, and what it reads.</summary>
/// <param name="Fragment">The shader, named through <see cref="Content.Shader"/>.</param>
public sealed record SurfaceShader(ShaderRef Fragment)
{
    /// <summary>How many numbers a surface shader can take.</summary>
    public const int MaxParameters = Rendition.ShaderParameters.Length;

    /// <summary>How many textures, and how many texture arrays, a surface shader can take.</summary>
    public const int MaxTextures = Rendition.ShaderTextures.Length;

    /// <summary>Numbers the shader reads as shaderParameters[i]. Up to <see cref="MaxParameters"/>.</summary>
    public Vector4[] Parameters { get; init; } = [];

    /// <summary>Textures the shader reads with shaderTexture(i, uv). Up to <see cref="MaxTextures"/>.</summary>
    public TextureRef[] Textures { get; init; } = [];

    /// <summary>Texture arrays the shader reads with shaderLayer(i, uv, layer). Up to <see cref="MaxTextures"/>.</summary>
    public TextureArrayRef[] Arrays { get; init; } = [];
}

/// <summary>How a generated mesh looks. Everything is optional; the default is matte grey.</summary>
public sealed record Surface
{
    public Vector3 Color { get; init; } = new(0.7f);
    public float Roughness { get; init; } = 0.4f;
    public float Metallic { get; init; }
    public TextureRef? ColorTexture { get; init; }
    public TextureRef? NormalTexture { get; init; }
    public TextureRef? SurfaceTexture { get; init; }

    /// <summary>
    /// How many metres the textures cover before they repeat, from a material graph's width.
    /// Shapes tile at this size unless given <c>uvPerMetre</c>. Null when the surface isn't from a graph.
    /// </summary>
    public float? TileWidth { get; init; }

    /// <summary>
    /// Below 1 the surface is glass: the world shows through it, tinted by
    /// <see cref="Color"/>, and its shadow is lighter. 0 is perfectly clear.
    /// </summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>How much glass bends what's behind it: 1 is straight through, as a window, and about 1.5 is a bottle or a marble.</summary>
    public float Ior { get; init; } = 1f;

    /// <summary>A height map that makes grooves such as mortar look sunk in, or null for none.</summary>
    public HeightMap? Height { get; init; }

    /// <summary>Fine grain over the colour and normals, tiled more often than the other textures, or null for none.</summary>
    public DetailMap? Detail { get; init; }

    /// <summary>How much soft light reaches each point, or null for all of it everywhere.</summary>
    public OcclusionMap? Occlusion { get; init; }

    /// <summary>
    /// Cel shading instead of the physically based kind, for a cartoon look. The soft light from the sky stays
    /// smooth, so shade keeps its colour. It can't be combined with <see cref="Foliage"/> or <see cref="Subsurface"/>,
    /// which it turns off. Null, the default, is the physically based look.
    /// </summary>
    public Toon? Toon { get; init; }

    /// <summary>Switches such as outlines and puddles. None by default.</summary>
    public SurfaceFeatures Features { get; init; }

    /// <summary>Lights the surface as leaves or blades, or null, the default, for a solid surface.</summary>
    public Foliage? Foliage { get; init; }

    /// <summary>
    /// How far the surface sways in the wind set with <see cref="View.Wind"/>, as a share of it: 1 for grass, less for
    /// stiffer things such as ivy or a tree's crown, and 0, the default, to stand still. Each part sways more the higher
    /// it is above its lowest point, up to 1 m, so it stays rooted. Its shadow sways with it.
    /// </summary>
    public float Wind { get; init; }

    /// <summary>
    /// Fades everything with this surface out with distance from the camera, such as grass that's gone by 60 metres:
    /// <c>new DistanceFade(50, 60)</c>. It's dithered away, and anti-aliasing smooths that into a fade. A model drawn
    /// with a fade of its own uses that instead. Null, the default, never fades. Glass fades its opacity.
    /// </summary>
    public DistanceFade? FadeDistance { get; init; }

    /// <summary>
    /// For glass, below full <see cref="Opacity"/>: how far behind it, in metres, the scene must be for it to show
    /// fully, so water fades softly where it meets the shore instead of ending in a hard line. 0, the default, never
    /// fades, as suits a window in its frame.
    /// </summary>
    public float SoftEdge { get; init; }

    /// <summary>Light that spreads under the surface, for skin, wax and marble. Off by default.</summary>
    public Subsurface Subsurface { get; init; }

    /// <summary>A fragment shader of the game's own and what it reads, or null for the standard one.</summary>
    public SurfaceShader? Shader { get; init; }
}

/// <summary>
/// A surface made by code rather than loaded from a file, such as generated ground. Simplify
/// is off for terrain chunks, whose edges must stay exactly where their neighbours expect.
/// </summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, Surface Surface, bool Simplify = true)
{
    /// <summary>The old shape: one colour texture and a roughness. The texture is owned by the mesh and freed with it.</summary>
    public GeneratedMesh(Vertex[] vertices, uint[] indices, GeneratedTexture texture, float roughness)
        : this(vertices, indices, new Surface { Color = Vector3.One, Roughness = roughness })
    {
        OwnedTexture = texture;
    }

    /// <summary>A colour texture that lives and dies with this mesh, from the old constructor.</summary>
    internal GeneratedTexture? OwnedTexture { get; init; }

    /// <summary>
    /// This mesh when its <see cref="Vertex.Uv2"/> already passes <see cref="LightmapUnwrap.Problem"/> at
    /// <see cref="LightmapUnwrap.ReferenceSize"/>, as the model cooker checks a file's set, otherwise a copy unwrapped by
    /// <see cref="LightmapUnwrap.Unwrap"/>, with vertices split where its charts meet. The unwrap's page is
    /// <see cref="LightmapUnwrap.ReferenceSize"/> texels square, or twice that up to
    /// <see cref="LightmapUnwrap.LargestSize"/> when the charts don't fit. A set made for a larger page only, such as one
    /// whose gutters are two texels at 1024 but one at 512, is replaced.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The charts don't fit even the largest page, or the set must be replaced but the surface reads its
    /// <see cref="Surface.Occlusion"/> through it.
    /// </exception>
    public GeneratedMesh WithLightmapUvs()
    {
        if (Indices.Length == 0 || Uv2Problem() is not { } problem)
        {
            return this;
        }

        // The occlusion was painted for this set, so on a new one it would land in the wrong places.
        if (Surface.Occlusion is { OnUv2: true })
        {
            throw new InvalidOperationException(
                $"The surface reads its occlusion texture through Uv2, so Uv2 can't be replaced to fit a lightmap. {problem} Fix the mesh's Uv2, or read the occlusion through Uv.");
        }

        string? failure = null;
        for (int size = LightmapUnwrap.ReferenceSize; size <= LightmapUnwrap.LargestSize; size *= 2)
        {
            try
            {
                var (vertices, indices) = LightmapUnwrap.Unwrap(Vertices, Indices, size);
                return this with { Vertices = vertices, Indices = indices };
            }
            catch (InvalidOperationException exception)
            {
                failure = exception.Message;
            }
        }

        throw new InvalidOperationException(failure);
    }

    /// <summary>Why <see cref="Vertex.Uv2"/> doesn't fit a lightmap, as <see cref="LightmapUnwrap.Problem"/> says, or null when it does.</summary>
    private string? Uv2Problem()
    {
        var positions = new Vector3[Vertices.Length];
        var uvs = new Vector2[Vertices.Length];
        for (int i = 0; i < Vertices.Length; i++)
        {
            positions[i] = Vertices[i].Position;
            uvs[i] = Vertices[i].Uv2;
        }

        return LightmapUnwrap.Problem(positions, uvs, Indices);
    }
}
