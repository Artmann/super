namespace Super;

/// <summary>A thing in the world. It's only an ID; its data lives in components.</summary>
public readonly record struct Entity(int Id, int Generation);

/// <summary>
/// Holds every entity and its components. Each World stands alone, so several games can
/// run side by side. Queries visit entities in an order that only depends on what was
/// done to the world, which keeps seeded runs repeatable.
/// </summary>
public sealed class World : ITickHook, IStateHook
{
    private readonly EntitySlots _entities = new();

    /// <summary>How many entities are alive.</summary>
    public int Count => _entities.Count;

    /// <summary>The component types and archetypes, which queries walk, with what each type requires and sets off.</summary>
    internal ArchetypeTable Archetypes { get; } = new();

    /// <summary>The changes waiting for a query or a spawn to finish.</summary>
    internal PendingChanges Pending { get; } = new();

    /// <summary>Whether a query is running, so changes to the world's shape wait until it ends.</summary>
    internal bool IsQuerying => Pending.IsQuerying;

    /// <summary>
    /// Makes an entity with no components. During a query it's alive at once, and components given to it arrive
    /// when the query ends.
    /// </summary>
    // A new entity sits in the empty archetype, which no query walks, so it can be made at any time.
    public Entity Create() => _entities.Create(Archetypes.Empty);

    /// <summary>
    /// Removes an entity. Its ID can be reused, but old handles to it stop being alive. During a query it stays
    /// alive, with its components, until the query ends. <c>OnRemoved</c> handlers run for each of its components
    /// first.
    /// </summary>
    public void Destroy(Entity entity)
    {
        var mask = _entities.Of(entity).Archetype!.Mask; // Throws now for a dead entity, even inside a query.
        if (IsQuerying)
        {
            Pending.Destroy(entity);
            return;
        }

        if (!Archetypes.Reactions.AnyRemoved)
        {
            _entities.Free(entity);
            return;
        }

        // What the handlers change waits until the entity is gone, so nothing they give it outlives it.
        using var held = Pending.Hold(this);
        Archetypes.Reactions.RunRemoved(this, entity, mask);
        _entities.Free(entity);
    }

    public bool IsAlive(Entity entity) => _entities.IsAlive(entity);

    /// <summary>
    /// Gives the entity a component, or replaces the one it has. A component it didn't have also brings the
    /// components <c>Require</c> asks for, unless the entity has them already.
    /// </summary>
    public void Add<T>(Entity entity, T component)
        where T : struct
    {
        if (!Put(entity, in component, out int type))
        {
            return;
        }

        // The handlers wait for the required components, so they see the entity whole.
        using var held = Pending.Hold(this);
        Archetypes.Required.ApplyTo(this, entity, type);
    }

    /// <summary>
    /// Takes a component away. Does nothing if the entity doesn't have it. During a query it goes when the query
    /// ends.
    /// </summary>
    public void Remove<T>(Entity entity)
        where T : struct =>
        Remove(entity, Archetypes.TypeId<T>());

    public bool Has<T>(Entity entity)
        where T : struct =>
        Has(entity, Archetypes.TypeId<T>());

    /// <summary>The entity's component, by reference so it can be changed.</summary>
    public ref T Get<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref _entities.Of(entity);
        int type = Archetypes.TypeId<T>();
        if (!slot.Archetype!.Mask.Has(type))
        {
            throw new InvalidOperationException(
                Pending.IsAdding(entity, type)
                    ? $"Entity {entity.Id}'s {typeof(T).Name} was added during a query and arrives when the query ends."
                    : $"Entity {entity.Id} has no {typeof(T).Name}.");
        }

        return ref slot.Archetype.Column<T>(type).Values[slot.Row];
    }

    // Queries allocate nothing, so they're safe to run every tick and every frame. Each row
    // hands out references: changing row.First.Value changes the component in the world.
    public Query<T1> Query<T1>()
        where T1 : struct =>
        new(this);

    public Query<T1, T2> Query<T1, T2>()
        where T1 : struct
        where T2 : struct =>
        new(this);

    public Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : struct
        where T2 : struct
        where T3 : struct =>
        new(this);

    public Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct =>
        new(this);

    void ITickHook.BeforeUpdate() => LastTicks.Remember(this);

    void IStateHook.Exited<TState>(TState state) => ScopedEntities.DestroyScopedTo(this, state);

    // Whatever was just placed for the new state should appear there, not glide from the old one.
    void IStateHook.Entered() => LastTicks.Remember(this);

    /// <summary>The component type's ID, registering it the first time. Queries ask for theirs as they start.</summary>
    internal int TypeId<T>() => Archetypes.TypeId<T>();

    /// <summary>
    /// Sets the component without bringing its required ones. Returns whether the entity got a type it didn't have,
    /// and queues that type's <c>OnAdded</c> handlers, which the caller holds off until its change is complete.
    /// During a query a new type waits for the query to end, and is added through <see cref="Add{T}"/> then.
    /// </summary>
    internal bool Put<T>(Entity entity, in T component, out int type)
        where T : struct
    {
        ref var slot = ref _entities.Of(entity);
        type = Archetypes.TypeId<T>();
        bool added = !slot.Archetype!.Mask.Has(type);
        if (added)
        {
            if (IsQuerying)
            {
                Pending.Add(entity, type, in component);
                return false;
            }

            _entities.MoveTo(ref slot, Archetypes.With<T>(slot.Archetype, type));
            if (Archetypes.Reactions.HasAdded(type))
            {
                Pending.Added(entity, type);
            }
        }

        slot.Archetype!.Column<T>(type).Values[slot.Row] = component;
        return added;
    }

    internal bool Has(Entity entity, int type) => _entities.Of(entity).Archetype!.Mask.Has(type);

    /// <summary>Takes the component with this type ID away, or queues that during a query.</summary>
    internal void Remove(Entity entity, int type)
    {
        if (!Has(entity, type))
        {
            return;
        }

        if (IsQuerying)
        {
            Pending.Remove(entity, type);
            return;
        }

        using var held = Pending.Hold(this);
        Archetypes.Reactions.RunRemoved(this, entity, type);

        // Read after the handlers, which may have made entities and moved the slots.
        ref var slot = ref _entities.Of(entity);
        _entities.MoveTo(ref slot, Archetypes.Without(slot.Archetype!, type));
    }

    internal void BeginIteration() => Pending.Begin();

    internal void EndIteration() => Pending.End(this);
}
