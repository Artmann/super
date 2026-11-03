using System.Numerics;

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
}
