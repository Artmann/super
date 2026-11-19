namespace Super;

/// <summary>Clears away the entities a game scoped to a state with <see cref="ScopedTo{TState}"/> when it leaves it.</summary>
internal static class ScopedEntities
{
    /// <summary>
    /// Destroys every entity scoped to this state. The walk counts as a query, so the destroys wait for it to end
    /// and no row moves while it's being read.
    /// </summary>
    public static void DestroyScopedTo<TState>(World world, TState state)
        where TState : struct, Enum
    {
        var archetypes = world.Archetypes;
        int scopedType = archetypes.TypeIdOrMissing<ScopedTo<TState>>();
        if (scopedType < 0)
        {
            return;
        }

        var same = EqualityComparer<TState>.Default;
        var mask = default(ComponentMask).With(scopedType);
        int index = -1;
        world.BeginIteration();
        try
        {
            while (archetypes.NextMatching(ref index, mask) is { } archetype)
            {
                var scopes = archetype.Column<ScopedTo<TState>>(scopedType).Values;
                for (int i = 0; i < archetype.Count; i++)
                {
                    if (same.Equals(scopes[i].State, state))
                    {
                        world.Destroy(archetype.Entities[i]);
                    }
                }
            }
        }
        finally
        {
            world.EndIteration();
        }
    }
}
