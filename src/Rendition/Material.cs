using System.Numerics;
using System.Runtime.CompilerServices;

namespace Rendition;

[Flags]
public enum MaterialFlags
{
    None = 0,

    /// <summary>Thin surfaces seen from both sides, such as grilles.</summary>
    TwoSided = 1,

    /// <summary>Glass and the like: blended over the scene. BaseColor.W sets how much tint it adds.</summary>
    Transparent = 2,

    /// <summary>
    /// Leaves on a tree's crown. Back faces keep the normal the mesh gives them instead
    /// of flipping it, and sunlight shines through from behind. Pair it with normals
    /// that point out of the crown, as <see cref="FoliageNormals"/> makes, so the crown
    /// is lit as one shape and not as a pile of separate cards.
    /// </summary>
    Foliage = 4,

    /// <summary>
    /// The surface texture's blue channel says how strongly the detail textures show, from
    /// none at black to full at white. Without it they show everywhere.
    /// </summary>
    DetailMask = 8,

    /// <summary>
    /// Darkens and lightens the colour in broad patches laid by where the surface is in the world, not by its uv,
    /// with a little roughness to match, so a texture that repeats across a big wall or floor doesn't show it.
    /// </summary>
    WorldVariation = 16,

    /// <summary>
    /// Opacity fades the surface out softly instead of cutting it: a share of its pixels as large as the opacity
    /// is drawn, a different share each frame, and anti-aliasing blends them with what's behind. For decals such as
    /// stains and grime laid just in front of another surface. Without anti-aliasing the pattern shows as grain.
    /// </summary>
    Dithered = 32,

    /// <summary>
    /// Where the surface lies below the frame's <see cref="WeatherSettings.Puddles"/> level it is water: a rippled mirror
    /// over a colour that darkens with depth. Just above, it is damp: darker and smoother, fading to dry over the
    /// band. Its height map counts, so the hollows flood before the tops. Without a level it does nothing.
    /// </summary>
    Puddles = 64,

    /// <summary>
    /// <see cref="MaterialTextures.Occlusion"/> is read through the second UV set, <see cref="Vertex.Uv2"/>, instead of
    /// the first. For occlusion baked onto a layout of its own, as many models keep it.
    /// </summary>
    OcclusionOnUv2 = 128,

    /// <summary>
    /// Draws an ink line along the surface's outer edges and creases, where the depth or the normal jumps, as the
    /// frame's <see cref="ScreenSpaceSettings.Outlines"/> sets it. For toon looks, but any material can have one.
    /// </summary>
    Outline = 256,

    /// <summary>
    /// Decals don't lay on the surface: a character walking through a stain or standing over a scorch mark keeps its
    /// own look. Skinned draws, which are characters, never take decals, whatever their material says.
    /// </summary>
    NoDecals = 512,
}

/// <summary>
/// Cel shading for a <see cref="Material.Toon"/>: the light from the sun and lamps falls into a few flat bands instead
/// of a smooth gradient, shine is a hard-edged spot, and a rim of colour can light the outline of the shape.
/// </summary>
public readonly record struct ToonShading
{
    /// <summary>The most bands of light a toon material can have.</summary>
    public const int MostSteps = 16;

    /// <summary>Three bands, no ramp and no rim.</summary>
    public ToonShading()
    {
    }

    /// <summary>
    /// How many bands the light falls into, from 1, lit or not, to <see cref="MostSteps"/>. Ignored with a
    /// <see cref="Ramp"/>.
    /// </summary>
    public int Steps { get; init; } = 3;

    /// <summary>
    /// A ramp from dark on the left to lit on the right, read across its middle row by how much light arrives, so
    /// its colours and the places they change pick the bands. Store it as linear data. Its first and last texels
    /// hold all the way to the ends, so it can repeat. Null for <see cref="Steps"/> plain bands.
    /// </summary>
    public TextureHandle? Ramp { get; init; }

    /// <summary>Light added along the shape's outline, where the surface turns away from the camera. Zero for none.</summary>
    public Vector3 Rim { get; init; }

    /// <summary>Says why these settings can't be used, or returns null when they can.</summary>
    public string? Problem() =>
        Steps is >= 1 and <= MostSteps
            ? float.IsFinite(Rim.X) && float.IsFinite(Rim.Y) && float.IsFinite(Rim.Z) && Rim.X >= 0f && Rim.Y >= 0f && Rim.Z >= 0f
                ? null
                : $"toon rim {Rim} must be 0 or more in each channel."
            : $"toon steps {Steps} must be from 1 to {MostSteps}.";
}

