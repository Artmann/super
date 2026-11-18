using System.Numerics;

namespace Rendition.Passes;

/// <summary>What the environment bake and the background need to draw a clear sky and its sun.</summary>
/// <param name="Turbidity">How hazy the air is; 0 for the environment's bands, when the rest is zero too.</param>
/// <param name="Direction">Towards the sun.</param>
/// <param name="DiscRadiance">How bright the sun's disc is, so that over the disc it adds up to the light's colour.</param>
/// <param name="DiscCosine">The cosine of the disc's angular radius.</param>
/// <param name="Image">The photographed sky drawn in place of the others, or none.</param>
/// <param name="Rotation">How far <paramref name="Image"/> is turned about the up axis, in radians.</param>
/// <param name="Scale">What <paramref name="Image"/> is multiplied by: two to the power of its exposure.</param>
internal readonly record struct SkySun(
    float Turbidity, Vector3 Direction, Vector3 DiscRadiance, float DiscCosine,
    SkyImageHandle? Image = null, float Rotation = 0f, float Scale = 0f);

/// <summary>The sun a cooked sky image holds, as it was photographed; see <see cref="SkyImageData"/>.</summary>
internal readonly record struct SkyImageSun(Vector3 Direction, Vector3 Radiance, float Cosine);

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

    /// <summary>
    /// How bright the moon's lit face is, in the sky's units, a third of a noon sky's zenith: a pale disc by day and,
    /// with the camera opened up for the night, a bright one.
    /// </summary>
    public static readonly Vector3 MoonRadiance = new(0.2f, 0.2f, 0.19f);

    /// <summary>The moon's angular radius in radians, twice the real moon's so it reads on screen. Must match MOON_RADIUS in sky.glsl.</summary>
    public const float MoonAngularRadius = 0.009f;

    /// <summary>How bright the brightest stars are, in the sky's units, when the sky is fully dark.</summary>
    public const float StarBrightness = 0.4f;

    /// <summary>How far below the horizon the sun is, in radians, when the first stars show: two degrees.</summary>
    public const float StarsAppear = 0.035f;

    /// <summary>How far below the horizon the sun is when every star shows: twelve degrees, the end of nautical twilight.</summary>
    public const float StarsFull = 0.21f;

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
        if (!Physical(view))
        {
            return default;
        }

        float turbidity = Turbidity(view);
        if (view.Lights.IsEmpty)
        {
            return new SkySun(turbidity, Vector3.UnitY, Vector3.Zero, 1f);
        }

        ref readonly var light = ref view.Lights[SunIndex(view.Lights, view.Sky.Moon)];
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
        if (!Physical(view) || view.Lights.IsEmpty)
        {
            return view;
        }

        if (buffer.Length < view.Lights.Length)
        {
            buffer = new DirectionalLight[view.Lights.Length];
        }

        var lights = buffer.AsSpan(0, view.Lights.Length);
        view.Lights.CopyTo(lights);
        ref var sun = ref lights[SunIndex(lights, view.Sky.Moon)];
        var direction = Vector3.Normalize(sun.Direction);
        sun.Radiance *= Atmosphere.SunTint(direction, Turbidity(view), view.Shadows.LightAngle * 0.5f);
        return view with { Lights = lights };
    }

    /// <summary>
    /// Whether the physical sky's air fades far surfaces: not with Graphics.Atmosphere.AerialPerspective off, and not in a
    /// view with a haze of its own, which takes over even while Graphics.Atmosphere.Haze hides it. Reflection probe
    /// captures never draw it.
    /// </summary>
    public static bool AirShows(in FrameView view, RenderEffects off) =>
        !off.HasFlag(RenderEffects.AerialPerspective) && !(view.BackgroundFadeEnd > view.BackgroundFadeStart);

    private static float Turbidity(in FrameView view) => Math.Clamp(view.Sky.Turbidity, MinTurbidity, MaxTurbidity);

    /// <summary>Whether the view draws the physical sky: a turbidity is set and no photographed sky takes over.</summary>
    private static bool Physical(in FrameView view) => view.Sky.Turbidity > 0f && view.Sky.Image is null;

    /// <summary>
    /// A photographed sky and its sun, turned by <paramref name="rotation"/> about the up axis and brightened by
    /// <paramref name="exposure"/> stops. A sky without a sun has no disc. The disc is held to
    /// <see cref="MaxDiscRadiance"/>, as a physical sky's is.
    /// </summary>
    public static SkySun Photographed(SkyImageHandle image, in SkyImageSun sun, float rotation, float exposure)
    {
        float scale = MathF.Pow(2f, exposure);
        var direction = Vector3.Transform(sun.Direction, Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotation));
        var radiance = Vector3.Min(sun.Radiance * scale, new Vector3(MaxDiscRadiance));
        return new SkySun(0f, direction, radiance, sun.Cosine, image, rotation, scale);
    }

    /// <summary>
    /// The light that casts the shadows, among those the frame takes, or else the first, leaving out the moon's: a light
    /// aimed at <paramref name="moon"/>. With only the moon's light the sky follows that, having nothing else.
    /// </summary>
    private static int SunIndex(ReadOnlySpan<DirectionalLight> lights, Vector3 moon)
    {
        int count = Math.Min(lights.Length, FrameUniforms.MaxLights);
        int first = -1;
        for (int i = 0; i < count; i++)
        {
            if (IsMoon(lights[i].Direction, moon))
            {
                continue;
            }

            if (lights[i].CastsShadow)
            {
                return i;
            }

            if (first < 0)
            {
                first = i;
            }
        }

        return Math.Max(first, 0);
    }

    // The moon's light points where the moon is, give or take what normalising loses.
    private static bool IsMoon(Vector3 direction, Vector3 moon) =>
        moon != Vector3.Zero && direction != Vector3.Zero
        && Vector3.Dot(Vector3.Normalize(direction), Vector3.Normalize(moon)) > 0.99999f;

    /// <summary>
    /// The moon and the stars for the frame's uniforms: the moon's direction, w 1 when it shows; its lit face's
    /// radiance, dimmed by the air as the sun is, so it reddens low and sinks behind the horizon; and the stars, x how
    /// bright and y how far they have turned. A gradient sky has neither. The stars fade in as the sun goes from
    /// <see cref="StarsAppear"/> to <see cref="StarsFull"/> below the horizon, and are left out with
    /// <see cref="RenderEffects.Stars"/> off and with <see cref="AnalyticSky.HideStars"/>. Reflection probe captures fill
    /// their sky without the discs or the stars.
    /// </summary>
    public static (Vector4 Moon, Vector4 MoonRadiance, Vector4 Stars) Night(in FrameView view, Vector3 sun, RenderEffects off)
    {
        if (!Physical(view))
        {
            return default;
        }

        float turbidity = Turbidity(view);
        var moon = Vector4.Zero;
        var moonRadiance = Vector4.Zero;
        if (view.Sky.Moon != Vector3.Zero)
        {
            var towards = Vector3.Normalize(view.Sky.Moon);
            moon = new Vector4(towards, 1f);
            moonRadiance = new Vector4(MoonRadiance * Atmosphere.SunTint(towards, turbidity, MoonAngularRadius), 0f);
        }

        float dark = 1f - Atmosphere.SmoothStep(-StarsFull, -StarsAppear, MathF.Asin(Math.Clamp(sun.Y, -1f, 1f)));
        bool stars = !view.Sky.HideStars && !off.HasFlag(RenderEffects.Stars);
        var starry = new Vector4(stars ? StarBrightness * dark : 0f, view.Sky.StarRotation, 0f, 0f);
        return (moon, moonRadiance, starry);
    }
}
