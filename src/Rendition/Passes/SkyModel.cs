using System.Numerics;

namespace Rendition.Passes;

/// <summary>What the environment bake and the background need to draw a clear sky and its sun.</summary>
/// <param name="Turbidity">How hazy the air is; 0 for the environment's bands, when the rest is zero too.</param>
/// <param name="Direction">Towards the sun.</param>
/// <param name="DiscRadiance">How bright the sun's disc is, so that over the disc it adds up to the light's colour.</param>
/// <param name="DiscCosine">The cosine of the disc's angular radius.</param>
internal readonly record struct SkySun(float Turbidity, Vector3 Direction, Vector3 DiscRadiance, float DiscCosine);

/// <summary>
/// The view's physical sky and its sun. The sky itself is drawn on the GPU from the tables in
/// <see cref="Vulkan.SkyAtmosphere"/>; this picks the sun and works out its disc and the colour the air gives it.
/// </summary>
internal static class SkyModel
{
    /// <summary>
    /// The brightest a sun disc is drawn. Render targets hold half floats, which end at 65504,
    /// and bloom adds to the disc, so it stays well under.
    /// </summary>
    public const float MaxDiscRadiance = 30000f;

    /// <summary>The clearest air the sky is drawn with, as the scene's settings allow it.</summary>
    public const float MinTurbidity = 1.7f;

    /// <summary>The haziest air the sky is drawn with.</summary>
    public const float MaxTurbidity = 10f;

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

        float turbidity = Turbidity(view);
        if (view.Lights.IsEmpty)
        {
            return new SkySun(turbidity, Vector3.UnitY, Vector3.Zero, 1f);
        }

        ref readonly var light = ref view.Lights[SunIndex(view.Lights)];
        float radius = view.Shadows.LightAngle * 0.5f;
        // 2 pi (1 - cos r), written with the half-angle sine so a small sun keeps its precision.
        float halfSine = MathF.Sin(radius * 0.5f);
        float solidAngle = 4f * MathF.PI * halfSine * halfSine;
        var radiance = solidAngle > 0f
            ? Vector3.Min(light.Radiance / solidAngle, new Vector3(MaxDiscRadiance))
            : Vector3.Zero;
        return new SkySun(turbidity, Vector3.Normalize(light.Direction), radiance, MathF.Cos(radius));
    }

    /// <summary>
    /// The view with its sun coloured by the air it comes through, after <see cref="Atmosphere.SunTint"/>: as set
    /// with the sun 60 degrees up, warmer and dimmer near the horizon, and gone once it has set. Every reader of the
    /// lights, the disc included, sees the tinted sun. A gradient sky, or a view without lights, comes back as it is;
    /// otherwise the lights are copied into <paramref name="buffer"/>, grown as needed, which the returned view reads.
    /// </summary>
    public static FrameView Tinted(in FrameView view, ref DirectionalLight[] buffer)
    {
        if (!(view.Sky.Turbidity > 0f) || view.Lights.IsEmpty)
        {
            return view;
        }

        if (buffer.Length < view.Lights.Length)
        {
            buffer = new DirectionalLight[view.Lights.Length];
        }

        var lights = buffer.AsSpan(0, view.Lights.Length);
        view.Lights.CopyTo(lights);
        ref var sun = ref lights[SunIndex(lights)];
        var direction = Vector3.Normalize(sun.Direction);
        sun.Radiance *= Atmosphere.SunTint(direction, Turbidity(view), view.Shadows.LightAngle * 0.5f);
        return view with { Lights = lights };
    }

    /// <summary>
    /// Whether the physical sky's air fades far surfaces: not with Graphics.AerialPerspective off, not in a probe's
    /// capture, which leaves out the haze too, and not in a view with a haze of its own, which takes over even while
    /// Graphics.Haze hides it.
    /// </summary>
    public static bool AirShows(in FrameView view, RenderEffects off, bool capturingProbe) =>
        !off.HasFlag(RenderEffects.AerialPerspective) && !capturingProbe
        && !(view.BackgroundFadeEnd > view.BackgroundFadeStart);

    private static float Turbidity(in FrameView view) => Math.Clamp(view.Sky.Turbidity, MinTurbidity, MaxTurbidity);

    /// <summary>The light that casts the shadows, among those the frame takes, or else the first.</summary>
    private static int SunIndex(ReadOnlySpan<DirectionalLight> lights)
    {
        for (int i = 0; i < Math.Min(lights.Length, FrameUniforms.MaxLights); i++)
        {
            if (lights[i].CastsShadow)
            {
                return i;
            }
        }

        return 0;
    }
}
