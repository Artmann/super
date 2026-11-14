using Super.Ecs;

namespace Super.Physics;

/// <summary>Checks that an entity's <see cref="Ragdoll"/> can be built, saying what to fix when it can't.</summary>
internal static class RagdollChecks
{
    /// <summary>Checks the ragdoll and its entity, and gives the animator it takes its pose from.</summary>
    public static Animator Check(World world, Entity entity, Ragdoll ragdoll)
    {
        if (ragdoll.Body is null)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Ragdoll has no RagdollBody. Make one with RagdollBody.Humanoid.");
        }

        if (!world.Has<Animated>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a Ragdoll but isn't Animated; a ragdoll takes its pose from, and gives it to, an animator.");
        }

        var animator = world.Get<Animated>(entity).Value;
        if (animator.Rig != ragdoll.Body.Rig)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Ragdoll was made for another rig than its animator's. Make the RagdollBody from the same rig.");
        }

        CheckEntity(world, entity);
        CheckSettings(entity, ragdoll);
        return animator;
    }

    private static void CheckEntity(World world, Entity entity)
    {
        if (world.Has<Parent>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a Ragdoll and a Parent; a ragdoll moves in the world, so it can't follow a parent.");
        }

        if (world.Has<Collider>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a Ragdoll and a Collider; the ragdoll's parts are its colliders. Put any other collider on a child entity.");
        }

        var scale = world.Get<Transform>(entity).Scale;
        if (!(scale.X > 0f) || MathF.Abs(scale.X - scale.Y) > 1e-4f * scale.X || MathF.Abs(scale.X - scale.Z) > 1e-4f * scale.X)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Ragdoll needs the same scale on every axis, above 0, not {scale}.");
        }
    }

    private static void CheckSettings(Entity entity, Ragdoll ragdoll)
    {
        if (!(ragdoll.BlendSeconds >= 0f) || !float.IsFinite(ragdoll.BlendSeconds) || !float.IsFinite(ragdoll.Strength))
        {
            throw new ArgumentOutOfRangeException(nameof(ragdoll), ragdoll.BlendSeconds, $"Entity {entity.Id}'s Ragdoll blends over {ragdoll.BlendSeconds} seconds with strength {ragdoll.Strength}; the blend must be 0 or more and both finite.");
        }

        if (ragdoll.Mask is { } mask && mask.Rig != ragdoll.Body.Rig)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Ragdoll mask is for another rig.");
        }
    }
}
