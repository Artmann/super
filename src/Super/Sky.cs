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
}
