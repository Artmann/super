using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// What surrounds the world in a frame, reached through <see cref="View.Surroundings"/>: the sky and its fog, the
/// water the camera may be under, the puddles on the ground and the wind. Like the rest of a view, it holds only what
/// this frame says.
/// </summary>
public sealed class ViewSurroundings
{
    // How far a sky image's exposure goes either way, in stops; past it the image's half floats overflow or vanish.
    private const float MaxSkyExposure = 20f;

    internal ViewSurroundings()
    {
    }

    internal UnderwaterSettings WaterSettings { get; private set; }
    internal PuddleSettings? PuddleSettings { get; private set; }
    internal ReflectionPlane? ReflectionPlaneSettings { get; private set; }
    internal WindSettings? WindSettings { get; private set; }
    internal SkySettings? SkySettings { get; private set; }

    /// <summary>
    /// Sets the sky: a gradient from the ground up through the horizon to straight up, and
    /// haze and fog that set distant things back in it, or a physical or photographed sky in its place. Without one
    /// the world sits in a plain grey studio. Water, when set, takes over the sky.
    /// </summary>
    public void Sky(in SkySettings settings)
    {
        CheckSkyLight(settings);
        CheckSkyBodies(settings);
        CheckClouds(settings);
        RefuseBadFog(settings.Fog, nameof(settings));
        SkySettings = settings;
    }

    /// <summary>Puts the whole view underwater.</summary>
    public void Water(in UnderwaterSettings settings) => WaterSettings = settings;

    /// <summary>
    /// Lets water stand at <paramref name="level"/>, a height in metres, on every surface whose material asks for
    /// puddles. Whatever lies below it, counting the material's height map so hollows fill first, turns into a rippled
    /// mirror that darkens as it deepens, and for <paramref name="band"/> metres above it the surface is damp: darker
    /// and smoother at the waterline, dry at the band's top. <see cref="GraphicsSurfaces.Puddles"/> turns it off. See
    /// docs/rendering.md.
    /// </summary>
    public void Puddles(float level, float band = Rendition.PuddleSettings.DefaultBand)
    {
        if (!float.IsFinite(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The water level must be a height in metres.");
        }

        if (!(float.IsFinite(band) && band >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(band), band, "The damp band above the water must be 0 or more metres, and 0 leaves no damp band.");
        }

        PuddleSettings = new PuddleSettings(level, band);
    }

    /// <summary>
    /// Mirrors the scene in the flat, level plane at <paramref name="height"/> metres, such as a wet floor or a calm
    /// pool: surfaces lying in it that face up and are smooth enough show the scene from a camera mirrored under the
    /// plane, so they reflect what is off screen or hidden too, blurred by their roughness. Puddles set the plane at
    /// their level by themselves; this takes over from them. <see cref="GraphicsLighting.PlanarReflections"/> turns it
    /// off. See docs/rendering.md.
    /// </summary>
    public void ReflectionPlane(float height)
    {
        if (!float.IsFinite(height))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The reflection plane's height must be a number of metres.");
        }

        ReflectionPlaneSettings = new Rendition.ReflectionPlane(height);
    }

