using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Makes an entity's <see cref="Collider"/> an area that notices what enters and leaves it, rather than a solid
/// shape. Nothing bumps into it, and rays, shape casts and overlap tests pass through it. After each step,
/// <see cref="Physics.Entered"/> and <see cref="Physics.Exited"/> list who came and went.
/// </summary>
/// <param name="Detects">The layers whose colliders it notices, as bits.</param>
public readonly record struct Trigger(uint Detects = Physics.AllLayers)
{
    /// <summary>A trigger that detects every layer; without this, <c>new Trigger()</c> would detect none.</summary>
    public Trigger()
        : this(Physics.AllLayers)
    {
    }
}

/// <summary>A collider that came into, or left, a <see cref="Trigger"/> area.</summary>
public readonly record struct TriggerEvent(Entity Trigger, Entity Other);