/// <summary>
/// PBR surface parameters. Constants multiply their textures. The surface texture
/// holds metalness in red, roughness in green and opacity in alpha.
/// </summary>
public struct Material
{
    /// <summary>A material with every field zero or unset, apart from <see cref="Translucency"/>.</summary>
    public Material()
    {
    }

    public Vector4 BaseColor;
    public float Metallic;
    public float Roughness;

    /// <summary>
    /// Pixels with opacity below this are cut out. 0 keeps every pixel. Opacity is the
    /// base colour texture's alpha times BaseColor.W times the surface texture's alpha,
    /// so leaf alpha can live in either texture.
    /// </summary>
    public float AlphaCutoff;

    /// <summary>The texture maps the material reads: colour, normals, surface, glow and occlusion.</summary>
    public MaterialTextures Textures;

    /// <summary>Light the surface gives off. Multiplies its texture, so zero means none.</summary>
    public Vector3 Emissive;

    /// <summary>Strength of a clear glossy layer over the surface, such as car lacquer. 0 to 1.</summary>
    public float Clearcoat;

    /// <summary>A height map that sinks the surface's grooves in, by parallax occlusion mapping.</summary>
    public MaterialHeight Height;

    /// <summary>Fine grain and bumps tiled more often than the other textures.</summary>
    public MaterialDetail Detail;

    /// <summary>
    /// Cel shading instead of the physically based kind, for a cartoon look. The soft light from the sky, probes and
    /// bake stays smooth, so shade still has colour. It takes the G-buffer channel clearcoat, foliage and subsurface
    /// use, so it can't be combined with them: a toon material is drawn without them. Null, the default, is the
    /// physically based look. Transparent glass ignores it.
    /// </summary>
    public ToonShading? Toon;
    public MaterialFlags Flags;

    /// <summary>How transparent glass bends what's behind it and fades where it meets the scene.</summary>
    public MaterialGlass Glass;

    /// <summary>
    /// Light that spreads under the surface before it comes back out, as in skin, wax and
    /// marble. A radius of 0, the default, turns it off. It can't be combined with
    /// clearcoat or foliage, which use the same G-buffer channel.
    /// </summary>
    public Subsurface Subsurface;

    /// <summary>Worn edges and dirt, laid by masks baked from the mesh's shape.</summary>
    public MaterialWeathering Weathering;

    /// <summary>A surface shader in place of the standard one, with what it reads.</summary>
    public CustomSurface Custom;

    /// <summary>
    /// For <see cref="MaterialFlags.Foliage"/>, how much sunlight shines through from behind, from 0 for none to 1 for
    /// all of it. It starts at <see cref="DefaultTranslucency"/>, about what a leaf does; thin blades let through more.
    /// Other materials ignore it. A <c>default</c> material, which skips the constructor, has 0.
    /// </summary>
    public float Translucency = DefaultTranslucency;

    /// <summary>How much sunlight foliage lets through when nothing says: about what a leaf does.</summary>
    public const float DefaultTranslucency = 0.6f;

    /// <summary>
    /// How far the surface sways in the frame's <see cref="WeatherSettings.Wind"/>, as a share of its strength: 1 is grass,
    /// less is stiffer, and 0, the default, keeps it still. Vertices sway more the higher they are above the lowest
    /// point of their part, so its foot stays put. Shadows sway with it.
    /// </summary>
    public float Wind;

    /// <summary>
    /// Fades every draw with this material out with distance from the camera, such as grass at 60 metres. A draw's
    /// own <see cref="DrawItem.Fade"/> wins over it. Null, the default, never fades. Transparent glass fades its
    /// opacity instead of dithering.
    /// </summary>
    public DistanceFade? Fade;

    /// <summary>
    /// Says what's wrong with the material's own values, such as a negative wind sway or a fade that ends before it
    /// starts, or returns null when nothing is.
    /// </summary>
    public readonly string? Problem() =>
        Subsurface.Problem(Clearcoat, Flags)
        ?? ((Flags & MaterialFlags.Foliage) != 0 ? MaterialProblems.Translucency(Translucency) : null)
        ?? MaterialProblems.Wind(Wind)
        ?? (Fade is { } fade ? DistanceFade.Problem(fade) : null)
        ?? MaterialProblems.SoftEdge(Glass.SoftEdge)
        ?? Toon?.Problem();
}

