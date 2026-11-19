using System.Numerics;
using Rendition;
using Super;

namespace Hamlet;

/// <summary>
/// A day in two minutes. The sun rises in the east, crosses the south and sets in the west,
/// and the moon takes over at night from the other side, under a physical sky with stars.
/// Everything here is worked out from the time alone, so any tick gives the same light.
/// </summary>
internal static class DayCycle
{
    /// <summary>How long a whole day lasts, in seconds.</summary>
    public const float Length = 120f;

    /// <summary>The time of day when the game starts: 0 is midnight, 0.5 noon.</summary>
    public const float Start = 0.975f;

    /// <summary>The time of day at a moment, from 0 at midnight round to 1.</summary>
    public static float TimeOfDay(float seconds)
    {
        float day = Start + seconds / Length;
        return day - MathF.Floor(day);
    }

    /// <summary>Which way the sun is, pointing from the ground to it; below the horizon at night.</summary>
    public static Vector3 SunDirection(float seconds)
    {
        float angle = MathF.Tau * (TimeOfDay(seconds) - 0.25f);
        return Vector3.Normalize(new Vector3(MathF.Cos(angle), 0.7f * MathF.Sin(angle), -0.7f * MathF.Sin(angle)));
    }

    /// <summary>How high the sun is, in radians: negative when it has set.</summary>
    public static float Elevation(float seconds) => MathF.Asin(SunDirection(seconds).Y);

    /// <summary>
    /// The sun at this moment. It is always there, white: the sky's air reddens and dims it as it sinks and puts it
    /// out below the horizon. It casts the shadows by day and the moon by night.
    /// </summary>
    public static Sun Sun(float seconds)
    {
        var sun = SunDirection(seconds);
        return new Sun(sun, new Vector3(5.2f, 4.9f, 4.4f), CastsShadows: sun.Y > 0f);
    }

    /// <summary>The moon, opposite the sun, rising as the sun sinks; null while the sun is up.</summary>
    public static Sun? Moon(float seconds)
    {
        float elevation = Elevation(seconds);
        if (elevation > 0f)
        {
            return null;
        }

        float up = Smooth(0f, 0.08f, -elevation);
        return new Sun(-SunDirection(seconds), new Vector3(0.16f, 0.21f, 0.36f) * up, CastsShadows: true);
    }

    /// <summary>
    /// A physical sky that follows the sun, with the moon and the stars, which turn once a day; its lighting is rebuilt
    /// on the GPU without a wait.
    /// </summary>
    public static Sky Sky(float seconds)
    {
        float elevation = Elevation(seconds);
        float day = Smooth(-0.1f, 0.2f, elevation);
        return new Sky(new SkySettings
        {
            Turbidity = 2.5f,
            Moon = -SunDirection(seconds),
            StarRotation = MathF.Tau * TimeOfDay(seconds),
            Ground = Vector3.Lerp(new Vector3(0.006f, 0.007f, 0.01f), new Vector3(0.16f, 0.15f, 0.12f), day),
            Brightness = 1f,
            Fog = new FogSettings(0.0018f, 0f, 0.08f, 0.35f),
        });
    }

    /// <summary>
    /// The post-processing at this moment. The inside of a building is darker than the open air, so the exposure opens
    /// up by <paramref name="indoors"/> stops there.
    /// </summary>
    public static Finish Expose(float seconds, float indoors)
    {
        float elevation = Elevation(seconds);
        // Night is far darker than day; the camera opens up to see by lamplight and moonlight. The air dims the light
        // well before sunset, so it starts opening while the sun is still a little way up.
        // By day it stops down a little, so sunlight stands out from the sky's light in the shade.
        float night = 1f - Smooth(-0.1f, 0.25f, elevation);
        return new Finish(new PostSettings
        {
            Exposure = float.Lerp(-0.6f, 3.4f, night) + indoors,
            Contrast = 0.06f,
            Saturation = 0.05f,
            Vignette = 0.22f,
            Bloom = float.Lerp(0f, 0.08f, night),
            Temperature = float.Lerp(0.04f, -0.12f, night),
        });
    }

    public static float Smooth(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
