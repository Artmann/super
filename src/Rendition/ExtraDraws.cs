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
/// A rectangle on the screen, in pixels from the top left, which can be rounded, bordered
/// and clipped. The atlas gives each pixel's alpha and color gives the rest, in sRGB as
/// written in an image editor. With <see cref="TextureIndex"/> set, a texture gives the
/// pixels and color tints them. A default value for any of these changes nothing.
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

    /// <summary>The line drawn inside the edge, in sRGB. Needs <see cref="BorderWidth"/>.</summary>
    public Vector4 BorderColor;

    /// <summary>Only what's inside this is drawn: x, y, width, height in pixels. No width means no clip.</summary>
    public Vector4 Clip;

    public float CornerRadius;
    public float BorderWidth;

    /// <summary>Rounds the corners of <see cref="Clip"/>.</summary>
    public float ClipRadius;

    /// <summary>Zero reads the atlas. Anything else comes from <see cref="For"/>.</summary>
    public int TextureIndex;

    /// <summary>The value of <see cref="TextureIndex"/> that draws this texture.</summary>
    public static int For(TextureHandle texture) => texture.Index + 1;
}
