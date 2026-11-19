using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The frame's sky, as a component, for <see cref="ViewSurroundings.Sky"/>. Put it on any entity, such as one made
/// for the level; when several have one, the newest wins, and taking it away falls back to the one before.
/// </summary>
public readonly record struct Sky(SkySettings Settings);

/// <summary>
/// How far and how softly the frame's shadows reach, as a component, for <see cref="ViewLights.Shadows"/>, which says
/// what each value does and the limits it checks. The newest entity with one wins.
/// </summary>
public readonly record struct Shadows(
    float Radius,
    int Cascades = 1,
    float SunSize = ViewLights.DefaultSunSize,
    float Contact = ShadowSettings.DefaultContactLength);

/// <summary>
/// How nearby surfaces darken the sky's light, as a component, for <see cref="ViewIndirect.AmbientOcclusion"/>. The
/// newest entity with one wins.
/// </summary>
public readonly record struct AmbientOcclusion(
    float Radius = OcclusionSettings.DefaultRadius,
    float Strength = OcclusionSettings.DefaultStrength);

/// <summary>The look of the finished picture, as a component, for <see cref="ViewEffects.Finish"/>. The newest entity with one wins.</summary>
public readonly record struct Finish(PostSettings Settings);

/// <summary>
/// The ink lines round outlined surfaces, as a component, for <see cref="ViewEffects.Outlines"/>: black when the color
/// is null. The newest entity with one wins.
/// </summary>
public readonly record struct Outlines(
    Vector3? Color = null,
    float Thickness = OutlineSettings.DefaultThickness,
    float FadeStart = OutlineSettings.DefaultFadeStart,
    float FadeEnd = OutlineSettings.DefaultFadeEnd);
