using System.Numerics;

namespace Rendition;

/// <summary>
/// A level plane at <see cref="Height"/> metres that mirrors the scene, for puddles, a pond or a polished floor. Smooth
/// surfaces lying in it show a true reflection of what stands over them, sharp where it touches and blurring with
/// height and roughness, in place of the screen-space reflections and the reflection probes. See "Planar reflections"
/// in docs/rendering.md.
/// </summary>
public readonly record struct ReflectionPlane(float Height);

/// <summary>
/// Standing water on surfaces whose material has <see cref="MaterialFlags.Puddles"/>. <see cref="Level"/> is the
/// height of the water in metres; whatever lies below it is under water. <see cref="Band"/> is how far above the
/// water, in metres, the surface is damp, darkest at the waterline and dry at the band's top.
/// </summary>
public readonly record struct PuddleSettings(float Level, float Band = PuddleSettings.DefaultBand)
{
    /// <summary>The damp band's height when none is given, in metres.</summary>
    public const float DefaultBand = 0.08f;
}

/// <summary>
/// Wind that sways materials with <see cref="Material.Wind"/>. <see cref="Direction"/> is the way it blows, flattened
/// onto the ground; <see cref="Strength"/> is how far, in metres, it leans the tip of grass 1 m tall at a gust's peak;
/// <see cref="Gustiness"/> runs from 0 for a steady wind to 1 for one that drops to a fifth of that between gusts.
/// </summary>
public readonly record struct WindSettings(Vector3 Direction, float Strength, float Gustiness = WindSettings.DefaultGustiness)
{
    /// <summary>How gusty the wind is when not said: between gusts it leans things 60 % as far as at their peak.</summary>
    public const float DefaultGustiness = 0.5f;
}

/// <summary>Puts the camera underwater. Leaving Absorption at zero means there's no water.</summary>
public struct UnderwaterSettings
{
    /// <summary>Red, green and blue light lost per metre. Red should be the largest, as in the sea.</summary>
    public Vector3 Absorption;

    /// <summary>The water's colour looking up towards the surface.</summary>
    public Vector3 ShallowColor;

    /// <summary>The water's colour looking down.</summary>
    public Vector3 DeepColor;

    /// <summary>Height of the water's surface.</summary>
    public float SurfaceHeight;

    /// <summary>How much the moving net of light brightens sunlit surfaces. 1 doubles them at its peaks.</summary>
    public float CausticStrength;

    /// <summary>How many nets of light fit in a metre. 0.2 makes each about five metres wide.</summary>
    public float CausticScale;

    /// <summary>Brightness of the hazy shafts of light.</summary>
    public float ShaftStrength;

    /// <summary>
    /// How far from the camera, in metres, the water is drawn in the volumetric fog with its shafts and any fog
    /// volumes. Past it the water's colour fills in. Shafts need <c>Graphics.VolumetricFog</c>.
    /// </summary>
    public float ShaftDistance;
}

/// <summary>The part of the world covered by the shadow maps.</summary>
public struct ShadowSettings
{
    public Vector3 Center;

    /// <summary>How far from the centre the sharpest map reaches, in metres.</summary>
    public float Radius;

    /// <summary>
    /// How many maps to draw, 1 to 4, each covering three times the radius of the one
    /// before. More cover more ground at the same sharpness near the centre, for the
    /// cost of drawing the scene again. 0 counts as 1.
    /// </summary>
    public int Cascades;

    /// <summary>
    /// Apparent size of the light: 0 gives hard shadows, larger values blur them
    /// faster with distance from the object casting them.
    /// </summary>
    public float LightAngle;

    /// <summary>
    /// How far, in metres, each pixel looks along the light through the depth buffer for
    /// something touching it, which darkens the last few centimetres the shadow maps are
    /// too coarse to see. Only inside the first cascade's radius. 0 turns it off.
    /// </summary>
    public float ContactLength = DefaultContactLength;

    public const float DefaultContactLength = 0.25f;

    public ShadowSettings()
    {
    }
}

/// <summary>
/// How nearby surfaces hide the soft light from the sky. <see cref="Radius"/> is how far, in
/// metres, a point looks for them; <see cref="Strength"/>, from 0 to 1, is how dark they make it.
/// </summary>
public readonly record struct OcclusionSettings(float Radius = OcclusionSettings.DefaultRadius, float Strength = OcclusionSettings.DefaultStrength)
{
    public const float DefaultRadius = 0.6f;
    public const float DefaultStrength = 0.9f;

    /// <summary>The defaults, which suit rooms and streets seen from head height.</summary>
    public OcclusionSettings() : this(DefaultRadius, DefaultStrength)
    {
    }
}

