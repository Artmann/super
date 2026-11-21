using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// What the clouds' shaders read from the frame block, worked out on the CPU: how they're drawn, the light on them,
/// where the camera stands in their noise as the wind carries it, and the shadow map's place round the camera. See
/// "Clouds" in docs/design.md.
/// </summary>
internal static class CloudModel
{
    /// <summary>How many metres one tile of the shape noise covers. Must match CLOUD_SHAPE_PERIOD in cloud_noise.glsl.</summary>
    public const float ShapePeriod = 6000f;

    /// <summary>The same for the detail noise. Must match CLOUD_DETAIL_PERIOD.</summary>
    public const float DetailPeriod = 500f;

    /// <summary>The same for the weather map. Must match CLOUD_WEATHER_PERIOD.</summary>
    public const float WeatherPeriod = 40960f;

    /// <summary>The cirrus reads the weather map four times finer.</summary>
    public const float CirrusPeriod = WeatherPeriod / 4f;

    /// <summary>The shadow map's texels across and down.</summary>
    public const int ShadowResolution = 512;

    /// <summary>How far the shadow map reaches from the camera each way, in metres.</summary>
    public const float ShadowHalfWidth = 5000f;

    /// <summary>The march's steps across the layer straight up; rays across it take up to twice as many.</summary>
    public const int Steps = 64;

    /// <summary>How far the tops of the clouds lean along the wind, in metres.</summary>
    public const float Lean = 400f;

    /// <summary>How fast the detail noise moves of its own, in metres a second, so the edges churn even without wind.</summary>
    public static readonly Vector2 Churn = new(1.5f, 0.7f);

    /// <summary>
    /// The air between the camera and the clouds thins with height, so it hazes them less than the same distance
    /// along the ground.
    /// </summary>
    public const float AirThinning = 0.6f;

    /// <summary>
    /// The sun lights the clouds until it is this far below the horizon, in the sine of its height; below it the moon
    /// takes over.
    /// </summary>
    public const float SunSets = -0.05f;

    /// <summary>
    /// How the view draws its clouds: 0 not at all, 1 as a flat layer, 2 marched. Only a physical sky has clouds, and
    /// only with some to draw and <see cref="RenderEffects.Clouds"/> on.
    /// </summary>
    public static int Tier(in FrameView view, RenderEffects off)
    {
        if (!SkyModel.Physical(view) || view.Weather.Clouds is not { Any: true } || off.HasFlag(RenderEffects.Clouds))
        {
            return 0;
        }

        return off.HasFlag(RenderEffects.VolumetricClouds) ? 1 : 2;
    }

    /// <summary>
    /// The light on the clouds: the sun while it is up, then the moon's light, a light aimed at the moon, if the view
    /// has one. Returns its slot among the frame's lights, or -1 for none, when the clouds see only the sky.
    /// </summary>
    public static int KeyLight(in FrameView view, out Vector3 towards, out Vector3 radiance)
    {
        var lights = view.Lights;
        var moon = view.Environment.Sky.Moon;
        int count = Math.Min(lights.Length, FrameUniforms.MaxLights);
        towards = Vector3.UnitY;
        radiance = Vector3.Zero;
        if (count == 0)
        {
            return -1;
        }

        int sun = SkyModel.SunIndex(lights, moon);
        var sunDirection = Vector3.Normalize(lights[sun].Direction);
        if (sunDirection.Y > SunSets || moon == Vector3.Zero)
        {
            towards = sunDirection;
            radiance = lights[sun].Radiance;
            return sun;
        }

        for (int i = 0; i < count; i++)
        {
            if (SkyModel.IsMoon(lights[i].Direction, moon))
            {
                towards = Vector3.Normalize(lights[i].Direction);
                radiance = lights[i].Radiance;
                return i;
            }
        }

        towards = Vector3.Normalize(moon);
        return -1;
    }

