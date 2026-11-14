using System.Numerics;
using Super.Ecs;

namespace Super.Physics;

/// <summary>How a <see cref="Joint"/> holds two bodies together.</summary>
public enum JointKind
{
    /// <summary>Welded: neither moves nor turns against the other.</summary>
    Fixed,

    /// <summary>Held at one point but free to turn any way, like a shoulder or a chain link.</summary>
    Ball,

    /// <summary>Held at one point and turning only about one axis, like a door or a wheel.</summary>
    Hinge,

    /// <summary>Two points kept between a shortest and a longest distance apart, like a rope or a rod.</summary>
    Distance,
}

/// <summary>
/// Joins an entity's body to another entity's, or to the world, so they move together: a door on a hinge, a lamp on a
/// chain, a crate welded to a cart. The entity needs a <see cref="Collider"/>, and so does <see cref="To"/>; at least
/// one of them should be dynamic. Points and axes are in each entity's own space, and the joint holds the bodies as
/// they are when it's made, which is the next step after it's added or changed. Make one with <see cref="Fixed"/>,
/// <see cref="Ball"/>, <see cref="Hinge"/>, <see cref="Rope"/> or <see cref="Rod"/>. An entity has at most one joint;
/// for more, give the other entity the joint back to it. When <see cref="To"/> is destroyed or loses its collider,
/// the joint lets go.
/// </summary>
public readonly record struct Joint
{
    /// <summary>How it holds.</summary>
    public JointKind Kind { get; init; }

    /// <summary>The entity it's joined to, or null for a fixed point in the world.</summary>
    public Entity? To { get; init; }

    /// <summary>Where it's held, in this entity's space: the hinge or ball's pivot, or the rope's end.</summary>
    public Vector3 Anchor { get; init; }

    /// <summary>Where it's held on <see cref="To"/>, in its space, or in the world when there's no To. Only distance joints use it.</summary>
    public Vector3 OtherAnchor { get; init; }

    /// <summary>The axis a hinge turns about, in this entity's space.</summary>
    public Vector3 Axis { get; init; }

    /// <summary>How far a hinge turns one way from where it started, in degrees, from -180 to 0.</summary>
    public float MinAngle { get; init; }

    /// <summary>How far a hinge turns the other way, in degrees, from 0 to 180. -180 and 180 together let it spin freely.</summary>
    public float MaxAngle { get; init; }

    /// <summary>The shortest a distance joint gets, in metres.</summary>
    public float MinLength { get; init; }

    /// <summary>The longest a distance joint gets, in metres.</summary>
    public float MaxLength { get; init; }

    /// <summary>Welds this entity to <paramref name="to"/>, or in place in the world, as they are now.</summary>
    public static Joint Fixed(Entity? to = null) => new() { Kind = JointKind.Fixed, To = to };

    /// <summary>Holds this entity at <paramref name="anchor"/>, in its own space, letting it turn any way.</summary>
    public static Joint Ball(Vector3 anchor, Entity? to = null) => new() { Kind = JointKind.Ball, To = to, Anchor = anchor };

    /// <summary>
    /// Holds this entity at <paramref name="anchor"/> and lets it turn only about <paramref name="axis"/>, both in its
    /// own space, from <paramref name="minAngle"/> to <paramref name="maxAngle"/> degrees from where it started.
    /// </summary>
    public static Joint Hinge(Vector3 anchor, Vector3 axis, Entity? to = null, float minAngle = -180f, float maxAngle = 180f) =>
        new() { Kind = JointKind.Hinge, To = to, Anchor = anchor, Axis = axis, MinAngle = minAngle, MaxAngle = maxAngle };

    /// <summary>
    /// Ties <paramref name="anchor"/>, in this entity's space, to <paramref name="otherAnchor"/> on <paramref name="to"/>
    /// (or in the world) with a rope <paramref name="length"/> metres long: it goes slack but doesn't stretch.
    /// </summary>
    public static Joint Rope(Vector3 anchor, Entity? to, Vector3 otherAnchor, float length) =>
        new() { Kind = JointKind.Distance, To = to, Anchor = anchor, OtherAnchor = otherAnchor, MinLength = 0f, MaxLength = length };

    /// <summary>Like <see cref="Rope"/>, but stiff: the two points stay exactly <paramref name="length"/> metres apart.</summary>
    public static Joint Rod(Vector3 anchor, Entity? to, Vector3 otherAnchor, float length) =>
        new() { Kind = JointKind.Distance, To = to, Anchor = anchor, OtherAnchor = otherAnchor, MinLength = length, MaxLength = length };
}