    /// <summary>
    /// Lets the wind blow along the ground towards <paramref name="direction"/>, swaying every surface whose material
    /// asks for it, and its shadow with it. <paramref name="strength"/> is how far, in metres, it leans the tip of grass
    /// 1 m tall at a gust's peak: 0.1 is a breeze, 0.35 a fresh wind and 1 a gale. Gusts roll across the ground
    /// downwind; <paramref name="gustiness"/> runs from 0 for a steady wind to 1 for one that drops to a fifth of its
    /// strength between them. <see cref="GraphicsSurfaces.Wind"/> turns it off. See docs/rendering.md.
    /// </summary>
    public void Wind(Vector3 direction, float strength, float gustiness = Rendition.WindSettings.DefaultGustiness)
    {
        if (!(float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z)))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "The wind's direction must be finite.");
        }

        if (!(float.IsFinite(strength) && strength >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(strength), strength, "The wind's strength must be 0 or more metres, and 0 is a calm.");
        }

        if (strength > 0f && direction.X * direction.X + direction.Z * direction.Z < 1e-12f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction), direction, "The wind must blow along the ground, so its direction needs some x or z.");
        }

        if (!(gustiness is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gustiness), gustiness, "The wind's gustiness must be from 0 for steady to 1 for gusty.");
        }

        WindSettings = new WindSettings(direction, strength, gustiness);
    }

    /// <summary>Forgets the last frame's sky, water, puddles, reflection plane and wind as a frame starts.</summary>
    internal void Clear()
    {
        WaterSettings = default;
        PuddleSettings = null;
        ReflectionPlaneSettings = null;
        WindSettings = null;
        SkySettings = null;
    }

    // The sky's brightness, its haze and how clear a physical sky is.
    private static void CheckSkyLight(in SkySettings settings)
    {
        if (settings.Brightness < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Brightness, "The sky's brightness can't be negative.");
        }

        if (settings.HazeStart < 0f || settings.HazeEnd < settings.HazeStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"The haze must start at zero or further and end no nearer than it starts, but it runs from {settings.HazeStart} to {settings.HazeEnd} metres.");
        }

        float turbidity = settings.Turbidity;
        if (!(turbidity == 0f || turbidity is >= Super.SkySettings.MinTurbidity and <= Super.SkySettings.MaxTurbidity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The sky's turbidity must be 0 for the gradient sky, or from {Super.SkySettings.MinTurbidity} to {Super.SkySettings.MaxTurbidity} for a clear one, but it is {turbidity}."));
        }
    }

    // The moon, the stars and a photographed sky's turn and exposure.
    private static void CheckSkyBodies(in SkySettings settings)
    {
        if (!(float.IsFinite(settings.Moon.X) && float.IsFinite(settings.Moon.Y) && float.IsFinite(settings.Moon.Z))
            || !float.IsFinite(settings.StarRotation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The moon's direction and the stars' rotation must be numbers, but they are {settings.Moon} and {settings.StarRotation}."));
        }

        if (!float.IsFinite(settings.Rotation) || !(settings.Exposure is >= -MaxSkyExposure and <= MaxSkyExposure))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The sky image's rotation must be a number and its exposure from {-MaxSkyExposure} to {MaxSkyExposure} stops, but they are {settings.Rotation} and {settings.Exposure}."));
        }
    }

    // The clouds' cover, thickness, height and wind.
    private static void CheckClouds(in SkySettings settings)
    {
        if (settings.Clouds is not { } clouds)
        {
            return;
        }

        bool fractions = clouds.Coverage is >= 0f and <= 1f && clouds.Cirrus is >= 0f and <= 1f;
        bool density = clouds.Density is >= 0f and <= CloudSettings.MaxDensity;
        bool altitude = clouds.Altitude is >= 0f and <= CloudSettings.MaxAltitude;
        bool thickness = clouds.Thickness == 0f || clouds.Thickness is >= CloudSettings.MinThickness and <= CloudSettings.MaxThickness;
        bool wind = float.IsFinite(clouds.Wind.X) && float.IsFinite(clouds.Wind.Y);
        if (!(fractions && density && altitude && thickness && wind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The clouds' coverage and cirrus must be from 0 to 1, their density from 0 to {CloudSettings.MaxDensity}, their altitude from 0 to {CloudSettings.MaxAltitude} metres, their thickness 0 or from {CloudSettings.MinThickness} to {CloudSettings.MaxThickness} metres and their wind a number, but they are {clouds.Coverage}, {clouds.Cirrus}, {clouds.Density}, {clouds.Altitude}, {clouds.Thickness} and {clouds.Wind}."));
        }
    }

    private static void RefuseBadFog(in FogSettings fog, string paramName)
    {
        RefuseBelowZero(fog.Density, "density", "per metre");
        RefuseBelowZero(fog.Falloff, "falloff", "per metre, and 0 is fog of one density at every height");
        RefuseBelowZero(fog.SunScatter, "sun scatter", "and 0 gives no glow towards the sun");
        if (!float.IsFinite(fog.Height))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(CultureInfo.InvariantCulture, $"The fog's height must be a number of metres, but it is {fog.Height}."));
        }

        if (!(MathF.Abs(fog.Anisotropy) <= FogSettings.MaxAnisotropy))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The fog's anisotropy must be from -0.9 to 0.9, where 0 scatters light evenly and more scatters it onwards, but it is {fog.Anisotropy}."));
        }

        RefuseBelowZero(fog.VolumetricDistance, "volumetric distance", "in metres, and 0 is the plain fog only");

        void RefuseBelowZero(float value, string name, string unit)
        {
            if (!(float.IsFinite(value) && value >= 0f))
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    string.Create(CultureInfo.InvariantCulture, $"The fog's {name} must be 0 or more, {unit}, but it is {value}."));
            }
        }
    }
}
