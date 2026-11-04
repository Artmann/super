using System.Numerics;

namespace Super.Physics;

/// <summary>The kinds of shape a <see cref="Collider"/> can be.</summary>
public enum ColliderShape
{
    Ball,
    Box,
    Capsule,
}

/// <summary>
/// A shape on an entity that rays can hit, such as for clicking on it. It needs a <see cref="Super.Ecs.Transform"/>,
/// and moves, turns and scales with it, following <see cref="Super.Ecs.Parent"/> too. Make one with
/// <see cref="Ball"/>, <see cref="Box"/> or <see cref="Capsule"/>; the offset is in the entity's own space.
/// <see cref="Layers"/> says which of up to 16 layers it's on, as bits, so a query can pick some and skip the rest.
/// </summary>
public readonly record struct Collider
{
    /// <summary>The layer a collider is on when none is given: bit 0.</summary>
    public const uint DefaultLayer = 1;

    private Collider(ColliderShape shape, float radius, Vector3 size, Vector3 offset, uint layers)
    {
        Shape = shape;
        Radius = radius;
        Size = size;
        Offset = offset;
        Layers = layers;
    }

    public ColliderShape Shape { get; }

    /// <summary>A ball's or a capsule's radius.</summary>
    public float Radius { get; }

    /// <summary>A box's full width, height and depth. For a capsule, Y is its height, end to end.</summary>
    public Vector3 Size { get; }

    /// <summary>Where the shape sits from the entity's origin: a ball's or box's centre, or a capsule's foot.</summary>
    public Vector3 Offset { get; }

    /// <summary>The layers the collider is on, one bit each, from bit 0 to bit 15.</summary>
    public uint Layers { get; }

    /// <summary>A ball centred on the offset.</summary>
    public static Collider Ball(float radius, Vector3 offset = default, uint layers = DefaultLayer) =>
        new(ColliderShape.Ball, radius, new Vector3(radius * 2f), offset, layers);

    /// <summary>A box of this full size, centred on the offset and turned with the entity.</summary>
    public static Collider Box(Vector3 size, Vector3 offset = default, uint layers = DefaultLayer) =>
        new(ColliderShape.Box, 0f, size, offset, layers);

    /// <summary>
    /// An upright capsule standing on the offset, reaching <paramref name="height"/> up from it, end to end. It suits
    /// a character whose origin is at their feet. The height must be at least twice the radius.
    /// </summary>
    public static Collider Capsule(float height, float radius, Vector3 offset = default, uint layers = DefaultLayer) =>
        new(ColliderShape.Capsule, radius, new Vector3(radius * 2f, height, radius * 2f), offset, layers);

    /// <summary>The same shape on other layers.</summary>
    public Collider OnLayers(uint layers) => new(Shape, Radius, Size, Offset, layers);
}

/// <summary>Where a ray met a <see cref="Collider"/>: whose it is, how far along the ray, and the point in the world.</summary>
public readonly record struct RayHit(Super.Ecs.Entity Entity, float Distance, Vector3 Point);
