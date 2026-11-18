namespace Super.Ecs;

/// <summary>Clears away the entities a game scoped to a state with <see cref="ScopedTo{TState}"/> when it leaves it.</summary>
internal static class ScopedEntities
{
    /// <summary>Destroys every entity scoped to this state. Collects first, since destroying moves rows.</summary>
    public static void DestroyScopedTo<TState>(World world, TState state, List<Entity> leaving)
        where TState : struct, Enum
    {
        var archetypes = world.Archetypes;
        int scopedType = archetypes.TypeIdOrMissing<ScopedTo<TState>>();
        if (scopedType < 0)
        {
            return;
        }

        var same = EqualityComparer<TState>.Default;
        ulong mask = 1UL << scopedType;
        int index = -1;
        while (archetypes.NextMatching(ref index, mask) is { } archetype)
        {
            var scopes = archetype.Column<ScopedTo<TState>>(scopedType).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                if (same.Equals(scopes[i].State, state))
                {
                    leaving.Add(archetype.Entities[i]);
                }
            }
        }

        foreach (var entity in leaving)
        {
            world.Destroy(entity);
        }

        leaving.Clear();
    }
}
