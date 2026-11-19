namespace Super;

/// <summary>An entity's children: the entities whose <see cref="Parent"/> is it.</summary>
public static class WorldHierarchy
{
    /// <summary>
    /// The entities whose <see cref="Parent"/> is this one, as they are now, without their own children. Found by
    /// walking every parented entity, so keep it out of loops over many entities.
    /// </summary>
    public static IReadOnlyList<Entity> Children(this World world, Entity entity)
    {
        List<Entity>? children = null;
        Collect(world, entity, ref children);
        return children ?? [];
    }

    /// <summary>
    /// Destroys the entity's children, and theirs through <see cref="World.Destroy"/>. Called once the entity is gone,
    /// so a cycle of parents ends: an entity already dead is skipped.
    /// </summary>
    internal static void DestroyChildren(World world, Entity entity)
    {
        List<Entity>? children = null;
        Collect(world, entity, ref children);
        if (children is null)
        {
            return;
        }

        foreach (var child in children)
        {
            if (world.IsAlive(child))
            {
                world.Destroy(child);
            }
        }
    }

    // Rows are gathered first, since destroying a child moves the rows after it. The list is made only when there's a
    // child, so destroying an entity with none allocates nothing.
    private static void Collect(World world, Entity entity, ref List<Entity>? children)
    {
        int parentType = world.Archetypes.TypeIdOrMissing<Parent>();
        if (parentType < 0)
        {
            return;
        }

        int index = -1;
        while (world.Archetypes.NextMatching(ref index, default(ComponentMask).With(parentType)) is { } archetype)
        {
            var parents = archetype.Column<Parent>(parentType).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                if (parents[i].Value == entity)
                {
                    (children ??= []).Add(archetype.Entities[i]);
                }
            }
        }
    }
}
