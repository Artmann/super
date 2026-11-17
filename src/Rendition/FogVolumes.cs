using System.Globalization;
using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// The shape a fog volume fills, before its transform moves it. Each is a unit shape: the box spans −0.5 to 0.5;
/// the ellipsoid is a sphere of radius 0.5; the cylinder runs along Y with radius 0.5, from y −0.5 to 0.5; and the
/// cone has its tip at y 0.5 and a base of radius 0.5 at y −0.5.
/// </summary>
public enum FogShape
{
    /// <summary>A box from −0.5 to 0.5 on each axis.</summary>
    Box,

    /// <summary>A sphere of radius 0.5, stretched by the transform into an ellipsoid.</summary>
    Ellipsoid,

    /// <summary>A cylinder along Y, radius 0.5, from y −0.5 to 0.5.</summary>
    Cylinder,

    /// <summary>A cone with its tip at y 0.5 and a base of radius 0.5 at y −0.5, like a lamp's beam.</summary>
    Cone,
}

/// <summary>
/// How thick and what colour a fog volume is. <paramref name="Density"/> is how much of the light the fog stops per
/// metre, the same measure as the scene's fog: 0.05 is a thin haze, 0.5 a mist you can barely see across a room in.
/// </summary>
/// <param name="Density">How much of the light the fog stops per metre. 0 or more.</param>
public readonly record struct FogVolumeSettings(float Density)
{
    /// <summary>The colour the fog scatters light in, each channel from 0 to 1. White by default.</summary>
    public Vector3 Color { get; init; } = Vector3.One;

    /// <summary>
    /// The share of the way in from the surface over which the density rises from nothing to full, from 0 (a hard
    /// edge) to 1 (thickest only at the centre).
    /// </summary>
    public float EdgeFade { get; init; } = 0.25f;

    /// <summary>How much noise breaks up the density, from 0 (even) to 1 (patchy, with clear gaps).</summary>
    public float Noise { get; init; }

    /// <summary>How big the noise's features are, in metres. More than 0.</summary>
    public float NoiseSize { get; init; } = 2f;

    /// <summary>How fast the noise drifts, in metres per second, so the fog seems to move.</summary>
    public Vector3 Wind { get; init; }
}

/// <summary>One fog volume placed for this frame: its shape, where it is and what it is like.</summary>
public readonly record struct FogVolumeDraw(FogShape Shape, Matrix4x4 Transform, FogVolumeSettings Settings);

/// <summary>Checks the fog volumes a frame is given.</summary>
public static class FogVolumeBatch
{
    /// <summary>How many fog volumes one frame can hold. More throws.</summary>
    public const int MaxVolumes = 64;

    /// <summary>
    /// Throws when a fog volume can't be drawn: a shape that isn't one of the four, a transform squashed flat, or a
    /// setting out of range, named in the message.
    /// </summary>
    public static void Check(FogShape shape, in Matrix4x4 transform, in FogVolumeSettings settings)
    {
        if (!Enum.IsDefined(shape))
        {
            throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "A fog volume can be a Box, an Ellipsoid, a Cylinder or a Cone.");
        }

        if (!Matrix4x4.Invert(transform, out _) || !DecalBatch.IsFinite(transform))
        {
            throw new ArgumentException(
                "The fog volume's transform can't be inverted: it is squashed flat on some axis, or holds a number that isn't finite. "
                + "Give it a width, a height and a depth.",
                nameof(transform));
        }

        CheckSettings(settings);
    }

    private static void CheckSettings(in FogVolumeSettings settings)
    {
        if (!(settings.Density >= 0f && float.IsFinite(settings.Density)))
        {
            Refuse($"The fog volume's density is {settings.Density}, but it must be 0 or more.");
        }

        var color = settings.Color;
        if (!(Vector3.Clamp(color, Vector3.Zero, Vector3.One) == color))
        {
            Refuse($"The fog volume's colour is {color}, but each channel must be from 0 to 1.");
        }

        CheckShare(settings.EdgeFade, nameof(FogVolumeSettings.EdgeFade));
        CheckShare(settings.Noise, nameof(FogVolumeSettings.Noise));
        if (!(settings.NoiseSize > 0f && float.IsFinite(settings.NoiseSize)))
        {
            Refuse($"The fog volume's NoiseSize is {settings.NoiseSize}, but it must be more than 0 metres.");
        }

        var wind = settings.Wind;
        if (!(float.IsFinite(wind.X) && float.IsFinite(wind.Y) && float.IsFinite(wind.Z)))
        {
            Refuse($"The fog volume's wind is {wind}, but it must be finite.");
        }
    }

    private static void CheckShare(float value, string name)
    {
        if (!(value >= 0f && value <= 1f))
        {
            Refuse($"The fog volume's {name} is {value}, but it must be from 0 to 1.");
        }
    }

    private static void Refuse(FormattableString message) =>
        throw new ArgumentException(message.ToString(CultureInfo.InvariantCulture), "settings");
}