/// <summary>Says why one of a material's values can't be used, or returns null when it can.</summary>
public static class MaterialProblems
{
    /// <summary>Says why a soft edge can't be used, or returns null when it can.</summary>
    public static string? SoftEdge(float softEdge) =>
        float.IsFinite(softEdge) && softEdge >= 0f
            ? null
            : $"soft edge {softEdge.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be 0 or more.";

    /// <summary>Says why a foliage translucency can't be used, or returns null when it can.</summary>
    public static string? Translucency(float translucency) =>
        translucency is >= 0f and <= 1f
            ? null
            : $"foliage translucency {translucency.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be from 0 to 1.";

    /// <summary>Says why a wind sway can't be used, or returns null when it can.</summary>
    public static string? Wind(float wind) =>
        float.IsFinite(wind) && wind >= 0f
            ? null
            : $"wind sway {wind.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be 0 or more.";
}

/// <summary>The texture maps a <see cref="Material"/> reads. Each is optional.</summary>
public struct MaterialTextures
{
    /// <summary>The colour, with opacity in alpha. It multiplies <see cref="Material.BaseColor"/>.</summary>
    public TextureHandle? BaseColor;

    /// <summary>The normal map.</summary>
    public TextureHandle? Normal;

    /// <summary>Metalness in red, roughness in green and opacity in alpha.</summary>
    public TextureHandle? Surface;

    /// <summary>The light the surface gives off. It multiplies <see cref="Material.Emissive"/>.</summary>
    public TextureHandle? Emissive;

    /// <summary>
    /// How much soft light reaches each point, in red: white is open and black is hidden, as in the cracks between
    /// planks. It darkens the light from the sky, the light probes and the bake, and dims reflections, but not the
    /// sun's or a lamp's direct light, which casts its own shadows. Read through the first UV set, or through
    /// <see cref="Vertex.Uv2"/> with <see cref="MaterialFlags.OcclusionOnUv2"/>. Store it as linear data.
    /// </summary>
    public TextureHandle? Occlusion;
}

/// <summary>A height map that parallax occlusion mapping sinks the surface into.</summary>
public struct MaterialHeight
{
    /// <summary>
    /// A height map in the red channel: white is the top of the surface and black its
    /// deepest point. Parallax occlusion mapping shifts the other textures with it, so
    /// grooves look sunk in. Needs <see cref="Depth"/>.
    /// </summary>
    public TextureHandle? Texture;

    /// <summary>How far below the top black in the height map lies, in metres. 0 turns parallax off.</summary>
    public float Depth;
}

/// <summary>Fine grain and bumps over a material, tiled <see cref="Scale"/> times as often as its other textures.</summary>
public struct MaterialDetail
{
    /// <summary>
    /// Fine grain over the colour, tiled <see cref="Scale"/> times as often. Store it as
    /// linear data: mid grey (128) leaves the colour alone, white doubles it, black darkens it to nothing.
    /// </summary>
    public TextureHandle? Color;

    /// <summary>A second normal map tiled <see cref="Scale"/> times as often, blended over the first.</summary>
    public TextureHandle? Normal;

    /// <summary>How many times the detail textures repeat for each repeat of the others. 0 counts as 1.</summary>
    public float Scale;
}

/// <summary>How transparent glass bends what's behind it and meets the scene.</summary>
public struct MaterialGlass
{
    /// <summary>
    /// How much transparent glass bends what's behind it: 1 or less is straight through,
    /// as a thin window, and about 1.5 is solid glass, such as a bottle or a marble.
    /// </summary>
    public float Ior;

    /// <summary>
    /// For transparent glass, how far behind it, in metres, the scene must be for the glass to show fully, so water
    /// and glass fade softly where they meet the ground instead of ending in a hard line. 0, the default, never fades,
    /// which suits a window set into its frame.
    /// </summary>
    public float SoftEdge;
}

/// <summary>Worn edges and dirt laid over a material by masks baked from its mesh's shape.</summary>
public struct MaterialWeathering
{
    /// <summary>
    /// Masks baked from the mesh's shape, read through its second UV set, <see cref="Vertex.Uv2"/>: red is curvature,
    /// with 0.5 flat and above it convex, green how much of the sky the surface sees, blue height above the mesh's
    /// foot, 0 there and 1 at 30 cm or the mesh's top, and alpha how much an edge there wears, a slow noise so wear
    /// comes and goes. <see cref="Wear"/> and <see cref="Dirt"/> are laid by them. Without one, neither shows. The
    /// mask is an atlas of islands, so create it with <see cref="TextureEdges.Clamp"/>, as
    /// <see cref="Renderer.CreateModel"/> does; the material doesn't change how its textures are sampled.
    /// </summary>
    public TextureHandle? Mask;

