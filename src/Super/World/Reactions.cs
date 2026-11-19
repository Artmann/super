namespace Super;

/// <summary>Runs code when a component lands on an entity that had none, or is about to leave one.</summary>
public static class WorldReactions
{
    /// <summary>
    /// Runs <paramref name="handler"/> when a <typeparamref name="T"/> lands on an entity that had none, once every
    /// component the same <c>Spawn</c> or <c>Add</c> brings is on it. Replacing a value doesn't run it.
    /// </summary>
    public static void OnAdded<T>(this World world, Action<Entity> handler)
        where T : struct =>
        world.Archetypes.Reactions.Listen(world.TypeId<T>(), handler, removed: false);

    /// <summary>
    /// Runs <paramref name="handler"/> before a <typeparamref name="T"/> leaves an entity, through <c>Remove</c> or
    /// <c>Destroy</c>, so the handler can still read it.
    /// </summary>
    public static void OnRemoved<T>(this World world, Action<Entity> handler)
        where T : struct =>
        world.Archetypes.Reactions.Listen(world.TypeId<T>(), handler, removed: true);
}

/// <summary>
/// The handlers a world runs when components land or leave, kept per component type ID. A type with no handlers
/// costs one array read per change. While handlers run the world counts as querying, so their own changes wait in
/// <see cref="PendingChanges"/> and are made after them.
/// </summary>
internal sealed class Reactions
{
    private readonly List<Action<Entity>>?[] _added = new List<Action<Entity>>?[ComponentMask.MaxTypes];
    private readonly List<Action<Entity>>?[] _removed = new List<Action<Entity>>?[ComponentMask.MaxTypes];

    /// <summary>Whether any type has an <c>OnRemoved</c> handler, so a destroy with none can skip looking.</summary>
    public bool AnyRemoved { get; private set; }

    public void Listen(int type, Action<Entity> handler, bool removed)
    {
        var handlers = removed ? _removed : _added;
        (handlers[type] ??= []).Add(handler);
        AnyRemoved |= removed;
    }

    public bool HasAdded(int type) => _added[type] is not null;

    public void RunAdded(World world, Entity entity, int type) => Run(world, entity, _added[type]);

    public void RunRemoved(World world, Entity entity, int type) => Run(world, entity, _removed[type]);

    /// <summary>Runs the removed handlers for each of the components in <paramref name="mask"/>, in type ID order.</summary>
    public void RunRemoved(World world, Entity entity, in ComponentMask mask)
    {
        for (int type = mask.Next(-1); type >= 0; type = mask.Next(type))
        {
            Run(world, entity, _removed[type]);
        }
    }

    // Walked by index, so a handler that adds another for the same type doesn't break the walk.
    private static void Run(World world, Entity entity, List<Action<Entity>>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        world.BeginIteration();
        try
        {
            for (int i = 0; i < handlers.Count; i++)
            {
                handlers[i](entity);
            }
        }
        finally
        {
            world.EndIteration();
        }
    }
}
