using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>One entity's collider in the physics world, and what it was built from, to tell when it must be built again.</summary>
internal struct TrackedBody
{
    public BodyID Body;
    public JoltPhysicsSharp.Body Handle;
    public Collider Collider;
    public Body Settings;
    public bool Trigger;
    public bool Character;
    public uint Mask;
    public Vector3 Scale;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Moving;
}

/// <summary>The body an entity asks for in one sync: its collider, settings, collision mask and place in the world.</summary>
internal readonly record struct WantedBody(
    Entity Entity,
    Collider Collider,
    Body Settings,
    bool Trigger,
    bool Character,
    uint Mask,
    Vector3 Scale,
    Vector3 Position,
    Quaternion Rotation)
{
    /// <summary>
    /// Whether <paramref name="tracked"/> must be built again: it's a different kind of body, or a dynamic body has a
    /// new shape, which must keep the mass asked for.
    /// </summary>
    public bool NeedsRebuild(in TrackedBody tracked) =>
        tracked.Settings != Settings
        || tracked.Trigger != Trigger
        || tracked.Character != Character
        || (Settings.Kind == BodyKind.Dynamic && (tracked.Collider != Collider || tracked.Scale != Scale));
}
