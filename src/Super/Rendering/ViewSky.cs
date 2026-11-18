using System.Numerics;
using Rendition;

namespace Super;

/// <summary>What a view's sky and water ask of a frame: the colours around the scene, the clear sky, the haze and the fog.</summary>
internal static class ViewSky
{
    /// <summary>The grey room a world without a sky sits in.</summary>
    internal static readonly SkyBands Studio = new(new Vector3(0.45f, 0.5f, 0.6f), new Vector3(0.5f, 0.5f, 0.52f), new Vector3(0.25f, 0.24f, 0.23f));

    /// <summary>
    /// The bands baked into the environment. Water wins over the sky, which wins over the studio.
    /// A clear sky keeps its three colours here, and the renderer uses only the ground below its horizon.
    /// </summary>
    internal static SkyBands SurroundingColors(View view, RenderEffects off)
    {
        var water = view.WaterSettings;
        if (Submerged(view, off))
        {
            return new SkyBands(water.ShallowColor, Vector3.Lerp(water.ShallowColor, water.DeepColor, 0.5f), water.DeepColor);
        }

        if (view.SkySettings is { } sky)
        {
            return new SkyBands(sky.Zenith, sky.Horizon, sky.Ground);
        }

        return Studio;
    }

    /// <summary>
    /// The view's clear sky, or none under water, which takes over the sky. <paramref name="image"/> gives the
    /// renderer's handle for a photographed sky.
    /// </summary>
    internal static AnalyticSky FrameSky(View view, RenderEffects off, Func<SkyImage, SkyImageHandle>? image = null) =>
        !Submerged(view, off) && view.SkySettings is { } sky
            ? new AnalyticSky
            {
                Turbidity = sky.Turbidity,
                Moon = sky.Moon,
                HideStars = sky.HideStars,
                StarRotation = sky.StarRotation,
                Image = sky.Image is { } photographed && image is not null ? image(photographed) : null,
                Rotation = sky.Rotation,
                Exposure = sky.Exposure,
            }
            : default;

    /// <summary>Where the haze is measured from: the camera's focus when the sky asks for it, else the camera.</summary>
    internal static Vector3? HazeOrigin(SkySettings? sky, Camera shot) => sky is { HazeFromFocus: true } ? shot.Target : null;

    /// <summary>
    /// The fog the sky asks for, or clear air where there's no sky or its fog was never set. Clear air is
    /// <c>new FogSettings()</c>, not <c>default</c>: the struct's default skips its initializers, so its volumetric
    /// distance would be 0 and the fog volumes, which need the volume, would never draw.
    /// </summary>
    internal static FogSettings FrameFog(SkySettings? sky) =>
        sky is { Fog: var fog } && fog != default ? fog : new FogSettings();

    /// <summary>Whether the view is under its water, which it never is while <see cref="RenderEffects.Underwater"/> is off.</summary>
    private static bool Submerged(View view, RenderEffects off) =>
        view.WaterSettings.Absorption != Vector3.Zero && !off.HasFlag(RenderEffects.Underwater);
}

/// <summary>The three colours baked into the environment: above, at the horizon and below.</summary>
internal readonly record struct SkyBands(Vector3 Ceiling, Vector3 Horizon, Vector3 Floor);
