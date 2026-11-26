using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// How much of a decal's material reaches the surface under it, each from 0 to 1, along with
/// how far round a corner it reaches. Each weight is multiplied by the material's opacity.
/// </summary>
/// <param name="Color">How much of the decal's colour covers the surface's.</param>
/// <param name="Normal">How much of the decal's normal map is laid over the surface's bumps.</param>
/// <param name="Roughness">How much of the decal's roughness replaces the surface's, as a damp patch shines.</param>
/// <param name="AngleFade">
/// The cosine of the angle from the box's up at which a surface stops taking the decal. The decal fades out
/// as a surface turns towards it, so 0.5 reaches surfaces up to 60 degrees away, and 0 reaches round to 90.
/// </param>
public readonly record struct DecalBlend(float Color = 1f, float Normal = 1f, float Roughness = 1f, float AngleFade = 0.5f)
{
    /// <summary>The whole material, fading out on surfaces more than 60 degrees from the box's up.</summary>
    public DecalBlend() : this(1f, 1f, 1f, 0.5f)
    {
    }
}

/// <summary>
/// A material laid onto whatever lies inside a box, such as a damp patch on a wall or rust under a
/// bracket. It projects down the box's own −Y: the box's X and Z span the material's width and height, and
/// its Y how deep it reaches. See docs/views.md.
/// </summary>
/// <param name="Box">Takes the cube from −0.5 to 0.5 on each axis to where the decal is in the world.</param>
/// <param name="Material">What is laid on. Its colour, surface and normal textures are used.</param>
/// <param name="Blend">How much of each part of the material reaches the surface.</param>
public readonly record struct DecalDraw(Matrix4x4 Box, MaterialHandle Material, DecalBlend Blend);

/// <summary>
/// A soft, see-through puff that always faces the camera, such as one wisp of dust or
/// smoke. It's lit by the sun, the sky and point and spot lights, falls into the sun's
/// shadow, sits in the fog, fades into haze, and melts into whatever it touches instead of showing a hard
/// edge where it meets the ground. A glowing one, such as a lick of flame, adds its own light.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Particle
{
    /// <summary>How many fit in one frame. Past this they aren't drawn.</summary>
    public const int MaxPerFrame = 32768;

    /// <summary>The middle of the puff.</summary>
    public Vector3 Position;

    /// <summary>How wide the puff is, in metres.</summary>
    public float Size;

    /// <summary>Linear red, green and blue from 0 to 1, and how solid its middle is from 0 to 1.</summary>
    public Vector4 Color;

    /// <summary>Turns the puff on the screen, in radians.</summary>
    public float Rotation;

    /// <summary>Picks the lumpy outline of the puff. Any number; nearby values look alike.</summary>
    public float Variant;

    /// <summary>
    /// Light the puff gives off itself, as a multiple of its colour, on top of the light it's
    /// lit by: 0 for dust, about 1 for embers, and past 1 for flames, which then bloom.
    /// </summary>
    public float Glow;
}

/// <summary>
/// A shape on the screen, in pixels from the top left, which can be rounded, bordered, shaded
/// and clipped. For a box, the atlas gives each pixel's alpha and color gives the rest, in sRGB
/// as written in an image editor. With <see cref="TextureIndex"/> set, a texture gives the pixels
/// and color tints them. <see cref="Kind"/> draws an ellipse, an arc, a line or a soft shadow in
/// place of the box. A default value for any of these changes nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OverlayQuad
{
    /// <summary>How many fit in one frame. Past this they aren't drawn.</summary>
    public const int MaxPerFrame = 8192;

    public Vector2 Position;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    public Vector4 Color;

    /// <summary>Where a gradient ends, in sRGB. Needs <see cref="Gradient"/>.</summary>
    public Vector4 Color2;

    /// <summary>The line drawn inside the edge, in sRGB. Needs <see cref="BorderWidth"/>.</summary>
    public Vector4 BorderColor;

    /// <summary>Only what's inside this is drawn: x, y, width, height in pixels. No width means no clip.</summary>
    public Vector4 Clip;

    /// <summary>How round each corner is, in pixels: top left, top right, bottom right, bottom left.</summary>
    public Vector4 Radii;

    /// <summary>Rounds the corners of <see cref="Clip"/>, in the same order as <see cref="Radii"/>.</summary>
    public Vector4 ClipRadii;

    /// <summary>
    /// Where <see cref="Color"/> turns into <see cref="Color2"/>, in pixels from <see cref="Position"/>. A
    /// linear gradient runs from (x, y) to (z, w); a radial one spreads from (x, y) out to z.
    /// </summary>
    public Vector4 GradientPoints;

    /// <summary>
    /// What the shape needs. An arc's start and sweep in radians, clockwise from the top; a line's two ends
    /// in pixels from <see cref="Position"/>; a shadow's box as x, y, width, height in pixels.
    /// </summary>
    public Vector4 Params;

    public float BorderWidth;

    /// <summary>How thick an arc or a line is, or how far a shadow blurs, in pixels.</summary>
    public float Thickness;

    /// <summary>Zero reads the atlas. Anything else comes from <see cref="For"/>.</summary>
    public int TextureIndex;

    public OverlayKind Kind;

    public OverlayGradient Gradient;

    /// <summary>The value of <see cref="TextureIndex"/> that draws this texture.</summary>
    public static int For(TextureHandle texture) => texture.Index + 1;
}

/// <summary>What an <see cref="OverlayQuad"/> draws inside its rectangle.</summary>
public enum OverlayKind
{
    /// <summary>A rounded box, a character from the atlas, or an image.</summary>
    Box,

    /// <summary>An ellipse filling the rectangle, which can have a border.</summary>
    Ellipse,

    /// <summary>Part of a ring with round ends, as thick as <see cref="OverlayQuad.Thickness"/>, touching the rectangle's shorter sides.</summary>
    Arc,

    /// <summary>A line with round ends between the two points in <see cref="OverlayQuad.Params"/>.</summary>
    Segment,

    /// <summary>
    /// The soft shadow of the rounded box in <see cref="OverlayQuad.Params"/>, blurred by
    /// <see cref="OverlayQuad.Thickness"/>. It isn't drawn inside the box, so it doesn't show through one that's see-through.
    /// </summary>
    Shadow,
}

/// <summary>How a box or an ellipse goes from <see cref="OverlayQuad.Color"/> to <see cref="OverlayQuad.Color2"/>.</summary>
public enum OverlayGradient
{
    None,
    Linear,
    Radial,
}

/// <summary>
/// A straight line in the world, drawn over the finished picture a set number of pixels thick, for seeing what a
/// game is doing: a path, a trigger area, a ray. It comes after the grade, so its colour is exactly the one asked
/// for, in sRGB as written in an image editor. Where something stands in front of it, it shows faintly, as
/// <see cref="Hidden"/> says.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct LineDraw
{
    /// <summary>Where the line starts, in the world.</summary>
    public Vector3 From;

    /// <summary>How thick the line is, in pixels of the window.</summary>
    public float Width;

    /// <summary>Where the line ends, in the world.</summary>
    public Vector3 To;

    /// <summary>
    /// How much of the line shows where the scene is in front of it, from 0, none, to 1, all of it, so a line at 1 is
    /// drawn over everything.
    /// </summary>
    public float Hidden;

    /// <summary>Red, green and blue in sRGB, and how solid it is, each from 0 to 1.</summary>
    public Vector4 Color;
}
