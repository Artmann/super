using System.Numerics;

namespace Rendition;

/// <summary>
/// The clouds in a physical sky: a layer of heaped cumulus that the sun and the moon light, that the wind carries and
/// that shades the ground beneath, and a veil of high cirrus above it. Only a physical sky has clouds, one with a
/// turbidity and no photographed image. The default, <see cref="Clear"/>, has none; <see cref="Fair"/>,
/// <see cref="Cloudy"/> and <see cref="Overcast"/> are ready-made weather to start from. See "Clouds" in
/// docs/rendering.md.
/// </summary>
public readonly record struct CloudSettings
{
    /// <summary>The height of the clouds' base used when <see cref="Altitude"/> is 0, in metres.</summary>
    public const float DefaultAltitude = 1500f;

    /// <summary>How deep the layer is when <see cref="Thickness"/> is 0, in metres.</summary>
    public const float DefaultThickness = 1600f;

    /// <summary>The highest <see cref="Altitude"/>, in metres.</summary>
    public const float MaxAltitude = 8000f;

    /// <summary>The thinnest and thickest the layer can be, in metres.</summary>
    public const float MinThickness = 200f;

    /// <inheritdoc cref="MinThickness"/>
    public const float MaxThickness = 4000f;

    /// <summary>The most <see cref="Density"/> can be.</summary>
    public const float MaxDensity = 4f;

    /// <summary>A clear sky: no clouds at all.</summary>
    public static CloudSettings Clear => default;

    /// <summary>Fair weather: scattered cumulus over about a third of the sky and a little cirrus, drifting slowly.</summary>
    public static CloudSettings Fair => new()
    {
        Coverage = 0.38f,
        Density = 1f,
        Wind = new Vector2(8f, 3f),
        Cirrus = 0.35f,
    };

    /// <summary>Broken cloud over more than half the sky, so the sun comes and goes.</summary>
    public static CloudSettings Cloudy => new()
    {
        Coverage = 0.6f,
        Density = 1.2f,
        Wind = new Vector2(12f, 4f),
        Cirrus = 0.2f,
    };

    /// <summary>A grey sky: a thick deck hides the sun, and its soft light comes from everywhere at once.</summary>
    public static CloudSettings Overcast => new()
    {
        Coverage = 0.95f,
        Density = 1.6f,
        Altitude = 900f,
        Thickness = 2200f,
        Wind = new Vector2(6f, 2f),
    };

    /// <summary>
    /// How much of the sky the cumulus covers, from 0 for none to 1 for all of it. Past about 0.85 the clouds merge
    /// into a deck and the light under it turns grey.
    /// </summary>
    public float Coverage { get; init; }

    /// <summary>
    /// How thick the clouds are inside, from 0 to <see cref="MaxDensity"/>: 1 is fair-weather cumulus, more is darker
    /// and greyer underneath, less is thin and wispy. 0 counts as 1.
    /// </summary>
    public float Density { get; init; }

    /// <summary>
    /// How high the clouds' base is above the world's ground, y 0, in metres, up to <see cref="MaxAltitude"/>. 0 is
    /// <see cref="DefaultAltitude"/>.
    /// </summary>
    public float Altitude { get; init; }

    /// <summary>
    /// How deep the layer is from its base to the tallest tops, in metres, from <see cref="MinThickness"/> to
    /// <see cref="MaxThickness"/>. 0 is <see cref="DefaultThickness"/>.
    /// </summary>
    public float Thickness { get; init; }

    /// <summary>
    /// How fast and which way the clouds drift, in metres a second across the ground, x and z. The cirrus drifts
    /// twice as fast. Zero leaves the clouds still, though they still change shape slowly.
    /// </summary>
    public Vector2 Wind { get; init; }

    /// <summary>The high, thin streaks of cirrus above the cumulus, from 0 for none to 1 for a sky full of them.</summary>
    public float Cirrus { get; init; }

    /// <summary>Whether there is anything to draw: some cumulus or some cirrus.</summary>
    public bool Any => Coverage > 0f || Cirrus > 0f;

    /// <summary>The base's height, <see cref="DefaultAltitude"/> for 0, held to 0 to <see cref="MaxAltitude"/>.</summary>
    internal float BaseHeight => Altitude > 0f ? Math.Min(Altitude, MaxAltitude) : DefaultAltitude;

    /// <summary>The layer's depth, <see cref="DefaultThickness"/> for 0, held to its range.</summary>
    internal float Depth => Thickness > 0f ? Math.Clamp(Thickness, MinThickness, MaxThickness) : DefaultThickness;

    /// <summary>The density, 1 for 0, held to <see cref="MaxDensity"/>.</summary>
    internal float Thickening => Density > 0f ? Math.Min(Density, MaxDensity) : 1f;
}
