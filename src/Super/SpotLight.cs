using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A light that shines one way in a cone, such as a lantern's beam, for <see cref="ViewLights.Spot"/>. It's full
/// inside the inner angle and fades to nothing at the outer one; both are measured from the direction, in radians,
/// and the outer must be under a right angle. The direction needn't be one unit long. Otherwise it's like a
/// <see cref="PointLight"/>, though it costs a sixth as much shadow. On an entity, the position and direction count from
/// its <see cref="Transform"/> and <see cref="Parent"/>, and a zero direction points along the entity's +z.
/// </summary>
public readonly record struct SpotLight(Vector3 Position, Vector3 Direction, Vector3 Color, float Range)
{
    /// <summary>The angle from the direction, in radians, inside which it shines fully. From 0 up to the outer angle.</summary>
    public required float InnerAngle { get; init; }

    /// <summary>The angle from the direction, in radians, where it has faded to nothing. More than 0 and under π/2.</summary>
    public required float OuterAngle { get; init; }

    /// <inheritdoc cref="PointLight.Shadows"/>
    public bool Shadows { get; init; } = true;

    /// <inheritdoc cref="PointLight.Radius"/>
    public float Radius { get; init; } = LocalLight.DefaultRadius;

    /// <inheritdoc cref="PointLight.Volumetric"/>
    public float Volumetric { get; init; } = 1f;

    /// <inheritdoc cref="PointLight.Mode"/>
    public LightMode Mode { get; init; } = LightMode.Mixed;

    /// <summary>The light as the renderer takes it, named by <paramref name="key"/>.</summary>
    internal LocalLight ToLocal(int key)
    {
        var light = new PointLight(Position, Color, Range) { Shadows = Shadows, Radius = Radius, Volumetric = Volumetric, Mode = Mode }
            .ToLocal(key);
        light.Direction = Vector3.Normalize(Direction);
        light.InnerAngle = InnerAngle;
        light.OuterAngle = OuterAngle;
        return light;
    }
}
