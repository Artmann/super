namespace Super;

/// <summary>
/// Counts the entities that have a component, and finds the one entity that has it, such as the player or the
/// camera. Neither allocates or starts a query, so they are safe anywhere, each tick.
/// </summary>
public static class WorldFind
{
    /// <summary>How many entities have a <typeparamref name="T"/>.</summary>
    public static int Count<T>(this World world)
        where T : struct
    {
        int count = 0;
        int type = world.Archetypes.TypeIdOrMissing<T>();
        if (type < 0)
        {
            return count;
        }

        var mask = default(ComponentMask).With(type);
        int index = -1;
        while (world.Archetypes.NextMatching(ref index, mask) is { } archetype)
        {
            count += archetype.Count;
        }

        return count;
    }

    /// <summary>
    /// The one entity that has a <typeparamref name="T"/>. Throws when there is none or more than one, so use it for
    /// things a game has exactly one of. Read its component with <c>world.Get</c>.
    /// </summary>
    public static Entity Single<T>(this World world)
        where T : struct
    {
        if (world.TrySingle<T>(out var entity))
        {
            return entity;
        }

        int count = world.Count<T>();
        throw new InvalidOperationException(
            count == 0
                ? $"No entity has a {typeof(T).Name}."
                : $"{count} entities have a {typeof(T).Name}, and Single needs exactly one.");
    }

    /// <summary>
    /// Finds the one entity that has a <typeparamref name="T"/>. False when there is none, and when there is more
    /// than one, since no one of them is the answer.
    /// </summary>
    public static bool TrySingle<T>(this World world, out Entity entity)
        where T : struct
    {
        entity = default;
        int type = world.Archetypes.TypeIdOrMissing<T>();
        if (type < 0)
        {
            return false;
        }

        var mask = default(ComponentMask).With(type);
        int index = -1;
        if (world.Archetypes.NextMatching(ref index, mask) is not { Count: 1 } archetype
            || world.Archetypes.NextMatching(ref index, mask) is not null)
        {
            return false;
        }

        entity = archetype.Entities[0];
        return true;
    }
}
