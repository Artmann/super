
namespace Super.Physics;

/// <summary>
/// Makes an entity's <see cref="Collider"/> an area that notices what enters and leaves it, rather than a solid
/// shape. Nothing bumps into it, and rays, shape casts and overlap tests pass through it. After each step,
/// <see cref="PhysicsTriggers.Entered"/> and <see cref="PhysicsTriggers.Exited"/> list who came and went.
/// </summary>
/// <param name="Detects">The layers whose colliders it notices, as bits.</param>
public readonly record struct Trigger(uint Detects = LayerBits.All)
{
    /// <summary>A trigger that detects every layer; without this, <c>new Trigger()</c> would detect none.</summary>
    public Trigger()
        : this(LayerBits.All)
    {
    }
}

/// <summary>A collider that came into, or left, a <see cref="Trigger"/> area.</summary>
public readonly record struct TriggerEvent(Entity Trigger, Entity Other);
