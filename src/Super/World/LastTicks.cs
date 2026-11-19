namespace Super;

/// <summary>Moves an entity without the blend that drawing gives every move between ticks.</summary>
public static class WorldMotion
{
    /// <summary>
    /// Moves an entity with no glide: it's drawn at the new place on the very next frame. Use it for a respawn, a
    /// wrap at an edge or anything else that isn't a move. Gives the entity the transform if it has none.
    /// </summary>
    public static void Teleport(this World world, Entity entity, Transform transform)
    {
        world.Add(entity, transform);
        if (world.Has<LastTick>(entity))
        {
            world.Get<LastTick>(entity).Previous = transform;
        }
    }
}

/// <summary>Keeps each entity's memory of its last tick, which drawing blends from.</summary>
internal static class LastTicks
{
    /// <summary>
    /// Copies every transform, and every swim's phase, into the entity's <see cref="LastTick"/>. The game loop does
    /// this before each Update and when a state is entered. <see cref="Static"/> entities never move, so they're
    /// skipped.
    /// </summary>
    public static void Remember(World world)
    {
        var archetypes = world.Archetypes;
        int transformType = archetypes.TypeIdOrMissing<Transform>();
        if (transformType < 0)
        {
            return;
        }

        int lastTickType = archetypes.TypeId<LastTick>();
        int staticType = archetypes.TypeIdOrMissing<Static>();
        int swimType = archetypes.TypeIdOrMissing<Swim>();
        var mask = default(ComponentMask).With(transformType).With(lastTickType);
        int index = -1;
        while (archetypes.NextMatching(ref index, mask) is { } archetype)
        {
            if (staticType >= 0 && archetype.Mask.Has(staticType))
            {
                continue;
            }

            var transforms = archetype.Column<Transform>(transformType).Values;
            var lastTicks = archetype.Column<LastTick>(lastTickType).Values;
            var swims = archetype.ColumnOrNull<Swim>(swimType)?.Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                lastTicks[i].Previous = transforms[i];
                lastTicks[i].HasPrevious = true;
                if (swims is not null)
                {
                    lastTicks[i].PreviousPhase = swims[i].Phase;
                }
            }
        }
    }
}