/// <summary>
/// Light bounced from the surfaces on screen near a point. <see cref="Strength"/>, from 0 to 2, scales it, and 1 is
/// the true amount; <see cref="Radius"/> is how far, in metres, a point looks for surfaces that light it.
/// </summary>
public readonly record struct BounceSettings(float Strength = BounceSettings.DefaultStrength, float Radius = BounceSettings.DefaultRadius)
{
    public const float DefaultStrength = 1f;
    public const float DefaultRadius = 2f;
    public const float MostStrength = 2f;
    public const float MostRadius = 8f;

    /// <summary>The defaults: the true amount, from surfaces up to two metres away.</summary>
    public BounceSettings() : this(DefaultStrength, DefaultRadius)
    {
    }
}

/// <summary>
/// The ink lines drawn round materials with <see cref="MaterialFlags.Outline"/>: their linear colour, how many pixels of
/// the picture wide they are, and the distances in metres over which they fade out, so far-off things don't turn into
/// smudges of ink.
/// </summary>
public readonly record struct OutlineSettings(
    Vector3 Color,
    float Thickness = OutlineSettings.DefaultThickness,
    float FadeStart = OutlineSettings.DefaultFadeStart,
    float FadeEnd = OutlineSettings.DefaultFadeEnd)
{
    public const float DefaultThickness = 1.5f;
    public const float DefaultFadeStart = 30f;
    public const float DefaultFadeEnd = 60f;

    /// <summary>The thickest an outline can be, in pixels.</summary>
    public const float MostThickness = 8f;

    /// <summary>Black lines a pixel and a half wide, fading out from 30 to 60 metres.</summary>
    public OutlineSettings() : this(Vector3.Zero)
    {
    }

    /// <summary>Says why these settings can't be used, or returns null when they can.</summary>
    public string? Problem() =>
        !(Thickness is >= 0f and <= MostThickness)
            ? $"The outline thickness must be from 0 to {MostThickness} pixels, not {Thickness}."
            : !(FadeStart >= 0f && FadeEnd >= FadeStart && float.IsFinite(FadeEnd))
                ? $"The outline must start fading at zero or further and end no nearer than it starts, but it runs from {FadeStart} to {FadeEnd} metres."
                : !(Color.X >= 0f && Color.Y >= 0f && Color.Z >= 0f && float.IsFinite(Color.X + Color.Y + Color.Z))
                    ? $"The outline colour {Color} must be 0 or more in each channel."
                    : null;
}

/// <summary>
/// Air that thickens towards the ground and dims and tints what's far away. <see cref="Density"/>
/// is how much light it takes per metre at <see cref="Height"/>, 0 for none; above that height
/// it thins by e every 1 / <see cref="Falloff"/> metres, and below it thickens the same way, so
/// 0 falloff is fog of one density everywhere. It takes the colour of the sky behind, and glows
/// towards the sun by <see cref="SunScatter"/>.
/// </summary>
public readonly record struct FogSettings(float Density = 0f, float Height = 0f, float Falloff = FogSettings.DefaultFalloff, float SunScatter = FogSettings.DefaultSunScatter)
{
    public const float DefaultFalloff = 0.1f;
    public const float DefaultSunScatter = 0.5f;

    /// <summary>No fog yet, with a falloff and sun scatter that suit a day outdoors once given a density.</summary>
    public FogSettings() : this(0f, 0f, DefaultFalloff, DefaultSunScatter)
    {
    }

    public const float DefaultAnisotropy = 0.6f;
    public const float DefaultVolumetricDistance = 48f;

    /// <summary>The most <see cref="Anisotropy"/> can be either way.</summary>
    public const float MaxAnisotropy = 0.9f;

    /// <summary>
    /// Which way the fog scatters light, from -0.9 to 0.9: 0 sends it every way alike, more
    /// sends it onwards, so the air glows most looking towards a light, as real haze does,
    /// and less sends it back. Only the volumetric fog uses it.
    /// </summary>
    public float Anisotropy { get; init; } = DefaultAnisotropy;

    /// <summary>
    /// How far from the camera, in metres, the fog is worked out in a volume that the sun's
    /// shadows and the point and spot lights light up, giving shafts of light and halos.
    /// Past it the plain fog carries on. 0 is the plain fog only.
    /// </summary>
    public float VolumetricDistance { get; init; } = DefaultVolumetricDistance;
}
