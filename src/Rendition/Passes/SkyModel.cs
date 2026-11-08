using System.Numerics;

namespace Rendition.Passes;

/// <summary>What the environment bake and the background need to draw a clear sky and its sun.</summary>
/// <param name="Turbidity">How hazy the air is; 0 for the environment's bands, when the rest is zero too.</param>
/// <param name="Direction">Towards the sun.</param>
/// <param name="DiscRadiance">How bright the sun's disc is, so that over the disc it adds up to the light's colour.</param>
/// <param name="DiscCosine">The cosine of the disc's angular radius.</param>
internal readonly record struct SkySun(float Turbidity, Vector3 Direction, Vector3 DiscRadiance, float DiscCosine);

/// <summary>
/// The Preetham, Shirley and Smits (1999) clear sky, as Shaders/sky.glsl works it out. Each
/// <see cref="Vector3"/> holds luminance Y, then chromaticity x and y.
/// </summary>
internal static class SkyModel
{
    /// <summary>
    /// The brightest a sun disc is drawn. Render targets hold half floats, which end at 65504,
    /// and bloom adds to the disc, so it stays well under.
    /// </summary>
    public const float MaxDiscRadiance = 30000f;

    /// <summary>
    /// The clearest air the model is drawn with. Below about 1.2 the luminance's B turns positive,
    /// and its exponential overflows at the horizon.
    /// </summary>
    public const float MinTurbidity = 1.7f;

    /// <summary>The haziest air the model is drawn with, the end of the paper's range.</summary>
    public const float MaxTurbidity = 10f;

    /// <summary>The Perez distribution's five coefficients for luminance and the two chromaticities.</summary>
    public static (Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 E) Perez(float turbidity)
    {
        float t = turbidity;
        return (
            new Vector3(0.1787f * t - 1.4630f, -0.0193f * t - 0.2592f, -0.0167f * t - 0.2608f),
            new Vector3(-0.3554f * t + 0.4275f, -0.0665f * t + 0.0008f, -0.0950f * t + 0.0092f),
            new Vector3(-0.0227f * t + 5.3251f, -0.0004f * t + 0.2125f, -0.0079f * t + 0.2102f),
            new Vector3(0.1206f * t - 2.5771f, -0.0641f * t - 0.8989f, -0.0441f * t - 1.6537f),
            new Vector3(-0.0670f * t + 0.3703f, -0.0033f * t + 0.0452f, -0.0109f * t + 0.0529f));
    }

    /// <summary>
    /// The sky straight up: luminance in thousands of candela per square metre, and its
    /// chromaticity, for a sun <paramref name="sunZenithAngle"/> radians from straight up.
    /// </summary>
    public static Vector3 Zenith(float turbidity, float sunZenithAngle)
    {
        float t = turbidity;
        float theta = sunZenithAngle;
        float chi = (4f / 9f - t / 120f) * (MathF.PI - 2f * theta);
        float luminance = (4.0453f * t - 4.9710f) * MathF.Tan(chi) - 0.2155f * t + 2.4192f;

        var angles = new Vector4(theta * theta * theta, theta * theta, theta, 1f);
        float x = t * t * Vector4.Dot(new Vector4(0.00166f, -0.00375f, 0.00209f, 0f), angles)
            + t * Vector4.Dot(new Vector4(-0.02903f, 0.06377f, -0.03202f, 0.00394f), angles)
            + Vector4.Dot(new Vector4(0.11693f, -0.21196f, 0.06052f, 0.25886f), angles);
        float y = t * t * Vector4.Dot(new Vector4(0.00275f, -0.00610f, 0.00317f, 0f), angles)
            + t * Vector4.Dot(new Vector4(-0.04214f, 0.08970f, -0.04153f, 0.00516f), angles)
            + Vector4.Dot(new Vector4(0.15346f, -0.26756f, 0.06670f, 0.26688f), angles);
        return new Vector3(luminance, x, y);
    }

    /// <summary>
    /// The view's clear sky and its sun: the light that casts the shadows, or else the first. The
    /// disc's radius is half of <see cref="ShadowSettings.LightAngle"/>, which is how wide the sun
    /// looks. A gradient sky gives all zeros, so moving the sun under one changes nothing here.
    /// A turbidity above zero is held from <see cref="MinTurbidity"/> to <see cref="MaxTurbidity"/>.
    /// </summary>
    public static SkySun Sun(in FrameView view)
    {
        if (!(view.Sky.Turbidity > 0f))
        {
            return default;
        }

        float turbidity = Math.Clamp(view.Sky.Turbidity, MinTurbidity, MaxTurbidity);
        if (view.Lights.IsEmpty)
        {
            return new SkySun(turbidity, Vector3.UnitY, Vector3.Zero, 1f);
        }

        int index = 0;
        for (int i = 0; i < Math.Min(view.Lights.Length, FrameUniforms.MaxLights); i++)
        {
            if (view.Lights[i].CastsShadow)
            {
                index = i;
                break;
            }
        }

        ref readonly var light = ref view.Lights[index];
        float radius = view.Shadows.LightAngle * 0.5f;
        // 2 pi (1 - cos r), written with the half-angle sine so a small sun keeps its precision.
        float halfSine = MathF.Sin(radius * 0.5f);
        float solidAngle = 4f * MathF.PI * halfSine * halfSine;
        var radiance = solidAngle > 0f
            ? Vector3.Min(light.Radiance / solidAngle, new Vector3(MaxDiscRadiance))
            : Vector3.Zero;
        return new SkySun(turbidity, Vector3.Normalize(light.Direction), radiance, MathF.Cos(radius));
    }
}