    /// <summary>
    /// The frame block's clouds. <paramref name="previous"/> is last frame's view and projection, unshifted;
    /// <paramref name="frame"/> which pixel of each 2 by 2 the march takes and whether last frame's clouds can be
    /// reprojected; <paramref name="airExtinction"/> the physical sky's air at the ground, per metre, or zero without it.
    /// </summary>
    public static FrameCloudBlock Pack(
        in FrameView view, RenderEffects off, in Matrix4x4 previous, (int Subpixel, bool History) frame, Vector3 airExtinction)
    {
        var block = new FrameCloudBlock { CloudShadow = new Vector4(0f, 0f, ShadowHalfWidth, -1f) };
        int tier = Tier(view, off);
        if (tier == 0)
        {
            return block;
        }

        var clouds = view.Weather.Clouds!.Value;
        var camera = view.CameraPosition;
        var at = new Vector2(camera.X, camera.Z);
        double time = view.Time;

        var relative = Matrix4x4.CreateTranslation(camera) * view.View * view.Projection;
        Matrix4x4.Invert(relative, out block.CloudRays);
        block.CloudReprojection = Matrix4x4.CreateTranslation(camera) * previous;

        block.CloudShape = new Vector4(Math.Clamp(clouds.Coverage, 0f, 1f), clouds.Thickening, clouds.BaseHeight, clouds.Depth);
        var drift = clouds.Wind;
        block.CloudWeather = Pair(Drifted(at, drift, time, WeatherPeriod), Drifted(at, drift * 2f, time, CirrusPeriod));
        block.CloudNoise = Pair(Drifted(at, drift, time, ShapePeriod), Drifted(at, drift + Churn, time, DetailPeriod));

        int light = KeyLight(view, out var towards, out var radiance);
        bool shadows = light >= 0 && !off.HasFlag(RenderEffects.CloudShadows) && clouds.Coverage > 0f;
        float texel = 2f * ShadowHalfWidth / ShadowResolution;
        var centre = new Vector2(MathF.Round(at.X / texel), MathF.Round(at.Y / texel)) * texel;
        block.CloudShadow = new Vector4(centre - at, ShadowHalfWidth, shadows ? light : -1f);

        block.CloudLight = new Vector4(towards, tier);
        block.CloudRadiance = new Vector4(radiance, Math.Clamp(clouds.Cirrus, 0f, 1f));
        block.CloudFrame = new Vector4(frame.Subpixel & 3, frame.History ? 1f : 0f, Steps, 0f);
        block.CloudAir = new Vector4(airExtinction * AirThinning, 0f);
        var lean = drift.LengthSquared() > 0f ? Vector2.Normalize(drift) * Lean : Vector2.Zero;
        block.CloudLean = new Vector4(lean, 0f, 0f);
        return block;
    }

    /// <summary>
    /// How far the wind carries the clouds, in metres, before the captures move them along: at a fair breeze, about
    /// every half minute.
    /// </summary>
    public const float CaptureStep = 250f;

    /// <summary>
    /// The clouds the environment's captures draw, so the sky light and the reflections hold them and an overcast sky
    /// greys the light: none unless the view draws clouds at all. The light is divided by
    /// <paramref name="intensity"/>, the environment's, since the captures are drawn before it scales them, and its
    /// slot among the lights doesn't matter here. <paramref name="airExtinction"/> is the physical sky's air at the
    /// ground, per metre, or zero without it.
    /// </summary>
    public static SkyClouds Capture(in FrameView view, RenderEffects off, float intensity, Vector3 airExtinction)
    {
        if (Tier(view, off) == 0)
        {
            return default;
        }

        var clouds = view.Weather.Clouds!.Value;
        var at = new Vector2(view.CameraPosition.X, view.CameraPosition.Z);
        var drift = clouds.Wind;
        var weather = Snapped(Drifted(at, drift, view.Time, WeatherPeriod));
        var cirrus = Snapped(Drifted(at, drift * 2f, view.Time, CirrusPeriod));
        KeyLight(view, out var towards, out var radiance);
        return new SkyClouds(
            new Vector4(Math.Clamp(clouds.Coverage, 0f, 1f), clouds.Thickening, clouds.BaseHeight, clouds.Depth),
            new Vector4(towards, 0f),
            new Vector4(radiance / Math.Max(intensity, 1e-3f), Math.Clamp(clouds.Cirrus, 0f, 1f)),
            Pair(weather, cirrus),
            new Vector4(airExtinction * AirThinning, 0f));

        static Vector2 Snapped(Vector2 point) =>
            new(MathF.Round(point.X / CaptureStep) * CaptureStep, MathF.Round(point.Y / CaptureStep) * CaptureStep);
    }

    /// <summary>The view's clear sky with the clouds the captures draw, hazed by the air as the frame hazes the clouds on screen.</summary>
    public static SkySun CapturedSky(in FrameView view, RenderEffects off)
    {
        var sun = SkyModel.Sun(view);
        var air = Atmosphere.Air(sun.Turbidity, sun.Direction, view.Environment.Intensity, SkyModel.AirShows(view, off)).Extinction;
        return sun with { Clouds = Capture(view, off, view.Environment.Intensity, new Vector3(air.X, air.Y, air.Z)) };
    }

    /// <summary>
    /// A hash of the clouds a physical sky has, whether or not they're drawn, so switching them off doesn't make a bake
    /// stale; zero without any.
    /// </summary>
    public static ulong Hash(in FrameView view) =>
        SkyModel.Physical(view) && view.Weather.Clouds is { Any: true } clouds
            ? StableHasher.ForThread().Add(clouds.Coverage).Add(clouds.Thickening).Add(clouds.BaseHeight)
                .Add(clouds.Depth).Add(clouds.Cirrus).Finish()
            : 0UL;

    /// <summary>
    /// Where a point at <paramref name="at"/> stands in a texture of this period, in metres, once the wind has carried
    /// the clouds this far: the texture moves with the wind, so the point moves against it. Worked out in doubles and
    /// wrapped, so hours of drift keep their precision.
    /// </summary>
    public static Vector2 Drifted(Vector2 at, Vector2 wind, double time, float period) =>
        new(Wrap(at.X - wind.X * time, period), Wrap(at.Y - wind.Y * time, period));

    private static Vector4 Pair(Vector2 first, Vector2 second) => new(first.X, first.Y, second.X, second.Y);

    private static float Wrap(double value, double period) => (float)(value - Math.Floor(value / period) * period);
}
