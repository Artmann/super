using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A light that shines every way from a point, such as a candle or a torch, for <see cref="ViewLights.Point"/>. The
/// color is how bright it is on a surface a metre away, and it dims with the square of the distance, reaching nothing
/// at the range, in metres. See "Point and spot lights" in docs/rendering.md.
/// </summary>
public readonly record struct PointLight(Vector3 Position, Vector3 Color, float Range)
{
    /// <summary>
    /// Whether things block it, so it casts shadows. Lights with shadows share an atlas, so the biggest on screen get
    /// the sharpest ones. On unless set.
    /// </summary>
    public bool Shadows { get; init; } = true;

    /// <summary>
    /// How big the glowing part is, in metres: raise it for a light right beside a wall, such as a torch in a
    /// bracket, so the wall isn't blown out. It also softens the shadows. More than 0 and less than the range.
    /// </summary>
    public float Radius { get; init; } = LocalLight.DefaultRadius;

    /// <summary>
    /// How brightly it lights the volumetric fog around it, as a halo: 1 is true to the fog, 0 leaves it out.
    /// </summary>
    public float Volumetric { get; init; } = 1f;

    /// <summary>How a bake treats it; see "Light modes" in docs/rendering.md.</summary>
    public LightMode Mode { get; init; } = LightMode.Mixed;

    /// <summary>The light as the renderer takes it, named by <paramref name="key"/>.</summary>
    internal LocalLight ToLocal(int key) => new()
    {
        Key = key,
        Position = Position,
        Radiance = Color,
        Range = Range,
        Radius = Radius,
        CastsShadow = Shadows,
        Volumetric = Volumetric,
        Mode = Mode,
    };
}
