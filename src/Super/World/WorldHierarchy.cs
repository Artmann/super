namespace Super;

/// <summary>An entity's children: the entities whose <see cref="Parent"/> is it.</summary>
public static class WorldHierarchy
{
    /// <summary>
    /// The entities whose <see cref="Parent"/> is this one, as they are now, without their own children, in no
    /// particular order. Found in the child index, so it costs the same however many parented entities the world holds.
    /// </summary>
    public static IReadOnlyList<Entity> Children(this World world, Entity entity) =>
        world.Archetypes.Children.CopyOf(world, entity);

    /// <summary>
    /// Destroys the entity's children, and theirs through <see cref="World.Destroy"/>. Called once the entity is gone,
    /// so a cycle of parents ends: an entity already dead is skipped.
    /// </summary>
    // The child index finds them at once, however many parented entities the world holds. Each is checked against its
    // component as well, in case a handler moved it away since.
    internal static void DestroyChildren(World world, Entity entity)
    {
        if (world.Archetypes.Children.Take(world, entity) is not { } children)
        {
            return;
        }

        foreach (var child in children)
        {
            if (world.IsAlive(child) && world.Has<Parent>(child) && world.ParentOf(child).Value == entity)
            {
                world.Destroy(child);
            }
        }

        world.Archetypes.Children.Return(children);
    }

    /// <summary>
    /// The entity's parent, read without marking the child index unsure as <see cref="World.Get{T}"/> does, since it
    /// hands out no reference. The entity must have one.
    /// </summary>
    internal static Parent ParentOf(this World world, Entity entity)
    {
        ref var slot = ref world.Entities.Of(entity);
        return slot.Archetype!.Column<Parent>(world.Archetypes.Types.ParentType).Values[slot.Row];
    }
}
