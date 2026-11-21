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

/// <summary>
/// How much light surfaces on screen pass on to the surfaces near them, as a component, for
/// <see cref="ViewIndirect.ScreenBounce"/>. The newest entity with one wins.
/// </summary>
public readonly record struct ScreenBounce(
    float Strength = BounceSettings.DefaultStrength,
    float Radius = BounceSettings.DefaultRadius);

/// <summary>
/// A box of light probes of the game's own, as a component, for <see cref="ViewIndirect.LightProbes"/>, which says what
/// each value does and the limits it checks. The newest entity with one wins.
/// </summary>
public readonly record struct LightProbes(Vector3 Min, Vector3 Max, float Spacing);

/// <summary>
/// The one place whose lighting is baked, as a component, for <see cref="ViewIndirect.BakedLighting"/>, which says
/// what each value does and the limits it checks. The newest entity with one wins.
/// </summary>
public readonly record struct BakedLighting(
    string Name,
    Vector3 Min,
    Vector3 Max,
    float ProbeSpacing,
    int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre);

/// <summary>Puts the whole frame underwater, as a component, for <see cref="ViewSurroundings.Water"/>. The newest entity with one wins.</summary>
public readonly record struct Underwater(UnderwaterSettings Settings);

/// <summary>
/// Water standing at a height on the materials that ask for puddles, as a component, for
/// <see cref="ViewSurroundings.Puddles"/>. The newest entity with one wins.
/// </summary>
public readonly record struct Puddles(float Level, float Band = PuddleSettings.DefaultBand);

/// <summary>
/// A flat, level plane at <see cref="Height"/> metres that mirrors the scene, as a component, for
/// <see cref="ViewSurroundings.ReflectionPlane"/>. The newest entity with one wins.
/// </summary>
public readonly record struct MirrorPlane(float Height);

/// <summary>
/// A breeze that sways the materials that ask for wind, as a component, for <see cref="ViewSurroundings.Wind"/>, which
/// says what each value does and the limits it checks. Write it in <c>Update</c> for a wind that changes. The newest
/// entity with one wins.
/// </summary>
public readonly record struct Wind(Vector3 Direction, float Strength, float Gustiness = WindSettings.DefaultGustiness);
