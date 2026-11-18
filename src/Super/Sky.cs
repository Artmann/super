using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The sky around the world. It's what shows where nothing was drawn, and what lights and
/// reflects in every surface, so the two always match. Colors are red, green and blue;
/// above 1 is brighter.
/// </summary>
public struct SkySettings
{
    /// <summary>The color straight up.</summary>
    public Vector3 Zenith;

    /// <summary>The color all round the horizon.</summary>
    public Vector3 Horizon;

    /// <summary>The color below the horizon, which lights things from underneath.</summary>
    public Vector3 Ground;

    /// <summary>Scales the whole sky, both as seen and as light. Zero counts as 1.</summary>
    public float Brightness;

    /// <summary>
    /// How far away, in metres, things start fading into the sky behind them, and where
    /// they're gone. Leaving both at zero gives no haze.
    /// </summary>
    public float HazeStart;

    /// <inheritdoc cref="HazeStart"/>
    public float HazeEnd;

    /// <summary>
    /// Measures the haze from the point the camera looks at rather than from the camera. Suits a
    /// camera far off, as an isometric one is: from there everything on screen is about as far away,
    /// so haze from the camera fades it all alike, while haze from the focus fades the view's edges.
    /// </summary>
    public bool HazeFromFocus;

    /// <summary>What things fade into. Zero fades them into the sky behind them.</summary>
    public Vector3 HazeColor;

    /// <summary>
    /// Air that thickens towards the ground, so far things sit back in it: dimmed, tinted the
    /// colour of the sky behind them, and glowing towards the sun. Unlike haze it is measured
    /// along each line of sight and thins with height, so a tower's top is clearer than its foot.
    /// The default, no density, is clear air. <c>new FogSettings(0.004f, 0f, 0.15f, 0.6f)</c>
    /// is a light summer morning over a yard. Near the camera, up to its
    /// <see cref="FogSettings.VolumetricDistance"/>, the fog is lit by what really reaches it:
    /// the sun falls through it in shafts beside the shadows, and point and spot lights glow
    /// in it with halos. <see cref="GraphicsAtmosphere.VolumetricFog"/> turns that off.
    /// </summary>
    public FogSettings Fog;

    /// <summary>
    /// How hazy the air is, which turns on a physical sky lit by the sun: from 1.7, a crisp
    /// mountain sky, through 2 to 3 for a clear day, to 10, a milky summer haze. It glows
    /// round the sun, deepens overhead, reddens at sunset, fades to a faint glow at night, and
    /// shows the sun's disc, as wide as <c>View.Shadows</c>' sunSize. The sun's own light turns
    /// warm and dim as it sinks, and far surfaces fade into the air unless the view has a haze
    /// of its own (<see cref="GraphicsAtmosphere.AerialPerspective"/>). Zenith and Horizon are then not
    /// used; Ground still colours what's below the horizon, lit by the sun and the sky. Zero,
    /// the default, keeps the gradient.
    /// </summary>
    public float Turbidity;

    /// <summary>
    /// Towards the moon, which a physical sky shows as a disc lit on the side facing the sun, so it waxes and wanes
    /// as the two move apart. Zero, the default, is no moon. The moon's light is yours to add with <c>View.Sun</c>,
    /// aimed the same way; it may cast the shadows, and the sky still follows the sun, not it.
    /// </summary>
    public Vector3 Moon;

    /// <summary>
    /// Leaves the stars out of a physical sky's night, as for an overcast one. They fade in as the sky darkens after
    /// sunset; <see cref="GraphicsAtmosphere.Stars"/> turns them off everywhere.
    /// </summary>
    public bool HideStars;

    /// <summary>
    /// How far the stars have turned about the zenith, in radians. Leave it at zero for a still sky, or move it with
    /// the time for one that turns through the night.
    /// </summary>
    public float StarRotation;

    /// <summary>
    /// A photographed sky from <see cref="Content.Sky"/>, shown behind the scene and lighting and reflecting in it in
    /// place of Zenith, Horizon, Ground and <see cref="Turbidity"/>; the moon and stars are left out too. Its sun shows
    /// as a disc, but its light is yours to add with <c>View.Sun</c>, aimed at <see cref="SkyImage.SunDirectionAt"/>.
    /// See "A photographed sky" in docs/rendering.md.
    /// </summary>
    public SkyImage? Image;

    /// <summary>How far <see cref="Image"/> is turned about the up axis, in radians, counterclockwise seen from above.</summary>
    public float Rotation;

    /// <summary>Brightens <see cref="Image"/> by this many stops, or dims it below zero. Zero is the image as stored.</summary>
    public float Exposure;

    /// <summary>The clearest and the haziest a clear sky can be.</summary>
    public const float MinTurbidity = 1.7f;

    /// <inheritdoc cref="MinTurbidity"/>
    public const float MaxTurbidity = 10f;
}
