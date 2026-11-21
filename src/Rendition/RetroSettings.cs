using System.Numerics;

namespace Rendition;

/// <summary>
/// An old console's or an old screen's picture: big square pixels, fewer shades of each colour and a fine pattern of
/// dots between the shades. The default changes nothing. See "Retro pixels" in docs/rendering.md.
/// </summary>
public struct RetroSettings
{
    /// <summary>The largest <see cref="PixelSize"/>. A larger one is drawn this size.</summary>
    public const float MaxPixelSize = 32f;

    /// <summary>
    /// How big each of the picture's pixels is drawn, in pixels of a picture 1080 pixels tall, so the blocks cover the
    /// same share of the screen at any size: 4 makes a 1080-pixel picture 270 pixels tall. 1 or less leaves the pixels
    /// alone.
    /// </summary>
    public float PixelSize;

    /// <summary>
    /// How many shades each colour channel keeps, from 2 to 256, as on a screen with few colours. 0 keeps them all.
    /// </summary>
    public int Levels;

    /// <summary>
    /// How much a pattern of dots blends neighbouring shades where <see cref="Levels"/> cuts them, from 0, hard steps,
    /// to 1, an even pattern. The dots are the size of the picture's pixels.
    /// </summary>
    public float Dither;

    /// <summary>
    /// Packs the settings for the finish pass: x is a pixel's height as a share of the picture's, 0 for none; y is the
    /// number of steps between black and white, 0 for none; z is the dither. All zero while <see cref="RenderEffects.Retro"/>
    /// is off.
    /// </summary>
    internal readonly Vector4 Pack(RenderEffects off)
    {
        if ((off & RenderEffects.Retro) != 0)
        {
            return Vector4.Zero;
        }

        float pixel = PixelSize > 1f ? Math.Min(PixelSize, MaxPixelSize) / 1080f : 0f;
        float steps = Levels > 0 ? Math.Clamp(Levels, 2, 256) - 1 : 0f;
        return new Vector4(pixel, steps, steps > 0f ? Math.Clamp(Dither, 0f, 1f) : 0f, 0f);
    }
}
