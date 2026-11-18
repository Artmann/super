using System.Numerics;

namespace Rendition;

/// <summary>
/// How a light is used once a space has a bake. Mixed is the default, so a light that names no mode
/// behaves as it always did. Until a bake is fresh, every light is drawn live whatever its mode.
/// </summary>
public enum LightMode
{
    /// <summary>Lit live, and also part of any bake.</summary>
    Mixed = 0,

    /// <summary>Lit live only, never part of a bake.</summary>
    Realtime = 1,

    /// <summary>Part of a bake only, so a bake that covers it replaces the live light.</summary>
    Baked = 2,
}

public struct DirectionalLight
{
    /// <summary>How a bake treats this light. Defaults to <see cref="LightMode.Mixed"/>.</summary>
    public LightMode Mode;

    /// <summary>Unit vector pointing from the surface towards the light.</summary>
    public Vector3 Direction;
    public Vector3 Radiance;

    /// <summary>The renderer draws one shadow map, for the first light that sets this.</summary>
    public bool CastsShadow;
}

/// <summary>
/// A light at a point, such as a torch, a lamp or a candle, that dims with distance and
/// ends at its range. Given a cone it's a spot, shining one way only. Any number can light
/// a frame, up to <see cref="MaxPerFrame"/>; see "Point and spot lights" in docs/rendering.md.
/// </summary>
public struct LocalLight
{
    /// <summary>The most one frame can have. The renderer throws past this.</summary>
    public const int MaxPerFrame = 1024;

    /// <summary>How a bake treats this light. Defaults to <see cref="LightMode.Mixed"/>.</summary>
    public LightMode Mode;

    /// <summary>
    /// Picks out a light from one frame to the next, so its shadow keeps its sharpness as it
    /// moves. Any number, but no two lights in a frame should share one.
    /// </summary>
    public int Key;

    public Vector3 Position;

    /// <summary>Linear colour times brightness, as it falls on a surface one metre away facing it.</summary>
    public Vector3 Radiance;

    /// <summary>How far the light reaches, in metres. It fades smoothly to nothing there.</summary>
    public float Range;

    /// <summary>The radius a light has when <see cref="Radius"/> is left at 0, in metres.</summary>
    public const float DefaultRadius = 0.1f;

    /// <summary>
    /// How big the glowing part is, in metres, such as a torch's flame. Light can't get much
    /// brighter than it is at this distance, so a bigger radius calms a wall right beside
    /// the light while leaving it as bright a metre away and beyond. A bigger light also
    /// casts softer shadows. 0 means <see cref="DefaultRadius"/>.
    /// </summary>
    public float Radius;

    /// <summary>For a spot, the unit vector it shines along. Ignored for a point light.</summary>
    public Vector3 Direction;

    /// <summary>For a spot, the angle from its axis, in radians, inside which it shines fully.</summary>
    public float InnerAngle;

    /// <summary>
    /// For a spot, the angle from its axis, in radians, where it has faded to nothing; under
    /// π/2. Zero makes a point light, which shines every way.
    /// </summary>
    public float OuterAngle;

    /// <summary>Whether things block it. Shadows are drawn into a shared atlas; see docs/rendering.md.</summary>
    public bool CastsShadow;

    /// <summary>
    /// How strongly it lights the volumetric fog, giving it a halo: 1 is as much as the air
    /// really scatters, 0 leaves it out of the fog, and more makes the halo stronger.
    /// </summary>
    public float Volumetric = 1f;

    public LocalLight()
    {
    }

    /// <summary>Whether it's a spot rather than a point light.</summary>
    public readonly bool IsSpot => OuterAngle > 0f;
}
