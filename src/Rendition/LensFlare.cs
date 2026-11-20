using System.Numerics;

namespace Rendition;

/// <summary>
/// The glints a bright light leaves across the picture, as light bounces between a camera's lenses: from the sun, and
/// from any bright lights a game names. Each element sits on the line from the light through the middle of the screen,
/// and the flare fades as something covers the light. It needs no setup: with a sun on screen the default set shows. See
/// "Lens flare" in docs/rendering.md.
/// </summary>
public struct LensFlareSettings
{
    /// <summary>At most this many lights flare at once: the sun and <see cref="MaxLights"/> named lights.</summary>
    public const int MaxSources = 4;

    /// <summary>At most this many named lights flare, besides the sun.</summary>
    public const int MaxLights = MaxSources - 1;

    /// <summary>At most this many elements make up a flare.</summary>
    public const int MaxElements = 16;

    /// <summary>
    /// How bright the whole flare is, as a scale on its elements: 1 when null, 0 for none, 2 for twice as bright.
    /// </summary>
    public float? Intensity;

    /// <summary>The elements of the flare, at most <see cref="MaxElements"/>. Empty is <see cref="DefaultElements"/>.</summary>
    public ReadOnlyMemory<LensFlareElement> Elements;

    /// <summary>
    /// Bright lights besides the sun that flare, such as a lighthouse lamp or a car's headlight, at most
    /// <see cref="MaxLights"/>. The sun always flares when it is on screen.
    /// </summary>
    public ReadOnlyMemory<LensFlareLight> Lights;

    /// <summary>
    /// A subtle flare: a thin streak and a faint halo round the light, a few soft ghosts and hexagons towards and past
    /// the middle of the screen, and a ring on the far side. Each is coloured by the light.
    /// </summary>
    public static ReadOnlyMemory<LensFlareElement> DefaultElements { get; } = new LensFlareElement[]
    {
        new(LensFlareShape.Streak, 0f, 1.1f, new Vector3(0.05f, 0.07f, 0.1f)),
        new(LensFlareShape.Halo, 0f, 0.9f, new Vector3(0.012f, 0.012f, 0.014f)),
        new(LensFlareShape.Polygon, 0.35f, 0.06f, new Vector3(0.03f, 0.025f, 0.02f)),
        new(LensFlareShape.Ghost, 0.6f, 0.1f, new Vector3(0.035f, 0.025f, 0.015f)),
        new(LensFlareShape.Ghost, 1.25f, 0.07f, new Vector3(0.015f, 0.03f, 0.02f)),
        new(LensFlareShape.Polygon, 1.5f, 0.12f, new Vector3(0.012f, 0.015f, 0.025f)),
        new(LensFlareShape.Ghost, 1.8f, 0.16f, new Vector3(0.01f, 0.014f, 0.025f)),
        new(LensFlareShape.Ring, 2.1f, 0.25f, new Vector3(0.012f, 0.014f, 0.018f)),
    };
}

/// <summary>The shape of one <see cref="LensFlareElement"/>.</summary>
public enum LensFlareShape
{
    /// <summary>A soft disc with a brighter rim, its colours a little apart at the edge.</summary>
    Ghost,

    /// <summary>A thin soft ring.</summary>
    Ring,

    /// <summary>A soft polygon, the shape of the lens's aperture, with <see cref="LensFlareElement.Sides"/> sides.</summary>
    Polygon,

    /// <summary>A thin line of light across the screen through the light, as from an anamorphic lens.</summary>
    Streak,

    /// <summary>A wide faint ring round the light, its colours a little apart, like a rainbow.</summary>
    Halo,
}

/// <summary>
/// One glint of a <see cref="LensFlareSettings"/>. <paramref name="Position"/> places it on the line from the light through
/// the middle of the screen: 0 is on the light, 1 the middle and 2 the light's mirror across the middle. <paramref name="Size"/>
/// is its width in screen heights. <paramref name="Color"/> is linear red, green and blue, multiplied by the light's colour,
/// so small values such as 0.02 are usual for the sun.
/// </summary>
public readonly record struct LensFlareElement(LensFlareShape Shape, float Position, float Size, Vector3 Color)
{
    /// <summary>A <see cref="LensFlareShape.Polygon"/>'s sides, from 3. 0 is six.</summary>
    public int Sides { get; init; }
}

/// <summary>
/// A bright light that flares, besides the sun. <paramref name="Color"/> is its linear colour and brightness, like a light's
/// radiance, and <paramref name="Radius"/> how far round it, in metres, something must cover to hide it. Something nearer
/// the camera than the light by less than the radius, such as the lamp's own glass, doesn't hide it.
/// </summary>
public readonly record struct LensFlareLight(Vector3 Position, Vector3 Color, float Radius = 0.2f);
