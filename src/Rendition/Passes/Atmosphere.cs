using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The physical sky's air, on the CPU: what Shaders/atmosphere.glsl draws the sky from, for the parts of a frame the
/// CPU sets up. The sun's light dims and warms as it sinks through more air, the floor under the sky is lit by what is
/// left, and far surfaces fade into the air. Distances are kilometres and coefficients per kilometre, as in the shader,
/// whose constants must match these.
/// </summary>
internal static class Atmosphere
{
    public const float PlanetRadius = 6360f;
    public const float AtmosphereRadius = 6460f;

    /// <summary>Where the sky is seen from, as VIEW_RADIUS: just above the ground.</summary>
    public const float ViewHeight = 0.05f;

    /// <summary>The sun's light at the top of the air in the renderer's units, as SOLAR_STRENGTH.</summary>
    public const float SolarStrength = 13f;

    private const float RayleighHeight = 8f;
    private const float MieHeight = 1.2f;
    private const float OzoneCentre = 25f;
    private const float OzoneHalfWidth = 15f;
    private const int Steps = 256;

    private static readonly Vector3 RayleighScattering = new(5.802e-3f, 13.558e-3f, 33.1e-3f);
    private static readonly Vector3 OzoneAbsorption = new(0.650e-3f, 1.881e-3f, 0.085e-3f);

    /// <summary>The cosine from straight up of the sun the sun's tint is measured against: 60 degrees up.</summary>
    private static readonly float ReferenceCosine = MathF.Sin(MathF.PI / 3f);

    /// <summary>
    /// The aerosol at the ground for a turbidity, per kilometre. Ångström's turbidity coefficient comes from the
    /// turbidity, as Preetham relates them; the optical depth at 550 nm from that; and the aerosol, falling off over
    /// 1.2 km, is spread so straight up it has that depth. Nine tenths of it scatters and the rest absorbs.
    /// </summary>
    public static (float Scattering, float Extinction) Aerosol(float turbidity)
    {
        float beta = 0.04608f * turbidity - 0.04586f;
        float depth = beta * MathF.Pow(0.55f, -1.3f);
        float extinction = Math.Max(depth, 0f) / MieHeight;
        return (0.9f * extinction, extinction);
    }

    /// <summary>
    /// The share of each colour of light that crosses the air from <paramref name="height"/> kilometres up towards
    /// <paramref name="cosine"/> from straight up, 0 where the planet is in the way.
    /// </summary>
    public static Vector3 Transmittance(float height, float cosine, float turbidity)
    {
        float r = PlanetRadius + height;
        float mu = Math.Clamp(cosine, -1f, 1f);
        if (mu < 0f && r * r * (mu * mu - 1f) + PlanetRadius * PlanetRadius >= 0f)
        {
            return Vector3.Zero;
        }

        float distance = -r * mu + MathF.Sqrt(Math.Max(r * r * (mu * mu - 1f) + AtmosphereRadius * AtmosphereRadius, 0f));
        float mie = Aerosol(turbidity).Extinction;
        float step = distance / Steps;
        var depth = Vector3.Zero;
        for (int i = 0; i < Steps; i++)
        {
            float t = (i + 0.5f) * step;
            float above = MathF.Sqrt(r * r + t * t + 2f * r * t * mu) - PlanetRadius;
            float ozone = Math.Max(0f, 1f - MathF.Abs(above - OzoneCentre) / OzoneHalfWidth);
            depth += (RayleighScattering * MathF.Exp(-above / RayleighHeight)
                + new Vector3(mie * MathF.Exp(-above / MieHeight))
                + OzoneAbsorption * ozone) * step;
        }

        return new Vector3(MathF.Exp(-depth.X), MathF.Exp(-depth.Y), MathF.Exp(-depth.Z));
    }

    /// <summary>
    /// What the sun's light is multiplied by for the air it crosses: 1 with the sun 60 degrees up, a little more
    /// above, warmer and dimmer below, and black once it has sunk behind the horizon. The last of it fades over the
    /// sun's own radius, <paramref name="discRadius"/> radians, so the light doesn't snap off.
    /// </summary>
    public static Vector3 SunTint(Vector3 towardsSun, float turbidity, float discRadius)
    {
        var direction = Vector3.Normalize(towardsSun);
        var here = Transmittance(ViewHeight, direction.Y, turbidity);
        var reference = Transmittance(ViewHeight, ReferenceCosine, turbidity);

        // The horizon dips a little below level from just above the ground.
        float dip = MathF.Acos(PlanetRadius / (PlanetRadius + ViewHeight));
        float elevation = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        float shown = SmoothStep(-dip - discRadius, -dip + discRadius, elevation);
        if (shown <= 0f)
        {
            return Vector3.Zero;
        }

        // Just past the horizon the table gives 0; the part of the disc still above it keeps the last colour.
        if (here == Vector3.Zero)
        {
            here = Transmittance(ViewHeight, MathF.Sin(-dip) + 1e-4f, turbidity);
        }

        return here / reference * shown;
    }

    /// <summary>
    /// How much of its colour the floor under the physical sky shows, 1 with the sun 60 degrees up: the sun's light
    /// across it, dimmed by the air, and a quarter as much again from the sky, which fades out after sunset. At night
    /// a little is left, so the ground never goes fully black.
    /// </summary>
    public static float GroundLight(Vector3 towardsSun, float turbidity)
    {
        float Lit(float cosine)
        {
            var direct = Transmittance(ViewHeight, cosine, turbidity);
            float luminance = Vector3.Dot(direct, new Vector3(0.2126f, 0.7152f, 0.0722f));
            return luminance * Math.Max(cosine, 0f) + 0.25f * SmoothStep(-0.1f, 0.2f, cosine);
        }

        float up = Vector3.Normalize(towardsSun).Y;
        return Math.Max(Lit(up) / Lit(ReferenceCosine), 0.002f);
    }

    /// <summary>
    /// The frame's AirExtinction and AirSun for Shaders/sky.glsl's throughAir: the air at the ground, per metre, and
    /// the sunlight reaching it in the sky's units times the environment's intensity. Off, the sun's w is 0 and the
    /// shaders leave surfaces as they are.
    /// </summary>
    public static (Vector4 Extinction, Vector4 Sun) Air(float turbidity, Vector3 towardsSun, float environmentIntensity, bool on)
    {
        if (!on || !(turbidity > 0f))
        {
            return (Vector4.Zero, Vector4.Zero);
        }

        var (scattering, extinction) = Aerosol(turbidity);
        var perMetre = (RayleighScattering + new Vector3(extinction)) / 1000f;
        var sunlight = SolarStrength * environmentIntensity
            * Transmittance(ViewHeight, Vector3.Normalize(towardsSun).Y, turbidity);
        return (new Vector4(perMetre, scattering / 1000f), new Vector4(sunlight, 1f));
    }

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