    /// <summary>
    /// Worn edges: where the mask says the surface is convex, the colour is multiplied by twice the layer's colour,
    /// so 0.5 leaves it as it is and the grain shows through, and the roughness turns to the layer's.
    /// </summary>
    public MaterialLayer Wear;

    /// <summary>
    /// Dirt: in hollows the sky can't reach, and low down near the mesh's foot, the colour is multiplied by twice the
    /// layer's colour and the roughness turns to the layer's.
    /// </summary>
    public MaterialLayer Dirt;
}

/// <summary>A surface shader that replaces the standard one, and the numbers and textures it reads.</summary>
public struct CustomSurface
{
    /// <summary>Replaces the standard surface shader. Transparent materials can't use one yet.</summary>
    public SurfaceShaderHandle? Shader;

    /// <summary>Numbers for a surface shader to use however it likes, as shaderParameters[i].</summary>
    public ShaderParameters Parameters;

    /// <summary>Textures for a surface shader, read with shaderTexture(i, uv). Unset ones read white.</summary>
    public ShaderTextures Textures;

    /// <summary>Texture arrays for a surface shader, read with shaderLayer(i, uv, layer). Unset ones read white.</summary>
    public ShaderArrays Arrays;
}

/// <summary>Eight vectors a surface shader can read as shaderParameters[0] to [7].</summary>
[InlineArray(Length)]
public struct ShaderParameters
{
    public const int Length = 8;
    private Vector4 _element;
}

/// <summary>Four texture slots a surface shader can read with shaderTexture(0..3, uv).</summary>
[InlineArray(Length)]
public struct ShaderTextures
{
    public const int Length = 4;
    private TextureHandle? _element;
}

/// <summary>Four texture array slots a surface shader can read with shaderLayer(0..3, uv, layer).</summary>
[InlineArray(Length)]
public struct ShaderArrays
{
    public const int Length = 4;
    private TextureArrayHandle? _element;
}

/// <summary>
/// A layer laid over a material where its mask says, such as wear on its edges or dirt in its hollows. See
/// <see cref="MaterialWeathering.Wear"/>.
/// </summary>
/// <param name="Color">The layer's colour, linear.</param>
/// <param name="Roughness">The layer's roughness, from 0 to 1, or below 0 to keep the surface's own.</param>
/// <param name="Amount">How strongly it shows, from 0, which turns it off, to 1.</param>
public readonly record struct MaterialLayer(Vector3 Color, float Roughness, float Amount);

/// <summary>
/// How light spreads under a surface. The lighting pass blurs the diffuse light of these
/// surfaces across the screen, further for the colours that travel further, so shadow
/// edges soften and take on a warm rim, as on skin.
/// </summary>
public struct Subsurface
{
    /// <summary>The longest distance light spreads under the surface, in metres. Skin is about 0.01. 0 turns it off.</summary>
    public float Radius;

    /// <summary>
    /// How far red, green and blue each spread, as fractions of <see cref="Radius"/>.
    /// Skin lets red go furthest, so about (1, 0.4, 0.25). Zero counts as one for all three.
    /// </summary>
    public Vector3 Color;

    /// <summary>
    /// How much light from behind shines through thin parts, such as ears or a candle's
    /// rim, from 0 to 1. It's tinted by the base colour and <see cref="Color"/>.
    /// </summary>
    public float Transmission;

    /// <summary>True when the surface scatters light at all.</summary>
    public readonly bool IsOn => Radius > 0f;

    /// <summary>
    /// What stops a material from having this subsurface, or null when nothing does.
    /// Clearcoat, foliage and subsurface share a G-buffer channel.
    /// </summary>
    public readonly string? Problem(float clearcoat, MaterialFlags flags)
    {
        if (float.IsNaN(Radius) || Radius < 0f)
        {
            return "subsurface radius must be 0 or more.";
        }

        if (!IsOn)
        {
            return null;
        }

        if (clearcoat > 0f)
        {
            return "subsurface can't be combined with clearcoat, because they share a G-buffer channel.";
        }

        if ((flags & MaterialFlags.Foliage) != 0)
        {
            return "subsurface can't be combined with foliage, because they share a G-buffer channel.";
        }

        return null;
    }
}
