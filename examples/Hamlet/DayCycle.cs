using System.Numerics;
using Rendition;
using Super;

namespace Hamlet;

/// <summary>
/// A day in two minutes. The sun rises in the east, crosses the south and sets in the west,
/// and the moon takes over at night from the other side. The sky's colours follow the sun.
/// Everything here is worked out from the time alone, so any tick gives the same light.
/// </summary>
internal static class DayCycle
{
    /// <summary>How long a whole day lasts, in seconds.</summary>
    public const float Length = 120f;

    /// <summary>The time of day when the game starts: 0 is midnight, 0.5 noon.</summary>
    public const float Start = 0.975f;

    /// <summary>
    /// How often the sky's colours change, in seconds. A gradient sky is lit from a baked
    /// copy, and changing its colours bakes it again, which waits for the graphics card, so
    /// they step rather than glide.
    /// </summary>
    public const float SkyStep = 0.5f;

    private static readonly Vector3 NightZenith = new(0.004f, 0.006f, 0.016f);
    private static readonly Vector3 NightHorizon = new(0.012f, 0.016f, 0.032f);
    private static readonly Vector3 TwilightZenith = new(0.05f, 0.065f, 0.15f);
    private static readonly Vector3 TwilightHorizon = new(0.45f, 0.24f, 0.13f);
    private static readonly Vector3 LowZenith = new(0.16f, 0.24f, 0.5f);
    private static readonly Vector3 LowHorizon = new(0.95f, 0.55f, 0.3f);
    private static readonly Vector3 DayZenith = new(0.18f, 0.36f, 0.85f);
    private static readonly Vector3 DayHorizon = new(0.62f, 0.75f, 0.9f);

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
    /// Adds the sun, or the moon once the sun is down, the sky and the post-processing for
    /// this moment. The inside of a building is darker than the open air, so the exposure
    /// opens up by <paramref name="indoors"/> stops there.
    /// </summary>
    public static void Light(View view, float seconds, float indoors)
    {
        var sun = SunDirection(seconds);
        float elevation = MathF.Asin(sun.Y);
        if (elevation > 0f)
        {
            float high = Smooth(0f, 0.45f, elevation);
            var color = Vector3.Lerp(new Vector3(3.6f, 1.5f, 0.6f), new Vector3(5.2f, 4.9f, 4.4f), high);
            view.Sun(sun, color * Smooth(0f, 0.05f, elevation), shadows: true);
        }
        else
        {
            // The moon is opposite the sun, and rises as the sun sinks.
            var moon = new Vector3(-sun.X, -sun.Y, -sun.Z);
            float up = Smooth(0f, 0.08f, -elevation);
            view.Sun(moon, new Vector3(0.16f, 0.21f, 0.36f) * up, shadows: true);
        }

        // The sky's colours step every half second, so its baked lighting isn't redone each frame.
        float stepped = MathF.Floor(seconds / SkyStep) * SkyStep;
        float skyElevation = Elevation(stepped);
        var (zenith, horizon) = SkyColors(skyElevation);
        float day = Smooth(-0.1f, 0.2f, skyElevation);
        view.Sky(new SkySettings
        {
            Zenith = zenith,
            Horizon = horizon,
            Ground = Vector3.Lerp(new Vector3(0.006f, 0.007f, 0.01f), new Vector3(0.16f, 0.15f, 0.12f), day),
            Brightness = 1f,
            HazeStart = 110f,
            HazeEnd = 420f,
            Fog = new FogSettings(0.0018f, 0f, 0.08f, 0.35f),
        });

        // Night is far darker than day; the camera opens up to see by lamplight and moonlight.
        // By day it stops down a little, so sunlight stands out from the sky's light in the shade.
        float night = 1f - Smooth(-0.12f, 0.1f, elevation);
        view.Finish(new PostSettings
        {
            Exposure = float.Lerp(-0.6f, 3.4f, night) + indoors,
            Contrast = 0.06f,
            Saturation = 0.05f,
            Vignette = 0.22f,
            Bloom = float.Lerp(0f, 0.08f, night),
            Temperature = float.Lerp(0.04f, -0.12f, night),
        });
    }

    /// <summary>The sky straight up and round the horizon when the sun is at this height.</summary>
    private static (Vector3 Zenith, Vector3 Horizon) SkyColors(float elevation)
    {
        if (elevation < -0.2f)
        {
            return (NightZenith, NightHorizon);
        }

        if (elevation < -0.04f)
        {
            float t = Smooth(-0.2f, -0.04f, elevation);
            return (Vector3.Lerp(NightZenith, TwilightZenith, t), Vector3.Lerp(NightHorizon, TwilightHorizon, t));
        }

        if (elevation < 0.08f)
        {
            float t = Smooth(-0.04f, 0.08f, elevation);
            return (Vector3.Lerp(TwilightZenith, LowZenith, t), Vector3.Lerp(TwilightHorizon, LowHorizon, t));
        }

        float u = Smooth(0.08f, 0.4f, elevation);
        return (Vector3.Lerp(LowZenith, DayZenith, u), Vector3.Lerp(LowHorizon, DayHorizon, u));
    }

    public static float Smooth(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
