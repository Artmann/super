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
    /// in it with halos. <see cref="Graphics.VolumetricFog"/> turns that off.
    /// </summary>
    public FogSettings Fog;

    /// <summary>
    /// How hazy the air is, which turns on a real clear sky worked out from the sun: from 1.7, a
    /// crisp mountain sky, through 2 to 3 for a clear day, to 10, a milky summer haze. It
    /// brightens towards the sun, deepens overhead, and shows the sun's disc, as wide as
    /// <c>View.Shadows</c>' sunSize. Zenith and Horizon are then not used; Ground still colours
    /// what's below the horizon. Zero, the default, keeps the gradient.
    /// </summary>
    public float Turbidity;

    /// <summary>The clearest and the haziest a clear sky can be.</summary>
    public const float MinTurbidity = 1.7f;

    /// <inheritdoc cref="MinTurbidity"/>
    public const float MaxTurbidity = 10f;
}
